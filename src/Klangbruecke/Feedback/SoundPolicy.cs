using Klangbruecke.Connection;

namespace Klangbruecke.Feedback;

/// <summary>
/// Which sound (if any) a connection-state transition earns. Pure, so the transition table is pinned
/// by tests - the same shape as <see cref="TrayIconPolicy"/> for the glyph. Fires only on the edges a
/// user cares about; silent on the Discovering/Connecting/RetryBackoff churn so it is not chatty.
/// </summary>
public static class SoundPolicy
{
    public static SoundEvent? For(ConnectionState previous, ConnectionState next)
    {
        // Came fully up.
        if (next == ConnectionState.Connected && previous != ConnectionState.Connected)
        {
            return SoundEvent.Connected;
        }

        // A half dropped from a full bridge (not a partial initial connect).
        if (next == ConnectionState.Degraded && previous == ConnectionState.Connected)
        {
            return SoundEvent.Degraded;
        }

        // The bridge was lost - left a delivering state for one that is not.
        bool wasDelivering = previous is ConnectionState.Connected or ConnectionState.Degraded;
        bool stillDelivering = next is ConnectionState.Connected or ConnectionState.Degraded;
        if (wasDelivering && !stillDelivering)
        {
            return SoundEvent.Disconnected;
        }

        return null;
    }
}
