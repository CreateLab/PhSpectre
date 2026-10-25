using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhSpectre.Avalonia.Models;
using PhSpectre.Avalonia.Services;
using PhSpectre.Camera;
using PhSpectre.Models;
using PhSpectre.Recipes;

namespace PhSpectre.Avalonia.ViewModels;

public enum RecipeSourceKind { Photo, Saved, Popular, Custom }

/// <summary>One line in the recipe list: a photo (with the recipe read from it), a saved recipe or a popular one.</summary>
public sealed partial class RecipeRow(
    RecipeSourceKind kind, string title, string subtitle, FilmRecipe? recipe, RecipeLibraryViewModel owner,
    string? author = null, string? sourceUrl = null, RecipeOrigin origin = RecipeOrigin.Bundled, string? entryId = null,
    FileEntry? photo = null) : ObservableObject
{
    public RecipeSourceKind Kind { get; } = kind;
    public string Title { get; } = title;
    public FileEntry? Photo { get; } = photo;
    public string? Author { get; } = author;
    public string? SourceUrl { get; } = sourceUrl;
    public RecipeOrigin Origin { get; } = origin;
    public string? EntryId { get; } = entryId;
    public ICommand SelectCommand => owner.SelectCommand;
    public ICommand StarCommand => owner.ToggleStarCommand;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasRecipe), nameof(TitleOpacity), nameof(DisplayTitle))]
    private FilmRecipe? _recipe = recipe;
    [ObservableProperty] private string _subtitle = subtitle;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayTitle))] private string? _customName;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isStarred;

    public bool HasRecipe => Recipe != null;
    public double TitleOpacity => HasRecipe ? 1.0 : 0.6;
    public double SubtitleOpacity => HasRecipe ? 0.65 : 0.45;

    /// <summary>A photo row is titled by its file name; once chosen, the recipe's own name is what gets written.</summary>
    public string DisplayTitle => !string.IsNullOrWhiteSpace(CustomName) ? CustomName.Trim()
        : Kind is RecipeSourceKind.Photo or RecipeSourceKind.Custom && Recipe != null ? RecipeLibraryViewModel.TitleFor(Recipe) : Title;
}

/// <summary>An entry of the film simulation drop-down: the name (null = all) and its label with a count.</summary>
public sealed record FilmOption(string? Name, string Label);

public sealed class RecipeGroup(string header, IReadOnlyList<RecipeRow> rows)
{
    public string Header { get; } = header;
    public bool HasHeader => Header.Length > 0;
    public IReadOnlyList<RecipeRow> Rows { get; } = rows;
}

public sealed partial class RecipeChip(string label, string filter, RecipeLibraryViewModel owner) : ObservableObject
{
    public string Label { get; } = label;
    public string Filter { get; } = filter;
    public ICommand Command => owner.SetFilterCommand;
    [ObservableProperty] private bool _isActive;
}

/// <summary>One of the parameter tiles in the middle column; muted when the connected camera does not store it in a slot.</summary>
public sealed record ParamTile(string Label, string Value, bool IsMuted)
{
    public double Opacity => IsMuted ? 0.45 : 1.0;
}

/// <summary>
/// The left and middle columns of the Camera mode: where the recipe comes from (this photo, saved, popular), the list
/// with search, chips and groups, and the selected recipe shown in full. Selecting a row is the only step to choose a
/// recipe; the star keeps it in Saved.
/// </summary>
public sealed partial class RecipeLibraryViewModel : ObservableObject
{

    private readonly SettingsViewModel _settings;

    public RecipeLibraryViewModel(SettingsViewModel settings)
    {
        _settings = settings;
    }

    [ObservableProperty] private int _sourceIndex;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _filter = "All";
    [ObservableProperty] private RecipeRow? _selected;

    // Phone: the list is step 1, the slots step 2; a tapped row opens as a sheet first.
    [ObservableProperty] private bool _isNarrow;
    [ObservableProperty] private int _phoneStep;
    [ObservableProperty] private bool _sheetOpen;

    public ObservableCollection<RecipeGroup> Groups { get; } = [];
    public ObservableCollection<RecipeChip> Chips { get; } = [];
    public ObservableCollection<ParamTile> Tiles { get; } = [];

