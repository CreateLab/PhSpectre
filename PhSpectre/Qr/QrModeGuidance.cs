using PhSpectre.Models;

namespace PhSpectre.Qr;

// Export-Mode-aware guidance for the Generated-link content preset (v2 spec §2): Recipe mode
// already prints the whole recipe as text, InfoOnly/CollageInfoOnly already print camera/
// lens/EXIF as text — recommending a preset that doesn't just repeat what's already on the
// card, and flagging one that does, keeps a fresh "Show QR code" toggle from defaulting into
// pure duplication. This is advisory only, never a hard restriction — the user can still pick
// any preset deliberately; see SettingsViewModel's QrContentPreset auto-recommendation, which
// backs off the moment the user picks a preset of their own.
public static class QrModeGuidance
{
    public static QrContentPreset RecommendedPreset(ExportMode mode) => mode switch
    {
        ExportMode.Recipe => QrContentPreset.CameraLensAndPhotoInfo,
        ExportMode.InfoOnly or ExportMode.CollageInfoOnly => QrContentPreset.RecipeOnly,
        _ => QrContentPreset.CameraLensPhotoInfoAndRecipe,
    };

    // True when the given preset repeats data the card already prints as text in this mode.
    public static bool DuplicatesCardText(ExportMode mode, QrContentPreset preset) => mode switch
    {
        ExportMode.Recipe => preset is QrContentPreset.CameraAndRecipe or QrContentPreset.CameraLensPhotoInfoAndRecipe
            or QrContentPreset.CameraPhotoInfoAndRecipe or QrContentPreset.RecipeOnly,
        ExportMode.InfoOnly or ExportMode.CollageInfoOnly => preset == QrContentPreset.CameraLensAndPhotoInfo,
        _ => false,
    };
}
