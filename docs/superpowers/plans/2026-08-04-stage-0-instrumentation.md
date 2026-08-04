# Stage 0: Instrumentation and Validation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a first-run failure diagnosable, fix the three known landmines, and find out whether this app can actually connect to the phone.

**Architecture:** Add a rolling file log as the app's only durable diagnostic surface, then fix four specific defects in the existing scaffold: unmarshalled UI updates, silent play-thread failures with no record of the capture/render format pair, transport selection that ignores which phone was chosen, and no graceful degradation when running without MSIX package identity. Logic worth testing is extracted into pure static helpers that need no Bluetooth hardware; the WinRT and WASAPI layers are verified by hand against the OS.

> The second item originally read "a missing resampler". **There is no resampler and there must not be one** — the item was reverted during execution and the reasoning is in the warning at the head of Task 5. Anything below that inserts a `MediaFoundationResampler` is kept only as a record of what was tried.

**Tech Stack:** .NET 8, WinForms (tray only), NAudio 2.2.1, WinRT (`Windows.Media.Audio`, `Windows.ApplicationModel.Calls`), xunit.

**Spec:** `docs/superpowers/specs/2026-08-04-connection-lifecycle-design.md`

## Global Constraints

- Target framework is `net8.0-windows10.0.19041.0`. **Do not raise the minimum.** 19041 is the floor for both `AudioPlaybackConnection` and `PhoneLineTransportDevice`, and the dev machine is 19045.
- ASCII `Klangbruecke` everywhere — folder, namespace, assembly, package identity, display name. **No umlaut anywhere.**
- x64 only. `<Platforms>x64</Platforms>`, `<PlatformTarget>x64</PlatformTarget>`.
- No new NuGet dependencies in `src/Klangbruecke` beyond NAudio 2.2.1. Test-only packages are fine in the test project.
- **Do not implement LAF token generation.** Microsoft removed `PhoneLineTransportDevice` from the Limited Access Feature list. See `docs/FINDINGS.md` §2.
- **Do not propose or add** BTstack, a USB dongle, Zadig/WinUSB rebinding, or 32feet.NET. See `docs/FINDINGS.md` §5.
- Logging must never throw. A logging fault must not take down the audio bridge.
- The app is tray-only. It must never open a window or a console.
- Nullable reference types are enabled. Implicit usings are enabled.
- Comments explain *why*, not *what*. Match the existing scaffold's density — it is sparse and load-bearing.

---

### Task 1: Solution, test project, and the log writer

**Files:**
- Create: `Klangbruecke.sln`
- Create: `tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj`
- Create: `src/Klangbruecke/Diagnostics/ILog.cs`
- Create: `src/Klangbruecke/Diagnostics/FileLog.cs`
- Test: `tests/Klangbruecke.Tests/Diagnostics/FileLogTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Klangbruecke.Diagnostics.LogLevel` (enum: `Info`, `Warn`, `Error`); `Klangbruecke.Diagnostics.ILog` with `void Write(LogLevel level, string message, Exception? exception = null)`; `Klangbruecke.Diagnostics.FileLog : ILog` with constructor `FileLog(string directory, int retentionDays = 7, Func<DateTimeOffset>? clock = null)`, `static string DefaultDirectory`, `static string FileNameFor(DateTimeOffset day)`, and `public static string Format(DateTimeOffset now, LogLevel level, string message, Exception? exception)`.

`Format` is public rather than internal because the test assembly asserts on it directly and there is no `InternalsVisibleTo`.

The spec placed the test project in Stage 1. It moves here because Stage 0 is written test-first and there is nowhere to put a test otherwise.

- [ ] **Step 1: Create the solution and test project**

```powershell
dotnet new sln -n Klangbruecke
dotnet sln add src/Klangbruecke/Klangbruecke.csproj
New-Item -ItemType Directory -Force tests/Klangbruecke.Tests | Out-Null
```

Then create `tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj` with exactly this content:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>
    <SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>
    <!-- The project under test is a WinForms app; referencing it requires this. -->
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Platforms>x64</Platforms>
    <PlatformTarget>x64</PlatformTarget>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Klangbruecke\Klangbruecke.csproj" />
  </ItemGroup>

</Project>
```

Then:

```powershell
dotnet sln add tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj
dotnet restore
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Klangbruecke.Tests/Diagnostics/FileLogTests.cs`:

```csharp
using Klangbruecke.Diagnostics;

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
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj`
Expected: FAIL — compile errors, `The type or namespace name 'Diagnostics' does not exist in the namespace 'Klangbruecke'`.

- [ ] **Step 4: Write the implementation**

Create `src/Klangbruecke/Diagnostics/ILog.cs`:

```csharp
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
```

Create `src/Klangbruecke/Diagnostics/FileLog.cs`:

```csharp
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
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj`
Expected: PASS — 8 passed (the `[Theory]` contributes 3).

- [ ] **Step 6: Commit**

```powershell
git add Klangbruecke.sln tests/ src/Klangbruecke/Diagnostics/
git commit -m "Add test project and rolling file log"
```

---

### Task 2: Log retention

**Files:**
- Modify: `src/Klangbruecke/Diagnostics/FileLog.cs`
- Test: `tests/Klangbruecke.Tests/Diagnostics/FileLogRetentionTests.cs`

**Interfaces:**
- Consumes: `FileLog(string directory, int retentionDays = 7, Func<DateTimeOffset>? clock = null)`, `FileLog.FileNameFor(DateTimeOffset)` from Task 1.
- Produces: no new public API. On the first write of each day, `FileLog` prunes so that exactly `retentionDays` dated files remain (today through today minus `retentionDays` - 1). `retentionDays` is a **file count**, not an age threshold — the ambiguity between those two readings cost a fix round.

- [ ] **Step 1: Write the failing tests**

Create `tests/Klangbruecke.Tests/Diagnostics/FileLogRetentionTests.cs`:

```csharp
using Klangbruecke.Diagnostics;

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

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj --filter FileLogRetentionTests`
Expected: FAIL — `Write_DeletesFilesOlderThanRetention` fails because the old file still exists. `Write_OnANewDay_StartsANewFile` already passes; that is fine, it guards the behaviour Task 1 gave us for free.

- [ ] **Step 3: Write the implementation**

In `src/Klangbruecke/Diagnostics/FileLog.cs`, add a field beside `_gate`:

```csharp
    private string? _prunedForFile;
