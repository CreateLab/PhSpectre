using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhSpectre.Avalonia.Services;
using PhSpectre.Camera;
using PhSpectre.Models;
using PhSpectre.Recipes;

namespace PhSpectre.Avalonia.ViewModels;

/// <summary>The write dialog: warning, progress, result. Everything before it lives in the Camera tab.</summary>
public enum CameraStep { Closed, Warning, Writing, Done, Error }

/// <summary>What the Camera tab shows.</summary>
public enum CameraTabState { Off, Searching, WrongMode, Ready, ReadOnly }

public enum WriteStepState { Pending, Active, Done }

public sealed partial class CameraSlotItem : ObservableObject
{
    public CameraSlotItem(FujiSlotInfo info, ICommand select)
    {
        Info = info;
        SelectCommand = select;
    }

    public ICommand SelectCommand { get; }

    public FujiSlotInfo Info { get; }
    public int Number => Info.Slot;
    public string Label => $"C{Info.Slot}";
    public string Title => Info.Name ?? "Empty";
    public bool IsEmpty => Info.IsEmpty;
    public bool IsOccupied => !Info.IsEmpty;

    /// <summary>Key parameters under the name: "Classic Neg. · DR400 · Daylight".</summary>
    public string Detail
    {
        get
        {
            if (IsEmpty) return "Not set";
            var r = Info.Recipe;
            var parts = new[] { r.FilmSimulation, r.DynamicRange, r.WhiteBalance }.Where(p => !string.IsNullOrEmpty(p));
            return string.Join(" · ", parts);
        }
    }

    [ObservableProperty] private bool _isSelected;

    public double TitleOpacity => IsEmpty ? 0.55 : 1.0;
}

public sealed class RestorePointItem
{
    internal RestorePointItem(RestorePointEntry entry, ICommand restore)
    {
        Entry = entry;
        RestoreCommand = restore;
    }

    internal RestorePointEntry Entry { get; }
    public ICommand RestoreCommand { get; }

    public string Title => $"C{Entry.Backup.Slot} · {FormatWhen(Entry.Backup.Created)}";
    public string Subtitle => Entry.Backup.SlotName is { Length: > 0 } n ? $"Was: {n}" : "Was empty";

    private static string FormatWhen(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var days = (DateTime.Now.Date - local.Date).Days;
        var time = local.ToString("HH:mm");
        return days switch
        {
            0 => $"today {time}",
            1 => $"yesterday {time}",
            _ => $"{local:MMM d} {time}",
        };
    }
}

public sealed partial class WriteProgressItem : ObservableObject
{
    public WriteProgressItem(string label) => Label = label;

    public string Label { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsActive), nameof(IsDone))]
    private WriteStepState _state;

    public bool IsPending => State == WriteStepState.Pending;
    public bool IsActive => State == WriteStepState.Active;
    public bool IsDone => State == WriteStepState.Done;
}

public sealed partial class ManualStepItem : ObservableObject
{
    public ManualStepItem(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public string Value { get; }

    [ObservableProperty] private bool _isDone;
}

/// <summary>
/// Everything about the camera. The Camera tab shows <see cref="TabState"/>: off, searching, ready (slots + the
/// recipe to write) or read-only (manual steps). While enabled it watches the USB port in the background, which
/// also drives the indicator in the top bar. Writing happens in a modal dialog: warning, progress, result.
/// </summary>
public sealed partial class CameraDialogViewModel : ObservableObject
{
    private const int ManualPreviewCount = 5;

    private readonly SettingsViewModel _settings;
    private readonly Action<string> _toast;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly List<ManualStepItem> _manualAll = [];
    private FujiCameraSnapshot? _snapshot;
    private CancellationTokenSource? _monitor;
    private CancellationTokenSource? _cts;
    private bool _enabled;
    private bool _needsRefresh;
    private bool _manualExpanded;
    private bool _hasError;
    private string? _restorePath;
    private FujiSlotBackup? _restorePoint;
    private string _errorDetails = "";
    private FilmRecipe? _writing;

