namespace PhSpectre.Camera;

public class PtpException : Exception
{
    /// <summary>PTP response code when the camera rejected the operation; null for protocol/framing errors.</summary>
    public ushort? ResponseCode { get; }

    public PtpException(string message) : base(message) { }

    public PtpException(string message, ushort responseCode) : base(message)
    {
        ResponseCode = responseCode;
    }
}
