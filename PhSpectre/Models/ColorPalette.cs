namespace PhSpectre.Models;

public sealed class ColorPalette
{
    public IReadOnlyList<ColorSwatch> Swatches { get; }

    public ColorPalette(IReadOnlyList<ColorSwatch> swatches)
    {
        ArgumentNullException.ThrowIfNull(swatches);
        if (swatches.Count == 0)
            throw new ArgumentException("Palette must contain at least one swatch.", nameof(swatches));
        Swatches = swatches;
    }
}
