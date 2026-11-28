using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Android.Content;
using Android.Provider;

namespace PhSpectre.Android;

/// <summary>Saves a picture into the gallery through MediaStore, without a file dialog or a storage permission.</summary>
internal static class GallerySaver
{
    private const string RelativeFolder = "PhSpectre";

    [SupportedOSPlatform("android29.0")] // the app's minimum
    public static async Task<bool> SaveAsync(string sourcePath, string displayName)
    {
        var resolver = global::Android.App.Application.Context.ContentResolver;
        if (resolver == null) return false;

        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, displayName);
        values.Put(MediaStore.IMediaColumns.MimeType, MimeTypeFor(displayName));
        values.Put(MediaStore.IMediaColumns.RelativePath, global::Android.OS.Environment.DirectoryPictures + "/" + RelativeFolder);
        values.Put(MediaStore.IMediaColumns.IsPending, 1);

        var uri = resolver.Insert(MediaStore.Images.Media.ExternalContentUri!, values);
        if (uri == null) return false;

        try
        {
            await using var dest = resolver.OpenOutputStream(uri) ?? throw new IOException("The gallery gave no stream.");
            await using var src = File.OpenRead(sourcePath);
            await src.CopyToAsync(dest);
        }
        catch
        {
            resolver.Delete(uri, null, null); // no empty, forever pending entry
            throw;
        }

        values.Clear();
        values.Put(MediaStore.IMediaColumns.IsPending, 0);
        resolver.Update(uri, values, null, null);
        return true;
    }

    private static string MimeTypeFor(string displayName) =>
        Path.GetExtension(displayName).ToLowerInvariant() is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png";
}
