using PhSpectre.Localization;
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

/// <summary>The write dialog: warning, progress, result.</summary>
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
    public string Title => Info.Name ?? Loc.T("Empty");
    public bool IsEmpty => Info.IsEmpty;
    public bool IsOccupied => !Info.IsEmpty;

    /// <summary>Key parameters under the name: "Classic Neg. · DR400 · Daylight".</summary>
    public string Detail
    {
        get
        {
            if (IsEmpty) return Loc.T("Not set");
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
    public string Subtitle => Entry.Backup.SlotName is { Length: > 0 } n ? Loc.F("Was: {0}", n) : Loc.T("Was empty");

    private static string FormatWhen(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var days = (DateTime.Now.Date - local.Date).Days;
        var time = local.ToString("HH:mm");
        return days switch
        {
            0 => Loc.F("today {0}", time),
            1 => Loc.F("yesterday {0}", time),
            _ => $"{local.ToString(Loc.T("MMM d"), Loc.Culture)} {time}",
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
/// The Camera tab and the write dialog. While enabled it watches the USB port in the background, which also drives
/// the indicator in the top bar.
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

    [ObservableProperty] private CameraTabState _tabState = CameraTabState.Off;
    [ObservableProperty] private string _connectStatus = Loc.T("Looking for a camera…");
    // Details of the last failed camera check, for "Copy error".
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasConnectError))] private string _connectErrorDetails = "";
    public bool HasConnectError => ConnectErrorDetails.Length > 0;
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
    public string ManualMoreText => Loc.F("+ {0} more", _manualAll.Count - ManualPreviewCount);
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

    // The setup dialog shown while the feature is off.
    [ObservableProperty] private bool _setupOpen;
    public void OpenSetup() => SetupOpen = true;
    public void CloseSetup() => SetupOpen = false;
    [RelayCommand]
    private void CloseSetupDialog() => SetupOpen = false;

    /// <summary>Under the slot list: what the chosen slot holds and that it is saved first.</summary>
    public string SlotNote => SelectedSlot is { IsOccupied: true } s ? Loc.F("{0} “{1}” will be replaced. It is saved first.", s.Label, s.Title) : "";
    public bool HasSlotNote => SlotNote.Length > 0;
    public bool HasRestorePoints => RestorePoints.Count > 0;
    public string RestorePointsText => Loc.F("Restore points · {0}", RestorePoints.Count);
    public string RestoreFolder => RestorePointStore.DisplayFolder;
    public bool CanOpenRestoreFolder => !OperatingSystem.IsAndroid();

    /// <summary>Badge on the camera card: green when ready, amber when it can only be read.</summary>
    public string CameraBadge => TabState switch
    {
        CameraTabState.Ready => Loc.T("Ready"),
        CameraTabState.ReadOnly => Loc.T("Can't write"),
        CameraTabState.WrongMode => Loc.T("Wrong USB mode"),
        _ => Loc.T("Searching"),
    };
    public bool BadgeIsOk => TabState == CameraTabState.Ready;
    public bool BadgeIsWarn => TabState is CameraTabState.ReadOnly or CameraTabState.WrongMode;
    public bool BadgeIsIdle => TabState is CameraTabState.Searching or CameraTabState.Off;
    public string CardTitle => TabState is CameraTabState.Searching or CameraTabState.Off ? Loc.T("No camera") : CameraName;
    public string CardDetail => TabState is CameraTabState.Searching or CameraTabState.Off ? Loc.T("Not connected") : CameraDetail;

    partial void OnTabStateChanged(CameraTabState value)
    {
        OnPropertyChanged(string.Empty);
        _settings.Library.CameraChanged();
    }
    partial void OnReadOnlyTextChanged(string value) => OnPropertyChanged(nameof(HasReadOnlyText));

    public bool IndicatorVisible => _enabled && CameraService.IsSupported;
    public bool IndicatorOk => IndicatorVisible && Step == CameraStep.Closed && !_hasError && IsTabConnected;
    public bool IndicatorIdle => IndicatorVisible && Step == CameraStep.Closed && !_hasError && !IsTabConnected;
    public bool IndicatorBusy => IndicatorVisible && Step == CameraStep.Writing;
    public bool IndicatorWarn => IndicatorVisible && _hasError && Step != CameraStep.Writing;
    public string IndicatorText =>
        Step == CameraStep.Writing ? Loc.F("Writing to {0}…", SelectedSlot?.Label)
        : _hasError ? Loc.T("Check camera")
        : IsTabConnected ? ShortModel
        : Loc.T("No camera");
    private string ShortModel => _snapshot?.Model ?? CameraName.Replace("FUJIFILM ", "");

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
            return $"{from} · {Loc.N(count, "{0} setting", "{0} settings")}";
        }
    }
    public int SkippedCount => CurrentRecipe is { } r ? FujiSlotEncoder.Encode(r, _snapshot?.Profile).Skipped.Count : 0;
    public bool HasSkipped => SkippedCount > 0;
    public string SkippedBadge => Loc.F("{0} SKIPPED", SkippedCount);

    // With a recipe the first empty slot is suggested, so nothing named is replaced by accident.
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


    public bool CanWrite => IsTabReady && SelectedSlot != null && HasRecipe;
    public bool CanRead => IsTabConnected && SelectedSlot is { IsOccupied: true };
    public string WriteButtonText =>
        !HasRecipe ? Loc.T("Choose a recipe to write")
        : SelectedSlot == null ? Loc.T("Pick a slot")
        : IsSingleSlot ? Loc.F("Replace {0}", SelectedSlot.Label)
        : Loc.F("Write “{0}” to {1}", RecipeName, SelectedSlot.Label);
    public string ReadButtonText => SelectedSlot is { IsOccupied: true } s ? Loc.F("Read {0} into the app", s.Label) : Loc.T("Read a slot into the app");
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

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _monitor?.Cancel();
        _monitor = null;
        if (enabled)
        {
            _snapshot = null;
            _hasError = false;
            ConnectErrorDetails = "";
            TabState = CameraTabState.Searching;
            ConnectStatus = Loc.T("Looking for a camera…");
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

    // Watching the USB port
    private async Task MonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delay = 1500;
            // The open dialog owns the session.
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
                            // Known camera: only check that it is still there.
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
                    ConnectStatus = Loc.F("Could not talk to the camera: {0}", e.Message);
                    ConnectErrorDetails = $"{ConnectStatus}\n{e}";
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
        ConnectErrorDetails = "";
        if (TabState == CameraTabState.Searching && _snapshot == null && Slots.Count == 0) return;
        ClearCamera();
        TabState = CameraTabState.Searching;
        ConnectStatus = Loc.T("Looking for a camera…");
    }

    private void Apply(FujiCameraSnapshot snapshot)
    {
        ConnectErrorDetails = "";
        _needsRefresh = false;
        CameraName = $"FUJIFILM {snapshot.Model}";
        CameraDetail = $"USB · fw {snapshot.Firmware}";

        if (snapshot.Profile is { } p && p.Access == FujiBankAccess.Direct && !snapshot.RawConvMode)
        {
            ClearCamera();
            CameraName = $"FUJIFILM {snapshot.Model}";
            CameraDetail = Loc.T("USB · wrong mode");
            ConnectStatus = Loc.F("{0} found, but its USB mode is not RAW CONV./BACKUP RESTORE. Change it in the camera menu.", snapshot.Model);
            TabState = CameraTabState.WrongMode;
            return;
        }

        var keep = SelectedSlot?.Number;
        _snapshot = snapshot;
        Slots.Clear();
        foreach (var slot in snapshot.Slots) Slots.Add(new CameraSlotItem(slot, SelectSlotCommand));

        string? reason = null;
        if (snapshot.Profile == null)
            reason = Loc.F("{0} can't be written over USB yet. Set the recipe by hand.", snapshot.Model);
        else if (!snapshot.DirectAccess)
            reason = Loc.F("{0} keeps its custom settings inside the full camera backup, which this app doesn't edit. Set the recipe by hand.", snapshot.Model);
        else if (snapshot.ReadError != null)
            reason = Loc.F("This model may store slots differently from the ones checked so far ({0}). Set the recipe by hand and copy the camera report so support can be added.", snapshot.ReadError);
        else if (!snapshot.CanWrite)
            reason = Loc.F("Writing to {0} isn't enabled yet: models are checked on real cameras one at a time. You can still read its slots. Set the recipe by hand.", snapshot.Model);

        ReadOnlyText = reason ?? "";
        RebuildManual();
        TabState = reason == null ? CameraTabState.Ready : CameraTabState.ReadOnly;

        // Keep the chosen slot across refreshes.
        var next = Slots.FirstOrDefault(s => s.Number == keep);
        if (next != null) SelectSlot(next);
        SuggestSlot();
        OnPropertyChanged(string.Empty);
        _settings.Library.CameraChanged();
    }

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
            ? Loc.F("Read {0} into the app: “{1}” is in My recipes", slot.Label, result.Entry.Name)
            : Loc.F("Read {0}: “{1}” was already in My recipes", slot.Label, result.Entry.Name));
    }

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
        foreach (var change in diff.Changes) Changes.Add(change with { Field = Loc.T(change.Field) });
        UnchangedText = diff.Unchanged > 0 ? Loc.F("{0} unchanged", diff.Unchanged) : "";
        if (encoding.Skipped.Count > 0)
            SkippedText = string.Join("\n", encoding.Skipped);
        if (SelectedSlot.IsOccupied)
            ReplaceText = Loc.F("{0} holds “{1}”. It will be replaced; the old settings are saved to a restore point first.", SelectedSlot.Label, SelectedSlot.Title);
    }

    private void RebuildManual()
    {
        _manualAll.Clear();
        if (CurrentRecipe is { } recipe)
            foreach (var field in RecipeFields.BuildAll(recipe))
                _manualAll.Add(new ManualStepItem(Loc.T(field.FullLabel), field.Value));
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
            _toast(Loc.T("Connect the camera first"));
            return;
        }
        if (!string.Equals(item.Entry.Backup.Model, _snapshot.Model, StringComparison.OrdinalIgnoreCase))
        {
            _toast(Loc.F("That restore point is from {0}, not {1}", item.Entry.Backup.Model, _snapshot.Model));
            return;
        }
        _restorePoint = item.Entry.Backup;
        await RestorePreviousAsync(fromDone: false, toTab: true);
    }

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
        CameraStep.Warning => Loc.F("Write to {0}", SelectedSlot?.Label),
        CameraStep.Writing => Loc.T("Writing to camera"),
        CameraStep.Done => Loc.T("Done"),
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
        ProgressTitle = Loc.F("Writing “{0}” to {1}", RecipeName, label);
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
                    ?? throw new FujiWriteException(Loc.T("The camera is no longer connected."));

                // The dry run saves the restore point before the first property is sent.
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
            ShowError(Loc.T("Cannot write"), e.Message, e.Message, canRestore: false);
        }
        catch (OperationCanceledException)
        {
            ShowError(Loc.F("Stopped, nothing was changed in {0}", label), Loc.T("The write was cancelled before it started."), "", canRestore: false);
        }
        catch (Exception e)
        {
            AppLogger.LogError("Camera write failed", e);
            var rollbackHint = _restorePath != null ? " " + Loc.T("The slot may be half-written; use “Restore previous” once the camera is back.") : "";
            ShowError(Loc.T("Connection lost"), $"{e.Message}{rollbackHint}", e.ToString(), canRestore: _restorePath != null);
        }
        finally
        {
            _needsRefresh = true;
            RefreshRestorePoints();
        }
    }

    // Not IProgress<T>: reports must arrive in order.
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
            DoneTitle = Loc.F("Recipe written to {0}", label);
            DoneSlot = $"{label} · {RecipeName}";
            DoneVerified = Loc.F("{0} of {1}", encoding.Writes.Count, encoding.Writes.Count);
            DoneSkipped = encoding.Skipped.Count == 0 ? Loc.T("None") : Loc.N(encoding.Skipped.Count, "{0} setting", "{0} settings");
            DoneRestorePoint = _restorePath == null ? "" : Path.GetFileName(_restorePath);
            CanUndo = _restorePoint != null;
            Step = CameraStep.Done;
            return;
        }

        var details = string.Join("\n", report.Problems);
        if (report.Cancelled)
            ShowError(report.RolledBack ? Loc.F("Stopped, {0} is back as it was", label) : Loc.F("Stopped, but {0} could not be fully restored", label),
                report.RolledBack ? Loc.T("The slot was restored from the restore point.") : details, details, canRestore: !report.RolledBack);
        else
            ShowError(report.RolledBack ? Loc.F("Write failed, {0} is back as it was", label) : Loc.T("Write failed and the slot could not be restored"),
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
                    ?? throw new FujiWriteException(Loc.T("The camera is not connected. Connect it and try again."));
                var report = await FujiRecipeWriter.RestoreAsync(session, _restorePoint.Slot, _restorePoint.Properties);
                if (report.Success)
                {
                    _hasError = false;
                    _needsRefresh = true;
                    _toast(Loc.F("C{0} restored", _restorePoint.Slot));
                    CanUndo = false;
                    CanRestore = false;
                    Close();
                }
                else if (toTab)
                    _toast(Loc.T("Restore incomplete") + ": " + string.Join("; ", report.Problems));
                else
                    ShowError(Loc.T("Restore incomplete"), string.Join("\n", report.Problems), string.Join("\n", report.Problems), canRestore: true);
            }
            finally
            {
                _io.Release();
            }
        }
        catch (Exception e)
        {
            AppLogger.LogError("Camera restore failed", e);
            if (toTab) _toast(Loc.F("Restore failed: {0}", e.Message));
            else ShowError(Loc.T("Restore failed"), e.Message, e.ToString(), canRestore: true);
        }
    }

    [RelayCommand]
    private void Retry()
    {
        Acknowledged = true;
        ConfirmWriteCommand.Execute(null);
    }

    [RelayCommand]
    private void Close()
    {
        // While writing, only "Stop and restore" closes the dialog.
        if (Step == CameraStep.Writing) return;
        _hasError = false;
        _needsRefresh = true;
        Step = CameraStep.Closed;
    }
}