    public CameraDialogViewModel(SettingsViewModel settings, Action<string> toast)
    {
        _settings = settings;
        _toast = toast;
        RefreshRestorePoints();
    }

    // ── tab state ────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private CameraTabState _tabState = CameraTabState.Off;
    [ObservableProperty] private string _connectStatus = "Looking for a camera…";
    [ObservableProperty] private string _cameraName = "";
    [ObservableProperty] private string _cameraDetail = "";
    [ObservableProperty] private string _readOnlyText = "";
    [ObservableProperty] private CameraSlotItem? _selectedSlot;

    public ObservableCollection<CameraSlotItem> Slots { get; } = [];
    public ObservableCollection<ManualStepItem> ManualSteps { get; } = [];
    public ObservableCollection<RestorePointItem> RestorePoints { get; } = [];

    public bool IsTabOff => TabState == CameraTabState.Off;
    public bool IsTabSearching => TabState is CameraTabState.Searching or CameraTabState.WrongMode;
    public bool IsTabReady => TabState == CameraTabState.Ready;
    public bool IsTabReadOnly => TabState == CameraTabState.ReadOnly;
    public bool IsTabConnected => TabState is CameraTabState.Ready or CameraTabState.ReadOnly;
    public bool ShowSlots => IsTabConnected && Slots.Count > 0;
    public bool IsSingleSlot => Slots.Count == 1;
    public bool HasReadOnlyText => ReadOnlyText.Length > 0;
    public bool HasManualMore => _manualAll.Count > ManualPreviewCount && !_manualExpanded;
    public string ManualMoreText => $"+ {_manualAll.Count - ManualPreviewCount} more";
    public FujiCameraProfile? Profile => _snapshot?.Profile;
    public int RestoreKeepIndex
    {
        get => _settings.RestoreKeepIndex;
        set
        {
            _settings.RestoreKeepIndex = value;
            OnPropertyChanged();
        }
    }

    // The "Camera setup" dialog: what the feature does and the Turn on button (shown while the feature is off).
    [ObservableProperty] private bool _setupOpen;
    public void OpenSetup() => SetupOpen = true;
    public void CloseSetup() => SetupOpen = false;
    [RelayCommand]
    private void CloseSetupDialog() => SetupOpen = false;

    /// <summary>Under the slot list: what the chosen slot holds and that it is saved first.</summary>
    public string SlotNote => SelectedSlot is { IsOccupied: true } s ? $"{s.Label} “{s.Title}” will be replaced. It is saved first." : "";
    public bool HasSlotNote => SlotNote.Length > 0;
    public bool HasRestorePoints => RestorePoints.Count > 0;
    public string RestorePointsText => $"Restore points · {RestorePoints.Count}";
    public string RestoreFolder => RestorePointStore.DisplayFolder;
    public bool CanOpenRestoreFolder => !OperatingSystem.IsAndroid();

    /// <summary>Badge on the camera card: green when ready, amber when it can only be read.</summary>
    public string CameraBadge => TabState switch
    {
        CameraTabState.Ready => "Ready",
        CameraTabState.ReadOnly => "Can't write",
        CameraTabState.WrongMode => "Wrong USB mode",
        _ => "Searching",
    };
    public bool BadgeIsOk => TabState == CameraTabState.Ready;
    public bool BadgeIsWarn => TabState is CameraTabState.ReadOnly or CameraTabState.WrongMode;
    public bool BadgeIsIdle => TabState is CameraTabState.Searching or CameraTabState.Off;
    public string CardTitle => TabState is CameraTabState.Searching or CameraTabState.Off ? "No camera" : CameraName;
    public string CardDetail => TabState is CameraTabState.Searching or CameraTabState.Off ? "Not connected" : CameraDetail;

