using System;
using System.Threading;
using System.Threading.Tasks;
using PhSpectre.Camera;

namespace PhSpectre.Avalonia.Services;

/// <summary>
/// Entry point to the camera. Each host sets <see cref="TransportFactory"/> at startup; without it the camera
/// features are hidden.
/// </summary>
public static class CameraService
{
    /// <summary>Transport for the first plugged-in Fujifilm body, or null.</summary>
    public static Func<CancellationToken, Task<IPtpTransport?>>? TransportFactory { get; set; }

    public static bool IsSupported => TransportFactory != null;

    /// <summary>Opens a PTP session; the caller disposes it. Null when no camera is found.</summary>
    internal static async Task<PtpSession?> OpenAsync(CancellationToken ct)
    {
        var factory = TransportFactory;
        if (factory == null) return null;

        var transport = await factory(ct);
        if (transport == null) return null;

        var session = new PtpSession(transport);
        try
        {
            await session.OpenAsync(ct: ct);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }
}
