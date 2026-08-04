using System.Diagnostics;
using System.Globalization;
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
    private const string DetailIndent = "    ";
    private const string FileNamePrefix = "klangbruecke-";
    private const string FileNameExtension = ".log";

    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private string? _prunedForFile;

    public FileLog(string directory, int retentionDays = 7, Func<DateTimeOffset>? clock = null)
    {
        _directory = directory;
        // Floored at one day: a zero window puts the cutoff on today, so the sweep would delete the
        // very file the write is about to append to, discarding the day's earlier lines with it.
        _retentionDays = Math.Max(1, retentionDays);
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Klangbruecke",
        "logs");

    // Invariant culture: the file name is a machine-readable contract that the retention sweep
    // globs and parses. Under a non-Gregorian calendar 'yyyy' renders as some other year.
    public static string FileNameFor(DateTimeOffset day) =>
        string.Create(CultureInfo.InvariantCulture, $"{FileNamePrefix}{day:yyyyMMdd}{FileNameExtension}");

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        try
        {
            // Status arrives from the WinRT threadpool and from NAudio callbacks, so writes race.
            // The stamp is taken inside the lock because Monitor is not FIFO: stamped outside, a
            // later line can win the lock first and land above an earlier one, misordering the
            // connect/disconnect sequence a reader is trying to reconstruct top-to-bottom.
            lock (_gate)
            {
                DateTimeOffset now = _clock();
                string line = Format(now, level, message, exception);

                Directory.CreateDirectory(_directory);

                string fileName = FileNameFor(now);

                if (_prunedForFile != fileName)
                {
                    // The day is claimed before the sweep and the sweep has its own catch, so a
                    // failing sweep can neither cost this line nor make every later write retry it.
                    // With that settled, sweeping before the append is the better order: on a full
                    // disk the append is what fails, and freeing space first is the only chance it
                    // has of succeeding.
                    _prunedForFile = fileName;

                    try
                    {
                        Prune(now);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine(ex);
                    }
                }

                File.AppendAllText(Path.Combine(_directory, fileName), line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            // Deliberate. Logging must never be the reason the app fails.
            // If the directory is permanently unwritable, nothing is ever written and there is no
            // other signal that anything was attempted. Trace, not Debug: Debug.WriteLine is
            // [Conditional("DEBUG")] and so compiles out of the Release build that ships, which
            // would leave this catch empty exactly where the signal is needed. TRACE is defined in
            // Release, and DefaultTraceListener forwards to OutputDebugString, so this is readable
            // under DebugView on the target machine. The default listener will not throw; a
            // pathological Exception.ToString() override could, which is why this is the last
            // statement of a catch that is already the never-throw boundary.
            Trace.WriteLine(ex);
        }
    }

    /// <summary>
    /// Runs once per day rather than per write. Dates come from the file name, not the
    /// filesystem timestamp, so a file touched by a backup tool is not given a reprieve.
    /// </summary>
    private void Prune(DateTimeOffset now)
    {
        // Dates compared to dates, never to an instant. A name carries a day and no time, so
        // measuring it against a wall-clock 'now' would hand the boundary file's fate to the hour
        // the day's first write happened to land on and to the machine's UTC offset - retaining a
        // day less than promised on a westward offset.
        //
        // The window is a file count, not a comparison: _retentionDays files survive, today and the
        // days before it, so that 'retentionDays: 7' leaves the seven files it reads as. Stated in
        // files because that is the durable contract - the comparison below is just how it is met.
        DateOnly oldestKept = DateOnly.FromDateTime(now.Date).AddDays(1 - _retentionDays);

        foreach (string path in Directory.EnumerateFiles(_directory, FileNamePrefix + "*" + FileNameExtension))
        {
            string stamp = Path.GetFileNameWithoutExtension(path)[FileNamePrefix.Length..];

            // Invariant, pairing FileNameFor: parsing under CurrentCulture would reject the names
            // this app writes on any non-Gregorian calendar, and retention would silently stop.
            if (!DateOnly.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateOnly day)
                || day >= oldestKept)
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held open by a backup tool or another instance. Skipping it must not abandon the
                // rest of the sweep, or one stuck file shelters every older one behind it forever.
                Trace.WriteLine(ex);
            }
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

        // Invariant culture: in a custom format string ':' is a placeholder substituted from
        // DateTimeFormatInfo.TimeSeparator, so CurrentCulture can silently break the sortable stamp.
        string line = string.Create(CultureInfo.InvariantCulture, $"{now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] {message}");

        if (exception is null)
        {
            return line;
        }

        // ToString() rather than type-plus-message: a failed WinRT async op renders as
        // "AggregateException: One or more errors occurred." and the cause lives entirely in the
        // inner exception and the stack. Indented so the detail stays one visually distinct block
        // hanging off its message line.
        string detail = DetailIndent + exception.ToString().ReplaceLineEndings(Environment.NewLine + DetailIndent);

        return line + Environment.NewLine + detail;
    }
}
