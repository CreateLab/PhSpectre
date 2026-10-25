using System;
using System.Threading;
using System.Threading.Tasks;
using PhSpectre.Camera;

namespace PhSpectre.Avalonia.Services;

/// <summary>
/// Entry point to the camera. The core and the UI know nothing about USB; each host sets
/// <see cref="TransportFactory"/> at startup (Windows: WPD, Android: UsbManager), the same way
/// FileDialogService.GallerySaver is wired. With no factory the camera features are hidden.
/// </summary>
public static class CameraService
{
    /// <summary>Returns a transport for the first Fujifilm body that is plugged in, or null when there is none.</summary>
    public static Func<CancellationToken, Task<IPtpTransport?>>? TransportFactory { get; set; }

    public static bool IsSupported => TransportFactory != null;

    /// <summary>Opens a PTP session with the connected camera; the caller disposes it. Null when no camera is found.</summary>
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
