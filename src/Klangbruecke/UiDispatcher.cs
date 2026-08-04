namespace Klangbruecke;

public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Runs inline. For tests and for any context that is already on the UI thread.</summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>
/// Marshals onto the UI thread via a hidden control's handle.
///
/// A control rather than SynchronizationContext.Current: this is constructed before any real
/// control exists, so WinForms has not installed its context yet and Current would be null.
/// </summary>
public sealed class ControlUiDispatcher : IUiDispatcher, IDisposable
{
    private readonly Control _marshaller;

    public ControlUiDispatcher()
    {
        _marshaller = new Control();

        // Control defaults to visible. In practice WinForms parks a parentless handle-created
        // control on its hidden tool window, so this never reaches the screen either way - but
        // this app must never show a window, and that is too load-bearing a guarantee to rest on
        // an implementation detail of where WinForms happens to park things. Cleared before the
        // handle exists, so WS_VISIBLE is never in the created window's style at all.
        _marshaller.Visible = false;

        // Forces handle creation. Without a handle there is nothing to marshal to.
        //
        // It has to be this and not the more obvious CreateControl(): that call is gated on
        // visibility, so with the line above it returns having done nothing, and this class then
        // silently degrades to running every action inline on the caller's thread - the exact bug it
        // exists to fix, with no test failure to show for it.
        _ = _marshaller.Handle;
    }

    public void Post(Action action)
    {
        // Disposal destroys the handle, after which InvokeRequired reports false and the action
        // would run inline on whichever background thread posted it - the bug this class removes.
        if (_marshaller.IsDisposed)
        {
            return;
        }

        if (!_marshaller.InvokeRequired)
        {
            // Outside the catch below: swallowing a failed tray write would hide exactly the
            // exception this dispatcher exists to eliminate. On this path the caller is the UI
            // thread, so it surfaces through Application.ThreadException like any other handler.
            action();
            return;
        }

        try
        {
            _marshaller.BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // Raced with shutdown.
        }
        catch (InvalidOperationException)
        {
            // Handle went away between the check and the call.
        }
    }

    /// <summary>
    /// Drops any queued actions: destroying the handle completes them without running them, so a
    /// caller that disposes this before the objects its actions touch cannot be reentered.
    /// </summary>
    public void Dispose() => _marshaller.Dispose();
}
