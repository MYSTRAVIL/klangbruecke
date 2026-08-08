using System;
using System.Threading.Tasks;
using Klangbruecke.App;
using Xunit;

namespace Klangbruecke.Tests.App;

public sealed class UpdateCheckerTests
{
    private sealed class StubFeed : IReleaseFeed
    {
        private readonly ReleaseInfo? _info;
        private readonly Exception? _error;
        public StubFeed(ReleaseInfo? info) => _info = info;
        public StubFeed(Exception error) => _error = error;
        public Task<ReleaseInfo?> GetLatestReleaseAsync() =>
            _error is not null ? Task.FromException<ReleaseInfo?>(_error) : Task.FromResult(_info);
    }

    [Theory]
    [InlineData("v0.2.3", true)]
    [InlineData("0.2.3", true)]
    [InlineData("v1.0", true)]
    [InlineData("vNope", false)]
    public void TryParseTag_tolerates_the_v_prefix_and_rejects_junk(string tag, bool ok)
    {
        Assert.Equal(ok, UpdateChecker.TryParseTag(tag, out _));
    }

    [Fact]
    public async Task A_newer_release_is_UpdateAvailable()
    {
        var checker = new UpdateChecker(
            new StubFeed(new ReleaseInfo("v0.2.3", "https://example/rel")), new Version(0, 2, 2, 0));

        UpdateCheckResult result = await checker.CheckAsync();

        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(new Version(0, 2, 3), result.Latest);
        Assert.Equal("https://example/rel", result.ReleaseUrl);
    }

    [Fact]
    public async Task The_same_version_is_UpToDate()
    {
        var checker = new UpdateChecker(
            new StubFeed(new ReleaseInfo("v0.2.2", "url")), new Version(0, 2, 2, 0));

        Assert.Equal(UpdateStatus.UpToDate, (await checker.CheckAsync()).Status);
    }

    [Fact]
    public async Task An_older_release_is_UpToDate()
    {
        var checker = new UpdateChecker(
            new StubFeed(new ReleaseInfo("v0.2.1", "url")), new Version(0, 2, 2, 0));

        Assert.Equal(UpdateStatus.UpToDate, (await checker.CheckAsync()).Status);
    }

    [Fact]
    public async Task No_releases_is_Failed()
    {
        var checker = new UpdateChecker(new StubFeed((ReleaseInfo?)null), new Version(0, 2, 2, 0));
        Assert.Equal(UpdateStatus.Failed, (await checker.CheckAsync()).Status);
    }

    [Fact]
    public async Task A_feed_that_throws_is_Failed_not_a_crash()
    {
        var checker = new UpdateChecker(new StubFeed(new Exception("offline")), new Version(0, 2, 2, 0));

        UpdateCheckResult result = await checker.CheckAsync();

        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains("offline", result.Message);
    }

    [Fact]
    public async Task A_malformed_tag_is_Failed()
    {
        var checker = new UpdateChecker(new StubFeed(new ReleaseInfo("banana", "url")), new Version(0, 2, 2, 0));
        Assert.Equal(UpdateStatus.Failed, (await checker.CheckAsync()).Status);
    }
}
