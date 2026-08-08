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
}
