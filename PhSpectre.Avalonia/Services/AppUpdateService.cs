using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PhSpectre.Services;

namespace PhSpectre.Avalonia.Services;

// Single source of truth for "is a newer release available", shared by the Desktop
// toolbar banner and the Android Settings row so the check only runs once per launch.
// App.axaml.cs kicks off CheckAsync() in the background right after the root view is
// constructed; both platforms' ViewModels just read this singleton's properties.
public sealed partial class AppUpdateService : ObservableObject
{
    public static AppUpdateService Instance { get; } = new();

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private string? _latestVersionText;
    [ObservableProperty] private string? _releaseUrl;

    public string CurrentVersionText => AppVersionInfo.DisplayVersion;

    private AppUpdateService() { }

    public async Task CheckAsync()
    {
        // Dev builds have no meaningful version to compare against — skip silently
        // rather than reporting every dev build as perpetually out of date.
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
                // No update anymore (e.g. user upgraded some other way) — clear any
                // stale "known newer version" so a dismissed-then-re-released version
                // doesn't linger forever.
                state.LastKnownVersion = null;
                state.LastKnownUrl = null;
            }
            // Unknown (network/API failure) leaves the previous LastKnownVersion as-is.

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
