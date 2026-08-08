# Tray UX Bundle 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax. This plan is executed UNATTENDED (the user is away) — every decision is already made below; do not stop for clarification. Get to *builds + full suite green + whole-branch review clean*, push the branch, and STOP without merging to `main` (hardware smoke + merge are the user's, per the spec).

**Goal:** Three tray UX improvements — left-click opens the menu, event sounds (synthesized chimes on Connected/Disconnected/Degraded, toggleable), and auto-pick among a remembered set of phones (first-present-wins).

**Architecture:** Pure tested policies (`SoundPolicy`, `PhonePicker`) + a thin untested seam (`ISoundPlayer`), matching the existing `TrayIconPolicy`/`IAppShell` split. Auto-pick is a resolver layer over the existing single-active-phone machinery (pick which remembered phone is present → point the existing watch/connect at it).

**Tech Stack:** C# / .NET 8 (`net8.0-windows10.0.19041.0`), WinForms tray, xUnit, embedded resources, `System.Media.SoundPlayer`.

Design spec: `docs/superpowers/specs/2026-08-08-tray-ux-bundle-2-design.md`.

## Global Constraints

- Target framework `net8.0-windows10.0.19041.0`. Do not raise it.
- ASCII `Klangbruecke` everywhere (no umlaut).
- xUnit tests (`[Fact]`/`[Theory]`/`[InlineData]`, `Assert.*`); test project `tests/Klangbruecke.Tests`, mirroring `src` layout.
- Match the surrounding code's high comment density and naming.
- Run the suite unfiltered (`dotnet test`); it is currently **4290 green, zero warnings** — keep it green with zero new warnings.
- Commit per task. Do NOT push per-task; the controller pushes the branch once at the end. Do NOT merge to `main`.
- `ConnectionState` values: `Idle, Discovering, Connecting, Connected, Degraded, Suppressed, RetryBackoff`.
- Do NOT run or launch the app (`dotnet run` dies in the music half, FINDINGS §8). Sound audibility, the left-click menu, and auto-pick with real phones are hardware smokes deferred to the user.
- Do NOT create/switch/reset git branches (`git checkout`/`switch`/`branch`/`reset`/`stash`/`merge`); only `git add` + `git commit` on the working branch.

---

### Task 1: `SoundEvent` + `SoundPolicy`

**Files:**
- Create: `src/Klangbruecke/Feedback/SoundEvent.cs`
- Create: `src/Klangbruecke/Feedback/SoundPolicy.cs`
- Test: `tests/Klangbruecke.Tests/Feedback/SoundPolicyTests.cs`

**Interfaces:**
- Produces: `enum Klangbruecke.Feedback.SoundEvent { Connected, Disconnected, Degraded }`; `static SoundEvent? SoundPolicy.For(ConnectionState previous, ConnectionState next)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Klangbruecke.Connection;
using Klangbruecke.Feedback;
using Xunit;

namespace Klangbruecke.Tests.Feedback;

public sealed class SoundPolicyTests
{
    [Fact]
    public void Entering_Connected_plays_Connected()
    {
        Assert.Equal(SoundEvent.Connected, SoundPolicy.For(ConnectionState.Connecting, ConnectionState.Connected));
        Assert.Equal(SoundEvent.Connected, SoundPolicy.For(ConnectionState.Degraded, ConnectionState.Connected));
    }

    [Fact]
    public void Staying_Connected_is_silent()
    {
        Assert.Null(SoundPolicy.For(ConnectionState.Connected, ConnectionState.Connected));
    }

    [Fact]
    public void A_half_dropping_from_full_plays_Degraded()
    {
        Assert.Equal(SoundEvent.Degraded, SoundPolicy.For(ConnectionState.Connected, ConnectionState.Degraded));
    }

    [Fact]
    public void A_partial_initial_connect_is_not_Degraded()
    {
        Assert.Null(SoundPolicy.For(ConnectionState.Connecting, ConnectionState.Degraded));
        Assert.Null(SoundPolicy.For(ConnectionState.Discovering, ConnectionState.Degraded));
    }

    [Theory]
    [InlineData(ConnectionState.Connected, ConnectionState.Idle)]
    [InlineData(ConnectionState.Connected, ConnectionState.Discovering)]
    [InlineData(ConnectionState.Connected, ConnectionState.RetryBackoff)]
    [InlineData(ConnectionState.Connected, ConnectionState.Suppressed)]
    [InlineData(ConnectionState.Degraded, ConnectionState.Discovering)]
    public void Losing_the_bridge_plays_Disconnected(ConnectionState previous, ConnectionState next)
    {
        Assert.Equal(SoundEvent.Disconnected, SoundPolicy.For(previous, next));
    }

    [Theory]
    [InlineData(ConnectionState.Idle, ConnectionState.Discovering)]
    [InlineData(ConnectionState.Discovering, ConnectionState.Connecting)]
    [InlineData(ConnectionState.RetryBackoff, ConnectionState.Connecting)]
    [InlineData(ConnectionState.Idle, ConnectionState.Idle)]
    public void Churn_below_a_live_bridge_is_silent(ConnectionState previous, ConnectionState next)
    {
        Assert.Null(SoundPolicy.For(previous, next));
    }
}
```