    partial void OnTabStateChanged(CameraTabState value)
    {
        OnPropertyChanged(string.Empty);
        _settings.Library.CameraChanged();
    }
    partial void OnReadOnlyTextChanged(string value) => OnPropertyChanged(nameof(HasReadOnlyText));

    // ── indicator in the top bar ─────────────────────────────────────────────────────────
    public bool IndicatorVisible => _enabled && CameraService.IsSupported;
    public bool IndicatorOk => IndicatorVisible && Step == CameraStep.Closed && !_hasError && IsTabConnected;
    public bool IndicatorIdle => IndicatorVisible && Step == CameraStep.Closed && !_hasError && !IsTabConnected;
    public bool IndicatorBusy => IndicatorVisible && Step == CameraStep.Writing;
    public bool IndicatorWarn => IndicatorVisible && _hasError && Step != CameraStep.Writing;
    public string IndicatorText =>
        Step == CameraStep.Writing ? $"Writing to {SelectedSlot?.Label}…"
        : _hasError ? "Check camera"
        : IsTabConnected ? ShortModel
        : "No camera";
    private string ShortModel => _snapshot?.Model ?? CameraName.Replace("FUJIFILM ", "");

    // ── recipe to write ──────────────────────────────────────────────────────────────────
    private FilmRecipe? CurrentRecipe => _settings.Library.Selected is { Recipe: { } r } row
        ? r with { RecipeName = _settings.SlotNameFor(r with { RecipeName = row.DisplayTitle }) }
        : null;

    public bool HasRecipe => _settings.Library.HasSelection;
    public bool NoRecipe => !HasRecipe;
    public string RecipeName => CurrentRecipe?.RecipeName ?? "";
    public string RecipeDetail
    {
        get
        {
            if (CurrentRecipe is not { } recipe) return "";
            var count = RecipeFields.BuildAll(recipe).Count;
            var from = _settings.RecipeSourceBadgeText;
            return $"{from} · {count} settings";
        }
    }
    public int SkippedCount => CurrentRecipe is { } r ? FujiSlotEncoder.Encode(r, _snapshot?.Profile).Skipped.Count : 0;
    public bool HasSkipped => SkippedCount > 0;
    public string SkippedBadge => $"{SkippedCount} SKIPPED";

    // No slot is chosen on its own while there is no recipe; with one, the first empty slot is only suggested, so nothing named gets replaced by accident.
    private void SuggestSlot()
    {
        if (SelectedSlot == null && HasRecipe && IsTabReady && Slots.FirstOrDefault(s => s.IsEmpty) is { } empty) SelectSlot(empty);
    }

    public void RecipeChanged()
    {
        SuggestSlot();
        RebuildManual();
        BuildDiff();
        OnPropertyChanged(nameof(HasRecipe));
        OnPropertyChanged(nameof(NoRecipe));
        OnPropertyChanged(nameof(RecipeName));
        OnPropertyChanged(nameof(RecipeDetail));
        OnPropertyChanged(nameof(SkippedCount));
        OnPropertyChanged(nameof(HasSkipped));
        OnPropertyChanged(nameof(SkippedBadge));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(WriteButtonText));
    }


    // ── footer ───────────────────────────────────────────────────────────────────────────
    public bool CanWrite => IsTabReady && SelectedSlot != null && HasRecipe;
    public bool CanRead => IsTabConnected && SelectedSlot is { IsOccupied: true };
    public string WriteButtonText =>
        !HasRecipe ? "Choose a recipe to write"
        : SelectedSlot == null ? "Pick a slot"
        : IsSingleSlot ? $"Replace {SelectedSlot.Label}"
        : $"Write “{RecipeName}” to {SelectedSlot.Label}";
    public string ReadButtonText => SelectedSlot is { IsOccupied: true } s ? $"Read {s.Label} into the app" : "Read a slot into the app";
    public bool ShowFooter => IsTabReady;
    public bool ShowReadOnlyFooter => IsTabReadOnly && Slots.Count > 0;

