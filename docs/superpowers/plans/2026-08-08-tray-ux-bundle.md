# Tray UX Bundle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add six tray affordances — Connect Now, Open Logs, About, Check for Updates, Copy Diagnostics, and a README troubleshooting section — without betraying the tray-first ethos.

**Architecture:** Pure/testable logic units (`AboutText`, `UpdateChecker`, `DiagnosticsReport`, `LogTail`) plus one shell seam (`IAppShell`); `TrayContext` stays a thin view that turns each click into one call to the manager or the seam. Connect Now reuses `ConnectionManager`'s existing `ClickGrant` carve-out — a one-shot connect that overrides auto-reconnect-off without changing the setting.

**Tech Stack:** C# / .NET 8 (`net8.0-windows10.0.19041.0`), WinForms tray, xUnit tests, `System.Text.Json`, `HttpClient`.

Design spec: `docs/superpowers/specs/2026-08-08-tray-ux-bundle-design.md`.

## Global Constraints

- Target framework `net8.0-windows10.0.19041.0`. Do not raise the floor.
- ASCII `Klangbruecke` everywhere (no umlaut) — folder, namespace, identifiers, copy.
- Tests are **xUnit** (`[Fact]`/`[Theory]`/`[InlineData]`, `Assert.*`). Test project: `tests/Klangbruecke.Tests`. Mirror the `src` folder layout.
- Match the surrounding code's high comment density and naming when editing existing files.
- **Run the test suite unfiltered** (`dotnet test`, or `--logger "console;verbosity=detailed"`); a rare flake names itself only unfiltered (STATUS.md).
- Commit per task. Do not push (pushing is on request).
- If this bundle ends in a packaged build, **bump `<Version>` in both `src/Klangbruecke/Klangbruecke.csproj` and `packaging/AppxManifest.xml`** or `Add-AppxPackage` will not upgrade. (Not required to land these commits; only to install.)
- Zero new compiler warnings (STATUS.md: "zero warnings").

---

### Task 1: `AboutText` + `AppVersion`

**Files:**
- Create: `src/Klangbruecke/App/AboutText.cs`
- Create: `src/Klangbruecke/App/AppVersion.cs`
- Test: `tests/Klangbruecke.Tests/App/AboutTextTests.cs`

**Interfaces:**
- Produces: `AboutText.Build(Version version) : string`, `AboutText.RepoUrl : string` (const); `AppVersion.Current : Version`.

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using Klangbruecke.App;
using Xunit;

namespace Klangbruecke.Tests.App;

