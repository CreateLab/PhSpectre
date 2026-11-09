using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.ColorGrading;

// An ordered set of grading layers applied to a photo: Lightroom preset first, then LUT. Each layer
// has its own intensity and can be switched off without losing its settings. Pure data + Apply, so it
// can be previewed on a downscaled copy and re-run at full size for export.
public sealed class GradeStack
{
    public LrPreset? Preset { get; set; }
    public float PresetStrength { get; set; } = 1f;
    public bool PresetEnabled { get; set; } = true;

    public CubeLut? Lut { get; set; }
    public float LutStrength { get; set; } = 1f;
    public bool LutEnabled { get; set; } = true;

    public bool IsEmpty =>
        !((Preset != null && PresetEnabled && PresetStrength > 0f) || (Lut != null && LutEnabled && LutStrength > 0f));

    public void Apply(Image<Rgb24> image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (Preset != null && PresetEnabled) LrPresetApplier.Apply(image, Preset, PresetStrength);
        if (Lut != null && LutEnabled) LutApplier.Apply(image, Lut, LutStrength);
    }

    // Returns a graded copy; the source is untouched (non-destructive).
    public Image<Rgb24> Render(Image<Rgb24> source)
    {
        var copy = source.Clone();
        Apply(copy);
        return copy;
    }
}
