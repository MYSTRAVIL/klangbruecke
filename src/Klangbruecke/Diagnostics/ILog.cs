namespace Klangbruecke.Diagnostics;

public enum LogLevel
{
    Info,
    Warn,
    Error,
}

public interface ILog
{
    void Write(LogLevel level, string message, Exception? exception = null);
}