- [ ] **Step 2: Run to verify they fail** — `dotnet test --filter FullyQualifiedName~SoundPolicyTests` → FAIL (types missing).

- [ ] **Step 3: Implement**

`src/Klangbruecke/Feedback/SoundEvent.cs`:
```csharp
namespace Klangbruecke.Feedback;

/// <summary>A connection-lifecycle event worth an audible cue. See <see cref="SoundPolicy"/>.</summary>
public enum SoundEvent
{
    Connected,
    Disconnected,
    Degraded,
}
```

`src/Klangbruecke/Feedback/SoundPolicy.cs`:
```csharp
using Klangbruecke.Connection;

namespace Klangbruecke.Feedback;

/// <summary>
/// Which sound (if any) a connection-state transition earns. Pure, so the transition table is pinned
/// by tests - the same shape as <see cref="TrayIconPolicy"/> for the glyph. Fires only on the edges a
/// user cares about; silent on the Discovering/Connecting/RetryBackoff churn so it is not chatty.
/// </summary>
public static class SoundPolicy
{
    public static SoundEvent? For(ConnectionState previous, ConnectionState next)
    {
        // Came fully up.
        if (next == ConnectionState.Connected && previous != ConnectionState.Connected)
        {
            return SoundEvent.Connected;
        }

        // A half dropped from a full bridge (not a partial initial connect).
        if (next == ConnectionState.Degraded && previous == ConnectionState.Connected)
        {
            return SoundEvent.Degraded;
        }

        // The bridge was lost - left a delivering state for one that is not.
        bool wasDelivering = previous is ConnectionState.Connected or ConnectionState.Degraded;
        bool stillDelivering = next is ConnectionState.Connected or ConnectionState.Degraded;
        if (wasDelivering && !stillDelivering)
        {
            return SoundEvent.Disconnected;
        }

        return null;
    }
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter FullyQualifiedName~SoundPolicyTests` → PASS. Then full `dotnet test`.

- [ ] **Step 5: Commit** — `git add` the three files; `git commit -m "Add SoundEvent and SoundPolicy (state-transition to chime)"`.

---

### Task 2: `PhonePicker`

**Files:**
- Create: `src/Klangbruecke/Connection/PhonePicker.cs`
- Test: `tests/Klangbruecke.Tests/Connection/PhonePickerTests.cs`

**Interfaces:**
- Produces: `static string? PhonePicker.Pick(string? activeId, IReadOnlyList<string> remembered, Func<string,bool> isPresent)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Collections.Generic;
using Klangbruecke.Connection;
using Xunit;

namespace Klangbruecke.Tests.Connection;

public sealed class PhonePickerTests
{
    private static readonly IReadOnlyList<string> AB = new[] { "A", "B" };

    [Fact]
    public void Keeps_a_present_incumbent_even_if_another_is_also_present()
    {
        // A is active and present; B also present. First-present-wins never thrashes the incumbent.
        Assert.Equal("A", PhonePicker.Pick("A", AB, _ => true));
        Assert.Equal("B", PhonePicker.Pick("B", AB, _ => true));
    }

    [Fact]
    public void Picks_the_first_present_when_the_incumbent_is_absent()
    {
        Assert.Equal("B", PhonePicker.Pick("A", AB, id => id == "B"));
    }

    [Fact]
    public void Keeps_watching_the_absent_incumbent_when_none_is_present()
    {
        Assert.Equal("A", PhonePicker.Pick("A", AB, _ => false));
    }

    [Fact]
    public void Falls_back_to_the_first_remembered_when_there_is_no_incumbent()
    {
        Assert.Equal("A", PhonePicker.Pick(null, AB, _ => false));
    }

    [Fact]
    public void An_incumbent_no_longer_remembered_is_dropped()
    {
        Assert.Equal("A", PhonePicker.Pick("X", AB, _ => false));
    }

    [Fact]
    public void No_remembered_phones_picks_nothing()
    {
        Assert.Null(PhonePicker.Pick("A", Array.Empty<string>(), _ => true));
        Assert.Null(PhonePicker.Pick(null, Array.Empty<string>(), _ => true));
    }
}
```

