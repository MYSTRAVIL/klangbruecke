using System;
using System.IO;
using Klangbruecke.Diagnostics;
using Xunit;

namespace Klangbruecke.Tests.Diagnostics;

public sealed class LogTailTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("klangbruecke-logtail");
    private static readonly DateTimeOffset Day = new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Delete(recursive: true);

    private void WriteLog(params string[] lines) =>
        File.WriteAllLines(Path.Combine(_dir.FullName, FileLog.FileNameFor(Day)), lines);

    private void WriteLogFor(DateTimeOffset day, params string[] lines) =>
        File.WriteAllLines(Path.Combine(_dir.FullName, FileLog.FileNameFor(day)), lines);

    [Fact]
    public void Returns_the_last_n_lines_in_order()
    {
        WriteLog("one", "two", "three", "four", "five");

        Assert.Equal(new[] { "three", "four", "five" }, LogTail.ReadRecent(_dir.FullName, Day, 3));
    }

    [Fact]
    public void Returns_all_lines_when_fewer_than_n()
    {
        WriteLog("a", "b");

        Assert.Equal(new[] { "a", "b" }, LogTail.ReadRecent(_dir.FullName, Day, 30));
    }

    [Fact]
    public void A_missing_file_is_empty_not_a_throw()
    {
        Assert.Empty(LogTail.ReadRecent(_dir.FullName, Day, 30));
    }

    [Fact]
    public void Spans_the_day_boundary_to_return_the_last_n_lines_across_multiple_days()
    {
        WriteLogFor(Day.AddDays(-1), "y1", "y2");
        WriteLogFor(Day, "t1");

        Assert.Equal(new[] { "y1", "y2", "t1" }, LogTail.ReadRecent(_dir.FullName, Day, 3));
        Assert.Equal(new[] { "y2", "t1" }, LogTail.ReadRecent(_dir.FullName, Day, 2));
    }

    [Fact]
    public void Excludes_files_older_than_maxDaysBack()
    {
        WriteLogFor(Day.AddDays(-10), "old-line");
        WriteLogFor(Day, "new-line");

        var result = LogTail.ReadRecent(_dir.FullName, Day, 30);

        Assert.DoesNotContain("old-line", result);
        Assert.Contains("new-line", result);
    }

    [Fact]
    public void Count_zero_returns_empty()
    {
        WriteLog("a", "b");

        Assert.Empty(LogTail.ReadRecent(_dir.FullName, Day, 0));
    }

    [Fact]
    public void Negative_count_returns_empty()
    {
        WriteLog("a", "b");

        Assert.Empty(LogTail.ReadRecent(_dir.FullName, Day, -5));
    }

    [Fact]
    public void An_existing_but_empty_file_returns_empty()
    {
        WriteLog();

        Assert.Empty(LogTail.ReadRecent(_dir.FullName, Day, 30));
    }
}
