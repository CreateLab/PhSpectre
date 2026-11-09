using System.Linq;
using System.Threading.Tasks;
using global::Avalonia.Controls;
using global::Avalonia.Platform.Storage;
using PhSpectre.Avalonia.Services;
using PhSpectre.Avalonia.ViewModels;

namespace PhSpectre.Avalonia.Views;

// Pickers for the Edit screen, shared by the inspector buttons and the top-bar Export button.
public static class EditActions
{
    public static async Task ImportAsync(TopLevel? top, ProcessViewModel vm)
    {
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import LUTs and presets",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("LUTs, presets, archives") { Patterns = ["*.cube", "*.xmp", "*.zip"] }]
        });
        // Mobile pickers hand back content URIs without a filesystem path; those are skipped here.
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
        if (paths.Count > 0) vm.Import(paths);
        else if (files.Count > 0) vm.ImportStatus = "This platform can't import from that location yet.";
    }

    public static async Task ExportAllAsync(TopLevel? top, ProcessViewModel vm)
    {
        if (top == null || !vm.CanExportAll) return;
        var folder = await FileDialogService.PickExportFolderAsync(top, vm.DefaultExportDir);
        if (folder != null) await vm.ExportAllAsync(folder);
    }

    public static async Task ExportAsync(TopLevel? top, ProcessViewModel vm)
    {
        if (top == null || !vm.CanExport) return;
        var path = await FileDialogService.SavePngAsync(top, vm.SuggestedExportName, vm.DefaultExportDir);
        if (path != null) await vm.ExportAsync(path);
    }
}