- [ ] **Step 2: Run to verify fail** — `dotnet test --filter FullyQualifiedName~PhonePickerTests` → FAIL.

- [ ] **Step 3: Implement**

`src/Klangbruecke/Connection/PhonePicker.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace Klangbruecke.Connection;

/// <summary>
/// First-present-wins auto-pick over a remembered phone set. Pure, so its rules are pinned by tests;
/// presence is a predicate the caller backs with a live link-status read. Never thrashes a working
/// (present) incumbent, even when another remembered phone is also present.
/// </summary>
public static class PhonePicker
{
    public static string? Pick(string? activeId, IReadOnlyList<string> remembered, Func<string, bool> isPresent)
    {
        // 1. Keep a present incumbent that is still remembered.
        if (activeId is not null && remembered.Contains(activeId) && isPresent(activeId))
        {
            return activeId;
        }

        // 2. Otherwise the first remembered phone that is present.
        foreach (string id in remembered)
        {
            if (isPresent(id))
            {
                return id;
            }
        }

        // 3. None present: keep watching the remembered incumbent so its fast-reconnect edge still fires.
        if (activeId is not null && remembered.Contains(activeId))
        {
            return activeId;
        }

        // 4. No usable incumbent: watch the first remembered phone, or nothing.
        return remembered.Count > 0 ? remembered[0] : null;
    }
}
```

- [ ] **Step 4: Run pass** — filter, then full `dotnet test`.
- [ ] **Step 5: Commit** — `git commit -m "Add PhonePicker (first-present-wins auto-pick)"`.

---

### Task 3: `Settings` — remembered phones, event-sounds flag, migration

**Files:**
- Modify: `src/Klangbruecke/Config/Settings.cs`
- Test: `tests/Klangbruecke.Tests/Config/SettingsTests.cs` (create if absent)

**Interfaces:**
- Produces: `Settings.RememberedPhoneIds : List<string>` (default `new()`); `Settings.EventSounds : bool` (default `true`); `Settings.Load` migrates a lone `PhoneDeviceId` into `RememberedPhoneIds`.

- [ ] **Step 1: Write the failing tests** — a migration test. `Settings.Load` reads `Settings.FilePath`, which is the real `%LOCALAPPDATA%` file, so DO NOT write there. Instead test the migration as a pure step: add an `internal static Settings Migrate(Settings s)` that `Load` calls, and test `Migrate` directly.

```csharp
using System.Collections.Generic;
using Klangbruecke.Config;
using Xunit;

namespace Klangbruecke.Tests.Config;

public sealed class SettingsTests
{
    [Fact]
    public void Migrate_seeds_remembered_from_a_lone_selected_phone()
    {
        var s = new Settings { PhoneDeviceId = "phone-A", RememberedPhoneIds = new List<string>() };
        Settings.Migrate(s);
        Assert.Equal(new[] { "phone-A" }, s.RememberedPhoneIds);
    }

    [Fact]
    public void Migrate_leaves_an_existing_remembered_set_alone()
    {
        var s = new Settings { PhoneDeviceId = "phone-A", RememberedPhoneIds = new List<string> { "phone-B" } };
        Settings.Migrate(s);
        Assert.Equal(new[] { "phone-B" }, s.RememberedPhoneIds);
    }

    [Fact]
    public void Migrate_with_no_selected_phone_leaves_the_set_empty()
    {
        var s = new Settings { PhoneDeviceId = null, RememberedPhoneIds = new List<string>() };
        Settings.Migrate(s);
        Assert.Empty(s.RememberedPhoneIds);
    }

    [Fact]
    public void EventSounds_defaults_on()
    {
        Assert.True(new Settings().EventSounds);
    }
}
```

- [ ] **Step 2: Run to verify fail.**

