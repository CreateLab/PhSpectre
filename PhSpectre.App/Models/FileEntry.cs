using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhSpectre.App.Models;

public partial class FileEntry : ObservableObject
{
    public string FileName { get; }
    public string FullPath { get; }

    [ObservableProperty] private Bitmap? _thumbnailBitmap;

    public FileEntry(string fileName, string fullPath)
    {
        FileName = fileName;
        FullPath = fullPath;
    }
}
