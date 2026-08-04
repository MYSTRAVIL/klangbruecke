using System.Globalization;
using Klangbruecke.Diagnostics;
using Xunit;

namespace Klangbruecke.Tests.Diagnostics;

public sealed class FileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kb-log-" + Guid.NewGuid().ToString("N"));

    private static DateTimeOffset At(int year, int month, int day, int hour = 12)
        => new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Write_AppendsLineToFileNamedForTheDay()
    {
        var clock = At(2026, 8, 4);
        var log = new FileLog(_dir, clock: () => clock);

        log.Write(LogLevel.Info, "A2DP sink connected.");

        string path = Path.Combine(_dir, "klangbruecke-20260804.log");
        Assert.True(File.Exists(path));
        Assert.Contains("A2DP sink connected.", File.ReadAllText(path));
    }

    [Fact]
    public void Write_AppendsRatherThanOverwrites()
    {
        var clock = At(2026, 8, 4);
        var log = new FileLog(_dir, clock: () => clock);

        log.Write(LogLevel.Info, "first");
        log.Write(LogLevel.Info, "second");

        string text = File.ReadAllText(Path.Combine(_dir, "klangbruecke-20260804.log"));
        Assert.Contains("first", text);
        Assert.Contains("second", text);
    }

    [Theory]
    [InlineData(LogLevel.Info, "[INF]")]
    [InlineData(LogLevel.Warn, "[WRN]")]
    [InlineData(LogLevel.Error, "[ERR]")]
    public void Format_TagsTheLevel(LogLevel level, string expectedTag)
    {
        string line = FileLog.Format(At(2026, 8, 4), level, "message", null);

        Assert.Contains(expectedTag, line);
    }

    [Fact]
    public void Format_LeadsWithASortableTimestamp()
    {
        string line = FileLog.Format(At(2026, 8, 4, hour: 9), LogLevel.Info, "message", null);

        Assert.StartsWith("2026-08-04 09:00:00.000", line);
    }

    [Fact]
    public void Format_IncludesExceptionTypeAndMessage()
    {
        string line = FileLog.Format(At(2026, 8, 4), LogLevel.Error, "boom", new InvalidOperationException("no endpoint"));

        Assert.Contains("InvalidOperationException", line);
        Assert.Contains("no endpoint", line);
    }

    [Fact]
    public void Format_IncludesInnerExceptionAndStackTrace()
    {
        string line = FileLog.Format(At(2026, 8, 4), LogLevel.Error, "boom", ThrownWrappedException());

        // The outer AggregateException says only "One or more errors occurred." The cause is the
        // inner exception, and where it happened is only in the stack.
        Assert.Contains("no endpoint", line);
        Assert.Contains("InvalidOperationException", line);
        Assert.Contains(nameof(ThrownWrappedException), line);
    }

    [Fact]
    public void Format_IndentsExceptionDetailBelowTheMessageLine()
    {
        string line = FileLog.Format(At(2026, 8, 4), LogLevel.Error, "boom", ThrownWrappedException());

        string[] lines = line.ReplaceLineEndings("\n").Split('\n');
        Assert.StartsWith("2026-08-04 12:00:00.000 [ERR] boom", lines[0]);
        Assert.True(lines.Length > 1, "the exception detail should occupy its own lines");
        Assert.All(lines.Skip(1), detail => Assert.StartsWith("    ", detail));
    }

    [Fact]
    public void Format_UsesInvariantCulture_NotTheCurrentCultureTimeSeparator()
    {
        var hostile = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        hostile.DateTimeFormat.TimeSeparator = "#";

        string line = WithCulture(hostile, () => FileLog.Format(At(2026, 8, 4, hour: 9), LogLevel.Info, "message", null));

        Assert.StartsWith("2026-08-04 09:00:00.000", line);
    }

    [Fact]
    public void FileNameFor_UsesInvariantCulture_NotTheCurrentCultureCalendar()
    {
        // th-TH defaults to the Buddhist calendar, which renders 2026 as 2569.
        string name = WithCulture(new CultureInfo("th-TH"), () => FileLog.FileNameFor(At(2026, 8, 4)));

        Assert.Equal("klangbruecke-20260804.log", name);
    }

    private static T WithCulture<T>(CultureInfo culture, Func<T> action)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static Exception ThrownWrappedException()
    {
        try
        {
            try
            {
                throw new InvalidOperationException("no endpoint");
            }
            catch (Exception inner)
            {
                throw new AggregateException("One or more errors occurred.", inner);
            }
        }
        catch (Exception thrown)
        {
            return thrown;
        }
    }

    [Fact]
    public void Write_SwallowsFailures_SoLoggingCannotKillTheApp()
    {
        // A path containing a NUL character cannot be created on any Windows volume.
        var log = new FileLog("\0invalid\0", clock: () => At(2026, 8, 4));

        Assert.Null(Record.Exception(() => log.Write(LogLevel.Error, "should not throw")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
