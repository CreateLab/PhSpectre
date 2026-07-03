using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace PhSpectre.Avalonia.Services;

public static class FileDialogService
{
    // Set by the Android head at startup: writes straight into the Photos/Pictures
    // gallery via MediaStore instead of showing a "where do you want to save this"
    // dialog, which is what users expect from a mobile photo app. Falls back to the
    // SAF save-file picker on platforms that don't register one (desktop, and any
    // future mobile head that hasn't wired one up yet).
    public static Func<string, string, Task<bool>>? GallerySaver { get; set; }


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

    // Mobile pickers (e.g. Android SAF) hand back content-URI files with no local
    // filesystem path, so picking/saving there has to go through streams instead.

    public static async Task<(Stream Stream, string FileName)?> PickImageAsync(TopLevel topLevel)
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a photo",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Photos") { Patterns = ["*.jpg", "*.jpeg"], MimeTypes = ["image/jpeg"] }]
        });
        if (files.Count == 0) return null;

        var file = files[0];
        var stream = await file.OpenReadAsync();
        return (stream, file.Name);
    }

    public static Task<bool> SavePngFromFileAsync(TopLevel topLevel, string suggestedName, string sourcePath) =>
        GallerySaver != null
            ? GallerySaver(sourcePath, suggestedName)
            : SaveViaPickerAsync(topLevel, suggestedName, sourcePath);

    private static async Task<bool> SaveViaPickerAsync(TopLevel topLevel, string suggestedName, string sourcePath)
    {
        var result = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save palette",
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }]
        });
        if (result == null) return false;

        await using var dest = await result.OpenWriteAsync();
        await using var src  = File.OpenRead(sourcePath);
        await src.CopyToAsync(dest);
        return true;
    }
}
