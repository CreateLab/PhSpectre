using PhSpectre.Recipes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace PhSpecre.Tests;

public class RecipeReaderTests
{
    [Fact]
    public void Read_Image_NonFujiExif_ReturnsNull()
    {
        using var image = new Image<Rgb24>(4, 4);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Make, "Canon");

        Assert.Null(RecipeReader.Read(image));
    }

    [Fact]
    public void Read_Image_NoExifProfile_ReturnsNull()
    {
        using var image = new Image<Rgb24>(4, 4);
        Assert.Null(RecipeReader.Read(image));
    }

    [Fact]
    public void Read_Image_FujiExifWithoutMakerNote_ReturnsNull()
    {
        using var image = new Image<Rgb24>(4, 4);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Make, "FUJIFILM");

        // No MakerNote: null, not an exception.
        Assert.Null(RecipeReader.Read(image));
    }
}
