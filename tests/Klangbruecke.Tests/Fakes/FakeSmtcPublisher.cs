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

    public bool Disposed { get; private set; }

    public event EventHandler<MediaCommand>? CommandRequested;

    public void Publish(MediaSnapshot snapshot) => Published.Add(snapshot);

    /// <summary>A transport button / media key press. Forwards to whatever the link subscribed.</summary>
    public void RaiseCommand(MediaCommand command) => CommandRequested?.Invoke(this, command);

    public void Dispose() => Disposed = true;
}
