namespace PhSpectre.Camera;

/// <summary>Byte pipe to a PTP responder, implemented per host.</summary>
public interface ICameraTransport : IAsyncDisposable
{
    /// <summary>Sends one complete PTP container (header + payload) over the bulk-out endpoint.</summary>
    Task SendAsync(byte[] container, CancellationToken ct = default);

    /// <summary>Receives one complete container, reassembled using its length field.</summary>
    Task<byte[]> ReceiveAsync(CancellationToken ct = default);
}