- [ ] **Step 3: Implement** — add to `Settings`:
```csharp
/// <summary>Phones to auto-connect: whichever is present wins (first-present). See PhonePicker.</summary>
public List<string> RememberedPhoneIds { get; set; } = new();

/// <summary>Play a chime on connect / disconnect / degrade.</summary>
public bool EventSounds { get; set; } = true;
```
Add the migration and call it from `Load` after deserialize (both the found-file and default paths return through it):
```csharp
// Seed the remembered set from a pre-bundle-2 single selection, so an upgrade keeps auto-connecting
// the one phone the user had picked. Idempotent: only fires when nothing is remembered yet.
internal static Settings Migrate(Settings settings)
{
    if (settings.RememberedPhoneIds.Count == 0 && settings.PhoneDeviceId is not null)
    {
        settings.RememberedPhoneIds.Add(settings.PhoneDeviceId);
    }

    return settings;
}
```
In `Load`, wrap each return: `return Migrate(JsonSerializer.Deserialize<Settings>(...) ?? new Settings());` and `return Migrate(new Settings());`. (A null `RememberedPhoneIds` from an old JSON without the field deserializes to the property initializer's `new()`, so `.Count` is safe.)

- [ ] **Step 4: Run pass** (filter, then full suite).
- [ ] **Step 5: Commit** — `git commit -m "Settings: remembered phones + event-sounds flag + migration"`.

---

### Task 4: `FakeLinkMonitor` per-id status (test infrastructure)

**Files:**
- Modify: `tests/Klangbruecke.Tests/Fakes/FakeLinkMonitor.cs`

**Interfaces:**
- Produces: a way to stage per-device presence so resolver tests can say "A present, B absent". Add `Dictionary<string,BluetoothLinkStatus> StatusById { get; }` and make `ReadLinkStatusAsync(string? deviceId)` (if the interface exposes an id overload) — OR, since `ILinkMonitor.ReadLinkStatusAsync()` takes no id, add a test-only `SetStatusFor(string id, BluetoothLinkStatus)` plus have the fake track the last watched id. Read the real `ILinkMonitor` interface first and match it.

Note for the implementer: the production resolver reads other phones' presence via the **static** `LinkMonitor.ReadLinkStatusAsync(string deviceId)` (see LinkMonitor.cs) — that static cannot be faked. So Task 7 must call presence through an injectable seam. Decide in Task 7 (see its note) whether to (a) add `Task<BluetoothLinkStatus> ReadLinkStatusForAsync(string deviceId)` to `ILinkMonitor` (real impl delegates to the static; fake reads `StatusById`), which is the clean, testable choice. **This task implements the fake side of that:** add `StatusById` and implement `ReadLinkStatusForAsync` on `FakeLinkMonitor` returning `StatusById.GetValueOrDefault(id, Status)`.

- [ ] **Step 1:** Read `src/Klangbruecke/Bluetooth/ILinkMonitor.cs` and `FakeLinkMonitor.cs`. Add `ReadLinkStatusForAsync(string deviceId)` to `ILinkMonitor` (this is the seam Task 7 needs), implement it on the real `LinkMonitor` as `=> ReadLinkStatusAsync(deviceId)` (the existing static), and on `FakeLinkMonitor` as returning from a new `StatusById` map (fallback to the existing `Status`).
- [ ] **Step 2:** Build + full `dotnet test` (green; nothing calls the new member yet).
- [ ] **Step 3: Commit** — `git commit -m "Add ILinkMonitor.ReadLinkStatusForAsync + fake per-id status"`.

---

### Task 5: Synthesized chimes — generator + embedded assets

**Files:**
- Create: `packaging/Generate-Sounds.ps1`
- Create (generated, committed): `src/Klangbruecke/Assets/connect.wav`, `disconnect.wav`, `degraded.wav`
- Modify: `src/Klangbruecke/Klangbruecke.csproj` (add the three as `<EmbeddedResource>`)

- [ ] **Step 1: Write `packaging/Generate-Sounds.ps1`** (16-bit mono PCM WAV; small amplitude; 5 ms fades to avoid clicks):
```powershell
<#
.SYNOPSIS
  Generates the three event chimes (connect/disconnect/degraded) as 16-bit mono PCM WAVs into
  src/Klangbruecke/Assets. Re-run to change the tones; the .wav files are committed and embedded.
#>
[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
$rate = 44100
$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Klangbruecke\Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

function Write-Wav([string]$path, [double[]]$freqs, [double]$segMs) {
    $amp = 0.25; $fade = [int]($rate * 0.005)
    $samples = New-Object System.Collections.Generic.List[int16]
    foreach ($f in $freqs) {
        $n = [int]($rate * $segMs / 1000.0)
        for ($i = 0; $i -lt $n; $i++) {
            $env = 1.0
            if ($i -lt $fade) { $env = $i / $fade }
            elseif ($i -gt ($n - $fade)) { $env = ($n - $i) / $fade }
            $v = [math]::Sin(2 * [math]::PI * $f * $i / $rate) * $amp * $env
            $samples.Add([int16]([math]::Round($v * 32767)))
        }
    }
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $dataLen = $samples.Count * 2
    $bw.Write([char[]]'RIFF'); $bw.Write([int](36 + $dataLen)); $bw.Write([char[]]'WAVE')
    $bw.Write([char[]]'fmt '); $bw.Write([int]16); $bw.Write([int16]1); $bw.Write([int16]1)
    $bw.Write([int]$rate); $bw.Write([int]($rate * 2)); $bw.Write([int16]2); $bw.Write([int16]16)
    $bw.Write([char[]]'data'); $bw.Write([int]$dataLen)
    foreach ($s in $samples) { $bw.Write([int16]$s) }
    $bw.Flush(); [System.IO.File]::WriteAllBytes($path, $ms.ToArray()); $bw.Dispose(); $ms.Dispose()
    Write-Host "wrote $path ($($samples.Count) samples)"
}

Write-Wav (Join-Path $assets 'connect.wav')    @(660, 880) 120
Write-Wav (Join-Path $assets 'disconnect.wav') @(880, 660) 120
Write-Wav (Join-Path $assets 'degraded.wav')   @(440)      180
```

- [ ] **Step 2: Run it** — `pwsh packaging/Generate-Sounds.ps1` (or `powershell`); confirm the three `.wav` files exist under `src/Klangbruecke/Assets/` and are non-trivial (> 1 KB each). Play is NOT required (no audio device assumption).

- [ ] **Step 3: Embed them** — in `Klangbruecke.csproj`, alongside the existing tray-ico `<EmbeddedResource>` group, add:
```xml
<EmbeddedResource Include="Assets\connect.wav" />
<EmbeddedResource Include="Assets\disconnect.wav" />
<EmbeddedResource Include="Assets\degraded.wav" />
```

- [ ] **Step 4: Build** — `dotnet build`; confirm no warnings and the resources embed (a `dotnet test` run stays green).
- [ ] **Step 5: Commit** — `git add` the script, the three `.wav`, and the csproj; `git commit -m "Add synthesized event chimes + generator, embedded"`.

---

### Task 6: `ISoundPlayer` + `SoundPlayer` + `FakeSoundPlayer`

**Files:**
- Create: `src/Klangbruecke/Feedback/ISoundPlayer.cs`
- Create: `src/Klangbruecke/Feedback/SoundPlayer.cs`
- Create: `tests/Klangbruecke.Tests/Fakes/FakeSoundPlayer.cs`

**Interfaces:**
- Consumes: `SoundEvent` (Task 1), the embedded WAVs (Task 5).
- Produces: `interface ISoundPlayer { void Play(SoundEvent e); }`; `sealed class SoundPlayer : ISoundPlayer`; `sealed class FakeSoundPlayer : ISoundPlayer` with `List<SoundEvent> Played { get; }`.

`SoundPlayer` loads each WAV once from the assembly manifest (mirror `TrayIcons.Load` — match by suffix), keeps a `System.Media.SoundPlayer` per event (or replays the stream), and `Play` is guarded (never throws; log at Warn on failure). It is thin OS plumbing, **untested by design** (like `AppShell`/`WasapiDeviceFactory`).

- [ ] **Step 1: Interface + fake**

`src/Klangbruecke/Feedback/ISoundPlayer.cs`:
```csharp
namespace Klangbruecke.Feedback;

/// <summary>Plays a short chime for a <see cref="SoundEvent"/>. Never throws.</summary>
public interface ISoundPlayer
{
    void Play(SoundEvent e);
}
```

`tests/Klangbruecke.Tests/Fakes/FakeSoundPlayer.cs`:
```csharp
using System.Collections.Generic;
using Klangbruecke.Feedback;

namespace Klangbruecke.Tests.Fakes;

public sealed class FakeSoundPlayer : ISoundPlayer
{
    public List<SoundEvent> Played { get; } = new();
    public void Play(SoundEvent e) => Played.Add(e);
}
```

- [ ] **Step 2: Implement `SoundPlayer`**

`src/Klangbruecke/Feedback/SoundPlayer.cs`:
```csharp
using System;
using System.IO;
using System.Media;
using System.Reflection;
using Klangbruecke.Diagnostics;

namespace Klangbruecke.Feedback;

/// <summary>
/// Plays the embedded chimes to the default output. Thin OS plumbing, untested by design like
/// WasapiDeviceFactory: each Play is guarded so a playback fault cannot crash the tray. The WAV bytes
/// are read once from the assembly manifest (same pattern as TrayIcons).
/// </summary>
public sealed class SoundPlayer : ISoundPlayer
{
    private readonly System.Media.SoundPlayer _connect;
    private readonly System.Media.SoundPlayer _disconnect;
    private readonly System.Media.SoundPlayer _degraded;

    public SoundPlayer()
    {
        _connect = Load("connect.wav");
        _disconnect = Load("disconnect.wav");
        _degraded = Load("degraded.wav");
    }

    public void Play(SoundEvent e)
    {
        try
        {
            Pick(e).Play(); // async; returns immediately, plays on a worker thread
        }
        catch (Exception ex)
        {
            Log.Warn($"Playing the {e} chime failed: {ex.Message}");
        }
    }

    private System.Media.SoundPlayer Pick(SoundEvent e) => e switch
    {
        SoundEvent.Connected => _connect,
        SoundEvent.Disconnected => _disconnect,
        _ => _degraded,
    };

    private static System.Media.SoundPlayer Load(string fileName)
    {
        Assembly assembly = typeof(SoundPlayer).Assembly;
        string name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(fileName, StringComparison.Ordinal));
        // Copy to a MemoryStream the SoundPlayer keeps; the manifest stream is not seekable-for-replay.
        using Stream stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded chime '{name}' was null.");
        var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;
        var player = new System.Media.SoundPlayer(buffer);
        player.Load();
        return player;
    }
}
```
(Add `using System.Linq;` for `Single`.)

- [ ] **Step 3: Build + full suite** — green; nothing consumes it yet.
- [ ] **Step 4: Commit** — `git commit -m "Add ISoundPlayer seam, SoundPlayer, and FakeSoundPlayer"`.

---

### Task 7: `ConnectionManager` — remembered-set API, resolver, SetEventSounds

**Files:**
- Modify: `src/Klangbruecke/Connection/ConnectionManager.cs`
- Modify: `tests/Klangbruecke.Tests/Connection/ConnectionManagerTests.cs`

This is the core task. Read `ConnectionManager.cs` fully first. It currently has `SelectPhone(id)`, `DeselectPhone()`, `SetAutoReconnect`, `SetCallsEnabled`, `RequestConnect`, `RequestDisconnect`, the `_clickGrant` carve-out, and constructs `Reconciler` with a periodic tick.

**Interfaces (produce):**
- `void SetPhoneRemembered(string id, bool remembered)`
- `void ClearRememberedPhones()`
- `void SetEventSounds(bool enabled)`
- Keep `RequestConnect()` — it now runs the resolver.
- Remove `SelectPhone`/`DeselectPhone` (fold into the above).

**Design to implement:**
1. **`SetActivePhone(string id)`** (private) — the old `SelectPhone` body generalized to "make this the active/watched phone now": set `_settings.PhoneDeviceId = id`, save, `_latch.OnPhoneSelectionChanged()`, `_clickGrant = ClickGrant.Phone`, `_graceWindow.Cancel()`, calls-role handling on change, `_linkMachine.OnPhoneSelected()`, `_linkMonitor.Watch(id)`, `_ = _reconciler.RunAsync("phone resolved", userAsked: true)`, then the grant-preserving `Publish`.
2. **`SetPhoneRemembered(id, remembered)`** — add/remove `id` in `_settings.RememberedPhoneIds`, `_settings.Save()`, then `ResolveActivePhoneAsync()`.
3. **`ClearRememberedPhones()`** — clear the list, save, `_settings.PhoneDeviceId = null`, `_linkMonitor.StopWatching()`, `_linkMachine.OnPhoneDeselected()`, reset latch/grant, `Publish()`.
4. **`ResolveActivePhoneAsync()`** (private) — build a presence predicate by awaiting `_linkMonitor.ReadLinkStatusForAsync(id) == BluetoothLinkStatus.Connected` for each remembered id (await them; cache into a dict, then call the pure picker). `string? pick = PhonePicker.Pick(_settings.PhoneDeviceId, _settings.RememberedPhoneIds, id => presentMap[id]);` If `pick is null` → `ClearRememberedPhones`-like dormancy (stop watching). Else if `pick != _settings.PhoneDeviceId` → `SetActivePhone(pick)`. Else nothing (incumbent kept). Guard with the same disposed/superseded discipline the class uses; this awaits, so re-check `_disposed` after the awaits.
5. **Run the resolver** at: `Start()` (after settings applied), whenever the active phone is lost (the existing removed-edge / grace-window range-exit path), and on a **periodic tick** — schedule `_scheduler.SchedulePeriodic(TimeSpan.FromSeconds(30), () => _ = ResolveActivePhoneAsync())` in `Start()` (dispose the handle in `Dispose`). Keeping this separate from `Reconciler` leaves that delicate class untouched.
6. **`RequestConnect()`** — change to `_ = ResolveActivePhoneAsync()` (grant + connect the picked phone), keeping the existing `Log.Info("Connect requested from the tray.")` and no-op-when-empty guard (`RememberedPhoneIds.Count == 0`).
7. **`SetEventSounds(bool)`** — `_settings.EventSounds = enabled; _settings.Save();` then `Publish()` (so the tray tick refreshes).
8. **Startup active phone** — `Start()` should pick from the remembered set (via the resolver) rather than reading a single `PhoneDeviceId`. The migrated `RememberedPhoneIds` makes an upgraded user behave as before.

**Tests to update/add** (`ConnectionManagerTests` + its `Harness`):
- The `Harness` currently seeds `phoneDeviceId`. Update it to also seed `RememberedPhoneIds` (default: the single `PhoneId` so existing tests keep meaning "one remembered phone"). Where tests called `SelectPhone(PhoneId)`, replace with `SetPhoneRemembered(PhoneId, true)` (which resolves+connects the same way). Where they called `DeselectPhone()`, replace with `ClearRememberedPhones()`.
- Presence: `Harness.Link` (FakeLinkMonitor) now answers `ReadLinkStatusForAsync` per id (Task 4). For the single-phone tests, set `StatusById[PhoneId]` (or rely on the fallback `Status`).
- **New auto-pick tests** (use two ids `PhoneId`/`OtherPhoneId`):
  - Both remembered, only B present → resolver makes B active and connects it.
  - A active+present, B also present → stays on A (incumbent kept). Assert no switch to B.
  - A active but now absent, B present → switches to B on the next resolver tick (`h.Scheduler.Advance(Seconds(30))`).
  - `ClearRememberedPhones` → Idle, stops watching.
- Keep the three `RequestConnect` tests working (now via the resolver).

- [ ] **Step 1:** Read `ConnectionManager.cs` fully. Write the failing new auto-pick tests + adapt the existing `SelectPhone`/`DeselectPhone` call sites and `Harness`.
- [ ] **Step 2:** Run `dotnet test --filter FullyQualifiedName~ConnectionManagerTests` → the new tests FAIL, and the renamed call sites won't compile until Step 3.
- [ ] **Step 3:** Implement items 1-8 above; remove `SelectPhone`/`DeselectPhone`.
- [ ] **Step 4:** `dotnet test --filter FullyQualifiedName~ConnectionManagerTests` → PASS, then the FULL suite green (no regression to grace/reconcile/grant tests).
- [ ] **Step 5: Commit** — `git commit -m "ConnectionManager: remembered-phone resolver + event-sounds setting"`.

---

### Task 8: `TrayContext` + `Program` — sounds wiring + left-click menu

**Files:**
- Modify: `src/Klangbruecke/TrayContext.cs`
- Modify: `src/Klangbruecke/Program.cs`

View + composition; no unit test (WinForms). Gate: build + full suite + (deferred) user smoke.

- [ ] **Step 1: Dependencies + previous-state tracking.** Add `using Klangbruecke.Feedback;`. Add a field `private readonly ISoundPlayer _sound;` and constructor param; assign it. Add `private ConnectionState _lastSoundState;` initialized from `_connection.State` in the constructor (so the first real transition is measured from the startup state, not a default).

- [ ] **Step 2: Play on transition.** In `OnConnectionStateChanged` (which already fires on `StateChanged`), before/after the existing repaint, compute and play:
```csharp
ConnectionState next = _connection.State;
if (_settings.EventSounds)
{
    SoundEvent? sound = SoundPolicy.For(_lastSoundState, next);
    if (sound is { } e)
    {
        _sound.Play(e);
    }
}
_lastSoundState = next;
```
(Place after `ShowConnectionState()`/`UpdateIcon()`; keep those unchanged.)

- [ ] **Step 3: "Sounds" toggle.** In `RebuildMenuAsync`, add near the Calls / Reconnect-automatically toggles:
```csharp
var sounds = new ToolStripMenuItem("Sounds") { Checked = _settings.EventSounds };
sounds.Click += (_, _) => _connection.SetEventSounds(!_settings.EventSounds);
_menu.Items.Add(sounds);
```

- [ ] **Step 4: Left-click opens the menu.** Extract the rebuild-and-show from `OnMenuOpening` into `private async Task ShowContextMenuAsync()` (the `RebuildMenuAsync` + `_menu.Show(Cursor.Position)` body with the `_menuRebuilt` handling), and call it from `OnMenuOpening`'s cancel branch. Subscribe in the constructor:
```csharp
_icon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) _ = ShowContextMenuAsync(); };
```
Ensure the `_menuRebuilt` guard still prevents the Opening raised by `Show()` from re-rebuilding.

- [ ] **Step 5: Update the class doc.** `TrayContext`'s `<summary>` enumerates its dependencies and non-manager actions (updated in bundle 1). It now also holds `ISoundPlayer` and plays sounds on state changes, and left-click opens the menu. Update that paragraph so it stays accurate (this file's standing rule).

