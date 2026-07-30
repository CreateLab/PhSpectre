using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhSpectre.Avalonia.Services;
using PhSpectre.Rendering;
using PhotoMetadata = PhSpectre.Rendering.PaletteImageRenderer.PhotoMetadata;

namespace PhSpectre.Avalonia.ViewModels;

// Mobile-only: how large a working copy to process a picked photo at. Desktop never
// downscales, so this has no effect there — SettingsPanel only shows it when IsMobile.
public enum WorkingQuality { Fast, Balanced, Best }

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty] private bool           _isDarkTheme    = true;
    [ObservableProperty] private int            _colorCount     = 0;
    [ObservableProperty] private bool           _showHex        = true;
    [ObservableProperty] private bool           _hexBelow       = false;
    [ObservableProperty] private MetaVerbosity  _metaVerbosity  = MetaVerbosity.Default;
    [ObservableProperty] private MetaStyle      _metaStyle      = MetaStyle.FilmStrip;
    [ObservableProperty] private SamplingMode   _samplingMode   = SamplingMode.Vivid;
    [ObservableProperty] private bool           _showSwatches   = true;
    [ObservableProperty] private bool           _halfSize       = false;
    [ObservableProperty] private WorkingQuality _workingQuality = WorkingQuality.Fast;
    [ObservableProperty] private OutputFormat   _outputFormat   = OutputFormat.Png;
    [ObservableProperty] private ExportPreset   _exportPreset   = ExportPreset.Original;

    [ObservableProperty] private float          _labelScale          = 1.0f;
    [ObservableProperty] private float          _swatchScale         = 1.0f;
    [ObservableProperty] private bool           _showPercent         = false;
    [ObservableProperty] private SwatchShape    _swatchShape         = SwatchShape.Rectangle;
    [ObservableProperty] private SortOrder      _sortOrder           = SortOrder.None;
    [ObservableProperty] private bool           _useCustomBackground = false;
    [ObservableProperty] private string         _customBackgroundHex = "#FFFFFF";
    [ObservableProperty] private CompositionGuide _compositionGuide  = CompositionGuide.None;

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

    private PhotoMetadata? _detectedMetadata;

    // True only while LoadMetadataFields is bulk-populating the 7 text fields above (e.g.
    // right after a new photo is selected) — lets the owning ViewModel's Settings.PropertyChanged
    // watcher tell "user actually edited something" apart from "we just loaded a new photo's
    // detected EXIF", so the Regenerate button doesn't pop up for a photo that's already current.
    public bool IsBulkLoading { get; private set; }

    private static readonly PhotoMetadata EmptyMetadata = new("", "", "", "", "", "", "", "", "", "", "", "");

    public void LoadMetadataFields(PhotoMetadata? detected)
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
        }
        finally
        {
            IsBulkLoading = false;
        }
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
    private void ResetMetadata() => LoadMetadataFields(_detectedMetadata);

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
    public string SaveButtonLabel  => OutputFormat == OutputFormat.Jpeg ? "Save JPEG" : "Save PNG";

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
    partial void OnExportPresetChanged(ExportPreset value)     => OnPropertyChanged(nameof(ExportPresetIndex));
    partial void OnSwatchShapeChanged(SwatchShape value)       => OnPropertyChanged(nameof(SwatchShapeIndex));
    partial void OnSortOrderChanged(SortOrder value)           => OnPropertyChanged(nameof(SortOrderIndex));
    partial void OnCompositionGuideChanged(CompositionGuide value) => OnPropertyChanged(nameof(CompositionGuideIndex));
}
