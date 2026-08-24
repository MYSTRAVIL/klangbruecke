using Klangbruecke.Audio;

namespace Klangbruecke.Tests.Fakes;

/// <summary>The endpoint condition a test wants the watchdog to see, and a count of reads.</summary>
public sealed class FakeSinkEndpointStateProbe : ISinkEndpointStateProbe
{
    public SinkEndpointCondition Condition { get; set; } = SinkEndpointCondition.Active;

    public int ProbeCount { get; private set; }

    public SinkEndpointCondition Probe()
    {
        ProbeCount++;
        return Condition;
    }
}
