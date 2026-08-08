using System;
using System.Threading.Tasks;

namespace Klangbruecke.App;

/// <summary>
/// Compares the running version against the newest GitHub release. All failure - offline, HTTP error,
/// no releases, an unparseable tag - becomes <see cref="UpdateStatus.Failed"/>; this never throws, so
/// the tray click that calls it cannot crash the app.
/// </summary>
public sealed class UpdateChecker
{
    private readonly IReleaseFeed _feed;
    private readonly Version _current;

    public UpdateChecker(IReleaseFeed feed, Version current)
    {
        _feed = feed;
        _current = current;
    }

    public async Task<UpdateCheckResult> CheckAsync()
    {
        ReleaseInfo? release;
        try
        {
            release = await _feed.GetLatestReleaseAsync();
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failed(ex.Message);
        }

        if (release is null)
        {
            return UpdateCheckResult.Failed("No releases found.");
        }

        if (!TryParseTag(release.Tag, out Version latest))
        {
            return UpdateCheckResult.Failed($"Could not read the release tag '{release.Tag}'.");
        }

        return Normalize(latest) > Normalize(_current)
            ? UpdateCheckResult.Available(latest, release.HtmlUrl)
            : UpdateCheckResult.UpToDate(latest);
    }

    // Tags are v-prefixed semver (packaging/Publish-Release.ps1). Tolerate a missing 'v'.
    internal static bool TryParseTag(string tag, out Version version) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out version!);

    // First three components only: the tag has no fourth part, and the running version's is always 0.
    private static Version Normalize(Version v) => new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
}
