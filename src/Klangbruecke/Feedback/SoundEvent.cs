namespace Klangbruecke.Feedback;

/// <summary>A connection-lifecycle event worth an audible cue. See <see cref="SoundPolicy"/>.</summary>
public enum SoundEvent
{
    Connected,
    Disconnected,
    Degraded,
}
