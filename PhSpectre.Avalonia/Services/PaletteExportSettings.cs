using PhSpectre;
using PhSpectre.Rendering;
using PhSpectre.Avalonia.ViewModels;

namespace PhSpectre.Avalonia.Services;

// Immutable snapshot of the settings panel, taken once at the start of an export (single
// or batch) so the parameters used for a whole batch can't drift if the user keeps
// adjusting the live panel while it's running.
public sealed record PaletteExportSettings(
    int? Colors,
    SamplingMode SamplingMode,
    bool ShowHex,
    bool HexBelow,
    MetaVerbosity MetaVerbosity,
    MetaStyle MetaStyle,
    Theme Theme,
    bool ShowSwatches,
    bool HalfSize,
    OutputFormat Format,
    ExportPreset ExportPreset)
{
    public string FileExtension => Format == OutputFormat.Jpeg ? ".jpg" : ".png";

    public static PaletteExportSettings SnapshotFrom(SettingsViewModel s) => new(
        s.Colors, s.SamplingMode, s.ShowHex, s.HexBelow, s.MetaVerbosity, s.MetaStyle,
        s.Theme, s.ShowSwatches, s.HalfSize, s.OutputFormat, s.ExportPreset);
}
