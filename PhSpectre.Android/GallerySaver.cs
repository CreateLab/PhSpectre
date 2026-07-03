using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Android.Content;
using Android.Media;
using Android.OS;
using Android.Provider;

namespace PhSpectre.Android;

/// <summary>
/// Writes a PNG straight into the Photos/Pictures gallery via MediaStore — no
/// "where do you want to save this" dialog, which is what a mobile photo app
/// should do. Registered against <c>FileDialogService.GallerySaver</c> at startup.
/// </summary>
internal static class GallerySaver
{
    private const string RelativeFolder = "PhSpectre";

    public static async Task<bool> SaveAsync(string sourcePath, string displayName)
    {
        var context  = global::Android.App.Application.Context;
        var resolver = context.ContentResolver;
        if (resolver == null) return false;

        var mimeType = MimeTypeFor(displayName);

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            return await SaveScopedAsync(resolver, sourcePath, displayName, mimeType);

        return SaveLegacy(context, sourcePath, displayName, mimeType);
    }

    private static string MimeTypeFor(string displayName) =>
        Path.GetExtension(displayName).ToLowerInvariant() is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png";

    // API 29+ (scoped storage): no storage permission needed to create our own
    // MediaStore entries — the whole point of scoped storage.
    [SupportedOSPlatform("android29.0")]
    private static async Task<bool> SaveScopedAsync(ContentResolver resolver, string sourcePath, string displayName, string mimeType)
    {
        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, displayName);
        values.Put(MediaStore.IMediaColumns.MimeType, mimeType);
        values.Put(MediaStore.IMediaColumns.RelativePath, global::Android.OS.Environment.DirectoryPictures + "/" + RelativeFolder);
        values.Put(MediaStore.IMediaColumns.IsPending, 1);

        var uri = resolver.Insert(MediaStore.Images.Media.ExternalContentUri!, values);
        if (uri == null) return false;

        await using (var dest = resolver.OpenOutputStream(uri))
        await using (var src = File.OpenRead(sourcePath))
            await src.CopyToAsync(dest!);

        values.Clear();
        values.Put(MediaStore.IMediaColumns.IsPending, 0);
        resolver.Update(uri, values, null, null);
        return true;
    }

    // API 23-28: scoped storage doesn't exist yet, so this needs WRITE_EXTERNAL_STORAGE
    // (declared in the manifest, maxSdkVersion 28) actually granted at runtime. If it
    // isn't, this throws and the caller surfaces it as an error rather than crashing.
    private static bool SaveLegacy(Context context, string sourcePath, string displayName, string mimeType)
    {
        var picturesDir = global::Android.OS.Environment
            .GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryPictures)!.AbsolutePath;
        var targetDir = Path.Combine(picturesDir, RelativeFolder);
        Directory.CreateDirectory(targetDir);
        var targetPath = Path.Combine(targetDir, displayName);
        File.Copy(sourcePath, targetPath, overwrite: true);

        MediaScannerConnection.ScanFile(context, [targetPath], [mimeType], null);
        return true;
    }
}
