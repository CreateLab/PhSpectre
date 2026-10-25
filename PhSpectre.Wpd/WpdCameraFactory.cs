using PhSpectre.Camera;

namespace PhSpectre.Wpd;

public static class WpdCameraFactory
{
    /// <summary>
    /// Opens the first plugged-in Fujifilm body, or returns null when there is none. Runs on the thread pool
    /// (an MTA thread), which is where the WPD calls were verified to work; a UI thread is STA.
    /// </summary>
    public static Task<IPtpTransport?> OpenFirstFujifilmAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var found = WpdTransport.ListDevices()
            .FirstOrDefault(d => d.PnpId.Contains("vid_04CB", StringComparison.OrdinalIgnoreCase));
        return found == null ? null : (IPtpTransport?)WpdTransport.Open(found);
    }, ct);
}
