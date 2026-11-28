using PhSpectre.Models;

namespace PhSpectre.Qr;

// Advises against a QR preset that repeats what the card already prints; never a restriction.
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
