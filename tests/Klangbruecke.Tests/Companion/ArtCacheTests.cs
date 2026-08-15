using Klangbruecke.Companion;
using Xunit;

namespace Klangbruecke.Tests.Companion;

public sealed class ArtCacheTests
{
    [Fact]
    public void Miss_BeforePut()
    {
        var cache = new ArtCache();
        Assert.False(cache.TryGet("h", out _));
    }

    [Fact]
    public void Hit_AfterPut()
    {
        var cache = new ArtCache();
        cache.Put("h", new byte[] { 1, 2, 3 });
        Assert.True(cache.TryGet("h", out byte[] bytes));
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    [Fact]
    public void EvictsOldest_BeyondCapacity()
    {
        var cache = new ArtCache(capacity: 2);
        cache.Put("a", new byte[] { 1 });
        cache.Put("b", new byte[] { 2 });
        cache.Put("c", new byte[] { 3 }); // evicts "a"
        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
    }
}
