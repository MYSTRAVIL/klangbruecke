using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Klangbruecke.App;

public sealed class GitHubReleaseFeed : IReleaseFeed
{
    // The list, not /releases/latest: 0.x builds ship as prereleases and /latest omits them (404).
    private const string ReleasesUrl = "https://api.github.com/repos/MYSTRAVIL/klangbruecke/releases";

    private readonly HttpClient _http;

    public GitHubReleaseFeed(HttpClient http)
    {
        _http = http;
        // GitHub rejects a request with no User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Klangbruecke");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<ReleaseInfo?> GetLatestReleaseAsync()
    {
        using System.IO.Stream stream = await _http.GetStreamAsync(ReleasesUrl);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream);

        // GitHub returns releases newest-first; take the first with a string tag.
        foreach (JsonElement release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("tag_name", out JsonElement tag) && tag.ValueKind == JsonValueKind.String)
            {
                string url = release.TryGetProperty("html_url", out JsonElement html)
                             && html.ValueKind == JsonValueKind.String
                    ? html.GetString()!
                    : "https://github.com/MYSTRAVIL/klangbruecke/releases";

                return new ReleaseInfo(tag.GetString()!, url);
            }
        }

        return null;
    }
}