```

Then, inside the `lock (_gate)` block in `Write`, replace the two existing statements with:

```csharp
                Directory.CreateDirectory(_directory);

                string fileName = FileNameFor(now);
                if (_prunedForFile != fileName)
                {
                    Prune(now);
                    _prunedForFile = fileName;
                }

                File.AppendAllText(Path.Combine(_directory, fileName), line + Environment.NewLine, Encoding.UTF8);
```

And add this private method:

```csharp
    /// <summary>
    /// Runs once per day rather than per write. Dates come from the file name, not the
    /// filesystem timestamp, so a file touched by a backup tool is not given a reprieve.
    /// </summary>
    private void Prune(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now.AddDays(-_retentionDays);

        foreach (string path in Directory.EnumerateFiles(_directory, "klangbruecke-*.log"))
        {
            string stamp = Path.GetFileNameWithoutExtension(path).Replace("klangbruecke-", string.Empty);

            if (DateTimeOffset.TryParseExact(stamp, "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset day)
                && day < cutoff)
            {
                File.Delete(path);
            }
        }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj`
Expected: PASS — 12 passed.

- [ ] **Step 5: Commit**

```powershell
git add src/Klangbruecke/Diagnostics/FileLog.cs tests/Klangbruecke.Tests/Diagnostics/FileLogRetentionTests.cs
git commit -m "Prune log files past the retention window"
```

---

### Task 3: Ambient log facade, wired into startup

**Files:**
- Create: `src/Klangbruecke/Diagnostics/Log.cs`
- Modify: `src/Klangbruecke/Program.cs`
- Test: `tests/Klangbruecke.Tests/Diagnostics/LogTests.cs`

**Interfaces:**
- Consumes: `ILog`, `LogLevel`, `FileLog` from Task 1.
- Produces: `Klangbruecke.Diagnostics.Log` static class with `static ILog Current { get; set; }`, `static void Info(string message)`, `static void Warn(string message)`, `static void Error(string message, Exception? exception = null)`. Also `Klangbruecke.Diagnostics.NullLog : ILog`. **Every later task calls `Log.Info` / `Log.Warn` / `Log.Error`.**

- [ ] **Step 1: Stop xunit running collections in parallel**

`Log.Current` is static, and `LogTests` swaps it. Parallel collections would race on it.

Create `tests/Klangbruecke.Tests/AssemblyInfo.cs`:

```csharp
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Klangbruecke.Tests/Diagnostics/LogTests.cs`:

```csharp
using Klangbruecke.Diagnostics;

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
    public void NullLog_AcceptsWritesWithoutThrowing()
    {
        Assert.Null(Record.Exception(() => new NullLog().Write(LogLevel.Error, "nowhere", new Exception("x"))));
    }

    public void Dispose() => Log.Current = _original;
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj --filter LogTests`
Expected: FAIL — `The name 'Log' does not exist in the current context`.

- [ ] **Step 4: Write the implementation**

Create `src/Klangbruecke/Diagnostics/Log.cs`:

```csharp
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
    public static ILog Current { get; set; } = new NullLog();

    public static void Info(string message) => Current.Write(LogLevel.Info, message);

    public static void Warn(string message) => Current.Write(LogLevel.Warn, message);

    public static void Error(string message, Exception? exception = null)
        => Current.Write(LogLevel.Error, message, exception);
}
```

Then replace `src/Klangbruecke/Program.cs` entirely with:

```csharp
using System.Reflection;
using Klangbruecke.Diagnostics;

namespace Klangbruecke;

internal static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main()
    {
        // A second instance would fight the first for the Bluetooth connection.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Klangbruecke.SingleInstance", out bool isNew);
        if (!isNew)
        {
            return;
        }

        Log.Current = new FileLog(FileLog.DefaultDirectory);
        Log.Info($"Klangbruecke {Assembly.GetExecutingAssembly().GetName().Version} starting.");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception.", e.ExceptionObject as Exception);

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayContext());
        }
        finally
        {
            Log.Info("Klangbruecke exiting.");
        }

        GC.KeepAlive(_singleInstance);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj`
Expected: PASS — 16 passed.

- [ ] **Step 6: Verify the app writes a log**

Run: `dotnet run --project src/Klangbruecke/Klangbruecke.csproj`

The tray icon appears. Right-click it and choose Exit. Then:

```powershell
Get-Content "$env:LOCALAPPDATA\Klangbruecke\logs\klangbruecke-$(Get-Date -Format yyyyMMdd).log"
```

Expected: lines containing `Klangbruecke ... starting.` and `Klangbruecke exiting.`

- [ ] **Step 7: Commit**

```powershell
git add src/Klangbruecke/Diagnostics/Log.cs src/Klangbruecke/Program.cs tests/Klangbruecke.Tests/
git commit -m "Add ambient log facade and wire it into startup"
```

---

### Task 4: Marshal tray updates onto the UI thread

**Files:**
- Create: `src/Klangbruecke/UiDispatcher.cs`
- Modify: `src/Klangbruecke/TrayContext.cs` (fields at 15-21, constructor at 23-50, `SetStatus` at 52-58, `Dispose` at 215-227)
- Test: `tests/Klangbruecke.Tests/UiDispatcherTests.cs`

**Interfaces:**
- Consumes: `Log` from Task 3.
- Produces: `Klangbruecke.IUiDispatcher` with `void Post(Action action)`; `Klangbruecke.ControlUiDispatcher : IUiDispatcher, IDisposable`; `Klangbruecke.ImmediateUiDispatcher : IUiDispatcher`.

`TrayContext.SetStatus` writes `_icon.Text` directly, but it is reached from `AudioPlaybackConnection.StateChanged` (WinRT threadpool) and NAudio's `RecordingStopped`. This surfaces as an intermittent `InvalidOperationException`, not a clean failure.

A hidden `Control` is used rather than `SynchronizationContext.Current`, because `TrayContext` is an `ApplicationContext` and `NotifyIcon` is not a `Control` — nothing has installed a WinForms synchronization context by the time the constructor runs.

- [ ] **Step 1: Write the failing tests**

Create `tests/Klangbruecke.Tests/UiDispatcherTests.cs`:

```csharp
namespace Klangbruecke.Tests;

