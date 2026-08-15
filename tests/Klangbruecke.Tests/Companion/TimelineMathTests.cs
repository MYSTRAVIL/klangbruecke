using Klangbruecke.Companion;
using Xunit;

namespace Klangbruecke.Tests.Companion;

public sealed class TimelineMathTests
{
    [Fact]
    public void Advances_ByElapsedTimesSpeed()
        => Assert.Equal(32000, TimelineMath.PositionAt(30000, TimeSpan.FromSeconds(2), 1.0, 200000));

    [Fact]
    public void Paused_DoesNotAdvance()
        => Assert.Equal(30000, TimelineMath.PositionAt(30000, TimeSpan.FromSeconds(5), 0.0, 200000));

    [Fact]
    public void ClampsToDuration()
        => Assert.Equal(200000, TimelineMath.PositionAt(199000, TimeSpan.FromSeconds(5), 1.0, 200000));

    [Fact]
    public void NoDuration_DoesNotClamp()
        => Assert.Equal(35000, TimelineMath.PositionAt(30000, TimeSpan.FromSeconds(5), 1.0, 0));
}
