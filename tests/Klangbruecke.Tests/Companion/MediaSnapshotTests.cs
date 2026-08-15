using Klangbruecke.Companion;
using Xunit;

namespace Klangbruecke.Tests.Companion;

public sealed class MediaSnapshotTests
{
    [Fact]
    public void Empty_HasNoSession()
    {
        Assert.False(MediaSnapshot.Empty.HasSession);
        Assert.Equal("", MediaSnapshot.Empty.Title);
    }

    [Fact]
    public void Snapshot_RoundTripsFields()
    {
        var s = new MediaSnapshot("T", "A", "Al", IsPlaying: true, HasSession: true);
        Assert.Equal("T", s.Title);
        Assert.True(s.IsPlaying);
        Assert.True(s.HasSession);
    }
}
