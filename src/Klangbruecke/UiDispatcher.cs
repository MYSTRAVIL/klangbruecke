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

        // Forces handle creation. Without a handle there is nothing to marshal to.
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
