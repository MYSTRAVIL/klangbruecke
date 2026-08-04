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
        SeedLogFor(At(2026, 7, 20));
        SeedLogFor(At(2026, 7, 21));
        string locked = Path.Combine(_dir, "klangbruecke-20260720.log");

        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            new FileLog(_dir, retentionDays: 7, clock: () => At(2026, 8, 4)).Write(LogLevel.Info, "today");
        }

        // The line matters more than the disk space, and one held file must not shelter the rest.
        Assert.Contains("today", File.ReadAllText(Path.Combine(_dir, "klangbruecke-20260804.log")));
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
