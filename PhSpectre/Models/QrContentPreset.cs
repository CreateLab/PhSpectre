namespace PhSpectre.Models;

// Which subset of the shot's data goes into a generated-link QR payload. Each value is one
// row of the preset table: Camera is always included when present; the rest toggle Lens,
// Photo info (focal/aperture/shutter/ISO) and Recipe independently.
public enum QrContentPreset
{
    CameraAndRecipe,
    CameraLensPhotoInfoAndRecipe,
    CameraPhotoInfoAndRecipe,
    CameraLensAndPhotoInfo,
    RecipeOnly,
}
