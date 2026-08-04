using Klangbruecke.Diagnostics;
using Xunit;

namespace Klangbruecke.Tests.Diagnostics;

public sealed class FileLogRetentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kb-ret-" + Guid.NewGuid().ToString("N"));

    private static DateTimeOffset At(int year, int month, int day)
        => new(year, month, day, 12, 0, 0, TimeSpan.Zero);

    private void SeedLogFor(DateTimeOffset day)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, FileLog.FileNameFor(day)), "seeded" + Environment.NewLine);
    }

    [Fact]
    public void Write_OnANewDay_StartsANewFile()
    {
        var now = At(2026, 8, 4);
        var log = new FileLog(_dir, clock: () => now);
        log.Write(LogLevel.Info, "day one");

        now = At(2026, 8, 5);
        log.Write(LogLevel.Info, "day two");

        Assert.Contains("day one", File.ReadAllText(Path.Combine(_dir, "klangbruecke-20260804.log")));
        Assert.Contains("day two", File.ReadAllText(Path.Combine(_dir, "klangbruecke-20260805.log")));
    }

    [Fact]
    public void Write_DeletesFilesOlderThanRetention()
    {
        SeedLogFor(At(2026, 7, 20));   // 15 days before "now"
        var now = At(2026, 8, 4);

        new FileLog(_dir, retentionDays: 7, clock: () => now).Write(LogLevel.Info, "today");

        Assert.False(File.Exists(Path.Combine(_dir, "klangbruecke-20260720.log")));
    }

    [Fact]
    public void Write_KeepsFilesInsideRetention()
    {
        SeedLogFor(At(2026, 8, 1));    // 3 days before "now"
        var now = At(2026, 8, 4);

        new FileLog(_dir, retentionDays: 7, clock: () => now).Write(LogLevel.Info, "today");

        Assert.True(File.Exists(Path.Combine(_dir, "klangbruecke-20260801.log")));
    }

    [Fact]
    public void Write_RetainsExactlyRetentionDaysFiles()
    {
        var now = At(2026, 8, 4);
        for (int daysBack = 1; daysBack <= 12; daysBack++)
        {
            SeedLogFor(now.AddDays(-daysBack));
        }

        new FileLog(_dir, retentionDays: 7, clock: () => now).Write(LogLevel.Info, "today");

        // The contract the constant states: seven dated files, today and the six before it.
        Assert.Equal(
            new[]
            {
                "klangbruecke-20260729.log",
                "klangbruecke-20260730.log",
                "klangbruecke-20260731.log",
                "klangbruecke-20260801.log",
                "klangbruecke-20260802.log",
                "klangbruecke-20260803.log",
                "klangbruecke-20260804.log",
            },
            Directory.EnumerateFiles(_dir).Select(path => Path.GetFileName(path)!).Order().ToArray());
    }

    [Fact]
    public void Write_KeepsTheOldestFileInsideTheWindow()
    {
        SeedLogFor(At(2026, 7, 29));   // 6 days before "now" - the seventh file
        var now = At(2026, 8, 4);

        new FileLog(_dir, retentionDays: 7, clock: () => now).Write(LogLevel.Info, "today");

        Assert.True(File.Exists(Path.Combine(_dir, "klangbruecke-20260729.log")));
    }

    [Fact]
    public void Write_DeletesTheFirstFileOutsideTheWindow()
    {
        SeedLogFor(At(2026, 7, 28));   // 7 days before "now" - the eighth file, one too many
        var now = At(2026, 8, 4);

        new FileLog(_dir, retentionDays: 7, clock: () => now).Write(LogLevel.Info, "today");

        Assert.False(File.Exists(Path.Combine(_dir, "klangbruecke-20260728.log")));
    }

    [Theory]
    [InlineData(0, 5, 0)]      // just after midnight
    [InlineData(23, 55, 0)]    // just before midnight
    [InlineData(20, 0, -8)]    // an evening on the US west coast
    [InlineData(9, 0, 13)]     // a morning in New Zealand
    public void Write_DrawsTheBoundaryByDate_NotByTimeOfDayOrOffset(int hour, int minute, int offsetHours)
    {
        SeedLogFor(At(2026, 7, 29));   // the oldest day inside the window
        SeedLogFor(At(2026, 7, 28));   // the first day outside it
        var now = new DateTimeOffset(2026, 8, 4, hour, minute, 0, TimeSpan.FromHours(offsetHours));

        new FileLog(_dir, retentionDays: 7, clock: () => now).Write(LogLevel.Info, "today");

        // Whatever the clock reads and wherever the machine sits, the same seven days survive.
        Assert.True(File.Exists(Path.Combine(_dir, "klangbruecke-20260729.log")));
        Assert.False(File.Exists(Path.Combine(_dir, "klangbruecke-20260728.log")));
    }

    [Fact]
    public void Write_PrunesOncePerDay_NotOncePerWrite()
    {
        var now = At(2026, 8, 4);
        var log = new FileLog(_dir, retentionDays: 7, clock: () => now);
        log.Write(LogLevel.Info, "first");

        SeedLogFor(At(2026, 7, 20));
        log.Write(LogLevel.Info, "second");

        // Sweeping the directory on every line would cost a disk enumeration per log statement.
        Assert.True(File.Exists(Path.Combine(_dir, "klangbruecke-20260720.log")));
    }

    [Fact]
    public void Write_PrunesAgainOnTheNextDay_NotOncePerProcess()
    {
        var now = At(2026, 8, 4);
        var log = new FileLog(_dir, retentionDays: 7, clock: () => now);
        log.Write(LogLevel.Info, "first");

        SeedLogFor(At(2026, 7, 20));
        now = At(2026, 8, 5);
        log.Write(LogLevel.Info, "next day");

        // The app runs for weeks between reboots; a sweep that fires once per process never
        // reaches the files that expire while it is running.
        Assert.False(File.Exists(Path.Combine(_dir, "klangbruecke-20260720.log")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Write_WithANonsensicalRetentionWindow_KeepsTheFileItIsWritingTo(int retentionDays)
    {
        SeedLogFor(At(2026, 8, 4));

        new FileLog(_dir, retentionDays, clock: () => At(2026, 8, 4)).Write(LogLevel.Info, "today");

        string text = File.ReadAllText(Path.Combine(_dir, "klangbruecke-20260804.log"));
        Assert.Contains("seeded", text);
        Assert.Contains("today", text);
    }

    [Fact]
    public void Write_IgnoresUnrelatedFilesInTheDirectory()
    {
        Directory.CreateDirectory(_dir);
        string stranger = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(stranger, "not ours");

        new FileLog(_dir, retentionDays: 7, clock: () => At(2026, 8, 4)).Write(LogLevel.Info, "today");

        Assert.True(File.Exists(stranger));
    }

    [Fact]
    public void Write_LeavesLogFilesWhoseNameIsNotADate()
    {
        Directory.CreateDirectory(_dir);
        string undated = Path.Combine(_dir, "klangbruecke-crashdump.log");
        File.WriteAllText(undated, "matches the glob, is not a date");

        new FileLog(_dir, retentionDays: 7, clock: () => At(2026, 8, 4)).Write(LogLevel.Info, "today");

        Assert.True(File.Exists(undated));
        Assert.Contains("today", File.ReadAllText(Path.Combine(_dir, "klangbruecke-20260804.log")));
    }

    [Fact]
    public void Write_SurvivesAnExpiredFileThatCannotBeDeleted()
    {
        SeedLogFor(At(2026, 7, 19));
        SeedLogFor(At(2026, 7, 20));
        SeedLogFor(At(2026, 7, 21));
        string locked = Path.Combine(_dir, "klangbruecke-20260720.log");

        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            new FileLog(_dir, retentionDays: 7, clock: () => At(2026, 8, 4)).Write(LogLevel.Info, "today");
        }

        // The line matters more than the disk space, and one held file must not shelter the rest.
        // Seeded on both sides of the locked file because EnumerateFiles promises no ordering.
        Assert.Contains("today", File.ReadAllText(Path.Combine(_dir, "klangbruecke-20260804.log")));
        Assert.False(File.Exists(Path.Combine(_dir, "klangbruecke-20260719.log")));
        Assert.False(File.Exists(Path.Combine(_dir, "klangbruecke-20260721.log")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
