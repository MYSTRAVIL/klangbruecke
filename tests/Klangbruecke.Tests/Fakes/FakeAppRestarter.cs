using Klangbruecke.Platform;

namespace Klangbruecke.Tests.Fakes;

/// <summary>Counts restart requests instead of relaunching the process.</summary>
public sealed class FakeAppRestarter : IAppRestarter
{
    public int RestartCount { get; private set; }

    public void Restart() => RestartCount++;
}
