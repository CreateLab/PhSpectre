using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.ColorGrading;

// Preset first, then LUT, each with its own strength and switch.
public sealed class GradeStack
{
    public LrPreset? Preset { get; set; }
    public float PresetStrength { get; set; } = 1f;
    public bool PresetEnabled { get; set; } = true;

    public CubeLut? Lut { get; set; }
    public float LutStrength { get; set; } = 1f;
    public bool LutEnabled { get; set; } = true;

    /// <summary>The active preset smooths skin, so a skin mask is needed.</summary>
    public bool SmoothsSkin => Preset != null && PresetEnabled && PresetStrength > 0f && Preset.Get("SkinSmoothing") > 0f;

    public bool IsEmpty =>
        !((Preset != null && PresetEnabled && PresetStrength > 0f) || (Lut != null && LutEnabled && LutStrength > 0f));

    // For a piece of a photo pass wholeFrameEffects = false with the photo's long side, noise profile and skin
    // mask. detail = false skips noise reduction, skin smoothing and sharpening.
    public void Apply(Image<Rgb24> image, bool wholeFrameEffects = true, bool detail = true, int photoLongSide = 0,
        NoiseReduction.NoiseProfile? noise = null, SkinMask? skin = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (Preset != null && PresetEnabled) LrPresetApplier.Apply(image, Preset, PresetStrength, wholeFrameEffects, detail, photoLongSide, noise, skin);
        if (Lut != null && LutEnabled) LutApplier.Apply(image, Lut, LutStrength);
    }

    // Returns a graded copy.
    public Image<Rgb24> Render(Image<Rgb24> source, SkinMask? skin = null)
    {
        var copy = source.Clone();
        Apply(copy, skin: skin);
        return copy;
    }
}
