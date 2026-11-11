namespace PhSpectre.Rendering;

// JPEG quality used by every export (Edit and the card renderers), set from the one Export tab.
public static class ExportDefaults
{
    public static int JpegQuality { get; set; } = 95;
}
