using System;

namespace Klangbruecke.App;

public enum UpdateStatus { UpToDate, UpdateAvailable, Failed }

public sealed record UpdateCheckResult(UpdateStatus Status, Version? Latest, string? ReleaseUrl, string? Message)
{
    public static UpdateCheckResult UpToDate(Version latest) => new(UpdateStatus.UpToDate, latest, null, null);

    public static UpdateCheckResult Available(Version latest, string url) =>
        new(UpdateStatus.UpdateAvailable, latest, url, null);

    public static UpdateCheckResult Failed(string message) => new(UpdateStatus.Failed, null, null, message);
}