    public bool IsPhotoSource => SourceIndex == 0;
    public bool IsSavedSource => SourceIndex == 1;
    public bool IsPopularSource => SourceIndex == 2;
    public bool ShowSearch => true;
    public bool ShowPhotoFilter => IsPhotoSource && HasPhotos;
    public bool ShowChips => !IsPhotoSource;
    public string NoSelectionText => IsPhotoSource && Selected is { Kind: RecipeSourceKind.Photo, HasRecipe: false }
        ? "This photo has no recipe. Pick one from Saved or Popular, or enter it by hand."
        : "Pick a recipe on the left";
    public bool SelectedIsPhoto => Selected is { Kind: RecipeSourceKind.Photo, HasRecipe: true };
    public FileEntry? SelectedPhoto => Selected?.Photo;
    public string SelectedFileName => Selected?.Photo?.FileName ?? "";
    public string SavedTabText => $"Saved · {RecipeLibraryService.User.Entries.Count}";
    public bool ShowSavedHelp => IsSavedSource && RecipeLibraryService.User.Entries.Count == 0;
    public bool ShowNoMatches => !IsPhotoSource && !ShowSavedHelp && Groups.Count == 0;
    public bool ShowCredit => IsPopularSource;

    // ── selection ────────────────────────────────────────────────────────────────────────
    public bool HasSelection => Selected?.Recipe != null;
    public bool NoSelection => !HasSelection;
    public FilmRecipe? SelectedRecipe => Selected?.Recipe;
    public string SelectedTitle => Selected?.DisplayTitle ?? "";
    public string SelectedSubtitle
    {
        get
        {
            if (Selected is not { } row) return "";
            var film = row.Recipe?.FilmSimulation ?? "";
            return row.Author is { Length: > 0 } a ? $"{film} · by {a}" : film;
        }
    }
    public bool HasSource => !string.IsNullOrEmpty(Selected?.SourceUrl);
    public bool SelectedStarred => Selected?.IsStarred ?? false;
    public string SaveButtonText => SelectedStarred ? "Saved" : "Save";
    public bool HasMutedTiles => Tiles.Any(t => t.IsMuted);
    public string MutedText
    {
        get
        {
            var names = Tiles.Where(t => t.IsMuted).Select(t => t.Label).ToList();
            return names.Count switch
            {
                0 => "",
                1 => $"{names[0]} is not stored in slots on this camera. It will be skipped.",
                _ => $"{string.Join(", ", names)} are not stored in slots on this camera. They will be skipped.",
            };
        }
    }
    public string ChooseButtonText => HasSelection ? "Use this recipe" : "Choose a recipe";
    public bool ShowRecipeStep => !IsNarrow || PhoneStep == 0;
    // ── what the screen shows: connecting, browsing without a camera, or everything ──────
    [ObservableProperty] private bool _browsing;

    private bool Connected => _settings.Camera.IsTabConnected;
    public bool ShowConnectScreen => !Connected && !Browsing;
    public bool ShowBrowser => !ShowConnectScreen;
    public bool ShowRightColumn => Connected;
    public bool ShowBrowseBar => Browsing && !Connected;
    public bool ShowUseButton => Connected;
    public int CenterSpan => Connected && !IsNarrow ? 1 : 2;
    public bool ShowAddMenu => IsSavedSource && !ShowSavedHelp;

    partial void OnBrowsingChanged(bool value) => RaiseConnection();

    [RelayCommand]
    private void BrowseRecipes() => Browsing = true;

    [RelayCommand]
    private void ConnectCamera() => Browsing = false;

    private void RaiseConnection()
    {
        foreach (var name in new[] { nameof(ShowConnectScreen), nameof(ShowBrowser), nameof(ShowRightColumn), nameof(ShowBrowseBar),
                     nameof(ShowUseButton), nameof(CenterSpan), nameof(ShowSlotStep), nameof(ShowChooseFooter), nameof(ShowSheet) })
            OnPropertyChanged(name);
    }

    public bool ShowSlotStep => Connected && (!IsNarrow || PhoneStep == 1);
    public bool ShowChooseFooter => IsNarrow && PhoneStep == 0 && Connected;
    public bool ShowSheet => IsNarrow && SheetOpen && HasSelection;
    public bool ShowCenterColumn => !IsNarrow;

    partial void OnSourceIndexChanged(int value) => Refresh();
    partial void OnSearchChanged(string value) => Refresh();
    partial void OnFilterChanged(string value) => Refresh();

    partial void OnSelectedChanged(RecipeRow? oldValue, RecipeRow? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
        RaiseSelection();
        _settings.Camera.RecipeChanged();
    }

