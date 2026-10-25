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

    private readonly UsbContext _context;
    private readonly IUsbDevice _device;
    private readonly UsbEndpointReader _reader;
    private readonly UsbEndpointWriter _writer;
    private readonly int _interfaceId;
    private readonly int _maxPacket;

    public string Description { get; }

    private LibUsbTransport(UsbContext context, IUsbDevice device, int interfaceId,
        UsbEndpointReader reader, UsbEndpointWriter writer, int maxPacket, string description)
    {
        _context = context;
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
        using var context = new UsbContext();
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
    public static LibUsbTransport Open(ushort vendorId = 0x04CB)
    {
        var context = new UsbContext();
        try
        {
            foreach (var device in context.List())
            {
                if (device.VendorId != vendorId) continue;

                device.Open();
                var iface = FindStillImageInterface(device);
                if (iface == null)
                {
                    device.Dispose();
                    continue;
                }

                device.ClaimInterface(iface.Number);

                var endpoints = iface.Endpoints;
                var bulkIn = endpoints.FirstOrDefault(e => IsBulk(e) && (e.EndpointAddress & 0x80) != 0)
                    ?? throw new PtpException("PTP interface has no bulk-in endpoint.");
                var bulkOut = endpoints.FirstOrDefault(e => IsBulk(e) && (e.EndpointAddress & 0x80) == 0)
                    ?? throw new PtpException("PTP interface has no bulk-out endpoint.");

                var reader = device.OpenEndpointReader((ReadEndpointID)bulkIn.EndpointAddress);
                var writer = device.OpenEndpointWriter((WriteEndpointID)bulkOut.EndpointAddress);
                var description = $"{device.VendorId:X4}:{device.ProductId:X4} {device.Info.Product}";
                return new LibUsbTransport(context, device, iface.Number, reader, writer, bulkIn.MaxPacketSize, description);
            }
        }
        catch
        {
            context.Dispose();
            throw;
        }

        context.Dispose();
        throw new PtpException(
            $"No USB device with vendor id 0x{vendorId:X4} exposing a Still Image interface was found or it could not be opened.");
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
        _context.Dispose();
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