    partial void OnSelectedSlotChanged(CameraSlotItem? value)
    {
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(CanRead));
        OnPropertyChanged(nameof(WriteButtonText));
        OnPropertyChanged(nameof(ReadButtonText));
        OnPropertyChanged(nameof(SlotNote));
        OnPropertyChanged(nameof(HasSlotNote));
        BuildDiff();
    }

    // ── on / off ─────────────────────────────────────────────────────────────────────────
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _monitor?.Cancel();
        _monitor = null;
        if (enabled)
        {
            _snapshot = null;
            _hasError = false;
            TabState = CameraTabState.Searching;
            ConnectStatus = "Looking for a camera…";
            _monitor = new CancellationTokenSource();
            _ = MonitorAsync(_monitor.Token);
        }
        else
        {
            ClearCamera();
            TabState = CameraTabState.Off;
        }
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void TurnOn() => _settings.CameraExperimental = true;

    [RelayCommand]
    private void TurnOff() => _settings.CameraExperimental = false;

    private void ClearCamera()
    {
        _snapshot = null;
        SelectedSlot = null;
        Slots.Clear();
        ManualSteps.Clear();
        _manualAll.Clear();
        ReadOnlyText = "";
        CameraName = "";
        CameraDetail = "";
    }

    // ── watching the USB port ────────────────────────────────────────────────────────────
    private async Task MonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delay = 1500;
            // While the dialog is open the session belongs to it; the loop just waits.
            if (Step == CameraStep.Closed)
            {
                try
                {
                    await _io.WaitAsync(ct);
                    try
                    {
                        if (_snapshot == null || _needsRefresh)
                        {
                            var snapshot = await ReadSnapshotAsync(ct);
                            if (Step == CameraStep.Closed)
                            {
                                if (snapshot == null) NoCamera();
                                else Apply(snapshot);
                            }
                        }
                        else
                        {
                            // Camera already known: only check that it is still there.
                            await using var session = await CameraService.OpenAsync(ct);
                            if (session == null) NoCamera();
                            delay = 4000;
                        }
                    }
                    finally
                    {
                        _io.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    AppLogger.LogError("Camera check failed", e);
                    NoCamera();
                    ConnectStatus = $"Could not talk to the camera: {e.Message}";
                }
            }

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task<FujiCameraSnapshot?> ReadSnapshotAsync(CancellationToken ct)
    {
        await using var session = await CameraService.OpenAsync(ct);
        return session == null ? null : await FujiSlotReader.ReadAllAsync(session, ct);
    }

    private void NoCamera()
    {
        if (TabState == CameraTabState.Searching && _snapshot == null && Slots.Count == 0) return;
        ClearCamera();
        TabState = CameraTabState.Searching;
        ConnectStatus = "Looking for a camera…";
    }

    private void Apply(FujiCameraSnapshot snapshot)
    {
        _needsRefresh = false;
        CameraName = $"FUJIFILM {snapshot.Model}";
        CameraDetail = $"USB · fw {snapshot.Firmware}";

        if (snapshot.Profile is { } p && p.Access == FujiBankAccess.Direct && !snapshot.RawConvMode)
        {
            ClearCamera();
            CameraName = $"FUJIFILM {snapshot.Model}";
            CameraDetail = "USB · wrong mode";
            ConnectStatus = $"{snapshot.Model} found, but its USB mode is not RAW CONV./BACKUP RESTORE. Change it in the camera menu.";
            TabState = CameraTabState.WrongMode;
            return;
        }

        var keep = SelectedSlot?.Number;
        _snapshot = snapshot;
        Slots.Clear();
        foreach (var slot in snapshot.Slots) Slots.Add(new CameraSlotItem(slot, SelectSlotCommand));

        string? reason = null;
        if (snapshot.Profile == null)
            reason = $"{snapshot.Model} can't be written over USB yet. Set the recipe by hand.";
        else if (!snapshot.DirectAccess)
            reason = $"{snapshot.Model} keeps its custom settings inside the full camera backup, which this app doesn't edit. Set the recipe by hand.";
        else if (snapshot.ReadError != null)
            reason = $"This model may store slots differently from the ones checked so far ({snapshot.ReadError}). Set the recipe by hand and copy the camera report so support can be added.";
        else if (!snapshot.CanWrite)
            reason = $"Writing to {snapshot.Model} isn't enabled yet: models are checked on real cameras one at a time. You can still read its slots. Set the recipe by hand.";

        ReadOnlyText = reason ?? "";
        RebuildManual();
        TabState = reason == null ? CameraTabState.Ready : CameraTabState.ReadOnly;

        // Keep the chosen slot across refreshes; otherwise suggest the first empty one (nothing gets replaced).
        var next = Slots.FirstOrDefault(s => s.Number == keep);
        if (next != null) SelectSlot(next);
        SuggestSlot();
        OnPropertyChanged(string.Empty);
        _settings.Library.CameraChanged();
    }

    // ── slots ────────────────────────────────────────────────────────────────────────────
    [RelayCommand]
    private void SelectSlot(CameraSlotItem? slot)
    {
        if (slot == null) return;
        foreach (var s in Slots) s.IsSelected = s == slot;
        SelectedSlot = slot;
    }

    /// <summary>Reads the chosen slot into the app: saved to My recipes and made the current recipe.</summary>
    [RelayCommand]
    private void ReadSlot()
    {
        if (SelectedSlot is not { IsOccupied: true } slot) return;
        var result = RecipeLibraryService.User.Add(slot.Info.Recipe, slot.Info.Name, RecipeOrigin.FromCamera);
        _settings.Library.SelectSaved(result.Entry);
        _toast(result.Outcome == AddOutcome.Added
            ? $"Read {slot.Label} into the app: “{result.Entry.Name}” is in My recipes"
            : $"Read {slot.Label}: “{result.Entry.Name}” was already in My recipes");
    }

    // ── diff and manual steps ────────────────────────────────────────────────────────────
    public ObservableCollection<RecipeChange> Changes { get; } = [];
    [ObservableProperty] private string _unchangedText = "";
    [ObservableProperty] private string _skippedText = "";
    [ObservableProperty] private string _replaceText = "";
    public bool HasSkippedText => SkippedText.Length > 0;
    public bool HasReplace => ReplaceText.Length > 0;
    partial void OnSkippedTextChanged(string value) => OnPropertyChanged(nameof(HasSkippedText));
    partial void OnReplaceTextChanged(string value) => OnPropertyChanged(nameof(HasReplace));

    private void BuildDiff()
    {
        Changes.Clear();
        UnchangedText = "";
        SkippedText = "";
        ReplaceText = "";
        if (CurrentRecipe is not { } recipe || SelectedSlot == null || _snapshot == null) return;

        var encoding = FujiSlotEncoder.Encode(recipe, _snapshot.Profile);
        var diff = RecipeDiff.Compute(SelectedSlot.Info.Raw, encoding, _snapshot.Model);
        foreach (var change in diff.Changes) Changes.Add(change);
        UnchangedText = diff.Unchanged > 0 ? $"{diff.Unchanged} unchanged" : "";
        if (encoding.Skipped.Count > 0)
            SkippedText = string.Join("\n", encoding.Skipped);
        if (SelectedSlot.IsOccupied)
            ReplaceText = $"{SelectedSlot.Label} holds “{SelectedSlot.Title}”. It will be replaced; the old settings are saved to a restore point first.";
    }

    private void RebuildManual()
    {
        _manualAll.Clear();
        if (CurrentRecipe is { } recipe)
            foreach (var field in RecipeFields.BuildAll(recipe))
                _manualAll.Add(new ManualStepItem(field.FullLabel, field.Value));
        RefreshManualView();
    }

    private void RefreshManualView()
    {
        ManualSteps.Clear();
        foreach (var step in _manualExpanded ? _manualAll : _manualAll.Take(ManualPreviewCount)) ManualSteps.Add(step);
        OnPropertyChanged(nameof(HasManualMore));
        OnPropertyChanged(nameof(ManualMoreText));
    }

    [RelayCommand]
    private void ShowAllManual()
    {
        _manualExpanded = true;
        RefreshManualView();
    }

    public string ManualAsText() => string.Join("\n", _manualAll.Select(s => $"{s.Label}: {s.Value}"));

    public string ErrorDetails => _errorDetails;

    /// <summary>Text for "Copy camera report": what this body reports, so an unverified model can be checked and enabled.</summary>
    public string ReportText => _snapshot == null ? "" : FujiCameraReport.Build(_snapshot, AppVersionInfo.DisplayVersion);
    public bool HasReport => _snapshot != null;

    /// <summary>"Collect camera dump": the report goes to a file (Pictures/PhSpectre/dumps) and to the clipboard, ready to send.</summary>
    public string? SaveReport()
    {
        if (_snapshot == null) return null;
        try
        {
            var folder = Path.Combine(Path.GetDirectoryName(RestorePointStore.Folder)!, "dumps");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{_snapshot.Model.Replace(' ', '-')}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, ReportText);
            return path;
        }
        catch (Exception e)
        {
            AppLogger.LogError("Could not save the camera dump", e);
            return null;
        }
    }

    // ── restore points ───────────────────────────────────────────────────────────────────
    private void RefreshRestorePoints()
    {
        RestorePoints.Clear();
        foreach (var entry in RestorePointStore.List())
            RestorePoints.Add(new RestorePointItem(entry, RestorePointCommand));
        OnPropertyChanged(nameof(HasRestorePoints));
        OnPropertyChanged(nameof(RestorePointsText));
    }

    [RelayCommand]
    private void OpenRestoreFolder()
    {
        try
        {
            Directory.CreateDirectory(RestorePointStore.Folder);
            Process.Start(new ProcessStartInfo(RestorePointStore.Folder) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            AppLogger.LogError("Could not open the restore folder", e);
        }
    }

    [RelayCommand]
    private async Task RestorePoint(RestorePointItem? item)
    {
        if (item == null) return;
        if (_snapshot == null || !IsTabReady)
        {
            _toast("Connect the camera first");
            return;
        }
        if (!string.Equals(item.Entry.Backup.Model, _snapshot.Model, StringComparison.OrdinalIgnoreCase))
        {
            _toast($"That restore point is from {item.Entry.Backup.Model}, not {_snapshot.Model}");
            return;
        }
        _restorePoint = item.Entry.Backup;
        await RestorePreviousAsync(fromDone: false, toTab: true);
    }

    // ── write ────────────────────────────────────────────────────────────────────────────
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ConfirmWriteCommand))]
    private bool _acknowledged;
    [ObservableProperty] private CameraStep _step = CameraStep.Closed;
    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private int _progressMax = 1;
    [ObservableProperty] private string _progressTitle = "";
    [ObservableProperty] private string _doneTitle = "";
    [ObservableProperty] private string _doneSlot = "";
    [ObservableProperty] private string _doneVerified = "";
    [ObservableProperty] private string _doneSkipped = "";
    [ObservableProperty] private string _doneRestorePoint = "";
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private string _errorTitle = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _canRestore;

    public ObservableCollection<WriteProgressItem> ProgressItems { get; } = [];

    public bool IsOpen => Step != CameraStep.Closed;
    public bool IsWarning => Step == CameraStep.Warning;
    public bool IsWriting => Step == CameraStep.Writing;
    public bool IsDone => Step == CameraStep.Done;
    public bool IsError => Step == CameraStep.Error;
    public bool HasChanges => Changes.Count > 0;
    public global::Avalonia.Layout.VerticalAlignment SheetAlignment =>
        _settings.IsMobile ? global::Avalonia.Layout.VerticalAlignment.Bottom : global::Avalonia.Layout.VerticalAlignment.Center;
    public string HeaderTitle => Step switch
    {
        CameraStep.Warning => $"Write to {SelectedSlot?.Label}",
        CameraStep.Writing => "Writing to camera",
        CameraStep.Done => "Done",
        CameraStep.Error => ErrorTitle,
        _ => "",
    };

    partial void OnStepChanged(CameraStep value) => OnPropertyChanged(string.Empty);
    partial void OnErrorTitleChanged(string value) => OnPropertyChanged(nameof(HeaderTitle));

    /// <summary>"Write “X” to C2": opens the warning (with what will change) in front of the tab.</summary>
    [RelayCommand]
    private void Continue()
    {
        if (!CanWrite) return;
        _hasError = false;
        BuildDiff();
        OnPropertyChanged(nameof(HasChanges));
        Acknowledged = false;
        Step = CameraStep.Warning;
    }

    private bool CanConfirm => Acknowledged;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmWrite()
    {
        if (CurrentRecipe is not { } recipe || SelectedSlot == null) return;
        var slot = SelectedSlot.Number;
        var label = SelectedSlot.Label;
        _writing = recipe;
        var encoding = FujiSlotEncoder.Encode(recipe, _snapshot?.Profile);

        ProgressItems.Clear();
        foreach (var write in encoding.Writes) ProgressItems.Add(new WriteProgressItem(write.Description));
        ProgressMax = Math.Max(1, encoding.Writes.Count * 2);
        ProgressValue = 0;
        ProgressTitle = $"Writing “{RecipeName}” to {label}";
        _restorePath = null;
        _restorePoint = null;
        CanUndo = false;
        Step = CameraStep.Writing;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            await _io.WaitAsync(CancellationToken.None);
            try
            {
                await using var session = await CameraService.OpenAsync(CancellationToken.None)
                    ?? throw new FujiWriteException("The camera is no longer connected.");

                // A dry run reads the slot, so the restore point exists on disk before the first property is sent.
                var plan = await FujiRecipeWriter.WriteAsync(session, slot, recipe, new FujiWriteOptions { DryRun = true }, ct);
                _restorePoint = FujiSlotBackup.From(plan);
                _restorePath = RestorePointStore.Save(_restorePoint, _settings.RestoreKeepLast);

                var progress = new UiProgress(OnProgress);
                var report = await FujiRecipeWriter.WriteAsync(session, slot, recipe,
                    new FujiWriteOptions { DryRun = false, AllowOverwrite = true, Progress = progress }, ct);
                ShowResult(report, label, encoding);
            }
            finally
            {
                _io.Release();
            }
        }
        catch (FujiWriteException e)
        {
            ShowError("Cannot write", e.Message, e.Message, canRestore: false);
        }
        catch (OperationCanceledException)
        {
            ShowError($"Stopped, nothing was changed in {label}", "The write was cancelled before it started.", "", canRestore: false);
        }
        catch (Exception e)
        {
            AppLogger.LogError("Camera write failed", e);
            var rollbackHint = _restorePath != null ? " The slot may be half-written; use “Restore previous” once the camera is back." : "";
            ShowError("Connection lost", $"{e.Message}{rollbackHint}", e.ToString(), canRestore: _restorePath != null);
        }
        finally
        {
            _needsRefresh = true;
            RefreshRestorePoints();
        }
    }

    // IProgress<T> posts to the captured context, which is not what we want: reports must arrive in order.
    private sealed class UiProgress(Action<FujiWriteProgress> report) : IProgress<FujiWriteProgress>
    {
        public void Report(FujiWriteProgress value) => report(value);
    }

    private void OnProgress(FujiWriteProgress p)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var index = p.Index - 1;
            if (index < 0 || index >= ProgressItems.Count) return;
            if (p.Phase == FujiWritePhase.Writing)
            {
                ProgressItems[index].State = WriteStepState.Active;
                ProgressValue = p.Index;
            }
            else
            {
                ProgressItems[index].State = WriteStepState.Done;
                ProgressValue = ProgressItems.Count + p.Index;
            }
        });
    }

    private void ShowResult(FujiWriteReport report, string label, SlotEncoding encoding)
    {
        if (report.Success)
        {
            _hasError = false;
            DoneTitle = $"Recipe written to {label}";
            DoneSlot = $"{label} · {RecipeName}";
            DoneVerified = $"{encoding.Writes.Count} of {encoding.Writes.Count}";
            DoneSkipped = encoding.Skipped.Count == 0 ? "None" : $"{encoding.Skipped.Count} setting(s)";
            DoneRestorePoint = _restorePath == null ? "" : Path.GetFileName(_restorePath);
            CanUndo = _restorePoint != null;
            Step = CameraStep.Done;
            return;
        }

        var details = string.Join("\n", report.Problems);
        if (report.Cancelled)
            ShowError(report.RolledBack ? $"Stopped, {label} is back as it was" : $"Stopped, but {label} could not be fully restored",
                report.RolledBack ? "The slot was restored from the restore point." : details, details, canRestore: !report.RolledBack);
        else
            ShowError(report.RolledBack ? $"Write failed, {label} is back as it was" : "Write failed and the slot could not be restored",
                details, details, canRestore: !report.RolledBack);
    }

    private void ShowError(string title, string text, string details, bool canRestore)
    {
        _hasError = true;
        ErrorTitle = title;
        ErrorText = text;
        _errorDetails = string.IsNullOrEmpty(details) ? text : details;
        CanRestore = canRestore && _restorePoint != null;
        Step = CameraStep.Error;
    }

    [RelayCommand]
    private void StopAndRestore() => _cts?.Cancel();

    // ── undo / restore ───────────────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task Undo() => await RestorePreviousAsync(fromDone: true, toTab: false);

    [RelayCommand]
    private async Task RestorePrevious() => await RestorePreviousAsync(fromDone: false, toTab: false);

    private async Task RestorePreviousAsync(bool fromDone, bool toTab)
    {
        if (_restorePoint == null) return;
        try
        {
            await _io.WaitAsync(CancellationToken.None);
            try
            {
                await using var session = await CameraService.OpenAsync(CancellationToken.None)
                    ?? throw new FujiWriteException("The camera is not connected. Connect it and try again.");
                var report = await FujiRecipeWriter.RestoreAsync(session, _restorePoint.Slot, _restorePoint.Properties);
                if (report.Success)
                {
                    _hasError = false;
                    _needsRefresh = true;
                    _toast($"C{_restorePoint.Slot} restored");
                    CanUndo = false;
                    CanRestore = false;
                    Close();
                }
                else if (toTab)
                    _toast("Restore incomplete: " + string.Join("; ", report.Problems));
                else
                    ShowError("Restore incomplete", string.Join("\n", report.Problems), string.Join("\n", report.Problems), canRestore: true);
            }
            finally
            {
                _io.Release();
            }
        }
        catch (Exception e)
        {
            AppLogger.LogError("Camera restore failed", e);
            if (toTab) _toast($"Restore failed: {e.Message}");
            else ShowError("Restore failed", e.Message, e.ToString(), canRestore: true);
        }
    }

    [RelayCommand]
    private void Retry()
    {
        Acknowledged = true;
        ConfirmWriteCommand.Execute(null);
    }

    // ── close ────────────────────────────────────────────────────────────────────────────
    [RelayCommand]
    private void Close()
    {
        // While writing the dialog must stay open: only "Stop and restore" ends it (cable pulls are handled as errors).
        if (Step == CameraStep.Writing) return;
        _hasError = false;
        _needsRefresh = true;
        Step = CameraStep.Closed;
    }
}
