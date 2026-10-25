namespace PhSpectre.Camera;

public sealed record PtpResponse(ushort Code, uint[] Parameters, byte[]? Data);

/// <summary>
/// One PTP operation round trip (command, optional data phase either way, response). Transports that
/// work below this level (raw USB bulk) are adapted by <see cref="ContainerPtpTransport"/>; transports
/// that only expose operations (Windows WPD/MTP) implement this directly.
/// </summary>
public interface IPtpTransport : IAsyncDisposable
{
    /// <summary>
    /// Runs one operation. A non-OK response code is returned, not thrown, so callers decide
    /// which codes are acceptable. Transport/framing failures throw <see cref="PtpException"/>.
    /// </summary>
    /// <param name="expectDataIn">
    /// True when the operation has a device-to-host data phase. Container transports see this on the wire;
    /// WPD must be told up front because it uses a different command per data direction.
    /// </param>
    Task<PtpResponse> ExecuteAsync(ushort operation, uint[] parameters, byte[]? dataOut, bool expectDataIn = false, CancellationToken ct = default);
}
