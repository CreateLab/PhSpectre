using PhSpectre.Localization;
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
    // Applies to fresh installs only; a saved settings.json overrides it.
    public bool HalfSize { get; set; } = true;
    public ExportMode ExportMode { get; set; } = ExportMode.Card;
    public bool ShowCameraInfo { get; set; }
    // JPEG by default: cards are made for sharing, and PNG encoding dominates render time.
    public OutputFormat OutputFormat { get; set; } = OutputFormat.Jpeg;
    public ExportPreset ExportPreset { get; set; } = ExportPreset.Original;
    public float LabelScale { get; set; } = 1.0f;
    public float SwatchScale { get; set; } = 1.0f;
    public bool ShowPercent { get; set; }
    public SwatchShape SwatchShape { get; set; } = SwatchShape.Rectangle;
    public SortOrder SortOrder { get; set; } = SortOrder.None;
    // Legacy: only used to migrate a settings.json without BackgroundMode.
    public bool UseCustomBackground { get; set; }
    public string CustomBackgroundHex { get; set; } = "#FFFFFF";
    // Nullable, to tell a missing key from an explicit choice.
    public BackgroundMode? BackgroundMode { get; set; }
    public CompositionGuide CompositionGuide { get; set; } = CompositionGuide.None;
    public int GutterThickness { get; set; } = 8;
    public bool ShowQrCode { get; set; }
    public QrPlacement QrPlacement { get; set; } = QrPlacement.Below;
    public QrCaption QrCaption { get; set; } = QrCaption.None;
    public QrContentSource QrContentSource { get; set; } = QrContentSource.GeneratedLink;
    public QrContentPreset QrContentPreset { get; set; } = QrContentPreset.CameraLensPhotoInfoAndRecipe;
    public string QrCustomUrl { get; set; } = "";
    public bool QrIncludeNote { get; set; }
    public string QrNoteText { get; set; } = "";
    public bool ShowRecipeCard { get; set; } = true;
    public string HiddenRecipeFields { get; set; } = "";
    public bool RecipeVisibilityUserSet { get; set; }
    public AppAppearance Appearance { get; set; } = AppAppearance.System;
    public AppLanguage Language { get; set; } = AppLanguage.System;
    public string SaveFolder { get; set; } = "";
    public bool ShowInFolderAfterSave { get; set; } = false;
    public bool CameraExperimental { get; set; }
    public int RestoreKeepLast { get; set; } = 20;
    public WorkingQuality WorkingQuality { get; set; } = WorkingQuality.Fast;
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
        nameof(SettingsViewModel.BackgroundMode), nameof(SettingsViewModel.CustomBackgroundHex),
        nameof(SettingsViewModel.CompositionGuide), nameof(SettingsViewModel.GutterThickness),
        nameof(SettingsViewModel.ExportMode), nameof(SettingsViewModel.ShowCameraInfo),
        nameof(SettingsViewModel.ShowQrCode), nameof(SettingsViewModel.QrPlacement),
        nameof(SettingsViewModel.QrCaption),
        nameof(SettingsViewModel.QrContentSource), nameof(SettingsViewModel.QrContentPreset),
        nameof(SettingsViewModel.QrCustomUrl), nameof(SettingsViewModel.QrIncludeNote),
        nameof(SettingsViewModel.QrNoteText), nameof(SettingsViewModel.ShowRecipeCard),
        nameof(SettingsViewModel.HiddenRecipeFields), nameof(SettingsViewModel.RecipeVisibilityUserSet),
        nameof(SettingsViewModel.Appearance), nameof(SettingsViewModel.Language), nameof(SettingsViewModel.SaveFolder), nameof(SettingsViewModel.ShowInFolderAfterSave),
        nameof(SettingsViewModel.CameraExperimental), nameof(SettingsViewModel.RestoreKeepLast),
        nameof(SettingsViewModel.WorkingQuality),
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
        vm.BackgroundMode        = s.BackgroundMode ?? (s.UseCustomBackground ? BackgroundMode.Custom : BackgroundMode.Theme);
        vm.CustomBackgroundHex   = s.CustomBackgroundHex;
        vm.CompositionGuide      = s.CompositionGuide;
        vm.GutterThickness       = s.GutterThickness;
        vm.ShowQrCode            = s.ShowQrCode;
        vm.QrPlacement           = s.QrPlacement;
        vm.QrCaption             = s.QrCaption;
        vm.QrContentSource       = s.QrContentSource;
        vm.QrContentPreset       = s.QrContentPreset;
        vm.QrCustomUrl           = s.QrCustomUrl;
        vm.QrIncludeNote         = s.QrIncludeNote;
        vm.QrNoteText            = s.QrNoteText;
        vm.ShowRecipeCard        = s.ShowRecipeCard;
        vm.HiddenRecipeFields    = s.HiddenRecipeFields ?? "";
        vm.RecipeVisibilityUserSet = s.RecipeVisibilityUserSet;
        vm.Appearance            = s.Appearance;
        vm.Language              = s.Language;
        vm.SaveFolder            = s.SaveFolder;
        vm.ShowInFolderAfterSave = s.ShowInFolderAfterSave;
        vm.CameraExperimental    = s.CameraExperimental;
        vm.RestoreKeepLast       = s.RestoreKeepLast;
        vm.WorkingQuality        = s.WorkingQuality;
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
        BackgroundMode      = vm.BackgroundMode,
        UseCustomBackground = vm.BackgroundMode == BackgroundMode.Custom,
        CustomBackgroundHex = vm.CustomBackgroundHex,
        CompositionGuide    = vm.CompositionGuide,
        GutterThickness     = vm.GutterThickness,
        ShowQrCode          = vm.ShowQrCode,
        QrPlacement         = vm.QrPlacement,
        QrCaption           = vm.QrCaption,
        QrContentSource     = vm.QrContentSource,
        QrContentPreset     = vm.QrContentPreset,
        QrCustomUrl         = vm.QrCustomUrl,
        QrIncludeNote       = vm.QrIncludeNote,
        QrNoteText          = vm.QrNoteText,
        ShowRecipeCard      = vm.ShowRecipeCard,
        HiddenRecipeFields  = vm.HiddenRecipeFields,
        RecipeVisibilityUserSet = vm.RecipeVisibilityUserSet,
        Appearance          = vm.Appearance,
        Language            = vm.Language,
        SaveFolder          = vm.SaveFolder,
        ShowInFolderAfterSave = vm.ShowInFolderAfterSave,
        CameraExperimental  = vm.CameraExperimental,
        RestoreKeepLast     = vm.RestoreKeepLast,
        WorkingQuality      = vm.WorkingQuality,
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