- [ ] **Step 6: Compose in `Program.cs`.** Add `using Klangbruecke.Feedback;`, construct `var sound = new SoundPlayer();`, and pass it to the `TrayContext` constructor.

- [ ] **Step 7:** `dotnet build` (no warnings) + full `dotnet test` (green).
- [ ] **Step 8: Commit** — `git commit -m "Tray: event sounds + Sounds toggle + left-click menu"`.

---

### Task 9: `TrayContext` — checkable phone submenu (remembered set)

**Files:**
- Modify: `src/Klangbruecke/TrayContext.cs`

Depends on Task 7's `SetPhoneRemembered`/`ClearRememberedPhones`. View; no unit test.

- [ ] **Step 1:** In `BuildPhoneMenuAsync`, change the phone list from single-select to **checkable**:
  - "None" item: `Checked = _settings.RememberedPhoneIds.Count == 0`; `Click` → `_connection.ClearRememberedPhones()`.
  - Each paired phone: `Checked = _settings.RememberedPhoneIds.Contains(device.Id)`; label shows the name, and append `" (connected)"` when `device.Id == _settings.PhoneDeviceId && _connection.State is ConnectionState.Connected or ConnectionState.Degraded`; `Click` → `_connection.SetPhoneRemembered(device.Id, !_settings.RememberedPhoneIds.Contains(device.Id))`.
  - Keep the existing enumeration try/catch and the "No paired devices found" empty case.
