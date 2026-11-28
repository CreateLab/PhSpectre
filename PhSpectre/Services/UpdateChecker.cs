using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PhSpectre.Services;

public enum UpdateStatus { UpToDate, UpdateAvailable, Unknown }

public sealed record UpdateCheckResult(UpdateStatus Status, Version? Version = null, string? Url = null)
{
    public static readonly UpdateCheckResult Unknown = new(UpdateStatus.Unknown);
    public static readonly UpdateCheckResult UpToDate = new(UpdateStatus.UpToDate);
}

// Comparison is separate from the network call for testing. Any failure gives Unknown, which callers ignore.
public static class UpdateChecker
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/CreateLab/PhSpectre/releases/latest";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private static readonly Lazy<HttpClient> HttpClientLazy = new(() =>
    {
        var client = new HttpClient { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PhSpectre-UpdateChecker", "1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    });

    public static UpdateStatus Compare(Version current, Version release) =>
        release > current ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate;

    public static bool TryParseTag(string? tagName, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(tagName)) return false;
        var trimmed = tagName[0] is 'v' or 'V' ? tagName[1..] : tagName;
        return Version.TryParse(trimmed, out version);
    }

    public static async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await HttpClientLazy.Value.GetAsync(LatestReleaseUrl, cancellationToken);
            if (!response.IsSuccessStatusCode) return UpdateCheckResult.Unknown;

            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken);
            if (!TryParseTag(release?.TagName, out var releaseVersion) || releaseVersion is null)
                return UpdateCheckResult.Unknown;

            return Compare(currentVersion, releaseVersion) == UpdateStatus.UpdateAvailable
                ? new UpdateCheckResult(UpdateStatus.UpdateAvailable, releaseVersion, release!.HtmlUrl)
                : UpdateCheckResult.UpToDate;
        }
        catch
        {
            return UpdateCheckResult.Unknown;
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    }
}
