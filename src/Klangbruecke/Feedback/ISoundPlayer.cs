namespace Klangbruecke.Feedback;

/// <summary>Plays a short chime for a <see cref="SoundEvent"/>. Never throws.</summary>
public interface ISoundPlayer
{
    void Play(SoundEvent e);
}
