using PhSpectre.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace PhSpectre.Avalonia.Services;

public static class FileDialogService
{
    // Set by the Android head: saves straight into the gallery. Elsewhere the save-file picker is used.
    public static Func<string, string, Task<bool>>? GallerySaver { get; set; }


    public static async Task<string?> PickFolderAsync(TopLevel topLevel)
    {
        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = Loc.T("Select photo folder"), AllowMultiple = false });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    // Batch export destination, with a suggested start location.
    public static async Task<string?> PickExportFolderAsync(TopLevel topLevel, string suggestedStartDir)
    {
        var startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(suggestedStartDir);
        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.T("Select destination folder"),
            AllowMultiple = false,
            SuggestedStartLocation = startFolder
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> SavePngAsync(TopLevel topLevel, string suggestedName, string defaultDir)
    {
        var startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(defaultDir);
        var result = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = SuggestedFileName.DialogTitle(suggestedName),
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = startFolder,
            FileTypeChoices = FileTypeChoicesFor(suggestedName)
        });
        return result?.TryGetLocalPath();
    }

    // The file type filter follows the extension of the suggested name.
    private static FilePickerFileType[] FileTypeChoicesFor(string suggestedName) =>
        Path.GetExtension(suggestedName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => [new FilePickerFileType("JPEG") { Patterns = ["*.jpg", "*.jpeg"] }],
            _                  => [new FilePickerFileType("PNG")  { Patterns = ["*.png"] }]
        };

    // Mobile pickers return content URIs without a local path, so streams are used.

    // HEIC/HEIF is offered on desktop only: on real Android devices BitmapFactory failed to decode these files.
    private static FilePickerFileType PhotoPickerFileType() => new(Loc.T("Photos"))
    {
        Patterns = OperatingSystem.IsAndroid()
            ? ["*.jpg", "*.jpeg"]
            : ["*.jpg", "*.jpeg", "*.heif", "*.heic", "*.hif"],
        MimeTypes = OperatingSystem.IsAndroid()
            ? ["image/jpeg"]
            : ["image/jpeg", "image/heif", "image/heic"]
    };

    public static async Task<(Stream Stream, string FileName)?> PickImageAsync(TopLevel topLevel)
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Select a photo"),
            AllowMultiple = false,
            FileTypeFilter = [PhotoPickerFileType()]
        });
        if (files.Count == 0) return null;

        var file = files[0];
        var stream = await file.OpenReadAsync();
        return (stream, file.Name);
    }

    public static async Task<IReadOnlyList<(Stream Stream, string FileName)>> PickImagesAsync(TopLevel topLevel)
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Select photos for the collage"),
            AllowMultiple = true,
            FileTypeFilter = [PhotoPickerFileType()]
        });

        var result = new List<(Stream, string)>(files.Count);
        foreach (var file in files)
            result.Add((await file.OpenReadAsync(), file.Name));
        return result;
    }

    public static Task<bool> SavePngFromFileAsync(TopLevel topLevel, string suggestedName, string sourcePath) =>
        GallerySaver != null
            ? GallerySaver(sourcePath, suggestedName)
            : SaveViaPickerAsync(topLevel, suggestedName, sourcePath);

    private static async Task<bool> SaveViaPickerAsync(TopLevel topLevel, string suggestedName, string sourcePath)
    {
        var result = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = SuggestedFileName.DialogTitle(suggestedName),
            SuggestedFileName = suggestedName,
            FileTypeChoices = FileTypeChoicesFor(suggestedName)
        });
        if (result == null) return false;

        await using var dest = await result.OpenWriteAsync();
        await using var src  = File.OpenRead(sourcePath);
        await src.CopyToAsync(dest);
        return true;
    }
}
