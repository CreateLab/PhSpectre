using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PhSpectre.Avalonia.ViewModels;
using PhSpectre.Models;
using PhSpectre.Rendering;

namespace PhSpectre.Avalonia.Services;

internal sealed class PersistedSettings
{
    public bool IsDarkTheme { get; set; } = true;
    public int ColorCount { get; set; }
    public bool ShowHex { get; set; } = true;
    public bool HexBelow { get; set; }
    public MetaVerbosity MetaVerbosity { get; set; } = MetaVerbosity.Default;
    public MetaStyle MetaStyle { get; set; } = MetaStyle.FilmStrip;
    public SamplingMode SamplingMode { get; set; } = SamplingMode.Vivid;
    // True only as the *fresh-install* default (no settings.json on disk yet, so this
    // record's own field initializer is what SettingsStorage.Load() returns) — anyone who
    // already has a settings.json gets their previously-saved value back unchanged, since
    // deserialization overwrites this default. No forced migration for existing users.
    public bool HalfSize { get; set; } = true;
    public ExportMode ExportMode { get; set; } = ExportMode.Card;
    public bool ShowCameraInfo { get; set; }
    public OutputFormat OutputFormat { get; set; } = OutputFormat.Png;
    public ExportPreset ExportPreset { get; set; } = ExportPreset.Original;
    public float LabelScale { get; set; } = 1.0f;
    public float SwatchScale { get; set; } = 1.0f;
    public bool ShowPercent { get; set; }
    public SwatchShape SwatchShape { get; set; } = SwatchShape.Rectangle;
    public SortOrder SortOrder { get; set; } = SortOrder.None;
    public bool UseCustomBackground { get; set; }
    public string CustomBackgroundHex { get; set; } = "#FFFFFF";
    public CompositionGuide CompositionGuide { get; set; } = CompositionGuide.None;
    public int GutterThickness { get; set; } = 8;
}

internal static class SettingsStorage
{
    private static readonly HashSet<string> PersistedPropertyNames = new()
    {
        nameof(SettingsViewModel.IsDarkTheme), nameof(SettingsViewModel.ColorCount),
        nameof(SettingsViewModel.ShowHex), nameof(SettingsViewModel.HexBelow),
        nameof(SettingsViewModel.MetaVerbosity), nameof(SettingsViewModel.MetaStyle),
        nameof(SettingsViewModel.SamplingMode),
        nameof(SettingsViewModel.HalfSize), nameof(SettingsViewModel.OutputFormat),
        nameof(SettingsViewModel.ExportPreset), nameof(SettingsViewModel.LabelScale),
        nameof(SettingsViewModel.SwatchScale), nameof(SettingsViewModel.ShowPercent),
        nameof(SettingsViewModel.SwatchShape), nameof(SettingsViewModel.SortOrder),
        nameof(SettingsViewModel.UseCustomBackground), nameof(SettingsViewModel.CustomBackgroundHex),
        nameof(SettingsViewModel.CompositionGuide), nameof(SettingsViewModel.GutterThickness),
        nameof(SettingsViewModel.ExportMode), nameof(SettingsViewModel.ShowCameraInfo),
    };

    public static bool IsPersistedProperty(string propertyName) => PersistedPropertyNames.Contains(propertyName);

    public static void Apply(SettingsViewModel vm, PersistedSettings s)
    {
        vm.IsDarkTheme           = s.IsDarkTheme;
        vm.ColorCount            = s.ColorCount;
        vm.ShowHex               = s.ShowHex;
        vm.HexBelow              = s.HexBelow;
        vm.MetaVerbosity         = s.MetaVerbosity;
        vm.MetaStyle             = s.MetaStyle;
        vm.SamplingMode          = s.SamplingMode;
        vm.HalfSize              = s.HalfSize;
        vm.ExportMode            = s.ExportMode;
        vm.ShowCameraInfo        = s.ShowCameraInfo;
        vm.OutputFormat          = s.OutputFormat;
        vm.ExportPreset          = s.ExportPreset;
        vm.LabelScale            = s.LabelScale;
        vm.SwatchScale           = s.SwatchScale;
        vm.ShowPercent           = s.ShowPercent;
        vm.SwatchShape           = s.SwatchShape;
        vm.SortOrder             = s.SortOrder;
        vm.UseCustomBackground   = s.UseCustomBackground;
        vm.CustomBackgroundHex   = s.CustomBackgroundHex;
        vm.CompositionGuide      = s.CompositionGuide;
        vm.GutterThickness       = s.GutterThickness;
    }

    public static PersistedSettings Capture(SettingsViewModel vm) => new()
    {
        IsDarkTheme         = vm.IsDarkTheme,
        ColorCount          = vm.ColorCount,
        ShowHex             = vm.ShowHex,
        HexBelow            = vm.HexBelow,
        MetaVerbosity       = vm.MetaVerbosity,
        MetaStyle           = vm.MetaStyle,
        SamplingMode        = vm.SamplingMode,
        HalfSize            = vm.HalfSize,
        ExportMode          = vm.ExportMode,
        ShowCameraInfo      = vm.ShowCameraInfo,
        OutputFormat        = vm.OutputFormat,
        ExportPreset        = vm.ExportPreset,
        LabelScale          = vm.LabelScale,
        SwatchScale         = vm.SwatchScale,
        ShowPercent         = vm.ShowPercent,
        SwatchShape         = vm.SwatchShape,
        SortOrder           = vm.SortOrder,
        UseCustomBackground = vm.UseCustomBackground,
        CustomBackgroundHex = vm.CustomBackgroundHex,
        CompositionGuide    = vm.CompositionGuide,
        GutterThickness     = vm.GutterThickness,
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhSpectre", "settings.json");

    public static PersistedSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new PersistedSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<PersistedSettings>(json) ?? new PersistedSettings();
        }
        catch (Exception ex)
        {
            AppLogger.LogError("Failed to load settings", ex);
            return new PersistedSettings();
        }
    }

    public static void Save(PersistedSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex)
        {
            AppLogger.LogError("Failed to save settings", ex);
        }
    }
}
