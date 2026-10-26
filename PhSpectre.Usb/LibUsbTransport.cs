using System.Buffers.Binary;
using LibUsbDotNet;
using LibUsbDotNet.Info;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using PhSpectre.Camera;

namespace PhSpectre.Usb;

/// <summary>
/// <see cref="ICameraTransport"/> over libusb bulk endpoints of a USB Still Image (PTP) interface.
/// Kept out of core so native libusb never reaches PhSpectre.Android, which needs its own UsbManager transport.
/// </summary>
public sealed class LibUsbTransport : ICameraTransport
{
    private const int StillImageClass = 6;
    private const int TransferTimeoutMs = 15_000;

    // One libusb context for the whole process: on macOS, initialising a second context right after the first one
    // is torn down fails with "Other error" (every other attempt), which broke the camera polling.
    private static readonly Lazy<UsbContext> SharedContext = new(() => new UsbContext());

    private readonly IUsbDevice _device;
    private readonly UsbEndpointReader _reader;
    private readonly UsbEndpointWriter _writer;
    private readonly int _interfaceId;
    private readonly int _maxPacket;

    public string Description { get; }

    private LibUsbTransport(IUsbDevice device, int interfaceId,
        UsbEndpointReader reader, UsbEndpointWriter writer, int maxPacket, string description)
    {
        _device = device;
        _interfaceId = interfaceId;
        _reader = reader;
        _writer = writer;
        _maxPacket = maxPacket;
        Description = description;
    }

    /// <summary>Lists connected devices exposing a Still Image interface, for diagnostics.</summary>
    public static IReadOnlyList<string> ListStillImageDevices()
    {
        var context = SharedContext.Value;
        var result = new List<string>();
        foreach (var device in context.List())
        {
            try
            {
                device.Open();
                var iface = FindStillImageInterface(device);
                if (iface != null)
                    result.Add($"{device.VendorId:X4}:{device.ProductId:X4} {device.Info.Manufacturer} {device.Info.Product}");
            }
            catch (Exception)
            {
                // Unopenable device (no driver access) can't be a usable candidate.
            }
            finally
            {
                device.Dispose();
            }
        }
        return result;
    }

    /// <summary>Opens the first device with the given vendor id that exposes a Still Image interface.</summary>
    public static LibUsbTransport Open(ushort vendorId = 0x04CB) =>
        OpenCore(vendorId, throwWhenAbsent: true)!;

    /// <summary>Like <see cref="Open"/>, but returns null when no such device is plugged in. </summary>
    public static LibUsbTransport? OpenIfPresent(ushort vendorId = 0x04CB) =>
        OpenCore(vendorId, throwWhenAbsent: false);

    private static LibUsbTransport? OpenCore(ushort vendorId, bool throwWhenAbsent)
    {
        var context = SharedContext.Value;
        var seen = false;
        var devices = context.List().ToList();
        LibUsbTransport? opened = null;
        try
        {
            foreach (var device in devices)
            {
                if (device.VendorId != vendorId) { device.Dispose(); continue; }
                seen = true;

                try
                {
                    device.Open();
                }
                catch (Exception ex)
                {
                    device.Dispose();
                    throw new PtpException(ClaimFailureHint(ex.Message));
                }
                var iface = FindStillImageInterface(device);
                if (iface == null)
                {
                    device.Dispose();
                    continue;
                }

                try
                {
                    Claim(device, iface.Number);
                }
                catch
                {
                    device.Dispose();
                    throw;
                }

                var endpoints = iface.Endpoints;
                var bulkIn = endpoints.FirstOrDefault(e => IsBulk(e) && (e.EndpointAddress & 0x80) != 0)
                    ?? throw new PtpException("PTP interface has no bulk-in endpoint.");
                var bulkOut = endpoints.FirstOrDefault(e => IsBulk(e) && (e.EndpointAddress & 0x80) == 0)
                    ?? throw new PtpException("PTP interface has no bulk-out endpoint.");

                var reader = device.OpenEndpointReader((ReadEndpointID)bulkIn.EndpointAddress);
                var writer = device.OpenEndpointWriter((WriteEndpointID)bulkOut.EndpointAddress);
                var description = $"{device.VendorId:X4}:{device.ProductId:X4} {device.Info.Product}";
                opened = new LibUsbTransport(device, iface.Number, reader, writer, bulkIn.MaxPacketSize, description);
                return opened;
            }
        }
        finally
        {
            // The context outlives every transport, so the handles of devices we didn't pick must be released here.
            foreach (var device in devices)
                if (opened == null || !ReferenceEquals(device, opened._device)) device.Dispose();
        }

        if (!seen && !throwWhenAbsent) return null;
        throw new PtpException(
            $"No USB device with vendor id 0x{vendorId:X4} exposing a Still Image interface was found or it could not be opened.");
    }

