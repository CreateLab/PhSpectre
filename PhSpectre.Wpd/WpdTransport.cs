using System.Runtime.InteropServices;
using PhSpectre.Camera;

namespace PhSpectre.Wpd;

public sealed record WpdDeviceInfo(string PnpId, string FriendlyName);

/// <summary>
/// <see cref="IPtpTransport"/> that tunnels PTP operations through the Windows MTP driver using WPD's
/// MTP vendor-extension commands. Windows opens the MTP session itself, so OpenSession is normally
/// answered with SessionAlreadyOpen, which <see cref="PtpSession"/> accepts.
/// </summary>
public sealed class WpdTransport : IPtpTransport
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DefaultReadChunk = 64 * 1024;

    private readonly IPortableDevice _device;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WpdDeviceInfo Info { get; }

    private WpdTransport(IPortableDevice device, WpdDeviceInfo info)
    {
        _device = device;
        Info = info;
    }

    public static IReadOnlyList<WpdDeviceInfo> ListDevices()
    {
        var manager = (IPortableDeviceManager)Activator.CreateInstance(WpdClasses.Manager)!;
        try
        {
            manager.RefreshDeviceList();
            uint count = 0;
            manager.GetDevices(IntPtr.Zero, ref count);
            if (count == 0) return [];

            // Raw LPWSTR[] buffer: the default COM marshaller would turn an IntPtr[] into a SAFEARRAY.
            var ids = Marshal.AllocCoTaskMem((int)count * IntPtr.Size);
            try
            {
                manager.GetDevices(ids, ref count);

                var result = new List<WpdDeviceInfo>();
                for (var i = 0; i < count; i++)
                {
                    var idPtr = Marshal.ReadIntPtr(ids, i * IntPtr.Size);
                    var id = Marshal.PtrToStringUni(idPtr) ?? string.Empty;
                    Marshal.FreeCoTaskMem(idPtr);
                    result.Add(new WpdDeviceInfo(id, FriendlyName(manager, id)));
                }
                return result;
            }
            finally
            {
                Marshal.FreeCoTaskMem(ids);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    /// <summary>Opens the first WPD device whose id contains the given USB vendor id (Fujifilm = 04CB).</summary>
    public static WpdTransport Open(string vendorIdHex = "04CB")
    {
        var match = ListDevices().FirstOrDefault(d => d.PnpId.Contains($"vid_{vendorIdHex}", StringComparison.OrdinalIgnoreCase))
            ?? throw new PtpException($"No WPD device with USB vendor id {vendorIdHex} found.");
        return Open(match);
    }

    public static WpdTransport Open(WpdDeviceInfo info)
    {
        var device = (IPortableDevice)Activator.CreateInstance(WpdClasses.Device)!;
        var clientInfo = NewValues();
        try
        {
            var name = WpdKeys.ClientName;
            var major = WpdKeys.ClientMajorVersion;
            var minor = WpdKeys.ClientMinorVersion;
            var revision = WpdKeys.ClientRevision;
            var access = WpdKeys.ClientDesiredAccess;
            clientInfo.SetStringValue(ref name, "PhSpectre");
            clientInfo.SetUnsignedIntegerValue(ref major, 1);
            clientInfo.SetUnsignedIntegerValue(ref minor, 0);
            clientInfo.SetUnsignedIntegerValue(ref revision, 0);
            clientInfo.SetUnsignedIntegerValue(ref access, GenericRead | GenericWrite);
            device.Open(info.PnpId, clientInfo);
        }
        catch (COMException e)
        {
            Marshal.ReleaseComObject(device);
            throw new PtpException($"Cannot open WPD device '{info.FriendlyName}': 0x{e.HResult:X8} {e.Message}");
        }
        finally
        {
            Marshal.ReleaseComObject(clientInfo);
        }
        return new WpdTransport(device, info);
    }

    public async Task<PtpResponse> ExecuteAsync(ushort operation, uint[] parameters, byte[]? dataOut,
        bool expectDataIn = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => Execute(operation, parameters, dataOut, expectDataIn), ct);
        }
        catch (COMException e)
        {
            throw new PtpException($"WPD call failed for operation 0x{operation:X4}: 0x{e.HResult:X8} {e.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _device.Close(); } catch (COMException) { /* already disconnected */ }
        Marshal.ReleaseComObject(_device);
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private PtpResponse Execute(ushort operation, uint[] parameters, byte[]? dataOut, bool expectDataIn)
    {
        // The MTP driver owns the session lifecycle and rejects a hand-sent Open/CloseSession
        // (observed on X-T5: ParameterNotSupported 0x2006), so report success without sending.
        if (operation is PtpSession.OperationOpenSession or PtpSession.OperationCloseSession)
            return new PtpResponse(PtpSession.ResponseOk, [], null);

        if (dataOut != null) return ExecuteWithDataOut(operation, parameters, dataOut);
        if (expectDataIn) return ExecuteWithDataIn(operation, parameters);

        var results = Send(WpdKeys.CommandWithoutDataPhase, operation, parameters);
        return ReadResponse(results, null);
    }

    private PtpResponse ExecuteWithDataIn(ushort operation, uint[] parameters)
    {
        var begin = Send(WpdKeys.CommandWithDataToRead, operation, parameters);
        var context = GetString(begin, WpdKeys.TransferContext);
        var total = GetUInt64(begin, WpdKeys.TransferTotalDataSize);
        var chunk = TryGetUInt32(begin, WpdKeys.OptimalTransferBufferSize) is { } optimal and > 0 ? optimal : DefaultReadChunk;

        var data = new byte[total];
        var offset = 0;
        while ((ulong)offset < total)
        {
            var want = (uint)Math.Min(chunk, total - (ulong)offset);
            var step = NewTransferCommand(WpdKeys.CommandReadData, context);
            var toRead = WpdKeys.TransferNumBytesToRead;
            var dataKey = WpdKeys.TransferData;
            step.SetUnsignedIntegerValue(ref toRead, want);

            // WPD requires an output buffer of the requested size supplied by the caller.
            var scratch = Marshal.AllocCoTaskMem((int)want);
            try
            {
                step.SetBufferValue(ref dataKey, scratch, want);
                _device.SendCommand(0, step, out var stepResults);
                ThrowIfFailed(stepResults, "ReadData");

                var read = GetUInt32(stepResults, WpdKeys.TransferNumBytesRead);
                var buffer = GetBuffer(stepResults, WpdKeys.TransferData);
                if (read == 0 || buffer.Length == 0)
                    throw new PtpException("WPD returned an empty data block before the transfer completed.");
                var take = (int)Math.Min((uint)buffer.Length, read);
                Array.Copy(buffer, 0, data, offset, take);
                offset += take;
            }
            finally
            {
                Marshal.FreeCoTaskMem(scratch);
            }
        }

        return ReadResponse(EndTransfer(context), data);
    }

    private PtpResponse ExecuteWithDataOut(ushort operation, uint[] parameters, byte[] data)
    {
        var begin = SendBegin(WpdKeys.CommandWithDataToWrite, operation, parameters, (ulong)data.Length);
        var context = GetString(begin, WpdKeys.TransferContext);
        var chunk = TryGetUInt32(begin, WpdKeys.OptimalTransferBufferSize) is { } optimal and > 0 ? optimal : DefaultReadChunk;

        var offset = 0;
        while (offset < data.Length)
        {
            var count = (uint)Math.Min(chunk, (uint)(data.Length - offset));
            var step = NewTransferCommand(WpdKeys.CommandWriteData, context);
            var toWrite = WpdKeys.TransferNumBytesToWrite;
            var dataKey = WpdKeys.TransferData;
            step.SetUnsignedIntegerValue(ref toWrite, count);

            var block = Marshal.AllocCoTaskMem((int)count);
            try
            {
                Marshal.Copy(data, offset, block, (int)count);
                step.SetBufferValue(ref dataKey, block, count);
                _device.SendCommand(0, step, out var stepResults);
                ThrowIfFailed(stepResults, "WriteData");
                var written = GetUInt32(stepResults, WpdKeys.TransferNumBytesWritten);
                if (written == 0)
                    throw new PtpException("WPD accepted zero bytes of the data phase.");
                offset += (int)written;
            }
            finally
            {
                Marshal.FreeCoTaskMem(block);
            }
        }

        return ReadResponse(EndTransfer(context), null);
    }

    private IPortableDeviceValues EndTransfer(string context) =>
        SendValues(NewTransferCommand(WpdKeys.CommandEndDataTransfer, context));

    private IPortableDeviceValues NewTransferCommand(uint commandId, string context)
    {
        var values = NewMtpCommand(commandId);
        var contextKey = WpdKeys.TransferContext;
        values.SetStringValue(ref contextKey, context);
        return values;
    }

    private IPortableDeviceValues Send(uint commandId, ushort operation, uint[] parameters) =>
        SendBegin(commandId, operation, parameters, null);

    private IPortableDeviceValues SendBegin(uint commandId, ushort operation, uint[] parameters, ulong? totalDataSize)
    {
        var values = NewMtpCommand(commandId);
        var opKey = WpdKeys.OperationCode;
        var paramsKey = WpdKeys.OperationParams;
        values.SetUnsignedIntegerValue(ref opKey, operation);

        var collection = (IPortableDevicePropVariantCollection)Activator.CreateInstance(WpdClasses.PropVariantCollection)!;
        foreach (var p in parameters)
        {
            var variant = PropVariantUInt32.Of(p);
            collection.Add(ref variant);
        }
        values.SetIUnknownValue(ref paramsKey, collection);

        if (totalDataSize is { } size)
        {
            var sizeKey = WpdKeys.TransferTotalDataSize;
            values.SetUnsignedLargeIntegerValue(ref sizeKey, size);
        }
        return SendValues(values);
    }

    private IPortableDeviceValues SendValues(IPortableDeviceValues command)
    {
        _device.SendCommand(0, command, out var results);
        ThrowIfFailed(results, "SendCommand");
        return results;
    }

    private static IPortableDeviceValues NewMtpCommand(uint commandId)
    {
        var values = NewValues();
        var categoryKey = WpdKeys.CommandCategory;
        var idKey = WpdKeys.CommandId;
        var category = WpdKeys.MtpExtCategory;
        values.SetGuidValue(ref categoryKey, ref category);
        values.SetUnsignedIntegerValue(ref idKey, commandId);
        return values;
    }

    private static IPortableDeviceValues NewValues() =>
        (IPortableDeviceValues)Activator.CreateInstance(WpdClasses.Values)!;

    private static PtpResponse ReadResponse(IPortableDeviceValues results, byte[]? data)
    {
        var codeKey = WpdKeys.ResponseCode;
        results.GetUnsignedIntegerValue(ref codeKey, out var code);

        var parameters = new List<uint>();
        var paramsKey = WpdKeys.ResponseParams;
        try
        {
            results.GetIUnknownValue(ref paramsKey, out var raw);
            var collection = (IPortableDevicePropVariantCollection)raw;
            collection.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                collection.GetAt(i, out var variant);
                parameters.Add(variant.Value);
            }
        }
        catch (COMException)
        {
            // No response parameters.
        }
        return new PtpResponse((ushort)code, parameters.ToArray(), data);
    }

    private static void ThrowIfFailed(IPortableDeviceValues results, string step)
    {
        var key = WpdKeys.Hresult;
        try
        {
            results.GetUnsignedIntegerValue(ref key, out var hr);
            if ((int)hr < 0)
                throw new PtpException($"WPD {step} failed: HRESULT 0x{hr:X8}.");
        }
        catch (COMException)
        {
            // Result carries no HRESULT; treat as success.
        }
    }

    private static string GetString(IPortableDeviceValues values, PropertyKey key)
    {
        values.GetStringValue(ref key, out var ptr);
        try { return Marshal.PtrToStringUni(ptr) ?? string.Empty; }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    private static uint GetUInt32(IPortableDeviceValues values, PropertyKey key)
    {
        values.GetUnsignedIntegerValue(ref key, out var v);
        return v;
    }

    private static uint? TryGetUInt32(IPortableDeviceValues values, PropertyKey key)
    {
        try { return GetUInt32(values, key); }
        catch (COMException) { return null; }
    }

    private static ulong GetUInt64(IPortableDeviceValues values, PropertyKey key)
    {
        values.GetUnsignedLargeIntegerValue(ref key, out var v);
        return v;
    }

    private static byte[] GetBuffer(IPortableDeviceValues values, PropertyKey key)
    {
        values.GetBufferValue(ref key, out var ptr, out var length);
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(ptr, bytes, 0, (int)length);
            return bytes;
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    private static string FriendlyName(IPortableDeviceManager manager, string pnpId)
    {
        uint chars = 0;
        manager.GetDeviceFriendlyName(pnpId, IntPtr.Zero, ref chars);
        if (chars == 0) return string.Empty;

        var buffer = Marshal.AllocCoTaskMem((int)chars * 2);
        try
        {
            manager.GetDeviceFriendlyName(pnpId, buffer, ref chars);
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }
}
