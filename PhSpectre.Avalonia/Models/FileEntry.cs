using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhSpectre.Avalonia.Models;

public partial class FileEntry : ObservableObject
{
    public string FileName { get; }
    public string FullPath { get; }

    [ObservableProperty] private Bitmap? _thumbnailBitmap;

    // Collage mode's multi-select checkbox — MainWindowViewModel listens for this changing
    // to keep CollageItems (the tray) in sync, so IsChecked stays the single source of truth
    // for tray membership instead of two places that could drift apart.
    [ObservableProperty] private bool _isChecked;

    // Set by MainWindowViewModel whenever the tray changes — true only for CollageItems[0],
    // whose order doubles as the collage's EXIF source. Kept as a plain property (updated
    // imperatively) rather than computed in XAML so the tray badge binding stays trivial.
    [ObservableProperty] private bool _isCollageExifSource;

    public FileEntry(string fileName, string fullPath)
    {
        FileName = fileName;
        FullPath = fullPath;
    }
}