public sealed class AboutTextTests
{
    [Fact]
    public void Build_names_the_app_and_the_three_part_version()
    {
        string text = AboutText.Build(new Version(0, 2, 2, 0));

        Assert.Contains("Klangbruecke", text);
        Assert.Contains("0.2.2", text);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~AboutTextTests`
Expected: FAIL — `AboutText` does not exist.

- [ ] **Step 3: Write minimal implementation**

`src/Klangbruecke/App/AboutText.cs`:
```csharp
using System;

namespace Klangbruecke.App;

/// <summary>The About dialog's body. Pure, so the wording is pinned by a test.</summary>
public static class AboutText
{
    public const string RepoUrl = "https://github.com/MYSTRAVIL/klangbruecke";

    // Three-part version: the packaged build carries a four-part number whose fourth part is
    // meaningless to a user (and the GitHub tag never has it).
    public static string Build(Version version) =>
        $"Klangbruecke {version.ToString(3)}\n" +
        "Phone audio on your PC over Bluetooth - music and calls, in the tray.";
}
```

`src/Klangbruecke/App/AppVersion.cs`:
```csharp
using System;
using System.Reflection;

namespace Klangbruecke.App;

/// <summary>The running assembly's version. Kept in step with the manifest by hand (see Program).</summary>
public static class AppVersion
{
    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~AboutTextTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Klangbruecke/App/AboutText.cs src/Klangbruecke/App/AppVersion.cs tests/Klangbruecke.Tests/App/AboutTextTests.cs
git commit -m "Add AboutText and AppVersion for the tray About item"
```

---

### Task 2: Update-check logic (`UpdateChecker`, `IReleaseFeed`, `GitHubReleaseFeed`)

**Files:**
- Create: `src/Klangbruecke/App/UpdateCheckResult.cs`
- Create: `src/Klangbruecke/App/IReleaseFeed.cs` (holds `IReleaseFeed` + `ReleaseInfo`)
- Create: `src/Klangbruecke/App/UpdateChecker.cs`
- Create: `src/Klangbruecke/App/GitHubReleaseFeed.cs`
- Test: `tests/Klangbruecke.Tests/App/UpdateCheckerTests.cs`

**Interfaces:**
- Consumes: `AppVersion.Current` (Task 1).
- Produces:
  - `enum UpdateStatus { UpToDate, UpdateAvailable, Failed }`
  - `record UpdateCheckResult(UpdateStatus Status, Version? Latest, string? ReleaseUrl, string? Message)` with `UpToDate(Version)`, `Available(Version, string)`, `Failed(string)` factories.
  - `record ReleaseInfo(string Tag, string HtmlUrl)`; `interface IReleaseFeed { Task<ReleaseInfo?> GetLatestReleaseAsync(); }`.
  - `class UpdateChecker(IReleaseFeed feed, Version current)` with `Task<UpdateCheckResult> CheckAsync()` and `internal static bool TryParseTag(string, out Version)`.
  - `class GitHubReleaseFeed(HttpClient) : IReleaseFeed`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Threading.Tasks;
using Klangbruecke.App;
using Xunit;

namespace Klangbruecke.Tests.App;

public sealed class UpdateCheckerTests
{
    private sealed class StubFeed : IReleaseFeed
    {
        private readonly ReleaseInfo? _info;
        private readonly Exception? _error;
        public StubFeed(ReleaseInfo? info) => _info = info;
        public StubFeed(Exception error) => _error = error;
        public Task<ReleaseInfo?> GetLatestReleaseAsync() =>
            _error is not null ? Task.FromException<ReleaseInfo?>(_error) : Task.FromResult(_info);
    }

    [Theory]
    [InlineData("v0.2.3", true)]
    [InlineData("0.2.3", true)]
    [InlineData("v1.0", true)]
    [InlineData("vNope", false)]
    public void TryParseTag_tolerates_the_v_prefix_and_rejects_junk(string tag, bool ok)
    {
        Assert.Equal(ok, UpdateChecker.TryParseTag(tag, out _));
    }

    [Fact]
    public async Task A_newer_release_is_UpdateAvailable()
    {
        var checker = new UpdateChecker(
            new StubFeed(new ReleaseInfo("v0.2.3", "https://example/rel")), new Version(0, 2, 2, 0));

        UpdateCheckResult result = await checker.CheckAsync();

        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(new Version(0, 2, 3), result.Latest);
        Assert.Equal("https://example/rel", result.ReleaseUrl);
    }

    [Fact]
    public async Task The_same_version_is_UpToDate()
    {
        var checker = new UpdateChecker(
            new StubFeed(new ReleaseInfo("v0.2.2", "url")), new Version(0, 2, 2, 0));

        Assert.Equal(UpdateStatus.UpToDate, (await checker.CheckAsync()).Status);
    }

    [Fact]
    public async Task An_older_release_is_UpToDate()
    {
        var checker = new UpdateChecker(
            new StubFeed(new ReleaseInfo("v0.2.1", "url")), new Version(0, 2, 2, 0));

        Assert.Equal(UpdateStatus.UpToDate, (await checker.CheckAsync()).Status);
    }

    [Fact]
    public async Task No_releases_is_Failed()
    {
        var checker = new UpdateChecker(new StubFeed((ReleaseInfo?)null), new Version(0, 2, 2, 0));
        Assert.Equal(UpdateStatus.Failed, (await checker.CheckAsync()).Status);
    }

    [Fact]
    public async Task A_feed_that_throws_is_Failed_not_a_crash()
    {
        var checker = new UpdateChecker(new StubFeed(new Exception("offline")), new Version(0, 2, 2, 0));

        UpdateCheckResult result = await checker.CheckAsync();

        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains("offline", result.Message);
    }

    [Fact]
    public async Task A_malformed_tag_is_Failed()
    {
        var checker = new UpdateChecker(new StubFeed(new ReleaseInfo("banana", "url")), new Version(0, 2, 2, 0));
        Assert.Equal(UpdateStatus.Failed, (await checker.CheckAsync()).Status);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~UpdateCheckerTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Write minimal implementation**

`src/Klangbruecke/App/UpdateCheckResult.cs`:
```csharp
using System;

namespace Klangbruecke.App;

public enum UpdateStatus { UpToDate, UpdateAvailable, Failed }

public sealed record UpdateCheckResult(UpdateStatus Status, Version? Latest, string? ReleaseUrl, string? Message)
{
    public static UpdateCheckResult UpToDate(Version latest) => new(UpdateStatus.UpToDate, latest, null, null);

    public static UpdateCheckResult Available(Version latest, string url) =>
        new(UpdateStatus.UpdateAvailable, latest, url, null);

    public static UpdateCheckResult Failed(string message) => new(UpdateStatus.Failed, null, null, message);
}
```

`src/Klangbruecke/App/IReleaseFeed.cs`:
```csharp
using System.Threading.Tasks;

namespace Klangbruecke.App;

/// <summary>The newest published release (prereleases included), or null if there are none.</summary>
public interface IReleaseFeed
{
    Task<ReleaseInfo?> GetLatestReleaseAsync();
}

public sealed record ReleaseInfo(string Tag, string HtmlUrl);
```

`src/Klangbruecke/App/UpdateChecker.cs`:
```csharp
using System;
using System.Threading.Tasks;

namespace Klangbruecke.App;

/// <summary>
/// Compares the running version against the newest GitHub release. All failure - offline, HTTP error,
/// no releases, an unparseable tag - becomes <see cref="UpdateStatus.Failed"/>; this never throws, so
/// the tray click that calls it cannot crash the app.
/// </summary>
public sealed class UpdateChecker
{
    private readonly IReleaseFeed _feed;
    private readonly Version _current;

    public UpdateChecker(IReleaseFeed feed, Version current)
    {
        _feed = feed;
        _current = current;
    }

    public async Task<UpdateCheckResult> CheckAsync()
    {
        ReleaseInfo? release;
        try
        {
            release = await _feed.GetLatestReleaseAsync();
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failed(ex.Message);
        }

        if (release is null)
        {
            return UpdateCheckResult.Failed("No releases found.");
        }

        if (!TryParseTag(release.Tag, out Version latest))
        {
            return UpdateCheckResult.Failed($"Could not read the release tag '{release.Tag}'.");
        }

        return Normalize(latest) > Normalize(_current)
            ? UpdateCheckResult.Available(latest, release.HtmlUrl)
            : UpdateCheckResult.UpToDate(latest);
    }

    // Tags are v-prefixed semver (packaging/Publish-Release.ps1). Tolerate a missing 'v'.
    internal static bool TryParseTag(string tag, out Version version) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out version!);

    // First three components only: the tag has no fourth part, and the running version's is always 0.
    private static Version Normalize(Version v) => new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
}
```

`src/Klangbruecke/App/GitHubReleaseFeed.cs` (thin OS/network wrapper, untested by design — exercised through the fake above, like `WasapiDeviceFactory`):
```csharp
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Klangbruecke.App;

public sealed class GitHubReleaseFeed : IReleaseFeed
{
    // The list, not /releases/latest: 0.x builds ship as prereleases and /latest omits them (404).
    private const string ReleasesUrl = "https://api.github.com/repos/MYSTRAVIL/klangbruecke/releases";

    private readonly HttpClient _http;

    public GitHubReleaseFeed(HttpClient http)
    {
        _http = http;
        // GitHub rejects a request with no User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Klangbruecke");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<ReleaseInfo?> GetLatestReleaseAsync()
    {
        using System.IO.Stream stream = await _http.GetStreamAsync(ReleasesUrl);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream);

        // GitHub returns releases newest-first; take the first with a string tag.
        foreach (JsonElement release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("tag_name", out JsonElement tag) && tag.ValueKind == JsonValueKind.String)
            {
                string url = release.TryGetProperty("html_url", out JsonElement html)
                             && html.ValueKind == JsonValueKind.String
                    ? html.GetString()!
                    : "https://github.com/MYSTRAVIL/klangbruecke/releases";

                return new ReleaseInfo(tag.GetString()!, url);
            }
        }

        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~UpdateCheckerTests`
Expected: PASS (all cases).

- [ ] **Step 5: Commit**

```bash
git add src/Klangbruecke/App/UpdateCheckResult.cs src/Klangbruecke/App/IReleaseFeed.cs src/Klangbruecke/App/UpdateChecker.cs src/Klangbruecke/App/GitHubReleaseFeed.cs tests/Klangbruecke.Tests/App/UpdateCheckerTests.cs
git commit -m "Add UpdateChecker and the GitHub release feed"
```

---

### Task 3: `LogTail`

**Files:**
- Create: `src/Klangbruecke/Diagnostics/LogTail.cs`
- Test: `tests/Klangbruecke.Tests/Diagnostics/LogTailTests.cs`

**Interfaces:**
- Consumes: `FileLog.FileNameFor(DateTimeOffset)` (existing).
- Produces: `LogTail.ReadRecent(string directory, DateTimeOffset day, int count) : IReadOnlyList<string>`.

- [ ] **Step 1: Write the failing tests**

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~LogTailTests`
Expected: FAIL — `LogTail` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Klangbruecke.Diagnostics;

/// <summary>
/// The tail of a day's log file, for the Copy Diagnostics snapshot. Never throws - diagnostics must
/// not be the reason the app fails, matching <see cref="FileLog"/>'s own boundary.
/// </summary>
public static class LogTail
{
    public static IReadOnlyList<string> ReadRecent(string directory, DateTimeOffset day, int count)
    {
        try
        {
            string path = Path.Combine(directory, FileLog.FileNameFor(day));
            if (!File.Exists(path))
            {
                return Array.Empty<string>();
            }

            string[] lines = File.ReadAllLines(path);
            int take = Math.Clamp(count, 0, lines.Length);
            return lines[^take..];
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            return Array.Empty<string>();
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~LogTailTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Klangbruecke/Diagnostics/LogTail.cs tests/Klangbruecke.Tests/Diagnostics/LogTailTests.cs
git commit -m "Add LogTail for the diagnostics snapshot"
```

---

### Task 4: `DiagnosticsReport`

**Files:**
- Create: `src/Klangbruecke/Diagnostics/DiagnosticsReport.cs`
- Test: `tests/Klangbruecke.Tests/Diagnostics/DiagnosticsReportTests.cs`

**Interfaces:**
- Produces: `DiagnosticsReport.Build(Version version, string os, string state, string detail, IReadOnlyList<string> recentLogLines) : string`.

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using Klangbruecke.Diagnostics;
using Xunit;

namespace Klangbruecke.Tests.Diagnostics;

public sealed class DiagnosticsReportTests
{
    [Fact]
    public void Build_includes_version_os_state_and_every_log_line()
    {
        string report = DiagnosticsReport.Build(
            new Version(0, 2, 2, 0),
            "Windows 10.0.19045",
            "Degraded",
            "music retrying in 8s",
            new[] { "line-one", "line-two" });

        Assert.Contains("0.2.2.0", report);
        Assert.Contains("Windows 10.0.19045", report);
        Assert.Contains("Degraded", report);
        Assert.Contains("music retrying in 8s", report);
        Assert.Contains("line-one", report);
        Assert.Contains("line-two", report);
        Assert.Contains("review before sharing", report);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~DiagnosticsReportTests`
Expected: FAIL — `DiagnosticsReport` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System;
using System.Collections.Generic;
using System.Text;

namespace Klangbruecke.Diagnostics;

/// <summary>The paste-ready snapshot behind Copy Diagnostics. Pure, so its shape is pinned.</summary>
public static class DiagnosticsReport
{
    public static string Build(
        Version version,
        string os,
        string state,
        string detail,
        IReadOnlyList<string> recentLogLines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Klangbruecke diagnostics - review before sharing (includes device names).");
        sb.AppendLine();
        sb.AppendLine($"Version: {version}");
        sb.AppendLine($"OS:      {os}");
        sb.AppendLine($"State:   {state} - {detail}");
        sb.AppendLine();
        sb.AppendLine($"Recent log ({recentLogLines.Count} lines):");

        foreach (string line in recentLogLines)
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~DiagnosticsReportTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Klangbruecke/Diagnostics/DiagnosticsReport.cs tests/Klangbruecke.Tests/Diagnostics/DiagnosticsReportTests.cs
git commit -m "Add DiagnosticsReport for the Copy Diagnostics snapshot"
```

---

### Task 5: `IAppShell` + `AppShell`

**Files:**
- Create: `src/Klangbruecke/Platform/IAppShell.cs`
- Create: `src/Klangbruecke/Platform/AppShell.cs`

**Interfaces:**
- Produces: `interface IAppShell { void OpenFolder(string); void OpenUrl(string); void CopyToClipboard(string); void ShowInfo(string title, string message); bool Confirm(string title, string message); }` and `sealed class AppShell : IAppShell`.

This task is a thin shell wrapper with no unit test, exactly like `WasapiDeviceFactory` (untested by design; it is nothing but guarded OS calls). Its gate is: the solution compiles and the full suite stays green. It is exercised for real by Task 7's manual smoke.

- [ ] **Step 1: Create the interface**

`src/Klangbruecke/Platform/IAppShell.cs`:
```csharp
namespace Klangbruecke.Platform;

/// <summary>
/// The shell verbs the tray needs. A seam so <see cref="TrayContext"/> stays a view - one call per
/// click - and so all the raw OS calls live in one guarded place (<see cref="AppShell"/>).
/// </summary>
public interface IAppShell
{
    void OpenFolder(string path);
    void OpenUrl(string url);
    void CopyToClipboard(string text);
    void ShowInfo(string title, string message);
    bool Confirm(string title, string message);
}
```

- [ ] **Step 2: Create the implementation**

`src/Klangbruecke/Platform/AppShell.cs`:
```csharp
using System;
using System.Diagnostics;
using System.Windows.Forms;
using Klangbruecke.Diagnostics;

namespace Klangbruecke.Platform;

/// <summary>
/// Thin, guarded wrapper over the shell. Untested by design, like WasapiDeviceFactory: it is only OS
/// calls, and each is guarded so a shell failure cannot crash the tray. Every method here runs on the
/// UI (STA) thread, dispatched from a menu click - which is what Clipboard requires.
/// </summary>
public sealed class AppShell : IAppShell
{
    public void OpenFolder(string path) => Launch(path, $"open the folder {path}");

    public void OpenUrl(string url) => Launch(url, $"open {url}");

    public void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Log.Error("Copying to the clipboard failed.", ex);
        }
    }

    public void ShowInfo(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes;

    private static void Launch(string target, string describe)
    {
        try
        {
            // UseShellExecute so a folder path opens Explorer and an http(s) url opens the browser.
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Shell action failed: {describe}.", ex);
        }
    }
}
```

- [ ] **Step 3: Build and run the full suite**

Run: `dotnet build` then `dotnet test`
Expected: builds with no new warnings; suite green (nothing consumes `AppShell` yet — Task 7 wires it).

- [ ] **Step 4: Commit**

```bash
git add src/Klangbruecke/Platform/IAppShell.cs src/Klangbruecke/Platform/AppShell.cs
git commit -m "Add IAppShell seam and AppShell shell wrapper"
```

---

### Task 6: `ConnectionManager.RequestConnect()`

**Files:**
- Modify: `src/Klangbruecke/Connection/ConnectionManager.cs` (add a public method near `RequestDisconnect`)
- Test: `tests/Klangbruecke.Tests/Connection/ConnectionManagerTests.cs` (add to the existing file)

**Interfaces:**
- Consumes (all existing, private, in `ConnectionManager`): `_disposed`, `_settings.PhoneDeviceId`, `_latch.OnPhoneSelectionChanged()`, `_clickGrant` / `ClickGrant.Phone`, `_graceWindow.Cancel()`, `_reconciler.RunAsync(string, bool)`, `Publish()`. The `Harness` (in the test file) exposes `.Manager`, `.Link`, `.Sink`, `.Settings`, `.Scheduler` and a ctor `Harness(phoneDeviceId = PhoneId, autoReconnect = true, enableCalls = true)`.
- Produces: `public void ConnectionManager.RequestConnect()`.

This mirrors `SelectPhone` (same file) applied to the already-selected phone — the exact `RequestDisconnect` → `SelectPhone` reconnect pattern the suite already exercises. Model the tests on the existing "SelectPhone after RequestDisconnect reconnects" and "SelectPhone connects with auto-reconnect off" tests.

- [ ] **Step 1: Write the failing tests**

Add to `ConnectionManagerTests.cs` (a `// --- connect now ---` section):
```csharp
[Fact]
public void RequestConnect_clears_a_deliberate_disconnect_and_reconnects()
{
    using Harness h = new();

    h.Link.RaiseAppeared();
    Assert.Equal(ConnectionState.Connected, h.Manager.State);

    h.Manager.RequestDisconnect();
    Assert.Equal(ConnectionState.Suppressed, h.Manager.State);

    h.Manager.RequestConnect();
    Assert.Equal(ConnectionState.Connected, h.Manager.State);
}

[Fact]
public void RequestConnect_reconnects_even_with_auto_reconnect_off()
{
    using Harness h = new(autoReconnect: false);

    // Present, but not connected: with auto-reconnect off and no click grant, the appear is not
    // permitted to connect.
    h.Link.RaiseAppeared();
    Assert.Empty(h.Sink.ConnectCalls);

    h.Manager.RequestConnect();
    Assert.Equal(new[] { PhoneId }, h.Sink.ConnectCalls);
}

[Fact]
public void RequestConnect_with_no_phone_selected_does_nothing()
{
    using Harness h = new(phoneDeviceId: null);

    h.Manager.RequestConnect();

    Assert.Equal(ConnectionState.Idle, h.Manager.State);
    Assert.Empty(h.Sink.ConnectCalls);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ConnectionManagerTests.RequestConnect`
Expected: FAIL — `RequestConnect` does not exist.
(If the staging in the auto-off test does not reproduce "present but not connected," align it with the existing SelectPhone-with-`autoReconnect: false` test in this file — same presence setup, `RequestConnect` in place of `SelectPhone`.)

- [ ] **Step 3: Write minimal implementation**

Add near `RequestDisconnect` in `ConnectionManager.cs`:
```csharp
/// <summary>
/// Connect now, to the phone already selected. The manual, one-shot override: it clears the
/// suppression latch (whether a deliberate Disconnect or an auto-reconnect-off suppression) and grants
/// a connect even with auto-reconnect off - exactly as <see cref="SelectPhone"/> does - but changes
/// neither the selected phone, the calls role, nor the auto-reconnect setting.
///
/// The grant is one-shot: <see cref="ReleaseClickGrantIfDelivering"/> drops it once a half is
/// delivering, so after the next drop with auto-reconnect off the app goes dormant again, matching the
/// toggle. Nothing to connect to with no phone selected, so that is a no-op.
/// </summary>
public void RequestConnect()
{
    if (_disposed || _settings.PhoneDeviceId is null)
    {
        return;
    }

    _latch.OnPhoneSelectionChanged();
    _clickGrant = ClickGrant.Phone;
    _graceWindow.Cancel();

    // Through the reconcile, the one connect path in the class - as SelectPhone does.
    _ = _reconciler.RunAsync("connect requested", userAsked: true);

    // Preserve the grant across the repaint, for the reason SelectPhone documents: Refresh releases it
    // the moment every enabled half looks satisfied, which on a same-phone re-drive is true before the
    // pass has checked either half.
    ClickGrant granted = _clickGrant;
    Publish();
    _clickGrant = granted;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ConnectionManagerTests.RequestConnect`
Expected: PASS. Then run the whole file unfiltered: `dotnet test --filter FullyQualifiedName~ConnectionManagerTests` — all green (no regression to the grant/suppression tests).

- [ ] **Step 5: Commit**

```bash
git add src/Klangbruecke/Connection/ConnectionManager.cs tests/Klangbruecke.Tests/Connection/ConnectionManagerTests.cs
git commit -m "Add ConnectionManager.RequestConnect for the tray Connect Now item"
```

---

### Task 7: Tray wiring + composition

**Files:**
- Modify: `src/Klangbruecke/TrayContext.cs` (dependencies, class doc, menu items + handlers)
- Modify: `src/Klangbruecke/Program.cs` (compose `AppShell`, `HttpClient`, `GitHubReleaseFeed`, `UpdateChecker`; pass into `TrayContext`)

**Interfaces:**
- Consumes: `IAppShell` (Task 5), `UpdateChecker` + `UpdateStatus`/`UpdateCheckResult` (Task 2), `AboutText`/`AppVersion` (Task 1), `DiagnosticsReport`/`LogTail` (Tasks 3-4), `ConnectionManager.RequestConnect` (Task 6), `FileLog.DefaultDirectory` (existing).
- Produces: user-visible menu; no new testable surface (view + composition, untested like the rest of `TrayContext`/`Program`).

Gate: solution builds with no new warnings, full `dotnet test` green, and a manual smoke of the menu (below). This task has no unit test — `TrayContext` is a WinForms `ApplicationContext` view the project deliberately does not unit-test; the logic it drives was all tested in Tasks 1-6.

- [ ] **Step 1: Add the dependencies to `TrayContext`**

Add fields and extend the constructor. Add `using Klangbruecke.App;` at the top.
```csharp
private readonly IAppShell _shell;
private readonly UpdateChecker _updateChecker;
```
Constructor signature becomes:
```csharp
public TrayContext(
    NotifyIcon icon,
    TrayIcons icons,
    StatusPresenter status,
    ConnectionManager connection,
    Settings settings,
    IAppShell shell,
    UpdateChecker updateChecker)
```
Assign `_shell = shell; _updateChecker = updateChecker;` alongside the existing assignments.

- [ ] **Step 2: Update the class doc to match the new dependencies**

The `<summary>` on `TrayContext` enumerates what reaches it ("Five things reach it…") and states "the only [non-manager action] is Exit." Both are now false. Update that paragraph: it now also holds the shell seam and the update checker, and its non-manager actions are Exit plus the Diagnostics items, which each make one call to `IAppShell`. (This file has a standing rule that its doc must not lie about what it touches — do not skip this step.)

- [ ] **Step 3: Reorder and extend the menu**

In `RebuildMenuAsync`, after the Output submenu + separator, build this order (Connect/Disconnect above the toggles, then a Diagnostics submenu):
```csharp
_menu.Items.Add(new ToolStripSeparator());

var connect = new ToolStripMenuItem("Connect Now") { Enabled = _settings.PhoneDeviceId is not null };
connect.Click += (_, _) => _connection.RequestConnect();
_menu.Items.Add(connect);

var disconnect = new ToolStripMenuItem("Disconnect") { Enabled = _settings.PhoneDeviceId is not null };
disconnect.Click += (_, _) => _connection.RequestDisconnect();
_menu.Items.Add(disconnect);

_menu.Items.Add(new ToolStripSeparator());

(string callsText, bool callsEnabled, bool callsTicked) =
    CallsPolicy.MenuItem(PackageIdentity.IsPackaged, _settings.EnableCalls);
var calls = new ToolStripMenuItem(callsText) { Checked = callsTicked, Enabled = callsEnabled };
calls.Click += (_, _) => _connection.SetCallsEnabled(!_settings.EnableCalls);
_menu.Items.Add(calls);

var autoReconnect = new ToolStripMenuItem("Reconnect automatically") { Checked = _settings.AutoReconnect };
autoReconnect.Click += (_, _) => _connection.SetAutoReconnect(!_settings.AutoReconnect);
_menu.Items.Add(autoReconnect);

_menu.Items.Add(new ToolStripSeparator());
_menu.Items.Add(BuildDiagnosticsMenu());

var exit = new ToolStripMenuItem("Exit");
exit.Click += (_, _) => ExitThread();
_menu.Items.Add(exit);
```
Remove the old Disconnect block lower down (it moves up here). Exit stays last.

- [ ] **Step 4: Add the Diagnostics submenu builder and handlers**

```csharp
private ToolStripMenuItem BuildDiagnosticsMenu()
{
    var menu = new ToolStripMenuItem("Diagnostics");

    var openLogs = new ToolStripMenuItem("Open Logs");
    openLogs.Click += (_, _) => _shell.OpenFolder(FileLog.DefaultDirectory);
    menu.DropDownItems.Add(openLogs);

    var copy = new ToolStripMenuItem("Copy Diagnostics");
    copy.Click += (_, _) => CopyDiagnostics();
    menu.DropDownItems.Add(copy);

    menu.DropDownItems.Add(new ToolStripSeparator());

    var updates = new ToolStripMenuItem("Check for Updates...");
    updates.Click += async (_, _) => await CheckForUpdatesAsync();
    menu.DropDownItems.Add(updates);

    var about = new ToolStripMenuItem("About Klangbruecke");
    about.Click += (_, _) => ShowAbout();
    menu.DropDownItems.Add(about);

    return menu;
}

private void ShowAbout()
{
    string body = AboutText.Build(AppVersion.Current);
    if (_shell.Confirm("About Klangbruecke", body + "\n\nOpen the project page on GitHub?"))
    {
        _shell.OpenUrl(AboutText.RepoUrl);
    }
}

private async Task CheckForUpdatesAsync()
{
    UpdateCheckResult result = await _updateChecker.CheckAsync();

    switch (result.Status)
    {
        case UpdateStatus.UpdateAvailable:
            if (_shell.Confirm("Update available", $"{result.Latest} is available. Open the release page?"))
            {
                _shell.OpenUrl(result.ReleaseUrl!);
            }
            break;

        case UpdateStatus.UpToDate:
            _shell.ShowInfo("You're up to date", $"Klangbruecke {result.Latest} is the latest release.");
            break;

        default:
            _shell.ShowInfo("Couldn't check for updates", result.Message!);
            break;
    }
}

private void CopyDiagnostics()
{
    IReadOnlyList<string> tail = LogTail.ReadRecent(FileLog.DefaultDirectory, DateTimeOffset.Now, 30);
    string report = DiagnosticsReport.Build(
        AppVersion.Current,
        Environment.OSVersion.ToString(),
        _connection.State.ToString(),
        _connection.Detail,
        tail);

    _shell.CopyToClipboard(report);
    _shell.ShowInfo("Diagnostics copied", "A diagnostics snapshot is on your clipboard. Review it before sharing.");
}
```
The `async (_, _) => await CheckForUpdatesAsync()` handler is async void; an escaping exception routes to `Application.ThreadException` (logged in Program) — but `CheckAsync` never throws, so it resolves to a message either way.

- [ ] **Step 5: Compose in `Program.cs`**

In `RunTray`, before constructing `TrayContext`:
```csharp
var shell = new AppShell();

// One long-lived HttpClient for the process, the recommended lifetime. Not disposed - it lives as
// long as the tray.
var updateChecker = new UpdateChecker(new GitHubReleaseFeed(new HttpClient()), AppVersion.Current);
```
Add `using System.Net.Http;` and `using Klangbruecke.App;`. Extend the `TrayContext` construction:
```csharp
var tray = new TrayContext(icon, trayIcons, status, connection, settings, shell, updateChecker);
```

- [ ] **Step 6: Build, test, and smoke the menu**

Run: `dotnet build` (no new warnings) then `dotnet test` (full suite green).
Manual smoke (packaged build, per CLAUDE.md the music half needs the MSIX cycle; bump the version in both `Klangbruecke.csproj` and `AppxManifest.xml` first, then `packaging/Build-Msix.ps1` and `Add-AppxPackage` from Windows PowerShell):
- Right-click the tray icon: the order is Phone / Output / Connect Now / Disconnect / Calls / Reconnect automatically / Diagnostics / Exit.
- Connect Now enabled only with a phone selected; clicking it while suppressed reconnects.
- Diagnostics → Open Logs opens `%LOCALAPPDATA%\Klangbruecke\logs`.
- Diagnostics → Copy Diagnostics puts a snapshot on the clipboard.
- Diagnostics → Check for Updates reports up-to-date against the current release.
- Diagnostics → About shows the version and offers the GitHub link.

- [ ] **Step 7: Commit**

```bash
git add src/Klangbruecke/TrayContext.cs src/Klangbruecke/Program.cs
git commit -m "Wire the tray UX bundle into the menu and composition root"
```

---

### Task 8: README troubleshooting section

**Files:**
- Modify: `README.md` (add a Troubleshooting section)

- [ ] **Step 1: Add the section**

Add a `## Troubleshooting` section to `README.md`:
```markdown
## Troubleshooting

- **See what's happening.** Right-click the tray icon → **Diagnostics → Open Logs**, or open
  `%LOCALAPPDATA%\Klangbruecke\logs` directly. **Copy Diagnostics** puts a paste-ready snapshot
  (version, OS, state, recent log lines) on the clipboard — review it before sharing.
- **It won't connect.** Check the pairing before suspecting the app. Klangbruecke shows "connected"
  for its own view of the connection; verify the real endpoint with PowerShell:
  `Get-PnpDevice -Class AudioEndpoint | Where-Object FriendlyName -like '*A2DP*'`. A stale pairing
  (the IRK trap) presents exactly like an app failure — look at `BTHUSB` events 35 / 16 / 24 in the
  System log first. See `docs/FINDINGS.md` §3.
- **Force a reconnect.** Diagnostics won't help if the app is deliberately dormant — use
  **Connect Now** to override a Disconnect or a switched-off auto-reconnect for one attempt.
- **Reset configuration.** Delete `%LOCALAPPDATA%\Klangbruecke\settings.json` and restart; the app
  starts from defaults (no phone selected).
- **Check for a newer build.** Diagnostics → **Check for Updates**, or see the
  [Releases page](https://github.com/MYSTRAVIL/klangbruecke/releases).
```

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "README: add a Troubleshooting section"
```

---

## Self-Review

**Spec coverage:** Connect Now → Task 6 (+ wiring Task 7); Open Logs → Task 7 (uses Task 5); About → Tasks 1 + 7; Check for Updates → Tasks 2 + 7; Copy Diagnostics → Tasks 3, 4 + 7; README troubleshooting → Task 8; `IAppShell` seam → Task 5; menu layout (Connect/Disconnect above toggles, Diagnostics submenu) → Task 7. All spec sections map to a task.

**Placeholder scan:** No TBD/TODO; every code and test step carries real content.

**Type consistency:** `UpdateChecker(IReleaseFeed, Version)` / `CheckAsync()` / `TryParseTag` / `UpdateCheckResult` factories (`UpToDate`/`Available`/`Failed`) / `UpdateStatus` used identically in Tasks 2 and 7. `IAppShell` verbs (`OpenFolder`/`OpenUrl`/`CopyToClipboard`/`ShowInfo`/`Confirm`) defined in Task 5, consumed in Task 7. `LogTail.ReadRecent(string, DateTimeOffset, int)` and `DiagnosticsReport.Build(Version, string, string, string, IReadOnlyList<string>)` defined in Tasks 3-4, called in Task 7. `AboutText.Build`/`RepoUrl` and `AppVersion.Current` defined in Task 1, used in Task 7. `ConnectionManager.RequestConnect()` defined in Task 6, called in Task 7. Consistent.

**Deviation from spec:** the spec's testing note mentioned a `FakeAppShell`; this plan drops it. Its only consumer would be a `TrayContext` unit test, and `TrayContext` is a WinForms view the project does not unit-test. All shell-facing logic that *can* be tested (update-result branching lives on `UpdateCheckResult`; the snapshot on `DiagnosticsReport`) is tested directly; the remaining glue is thin view code. No behavior lost.
