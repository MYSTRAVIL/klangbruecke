using System.Collections.Generic;
using Klangbruecke.Feedback;

namespace Klangbruecke.Tests.Fakes;

public sealed class FakeSoundPlayer : ISoundPlayer
{
    public List<SoundEvent> Played { get; } = new();
    public void Play(SoundEvent e) => Played.Add(e);
}