public sealed class UiDispatcherTests
{
    /// <summary>
    /// WinForms controls want an STA thread. xunit v2 gives tests an MTA thread and has no
    /// built-in way to change that, so the STA thread is created explicitly here rather than
    /// via a custom test framework.
    /// </summary>
    private static void OnStaThread(Action action)
    {
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw captured;
        }
    }

    [Fact]
    public void Immediate_RunsTheActionSynchronously()
    {
        bool ran = false;

        new ImmediateUiDispatcher().Post(() => ran = true);

        Assert.True(ran);
    }

    [Fact]
    public void Control_RunsSynchronouslyOnTheOwningThread()
    {
        OnStaThread(() =>
        {
            using var dispatcher = new ControlUiDispatcher();
            bool ran = false;

            dispatcher.Post(() => ran = true);

            Assert.True(ran);
        });
    }

    [Fact]
    public void Control_DoesNotThrowAfterDisposal()
    {
        OnStaThread(() =>
        {
            var dispatcher = new ControlUiDispatcher();
            dispatcher.Dispose();

            // The action must be dropped, not run: a disposed dispatcher has no UI thread left
            // to marshal onto.
            Assert.Null(Record.Exception(
                () => dispatcher.Post(() => throw new InvalidOperationException("must not run"))));
        });
    }
}
```

**Note on scope:** the cross-thread `BeginInvoke` path is not unit tested — asserting it requires a running message pump, which xunit does not provide. It is verified by hand in Task 9 Step 7: connect, disconnect the phone, and confirm the log records the state change with no `InvalidOperationException`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj --filter UiDispatcherTests`
Expected: FAIL — `The type or namespace name 'ImmediateUiDispatcher' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Klangbruecke/UiDispatcher.cs`:

```csharp
namespace Klangbruecke;

public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Runs inline. For tests and for any context that is already on the UI thread.</summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>
/// Marshals onto the UI thread via a hidden control's handle.
///
/// A control rather than SynchronizationContext.Current: this is constructed before any real
/// control exists, so WinForms has not installed its context yet and Current would be null.
/// </summary>
public sealed class ControlUiDispatcher : IUiDispatcher, IDisposable
{
    private readonly Control _marshaller;

    public ControlUiDispatcher()
    {
        _marshaller = new Control();

        // Forces handle creation. Without a handle there is nothing to marshal to.
        _ = _marshaller.Handle;
    }

    public void Post(Action action)
    {
        if (_marshaller.IsDisposed)
        {
            return;
        }

        try
        {
            if (_marshaller.InvokeRequired)
            {
                _marshaller.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (ObjectDisposedException)
        {
            // Raced with shutdown.
        }
        catch (InvalidOperationException)
        {
            // Handle went away between the check and the call.
        }
    }

    public void Dispose() => _marshaller.Dispose();
}
```

Now modify `src/Klangbruecke/TrayContext.cs`. Add to the `using` block at the top:

```csharp
using Klangbruecke.Diagnostics;
```

Add a field after `private readonly AudioRouter _router = new();` (line 19):

```csharp
    private readonly ControlUiDispatcher _ui = new();
```

Replace the three status subscriptions (lines 27-29) with:

```csharp
        _sink.Status += (_, m) => SetStatus(m);
        _calls.Status += (_, m) => SetStatus(m);
        _router.Status += (_, m) => SetStatus(m);
```

(unchanged — the marshalling belongs inside `SetStatus` so every caller is covered, including future ones).

Replace `SetStatus` (lines 52-58) with:

```csharp
    private void SetStatus(string message)
    {
        Log.Info(message);

        // Reached from the WinRT threadpool and from NAudio callbacks; touching the icon
        // off the UI thread throws intermittently rather than failing cleanly.
        _ui.Post(() =>
        {
            _lastStatus = message;

            // Tray tooltips are capped at 63 characters.
            _icon.Text = message.Length > 60 ? $"Klangbruecke: {message[..57]}..." : $"Klangbruecke: {message}";
        });
    }
```

In `Dispose` (lines 215-227), add `_ui.Dispose();` immediately after `_calls.Dispose();`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj`
Expected: PASS — 19 passed.

- [ ] **Step 5: Commit**

```powershell
git add src/Klangbruecke/UiDispatcher.cs src/Klangbruecke/TrayContext.cs tests/Klangbruecke.Tests/
git commit -m "Marshal tray status updates onto the UI thread"
```

---

### Task 5: Log the capture/render format pair, and stop failing silently

> **WARNING — this task as originally written was wrong and was reverted during execution.**
> Everything below that inserts a `MediaFoundationResampler` is **harmful**; do not re-execute it.
> Verified against decompiled NAudio 2.2.1: `WasapiOut.Init`'s DMO-fallback block is inside
> `if (shareMode == Exclusive)` and never runs here, shared mode already passes
> `SrcDefaultQuality | AutoConvertPcm` to `AudioClient.Initialize`, and
> `MediaFoundationTransform` pulls a fixed one second from a 500 ms buffer — a structural 1 Hz
> chop that destroys half the audio. `RequiresResampling` was additionally a tautology, comparing
> a normalized `IeeeFloat` against a raw `Extensible`.
>
> What shipped instead: `AudioFormatBridge.Differ` (both sides normalized, diagnostic only),
> the capture/render format pair logged **unconditionally** at `Start`, a `PlaybackStopped`
> subscription (nothing had one, so play-thread failures were invisible), and an exception-safe
> `Stop()`. See the corrected spec section and commit `d16f423`.

#### Original text, superseded — kept for the record

**Files:**
- Create: `src/Klangbruecke/Audio/AudioFormatBridge.cs`
- Modify: `src/Klangbruecke/Audio/AudioRouter.cs` (fields at 14-17, `Start` at 62-95, `Stop` at 112-136)
- Test: `tests/Klangbruecke.Tests/Audio/AudioFormatBridgeTests.cs`

**Interfaces:**
- Consumes: `Log` from Task 3.
- Produces: `Klangbruecke.Audio.AudioFormatBridge` static class with `static bool RequiresResampling(WaveFormat capture, WaveFormat output)` and `static string Describe(WaveFormat format)`.

`AudioRouter.Start` builds a `BufferedWaveProvider` from the capture `WaveFormat` and hands it straight to `WasapiOut` in shared mode. If the output endpoint's mix format differs, `Init` throws — currently caught and reported as "Could not start routing" with no resampler in the path. With VoiceMeeter and VB-Cable in the chain (`FINDINGS.md` §7) a mismatch is likely rather than hypothetical.

- [ ] **Step 1: Write the failing tests**

Create `tests/Klangbruecke.Tests/Audio/AudioFormatBridgeTests.cs`:

```csharp
using Klangbruecke.Audio;
using NAudio.Wave;

