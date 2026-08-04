using System.Text;

namespace Klangbruecke.Diagnostics;

/// <summary>
/// Rolling per-day log file.
///
/// The app has no console and no window, and the tray tooltip is overwritten by every status
/// change. This file is the only diagnostic surface that survives a failure, so it must never
/// throw: a logging fault must not take down the audio bridge.
/// </summary>
public sealed class FileLog : ILog
{
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    public FileLog(string directory, int retentionDays = 7, Func<DateTimeOffset>? clock = null)
    {
        _directory = directory;
        _retentionDays = retentionDays;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Klangbruecke",
        "logs");

    public static string FileNameFor(DateTimeOffset day) => $"klangbruecke-{day:yyyyMMdd}.log";

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        try
        {
            DateTimeOffset now = _clock();
            string line = Format(now, level, message, exception);

            // Status arrives from the WinRT threadpool and from NAudio callbacks, so writes race.
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(Path.Combine(_directory, FileNameFor(now)), line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Deliberate. Logging must never be the reason the app fails.
        }
    }

    public static string Format(DateTimeOffset now, LogLevel level, string message, Exception? exception)
    {
        string tag = level switch
        {
            LogLevel.Info => "INF",
            LogLevel.Warn => "WRN",
            _ => "ERR",
        };

        string line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] {message}";

        return exception is null
            ? line
            : $"{line}{Environment.NewLine}    {exception.GetType().Name}: {exception.Message}";
    }
}
