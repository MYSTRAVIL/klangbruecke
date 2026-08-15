using Klangbruecke.Companion;

namespace Klangbruecke.Tests.Fakes;

/// <summary>
/// The PC's media surface with the WinRT interop taken out. Records every snapshot published and lets
/// a test raise a transport command as if a button or media key had been pressed - so
/// <c>CompanionLink</c>'s publish-on-frame and forward-on-command behaviour can be driven with no
/// <c>SystemMediaTransportControls</c> and no hidden window.
///
/// Public, in Fakes, and not <c>file</c>-scoped so later tests (and <c>ConnectionManager</c>'s) can
/// consume it too. Internal rather than public because <see cref="MediaSnapshot"/> and
/// <see cref="MediaCommand"/> are internal - it lives in the test assembly, so internal is visible.
/// </summary>
internal sealed class FakeSmtcPublisher : ISmtcPublisher
{
    /// <summary>Every snapshot handed to <see cref="Publish"/>, oldest first.</summary>
    public List<MediaSnapshot> Published { get; } = new();

    /// <summary>Every timeline handed to <see cref="UpdateTimeline"/>, oldest first.</summary>
    public List<TimelineState> Timelines { get; } = new();

    public bool Disposed { get; private set; }

    public event EventHandler<MediaCommand>? CommandRequested;

    public event EventHandler<long>? SeekRequested;

    public void Publish(MediaSnapshot snapshot) => Published.Add(snapshot);

    public void UpdateTimeline(TimelineState timeline) => Timelines.Add(timeline);

    /// <summary>A transport button / media key press. Forwards to whatever the link subscribed.</summary>
    public void RaiseCommand(MediaCommand command) => CommandRequested?.Invoke(this, command);

    /// <summary>The user scrubbed the seek bar. Forwards to whatever the link subscribed.</summary>
    public void RaiseSeek(long positionMs) => SeekRequested?.Invoke(this, positionMs);

    public void Dispose() => Disposed = true;
}
