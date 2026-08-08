using Klangbruecke.Feedback;
using Xunit;

namespace Klangbruecke.Tests.Feedback;

/// <summary>
/// Tests for <see cref="SoundPlayer"/>, verifying that the embedded chimes are resolved correctly
/// from the assembly manifest.
/// </summary>
public sealed class SoundPlayerTests
{
    [Fact]
    public void Constructing_resolves_every_chime_without_throwing()
    {
        // Regression: a bare-suffix manifest match made "connect.wav" match "disconnect.wav" too,
        // so Load(..).Single() threw at construction and the app died at startup.
        Assert.Null(Record.Exception(() => new SoundPlayer()));
    }
}
