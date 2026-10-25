using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhSpectre.Avalonia.Services;
using PhSpectre.Models;
using PhSpectre.Qr;
using PhSpectre.Recipes;
using PhSpectre.Rendering;
using PhotoMetadata = PhSpectre.Rendering.PaletteImageRenderer.PhotoMetadata;

namespace PhSpectre.Avalonia.ViewModels;

// Mobile-only: how large a working copy to process a picked photo at. Desktop never
// downscales, so this has no effect there — SettingsPanel only shows it when IsMobile.
public enum WorkingQuality { Fast, Balanced, Best }


// How the exported card's background is painted — a flat color (from the Dark/Light theme, or
// a user-picked custom hex) or a blurred/brightened backdrop built from the source photo
// itself. Collage exports never use BlurredPhoto regardless of this setting — see
// PaletteExportSettings.UseBlurredBackground.
public enum BackgroundMode { Theme, Custom, BlurredPhoto }

// One tile in the Content → Recipe grid: a recipe parameter plus whether the card shows it.
public sealed class RecipeFieldItem(RecipeField f, bool isShown, bool isMobile, System.Windows.Input.ICommand toggle)
{
    public string Key => f.Key;
    public string Label => f.Label;
    public string FullLabel => f.FullLabel;
    public string Value => f.Value;
    public bool IsShown => isShown;
    public bool IsHidden => !isShown;
    public double TileOpacity => isShown ? 1.0 : 0.4;
    public bool IsMobile => isMobile;
    public bool IsDesktop => !isMobile;
    public System.Windows.Input.ICommand ToggleCommand => toggle;
}

