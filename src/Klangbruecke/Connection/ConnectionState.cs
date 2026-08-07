using System.Globalization;

namespace Klangbruecke.Connection;

/// <summary>What the tray reports. Derived, never assigned: see <see cref="ConnectionStateProjection"/>.</summary>
public enum ConnectionState
{
    /// <summary>
    /// Nothing is being attempted - no phone picked, no half switched on, or nothing that is switched
    /// on is doing anything yet. The detail names which of the three.
    /// </summary>
    Idle,

    /// <summary>A phone is picked and is not in the room. A <c>DeviceWatcher</c> is waiting for it.</summary>
    Discovering,

    /// <summary>At least one half is on its way up.</summary>
    Connecting,

    /// <summary>Every half the user asked for is delivering.</summary>
    Connected,

    /// <summary>Some of what the user asked for is delivering and some is not.</summary>
    Degraded,

    /// <summary>Dormant next to a phone it could connect to. The detail says why.</summary>
    Suppressed,

    /// <summary>Nothing is up, and at least one half is counting down to its next attempt.</summary>
    RetryBackoff,
}

/// <summary>The music half's own machine. <c>Off</c> is where it starts and where it returns.</summary>
public enum MusicState
{
    Off,
    Connecting,

    /// <summary>
    /// The Bluetooth connection is open and the WASAPI capture endpoint is not present.
    ///
    /// The normal condition whenever the phone is not streaming, and what a call looks like - a call
    /// invalidates the capture endpoint without closing the connection (docs/FINDINGS.md #3). It is
    /// up, not broken.
    /// </summary>
    Linked,

    /// <summary>Connection open and audio routing.</summary>
    Up,

    Backoff,
}

/// <summary>The calls half's own machine. Not link-scoped: registration survives a range exit.</summary>
public enum CallsState
{
    Off,
    Registering,
    Up,
    Backoff,
}

/// <summary>
/// Everything the projection is allowed to look at, gathered in one turn on the UI thread.
///
/// A value rather than a bag of references so that what the tray reports is a function of one
/// consistent instant. Reading the machines one at a time inside the projection would let the halves
/// move between reads and produce a state no snapshot ever held.
/// </summary>
/// <param name="NextRetryIn">
/// How long until the soonest scheduled attempt, or null when nothing is scheduled yet. Presentation
/// only - <see cref="ConnectionStateProjection.Project"/> ignores it.
/// </param>
/// <param name="ConnectPermitted">
/// May the manager <em>initiate</em> a connect right now?
///
/// Not the same question as the suppression latch, and that is exactly why it is here. The latch is
/// set by a drop; between it clearing and the next drop re-setting it there is a whole window in
/// which auto-reconnect is off, nothing will be attempted, and the three machines look identical to
/// an app that is about to try - so the tray reported <c>Discovering</c> ("waiting for the phone to
/// appear") or <c>RetryBackoff</c> ("retrying in 8s") for a phone nobody was going to fetch. Both are
/// promises, and the user waits on them.
/// </param>
public readonly record struct ConnectionSnapshot(
    bool PhoneSelected,
    SuppressionReason Suppression,
    LinkState Link,
    bool MusicEnabled,
    MusicState Music,
    bool CallsEnabled,
    CallsState Calls,
    TimeSpan? NextRetryIn,
    bool ConnectPermitted);

