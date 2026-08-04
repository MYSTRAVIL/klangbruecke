namespace Klangbruecke.Diagnostics;

/// <summary>Discards everything. The default until <see cref="Log.Current"/> is set at startup.</summary>
public sealed class NullLog : ILog
{
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
    }
}

/// <summary>
/// Ambient log. A static facade rather than injected dependencies because the call sites are
/// WinRT and NAudio event handlers that this app does not construct.
/// </summary>
public static class Log
{
    private static ILog _current = new NullLog();

    /// <summary>
    /// Settable so tests can swap in a recording fake.
    ///
    /// No lock: the writes this races with are reads of a reference field, which are atomic, so no
    /// caller can observe a half-assigned log. Nor a stale one - the app's single real assignment
    /// happens in Main before the WinRT and NAudio threads that log exist, and starting a thread
    /// publishes everything written before it.
    /// </summary>
    public static ILog Current
    {
        get => _current;

        // Coerced rather than rejected. Nullable annotations already bar null from this codebase,
        // so this only catches a caller that overrode them - but throwing here would make
        // configuring the log the one logging call that can kill the app, and the whole point of
        // FileLog's never-throw contract is that no later Log.Error can.
        set => _current = value ?? new NullLog();
    }

    public static void Info(string message) => Current.Write(LogLevel.Info, message);

    public static void Warn(string message) => Current.Write(LogLevel.Warn, message);

    public static void Error(string message, Exception? exception = null)
        => Current.Write(LogLevel.Error, message, exception);
}