// Mobile main-screen Export Mode chip row item — a named type for the same reason as
// RecipeParameterRow above (real CLR properties for {Binding} in MainView.axaml's
// DataTemplate, plain tuples don't work there).

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

    // The camera plate is an independent switch: with the palette off it is simply the only thing
    // on the card (turned on automatically when switching to "no palette"), but it can be turned off too.
    public bool EffectiveShowCameraInfo => ShowCameraInfo;
    public bool IsCameraInfoToggleEnabled => true;

    partial void OnExportModeChanged(ExportMode value)
    {
        if (value is ExportMode.InfoOnly or ExportMode.CollageInfoOnly && !ShowCameraInfo) ShowCameraInfo = true;
        OnPropertyChanged(nameof(ShowNoRecipeHint));
        if (value != ExportMode.Recipe) _paletteMemory = value is ExportMode.Card or ExportMode.Collage;
        OnPropertyChanged(nameof(ShowPalette));
        OnPropertyChanged(nameof(IsPaletteSwitchVisible));
        OnPropertyChanged(nameof(TopModeIndex)); OnPropertyChanged(nameof(TopModeSelection));
        OnPropertyChanged(nameof(ComputeColors));
        OnPropertyChanged(nameof(EffectiveShowCameraInfo));
        OnPropertyChanged(nameof(IsCameraInfoToggleEnabled));
        OnPropertyChanged(nameof(IsRecipeMode));
        OnPropertyChanged(nameof(ShowPhotoMetadataSection));
        OnPropertyChanged(nameof(SaveButtonLabel));

        // Re-point the QR content preset at this mode's recommendation (v2 spec §2) — but only
        // while the user hasn't picked a preset of their own; the moment they do (tracked via
        // OnQrContentPresetChanged), this stops touching it so a deliberate choice never gets
        // silently overwritten by a later Export Mode switch.
        if (_qrPresetIsAutoRecommended)
        {
            _settingQrPresetAutomatically = true;
            QrContentPreset = QrModeGuidance.RecommendedPreset(value);
            _settingQrPresetAutomatically = false;
        }
        OnPropertyChanged(nameof(ShowQrDuplicateHint));

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

    // Section visibility for the panel: each flag is just the underlying condition.
    public bool ShowCollageSectionUI       => ShowCollageOptions;
    public bool ShowSwatchesSectionUI      => ComputeColors;
    public bool ShowMetaVerbositySectionUI => EffectiveShowCameraInfo;
    public bool ShowPhotoMetadataSectionUI => ShowPhotoMetadataSection;
    public bool ShowFilmRecipeSectionUI    => IsRecipeMode;

    private void RaiseSectionVisibilityChanged()
    {
        OnPropertyChanged(nameof(ShowCollageSectionUI));
        OnPropertyChanged(nameof(ShowSwatchesSectionUI));
        OnPropertyChanged(nameof(ShowMetaVerbositySectionUI));
        OnPropertyChanged(nameof(ShowPhotoMetadataSectionUI));
        OnPropertyChanged(nameof(ShowFilmRecipeSectionUI));
    }

    // How many photos are currently selected (1 = single photo, 2+ = collage tray) — set by
    // the owning Main VM at the same points it re-validates ExportMode via ExportModeRules,
    // purely so the Export Mode selector in the panel can grey out the modes that don't
    // apply to the current selection instead of letting the user pick an invalid one.
    [ObservableProperty] private int _selectedPhotoCount = 1;

    public bool IsSingleModeAvailable  => SelectedPhotoCount <= 1;
    public bool IsCollageModeAvailable => SelectedPhotoCount >= 2;

    partial void OnSelectedPhotoCountChanged(int value)
    {
        OnPropertyChanged(nameof(IsSingleModeAvailable));
        OnPropertyChanged(nameof(IsCollageModeAvailable));
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
    // JPEG default, not PNG: these cards are made for sharing to social apps, not print, and
    // PNG's lossless encode is the single biggest cost in the whole render pipeline at native
    // photo resolution — several seconds on desktop, much more on mobile — regardless of
    // export mode, since it's paid on encode, not on whether k-means/color extraction ran.
    // Quality 92 (same as RecipeCardRenderer's own JpegEncoder) keeps the overlaid metadata
    // text/hex labels' sharp edges reading clean.
    [ObservableProperty] private OutputFormat   _outputFormat   = OutputFormat.Jpeg;
    [ObservableProperty] private ExportPreset   _exportPreset   = ExportPreset.Original;

    // Discrete stops the "Plate size" / "Label size" sliders snap to, shown as S/M/L/XL/XXL
    // instead of a raw multiplier. The underlying LabelScale/SwatchScale float is unchanged
    // (still clamped to [0.5, 2.0] by PaletteImageRenderer) — these are just a UI-level index
    // shim over it, same pattern as SwatchShapeIndex/SortOrderIndex below.
    public static readonly float[]  SizeSteps      = { 0.6f, 0.8f, 1.0f, 1.4f, 1.8f };
    public static readonly string[] SizeStepLabels = { "60%", "80%", "100%", "140%", "180%" };

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
    [ObservableProperty] private BackgroundMode _backgroundMode      = BackgroundMode.Theme;
    [ObservableProperty] private string         _customBackgroundHex = "#FFFFFF";
    [ObservableProperty] private CompositionGuide _compositionGuide  = CompositionGuide.None;

    // Collage-only settings — the panel row is only shown while the owning Main VM has
    // switched into collage mode (ShowCollageOptions), but the value persists across mode
    // toggles so it isn't lost when the user exits and re-enters collage mode. Gutter
    // color isn't a separate setting — it's always the same as the card's background
    // (Theme/CustomBackground below), so the gap between photos never seams against it.
    [ObservableProperty] private bool _showCollageOptions = false;
    [ObservableProperty] private int  _gutterThickness    = 8;

    partial void OnShowCollageOptionsChanged(bool value)
    {
        OnPropertyChanged(nameof(SaveButtonLabel));
        OnPropertyChanged(nameof(TopModeIndex)); OnPropertyChanged(nameof(TopModeSelection));
        RaiseSectionVisibilityChanged();
        OnPropertyChanged(nameof(ShowBlurredIgnoredForCollageHint));
    }

    // ── QR code (see PaletteExportSettings.BuildQrRenderOptions for how these resolve into
    // an actual render-time URL/placement) ─────────────────────────────────────────────────
    [ObservableProperty] private bool             _showQrCode      = false;
    [ObservableProperty] private QrPlacement       _qrPlacement     = QrPlacement.Below;
    // Independent of Placement (v4 spec §1b) — what's drawn beside the QR block, not what's
    // encoded inside it (that's QrContentPreset below).
    [ObservableProperty] private QrCaption         _qrCaption       = QrCaption.None;
    [ObservableProperty] private QrContentSource   _qrContentSource = QrContentSource.GeneratedLink;
    // Matches QrModeGuidance.RecommendedPreset(ExportMode.Card) — ExportMode's own default —
    // so a fresh VM starts already "auto-recommended" (see _qrPresetIsAutoRecommended below)
    // instead of looking like the user already made a choice.
    [ObservableProperty] private QrContentPreset   _qrContentPreset = QrContentPreset.CameraLensPhotoInfoAndRecipe;
    [ObservableProperty] private string            _qrCustomUrl     = "";
    [ObservableProperty] private bool              _qrIncludeNote   = false;
    [ObservableProperty] private string            _qrNoteText      = "";

    // Whether QrContentPreset is still following OnExportModeChanged's mode-appropriate
    // recommendation rather than a deliberate user pick — flips to false the first time
    // OnQrContentPresetChanged fires for a change this class didn't make itself.
    private bool _qrPresetIsAutoRecommended = true;
    private bool _settingQrPresetAutomatically;

    public bool IsQrCustomUrlMode => QrContentSource == QrContentSource.CustomUrl;

    // Generated-link presets only: whether the currently-selected preset repeats data this
    // Export Mode already prints as text on the card (v2 spec §2) — an advisory hint, not a
    // restriction, shown next to the preset picker.
    public bool ShowQrDuplicateHint => !IsQrCustomUrlMode && QrModeGuidance.DuplicatesCardText(ExportMode, QrContentPreset);

    // Note text field is fully hidden (not just greyed out) when "Include note" isn't
    // checked — a disabled-but-visible field read as a bug, not an unavailable option
    // (v2 bugfix §5).
    public bool ShowQrNoteTextBox => QrIncludeNote && !IsQrCustomUrlMode;

    public int QrPlacementIndex
    {
        get => (int)QrPlacement;
        set => QrPlacement = (QrPlacement)value;
    }

    public int QrCaptionIndex
    {
        get => (int)QrCaption;
        set => QrCaption = (QrCaption)value;
    }

    public int QrContentSourceIndex
    {
        get => (int)QrContentSource;
        set => QrContentSource = (QrContentSource)value;
    }

    public int QrContentPresetIndex
    {
        get => (int)QrContentPreset;
        set => QrContentPreset = (QrContentPreset)value;
    }

    partial void OnQrPlacementChanged(QrPlacement value) => OnPropertyChanged(nameof(QrPlacementIndex));
    partial void OnQrCaptionChanged(QrCaption value) => OnPropertyChanged(nameof(QrCaptionIndex));

    partial void OnQrContentSourceChanged(QrContentSource value)
    {
        OnPropertyChanged(nameof(QrContentSourceIndex));
        OnPropertyChanged(nameof(IsQrCustomUrlMode));
        OnPropertyChanged(nameof(ShowQrDuplicateHint));
        OnPropertyChanged(nameof(ShowQrNoteTextBox));
    }

    partial void OnQrContentPresetChanged(QrContentPreset value)
    {
        OnPropertyChanged(nameof(QrContentPresetIndex));
        OnPropertyChanged(nameof(ShowQrDuplicateHint));
        if (!_settingQrPresetAutomatically) _qrPresetIsAutoRecommended = false;
    }

    partial void OnQrIncludeNoteChanged(bool value) => OnPropertyChanged(nameof(ShowQrNoteTextBox));

    public const int QrNoteMaxLength = 80;
    public string QrNoteCounter => $"{QrNoteText?.Length ?? 0} / {QrNoteMaxLength}";
    partial void OnQrNoteTextChanged(string value) => OnPropertyChanged(nameof(QrNoteCounter));

    // ── Show recipe card (v3 spec §1a) ──────────────────────────────────────────────────
    // Independent of QR: whether Recipe mode draws its title + parameter-plate grid at all.
    // Off + Show QR on together is the "clean, compact" card the spec describes — just the
    // photo and the QR block, no duplicated text.
    [ObservableProperty] private bool _showRecipeCard = true;

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
            OnPropertyChanged(nameof(ShowNoRecipeHint));
            OnPropertyChanged(nameof(RecipeTitleText));
            ApplyAutoRecipeVisibility(value);
            RaiseRecipeFieldsChanged();
            OnPropertyChanged(nameof(RecipeSourceBadgeText));
            _library?.PhotoRecipeChanged();
        }
    }

    public bool HasRecipe => DetectedRecipe != null;

    // Recipe mode on a photo without a detectable recipe: the stage explains it and offers manual entry
    // instead of silently showing a plain photo (feedback #17).
    public bool ShowNoRecipeHint => HasPhoto && IsRecipeMode && !HasRecipe;

    [RelayCommand]
    private void SetRecipeManually()
    {
        ActiveTab = InspectorTab.Content;
        OpenRecipeEditor();
    }

    public string RecipeTitleText =>
        string.IsNullOrEmpty(DetectedRecipe?.FilmSimulation) ? "Custom Recipe" : DetectedRecipe!.FilmSimulation!;

    // Non-null (Label, Value) rows only, in display order — mirrors
    // PhSpectre.Rendering.RecipeCardRenderer.BuildRows exactly (kept as a separate small copy
    // here rather than shared, since this project doesn't reference PhSpectre.Rendering's
    // raster/ImageSharp types and BuildRows is internal to that assembly).
    // ── Recipe field visibility ─────────────────────────────────────────────────────────────
    // Keyed per recipe parameter (not per photo): the hidden set is persisted and shared by the
    // preview, the single export and Save all.
    [ObservableProperty] private string _hiddenRecipeFields = "";
    // Once the user picks visibility themselves, the automatic B&W preset stops touching it.
    [ObservableProperty] private bool _recipeVisibilityUserSet;

    partial void OnHiddenRecipeFieldsChanged(string value) => RaiseRecipeFieldsChanged();

    private void RaiseRecipeFieldsChanged()
    {
        OnPropertyChanged(nameof(RecipeFieldItems));
        OnPropertyChanged(nameof(RecipeVisibilitySummary));
        OnPropertyChanged(nameof(CanShowAllRecipeFields));
    }

    public IReadOnlyList<RecipeFieldItem> RecipeFieldItems
    {
        get
        {
            if (DetectedRecipe is not { } r) return [];
            var hidden = RecipeFields.ParseKeys(HiddenRecipeFields);
            return RecipeFields.BuildAll(r)
                .Select(f => new RecipeFieldItem(f, !hidden.Contains(f.Key), IsMobile, ToggleRecipeFieldCommand))
                .ToList();
        }
    }

    public string RecipeVisibilitySummary
    {
        get
        {
            var items = RecipeFieldItems;
            return $"Showing {items.Count(i => i.IsShown)} of {items.Count}";
        }
    }

    public bool CanShowAllRecipeFields => RecipeFieldItems.Any(i => i.IsHidden);

    [RelayCommand]
    private void ToggleRecipeField(string key)
    {
        var set = RecipeFields.ParseKeys(HiddenRecipeFields).ToHashSet();
        if (!set.Remove(key)) set.Add(key);
        RecipeVisibilityUserSet = true;
        HiddenRecipeFields = RecipeFields.JoinKeys(set);
    }

    // Presets are just a quick way to set the visibility, not a separate mode.
    [RelayCommand]
    private void ApplyRecipePreset(string preset)
    {
        IEnumerable<string> hidden = preset switch
        {
            "bw"  => RecipeFields.BlackAndWhiteHidden,
            "min" => DetectedRecipe is { } r ? RecipeFields.MinimalHidden(RecipeFields.BuildAll(r)) : [],
            _     => [],
        };
        RecipeVisibilityUserSet = true;
        HiddenRecipeFields = RecipeFields.JoinKeys(hidden);
    }

    // First open of a recipe the user hasn't customised: B&W film simulations start with the
    // colour fields hidden, colour ones with everything shown.
    private void ApplyAutoRecipeVisibility(FilmRecipe? recipe)
    {
        if (RecipeVisibilityUserSet || recipe == null) return;
        HiddenRecipeFields = RecipeFields.IsMonochrome(recipe.FilmSimulation)
            ? RecipeFields.JoinKeys(RecipeFields.BlackAndWhiteHidden) : "";
    }

    // ── Manual recipe entry ─────────────────────────────────────────────────────────────
    // Form-local state for the "Set recipe manually"/"Edit" editor — same reasoning as the
    // Photo Metadata editor fields above: not [ObservableProperty]-persisted settings, just
    // scratch state for a form the user explicitly opens and saves.

    [ObservableProperty] private bool    _isRecipeEditorOpen;
    [ObservableProperty] private string  _editRecipeName          = "";
    [ObservableProperty] private string  _editFilmSimulation      = "";
    [ObservableProperty] private string  _editWhiteBalance        = "";
    [ObservableProperty] private string  _editDynamicRange        = "";
    [ObservableProperty] private decimal _editHighlightTone;
    [ObservableProperty] private decimal _editShadowTone;
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
    public IReadOnlyList<string> FilmSimulationOptions => PhSpectre.Camera.FujiSlotCodec.FilmSimulationNames;
    public IReadOnlyList<string> WhiteBalanceOptions  => RecipeFieldOptions.WhiteBalance;
    public IReadOnlyList<string> DynamicRangeOptions  => RecipeFieldOptions.DynamicRange;
    public IReadOnlyList<string> ThreeLevelOptions    => RecipeFieldOptions.ThreeLevel;

    // Properties that only drive the inspector UI (tabs, editor state, hints) — they never change
    // the rendered card, so they must not trigger a preview rebuild.
    public static bool IsUiOnlyProperty(string? name) =>
        name is null
        || name.StartsWith("Edit", StringComparison.Ordinal)
        || name.StartsWith("Show", StringComparison.Ordinal) && name.EndsWith("UI", StringComparison.Ordinal)
        || name is nameof(ActiveTab) or nameof(ActiveTabIndex) or nameof(IsStyleTab) or nameof(IsContentTab)
            or nameof(IsExportTab) or nameof(IsRecipeEditorOpen) or nameof(ShowDiscardConfirm)
            or nameof(HasPhoto) or nameof(ShowNoRecipeHint) or nameof(IsMetadataEditorExpanded)
            or nameof(CameraExperimental) or nameof(IsCameraMode) or nameof(RestoreKeepLast) or nameof(RestoreKeepIndex)
            or nameof(RecipeStatusText) or nameof(HasRecipeStatus);

    public string RecipeSourceBadgeText => DetectedRecipe?.Source == RecipeSource.Manual ? "Custom" : "Auto · Fuji";

    [RelayCommand]
    private void OpenRecipeEditor() => OpenRecipeEditorFor(DetectedRecipe, false);

    /// <summary>Opens the form on a recipe (or empty). From the Camera mode the result is handed back to its picker instead of the photo.</summary>
    public void OpenRecipeEditorFor(FilmRecipe? r, bool forCamera)
    {
        _editForCamera = forCamera;
        _editBase = r;
        EditRecipeName            = r?.RecipeName ?? r?.FilmSimulation ?? "";
        EditFilmSimulation        = r?.FilmSimulation ?? "";
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
        _editSnapshot = EditState();
        ShowDiscardConfirm = false;
        IsRecipeEditorOpen = true;
    }

    // Unsaved-changes guard: the first Cancel/Esc/Back with edits only raises the confirm row.
    [ObservableProperty] private bool _showDiscardConfirm;
    private string _editSnapshot = "";
    public double FieldHeight => IsMobile ? 44 : 40;

    private bool _editForCamera;
    private FilmRecipe? _editBase;

    private string EditState() => string.Join("|", EditRecipeName, EditFilmSimulation, EditWhiteBalance, EditDynamicRange,
        EditHighlightTone, EditShadowTone, EditColor, EditSharpness, EditNoiseReduction, EditClarity,
        EditGrainEffect, EditColorChromeEffect, EditColorChromeFxBlue, EditBwAdjustment,
        EditWhiteBalanceShiftRed, EditWhiteBalanceShiftBlue);

    [RelayCommand]
    private void CancelRecipeEditor()
    {
        if (!ShowDiscardConfirm && EditState() != _editSnapshot) { ShowDiscardConfirm = true; return; }
        ShowDiscardConfirm = false;
        IsRecipeEditorOpen = false;
    }

    [RelayCommand]
    private void KeepEditingRecipe() => ShowDiscardConfirm = false;

    [RelayCommand]
    private void SaveRecipeEdits()
    {
        var prev = _editBase;
        var edited = new FilmRecipe(
            Source: RecipeSource.Manual,
            RecipeName: string.IsNullOrWhiteSpace(EditRecipeName) ? null : EditRecipeName.Trim(),
            CameraMake: prev?.CameraMake ?? "",
            CameraModel: prev?.CameraModel ?? "",
            FilmSimulation: !string.IsNullOrWhiteSpace(EditFilmSimulation) ? EditFilmSimulation
                : prev?.FilmSimulation ?? (string.IsNullOrWhiteSpace(EditRecipeName) ? null : EditRecipeName.Trim()),
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
            BwAdjustment: EditBwAdjustment,
            // The editor has no fields for these slot-only settings; keep what the recipe already had.
            GrainSize: prev?.GrainSize,
            SmoothSkinEffect: prev?.SmoothSkinEffect,
            ColorTemperature: prev?.ColorTemperature);
        if (_editForCamera) Library.UseCustom(edited);
        else DetectedRecipe = edited;
        ShowDiscardConfirm = false;
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

    // ── V2 shell state: app appearance, inspector tabs, top-bar mode, card chips ──────────────
    // App theme (chrome only — the exported card keeps its own Dark/Light/Custom choice).
    [ObservableProperty] private AppAppearance _appearance = AppAppearance.System;

    public bool IsAppearanceSystem => Appearance == AppAppearance.System;
    public bool IsAppearanceLight  => Appearance == AppAppearance.Light;
    public bool IsAppearanceDark   => Appearance == AppAppearance.Dark;

    partial void OnAppearanceChanged(AppAppearance value)
    {
        OnPropertyChanged(nameof(IsAppearanceSystem));
        OnPropertyChanged(nameof(IsAppearanceLight));
        OnPropertyChanged(nameof(IsAppearanceDark));
        App.ApplyAppearance(value);
    }

    [RelayCommand]
    private void SetAppearance(AppAppearance value) => Appearance = value;

    // Inspector tabs: Style / Content / Export. Session-only, never persisted.
    [ObservableProperty] private InspectorTab _activeTab = InspectorTab.Style;
    public int ActiveTabIndex
    {
        get => (int)ActiveTab;
        set => ActiveTab = (InspectorTab)value;
    }
    public bool IsStyleTab   => ActiveTab == InspectorTab.Style;
    public bool IsContentTab => ActiveTab == InspectorTab.Content;
    public bool IsExportTab  => ActiveTab == InspectorTab.Export;

    partial void OnActiveTabChanged(InspectorTab value)
    {
        OnPropertyChanged(nameof(ActiveTabIndex));
        OnPropertyChanged(nameof(IsStyleTab));
        OnPropertyChanged(nameof(IsContentTab));
        OnPropertyChanged(nameof(IsExportTab));
    }

    [RelayCommand]
    private void ShowExportTab() => ActiveTab = InspectorTab.Export;

    // The five ExportModes collapse into three top-bar modes plus a "Show palette" switch:
    // Card = Card / Info only, Collage = Collage / Collage (fast), Recipe stays. Underlying
    // ExportMode is unchanged, so rendering, persistence and ExportModeRules are untouched.
    private bool _paletteMemory = true; // what "Show palette" reads while Recipe (no palette) is active

    public bool ShowPalette
    {
        get => ExportMode == ExportMode.Recipe ? _paletteMemory : ExportMode is ExportMode.Card or ExportMode.Collage;
        set
        {
            _paletteMemory = value;
            ExportMode = ExportMode switch
            {
                ExportMode.Card or ExportMode.InfoOnly => value ? ExportMode.Card : ExportMode.InfoOnly,
                ExportMode.Collage or ExportMode.CollageInfoOnly => value ? ExportMode.Collage : ExportMode.CollageInfoOnly,
                _ => ExportMode
            };
            OnPropertyChanged();
        }
    }
    public bool IsPaletteSwitchVisible => ExportMode != ExportMode.Recipe;

    // 0 = Card, 1 = Recipe, 2 = Collage, 3 = Camera. ShowCollageOptions is the desktop's "collage tray is
    // open" flag, so the segment stays on Collage while the tray exists even with one photo.
    // Camera is a screen of its own, not an ExportMode: switching to it never touches the card preview.
    [ObservableProperty] private bool _isCameraMode;

    partial void OnIsCameraModeChanged(bool value)
    {
        OnPropertyChanged(nameof(TopModeIndex)); OnPropertyChanged(nameof(TopModeSelection));
        if (value) Library.Enter();
    }

    public int TopModeIndex =>
        IsCameraMode ? 3
        : ExportMode == ExportMode.Recipe ? 1
        : ShowCollageOptions || ExportMode is ExportMode.Collage or ExportMode.CollageInfoOnly ? 2
        : 0;

    public int TopModeSelection
    {
        get => TopModeIndex;
        set
        {
            if (value >= 0 && value != TopModeIndex) SelectTopMode(value);
            OnPropertyChanged();
        }
    }

    // Desktop/mobile shells hook this to switch between the single-photo and tray workflows
    // (entering/leaving collage needs selection state this class doesn't own). Without a
    // handler the mode is simply set.
    public Action<int>? TopModeHandler { get; set; }

    [RelayCommand]
    private void SelectTopMode(int index)
    {
        if (index == 3)
        {
            if (CameraExperimental && CameraSupported) IsCameraMode = true;
            return;
        }
        IsCameraMode = false;
        if (TopModeHandler != null) { TopModeHandler(index); return; }
        ApplyTopMode(index);
    }

    public void ApplyTopMode(int index) => ExportMode = index switch
    {
        1 => ExportMode.Recipe,
        2 => ShowPalette ? ExportMode.Collage : ExportMode.CollageInfoOnly,
        _ => ShowPalette ? ExportMode.Card : ExportMode.InfoOnly,
    };

    // Style → Card: Dark / Light / Custom as one row. "Blurred photo" stays a separate switch
    // (it replaces the flat fill but still follows the Dark/Light choice).
    public int CardChoiceIndex
    {
        get => BackgroundMode == BackgroundMode.Custom ? 2 : IsDarkTheme ? 0 : 1;
        set
        {
            if (value == 2) { BackgroundMode = BackgroundMode.Custom; return; }
            IsDarkTheme = value == 0;
            if (BackgroundMode == BackgroundMode.Custom) BackgroundMode = BackgroundMode.Theme;
            OnPropertyChanged();
        }
    }

    public bool UseBlurredBackdrop
    {
        get => BackgroundMode == BackgroundMode.BlurredPhoto;
        set
        {
            if (value) BackgroundMode = BackgroundMode.BlurredPhoto;
            else if (BackgroundMode == BackgroundMode.BlurredPhoto) BackgroundMode = BackgroundMode.Theme;
            OnPropertyChanged();
        }
    }
    public bool IsBlurredBackdropEnabled => !IsCustomBackgroundMode;

    // Colors: Auto / 3 / 4 / 5 / 6 / 8 as a segmented row.
    private static readonly int[] ColorCountStops = { 0, 3, 4, 5, 6, 8 };
    public int ColorCountSegment
    {
        get { var i = Array.IndexOf(ColorCountStops, ColorCount); return i < 0 ? 0 : i; }
        set => ColorCount = ColorCountStops[Math.Clamp(value, 0, ColorCountStops.Length - 1)];
    }

    // Export tab ───────────────────────────────────────────────────────────────────────────
    // Pixel size of the currently loaded photo, so the size options can show real numbers.
    [ObservableProperty] private int _sourceWidth;
    [ObservableProperty] private bool _hasPhoto;
    partial void OnHasPhotoChanged(bool value) => OnPropertyChanged(nameof(ShowNoRecipeHint));
    [ObservableProperty] private int _sourceHeight;

    public IReadOnlyList<ExportSizeOption> ExportSizeOptions
    {
        get
        {
            string Px(double f) => SourceWidth > 0 ? $"{(int)(SourceWidth * f)} × {(int)(SourceHeight * f)}" : "—";
            var list = new List<ExportSizeOption> { new("Original", Px(1)) };
            if (!IsMobile) list.Add(new("Half · 2× downscale", Px(0.5)));
            list.Add(new("Square", "1080 × 1080"));
            list.Add(new("IG post", "1080 × 1350"));
            list.Add(new("Story", "1080 × 1920"));
            list.Add(new("TG landscape", "1920 × 1080"));
            return list;
        }
    }

    partial void OnSourceWidthChanged(int value)  => OnPropertyChanged(nameof(ExportSizeOptions));
    partial void OnSourceHeightChanged(int value) => OnPropertyChanged(nameof(ExportSizeOptions));

    [ObservableProperty] private string _saveFolder = "";
    [ObservableProperty] private bool   _showInFolderAfterSave = false;
    public bool CanChooseSaveFolder => !IsMobile;

    // Recipe plates: equal-width columns, 2 on the narrow mobile sheet.
    public int RecipeColumns => IsMobile ? 2 : 3;
    public string SaveFolderDisplay => IsMobile
        ? "Photos · PhSpectre"
        : string.IsNullOrEmpty(SaveFolder) ? "Pictures/PhSpectre" : ShortenFolder(SaveFolder);

    partial void OnSaveFolderChanged(string value) => OnPropertyChanged(nameof(SaveFolderDisplay));

    private static string ShortenFolder(string path)
    {
        var parts = path.Replace('\\', '/').TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? string.Join('/', parts) : string.Join('/', parts[^2..]);
    }

    public Func<Task<string?>>? ChooseSaveFolderAsync { get; set; }

    [RelayCommand]
    private async Task ChangeSaveFolder()
    {
        if (ChooseSaveFolderAsync == null) return;
        var folder = await ChooseSaveFolderAsync();
        if (!string.IsNullOrEmpty(folder)) SaveFolder = folder;
    }

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
    public string SaveButtonLabel  => ShowCollageOptions || ExportMode is ExportMode.Collage or ExportMode.CollageInfoOnly
        ? "Save collage"
        : IsRecipeMode ? "Save recipe card" : "Save palette card";

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

    public int BackgroundModeIndex
    {
        get => (int)BackgroundMode;
        set => BackgroundMode = (BackgroundMode)value;
    }

    public bool IsCustomBackgroundMode => BackgroundMode == BackgroundMode.Custom;

    // Custom background hex validation. The render keeps using the last valid color while the
    // field holds something unparseable, so a typo never silently turns the card grey/themed.
    private string _lastValidCustomHex = "#FFFFFF";
    public string LastValidCustomHex => _lastValidCustomHex;
    public bool IsCustomHexValid => HexColorInput.TryParse(CustomBackgroundHex, out _);
    public global::Avalonia.Media.IBrush CustomSwatchBrush =>
        HexColorInput.TryParse(LastValidCustomHex, out var c)
            ? new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromRgb(c.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgb24>().R, c.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgb24>().G, c.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgb24>().B))
            : global::Avalonia.Media.Brushes.Transparent;
    public bool ShowCustomHexError => IsCustomBackgroundMode && !IsCustomHexValid;
    public bool IsThemeSelectable => !IsCustomBackgroundMode;
    public bool ShowThemeIgnoredHint => IsCustomBackgroundMode;

    partial void OnCustomBackgroundHexChanged(string value)
    {
        if (HexColorInput.TryParse(value, out _)) _lastValidCustomHex = value;
        OnPropertyChanged(nameof(IsCustomHexValid));
        OnPropertyChanged(nameof(CustomSwatchBrush));
        OnPropertyChanged(nameof(ShowCustomHexError));
    }

    // Surfaces the "Blurred photo is ignored for Collage" caveat as visible inline text
    // instead of only a hover ToolTip on the ComboBox item — touch/Android users have no
    // hover to trigger that tooltip, so without this they'd have no on-screen indication
    // PaletteExportSettings.UseBlurredBackground is silently forcing the setting off.
    public bool ShowBlurredIgnoredForCollageHint => ShowCollageOptions && BackgroundMode == BackgroundMode.BlurredPhoto;

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

    partial void OnColorCountChanged(int value)               { OnPropertyChanged(nameof(ColorCountIndex)); OnPropertyChanged(nameof(ColorCountSegment)); }
    partial void OnIsDarkThemeChanged(bool value)              => OnPropertyChanged(nameof(CardChoiceIndex));
    partial void OnMetaVerbosityChanged(MetaVerbosity value)  => OnPropertyChanged(nameof(MetaVerbosityIndex));
    partial void OnMetaStyleChanged(MetaStyle value)          => OnPropertyChanged(nameof(MetaStyleIndex));
    partial void OnSamplingModeChanged(SamplingMode value)    => OnPropertyChanged(nameof(SamplingModeIndex));
    partial void OnWorkingQualityChanged(WorkingQuality value) => OnPropertyChanged(nameof(WorkingQualityIndex));
    partial void OnOutputFormatChanged(OutputFormat value)     { OnPropertyChanged(nameof(OutputFormatIndex)); OnPropertyChanged(nameof(FileExtension)); OnPropertyChanged(nameof(SaveButtonLabel)); }
    partial void OnExportPresetChanged(ExportPreset value)     { OnPropertyChanged(nameof(ExportPresetIndex)); OnPropertyChanged(nameof(ExportSizeIndex)); }
    partial void OnHalfSizeChanged(bool value)                 => OnPropertyChanged(nameof(ExportSizeIndex));
    partial void OnBackgroundModeChanged(BackgroundMode value)
    {
        OnPropertyChanged(nameof(CardChoiceIndex));
        OnPropertyChanged(nameof(UseBlurredBackdrop));
        OnPropertyChanged(nameof(IsBlurredBackdropEnabled));
        OnPropertyChanged(nameof(BackgroundModeIndex));
        OnPropertyChanged(nameof(IsCustomBackgroundMode));
        OnPropertyChanged(nameof(ShowCustomHexError));
        OnPropertyChanged(nameof(IsThemeSelectable));
        OnPropertyChanged(nameof(ShowThemeIgnoredHint));
        OnPropertyChanged(nameof(ShowBlurredIgnoredForCollageHint));
    }
    partial void OnSwatchShapeChanged(SwatchShape value)       => OnPropertyChanged(nameof(SwatchShapeIndex));
    partial void OnSortOrderChanged(SortOrder value)           => OnPropertyChanged(nameof(SortOrderIndex));
    partial void OnCompositionGuideChanged(CompositionGuide value) => OnPropertyChanged(nameof(CompositionGuideIndex));
    partial void OnSwatchScaleChanged(float value) { OnPropertyChanged(nameof(SwatchScaleIndex)); OnPropertyChanged(nameof(SwatchScaleLabel)); }
    partial void OnLabelScaleChanged(float value)  { OnPropertyChanged(nameof(LabelScaleIndex));  OnPropertyChanged(nameof(LabelScaleLabel)); }
}

public enum AppAppearance { System, Light, Dark }
public enum InspectorTab { Style, Content, Export }

public sealed record ExportSizeOption(string Label, string Pixels);