/// <summary>
/// The seven reported states, derived from the three machines rather than stored beside them.
///
/// This is the keystone of the design. The alternative - one seven-state table that every component
/// writes into - is what makes a tray icon disagree with the OS, because each writer knows only its
/// own half and the last one to speak wins. Here nothing assigns a reported state at all: the link
/// machine, the suppression latch and the two half controllers each know one small thing, and the
/// name the user reads is computed from all four every time it is asked for.
///
/// Pure and total. The tray asks on a timer, and neither it nor the log line beside it can take a
/// throw or a null.
/// </summary>
public static class ConnectionStateProjection
{
    /// <summary>
    /// First match wins. The order is the behaviour: the four rules that can contend are the four at
    /// the top, and each of them describes a fact that outranks everything below it - there is no
    /// phone, so nothing else matters; the app is deliberately dormant, so a half's leftover state is
    /// not news; the phone is not in the room, so no half can be delivering whatever it believes; a
    /// half is on its way up, which beats reporting the app as broken while it fixes itself.
    /// </summary>
    public static ConnectionState Project(ConnectionSnapshot snapshot)
    {
        // 1. No phone selected.
        if (!snapshot.PhoneSelected)
        {
            return ConnectionState.Idle;
        }

        // 2. Suppressed, either reason. An unrecognised value is not None, and the safe reading of
        // "something suppressed us" is dormant rather than connected.
        if (snapshot.Suppression != SuppressionReason.None)
        {
            return ConnectionState.Suppressed;
        }

        // A disabled half is not attempted, so it can neither be up nor be failing. Every question
        // below is asked of the enabled halves only - that is what keeps "calls switched off" and
        // "no MSIX package identity" out of Degraded, and an unpackaged development run out of a
        // permanent false alarm.
        bool anyEnabled = snapshot.MusicEnabled || snapshot.CallsEnabled;

        bool musicUp = snapshot.MusicEnabled && snapshot.Music is MusicState.Linked or MusicState.Up;
        bool callsUp = snapshot.CallsEnabled && snapshot.Calls == CallsState.Up;

        bool musicConnecting = snapshot.MusicEnabled && snapshot.Music == MusicState.Connecting;
        bool callsConnecting = snapshot.CallsEnabled && snapshot.Calls == CallsState.Registering;

        bool musicBackoff = snapshot.MusicEnabled && snapshot.Music == MusicState.Backoff;
        bool callsBackoff = snapshot.CallsEnabled && snapshot.Calls == CallsState.Backoff;

        // Nothing a half believes about itself counts while the phone is out of the room - that is
        // rule 3's claim, and rules 2b and 3 have to agree about it or the pair contradict each
        // other on the same snapshot.
        bool anythingLive =
            snapshot.Link != LinkState.Absent
            && (musicUp || callsUp || musicConnecting || callsConnecting);

        // 2b. Nothing is delivering, nothing is on its way, and nothing may be started. The app is
        // dormant because of a setting, and the three states this would otherwise report -
        // Discovering, RetryBackoff, and the Idle whose detail reads "nothing is running yet" - are
        // each a promise that something is about to happen. A user who is told the app is looking for
        // their phone waits; a user who is told auto-reconnect is off goes and turns it on.
        //
        // Below the latch, not above it: a deliberate Disconnect also fails this test, and its detail
        // says how it ends, which this one cannot.
        if (anyEnabled && !snapshot.ConnectPermitted && !anythingLive)
        {
            return ConnectionState.Suppressed;
        }

        // 3. The phone is not in the room. Only Absent: NoPhone means the link machine has not been
        // asked yet, and a value the enum does not define is not evidence the phone has left - in
        // both cases the halves' own observations are the better witness. LinkMachine already
        // collapses a failed status read to Absent, so this rule sees a real answer.
        if (snapshot.Link == LinkState.Absent)
        {
            return ConnectionState.Discovering;
        }

        // 4. Something is on its way up.
        if (musicConnecting || callsConnecting)
        {
            return ConnectionState.Connecting;
        }

        // 5. Everything the user asked for is delivering. The anyEnabled guard is load-bearing:
        // "every enabled half is up" is vacuously true when no half is enabled, and without it the
        // unpackaged-with-music-off run would report a Connected app that is running nothing.
        bool everyEnabledUp =
            (!snapshot.MusicEnabled || musicUp) && (!snapshot.CallsEnabled || callsUp);

        if (anyEnabled && everyEnabledUp)
        {
            return ConnectionState.Connected;
        }

        // 6. Something is up and something is not. The plan's rule reads "at least one enabled half
        // up and at least one in Backoff"; this is that rule with the Backoff requirement dropped,
        // because an enabled half sitting in Off - the state every half starts in, and returns to
        // while a gate is closed - is equally not delivering, and the plan's eight rules have no
        // answer for it at all. Keeping the narrow rule and adding a separate fallback underneath
        // would have been two branches agreeing on every input, so neither could be broken without
        // the other covering for it.
        if (musicUp || callsUp)
        {
            return ConnectionState.Degraded;
        }

        // 7. Nothing is up, but something is still counting down. Same widening, same reason: the
        // plan's "every enabled half in Backoff" plus the case where the other enabled half is Off.
        if (musicBackoff || callsBackoff)
        {
            return ConnectionState.RetryBackoff;
        }

        // 8. Nothing enabled, or nothing enabled is doing anything. Idle rather than Connected, and
        // the detail names which of the two it is.
        return ConnectionState.Idle;
    }

    /// <summary>The short phrase the tray shows after the state name. Never null, never empty.</summary>
    /// <remarks>
    /// Derived from the projected state rather than alongside it, so the two cannot disagree: there is
    /// no arrangement of the snapshot that produces one state's name and another state's explanation.
    /// The phrases are short on purpose - <c>StatusPresenter</c> truncates the composed tooltip at 96
    /// characters, and every one of these puts its payload at the end.
    /// </remarks>
    public static string DetailFor(ConnectionSnapshot snapshot) => Project(snapshot) switch
    {
        ConnectionState.Idle => IdleDetail(snapshot),
        ConnectionState.Discovering => "waiting for the phone to appear",
        ConnectionState.Connecting => ConnectingDetail(snapshot),
        ConnectionState.Connected => ConnectedDetail(snapshot),
        ConnectionState.Degraded => DegradedDetail(snapshot),
        ConnectionState.Suppressed => SuppressedDetail(snapshot),
        ConnectionState.RetryBackoff => RetryPhrase(snapshot.NextRetryIn),

        // Unreachable while Project returns one of the seven, and required anyway: a switch
        // expression over an enum is never exhaustive to the compiler. An eighth state added later
        // gets a bare phrase rather than inheriting whichever arm happened to sit last.
        _ => "state unknown",
    };