namespace Klangbruecke.Tests.Audio;

public sealed class AudioFormatBridgeTests
{
    [Fact]
    public void RequiresResampling_IsFalse_ForIdenticalFormats()
    {
        var a = new WaveFormat(48000, 16, 2);
        var b = new WaveFormat(48000, 16, 2);

        Assert.False(AudioFormatBridge.RequiresResampling(a, b));
    }

    [Fact]
    public void RequiresResampling_IsTrue_WhenSampleRateDiffers()
    {
        // The A2DP sink commonly lands on 44.1 kHz while render endpoints sit at 48 kHz.
        var capture = new WaveFormat(44100, 16, 2);
        var output = new WaveFormat(48000, 16, 2);

        Assert.True(AudioFormatBridge.RequiresResampling(capture, output));
    }

    [Fact]
    public void RequiresResampling_IsTrue_WhenChannelCountDiffers()
    {
        Assert.True(AudioFormatBridge.RequiresResampling(new WaveFormat(48000, 16, 2), new WaveFormat(48000, 16, 1)));
    }

    [Fact]
    public void RequiresResampling_IsTrue_WhenBitDepthDiffers()
    {
        Assert.True(AudioFormatBridge.RequiresResampling(new WaveFormat(48000, 16, 2), new WaveFormat(48000, 24, 2)));
    }

    [Fact]
    public void RequiresResampling_IsTrue_WhenEncodingDiffers()
    {
        var pcm = new WaveFormat(48000, 32, 2);
        var ieeeFloat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        Assert.True(AudioFormatBridge.RequiresResampling(pcm, ieeeFloat));
    }

    [Fact]
    public void Describe_NamesRateDepthChannelsAndEncoding()
    {
        string described = AudioFormatBridge.Describe(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));

        Assert.Contains("48000", described);
        Assert.Contains("2ch", described);
        Assert.Contains("IeeeFloat", described);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj --filter AudioFormatBridgeTests`
Expected: FAIL — `The type or namespace name 'AudioFormatBridge' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Klangbruecke/Audio/AudioFormatBridge.cs`:

```csharp
using NAudio.Wave;

namespace Klangbruecke.Audio;

public static class AudioFormatBridge
{
    /// <summary>
    /// True when the capture format cannot be handed to a shared-mode render client as-is.
    /// Shared mode requires an exact match with the endpoint's mix format; a mismatch throws
    /// at Init, which presents as silence rather than an obvious error.
    /// </summary>
    public static bool RequiresResampling(WaveFormat capture, WaveFormat output)
        => capture.SampleRate != output.SampleRate
        || capture.Channels != output.Channels
        || capture.BitsPerSample != output.BitsPerSample
        || capture.Encoding != output.Encoding;

    public static string Describe(WaveFormat format)
        => $"{format.SampleRate}Hz {format.BitsPerSample}bit {format.Channels}ch {format.Encoding}";
}
```

Now modify `src/Klangbruecke/Audio/AudioRouter.cs`. Add to the `using` block at the top:

```csharp
using Klangbruecke.Diagnostics;
using NAudio.MediaFoundation;
```

Add a field after `private BufferedWaveProvider? _buffer;` (line 16):

```csharp
    private MediaFoundationResampler? _resampler;
```

Replace the body of `Start` (lines 62-95) with:

```csharp
    public bool Start(MMDevice source, MMDevice sink)
    {
        Stop();

        // Declared outside the try so the failure path can name both formats. Reading it is
        // itself a plausible failure point, hence the null until it is known.
        WaveFormat? outputFormat = null;

        try
        {
            _capture = new WasapiCapture(source);
            _buffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                // Enough slack to ride out scheduling hiccups without adding audible latency.
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true,
            };

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            outputFormat = sink.AudioClient.MixFormat;
            IWaveProvider playbackSource = _buffer;

            if (AudioFormatBridge.RequiresResampling(_capture.WaveFormat, outputFormat))
            {
                MediaFoundationApi.Startup();
                _resampler = new MediaFoundationResampler(_buffer, outputFormat) { ResamplerQuality = 60 };
                playbackSource = _resampler;

                Log.Info($"Resampling {AudioFormatBridge.Describe(_capture.WaveFormat)} -> " +
                         $"{AudioFormatBridge.Describe(outputFormat)}.");
            }

            _output = new WasapiOut(sink, AudioClientShareMode.Shared, useEventSync: true, latency: 50);
            _output.Init(playbackSource);

            _capture.StartRecording();
            _output.Play();

            IsRunning = true;
            Report($"Routing '{source.FriendlyName}' -> '{sink.FriendlyName}'.");
            return true;
        }
        catch (Exception ex)
        {
            // Log both formats: a format mismatch that survives the resampler is the likeliest
            // cause and is invisible from the message alone.
            string capture = _capture is null ? "unknown" : AudioFormatBridge.Describe(_capture.WaveFormat);
            string output = outputFormat is null ? "unknown" : AudioFormatBridge.Describe(outputFormat);

            Log.Error($"Routing failed. Capture={capture} Output={output}", ex);

            Report($"Could not start routing: {ex.Message}");
            Stop();
            return false;
        }
    }
```

In `Stop` (lines 112-136), add these two lines immediately before `_output?.Dispose();`:

```csharp
        _resampler?.Dispose();
        _resampler = null;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj`
Expected: PASS — 25 passed.

- [ ] **Step 5: Commit**

```powershell
git add src/Klangbruecke/Audio/ tests/Klangbruecke.Tests/Audio/
git commit -m "Resample when capture and render formats differ"
```

---

### Task 6: Correlate the call transport to the selected phone

**Files:**
- Create: `src/Klangbruecke/Bluetooth/BluetoothDeviceId.cs`
- Test: `tests/Klangbruecke.Tests/Bluetooth/BluetoothDeviceIdTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Klangbruecke.Bluetooth.BluetoothDeviceId` static class with `static string? TryExtractAddress(string? deviceId)` returning an uppercase 12-hex-digit address or null.

`TrayContext.cs:175` takes `transports.FirstOrDefault()` and never correlates the transport to the phone the user picked. With one phone paired this works by accident; with two it is a coin flip.

The A2DP selector and the phone-line selector return different id shapes for the same phone. The Bluetooth address is the only token they share. **The GUIDs embedded in device ids contain a 12-hex-digit final group** — `{0000110b-0000-1000-8000-00805f9b34fb}` ends in `00805f9b34fb` — so a naive 12-hex search matches the wrong thing. Braced sections are stripped first.

Wiring this into `TrayContext` happens in Task 8, once logging can capture the real id shapes.

- [ ] **Step 1: Write the failing tests**

Create `tests/Klangbruecke.Tests/Bluetooth/BluetoothDeviceIdTests.cs`:

```csharp
using Klangbruecke.Bluetooth;

