using System.Threading.Tasks;

namespace Klangbruecke.App;

/// <summary>The newest published release (prereleases included), or null if there are none.</summary>
public interface IReleaseFeed
{
    Task<ReleaseInfo?> GetLatestReleaseAsync();
}

public sealed record ReleaseInfo(string Tag, string HtmlUrl);
