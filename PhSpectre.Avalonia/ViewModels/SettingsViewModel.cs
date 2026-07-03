using System;
using CommunityToolkit.Mvvm.ComponentModel;
using PhSpectre.Rendering;

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

    public bool  IsMobile => OperatingSystem.IsAndroid();
    public Theme Theme    => IsDarkTheme ? Theme.Dark : Theme.Light;
    public int?  Colors   => ColorCount == 0 ? null : ColorCount;

    public int WorkingQualityIndex
    {
        get => (int)WorkingQuality;
        set => WorkingQuality = (WorkingQuality)value;
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
}
