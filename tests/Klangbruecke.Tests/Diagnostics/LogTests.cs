using Klangbruecke.Diagnostics;
using Xunit;

namespace Klangbruecke.Tests.Diagnostics;

public sealed class RecordingLog : ILog
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public void Write(LogLevel level, string message, Exception? exception = null)
        => Entries.Add((level, message, exception));
}

public sealed class LogTests : IDisposable
{
    private readonly ILog _original = Log.Current;
    private readonly RecordingLog _recording = new();

    public LogTests() => Log.Current = _recording;

    [Fact]
    public void Info_RoutesAtInfoLevel()
    {
        Log.Info("connected");

        Assert.Equal((LogLevel.Info, "connected"), (_recording.Entries[0].Level, _recording.Entries[0].Message));
    }

    [Fact]
    public void Warn_RoutesAtWarnLevel()
    {
        Log.Warn("no transport matched");

        Assert.Equal(LogLevel.Warn, _recording.Entries[0].Level);
    }

    [Fact]
    public void Error_CarriesTheException()
    {
        var boom = new InvalidOperationException("no endpoint");

        Log.Error("routing failed", boom);

        Assert.Equal(LogLevel.Error, _recording.Entries[0].Level);
        Assert.Same(boom, _recording.Entries[0].Exception);
    }

    [Fact]
    public void Error_WithoutAnException_StillRoutesTheMessage()
    {
        Log.Error("no usable output device");

        Assert.Equal((LogLevel.Error, "no usable output device"), (_recording.Entries[0].Level, _recording.Entries[0].Message));
        Assert.Null(_recording.Entries[0].Exception);
    }

    [Fact]
    public void Writes_ReachTheLogSetMostRecently()
    {
        var replacement = new RecordingLog();
        Log.Current = replacement;

        Log.Info("connected");

        // A cached read of the static would send the line to the log that was current at JIT time.
        Assert.Empty(_recording.Entries);
        Assert.Single(replacement.Entries);
    }

    [Fact]
    public void Current_SetToNull_KeepsWritesFromThrowing()
    {
        Log.Current = null!;

        Assert.Null(Record.Exception(() => Log.Info("nobody is listening")));
    }

    [Fact]
    public void NullLog_AcceptsWritesWithoutThrowing()
    {
        Assert.Null(Record.Exception(() => new NullLog().Write(LogLevel.Error, "nowhere", new Exception("x"))));
    }

    public void Dispose() => Log.Current = _original;
}
