using PhSpectre.Services;
using Xunit;

namespace PhSpecre.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("1.3.0", "1.2.0", UpdateStatus.UpdateAvailable)] // release newer
    [InlineData("1.2.0", "1.2.0", UpdateStatus.UpToDate)]        // equal
    [InlineData("1.1.0", "1.2.0", UpdateStatus.UpToDate)]        // downgrade — release older than current
    public void Compare_ReturnsExpectedStatus(string releaseVersion, string currentVersion, UpdateStatus expected)
    {
        var release = Version.Parse(releaseVersion);
        var current = Version.Parse(currentVersion);

        var result = UpdateChecker.Compare(current, release);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("V1.2.0", "1.2.0")]
    [InlineData("v2.0", "2.0")]
    public void TryParseTag_ParsesWithAndWithoutVPrefix(string tag, string expectedVersion)
    {
        var parsed = UpdateChecker.TryParseTag(tag, out var version);

        Assert.True(parsed);
        Assert.Equal(Version.Parse(expectedVersion), version);
    }

    [Theory]
    [InlineData("latest-windows")]
    [InlineData("not-a-version")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("v1.2.0-beta")]
    public void TryParseTag_ReturnsFalseForInvalidTags(string? tag)
    {
        var parsed = UpdateChecker.TryParseTag(tag, out var version);

        Assert.False(parsed);
        Assert.Null(version);
    }
}