- [ ] **Step 2:** `dotnet build` + full `dotnet test` (green).
- [ ] **Step 3: Commit** — `git commit -m "Tray: checkable phone submenu for the remembered set"`.

---

## Self-Review

**Spec coverage:** left-click → Task 8; event sounds → Tasks 1 (policy), 5 (assets), 6 (player), 7 (setting), 8 (wiring+toggle); auto-pick → Tasks 2 (picker), 3 (settings+migration), 4 (fake presence), 7 (resolver+API), 9 (submenu). `FakeLinkMonitor` per-id → Task 4. Migration → Task 3. All spec sections map.

**Placeholder scan:** none — pure units and the WAV generator carry full code; integration tasks carry the exact new members and wiring.

**Type consistency:** `SoundEvent`/`SoundPolicy.For` (Task 1) used in Tasks 6/8. `ISoundPlayer.Play`/`SoundPlayer`/`FakeSoundPlayer` (Task 6) used in 8. `PhonePicker.Pick` (Task 2) used in 7. `Settings.RememberedPhoneIds`/`EventSounds`/`Migrate` (Task 3) used in 7/8/9. `ILinkMonitor.ReadLinkStatusForAsync` (Task 4) used in 7. `SetPhoneRemembered`/`ClearRememberedPhones`/`SetEventSounds` (Task 7) used in 8/9. Consistent.

**Cross-task note for the executor:** Task 7 removes `SelectPhone`/`DeselectPhone`; grep the tree for any remaining call sites (TrayContext's old submenu, tests) and update them — Task 9 replaces the TrayContext ones, and Task 7 updates the tests, so after both there must be zero references to the removed methods.
