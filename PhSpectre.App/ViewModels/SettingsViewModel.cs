using CommunityToolkit.Mvvm.ComponentModel;
using PhSpectre.Rendering;

namespace PhSpectre.App.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty] private bool          _isDarkTheme   = true;
    [ObservableProperty] private int           _colorCount    = 0;       // 0=auto, 3-8=explicit
    [ObservableProperty] private bool          _showHex       = true;
    [ObservableProperty] private MetaVerbosity _metaVerbosity = MetaVerbosity.Default;
    [ObservableProperty] private MetaStyle     _metaStyle     = MetaStyle.FilmStrip;

    public Theme Theme  => IsDarkTheme ? Theme.Dark : Theme.Light;
    public int?  Colors => ColorCount == 0 ? null : ColorCount;

    // ComboBox shims — index maps directly to enum int value or custom offset
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

    partial void OnColorCountChanged(int value)    => OnPropertyChanged(nameof(ColorCountIndex));
    partial void OnMetaVerbosityChanged(MetaVerbosity value) => OnPropertyChanged(nameof(MetaVerbosityIndex));
    partial void OnMetaStyleChanged(MetaStyle value)         => OnPropertyChanged(nameof(MetaStyleIndex));
}