    // Three different silences, and the user cannot act on the right one without being told which.
    private static string IdleDetail(ConnectionSnapshot snapshot)
    {
        if (!snapshot.PhoneSelected)
        {
            return "no phone selected";
        }

        // The unpackaged development run with calls switched off. "Idle" on its own next to a phone
        // sitting right there reads as a bug in the app rather than as its configuration, and naming
        // both halves is what tells the user which switch to go and find.
        if (!snapshot.MusicEnabled && !snapshot.CallsEnabled)
        {
            return "music and calls are both off";
        }

        return "nothing is running yet";
    }

    private static string ConnectingDetail(ConnectionSnapshot snapshot)
    {
        bool music = snapshot.MusicEnabled && snapshot.Music == MusicState.Connecting;
        bool calls = snapshot.CallsEnabled && snapshot.Calls == CallsState.Registering;

        if (music && calls)
        {
            return "connecting music, registering calls";
        }

        return music ? "connecting music" : "registering calls";
    }

    private static string ConnectedDetail(ConnectionSnapshot snapshot)
    {
        // The one phrase in here that a user cannot work out for themselves. Connected with the music
        // half Linked means the connection is open and the phone simply is not sending anything -
        // press play, or take the call that is already using the endpoint. Without this the state
        // would be indistinguishable from music actually routing.
        if (snapshot.MusicEnabled && snapshot.Music == MusicState.Linked)
        {
            return "waiting for phone audio";
        }

        if (snapshot.MusicEnabled && snapshot.CallsEnabled)
        {
            return "music and calls up";
        }

        return snapshot.MusicEnabled ? "music up" : "calls up";
    }

    // Names the half that is missing, not the one that works. "Degraded" alone leaves the user to
    // discover which half they have lost by trying it.
    private static string DegradedDetail(ConnectionSnapshot snapshot)
    {
        bool musicDown = snapshot.MusicEnabled && snapshot.Music is not (MusicState.Linked or MusicState.Up);

        if (musicDown)
        {
            return snapshot.Music == MusicState.Backoff
                ? $"music {RetryPhrase(snapshot.NextRetryIn)}"
                : "music is not running";
        }

        return snapshot.Calls == CallsState.Backoff
            ? $"calls {RetryPhrase(snapshot.NextRetryIn)}"
            : "calls are not running";
    }

    private static string SuppressedDetail(ConnectionSnapshot snapshot) => snapshot.Suppression switch
    {
        // Says what ends it. A deliberate disconnect expires when the phone leaves and comes back,
        // which is the one thing the user would otherwise have no way of learning.
        SuppressionReason.Deliberate => "disconnected until the phone leaves and returns",

        SuppressionReason.AutoReconnectOff => "auto-reconnect is off",

        // Rule 2b, which is the only way Suppressed is reached with nothing latched. Connect
        // permission is withheld for exactly one reason, so naming it is a statement of fact rather
        // than the guess the default arm below refuses to make.
        SuppressionReason.None => "auto-reconnect is off",

        // Covers any reason added later - None has had its own arm since rule 2b landed. Deliberately
        // borrows neither phrase above: both are claims about how the dormancy ends, and guessing
        // wrong sends the user to wait for something that will not happen.
        _ => "not reconnecting",
    };

    private static string RetryPhrase(TimeSpan? nextRetryIn)
    {
        // A half is in Backoff from the instant its attempt fails, which is before the retry has been
        // scheduled. No number is better than a made-up one.
        if (nextRetryIn is not { } delay)
        {
            return "retrying shortly";
        }

        // Rounded up, so a retry 7.4 s away reads 8s and counts down. Truncation would show 7s for a
        // whole second and then 0s for the last one, which reads as a countdown that has stuck.
        double seconds = Math.Ceiling(delay.TotalSeconds);

        // Invariant, as FileLog does: this is a number the app formats for itself, and the tests pin
        // the exact string while FileLogTests swaps the current culture out from under them.
        return seconds <= 0
            ? "retrying now"
            : string.Create(CultureInfo.InvariantCulture, $"retrying in {seconds:0}s");
    }
}
