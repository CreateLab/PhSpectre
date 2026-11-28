using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PhSpectre.Services;

namespace PhSpectre.Avalonia.Services;

// Whether a newer release is available; shared by the desktop banner and the Android settings row, checked once per
// launch.
public sealed partial class AppUpdateService : ObservableObject
{
    public static AppUpdateService Instance { get; } = new();

    /// <summary>False in Google Play builds.</summary>
    public static bool Enabled { get; set; } = true;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private string? _latestVersionText;
    [ObservableProperty] private string? _releaseUrl;

    public string CurrentVersionText => AppVersionInfo.DisplayVersion;

    private AppUpdateService() { }

    public async Task CheckAsync()
    {
        // Store builds update through the store and use no network.
        if (!Enabled) return;

        // Dev builds have no version to compare.
        if (AppVersionInfo.Version is not { } currentVersion) return;

        var state = UpdateCheckStorage.Load();
        var dueForCheck = state.LastCheckedUtc is null
            || DateTime.UtcNow - state.LastCheckedUtc.Value >= CheckInterval;

        if (dueForCheck)
        {
            var result = await UpdateChecker.CheckAsync(currentVersion);
            state.LastCheckedUtc = DateTime.UtcNow;

            if (result.Status == UpdateStatus.UpdateAvailable && result.Version != null)
            {
                state.LastKnownVersion = result.Version.ToString();
                state.LastKnownUrl = result.Url;
            }
            else if (result.Status == UpdateStatus.UpToDate)
            {
                // Up to date: drop the remembered newer version.
                state.LastKnownVersion = null;
                state.LastKnownUrl = null;
            }
            // On failure keep the last known version.

            UpdateCheckStorage.Save(state);
        }

        ApplyState(state, currentVersion);
    }

    private void ApplyState(UpdateCheckState state, Version currentVersion)
    {
        if (state.LastKnownVersion is not { } known
            || !Version.TryParse(known, out var knownVersion)
            || knownVersion <= currentVersion
            || known == state.DismissedVersion)
        {
            IsAvailable = false;
            return;
        }

        LatestVersionText = known;
        ReleaseUrl = state.LastKnownUrl;
        IsAvailable = true;
    }

    public void Dismiss()
    {
        if (LatestVersionText is not { } version) return;

        var state = UpdateCheckStorage.Load();
        state.DismissedVersion = version;
        UpdateCheckStorage.Save(state);

        IsAvailable = false;
    }
}
