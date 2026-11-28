namespace PhSpectre.Models;

// Which shot data a generated link carries; the camera is always included.
public enum QrContentPreset
{
    CameraAndRecipe,
    CameraLensPhotoInfoAndRecipe,
    CameraPhotoInfoAndRecipe,
    CameraLensAndPhotoInfo,
    RecipeOnly,
}
