using System;
using Klangbruecke.Diagnostics;
using Xunit;

namespace Klangbruecke.Tests.Diagnostics;

public sealed class DiagnosticsReportTests
{
    [Fact]
    public void Build_includes_version_os_state_and_every_log_line()
    {
        string report = DiagnosticsReport.Build(
            new Version(0, 2, 2, 0),
            "Windows 10.0.19045",
            "Degraded",
            "music retrying in 8s",
            new[] { "line-one", "line-two" });

        Assert.Contains("0.2.2.0", report);
        Assert.Contains("Windows 10.0.19045", report);
        Assert.Contains("Degraded", report);
        Assert.Contains("music retrying in 8s", report);
        Assert.Contains("line-one", report);
        Assert.Contains("line-two", report);
        Assert.Contains("review before sharing", report);
    }

    [Fact]
    public void Build_throws_ArgumentNullException_when_recentLogLines_is_null()
    {
        Assert.Throws<ArgumentNullException>(() => DiagnosticsReport.Build(
            new Version(0, 2, 2, 0),
            "Windows 10.0.19045",
            "Degraded",
            "music retrying in 8s",
            null!));
    }

    [Fact]
    public void Build_with_empty_recentLogLines_still_contains_header_version_os_and_state()
    {
        string report = DiagnosticsReport.Build(
            new Version(0, 2, 2, 0),
            "Windows 10.0.19045",
            "Healthy",
            "all systems connected",
            Array.Empty<string>());

        Assert.Contains("0.2.2.0", report);
        Assert.Contains("Windows 10.0.19045", report);
        Assert.Contains("Healthy", report);
        Assert.Contains("all systems connected", report);
        Assert.Contains("review before sharing", report);
    }
}