namespace Klangbruecke.Tests.Bluetooth;

public sealed class BluetoothDeviceIdTests
{
    [Fact]
    public void TryExtractAddress_FindsTheAddressInABthenumId()
    {
        string id = @"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_LOCALMFG&0002\7&1a2b3c4d&0&0018092E5A5D_C00000000";

        Assert.Equal("0018092E5A5D", BluetoothDeviceId.TryExtractAddress(id));
    }

    [Fact]
    public void TryExtractAddress_IgnoresTheHexTailOfAGuid()
    {
        // 00805f9b34fb is 12 hex digits and would be matched by a naive scan.
        string id = @"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}";

        Assert.Null(BluetoothDeviceId.TryExtractAddress(id));
    }

    [Fact]
    public void TryExtractAddress_HandlesColonSeparatedForm()
    {
        string id = "Bluetooth#Bluetoothf8:e4:e3:11:22:33-00:18:09:2e:5a:5d";

        Assert.Equal("0018092E5A5D", BluetoothDeviceId.TryExtractAddress(id));
    }

    [Fact]
    public void TryExtractAddress_UppercasesTheResult()
    {
        string id = @"BTHENUM\7&1a2b3c4d&0&0018092e5a5d_C00000000";

        Assert.Equal("0018092E5A5D", BluetoothDeviceId.TryExtractAddress(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no address here")]
    public void TryExtractAddress_ReturnsNullWhenAbsent(string? id)
    {
        Assert.Null(BluetoothDeviceId.TryExtractAddress(id));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj --filter BluetoothDeviceIdTests`
Expected: FAIL — `The type or namespace name 'BluetoothDeviceId' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Klangbruecke/Bluetooth/BluetoothDeviceId.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Klangbruecke.Bluetooth;

/// <summary>
/// Pulls the Bluetooth address out of a Windows device id.
///
/// The A2DP selector and the phone-line selector return different id shapes for the same phone,
/// and the address is the only token they share. Matching on it is what stops the call transport
/// binding to whichever phone happens to enumerate first.
/// </summary>
public static partial class BluetoothDeviceId
{
    public static string? TryExtractAddress(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        // The colon form is unambiguous, so try it before touching anything else. The last
        // such run wins: ids of the form "<radio>-<device>" put the remote device second.
        MatchCollection separated = SeparatedAddress().Matches(deviceId);
        if (separated.Count > 0)
        {
            return separated[^1].Value.Replace(":", string.Empty).Replace("-", string.Empty).ToUpperInvariant();
        }

        // GUID sections end in a 12-hex-digit group that a bare scan would match, so drop
        // every braced section before looking for a plain run.
        string stripped = BracedSection().Replace(deviceId, " ");

        Match bare = BareAddress().Match(stripped);
        return bare.Success ? bare.Value.ToUpperInvariant() : null;
    }

    // Lookbehind rather than \b: the address is often glued to a word, as in
    // "Bluetooth#Bluetoothf8:e4:...", where \b would not anchor. The backreference forces one
    // consistent separator, without which the pattern happily matches the last four pairs of
    // one address plus the first two of the next.
    [GeneratedRegex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{2}(?<sep>[:-])(?:[0-9a-fA-F]{2}\k<sep>){4}[0-9a-fA-F]{2}(?![0-9a-fA-F])")]
    private static partial Regex SeparatedAddress();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex BracedSection();

    [GeneratedRegex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{12}(?![0-9a-fA-F])")]
    private static partial Regex BareAddress();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj`
Expected: PASS — 32 passed.

- [ ] **Step 5: Commit**

```powershell
git add src/Klangbruecke/Bluetooth/BluetoothDeviceId.cs tests/Klangbruecke.Tests/Bluetooth/
git commit -m "Extract Bluetooth address from device ids"
```

---

### Task 7: Decide whether the calls half can run

**Files:**
- Create: `src/Klangbruecke/Platform/PackageIdentity.cs`
- Create: `src/Klangbruecke/Platform/CallsPolicy.cs`
- Test: `tests/Klangbruecke.Tests/Platform/CallsPolicyTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Klangbruecke.Platform.PackageIdentity` static class with `static bool IsPackaged { get; }`; `Klangbruecke.Platform.CallsAvailability` enum (`Enabled`, `DisabledBySetting`, `DisabledNoPackageIdentity`); `Klangbruecke.Platform.CallsPolicy` static class with `static CallsAvailability Decide(bool enableCalls, bool isPackaged)` and `static string Explain(CallsAvailability availability)`.

`phoneLineTransportManagement` is a restricted capability and only works with MSIX package identity. Running unpackaged, the calls half cannot succeed — so it must not be attempted, and its absence must not read as a failure. This is what buys `dotnet run` as the inner development loop instead of a full install cycle per change.

Precedence: the user's setting wins. `EnableCalls = false` reports `DisabledBySetting` whether or not the app is packaged.

- [ ] **Step 1: Write the failing tests**

Create `tests/Klangbruecke.Tests/Platform/CallsPolicyTests.cs`:

```csharp
using Klangbruecke.Platform;

namespace Klangbruecke.Tests.Platform;

public sealed class CallsPolicyTests
{
    [Fact]
    public void Decide_IsEnabled_WhenWantedAndPackaged()
    {
        Assert.Equal(CallsAvailability.Enabled, CallsPolicy.Decide(enableCalls: true, isPackaged: true));
    }

    [Fact]
    public void Decide_BlamesPackageIdentity_WhenWantedButUnpackaged()
    {
        Assert.Equal(
            CallsAvailability.DisabledNoPackageIdentity,
            CallsPolicy.Decide(enableCalls: true, isPackaged: false));
    }

    [Fact]
    public void Decide_BlamesTheSetting_WhenTurnedOff()
    {
        Assert.Equal(CallsAvailability.DisabledBySetting, CallsPolicy.Decide(enableCalls: false, isPackaged: true));
    }

    [Fact]
    public void Decide_PrefersTheSetting_OverMissingPackageIdentity()
    {
        // The user's explicit choice is the more useful thing to report back.
        Assert.Equal(CallsAvailability.DisabledBySetting, CallsPolicy.Decide(enableCalls: false, isPackaged: false));
    }

    [Fact]
    public void Explain_NamesMsixWhenIdentityIsMissing()
    {
        string explanation = CallsPolicy.Explain(CallsAvailability.DisabledNoPackageIdentity);

        Assert.Contains("MSIX", explanation);
    }

    [Theory]
    [InlineData(CallsAvailability.Enabled)]
    [InlineData(CallsAvailability.DisabledBySetting)]
    [InlineData(CallsAvailability.DisabledNoPackageIdentity)]
    public void Explain_ReturnsSomethingForEveryCase(CallsAvailability availability)
    {
        Assert.False(string.IsNullOrWhiteSpace(CallsPolicy.Explain(availability)));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj --filter CallsPolicyTests`
Expected: FAIL — `The type or namespace name 'Platform' does not exist in the namespace 'Klangbruecke'`.

- [ ] **Step 3: Write the implementation**

Create `src/Klangbruecke/Platform/PackageIdentity.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Text;

namespace Klangbruecke.Platform;

/// <summary>
/// Whether this process is running with MSIX package identity.
///
/// The restricted capability phoneLineTransportManagement only applies with identity, so an
/// unpackaged run cannot do calls at all. Detecting that is what makes "dotnet run" usable as
/// a development loop for the music half. See docs/FINDINGS.md §2.
/// </summary>
public static class PackageIdentity
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            int length = 0;

            // With a zero-length null buffer this returns ERROR_INSUFFICIENT_BUFFER when there
            // is a package, and APPMODEL_ERROR_NO_PACKAGE when there is not.
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
```

Create `src/Klangbruecke/Platform/CallsPolicy.cs`:

```csharp
namespace Klangbruecke.Platform;

public enum CallsAvailability
{
    Enabled,
    DisabledBySetting,
    DisabledNoPackageIdentity,
}

public static class CallsPolicy
{
    /// <summary>
    /// A half that is switched off or structurally unavailable is not a failure and must not be
    /// retried. The user's setting takes precedence: it is the more useful thing to report.
    /// </summary>
    public static CallsAvailability Decide(bool enableCalls, bool isPackaged)
    {
        if (!enableCalls)
        {
            return CallsAvailability.DisabledBySetting;
        }

        return isPackaged ? CallsAvailability.Enabled : CallsAvailability.DisabledNoPackageIdentity;
    }

    public static string Explain(CallsAvailability availability) => availability switch
    {
        CallsAvailability.Enabled => "Calls enabled.",
        CallsAvailability.DisabledBySetting => "Calls disabled in settings.",
        _ => "Calls unavailable: no MSIX package identity, so the phoneLineTransportManagement "
           + "capability does not apply. Install the packaged build to route calls.",
    };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj`
Expected: PASS — 40 passed.

- [ ] **Step 5: Commit**

```powershell
git add src/Klangbruecke/Platform/ tests/Klangbruecke.Tests/Platform/
git commit -m "Detect package identity and gate the calls half on it"
```

---

### Task 8: Instrument the connect path

**Files:**
- Modify: `src/Klangbruecke/TrayContext.cs` (`ConnectAsync` at 158-186, `StartRouting` at 188-205, `Disconnect` at 207-213)
- Modify: `src/Klangbruecke/Bluetooth/AudioSinkService.cs` (`FindDevicesAsync` at 30-35, `ConnectAsync` at 37-75, `OnStateChanged` at 77-80)
- Modify: `src/Klangbruecke/Bluetooth/CallTransportService.cs` (`FindDevicesAsync` at 28-33)
- Modify: `src/Klangbruecke/Config/Settings.cs` (`Load` at 32-47, `Save` at 49-60)

**Interfaces:**
- Consumes: `Log` (Task 3), `BluetoothDeviceId.TryExtractAddress` (Task 6), `PackageIdentity.IsPackaged` / `CallsPolicy.Decide` / `CallsPolicy.Explain` (Task 7).
- Produces: no new public API.

This task wires the previous ones together and makes the first run produce evidence. Device ids are logged in full — they are the input to Task 6's address extraction, and their real shape on this machine is currently unknown.

- [ ] **Step 1: Log swallowed settings failures**

In `src/Klangbruecke/Config/Settings.cs`, add to the `using` block:

```csharp
using Klangbruecke.Diagnostics;
```

Replace the empty catch body in `Load` (line 43) with:

```csharp
            Log.Warn($"Could not read settings, starting from defaults: {ex.Message}");
```

Replace the empty catch body in `Save` (line 58) with:

```csharp
            Log.Warn($"Could not save settings: {ex.Message}");
```

- [ ] **Step 2: Instrument the sink service**

In `src/Klangbruecke/Bluetooth/AudioSinkService.cs`, add to the `using` block:

```csharp
using Klangbruecke.Diagnostics;
```

In `FindDevicesAsync`, replace the body (lines 32-34) with:

```csharp
        string selector = AudioPlaybackConnection.GetDeviceSelector();
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);

        Log.Info($"A2DP selector matched {devices.Count} device(s).");
        foreach (DeviceInformation device in devices)
        {
            Log.Info($"  A2DP candidate '{device.Name}' id={device.Id}");
        }

        return devices.ToList();
```

In `ConnectAsync`, add as the first line of the method body (before `Disconnect();` on line 39):

```csharp
        Log.Info($"Opening A2DP sink connection to id={deviceId}");
```

Replace `OnStateChanged` (lines 77-80) with:

```csharp
    private void OnStateChanged(AudioPlaybackConnection sender, object args)
    {
        // Reports state only. Acting on a drop is Stage 1's job; recording it is this stage's.
        Log.Info($"A2DP sink state changed to {sender.State}.");
        Report($"A2DP sink state: {sender.State}");
    }
```

- [ ] **Step 3: Instrument the transport service**

In `src/Klangbruecke/Bluetooth/CallTransportService.cs`, add to the `using` block:

```csharp
using Klangbruecke.Diagnostics;
```

Replace the body of `FindDevicesAsync` (lines 30-32) with:

```csharp
        string selector = PhoneLineTransportDevice.GetDeviceSelector();
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);

        Log.Info($"Phone-line selector matched {devices.Count} device(s).");
        foreach (DeviceInformation device in devices)
        {
            Log.Info($"  Transport candidate '{device.Name}' id={device.Id}");
        }

        return devices.ToList();
```

- [ ] **Step 4: Rewrite the tray connect path**

In `src/Klangbruecke/TrayContext.cs`, add to the `using` block:

```csharp
using Klangbruecke.Platform;
```

Replace `ConnectAsync` (lines 158-186) with:

```csharp
    private async Task ConnectAsync(string deviceId)
    {
        _settings.PhoneDeviceId = deviceId;
        _settings.Save();

        bool sinkOk = await _sink.ConnectAsync(deviceId);
        Log.Info($"A2DP connect {(sinkOk ? "succeeded" : "failed")}.");

        if (sinkOk)
        {
            StartRouting();
        }

        CallsAvailability calls = CallsPolicy.Decide(_settings.EnableCalls, PackageIdentity.IsPackaged);
        Log.Info(CallsPolicy.Explain(calls));

        if (calls != CallsAvailability.Enabled)
        {
            return;
        }

        // Independent of the music half - one failing must not take out the other.
        try
        {
            IReadOnlyList<DeviceInformation> transports = await CallTransportService.FindDevicesAsync();
            DeviceInformation? match = MatchTransport(transports, deviceId);

            if (match is null)
            {
                SetStatus("No call transport matches the selected phone.");
                return;
            }

            await _calls.ConnectAsync(match.Id);
        }
        catch (Exception ex)
        {
            Log.Error("Call transport enumeration failed.", ex);
            SetStatus($"Call transport unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Picks the transport belonging to the chosen phone rather than whichever enumerates
    /// first. The two selectors return different id shapes, so they are matched on the
    /// Bluetooth address they share.
    /// </summary>
    private static DeviceInformation? MatchTransport(IReadOnlyList<DeviceInformation> transports, string phoneDeviceId)
    {
        string? wanted = BluetoothDeviceId.TryExtractAddress(phoneDeviceId);

        if (wanted is not null)
        {
            DeviceInformation? match = transports
                .FirstOrDefault(t => BluetoothDeviceId.TryExtractAddress(t.Id) == wanted);

            if (match is not null)
            {
                Log.Info($"Matched transport '{match.Name}' to phone address {wanted}.");
                return match;
            }
        }

        // Address extraction is shape-dependent and the real id forms on this machine are not
        // yet confirmed. With exactly one candidate the old behaviour is still right; warn so
        // the log says why it was taken.
        if (transports.Count == 1)
        {
            Log.Warn($"No address match (phone id={phoneDeviceId}); falling back to the only transport available.");
            return transports[0];
        }

        Log.Warn($"No transport matched phone id={phoneDeviceId} among {transports.Count} candidates.");
        return null;
    }
```

Add logging to `StartRouting` — replace lines 190-202 with:

```csharp
        MMDevice? source = AudioRouter.FindSinkCaptureEndpoint();
        if (source is null)
        {
            // Per docs/FINDINGS.md §4 this is the expected state when nothing holds a
            // connection open, not a bug. It is also exactly what a failed connect looks like.
            SetStatus("No A2DP sink endpoint - nothing is holding a connection open.");
            return;
        }

        MMDevice? sink = AudioRouter.GetOutputDeviceOrDefault(_settings.OutputDeviceId);
        if (sink is null)
        {
            SetStatus("No usable output device.");
            return;
        }

        Log.Info($"Routing source='{source.FriendlyName}' sink='{sink.FriendlyName}'.");
```

Replace `Disconnect` (lines 207-213) with:

```csharp
    private void Disconnect()
    {
        Log.Info("Disconnect requested from the tray.");
        _router.Stop();
        _sink.Disconnect();
        _calls.Disconnect();
        SetStatus("Disconnected.");
    }
```

- [ ] **Step 5: Verify the build and the existing tests**

Run: `dotnet build Klangbruecke.sln -c Debug`
Expected: Build succeeded, 0 errors, 0 warnings.

Run: `dotnet test tests/Klangbruecke.Tests/Klangbruecke.Tests.csproj`
Expected: PASS — 40 passed.

- [ ] **Step 6: Verify the log records an unpackaged run**

Run: `dotnet run --project src/Klangbruecke/Klangbruecke.csproj`

Right-click the tray icon (this triggers enumeration), pick your phone, wait a few seconds, then Exit.

```powershell
Get-Content "$env:LOCALAPPDATA\Klangbruecke\logs\klangbruecke-$(Get-Date -Format yyyyMMdd).log"
```

This is the right path for an unpackaged run, and — because the manifest disables Desktop Bridge write virtualization (`docs/FINDINGS.md` §9) — for the installed build too. Both append here, so once Task 9 has run at least once, check the `Base directory:` line of the run you are reading before believing anything in it.

Expected: the log contains `A2DP selector matched N device(s).` with at least one `A2DP candidate` line showing a real device id, and the no-package-identity explanation for both halves.

**Record the real device id shape** — it is the input Task 6's regex was written against without evidence. If `TryExtractAddress` returns null for it, that shows up as the fallback warning in the log.

- [ ] **Step 7: Commit**

```powershell
git add src/Klangbruecke/
git commit -m "Instrument the connect path and correlate transport to phone"
```

---

### Task 9: Package, install, and validate against the phone

**Files:**
- Modify: `README.md` (Status section at 10-15)

**Interfaces:**
- Consumes: everything above.
- Produces: the answer to whether this app works, and the observations Stage 1 is designed against.

This task is hands-on and cannot be completed by an agent. It requires the phone, a real cellular call, and a person to hear whether audio is present in both directions.

- [ ] **Step 1: Build and sign the package**

```powershell
./packaging/New-DevCert.ps1      # once only; needs elevation
./packaging/Build-Msix.ps1
```

Expected: an `.msix` under `artifacts/`. If `Build-Msix.ps1` warns about a missing `.pfx`, `New-DevCert.ps1` has not been run or did not complete.

- [ ] **Step 2: Kill any development instance, then install**

**Kill any running `dotnet run` / `Klangbruecke.exe` from `bin\` first.** This is not tidiness. The single-instance mutex lives in the `Local\` namespace, which is **not** virtualized for MSIX, so a live unpackaged instance makes the packaged one log `Another instance already holds the single-instance mutex. Exiting.` and quit. Nothing appears in the tray and nothing else is written — from the outside it is indistinguishable from a crash.

```powershell
Get-Process Klangbruecke -ErrorAction SilentlyContinue | Stop-Process
```

Then enable sideloading: Settings → Update & Security → For developers → Sideload apps. Then double-click the `.msix` and install.

- [ ] **Step 3: Confirm which build wrote the log, and that identity is detected**

Launch Klangbruecke from the Start menu, then:

```powershell
Get-Content "$env:LOCALAPPDATA\Klangbruecke\logs\klangbruecke-$(Get-Date -Format yyyyMMdd).log" |
  Select-String "Base directory|Calls|Music"
```

That path is correct for the installed build — the manifest disables Desktop Bridge write virtualization (`docs/FINDINGS.md` §9). It is correct for `dotnet run` too, which is the catch: **both builds append to the same file**, so a day's log interleaves them.

Expected, in this order:

- `Base directory: C:\Program Files\WindowsApps\Klangbruecke_...` — **check this first.** If it names `src\Klangbruecke\bin\...` you are reading a development run and everything below it is about the wrong process.
- `Music enabled.`
- `Calls enabled.`

**Not** either no-package-identity line. If the log still says unpackaged while `Base directory` names `WindowsApps`, the identity probe itself is wrong — re-run `packaging/Test-PackageIdentity.ps1` (`FINDINGS.md` §2) rather than suspecting the packaging.

- [ ] **Step 4: Validate the music half**

Right-click the tray icon, pick the phone, pick an output device. Then verify against the OS rather than the tray — per `docs/FINDINGS.md` §4 the indicator has lied before:

```powershell
Get-PnpDevice -Class AudioEndpoint | Where-Object { $_.FriendlyName -match 'SNK|A2DP' }
```

Expected: `Line (Pixel 9 A2DP SNK)` present and `Status = OK`.

Then confirm the PC appears in the phone's own output picker, and play something. Audio must reach the selected output device.

If nothing connects, **check the pairing before suspecting the code.** The stale-IRK bug presents exactly like an API failure (`docs/FINDINGS.md` §3):

```powershell
Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=(Get-Date).AddHours(-2)} |
  Where-Object { $_.ProviderName -match 'BTH|Bluetooth' } |
  Select-Object TimeCreated, ProviderName, Id, LevelDisplayName, Message | Format-List
```

`BTHUSB` events 35 / 16 / 24 mean re-pair, not debug.

- [ ] **Step 5: Validate the call half**

Place a real cellular call. Verify audio in **both** directions — the mic half fails silently otherwise, so hearing the caller is not sufficient evidence.

Note that outgoing-call ringback is a known gap (`docs/FINDINGS.md` §6) and is not a failure of this stage.

- [ ] **Step 6: Kill the route from the far side, and watch it tear down**

`AudioRouter` has **zero automated coverage** — it cannot be constructed without real WASAPI endpoints — and five of its behaviours are defended by comments alone. Reverting any one of them leaves the whole unit suite green. This step is the only thing that exercises them, so do not skip it.

With music routing and playing, **disconnect Bluetooth on the phone** (not from the tray — the point is that the far side goes away without warning). Then:

```powershell
Get-Content "$env:LOCALAPPDATA\Klangbruecke\logs\klangbruecke-$(Get-Date -Format yyyyMMdd).log" |
  Select-String "Tearing the route down|stopped"
```

Expected: `Tearing the route down: the capture half stopped.` **or** `... the playback half stopped.` — exactly one of them, not both. One action here exercises the session token, the deferred teardown marshalling (`RequestTeardown`, which deadlocks if it ever runs on the thread that raised the event), and endpoint release.

Then **re-pick the phone from the tray menu and confirm it connects again.** A teardown that released nothing shows up here and nowhere else: the second attempt fails because the first route is still holding the A2DP capture endpoint open.

Record which half reported first. Stage 1 needs it — it says which of `RecordingStopped` and `PlaybackStopped` Windows raises first when an endpoint vanishes mid-stream.

- [ ] **Step 7: Check the log for cross-thread damage**

With the call still connected, disconnect Bluetooth on the phone, then:

```powershell
Get-Content "$env:LOCALAPPDATA\Klangbruecke\logs\klangbruecke-$(Get-Date -Format yyyyMMdd).log" |
  Select-String "InvalidOperationException|Unhandled"
```

Expected: no matches. This is the hand-verification of Task 4's cross-thread path, which could not be unit tested.

Also worth one look at the severity column:

```powershell
Get-Content "$env:LOCALAPPDATA\Klangbruecke\logs\klangbruecke-$(Get-Date -Format yyyyMMdd).log" |
  Select-String "\[ERR\]" -Context 0,6
```

Every `[ERR]` should be followed by an indented exception block. An `[ERR]` with no stack under it means a component logged a failure without the exception, which is the defect this stage's logging was rebuilt to prevent.

- [ ] **Step 8: Record the outcome**

Update the Status section of `README.md` (lines 10-15) to say what actually happened — which halves worked, and what did not. Append anything surprising to `docs/FINDINGS.md` rather than to the README.

Specifically record, for Stage 1's benefit:
- The real device id shapes from both selectors, and whether address matching worked or fell back.
- The capture/render format pair from the `Capture=... Render=...` line, and whether it said `differ` or `matched`. There is **no resampler** — WASAPI shared mode converts, and the earlier plan to add a `MediaFoundationResampler` was reverted as actively harmful (see the warning at the head of Task 5). The pair is logged unconditionally as a diagnostic, so record what it said; there is nothing to record about a resampler "engaging".
- What `RegisterApp()` did. This is the **first time it has ever run** (`FINDINGS.md` §2 records it as untested and names it as where the restricted capability probably bites), so whatever happened is new information: it returned, it threw, or the process vanished between the `Registering this app...` and `RegisterApp returned` lines.
- What `AudioPlaybackConnection.State` reports on a phone-initiated disconnect versus walking out of range. **This is the observation Stage 1's grace-window logic depends on.**
- Which half — capture or playback — reported first in Step 6.

**Do not re-open whether `AudioPlaybackConnection` works unpackaged.** It does not: `TryCreateFromId` terminates the process with an uncatchable `AccessViolationException`, reproduced on every attempt against a live device id, garbage ids, STA and MTA, two SDK projections, and a bare test host. That is settled and written up in `FINDINGS.md` §8. Re-running it to check only kills the process again and teaches nothing.

- [ ] **Step 9: Commit**

```powershell
git add README.md docs/FINDINGS.md
git commit -m "Record Stage 0 validation results"
```

---

## After this plan

Stage 1 (the `ConnectionManager` state machine) gets its own plan, written once Task 9 Step 8's observations exist. The spec's design for it stands; what the validation run supplies is the evidence for the grace-window behaviour and the device id handling that the state machine is built on.
