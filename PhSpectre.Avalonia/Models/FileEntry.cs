using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhSpectre.Avalonia.Models;

public partial class FileEntry : ObservableObject
{
    public string FileName { get; }
    public string FullPath { get; }

    [ObservableProperty] private Bitmap? _thumbnailBitmap;

    // IsChecked is the single source of truth for collage tray membership; MainWindowViewModel keeps CollageItems
    // in sync.
    [ObservableProperty] private bool _isChecked;

    // True only for CollageItems[0], which is also the collage's EXIF source.
    [ObservableProperty] private bool _isCollageExifSource;

    // Set once this photo has been saved.
    [ObservableProperty] private bool _isExported;

    // The photo currently on screen (mobile filmstrip).
    [ObservableProperty] private bool _isCurrent;

    public FileEntry(string fileName, string fullPath)
    {
        FileName = fileName;
        FullPath = fullPath;
    }
}
