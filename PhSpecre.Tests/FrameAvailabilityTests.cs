using PhSpectre.Rendering;
using Xunit;

namespace PhSpecre.Tests;

public class FrameAvailabilityTests
{
    [Theory]
    [InlineData(FrameType.None)]
    [InlineData(FrameType.Border)]
    [InlineData(FrameType.Card)]
    public void OtherTypes_AlwaysAvailable(FrameType t)
    {
        Assert.True(FrameAvailability.Check(t, false, true).Available);
        Assert.True(FrameAvailability.Check(t, true, false).Available);
    }

    [Fact] public void Recipe_WithExifNoLook_Available() =>
        Assert.True(FrameAvailability.Check(FrameType.RecipeCard, true, false).Available);

    [Fact] public void Recipe_NoExif_Unavailable()
    {
        var r = FrameAvailability.Check(FrameType.RecipeCard, false, false);
        Assert.False(r.Available);
        Assert.Contains("recipe", r.Reason);
    }

    [Fact] public void Recipe_WithLook_Unavailable() =>
        Assert.False(FrameAvailability.Check(FrameType.RecipeCard, true, true).Available);

    [Fact] public void Resolve_FallsBackToCard()
    {
        Assert.Equal(FrameType.Card, FrameAvailability.Resolve(FrameType.RecipeCard, true, true));
        Assert.Equal(FrameType.RecipeCard, FrameAvailability.Resolve(FrameType.RecipeCard, true, false));
        Assert.Equal(FrameType.Border, FrameAvailability.Resolve(FrameType.Border, false, true));
    }
}