    partial void OnIsNarrowChanged(bool value) => RaisePhone();
    partial void OnPhoneStepChanged(int value) => RaisePhone();
    partial void OnSheetOpenChanged(bool value) => RaisePhone();

    private void RaisePhone()
    {
        OnPropertyChanged(nameof(ShowRecipeStep));
        OnPropertyChanged(nameof(ShowSlotStep));
        OnPropertyChanged(nameof(ShowChooseFooter));
        OnPropertyChanged(nameof(ShowSheet));
        OnPropertyChanged(nameof(ShowCenterColumn));
        OnPropertyChanged(nameof(CenterSpan));
    }

    private void RaiseSelection()
    {
        BuildTiles();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(NoSelection));
        OnPropertyChanged(nameof(SelectedRecipe));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(IsNameEditable));
        OnPropertyChanged(nameof(NoSelectionText));
        OnPropertyChanged(nameof(SelectedIsPhoto));
        OnPropertyChanged(nameof(SelectedPhoto));
        OnPropertyChanged(nameof(SelectedFileName));
        OnPropertyChanged(nameof(SelectedSubtitle));
        OnPropertyChanged(nameof(HasSource));
        OnPropertyChanged(nameof(SelectedStarred));
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(ChooseButtonText));
        OnPropertyChanged(nameof(ShowSheet));
    }

    /// <summary>The connected camera's slot format changed: the muted tiles follow.</summary>
    public void CameraChanged()
    {
        if (Connected) Browsing = false;
        RaiseConnection();
        RaiseSelection();
    }

    private void BuildTiles()
    {
        Tiles.Clear();
        if (Selected?.Recipe is { } recipe)
        {
            var profile = _settings.Camera.Profile;
            var baseline = profile == null ? 0 : FujiSlotEncoder.Encode(recipe, profile).Skipped.Count;
            foreach (var f in RecipeFields.BuildAll(recipe))
            {
                var muted = profile != null && FujiSlotEncoder.Encode(Without(recipe, f.Key), profile).Skipped.Count < baseline;
                Tiles.Add(new ParamTile(f.FullLabel, f.Value, muted));
            }
        }
        OnPropertyChanged(nameof(HasMutedTiles));
        OnPropertyChanged(nameof(MutedText));
    }

    private static FilmRecipe Without(FilmRecipe r, string key) => key switch
    {
        RecipeFields.WhiteBalance => r with { WhiteBalance = null },
        RecipeFields.WhiteBalanceShift => r with { WhiteBalanceShift = null },
        RecipeFields.DynamicRange => r with { DynamicRange = null },
        RecipeFields.Highlight => r with { HighlightTone = null },
        RecipeFields.Shadow => r with { ShadowTone = null },
        RecipeFields.Color => r with { Color = null },
        RecipeFields.Sharpness => r with { Sharpness = null },
        RecipeFields.NoiseReduction => r with { NoiseReduction = null },
        RecipeFields.Clarity => r with { Clarity = null },
        RecipeFields.Grain => r with { GrainEffect = null, GrainSize = null },
        RecipeFields.ColorChrome => r with { ColorChromeEffect = null },
        RecipeFields.ColorChromeBlue => r with { ColorChromeFxBlue = null },
        RecipeFields.BwAdjustment => r with { BwAdjustment = null },
        _ => r,
    };

    // ── photos: the same current photo as in Card / Recipe / Collage ─────────────────────
    private static readonly Dictionary<string, FilmRecipe?> RecipeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RecipeRow> _photoRowsByPath = new(StringComparer.OrdinalIgnoreCase);
    private System.Collections.Specialized.INotifyCollectionChanged? _watchedPhotos;
    private int _loadVersion;
    private bool _syncingPhoto;

    public ObservableCollection<RecipeRow> PhotoRows { get; } = [];
    [ObservableProperty] private bool _onlyWithRecipe;
    [ObservableProperty] private RecipeRow? _selectedPhotoRow;

    /// <summary>The list should bring the current photo into view.</summary>
    public event Action? ScrollToCurrentRequested;

    public bool HasPhotos => (_settings.PhotosProvider?.Invoke().Count ?? 0) > 0;
    public bool ShowOpenFolder => IsPhotoSource && !HasPhotos;
    public bool ShowPhotoList => IsPhotoSource && HasPhotos;
    public bool ShowGroupedList => !IsPhotoSource;
    public bool ShowNoPhotoMatches => ShowPhotoList && PhotoRows.Count == 0;

    partial void OnOnlyWithRecipeChanged(bool value) => RebuildPhotos();

    partial void OnSelectedPhotoRowChanged(RecipeRow? value)
    {
        if (_syncingPhoto || value?.Photo == null) return;
        Selected = value;
        var current = _settings.CurrentPhotoProvider?.Invoke();
        if (!ReferenceEquals(current, value.Photo)) _settings.SelectPhotoAction?.Invoke(value.Photo);
        if (IsNarrow && value.HasRecipe) SheetOpen = true;
    }

    [RelayCommand]
    private void OpenFolder() => _settings.OpenPhotosCommand?.Execute(null);

    [RelayCommand]
    private void ChangePhoto() => SourceIndex = 0;

    [RelayCommand]
    private void ToggleOnlyWithRecipe() => OnlyWithRecipe = !OnlyWithRecipe;

    // ── entering the mode / the photo changed ────────────────────────────────────────────
    /// <summary>Called when the Camera mode opens: the current photo's recipe is chosen without any click.</summary>
    public void Enter()
    {
        Browsing = false;
        if (Selected == null || Selected.Kind == RecipeSourceKind.Photo)
        {
            SourceIndex = 0;
            PhoneStep = 0;
        }
        Refresh();
    }

    /// <summary>"Send to camera" in Content → Recipe: go back to this photo's recipe, whatever was chosen before.</summary>
    public void UsePhoto()
    {
        if (_settings.CurrentPhotoProvider?.Invoke() == null) return;
        Selected = null;
        SourceIndex = 0;
        PhoneStep = 0;
        if (_settings.IsCameraMode) Enter();
    }

    /// <summary>A different photo is on screen (or its recipe was edited).</summary>
    public void PhotoRecipeChanged()
    {
        if (!_settings.IsCameraMode) return;
        var current = _settings.CurrentPhotoProvider?.Invoke();
        if (current != null && _photoRowsByPath.TryGetValue(current.FullPath, out var row))
        {
            row.Recipe = _settings.DetectedRecipe;
            row.Subtitle = row.Recipe == null ? "No recipe" : Brief(row.Recipe);
        }
        if (IsPhotoSource || Selected?.Kind == RecipeSourceKind.Photo) SyncCurrentPhotoRow();
        RaiseSelection();
    }

    private void SyncCurrentPhotoRow()
    {
        var current = _settings.CurrentPhotoProvider?.Invoke();
        var row = current != null && _photoRowsByPath.TryGetValue(current.FullPath, out var r) ? r : null;
        if (row != null && _settings.DetectedRecipe is { } detected)
        {
            row.Recipe = detected;
            row.Subtitle = Brief(detected);
        }
        _syncingPhoto = true;
        try
        {
            SelectedPhotoRow = row != null && PhotoRows.Contains(row) ? row : null;
            if (row != null && (IsPhotoSource || Selected == null || Selected.Kind == RecipeSourceKind.Photo)) Selected = row;
            else if (row == null && Selected?.Kind == RecipeSourceKind.Photo) Selected = null;
        }
        finally { _syncingPhoto = false; }
        ScrollToCurrentRequested?.Invoke();
    }

    private void RebuildPhotos()
    {
        var photos = _settings.PhotosProvider?.Invoke() ?? [];
        if (photos is System.Collections.Specialized.INotifyCollectionChanged watch && !ReferenceEquals(watch, _watchedPhotos))
        {
            if (_watchedPhotos != null) _watchedPhotos.CollectionChanged -= OnPhotosChanged;
            _watchedPhotos = watch;
            watch.CollectionChanged += OnPhotosChanged;
        }

        var q = Search.Trim();
        PhotoRows.Clear();
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var photo in photos)
        {
            live.Add(photo.FullPath);
            if (!_photoRowsByPath.TryGetValue(photo.FullPath, out var row))
            {
                var known = RecipeCache.TryGetValue(photo.FullPath, out var cached);
                row = new RecipeRow(RecipeSourceKind.Photo, photo.FileName, !known ? "…" : cached == null ? "No recipe" : Brief(cached),
                    known ? cached : null, this, origin: RecipeOrigin.FromImage, photo: photo);
                _photoRowsByPath[photo.FullPath] = row;
            }
            if (q.Length > 0 && !row.Title.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            // A photo whose recipe is still being read stays in the list; it drops out once known to have none.
            if (OnlyWithRecipe && row.Subtitle == "No recipe") continue;
            PhotoRows.Add(row);
        }
        foreach (var gone in _photoRowsByPath.Keys.Where(k => !live.Contains(k)).ToList()) _photoRowsByPath.Remove(gone);

        OnPropertyChanged(nameof(HasPhotos));
        OnPropertyChanged(nameof(ShowOpenFolder));
        OnPropertyChanged(nameof(ShowPhotoList));
        OnPropertyChanged(nameof(ShowNoPhotoMatches));
        SyncCurrentPhotoRow();
        _ = LoadRecipesAsync(++_loadVersion);
    }

    private void OnPhotosChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_settings.IsCameraMode && IsPhotoSource) global::Avalonia.Threading.Dispatcher.UIThread.Post(RebuildPhotos);
    }

    // Recipes are read lazily, a few at a time, and cached for the session.
    private async Task LoadRecipesAsync(int version)
    {
        var pending = _photoRowsByPath.Values.Where(r => r.Photo != null && !RecipeCache.ContainsKey(r.Photo.FullPath)).ToList();
        for (var i = 0; i < pending.Count; i += 6)
        {
            var batch = pending.Skip(i).Take(6).ToList();
            var read = await Task.Run(() => batch.Select(r =>
            {
                try { return (Row: r, Recipe: RecipeReader.Read(r.Photo!.FullPath)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (Row: r, Recipe: (FilmRecipe?)null); }
            }).ToList());
            if (version != _loadVersion) return;
            foreach (var (row, recipe) in read)
            {
                RecipeCache[row.Photo!.FullPath] = recipe;
                row.Recipe = recipe;
                row.Subtitle = recipe == null ? "No recipe" : Brief(recipe);
            }
        }
        if (OnlyWithRecipe && pending.Count > 0) RebuildPhotos();
    }

    // ── naming, editing by hand, clipboard ───────────────────────────────────────────────
    private static readonly System.Text.Json.JsonSerializerOptions ClipboardJson = new() { WriteIndented = true };

    public bool IsNameEditable => Selected is { } s && s.Kind != RecipeSourceKind.Popular && s.Recipe != null;

    /// <summary>The name shown above the recipe; editable for photo, saved and hand-made recipes (it becomes the slot name).</summary>
    public string SelectedName
    {
        get => Selected?.DisplayTitle ?? "";
        set
        {
            if (Selected is not { } row || row.Kind == RecipeSourceKind.Popular) return;
            var name = value?.Trim() ?? "";
            if (name == row.DisplayTitle) return;
            if (row.Kind == RecipeSourceKind.Saved && row.EntryId != null)
            {
                if (name.Length == 0) { OnPropertyChanged(); return; }
                RecipeLibraryService.User.Rename(row.EntryId, name);
                var id = row.EntryId;
                Refresh();
                Selected = Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.EntryId == id) ?? row;
                return;
            }
            row.CustomName = name.Length == 0 ? null : name;
            RaiseSelection();
        }
    }

    /// <summary>A recipe that is not in any list yet (typed in, edited or pasted): it becomes the chosen one.</summary>
    public void UseCustom(FilmRecipe recipe)
    {
        var row = new RecipeRow(RecipeSourceKind.Custom, TitleFor(recipe), Brief(recipe), recipe with { Source = RecipeSource.Manual }, this,
            origin: RecipeOrigin.FromImage);
        Selected = row;
        if (IsNarrow) SheetOpen = true;
    }

    [RelayCommand]
    private void EditSelected()
    {
        if (Selected?.Recipe is { } r) _settings.OpenRecipeEditorFor(r with { RecipeName = Selected.DisplayTitle }, true);
    }

    [RelayCommand]
    private void NewByHand() => _settings.OpenRecipeEditorFor(null, true);

    /// <summary>The chosen recipe as text for the clipboard (JSON, so it pastes back exactly).</summary>
    public string? CopyText() =>
        Selected?.Recipe is { } r ? System.Text.Json.JsonSerializer.Serialize(r with { RecipeName = Selected.DisplayTitle }, ClipboardJson) : null;

    public void CopiedNotice() => _settings.SetRecipeStatus($"Copied “{SelectedName}”");

    public bool PasteText(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                var r = System.Text.Json.JsonSerializer.Deserialize<FilmRecipe>(text, ClipboardJson);
                if (r != null && (r.FilmSimulation != null || r.WhiteBalance != null || r.RecipeName != null))
                {
                    UseCustom(r with { CameraMake = r.CameraMake ?? "", CameraModel = r.CameraModel ?? "" });
                    _settings.SetRecipeStatus($"Pasted “{SelectedName}”");
                    return true;
                }
            }
            catch (System.Text.Json.JsonException) { }
        }
        _settings.SetRecipeStatus("The clipboard has no recipe copied from PhSpectre");
        return false;
    }

    // ── building the list ────────────────────────────────────────────────────────────────
    public void Refresh()
    {
        Groups.Clear();
        var q = Search.Trim();
        var candidates = new List<RecipeRow>();

        if (SourceIndex == 0)
        {
            Chips.Clear();
            RebuildPhotos();
            RaiseSourceProps();
            return;
        }

        switch (SourceIndex)
        {
            case 1:
                candidates.AddRange(RecipeLibraryService.User.Entries.Reverse().Select(e => FromEntry(e, RecipeSourceKind.Saved)));
                break;
            default:
                candidates.AddRange(RecipeLibraryService.Popular.Select(e => FromEntry(e, RecipeSourceKind.Popular)));
                break;
        }

        if (SourceIndex != 0)
        {
            BuildChips(candidates);
            var shown = candidates.Where(r => Matches(r, q) && Passes(r)).ToList();
            if (SourceIndex == 1) Groups.Add(new RecipeGroup("", shown));
            else
                foreach (var g in shown.GroupBy(r => r.Recipe!.FilmSimulation ?? "Other").OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                    Groups.Add(new RecipeGroup($"{g.Key.ToUpperInvariant()} · {g.Count()}", g.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList()));
            if (shown.Count == 0) Groups.Clear();
        }
        else
        {
            Chips.Clear();
        }

        foreach (var row in Groups.SelectMany(g => g.Rows))
        {
            row.IsStarred = row.Recipe != null && FindSaved(row.Recipe) != null;
            row.IsSelected = Selected != null && SameRow(row, Selected);
        }

        RaiseSourceProps();
    }

    private void RaiseSourceProps()
    {
        OnPropertyChanged(nameof(IsPhotoSource));
        OnPropertyChanged(nameof(IsSavedSource));
        OnPropertyChanged(nameof(IsPopularSource));
        OnPropertyChanged(nameof(ShowSearch));
        OnPropertyChanged(nameof(ShowChips));
        OnPropertyChanged(nameof(ShowPhotoFilter));
        OnPropertyChanged(nameof(SavedTabText));
        OnPropertyChanged(nameof(ShowSavedHelp));
        OnPropertyChanged(nameof(ShowNoMatches));
        OnPropertyChanged(nameof(ShowCredit));
        OnPropertyChanged(nameof(ShowOpenFolder));
        OnPropertyChanged(nameof(ShowPhotoList));
        OnPropertyChanged(nameof(ShowGroupedList));
        OnPropertyChanged(nameof(ShowAddMenu));
        OnPropertyChanged(nameof(NoSelectionText));
    }

    private static bool SameRow(RecipeRow a, RecipeRow b) =>
        ReferenceEquals(a, b)
        || (a.Recipe != null && b.Recipe != null && a.Kind == b.Kind && a.Title == b.Title
            && RecipeLibrary.ContentKey(a.Recipe).Equals(RecipeLibrary.ContentKey(b.Recipe)));

    private RecipeRow FromEntry(RecipeLibraryEntry e, RecipeSourceKind kind)
    {
        var from = e.Origin == RecipeOrigin.FromCamera ? " · from camera" : "";
        return new RecipeRow(kind, e.Name, $"{Brief(e.Recipe)}{from}", e.Recipe, this, e.Author, e.SourceUrl, e.Origin, e.Id);
    }

    private static string Brief(FilmRecipe r) =>
        string.Join(" · ", new[] { r.FilmSimulation, r.WhiteBalance, r.DynamicRange }.Where(p => !string.IsNullOrEmpty(p)));

    internal static string TitleFor(FilmRecipe r) =>
        !string.IsNullOrWhiteSpace(r.RecipeName) ? r.RecipeName! : r.FilmSimulation ?? "Recipe";

    private static bool Matches(RecipeRow r, string q) =>
        q.Length == 0
        || r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
        || (r.Recipe?.FilmSimulation?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
        || (r.Author?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);

    private bool Passes(RecipeRow r) =>
        (Filter switch
        {
            "Color" => !RecipeFields.IsMonochrome(r.Recipe?.FilmSimulation),
            "B&W" => RecipeFields.IsMonochrome(r.Recipe?.FilmSimulation),
            _ => true,
        })
        && (SelectedFilm?.Name is not { } film || string.Equals(r.Recipe?.FilmSimulation, film, StringComparison.OrdinalIgnoreCase));

    // Three chips always fit; the film simulation is a drop-down with counts instead of a row that overflows.
    private void BuildChips(List<RecipeRow> all)
    {
        Chips.Clear();
        Chips.Add(new RecipeChip("All", "All", this));
        Chips.Add(new RecipeChip("Color", "Color", this));
        Chips.Add(new RecipeChip("B&W", "B&W", this));
        if (Chips.All(c => c.Filter != Filter)) Filter = "All";
        foreach (var c in Chips) c.IsActive = c.Filter == Filter;

        var keep = SelectedFilm?.Name;
        var options = new List<FilmOption> { new(null, "Film simulation · All") };
        options.AddRange(all.Select(r => r.Recipe!.FilmSimulation).Where(s => !string.IsNullOrEmpty(s))
            .GroupBy(s => s!).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FilmOption(g.Key, $"{g.Key} · {g.Count()}")));
        _buildingFilms = true;
        FilmOptions.Clear();
        foreach (var o in options) FilmOptions.Add(o);
        SelectedFilm = FilmOptions.FirstOrDefault(o => o.Name == keep) ?? FilmOptions[0];
        _buildingFilms = false;
    }

    private bool _buildingFilms;
    public ObservableCollection<FilmOption> FilmOptions { get; } = [];
    [ObservableProperty] private FilmOption? _selectedFilm;
    partial void OnSelectedFilmChanged(FilmOption? value)
    {
        if (!_buildingFilms) Refresh();
    }

    // ── commands ─────────────────────────────────────────────────────────────────────────
    [RelayCommand]
    private void SetFilter(string? filter) => Filter = filter ?? "All";

    [RelayCommand]
    private void SetSource(string? index)
    {
        if (int.TryParse(index, out var i)) SourceIndex = i;
    }

    [RelayCommand]
    private void Select(RecipeRow? row)
    {
        if (row?.Recipe == null) return;
        Selected = row;
        if (IsNarrow) SheetOpen = true;
    }

    /// <summary>Selects a recipe that was just saved from the camera (or a slot), showing it in Saved.</summary>
    public void SelectSaved(RecipeLibraryEntry entry)
    {
        SourceIndex = 1;
        Refresh();
        Selected = Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.EntryId == entry.Id);
    }

    [RelayCommand]
    private void ToggleStar(RecipeRow? row)
    {
        if (row?.Recipe == null) return;
        var saved = FindSaved(row.Recipe);
        if (saved != null)
        {
            RecipeLibraryService.User.Remove(saved.Id);
            _settings.SetRecipeStatus($"Removed “{saved.Name}” from Saved");
        }
        else
        {
            var origin = row.Kind == RecipeSourceKind.Photo ? RecipeOrigin.FromImage : row.Origin;
            var result = RecipeLibraryService.User.Add(row.Recipe, row.DisplayTitle, origin, row.Author, row.SourceUrl);
            _settings.SetRecipeStatus($"Saved “{result.Entry.Name}”");
        }
        Refresh();
        RaiseSelection();
    }

    [RelayCommand]
    private void SaveSelected() => ToggleStar(Selected);

    [RelayCommand]
    private async Task OpenSource()
    {
        if (Selected?.SourceUrl is { } url && _settings.OpenUrlAsync != null)
            await _settings.OpenUrlAsync(url);
    }

    // Phone flow
    [RelayCommand]
    private void UseThisRecipe()
    {
        if (!HasSelection) return;
        SheetOpen = false;
        PhoneStep = 1;
    }

    [RelayCommand]
    private void CloseSheet() => SheetOpen = false;

    [RelayCommand]
    private void ChangeRecipe() => PhoneStep = 0;

    private static RecipeLibraryEntry? FindSaved(FilmRecipe recipe)
    {
        var key = RecipeLibrary.ContentKey(recipe);
        return RecipeLibraryService.User.Entries.FirstOrDefault(e => RecipeLibrary.ContentKey(e.Recipe).Equals(key));
    }
}
