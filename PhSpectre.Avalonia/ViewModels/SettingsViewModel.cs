using PhSpectre.Localization;
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

// Mobile only: size of the working copy.
public enum WorkingQuality { Fast, Balanced, Best }


// Card background: flat colour or the blurred photo (not for collages).
public enum BackgroundMode { Theme, Custom, BlurredPhoto }

// A recipe parameter and whether the card shows it.
public sealed class RecipeFieldItem(RecipeField f, bool isShown, bool isMobile, System.Windows.Input.ICommand toggle)
{
    public string Key => f.Key;
    public string Label => Loc.T(f.Label);
    public string FullLabel => Loc.T(f.FullLabel);
    public string Value => f.Value;
    public bool IsShown => isShown;
    public bool IsHidden => !isShown;
    public double TileOpacity => isShown ? 1.0 : 0.4;
    public bool IsMobile => isMobile;
    public bool IsDesktop => !isMobile;
    public System.Windows.Input.ICommand ToggleCommand => toggle;
}

// A named type: bindings need real properties.

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty] private bool           _isDarkTheme    = true;
    [ObservableProperty] private int            _colorCount     = 0;
    [ObservableProperty] private bool           _showHex        = true;
    [ObservableProperty] private bool           _hexBelow       = false;
    [ObservableProperty] private MetaVerbosity  _metaVerbosity  = MetaVerbosity.Default;
    [ObservableProperty] private MetaStyle      _metaStyle      = MetaStyle.FilmStrip;
    [ObservableProperty] private SamplingMode   _samplingMode   = SamplingMode.Vivid;
    // Fresh installs only.
    [ObservableProperty] private bool           _halfSize       = true;
    [ObservableProperty] private ExportMode     _exportMode     = ExportMode.Card;
    [ObservableProperty] private bool           _showCameraInfo = false;

    // Whether k-means extraction runs for the current mode; InfoOnly/Recipe/CollageInfoOnly skip it.
    public bool ComputeColors => ExportMode is ExportMode.Card or ExportMode.Collage;

    // The camera plate is independent; switching to "no palette" turns it on.
    public bool EffectiveShowCameraInfo => ShowCameraInfo;
    public bool IsCameraInfoToggleEnabled => true;

    partial void OnExportModeChanged(ExportMode value)
    {
        if (value is ExportMode.InfoOnly or ExportMode.CollageInfoOnly && !ShowCameraInfo) ShowCameraInfo = true;
        OnPropertyChanged(nameof(ShowNoRecipeHint));
        if (value != ExportMode.Recipe) _paletteMemory = value is ExportMode.Card or ExportMode.Collage;
        OnPropertyChanged(nameof(ShowPalette));
        OnPropertyChanged(nameof(IsPaletteSwitchVisible));
        RaiseLayoutChanged();
        OnPropertyChanged(nameof(ComputeColors));
        OnPropertyChanged(nameof(EffectiveShowCameraInfo));
        OnPropertyChanged(nameof(IsCameraInfoToggleEnabled));
        OnPropertyChanged(nameof(IsRecipeMode));
        OnPropertyChanged(nameof(ShowPhotoMetadataSection));
        OnPropertyChanged(nameof(SaveButtonLabel));

        // Follow the mode's recommended preset unless the user chose one.
        if (_qrPresetIsAutoRecommended)
        {
            _settingQrPresetAutomatically = true;
            QrContentPreset = QrModeGuidance.RecommendedPreset(value);
            _settingQrPresetAutomatically = false;
        }
        OnPropertyChanged(nameof(ShowQrDuplicateHint));

        RaiseSectionVisibilityChanged();
    }

    [RelayCommand]
    private void SelectExportMode(ExportMode mode) => ExportMode = mode;

    // Recipe fields matter in Recipe mode, metadata also with the camera plate.
    public bool IsRecipeMode => ExportMode == ExportMode.Recipe;
    public bool ShowPhotoMetadataSection => IsRecipeMode || EffectiveShowCameraInfo;

    partial void OnShowCameraInfoChanged(bool value)
    {
        OnPropertyChanged(nameof(EffectiveShowCameraInfo));
        OnPropertyChanged(nameof(ShowPhotoMetadataSection));
        RaiseSectionVisibilityChanged();
    }

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

    // Set by the owner so unavailable modes can be greyed out.
    [ObservableProperty] private int _selectedPhotoCount = 1;

    public bool IsSingleModeAvailable  => SelectedPhotoCount <= 1;
    public bool IsCollageModeAvailable => SelectedPhotoCount >= 2;

    partial void OnSelectedPhotoCountChanged(int value)
    {
        OnPropertyChanged(nameof(IsSingleModeAvailable));
        OnPropertyChanged(nameof(IsCollageModeAvailable));
    }
    [ObservableProperty] private WorkingQuality _workingQuality = WorkingQuality.Fast;

    // Pixel cap WorkingQuality maps to; shared by the mobile single-photo pipeline and the collage source decode.
    public int WorkingMaxDimension => WorkingQuality switch
    {
        WorkingQuality.Fast     => 2000,
        WorkingQuality.Balanced => 3400,
        WorkingQuality.Best     => 4800,
        _                       => 3400
    };
    // JPEG by default, quality 92 as in RecipeCardRenderer.
    [ObservableProperty] private OutputFormat   _outputFormat   = OutputFormat.Jpeg;
    [ObservableProperty] private ExportPreset   _exportPreset   = ExportPreset.Original;

    // Snap stops; the renderer clamps to [0.5, 2.0].
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

    // The gutter colour is always the card background.
    [ObservableProperty] private bool _showCollageOptions = false;
    [ObservableProperty] private int  _gutterThickness    = 8;

    partial void OnShowCollageOptionsChanged(bool value)
    {
        OnPropertyChanged(nameof(SaveButtonLabel));
        RaiseLayoutChanged();
        RaiseSectionVisibilityChanged();
        OnPropertyChanged(nameof(ShowBlurredIgnoredForCollageHint));
    }

    [ObservableProperty] private bool             _showQrCode      = false;
    [ObservableProperty] private QrPlacement       _qrPlacement     = QrPlacement.Below;
    // Independent of Placement.
    [ObservableProperty] private QrCaption         _qrCaption       = QrCaption.None;
    [ObservableProperty] private QrContentSource   _qrContentSource = QrContentSource.GeneratedLink;
    // Matches QrModeGuidance.RecommendedPreset(ExportMode.Card), so a fresh VM starts as auto-recommended.
    [ObservableProperty] private QrContentPreset   _qrContentPreset = QrContentPreset.CameraLensPhotoInfoAndRecipe;
    [ObservableProperty] private string            _qrCustomUrl     = "";
    [ObservableProperty] private bool              _qrIncludeNote   = false;
    [ObservableProperty] private string            _qrNoteText      = "";

    // False once the user changes the preset.
    private bool _qrPresetIsAutoRecommended = true;
    private bool _settingQrPresetAutomatically;

    public bool IsQrCustomUrlMode => QrContentSource == QrContentSource.CustomUrl;

    // Generated-link presets only: advisory hint that the preset repeats data the mode already prints.
    public bool ShowQrDuplicateHint => !IsQrCustomUrlMode && QrModeGuidance.DuplicatesCardText(ExportMode, QrContentPreset);

    // Hidden, not disabled: a disabled field reads as a bug.
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

    [ObservableProperty] private bool _showRecipeCard = true;

    // The editable subset of the metadata; the rest always comes from EXIF.
    [ObservableProperty] private bool   _isMetadataEditorExpanded;
    [ObservableProperty] private string _metaCameraText   = "";
    [ObservableProperty] private string _metaLensText     = "";
    [ObservableProperty] private string _metaFocalText    = "";
    [ObservableProperty] private string _metaApertureText = "";
    [ObservableProperty] private string _metaShutterText  = "";
    [ObservableProperty] private string _metaIsoText      = "";
    [ObservableProperty] private string _metaDateText     = "";

    // Focal length and ISO are steppers, aperture and shutter validated text; all stored as strings.
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

    // Invalid typing is reverted; values loaded from EXIF are left as they are.
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

    // Tells a bulk load from a user edit.
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

    // Read from EXIF. Not an [ObservableProperty], which would persist it and trigger Regenerate.
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
            EnforceFrameAvailability();
            _library?.PhotoRecipeChanged();
        }
    }

    public bool HasRecipe => DetectedRecipe != null;

    // Recipe mode without a detectable recipe: the stage explains it and offers manual entry.
    public bool ShowNoRecipeHint => HasPhoto && IsRecipeMode && !HasRecipe;

    [RelayCommand]
    private void SetRecipeManually()
    {
        ActiveTab = InspectorTab.Content;
        OpenRecipeEditor();
    }

    public string RecipeTitleText =>
        string.IsNullOrEmpty(DetectedRecipe?.FilmSimulation) ? Loc.T("Custom Recipe") : DetectedRecipe!.FilmSimulation!;

    // A small copy of RecipeCardRenderer.BuildRows, which is internal.
    [ObservableProperty] private string _hiddenRecipeFields = "";
    // A user's choice disables the automatic B&W preset.
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
            return Loc.F("Showing {0} of {1}", items.Count(i => i.IsShown), items.Count);
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

    // Presets only set the visibility.
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

    // First time a recipe is opened: B&W hides the colour fields.
    private void ApplyAutoRecipeVisibility(FilmRecipe? recipe)
    {
        if (RecipeVisibilityUserSet || recipe == null) return;
        HiddenRecipeFields = RecipeFields.IsMonochrome(recipe.FilmSimulation)
            ? RecipeFields.JoinKeys(RecipeFields.BlackAndWhiteHidden) : "";
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CardTabsVisible))] private bool    _isRecipeEditorOpen;
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

    // Forwarded so XAML can bind to them.
    public IReadOnlyList<string> FilmSimulationOptions => PhSpectre.Camera.FujiSlotCodec.FilmSimulationNames;
    public IReadOnlyList<string> WhiteBalanceOptions  => RecipeFieldOptions.WhiteBalance;
    public IReadOnlyList<string> DynamicRangeOptions  => RecipeFieldOptions.DynamicRange;
    public IReadOnlyList<string> ThreeLevelOptions    => RecipeFieldOptions.ThreeLevel;

    // Inspector-only state: no preview rebuild.
    public static bool IsUiOnlyProperty(string? name) =>
        name is null
        || name.StartsWith("Edit", StringComparison.Ordinal)
        || name.StartsWith("Show", StringComparison.Ordinal) && name.EndsWith("UI", StringComparison.Ordinal)
        || name is nameof(ActiveTab) or nameof(ActiveTabIndex) or nameof(IsStyleTab) or nameof(IsContentTab)
            or nameof(IsExportTab) or nameof(IsRecipeEditorOpen) or nameof(ShowDiscardConfirm)
            or nameof(HasPhoto) or nameof(ShowNoRecipeHint) or nameof(IsMetadataEditorExpanded)
            or nameof(CameraExperimental) or nameof(IsCameraMode) or nameof(RestoreKeepLast) or nameof(RestoreKeepIndex)
            or nameof(RecipeStatusText) or nameof(HasRecipeStatus)
        // Navigation state: no Regenerate.
        || name is "CardTopIndex" or "CardTabIndex" or "FrameTypeIndex" or "CardShowsOptions"
            or "CardTabsVisible" or "ShowLegacyInspector" or "ShowLegacyExport"
            or "ShowRecipeCardReasonInFrame" or "TopModeIndex" or "TopModeSelection"
            or "IsEditMode" or "IsProcessMode" or "FrameIsBorder"
            or "IsCollageLike" or "IsCollageMode"
            or "RecipeCardAvailable" or "RecipeCardReason" or "ShowRecipeCardReason";

    public string RecipeSourceBadgeText => DetectedRecipe?.Source == RecipeSource.Manual ? Loc.T("recipe::Custom") : "Auto · Fuji";

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

    // With unsaved edits the first Cancel only asks to confirm.
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
            // Keep settings the editor has no fields for.
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

    public string MetadataEditorToggleLabel => IsMetadataEditorExpanded ? Loc.T("Hide") : Loc.T("Edit");

    partial void OnIsMetadataEditorExpandedChanged(bool value) => OnPropertyChanged(nameof(MetadataEditorToggleLabel));

    // Shell state: appearance, inspector tabs, mode switch, card chips
    // Applied at the next start: XAML text is resolved once.
    [ObservableProperty] private AppLanguage _language = AppLanguage.System;
    public IReadOnlyList<string> LanguageLabels { get; } = [Loc.T("System"), "English", "Русский", "Español"];
    public int LanguageIndex
    {
        get => (int)Language;
        set { if (value >= 0) Language = (AppLanguage)value; }
    }
    partial void OnLanguageChanged(AppLanguage value) => OnPropertyChanged(nameof(LanguageIndex));

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

    // Session only.
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
        OnPropertyChanged(nameof(CardTopIndex)); OnPropertyChanged(nameof(CardShowsOptions));
            OnPropertyChanged(nameof(CardTabsVisible)); OnPropertyChanged(nameof(ShowRecipeCardReasonInFrame));
            OnPropertyChanged(nameof(ShowLegacyExport));
        OnPropertyChanged(nameof(IsStyleTab));
        OnPropertyChanged(nameof(IsContentTab));
        OnPropertyChanged(nameof(IsExportTab));
    }

    [RelayCommand]
    private void ShowExportTab() => ActiveTab = InspectorTab.Export;

    // The top bar shows three modes plus "Show palette"; ExportMode itself is unchanged.
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

    // 0 Edit, 1 Collage, 2 Camera. Collage stays selected while the tray is open; Camera is not an ExportMode.
    [ObservableProperty] private bool _isCameraMode;

    partial void OnIsCameraModeChanged(bool value)
    {
        RaiseLayoutChanged();
        if (value) Library.Enter();
        else if (IsProcessMode) Process.Refresh();
    }

    // The Camera tab stays selected while its setup dialog is open.
    public int TopModeIndex => IsCameraMode || _camera?.SetupOpen == true ? 2 : IsCollageLike ? 1 : 0;

    public int TopModeSelection
    {
        get => TopModeIndex;
        set
        {
            if (value >= 0 && value != TopModeIndex) SelectTopMode(value);
            OnPropertyChanged();
        }
    }

    // Without a handler the mode is simply set.
    public Action<int>? TopModeHandler { get; set; }

    [RelayCommand]
    private void SelectTopMode(int index)
    {
        if (index == 2)
        {
            if (!CameraSupported) return;
            if (!CameraExperimental)
            {
                Camera.OpenSetup();
            }
            else IsCameraMode = true;
            return;
        }
        IsCameraMode = false;
        // Legacy index: 0 Card, 1 Recipe, 2 Collage.
        int legacy = index == 1 ? 2 : FrameType == FrameType.RecipeCard ? 1 : 0;
        if (TopModeHandler != null) TopModeHandler(legacy); else ApplyTopMode(legacy);
        RaiseLayoutChanged();
        if (index == 0) NotifyPhotoChanged();
    }

    public void ApplyTopMode(int index) => ExportMode = index switch
    {
        1 => ExportMode.Recipe,
        2 => ShowPalette ? ExportMode.Collage : ExportMode.CollageInfoOnly,
        _ => ShowPalette ? ExportMode.Card : ExportMode.InfoOnly,
    };

    // "Blurred photo" is a separate switch.
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

    // Auto, 3, 4, 5, 6 or 8 colours
    private static readonly int[] ColorCountStops = { 0, 3, 4, 5, 6, 8 };
    public int ColorCountSegment
    {
        get { var i = Array.IndexOf(ColorCountStops, ColorCount); return i < 0 ? 0 : i; }
        set => ColorCount = ColorCountStops[Math.Clamp(value, 0, ColorCountStops.Length - 1)];
    }

    // Export tab. The loaded photo's pixel size, for the size labels.
    [ObservableProperty] private int _sourceWidth;
    [ObservableProperty] private bool _hasPhoto;
    partial void OnHasPhotoChanged(bool value) => OnPropertyChanged(nameof(ShowNoRecipeHint));
    [ObservableProperty] private int _sourceHeight;

    public IReadOnlyList<ExportSizeOption> ExportSizeOptions
    {
        get
        {
            string Px(double f) => SourceWidth > 0 ? $"{(int)(SourceWidth * f)} × {(int)(SourceHeight * f)}" : "—";
            var list = new List<ExportSizeOption> { new(Loc.T("Original"), Px(1)) };
            if (!IsMobile) list.Add(new(Loc.T("Half · 2× downscale"), Px(0.5)));
            list.Add(new(Loc.T("Square"), "1080 × 1080"));
            list.Add(new(Loc.T("IG post"), "1080 × 1350"));
            list.Add(new(Loc.T("Story"), "1080 × 1920"));
            list.Add(new(Loc.T("TG landscape"), "1920 × 1080"));
            return list;
        }
    }

    partial void OnSourceWidthChanged(int value)  => OnPropertyChanged(nameof(ExportSizeOptions));
    partial void OnSourceHeightChanged(int value) => OnPropertyChanged(nameof(ExportSizeOptions));

    [ObservableProperty] private string _saveFolder = "";
    [ObservableProperty] private bool   _showInFolderAfterSave = false;
    public bool CanChooseSaveFolder => !IsMobile;

    // Two columns on the narrow mobile sheet.
    public int RecipeColumns => IsMobile ? 2 : 3;
    public string SaveFolderDisplay => IsMobile
        ? Loc.T("Photos · PhSpectre")
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

    // Android shows this in Settings; desktop uses a banner.
    public string AppVersionText      => $"PhSpectre {AppUpdateService.Instance.CurrentVersionText}";
    public bool   IsUpdateAvailable   => AppUpdateService.Instance.IsAvailable;
    public string UpdateAvailableText => Loc.F("Update available: {0}", AppUpdateService.Instance.LatestVersionText);
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

    // The label says what the click produces.
    public string SaveButtonLabel  => ShowCollageOptions || ExportMode is ExportMode.Collage or ExportMode.CollageInfoOnly
        ? Loc.T("Save collage")
        : IsRecipeMode ? Loc.T("Save recipe card") : Loc.T("Save palette card");

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

    // Size list. Desktop: Original, Half, Square, IG post, Story, TG landscape. Mobile has no Half.
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

    // While the text is invalid the last valid colour is used.
    private string _lastValidCustomHex = "#FFFFFF";
    public string LastValidCustomHex => _lastValidCustomHex;
    public bool IsCustomHexValid => HexColorInput.TryParse(CustomBackgroundHex, out _);
    public global::Avalonia.Media.IBrush CustomSwatchBrush =>
        HexColorInput.TryParse(LastValidCustomHex, out var c)
            ? new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromRgb(
                c.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgb24>().R,
                c.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgb24>().G,
                c.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgb24>().B))
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

    // Touch screens have no tooltips.
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
    partial void OnOutputFormatChanged(OutputFormat value)    
    {
        if (_process != null) _process.ExportFormatIndex = value == OutputFormat.Png ? 1 : 0;
        OnPropertyChanged(nameof(OutputFormatIndex)); OnPropertyChanged(nameof(FileExtension));
        OnPropertyChanged(nameof(SaveButtonLabel));
    }
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
