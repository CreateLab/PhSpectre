namespace PhSpectre.Camera;

public sealed record PtpResponse(ushort Code, uint[] Parameters, byte[]? Data);

/// <summary>
/// One PTP operation (command, data phase, response). Byte transports are adapted by
/// <see cref="ContainerPtpTransport"/>; WPD implements this directly.
/// </summary>
public interface IPtpTransport : IAsyncDisposable
{
    /// <summary>Non-OK response codes are returned; transport failures throw <see cref="PtpException"/>.</summary>
    /// <param name="expectDataIn">True for a device-to-host data phase; WPD needs to know up front.</param>
    Task<PtpResponse> ExecuteAsync(ushort operation, uint[] parameters, byte[]? dataOut, bool expectDataIn = false, CancellationToken ct = default);
}
