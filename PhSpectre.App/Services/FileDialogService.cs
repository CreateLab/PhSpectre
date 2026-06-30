using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace PhSpectre.App.Services;

public static class FileDialogService
{
    public static async Task<string?> PickFolderAsync(TopLevel topLevel)
    {
        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select photo folder", AllowMultiple = false });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> SavePngAsync(TopLevel topLevel, string suggestedName, string defaultDir)
    {
        var startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(defaultDir);
        var result = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save palette",
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = startFolder,
            FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }]
        });
        return result?.TryGetLocalPath();
    }
}
