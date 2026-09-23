using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhSpectre.Avalonia.Services;
using PhSpectre.Models;
using PhSpectre.Recipes;
using PhSpectre.Rendering;
using PhotoMetadata = PhSpectre.Rendering.PaletteImageRenderer.PhotoMetadata;

namespace PhSpectre.Avalonia.ViewModels;

// Mobile-only: how large a working copy to process a picked photo at. Desktop never
// downscales, so this has no effect there — SettingsPanel only shows it when IsMobile.
public enum WorkingQuality { Fast, Balanced, Best }

// Mobile's bottom-sheet section focus (see SettingsViewModel.FocusedSection) — top-level so
// XAML's {x:Static} can reference individual values without nested-type syntax headaches.
public enum SettingsSection { ExportMode, Palette, Collage, Swatches, Metadata, Output, PhotoMetadata, FilmRecipe, About }

// A named type rather than a (string Label, string Value) tuple deliberately — Avalonia's
// binding engine resolves real CLR properties, not compile-time-only tuple element names, so
// {Binding Label}/{Binding Value} in SettingsPanel.axaml need this to exist as a property.
public sealed record RecipeParameterRow(string Label, string Value);

// Mobile main-screen Export Mode chip row item — a named type for the same reason as
// RecipeParameterRow above (real CLR properties for {Binding} in MainView.axaml's
// DataTemplate, plain tuples don't work there).
public sealed record ExportModeChip(ExportMode Mode, string Label, bool IsSelected);

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty] private bool           _isDarkTheme    = true;
    [ObservableProperty] private int            _colorCount     = 0;
    [ObservableProperty] private bool           _showHex        = true;
    [ObservableProperty] private bool           _hexBelow       = false;
    [ObservableProperty] private MetaVerbosity  _metaVerbosity  = MetaVerbosity.Default;
    [ObservableProperty] private MetaStyle      _metaStyle      = MetaStyle.FilmStrip;
    [ObservableProperty] private SamplingMode   _samplingMode   = SamplingMode.Vivid;
    // Default true for fresh installs only — SettingsStorage.Load() returns a real saved
    // false for anyone who already has a settings.json (no forced migration; see
    // PersistedSettings.HalfSize for the matching default).
    [ObservableProperty] private bool           _halfSize       = true;
    [ObservableProperty] private ExportMode     _exportMode     = ExportMode.Card;
    [ObservableProperty] private bool           _showCameraInfo = false;

    // Whether k-means palette extraction should run at all for the current mode — the whole
    // point of ExportMode: InfoOnly/Recipe/CollageInfoOnly skip the expensive part entirely
    // rather than computing colors that never get shown.
    public bool ComputeColors => ExportMode is ExportMode.Card or ExportMode.Collage;

    // InfoOnly/CollageInfoOnly force the camera-info plate on (it's the only content those
    // modes render) and disable the manual toggle so the user can't turn off the one thing
    // the mode exists to show.
    public bool EffectiveShowCameraInfo => ShowCameraInfo || ExportMode is ExportMode.InfoOnly or ExportMode.CollageInfoOnly;
    public bool IsCameraInfoToggleEnabled => ExportMode is not (ExportMode.InfoOnly or ExportMode.CollageInfoOnly);

    partial void OnExportModeChanged(ExportMode value)
    {
        OnPropertyChanged(nameof(ComputeColors));
        OnPropertyChanged(nameof(EffectiveShowCameraInfo));
        OnPropertyChanged(nameof(IsCameraInfoToggleEnabled));
        OnPropertyChanged(nameof(IsRecipeMode));
        OnPropertyChanged(nameof(ShowPhotoMetadataSection));
        OnPropertyChanged(nameof(SaveButtonLabel));
        OnPropertyChanged(nameof(ExportModeChips));
        RaiseSectionVisibilityChanged();
    }

    // Mobile main-screen chip row (desktop's own ComboBox in SettingsPanel.axaml.cs is
    // untouched and keeps its own item list — this is additive, mobile-only UI). Same 5
    // modes as desktop's ExportModeOptions, same "hide what's inapplicable" rule as the
    // desktop ComboBox rebuild — except at 0 selected photos, where every mode is shown
    // since none of them is actually wrong yet (nothing to render either way).
    private static readonly (ExportMode Mode, string Label)[] ExportModeLabels =
    [
        (ExportMode.Card,            "Card"),
        (ExportMode.InfoOnly,        "Info only"),
        (ExportMode.Recipe,          "Recipe"),
        (ExportMode.Collage,         "Collage"),
        (ExportMode.CollageInfoOnly, "Collage (fast)"),
    ];

    public IReadOnlyList<ExportModeChip> ExportModeChips
    {
        get
        {
            var single = new[] { ExportMode.Card, ExportMode.InfoOnly, ExportMode.Recipe };
            var applicable = SelectedPhotoCount switch
            {
                0 => (IReadOnlyCollection<ExportMode>)ExportModeLabels.Select(x => x.Mode).ToArray(),
                1 => single,
                _ => new[] { ExportMode.Collage, ExportMode.CollageInfoOnly }
            };
            return ExportModeLabels
                .Where(x => applicable.Contains(x.Mode))
                .Select(x => new ExportModeChip(x.Mode, x.Label, x.Mode == ExportMode))
                .ToList();
        }
    }

    [RelayCommand]
    private void SelectExportMode(ExportMode mode) => ExportMode = mode;

    // §5 of the bugfix pass: Film Recipe is only relevant in Recipe mode. Photo Metadata is
    // only relevant in Recipe mode OR whenever the camera-info plate is actually going to be
    // drawn (any mode with the info toggle on) — otherwise there's nothing on the exported
    // image that editing these fields would change.
    public bool IsRecipeMode => ExportMode == ExportMode.Recipe;
    public bool ShowPhotoMetadataSection => IsRecipeMode || EffectiveShowCameraInfo;

    partial void OnShowCameraInfoChanged(bool value)
    {
        OnPropertyChanged(nameof(EffectiveShowCameraInfo));
        OnPropertyChanged(nameof(ShowPhotoMetadataSection));
        RaiseSectionVisibilityChanged();
    }

    // ── Mobile bottom-sheet section focus ───────────────────────────────────────────────
    // Desktop never sets this (stays null forever), so IsSectionVisible always returns true
    // there and every Show*SectionUI property below reduces to exactly its underlying raw
    // condition — desktop's behavior is unchanged. Mobile's bottom sheet sets it to show
    // exactly one section at a time instead of the whole panel.
    [ObservableProperty] private SettingsSection? _focusedSection;

    private bool IsSectionVisible(SettingsSection section) => FocusedSection is null || FocusedSection == section;

    public bool ShowExportModeSectionUI    => IsSectionVisible(SettingsSection.ExportMode);
    public bool ShowPaletteSectionUI       => ComputeColors && IsSectionVisible(SettingsSection.Palette);
    public bool ShowCollageSectionUI       => ShowCollageOptions && IsSectionVisible(SettingsSection.Collage);
    public bool ShowSwatchesSectionUI      => ComputeColors && IsSectionVisible(SettingsSection.Swatches);
    public bool ShowMetaVerbositySectionUI => EffectiveShowCameraInfo && IsSectionVisible(SettingsSection.Metadata);
    public bool ShowOutputSectionUI        => IsSectionVisible(SettingsSection.Output);
    public bool ShowPhotoMetadataSectionUI => ShowPhotoMetadataSection && IsSectionVisible(SettingsSection.PhotoMetadata);
    public bool ShowFilmRecipeSectionUI    => IsRecipeMode && IsSectionVisible(SettingsSection.FilmRecipe);
    public bool ShowAboutSectionUI         => IsSectionVisible(SettingsSection.About);

    public bool IsSectionSheetOpen => FocusedSection != null;

    public string FocusedSectionTitle => FocusedSection switch
    {
        SettingsSection.ExportMode => "Export mode",
        SettingsSection.Palette => "Palette",
        SettingsSection.Collage => "Collage",
        SettingsSection.Swatches => "Swatches",
        SettingsSection.Metadata => "Metadata display",
        SettingsSection.Output => "Output",
        SettingsSection.PhotoMetadata => "Photo metadata",
        SettingsSection.FilmRecipe => "Film recipe",
        SettingsSection.About => "About",
        _ => ""
    };

    partial void OnFocusedSectionChanged(SettingsSection? value)
    {
        RaiseSectionVisibilityChanged();
        OnPropertyChanged(nameof(IsSectionSheetOpen));
        OnPropertyChanged(nameof(FocusedSectionTitle));
    }

    [RelayCommand]
    private void OpenSection(SettingsSection section) => FocusedSection = section;

    [RelayCommand]
    private void CloseSection() => FocusedSection = null;

    private void RaiseSectionVisibilityChanged()
    {
        OnPropertyChanged(nameof(ShowExportModeSectionUI));
        OnPropertyChanged(nameof(ShowPaletteSectionUI));
        OnPropertyChanged(nameof(ShowCollageSectionUI));
        OnPropertyChanged(nameof(ShowSwatchesSectionUI));
        OnPropertyChanged(nameof(ShowMetaVerbositySectionUI));
        OnPropertyChanged(nameof(ShowOutputSectionUI));
        OnPropertyChanged(nameof(ShowPhotoMetadataSectionUI));
        OnPropertyChanged(nameof(ShowFilmRecipeSectionUI));
        OnPropertyChanged(nameof(ShowAboutSectionUI));
    }

    // How many photos are currently selected (1 = single photo, 2+ = collage tray) — set by
    // the owning Main VM at the same points it re-validates ExportMode via ExportModeRules,
    // purely so the Export Mode selector in the panel can grey out the modes that don't
    // apply to the current selection instead of letting the user pick an invalid one.
    [ObservableProperty] private int _selectedPhotoCount = 1;

    public bool IsSingleModeAvailable  => SelectedPhotoCount == 1;
    public bool IsCollageModeAvailable => SelectedPhotoCount >= 2;

    partial void OnSelectedPhotoCountChanged(int value)
    {
        OnPropertyChanged(nameof(IsSingleModeAvailable));
        OnPropertyChanged(nameof(IsCollageModeAvailable));
        OnPropertyChanged(nameof(ExportModeChips));
    }
    [ObservableProperty] private WorkingQuality _workingQuality = WorkingQuality.Fast;

    // The actual pixel cap WorkingQuality maps to — shared by the single-photo mobile
    // pipeline (MainViewModel.MaxWorkingDimension) and the collage source-photo decode cap
    // (PaletteExportSettings.CollageSourceMaxDimension), so both respond to the same dial.
    public int WorkingMaxDimension => WorkingQuality switch
    {
        WorkingQuality.Fast     => 2000,
        WorkingQuality.Balanced => 3400,
        WorkingQuality.Best     => 4800,
        _                       => 3400
    };
    [ObservableProperty] private OutputFormat   _outputFormat   = OutputFormat.Png;
    [ObservableProperty] private ExportPreset   _exportPreset   = ExportPreset.Original;

    // Discrete stops the "Plate size" / "Label size" sliders snap to, shown as S/M/L/XL/XXL
    // instead of a raw multiplier. The underlying LabelScale/SwatchScale float is unchanged
    // (still clamped to [0.5, 2.0] by PaletteImageRenderer) — these are just a UI-level index
    // shim over it, same pattern as SwatchShapeIndex/SortOrderIndex below.
    public static readonly float[]  SizeSteps      = { 0.6f, 0.8f, 1.0f, 1.4f, 1.8f };
    public static readonly string[] SizeStepLabels = { "S", "M", "L", "XL", "XXL" };

    private static int ClosestStepIndex(float value)
    {
        int best = 0;
        for (int i = 1; i < SizeSteps.Length; i++)
            if (Math.Abs(SizeSteps[i] - value) < Math.Abs(SizeSteps[best] - value))
                best = i;
        return best;
    }

    [ObservableProperty] private float          _labelScale          = 1.0f;
    [ObservableProperty] private float          _swatchScale         = 1.0f;

    public int SwatchScaleIndex
    {
        get => ClosestStepIndex(SwatchScale);
        set => SwatchScale = SizeSteps[value];
    }
    public string SwatchScaleLabel => SizeStepLabels[SwatchScaleIndex];

    public int LabelScaleIndex
    {
        get => ClosestStepIndex(LabelScale);
        set => LabelScale = SizeSteps[value];
    }
    public string LabelScaleLabel => SizeStepLabels[LabelScaleIndex];
    [ObservableProperty] private bool           _showPercent         = false;
    [ObservableProperty] private SwatchShape    _swatchShape         = SwatchShape.Rectangle;
    [ObservableProperty] private SortOrder      _sortOrder           = SortOrder.None;
    [ObservableProperty] private bool           _useCustomBackground = false;
    [ObservableProperty] private string         _customBackgroundHex = "#FFFFFF";
    [ObservableProperty] private CompositionGuide _compositionGuide  = CompositionGuide.None;

    // Collage-only settings — the panel row is only shown while the owning Main VM has
    // switched into collage mode (ShowCollageOptions), but the value persists across mode
    // toggles so it isn't lost when the user exits and re-enters collage mode. Gutter
    // color isn't a separate setting — it's always the same as the card's background
    // (Theme/CustomBackground below), so the gap between photos never seams against it.
    [ObservableProperty] private bool _showCollageOptions = false;
    [ObservableProperty] private int  _gutterThickness    = 8;

    partial void OnShowCollageOptionsChanged(bool value) => RaiseSectionVisibilityChanged();

    // Editable subset of the photo's metadata strip (Camera/Lens/Focal/Aperture/Shutter/Iso/
    // Date) — the other 5 fields PhotoMetadata carries (FocalEq, ExposureBias, WhiteBalance,
    // ExpProgram, Serial) are always taken as-is from the detected EXIF, never user-edited.
    [ObservableProperty] private bool   _isMetadataEditorExpanded;
    [ObservableProperty] private string _metaCameraText   = "";
    [ObservableProperty] private string _metaLensText     = "";
    [ObservableProperty] private string _metaFocalText    = "";
    [ObservableProperty] private string _metaApertureText = "";
    [ObservableProperty] private string _metaShutterText  = "";
    [ObservableProperty] private string _metaIsoText      = "";
    [ObservableProperty] private string _metaDateText     = "";

    // ── Structured metadata input (base spec §6 / mobile catch-up) ─────────────────────
    // Focal/ISO get real numeric steppers; Aperture/Shutter get format-validated text
    // (reject-and-revert on an invalid commit rather than saving free-form garbage). All
    // four still store into the same plain-string Meta*Text fields above — BuildMetadataOverride
    // and everything downstream is unaffected, only the editor's input controls change.
    public decimal? FocalMm
    {
        get => ParseLeadingNumber(MetaFocalText);
        set => MetaFocalText = value.HasValue ? $"{value.Value:0.#}mm" : "";
    }

    public decimal? IsoValue
    {
        get => ParseLeadingNumber(MetaIsoText);
        set => MetaIsoText = value.HasValue ? $"ISO {value.Value:0}" : "";
    }

    partial void OnMetaFocalTextChanged(string value) => OnPropertyChanged(nameof(FocalMm));
    partial void OnMetaIsoTextChanged(string value)   => OnPropertyChanged(nameof(IsoValue));

    private static decimal? ParseLeadingNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"\d+(\.\d+)?");
        return m.Success && decimal.TryParse(m.Value, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    // Aperture ("f/2.8") / Shutter ("1/200s" or "2s") — reject-and-revert validation only
    // applies to user typing (UpdateSourceTrigger=LostFocus in XAML commits on blur), never
    // to a bulk EXIF load: real cameras/lenses don't always report these in exactly this
    // display format (e.g. a zoom's variable aperture), and a bulk load must never be
    // treated as "invalid input" and silently discarded.
    private string _lastValidAperture = "";
    private string _lastValidShutter  = "";
    private bool   _revertingAperture;
    private bool   _revertingShutter;

    partial void OnMetaApertureTextChanged(string value)
    {
        if (IsBulkLoading || _revertingAperture || string.IsNullOrWhiteSpace(value) || IsValidAperture(value))
        {
            _lastValidAperture = value;
            return;
        }
        _revertingAperture = true;
        MetaApertureText = _lastValidAperture;
        _revertingAperture = false;
    }

    partial void OnMetaShutterTextChanged(string value)
    {
        if (IsBulkLoading || _revertingShutter || string.IsNullOrWhiteSpace(value) || IsValidShutter(value))
        {
            _lastValidShutter = value;
            return;
        }
        _revertingShutter = true;
        MetaShutterText = _lastValidShutter;
        _revertingShutter = false;
    }

    private static bool IsValidAperture(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s.Trim(), @"^[fF]/\d+(\.\d+)?$");

    private static bool IsValidShutter(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s.Trim(), @"^(1/\d+s|\d+(\.\d+)?s)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private PhotoMetadata? _detectedMetadata;
    private FilmRecipe?    _detectedRecipe;

    // True only while LoadMetadataFields is bulk-populating the 7 text fields above (e.g.
    // right after a new photo is selected) — lets the owning ViewModel's Settings.PropertyChanged
    // watcher tell "user actually edited something" apart from "we just loaded a new photo's
    // detected EXIF", so the Regenerate button doesn't pop up for a photo that's already current.
    public bool IsBulkLoading { get; private set; }

    private static readonly PhotoMetadata EmptyMetadata = new("", "", "", "", "", "", "", "", "", "", "", "");

    public void LoadMetadataFields(PhotoMetadata? detected, FilmRecipe? recipe = null)
    {
        IsBulkLoading = true;
        try
        {
            _detectedMetadata = detected;
            var m = detected ?? EmptyMetadata;
            MetaCameraText   = m.Camera;
            MetaLensText     = m.Lens;
            MetaFocalText    = m.Focal;
            MetaApertureText = m.Aperture;
            MetaShutterText  = m.Shutter;
            MetaIsoText      = m.Iso;
            MetaDateText     = m.Date;
            DetectedRecipe   = recipe;
        }
        finally
        {
            IsBulkLoading = false;
        }
    }

    // Detected Fuji film recipe (read-only, derived from the current photo's EXIF — not a
    // persisted setting, so it deliberately isn't [ObservableProperty]: that would sweep it
    // into SettingsStorage and the settings-changed→Regenerate-available pipeline outside the
    // IsBulkLoading-guarded assignment above, same reasoning as _detectedMetadata.
    public FilmRecipe? DetectedRecipe
    {
        get => _detectedRecipe;
        private set
        {
            if (_detectedRecipe == value) return;
            _detectedRecipe = value;
            OnPropertyChanged(nameof(DetectedRecipe));
            OnPropertyChanged(nameof(HasRecipe));
            OnPropertyChanged(nameof(RecipeTitleText));
            OnPropertyChanged(nameof(RecipeParameterRows));
            OnPropertyChanged(nameof(RecipeSourceBadgeText));
        }
    }

    public bool HasRecipe => DetectedRecipe != null;

    public string RecipeTitleText =>
        string.IsNullOrEmpty(DetectedRecipe?.FilmSimulation) ? "Custom Recipe" : DetectedRecipe!.FilmSimulation!;

    // Non-null (Label, Value) rows only, in display order — mirrors
    // PhSpectre.Rendering.RecipeCardRenderer.BuildRows exactly (kept as a separate small copy
    // here rather than shared, since this project doesn't reference PhSpectre.Rendering's
    // raster/ImageSharp types and BuildRows is internal to that assembly).
    public IReadOnlyList<RecipeParameterRow> RecipeParameterRows
    {
        get
        {
            var rows = new List<RecipeParameterRow>();
            var r = DetectedRecipe;
            if (r == null) return rows;
            void Add(string label, string? value) { if (!string.IsNullOrEmpty(value)) rows.Add(new RecipeParameterRow(label, RecipeValueAbbreviations.Shorten(value))); }

            Add("White Balance", r.WhiteBalance);
            if (r.WhiteBalanceShift is { } shift) Add("WB Shift", $"R{shift.Red:+0;-0;0} B{shift.Blue:+0;-0;0}");
            Add("Dynamic Range", r.DynamicRange);
            if (r.HighlightTone is { } hi) Add("Highlight", hi.ToString("+0;-0;0"));
            if (r.ShadowTone is { } sh) Add("Shadow", sh.ToString("+0;-0;0"));
            if (r.Color is { } col) Add("Color", col.ToString());
            if (r.Sharpness is { } sharp) Add("Sharpness", sharp.ToString("+0;-0;0"));
            if (r.NoiseReduction is { } nr) Add("Noise Reduction", nr.ToString("+0;-0;0"));
            if (r.Clarity is { } clarity) Add("Clarity", clarity.ToString("+0.#;-0.#;0"));
            Add("Grain Effect", r.GrainEffect);
            Add("Color Chrome", r.ColorChromeEffect);
            Add("Color Chrome Blue", r.ColorChromeFxBlue);
            if (r.BwAdjustment is { } bw) Add("B&W Adjustment", bw.ToString("+0;-0;0"));

            return rows;
        }
    }

    // ── Manual recipe entry ─────────────────────────────────────────────────────────────
    // Form-local state for the "Set recipe manually"/"Edit" editor — same reasoning as the
    // Photo Metadata editor fields above: not [ObservableProperty]-persisted settings, just
    // scratch state for a form the user explicitly opens and saves.

    [ObservableProperty] private bool    _isRecipeEditorOpen;
    [ObservableProperty] private string  _editRecipeName          = "";
    [ObservableProperty] private string  _editWhiteBalance        = "";
    [ObservableProperty] private string  _editDynamicRange        = "";
    [ObservableProperty] private int     _editHighlightTone;
    [ObservableProperty] private int     _editShadowTone;
    [ObservableProperty] private int     _editColor;
    [ObservableProperty] private int     _editSharpness;
    [ObservableProperty] private int     _editNoiseReduction;
    [ObservableProperty] private decimal _editClarity;
    [ObservableProperty] private string  _editGrainEffect         = "Off";
    [ObservableProperty] private string  _editColorChromeEffect   = "Off";
    [ObservableProperty] private string  _editColorChromeFxBlue   = "Off";
    [ObservableProperty] private int     _editBwAdjustment;
    [ObservableProperty] private int     _editWhiteBalanceShiftRed;
    [ObservableProperty] private int     _editWhiteBalanceShiftBlue;

    // Dropdown option lists for the manual-entry form — forwarded from the core project's
    // RecipeFieldOptions so SettingsPanel.axaml can bind ItemsSource against an instance
    // property (XAML can't bind directly to a static class member).
    public IReadOnlyList<string> WhiteBalanceOptions  => RecipeFieldOptions.WhiteBalance;
    public IReadOnlyList<string> DynamicRangeOptions  => RecipeFieldOptions.DynamicRange;
    public IReadOnlyList<string> ThreeLevelOptions    => RecipeFieldOptions.ThreeLevel;

    public string RecipeSourceBadgeText => DetectedRecipe?.Source == RecipeSource.Manual ? "Custom" : "Auto · Fuji";

    [RelayCommand]
    private void OpenRecipeEditor()
    {
        var r = DetectedRecipe;
        EditRecipeName            = r?.RecipeName ?? r?.FilmSimulation ?? "";
        EditWhiteBalance          = r?.WhiteBalance ?? WhiteBalanceOptions[0];
        EditDynamicRange          = r?.DynamicRange ?? DynamicRangeOptions[0];
        EditHighlightTone         = r?.HighlightTone ?? 0;
        EditShadowTone            = r?.ShadowTone ?? 0;
        EditColor                 = r?.Color ?? 0;
        EditSharpness             = r?.Sharpness ?? 0;
        EditNoiseReduction        = r?.NoiseReduction ?? 0;
        EditClarity               = r?.Clarity ?? 0m;
        EditGrainEffect           = r?.GrainEffect ?? "Off";
        EditColorChromeEffect     = r?.ColorChromeEffect ?? "Off";
        EditColorChromeFxBlue     = r?.ColorChromeFxBlue ?? "Off";
        EditBwAdjustment          = r?.BwAdjustment ?? 0;
        EditWhiteBalanceShiftRed  = r?.WhiteBalanceShift?.Red ?? 0;
        EditWhiteBalanceShiftBlue = r?.WhiteBalanceShift?.Blue ?? 0;
        IsRecipeEditorOpen = true;
    }

    [RelayCommand]
    private void CancelRecipeEditor() => IsRecipeEditorOpen = false;

    [RelayCommand]
    private void SaveRecipeEdits()
    {
        var prev = DetectedRecipe;
        DetectedRecipe = new FilmRecipe(
            Source: RecipeSource.Manual,
            RecipeName: string.IsNullOrWhiteSpace(EditRecipeName) ? null : EditRecipeName.Trim(),
            CameraMake: prev?.CameraMake ?? "",
            CameraModel: prev?.CameraModel ?? "",
            FilmSimulation: string.IsNullOrWhiteSpace(EditRecipeName) ? prev?.FilmSimulation : EditRecipeName.Trim(),
            WhiteBalance: EditWhiteBalance,
            WhiteBalanceShift: (EditWhiteBalanceShiftRed, EditWhiteBalanceShiftBlue) == (0, 0) ? null : (EditWhiteBalanceShiftRed, EditWhiteBalanceShiftBlue),
            DynamicRange: EditDynamicRange,
            HighlightTone: EditHighlightTone,
            ShadowTone: EditShadowTone,
            Color: EditColor,
            Sharpness: EditSharpness,
            NoiseReduction: EditNoiseReduction,
            Clarity: EditClarity,
            GrainEffect: EditGrainEffect,
            ColorChromeEffect: EditColorChromeEffect,
            ColorChromeFxBlue: EditColorChromeFxBlue,
            BwAdjustment: EditBwAdjustment);
        IsRecipeEditorOpen = false;
    }

    public PhotoMetadata BuildMetadataOverride()
    {
        var baseline = _detectedMetadata ?? EmptyMetadata;
        return baseline with
        {
            Camera   = MetaCameraText,
            Lens     = MetaLensText,
            Focal    = MetaFocalText,
            Aperture = MetaApertureText,
            Shutter  = MetaShutterText,
            Iso      = MetaIsoText,
            Date     = MetaDateText
        };
    }

    [RelayCommand]
    private void ResetMetadata() => LoadMetadataFields(_detectedMetadata, _detectedRecipe);

    [RelayCommand]
    private void ToggleMetadataEditor() => IsMetadataEditorExpanded = !IsMetadataEditorExpanded;

    public string MetadataEditorToggleLabel => IsMetadataEditorExpanded ? "Hide" : "Edit";

    partial void OnIsMetadataEditorExpandedChanged(bool value) => OnPropertyChanged(nameof(MetadataEditorToggleLabel));

    public bool  IsMobile => OperatingSystem.IsAndroid();
    public Theme Theme    => IsDarkTheme ? Theme.Dark : Theme.Light;
    public int?  Colors   => ColorCount == 0 ? null : ColorCount;

    // Android shows this row directly in Settings (there's no toolbar to put a banner
    // in); Desktop gets its own dismissible toolbar banner instead (MainWindowViewModel)
    // and hides this row via the existing IsMobile-gated XAML pattern.
    public string AppVersionText      => $"PhSpectre {AppUpdateService.Instance.CurrentVersionText}";
    public bool   IsUpdateAvailable   => AppUpdateService.Instance.IsAvailable;
    public string UpdateAvailableText => $"Update available: {AppUpdateService.Instance.LatestVersionText}";
    public bool   ShowUpdateRow       => IsMobile && IsUpdateAvailable;

    public Func<string, Task>? OpenUrlAsync { get; set; }

    public SettingsViewModel()
    {
        AppUpdateService.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppUpdateService.IsAvailable) or nameof(AppUpdateService.LatestVersionText))
            {
                OnPropertyChanged(nameof(IsUpdateAvailable));
                OnPropertyChanged(nameof(UpdateAvailableText));
                OnPropertyChanged(nameof(ShowUpdateRow));
            }
        };
    }

    [RelayCommand]
    private async Task OpenUpdateUrl()
    {
        if (OpenUrlAsync != null && AppUpdateService.Instance.ReleaseUrl is { } url)
            await OpenUrlAsync(url);
    }

    public string FileExtension    => OutputFormat == OutputFormat.Jpeg ? ".jpg" : ".png";

    // Single unified save button (bugfix §1) — label tells the user what a click actually
    // produces, so Recipe mode reads "Save recipe card" instead of the generic PNG/JPEG label.
    public string SaveButtonLabel  => IsRecipeMode
        ? "Save recipe card"
        : OutputFormat == OutputFormat.Jpeg ? "Save JPEG" : "Save PNG";

    public int WorkingQualityIndex
    {
        get => (int)WorkingQuality;
        set => WorkingQuality = (WorkingQuality)value;
    }

    public int OutputFormatIndex
    {
        get => (int)OutputFormat;
        set => OutputFormat = (OutputFormat)value;
    }

    public int ExportPresetIndex
    {
        get => (int)ExportPreset;
        set => ExportPreset = (ExportPreset)value;
    }

    // Merged Export size dropdown (§6 of the bugfix pass) — folds the old standalone "Half
    // size" checkbox into this list as its own item, desktop-only (mobile already has its
    // own size-reduction control, Working size, and never reads HalfSize in its render path
    // — showing "Half" there would just be a silent no-op). Index layout:
    //   Desktop: 0=Original 1=Half 2=Square 3=IG post 4=Story 5=TG landscape
    //   Mobile:  0=Original 1=Square 2=IG post 3=Story 4=TG landscape
    public int ExportSizeIndex
    {
        get
        {
            if (IsMobile)
                return (int)ExportPreset;
            return ExportPreset switch
            {
                ExportPreset.Original => HalfSize ? 1 : 0,
                ExportPreset.Square => 2,
                ExportPreset.InstagramPost => 3,
                ExportPreset.Story => 4,
                ExportPreset.TelegramLandscape => 5,
                _ => 0
            };
        }
        set
        {
            if (IsMobile)
            {
                ExportPreset = (ExportPreset)value;
                return;
            }
            switch (value)
            {
                case 0: ExportPreset = ExportPreset.Original; HalfSize = false; break;
                case 1: ExportPreset = ExportPreset.Original; HalfSize = true; break;
                case 2: ExportPreset = ExportPreset.Square; HalfSize = false; break;
                case 3: ExportPreset = ExportPreset.InstagramPost; HalfSize = false; break;
                case 4: ExportPreset = ExportPreset.Story; HalfSize = false; break;
                case 5: ExportPreset = ExportPreset.TelegramLandscape; HalfSize = false; break;
            }
        }
    }

    public int SwatchShapeIndex
    {
        get => (int)SwatchShape;
        set => SwatchShape = (SwatchShape)value;
    }

    public int SortOrderIndex
    {
        get => (int)SortOrder;
        set => SortOrder = (SortOrder)value;
    }

    public int CompositionGuideIndex
    {
        get => (int)CompositionGuide;
        set => CompositionGuide = (CompositionGuide)value;
    }

    // ComboBox index shims
    public int ColorCountIndex
    {
        get => ColorCount == 0 ? 0 : ColorCount - 2;
        set => ColorCount = value == 0 ? 0 : value + 2;
    }

    public int MetaVerbosityIndex
    {
        get => (int)MetaVerbosity;
        set => MetaVerbosity = (MetaVerbosity)value;
    }

    public int MetaStyleIndex
    {
        get => (int)MetaStyle;
        set => MetaStyle = (MetaStyle)value;
    }

    public int SamplingModeIndex
    {
        get => (int)SamplingMode;
        set => SamplingMode = (SamplingMode)value;
    }

    partial void OnColorCountChanged(int value)               => OnPropertyChanged(nameof(ColorCountIndex));
    partial void OnMetaVerbosityChanged(MetaVerbosity value)  => OnPropertyChanged(nameof(MetaVerbosityIndex));
    partial void OnMetaStyleChanged(MetaStyle value)          => OnPropertyChanged(nameof(MetaStyleIndex));
    partial void OnSamplingModeChanged(SamplingMode value)    => OnPropertyChanged(nameof(SamplingModeIndex));
    partial void OnWorkingQualityChanged(WorkingQuality value) => OnPropertyChanged(nameof(WorkingQualityIndex));
    partial void OnOutputFormatChanged(OutputFormat value)     { OnPropertyChanged(nameof(OutputFormatIndex)); OnPropertyChanged(nameof(FileExtension)); OnPropertyChanged(nameof(SaveButtonLabel)); }
    partial void OnExportPresetChanged(ExportPreset value)     { OnPropertyChanged(nameof(ExportPresetIndex)); OnPropertyChanged(nameof(ExportSizeIndex)); }
    partial void OnHalfSizeChanged(bool value)                 => OnPropertyChanged(nameof(ExportSizeIndex));
    partial void OnSwatchShapeChanged(SwatchShape value)       => OnPropertyChanged(nameof(SwatchShapeIndex));
    partial void OnSortOrderChanged(SortOrder value)           => OnPropertyChanged(nameof(SortOrderIndex));
    partial void OnCompositionGuideChanged(CompositionGuide value) => OnPropertyChanged(nameof(CompositionGuideIndex));
    partial void OnSwatchScaleChanged(float value) { OnPropertyChanged(nameof(SwatchScaleIndex)); OnPropertyChanged(nameof(SwatchScaleLabel)); }
    partial void OnLabelScaleChanged(float value)  { OnPropertyChanged(nameof(LabelScaleIndex));  OnPropertyChanged(nameof(LabelScaleLabel)); }
}
