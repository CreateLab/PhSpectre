using PhSpectre.Camera;

namespace PhSpectre.Wpd;

public static class WpdCameraFactory
{
    /// <summary>Opens the first Fujifilm body, or null. Runs on the thread pool: WPD calls need an MTA thread.</summary>
    public static Task<IPtpTransport?> OpenFirstFujifilmAsync(CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var found = WpdTransport.ListDevices()
            .FirstOrDefault(d => d.PnpId.Contains("vid_04CB", StringComparison.OrdinalIgnoreCase));
        return found == null ? null : (IPtpTransport?)WpdTransport.Open(found);
    }, ct);
}
