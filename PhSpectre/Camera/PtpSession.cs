namespace PhSpectre.Camera;

/// <summary>
/// Minimal PTP initiator: one operation at a time, no event handling. Operations that the camera
/// answers with anything other than OK throw <see cref="PtpException"/> carrying the response code.
/// </summary>
public sealed class PtpSession : IAsyncDisposable
{
    public const ushort OperationGetDeviceInfo = 0x1001;
    public const ushort OperationOpenSession = 0x1002;
    public const ushort OperationCloseSession = 0x1003;
    public const ushort OperationGetDevicePropValue = 0x1015;
    public const ushort OperationSetDevicePropValue = 0x1016;

    public const ushort ResponseOk = 0x2001;
    public const ushort ResponseSessionAlreadyOpen = 0x201E;

    private readonly IPtpTransport _transport;
    private bool _open;

    public PtpSession(IPtpTransport transport)
    {
        _transport = transport;
    }

    public PtpSession(ICameraTransport transport) : this(new ContainerPtpTransport(transport))
    {
    }

    public async Task OpenAsync(uint sessionId = 1, CancellationToken ct = default)
    {
        var response = await _transport.ExecuteAsync(OperationOpenSession, [sessionId], null, ct: ct);
        // WPD/MTP drivers open the MTP session themselves, so "already open" is a usable session.
        if (response.Code is not (ResponseOk or ResponseSessionAlreadyOpen))
            throw Failure(OperationOpenSession, response.Code);
        _open = true;
    }

    public async Task<PtpDeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        var data = await TransactAsync(OperationGetDeviceInfo, [], null, expectDataIn: true, ct);
        return PtpDeviceInfo.Parse(data ?? throw new PtpException("GetDeviceInfo returned no data."));
    }

    public async Task<byte[]> GetPropertyAsync(ushort property, CancellationToken ct = default)
    {
        var data = await TransactAsync(OperationGetDevicePropValue, [property], null, expectDataIn: true, ct);
        return data ?? throw new PtpException($"GetDevicePropValue 0x{property:X4} returned no data.");
    }

    public Task SetPropertyAsync(ushort property, byte[] value, CancellationToken ct = default) =>
        TransactAsync(OperationSetDevicePropValue, [property], value, expectDataIn: false, ct);

    public async ValueTask DisposeAsync()
    {
        if (_open)
        {
            _open = false;
            try
            {
                await TransactAsync(OperationCloseSession, [], null, expectDataIn: false, CancellationToken.None);
            }
            catch (Exception)
            {
                // Camera may already be gone (unplugged); nothing to recover.
            }
        }
        await _transport.DisposeAsync();
    }

    private async Task<byte[]?> TransactAsync(ushort operation, uint[] parameters, byte[]? outgoingData, bool expectDataIn, CancellationToken ct)
    {
        var response = await _transport.ExecuteAsync(operation, parameters, outgoingData, expectDataIn, ct);
        if (response.Code != ResponseOk)
            throw Failure(operation, response.Code);
        return response.Data;
    }

    private static PtpException Failure(ushort operation, ushort responseCode) =>
        new($"Operation 0x{operation:X4} failed with response 0x{responseCode:X4}.", responseCode);
}
