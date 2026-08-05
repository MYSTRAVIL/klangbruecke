using Klangbruecke.Audio;
using Klangbruecke.Diagnostics;

namespace Klangbruecke.Tests.Fakes;

/// <summary>
/// A route that starts, stops, and dies on cue, with no WASAPI underneath it.
///
/// Two of its capabilities exist because the real router has been measured doing exactly this and
/// nothing above it may assume otherwise:
///
/// <list type="bullet">
/// <item><see cref="StartLeavesItRunning"/> - <c>AudioRouter.Start</c> returns <c>true</c> for a
/// capture that died inside <c>StartRecording</c>, because the capture thread dies asynchronously.
/// The bool is advisory; <see cref="IsRunning"/> is the truth.</item>
/// <item><see cref="DieSilently"/> - a route can stop without the stopped event ever arriving, which
/// is the only reason the 30 s reconcile reads <see cref="IsRunning"/> at all.</item>
/// </list>
///
/// <see cref="Stop"/> deliberately raises nothing. The real one does not either: a deliberate
/// teardown that echoed back as an event is how a reconnect loop restarts what a user just switched
/// off.
///
/// Public, in Fakes, and not <c>file</c>-scoped: <c>ConnectionManager</c>'s tests consume it too.
/// </summary>
public sealed class FakeAudioRouter : IAudioRouter
{
    /// <summary>The preferred output id every <see cref="Start"/> was asked for, oldest first.</summary>
    public List<string?> StartCalls { get; } = new();

    public int StopCount { get; private set; }

    public bool Disposed { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>What <see cref="Start"/> returns.</summary>
    public bool StartResult { get; set; } = true;

    /// <summary>
    /// Whether a <see cref="Start"/> that returned true leaves the route actually running. False is
    /// the measured lie described on the class.
    /// </summary>
    public bool StartLeavesItRunning { get; set; } = true;

    public event EventHandler<StatusMessage>? Status { add { } remove { } }

    public event EventHandler? Stopped;

    public bool Start(string? preferredOutputDeviceId)
    {
        StartCalls.Add(preferredOutputDeviceId);
        IsRunning = StartResult && StartLeavesItRunning;
        return StartResult;
    }

    public void Stop()
    {
        StopCount++;
        IsRunning = false;
    }

    /// <summary>
    /// The route dying on its own - a phone out of range, an endpoint that vanished under a call.
    /// <see cref="IsRunning"/> is already false when the event is raised, as the real one guarantees,
    /// so a subscriber cannot re-enter a route that no longer exists.
    /// </summary>
    public void Die()
    {
        IsRunning = false;
        Stopped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The route stopping without saying so. Only a level read can find this one.</summary>
    public void DieSilently() => IsRunning = false;

    public void Dispose() => Disposed = true;
}
