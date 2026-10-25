namespace PhSpectre.Camera;

/// <summary>
/// Byte pipe to a PTP responder. Implemented per host (libusb on Desktop, UsbManager on Android);
/// core only sees whole PTP containers so it stays free of OS dependencies.
/// </summary>
public interface ICameraTransport : IAsyncDisposable
{
    /// <summary>Sends one complete PTP container (header + payload) over the bulk-out endpoint.</summary>
    Task SendAsync(byte[] container, CancellationToken ct = default);

    /// <summary>
    /// Receives one complete PTP container. The implementation is responsible for reassembling
    /// multi-packet transfers using the length field in the container header.
    /// </summary>
    Task<byte[]> ReceiveAsync(CancellationToken ct = default);
}
