using PhSpectre.Camera;

namespace PhSpectre.Usb;

public static class LibUsbCameraFactory
{
    /// <summary>
    /// Opens the first plugged-in Fujifilm body over libusb (macOS and Linux), or returns null when there is none.
    /// A missing native libusb surfaces as a <see cref="PtpException"/> that says how to install it.
    /// </summary>
    public static Task<IPtpTransport?> OpenFirstFujifilmAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return LibUsbTransport.OpenIfPresent() is { } usb ? (IPtpTransport?)new ContainerPtpTransport(usb) : null;
        }
        catch (Exception ex) when (ex is not PtpException && LibUsbTransport.MissingLibraryHint(ex) is { } hint)
        {
            throw new PtpException(hint);
        }
    }, ct);
}
