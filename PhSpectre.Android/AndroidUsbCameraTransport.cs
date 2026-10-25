using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Android.OS;
using PhSpectre.Camera;

namespace PhSpectre.Android;

/// <summary>
/// <see cref="ICameraTransport"/> over Android's USB host API: one bulk-in and one bulk-out endpoint of the
/// camera's Still Image (PTP) interface. Not yet exercised against a real phone and camera.
/// </summary>
public sealed class AndroidUsbCameraTransport : ICameraTransport
{
    private const int FujifilmVendorId = 0x04CB;
    private const int StillImageClass = 6;
    private const int TransferTimeoutMs = 15_000;
    private const int ReadChunk = 16 * 1024;
    private const string PermissionAction = "com.CompanyName.PhSpectre.USB_PERMISSION";

    private readonly UsbDeviceConnection _connection;
    private readonly UsbInterface _interface;
    private readonly UsbEndpoint _in;
    private readonly UsbEndpoint _out;

    private AndroidUsbCameraTransport(UsbDeviceConnection connection, UsbInterface iface, UsbEndpoint bulkIn, UsbEndpoint bulkOut)
    {
        _connection = connection;
        _interface = iface;
        _in = bulkIn;
        _out = bulkOut;
    }

    /// <summary>Opens the first plugged-in Fujifilm body, asking the user for USB permission if needed. Null when none is attached.</summary>
    public static async Task<IPtpTransport?> OpenFirstFujifilmAsync(CancellationToken ct)
    {
        var context = global::Android.App.Application.Context;
        if (context.GetSystemService(Context.UsbService) is not UsbManager manager) return null;

        UsbDevice? device = null;
        foreach (var candidate in manager.DeviceList?.Values ?? [])
        {
            if (candidate.VendorId == FujifilmVendorId && FindStillImage(candidate) != null)
            {
                device = candidate;
                break;
            }
        }
        if (device == null) return null;

        if (!manager.HasPermission(device) && !await RequestPermissionAsync(context, manager, device, ct))
            throw new PtpException("USB permission for the camera was denied.");

        var iface = FindStillImage(device)!;
        UsbEndpoint? bulkIn = null, bulkOut = null;
        for (var i = 0; i < iface.EndpointCount; i++)
        {
            var endpoint = iface.GetEndpoint(i);
            if (endpoint == null || endpoint.Type != UsbAddressing.XferBulk) continue;
            if (endpoint.Direction == UsbAddressing.In) bulkIn ??= endpoint;
            else bulkOut ??= endpoint;
        }
        if (bulkIn == null || bulkOut == null)
            throw new PtpException("The camera's PTP interface has no bulk endpoints.");

        var connection = manager.OpenDevice(device)
            ?? throw new PtpException("Android could not open the camera.");
        if (!connection.ClaimInterface(iface, true))
        {
            connection.Close();
            throw new PtpException("Another app is using the camera.");
        }
        return new ContainerPtpTransport(new AndroidUsbCameraTransport(connection, iface, bulkIn, bulkOut));
    }

    public Task SendAsync(byte[] container, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            Write(container);
            // A transfer ending exactly on a packet boundary needs a zero-length packet to terminate it.
            if (container.Length % _out.MaxPacketSize == 0)
                Write([]);
        }, ct);

    public Task<byte[]> ReceiveAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var first = new byte[ReadChunk];
            int read;
            do
            {
                ct.ThrowIfCancellationRequested();
                read = _connection.BulkTransfer(_in, first, first.Length, TransferTimeoutMs);
                if (read < 0) throw new PtpException("USB read failed or timed out.");
            } while (read == 0); // skip zero-length terminators of the previous transfer

            if (read < PtpContainer.HeaderSize) throw new PtpException($"Short USB read ({read} bytes).");

            var total = (int)BinaryPrimitives.ReadUInt32LittleEndian(first);
            var result = new byte[Math.Max(total, read)];
            Array.Copy(first, result, read);

            var chunk = new byte[ReadChunk];
            while (read < total)
            {
                ct.ThrowIfCancellationRequested();
                var got = _connection.BulkTransfer(_in, chunk, Math.Min(chunk.Length, total - read), TransferTimeoutMs);
                if (got < 0) throw new PtpException("USB read failed or timed out.");
                if (got == 0) break;
                Array.Copy(chunk, 0, result, read, got);
                read += got;
            }
            return result;
        }, ct);

    public ValueTask DisposeAsync()
    {
        try { _connection.ReleaseInterface(_interface); } catch (Exception) { /* unplugged */ }
        _connection.Close();
        return ValueTask.CompletedTask;
    }

    private void Write(byte[] data)
    {
        if (_connection.BulkTransfer(_out, data, data.Length, TransferTimeoutMs) < 0)
            throw new PtpException("USB write failed or timed out.");
    }

    private static UsbInterface? FindStillImage(UsbDevice device)
    {
        for (var i = 0; i < device.InterfaceCount; i++)
        {
            var iface = device.GetInterface(i);
            if (iface != null && (int)iface.InterfaceClass == StillImageClass) return iface;
        }
        return null;
    }

    private static async Task<bool> RequestPermissionAsync(Context context, UsbManager manager, UsbDevice device, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new PermissionReceiver(tcs);
        var filter = new IntentFilter(PermissionAction);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
        else
            context.RegisterReceiver(receiver, filter);
        try
        {
            // Mutable on purpose: the system adds the device and the grant result to this intent.
            var flags = Build.VERSION.SdkInt >= BuildVersionCodes.S ? PendingIntentFlags.Mutable : PendingIntentFlags.UpdateCurrent;
            var intent = new Intent(PermissionAction).SetPackage(context.PackageName);
            var pending = PendingIntent.GetBroadcast(context, 0, intent, flags);
            manager.RequestPermission(device, pending);
            using var registration = ct.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        finally
        {
            context.UnregisterReceiver(receiver);
        }
    }

    private sealed class PermissionReceiver(TaskCompletionSource<bool> result) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent) =>
            result.TrySetResult(intent?.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false) ?? false);
    }
}