    /// <summary>True when a device with the given vendor id is plugged in; does not open it.</summary>
    public static bool IsPresent(ushort vendorId = 0x04CB)
    {
        var context = SharedContext.Value;
        foreach (var device in context.List())
        {
            try
            {
                if (device.VendorId == vendorId) return true;
            }
            finally
            {
                device.Dispose();
            }
        }
        return false;
    }

    private const int ClaimAttempts = 6;

    private static void Claim(IUsbDevice device, int interfaceNumber)
    {
        // Linux: let libusb take the interface from the kernel's usbfs/gphoto-style drivers; harmless elsewhere.
        if (device is UsbDevice native)
        {
            try { native.SetAutoDetachKernelDriver(true); }
            catch (Exception) { /* unsupported on this platform */ }
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                device.ClaimInterface(interfaceNumber);
                return;
            }
            catch (Exception ex) when (ex is not PtpException)
            {
                // macOS: ptpcamerad grabs every camera the moment it is plugged in and launchd restarts it after
                // a kill, so stopping it and claiming right away usually wins the race.
                if (OperatingSystem.IsMacOS() && attempt < ClaimAttempts)
                {
                    StopPtpCameraDaemon();
                    Thread.Sleep(250);
                    continue;
                }
                throw new PtpException(ClaimFailureHint(ex.Message));
            }
        }
    }

    private static void StopPtpCameraDaemon()
    {
        try
        {
            using var kill = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("killall", "ptpcamerad")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            kill?.WaitForExit(2000);
        }
        catch (Exception)
        {
            // killall missing or not allowed; the claim will report the real failure.
        }
    }

    private static string ClaimFailureHint(string detail)
    {
        if (OperatingSystem.IsMacOS())
            return "Another app is holding the camera. Quit Photos, Image Capture and Preview, replug the camera and try again. " + detail;
        if (OperatingSystem.IsLinux())
            return "Can't access the camera over USB. Close any app using it (file manager, gphoto2) and make sure your user may open " +
                   "Fujifilm devices. " + LinuxSetupHint + " " + detail;
        return "Could not claim the camera's USB interface. " + detail;
    }

    /// <summary>What a Linux user has to install and allow before libusb can talk to the camera.</summary>
    public const string LinuxSetupHint =
        "Linux setup: install libusb (Debian/Ubuntu: sudo apt install libusb-1.0-0; Fedora: sudo dnf install libusb1; Arch: sudo pacman -S libusb), " +
        "then add the udev rule SUBSYSTEM==\"usb\", ATTR{idVendor}==\"04cb\", MODE=\"0666\" to /etc/udev/rules.d/70-fujifilm.rules, " +
        "run sudo udevadm control --reload-rules and replug the camera.";

    /// <summary>Explains an exception thrown while loading or using libusb itself; null when the cause isn't a missing native library.</summary>
    internal static string? MissingLibraryHint(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is DllNotFoundException or BadImageFormatException)
            {
                return OperatingSystem.IsLinux()
                    ? "The libusb library is missing. " + LinuxSetupHint
                    : "The libusb library could not be loaded. " + e.Message;
            }
        }
        return null;
    }

    public Task SendAsync(byte[] container, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            Write(container);
            // A transfer that ends exactly on a packet boundary needs a zero-length packet to terminate it.
            if (container.Length % _maxPacket == 0)
                Write([]);
        }, ct);

    public Task<byte[]> ReceiveAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var buffer = new byte[_maxPacket];
            int read;
            // Skip zero-length terminator packets left over from the previous transfer.
            do
            {
                ct.ThrowIfCancellationRequested();
                Check(_reader.Read(buffer, TransferTimeoutMs, out read));
            } while (read == 0);

            if (read < PtpContainer.HeaderSize)
                throw new PtpException($"Short USB read ({read} bytes).");

            var total = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            var result = new byte[Math.Max(total, read)];
            Array.Copy(buffer, result, read);

            while (read < total)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = new byte[Math.Min(total - read, 64 * 1024)];
                Check(_reader.Read(chunk, TransferTimeoutMs, out var got));
                if (got == 0) break;
                Array.Copy(chunk, 0, result, read, got);
                read += got;
            }
            return result;
        }, ct);

    public ValueTask DisposeAsync()
    {
        try { _device.ReleaseInterface(_interfaceId); } catch (Exception) { /* device may be unplugged */ }
        _device.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Write(byte[] data) => Check(_writer.Write(data, TransferTimeoutMs, out _));

    private static void Check(Error error)
    {
        if (error != Error.Success)
            throw new PtpException($"USB transfer failed: {error}.");
    }

    private static bool IsBulk(UsbEndpointInfo e) =>
        (e.Attributes & 0x03) == 0x02;

    private static UsbInterfaceInfo? FindStillImageInterface(IUsbDevice device) =>
        device.Configs
            .SelectMany(c => c.Interfaces)
            .FirstOrDefault(i => (int)i.Class == StillImageClass);
}
