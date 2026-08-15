# Phone Media Remote — Phase 3 (album art + seek) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add album art (SMTC thumbnail) and a working seek bar (advancing scrubber + scrub-to-seek) to the phone media remote, on top of the verified Phase 2 transport + now-playing-text channel.

**Architecture:** Extend the length-prefixed RFCOMM wire protocol with two lazy frames (`RequestArt` PC→phone, `AlbumArt` phone→PC) and real `PlaybackState` position. The phone downscales/JPEG-encodes/hashes art once per track and answers `RequestArt` on cache-miss; it sends one live position on play/pause/seek and never streams. The PC caches art by hash, sets the SMTC `DisplayUpdater.Thumbnail`, and drives the SMTC timeline — the scrubber is kept *advancing* by a **PC-side interpolation tick** in `CompanionLink` (driven by the existing `IScheduler`, so the phone stays silent per the power constraint). A PC-side `PlaybackPositionChangeRequested` becomes a `Command{command:"seek",positionMs}` to the phone.

**Tech Stack:** .NET 8 (`net8.0-windows10.0.19041.0`), WinRT `Windows.Media.SystemMediaTransportControls`, xUnit; Android (Kotlin 1.9.24, minSdk per project, `MediaSessionManager`/`MediaController`, `BluetoothServerSocket` RFCOMM).

**Spec:** `docs/superpowers/specs/2026-08-12-phone-media-remote-design.md`
**Handoff:** `handoffs/2026-08-15-phase3-album-art-seek.md`
**Empirical ground truth:** `docs/FINDINGS.md` §19–§21, `docs/superpowers/companion-followups.md` #3/#4/#5

## Global Constraints

- **Target `net8.0-windows10.0.19041.0`. Do not raise the minimum.** 19041 is the WinRT + dev-machine floor.
- **ASCII `Klangbruecke` everywhere.** No umlaut in any identifier, path, or package name.
- **`MediaProtocol.cs` (C#) is the wire SOURCE OF TRUTH; `Protocol.kt` mirrors it byte-for-byte.** A field-name or type-byte mismatch fails silently. Command JSON field is **`command`** (not `action`); status strings are **`playing`/`paused`**.
- **Frame = `[4-byte big-endian length][1-byte type][payload]`; length counts `type + payload`, not itself.** Control payloads are UTF-8 JSON; `AlbumArt` is raw binary (no base64).
- **Android side is 100% event-driven: no timers, no wakelocks, no position streaming, blocking I/O.** Position is *set, not streamed* — one `PlaybackState` per play/pause/seek.
- **Do NOT touch `ConnectionManager`, and do NOT add `ConfigureAwait(false)` anywhere in `Companion/` or `Connection/`.** Both break the single-threaded contract. (`RfcommCompanionTransport` keeps its existing `ConfigureAwait(false)` — it is below the marshaling seam; nothing new is added.)
- **`SmtcPublisher` is UI-thread-affine; `CompanionLink` is single-threaded.** Inbound is already marshaled onto the UI thread by `UiMarshalingTransport`. The interpolation tick uses `IScheduler`, whose callbacks are delivered on the UI thread.
- **Never crash the tray.** Every callback guards-and-logs; a malformed frame drops the connection, it does not throw out.
- **Volume is out of scope.** Do not add it.
- **Commit proactively** per task (project convention: commits land on `main`). Push only on request.
- **Multi-agent git hazard:** if subagents EDIT the repo, run them ONE AT A TIME and verify the branch after each; parallel editors corrupt the shared working dir. Read-only/scratchpad agents may parallelize.

---

## Wire protocol delta (reference — implemented across Tasks 1, 8)

New message-type bytes (append to the existing `Hello=0x01, NowPlaying=0x02, PlaybackState=0x03, Command=0x10`):

| Name         | Byte  | Direction | Payload |
| ------------ | ----- | --------- | ------- |
| `AlbumArt`   | `0x04`| phone→PC  | binary: `[2-byte BE hashLen][hash UTF-8 bytes][JPEG bytes]` |
| `RequestArt` | `0x11`| PC→phone  | JSON `{ "artHash": "<hash>" }` |

Field deltas on existing frames:
- `NowPlaying` JSON gains **`artHash`** (nullable string; `null`/absent ⇒ no art). `durationMs` already present.
- `PlaybackState` JSON already carries `positionMs`/`timestampMs`/`speed` — the phone now sends **real** values; the PC now **reads** them (currently ignored).
- `Command` JSON gains optional **`positionMs`** (present only for `command:"seek"`, omitted otherwise) and a new action string **`"seek"`**.

`artHash` is an **opaque string** to the PC — only the phone computes it. The PC compares strings and keys its cache by them; no hash algorithm is mirrored.

## File structure

**PC — `src/Klangbruecke/Companion/`**
- `ProtocolMessage.cs` *(modify)* — message-type bytes; `NowPlayingPayload.ArtHash`; `CommandPayload.PositionMs`; new `RequestArtPayload`.
- `MediaProtocol.cs` *(modify)* — `EncodeRequestArt`, `EncodeSeek`; split decode into `DecodeNowPlaying` + `DecodePlaybackState` (returns a `PlaybackUpdate`); `TryReadAlbumArt`.
- `MediaSnapshot.cs` *(modify)* — add `DurationMs`, `ArtHash` (string?), `Art` (byte[]?).
- `PlaybackUpdate.cs` *(create)* — immutable `(IsPlaying, PositionMs, TimestampMs, Speed)` from a `PlaybackState` frame.
- `TimelineMath.cs` *(create)* — pure `PositionAt(basePositionMs, elapsed, speed, durationMs)`.
- `ArtCache.cs` *(create)* — bounded hash→bytes cache.
- `ISmtcPublisher.cs` *(modify)* — add `UpdateTimeline(TimelineState)` and `event SeekRequested`; new `TimelineState` record.
- `SmtcPublisher.cs` *(modify)* — thumbnail from `Art` (gated by hash), `UpdateTimelineProperties`/`PlaybackRate`, subscribe `PlaybackPositionChangeRequested`.
- `CompanionLink.cs` *(modify)* — own `ArtCache`; request art on miss; apply `AlbumArt`; fold position; arm/disarm interpolation tick; forward seek.

**PC — tests (`tests/Klangbruecke.Tests/`)**
- `Companion/MediaProtocolTests.cs` *(modify)*, `Companion/CompanionLinkTests.cs` *(modify)*, `Companion/MediaSnapshotTests.cs` *(modify)*
- `Companion/ArtCacheTests.cs` *(create)*, `Companion/TimelineMathTests.cs` *(create)*
- `Fakes/FakeSmtcPublisher.cs` *(modify)* — record `UpdateTimeline`, raise `SeekRequested`.

**Android — `android/app/src/main/java/klangbruecke/remote/`**
- `Protocol.kt` *(modify)* — new type bytes, `COMMAND_SEEK`, `encodeNowPlaying(...artHash)`, `encodeAlbumArt`, `decodeRequestArt`, `decodeSeekPositionMs`.
- `AlbumArtCodec.kt` *(create)* — bitmap → downscale (~400 px) → JPEG → hash.
- `MediaBridge.kt` *(modify)* — `Snapshot` gains `artHash`/`positionMs`/`timestampMs`/`speed`; compute+cache art; `currentArt()`; `applySeek(ms)`; live position.
- `RemoteService.kt` *(modify)* — send `artHash` + real position; answer `RequestArt` with `AlbumArt`; apply `seek`.

**Version / packaging**
- `packaging/AppxManifest.xml` *(modify)* `0.3.0.0`→`0.3.1.0`; `src/Klangbruecke/Klangbruecke.csproj` *(modify)* same.
- `android/app/build.gradle.kts` *(modify)* `versionCode 1`→`2`, `versionName "1.0"`→`"0.3.1"`.

---

### Task 1: PC wire protocol — new frames, DTOs, encode/decode (pure)

**Files:**
- Modify: `src/Klangbruecke/Companion/ProtocolMessage.cs`
- Modify: `src/Klangbruecke/Companion/MediaProtocol.cs`
- Create: `src/Klangbruecke/Companion/PlaybackUpdate.cs`
- Modify: `src/Klangbruecke/Companion/MediaSnapshot.cs`
- Test: `tests/Klangbruecke.Tests/Companion/MediaProtocolTests.cs`

**Interfaces:**
- Produces:
  - `MessageType.AlbumArt = 0x04`, `MessageType.RequestArt = 0x11`
  - `byte[] MediaProtocol.EncodeRequestArt(string artHash)`
  - `byte[] MediaProtocol.EncodeSeek(long positionMs)`
  - `MediaSnapshot MediaProtocol.DecodeNowPlaying(ReadOnlyMemory<byte> payload, MediaSnapshot prior)`
  - `PlaybackUpdate MediaProtocol.DecodePlaybackState(ReadOnlyMemory<byte> payload)`
  - `bool MediaProtocol.TryReadAlbumArt(ReadOnlyMemory<byte> payload, out string hash, out byte[] jpeg)`
  - `record PlaybackUpdate(bool IsPlaying, long PositionMs, long TimestampMs, double Speed)`
  - `MediaSnapshot` gains `long DurationMs`, `string? ArtHash`, `byte[]? Art`

- [ ] **Step 1: Extend the DTOs and message types.** In `ProtocolMessage.cs`, add to the enum:

```csharp
internal enum MessageType : byte
{
    Hello = 0x01,
    NowPlaying = 0x02,
    PlaybackState = 0x03,
    AlbumArt = 0x04,
    Command = 0x10,
    RequestArt = 0x11,
}
```

Add `artHash` to `NowPlayingPayload`, `positionMs` to `CommandPayload`, and a new `RequestArtPayload`:

```csharp
internal sealed record NowPlayingPayload(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("album")] string Album,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("hasSession")] bool HasSession,
    [property: JsonPropertyName("artHash")] string? ArtHash = null);

internal sealed record CommandPayload(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("positionMs")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? PositionMs = null);

internal sealed record RequestArtPayload(
    [property: JsonPropertyName("artHash")] string ArtHash);
```

- [ ] **Step 2: Add the `MediaSnapshot` fields.** In `MediaSnapshot.cs`, extend the record and `Empty`:

```csharp
internal sealed record MediaSnapshot(
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    bool HasSession,
    long DurationMs = 0,
    string? ArtHash = null,
    byte[]? Art = null)
{
    public static MediaSnapshot Empty { get; } = new("", "", "", false, false);
}
```

- [ ] **Step 3: Create `PlaybackUpdate.cs`.**

```csharp
namespace Klangbruecke.Companion;

/// <summary>
/// One PlaybackState frame decoded: whether the phone is playing, plus the timeline data the PC needs
/// to drive an advancing seek bar. Position is <em>live as of send</em> (the phone interpolated it);
/// the PC re-bases its own clock from it and interpolates forward, so the phone need never stream.
/// </summary>
internal sealed record PlaybackUpdate(bool IsPlaying, long PositionMs, long TimestampMs, double Speed);
```

- [ ] **Step 4: Write failing tests** in `MediaProtocolTests.cs`:

```csharp
[Fact]
public void EncodeRequestArt_FramesTypeAndHash()
{
    var frame = MediaProtocol.EncodeRequestArt("abc123");
    Assert.Equal((byte)MessageType.RequestArt, frame[4]);
    int len = (frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3];
    Assert.Equal(frame.Length - 4, len);
    Assert.Contains("abc123", System.Text.Encoding.UTF8.GetString(frame, 5, frame.Length - 5));
}

[Fact]
public void EncodeSeek_IsACommandFrame_WithPositionMs()
{
    var frame = MediaProtocol.EncodeSeek(42000);
    Assert.Equal((byte)MessageType.Command, frame[4]);
    string json = System.Text.Encoding.UTF8.GetString(frame, 5, frame.Length - 5);
    Assert.Contains("\"command\":\"seek\"", json);
    Assert.Contains("\"positionMs\":42000", json);
}

[Fact]
public void DecodeNowPlaying_CarriesDurationAndArtHash_AndClearsStaleArt()
{
    var prior = MediaSnapshot.Empty with { Art = new byte[] { 1, 2, 3 } };
    var payload = System.Text.Encoding.UTF8.GetBytes(
        "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"Al\",\"durationMs\":200000,\"hasSession\":true,\"artHash\":\"h1\"}");
    var snap = MediaProtocol.DecodeNowPlaying(payload, prior);
    Assert.Equal(200000, snap.DurationMs);
    Assert.Equal("h1", snap.ArtHash);
    Assert.Null(snap.Art); // new NowPlaying must not carry the previous track's image
}

[Fact]
public void DecodePlaybackState_ReadsPositionSpeedAndPlaying()
{
    var payload = System.Text.Encoding.UTF8.GetBytes(
        "{\"status\":\"playing\",\"positionMs\":30000,\"timestampMs\":123,\"speed\":1.0}");
    var u = MediaProtocol.DecodePlaybackState(payload);
    Assert.True(u.IsPlaying);
    Assert.Equal(30000, u.PositionMs);
    Assert.Equal(1.0, u.Speed);
}

[Fact]
public void TryReadAlbumArt_SplitsHashFromJpeg()
{
    byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 };
    var frame = MediaProtocolTestHelper.BuildAlbumArtPayload("hash42", jpeg);
    Assert.True(MediaProtocol.TryReadAlbumArt(frame, out string hash, out byte[] outJpeg));
    Assert.Equal("hash42", hash);
    Assert.Equal(jpeg, outJpeg);
}
```

Add a tiny helper at the bottom of the test file (mirrors the phone's binary layout, so the test proves the PC reads what the phone writes):

```csharp
internal static class MediaProtocolTestHelper
{
    // [2-byte BE hashLen][hash UTF-8][jpeg] — the AlbumArt payload the phone sends (no length prefix; the
    // outer frame already stripped it before FrameReceived/TryReadAlbumArt sees the body).
    public static byte[] BuildAlbumArtPayload(string hash, byte[] jpeg)
    {
        byte[] h = System.Text.Encoding.UTF8.GetBytes(hash);
        var buf = new byte[2 + h.Length + jpeg.Length];
        buf[0] = (byte)(h.Length >> 8);
        buf[1] = (byte)h.Length;
        h.CopyTo(buf, 2);
        jpeg.CopyTo(buf, 2 + h.Length);
        return buf;
    }
}
```

- [ ] **Step 5: Run tests — verify they fail.** Run: `dotnet test tests/Klangbruecke.Tests --filter FullyQualifiedName~MediaProtocolTests` — expect FAIL (methods not defined).

- [ ] **Step 6: Implement in `MediaProtocol.cs`.** Add `using System.Text.Json.Serialization;` if needed. Add encoders and the split decoders; replace `DecodeInbound` with `DecodeNowPlaying`/`DecodePlaybackState`; add `TryReadAlbumArt`. Add a `Seek` case to `WireName` is NOT needed — seek is encoded directly:

```csharp
public static byte[] EncodeRequestArt(string artHash)
{
    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new RequestArtPayload(artHash), JsonOptions);
    return Frame(MessageType.RequestArt, payload);
}

public static byte[] EncodeSeek(long positionMs)
{
    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new CommandPayload("seek", positionMs), JsonOptions);
    return Frame(MessageType.Command, payload);
}

public static MediaSnapshot DecodeNowPlaying(ReadOnlyMemory<byte> payload, MediaSnapshot prior)
{
    NowPlayingPayload np = Deserialize<NowPlayingPayload>(payload);
    return prior with
    {
        Title = np.Title,
        Artist = np.Artist,
        Album = np.Album,
        HasSession = np.HasSession,
        DurationMs = np.DurationMs,
        ArtHash = np.ArtHash,
        Art = null, // a new NowPlaying re-resolves art from the cache; never keep the old image
    };
}

public static PlaybackUpdate DecodePlaybackState(ReadOnlyMemory<byte> payload)
{
    PlaybackStatePayload ps = Deserialize<PlaybackStatePayload>(payload);
    return new PlaybackUpdate(ps.Status == "playing", ps.PositionMs, ps.TimestampMs, ps.Speed);
}

/// <summary>
/// Reads the binary AlbumArt body: [2-byte big-endian hashLen][hash UTF-8][JPEG bytes]. Returns false
/// on a truncated/garbled body rather than throwing - a bad art frame must not take the link down.
/// </summary>
public static bool TryReadAlbumArt(ReadOnlyMemory<byte> payload, out string hash, out byte[] jpeg)
{
    hash = string.Empty;
    jpeg = Array.Empty<byte>();
    ReadOnlySpan<byte> span = payload.Span;
    if (span.Length < 2) return false;

    int hashLen = (span[0] << 8) | span[1];
    if (hashLen < 0 || span.Length < 2 + hashLen) return false;

    hash = System.Text.Encoding.UTF8.GetString(span.Slice(2, hashLen));
    jpeg = span.Slice(2 + hashLen).ToArray();
    return true;
}
```

Delete the old `DecodeInbound` method. (Its callers are the tests updated in Step 4 and `CompanionLink`, updated in Task 5/6.)

- [ ] **Step 7: Update the two Phase-2 `DecodeInbound` tests** in `MediaProtocolTests.cs` to call `DecodeNowPlaying`/`DecodePlaybackState` (the behavior they assert — text replace, playing flip — is unchanged; only the method names move). The `TryReadFrame`/`EncodeCommand` tests are untouched.

- [ ] **Step 8: Run tests — verify pass.** Run: `dotnet test tests/Klangbruecke.Tests --filter FullyQualifiedName~MediaProtocolTests` — expect PASS.

- [ ] **Step 9: Commit.**

```bash
git add src/Klangbruecke/Companion/ProtocolMessage.cs src/Klangbruecke/Companion/MediaProtocol.cs src/Klangbruecke/Companion/PlaybackUpdate.cs src/Klangbruecke/Companion/MediaSnapshot.cs tests/Klangbruecke.Tests/Companion/MediaProtocolTests.cs
git commit -m "feat(companion): Phase 3 wire protocol — album-art + seek frames"
```

---

### Task 2: `ArtCache` — album art keyed by hash (pure)

**Files:**
- Create: `src/Klangbruecke/Companion/ArtCache.cs`
- Test: `tests/Klangbruecke.Tests/Companion/ArtCacheTests.cs`

**Interfaces:**
- Produces: `ArtCache` with `bool TryGet(string hash, out byte[] jpeg)`, `void Put(string hash, byte[] jpeg)`. Bounded to the most-recent 4 tracks (only the current track's art is ever needed; a tiny bound keeps memory flat without a policy anyone must reason about).

- [ ] **Step 1: Write failing tests** in `ArtCacheTests.cs`:

```csharp
using Klangbruecke.Companion;
using Xunit;

namespace Klangbruecke.Tests.Companion;

public sealed class ArtCacheTests
{
    [Fact]
    public void Miss_BeforePut()
    {
        var cache = new ArtCache();
        Assert.False(cache.TryGet("h", out _));
    }

    [Fact]
    public void Hit_AfterPut()
    {
        var cache = new ArtCache();
        cache.Put("h", new byte[] { 1, 2, 3 });
        Assert.True(cache.TryGet("h", out byte[] bytes));
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    [Fact]
    public void EvictsOldest_BeyondCapacity()
    {
        var cache = new ArtCache(capacity: 2);
        cache.Put("a", new byte[] { 1 });
        cache.Put("b", new byte[] { 2 });
        cache.Put("c", new byte[] { 3 }); // evicts "a"
        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
    }
}
```

- [ ] **Step 2: Run — verify fail.** Run: `dotnet test tests/Klangbruecke.Tests --filter FullyQualifiedName~ArtCacheTests` — FAIL.

- [ ] **Step 3: Implement `ArtCache.cs`.**

```csharp
namespace Klangbruecke.Companion;

/// <summary>
/// Album art keyed by the phone's opaque art hash. Insertion-ordered and bounded: only the current
/// track's art is ever needed, so a small cap keeps memory flat and evicts the oldest first. Not
/// thread-safe by design - it lives on the single-threaded CompanionLink like everything else here.
/// </summary>
internal sealed class ArtCache
{
    private readonly int _capacity;
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, byte[]> _bytes = new();

    public ArtCache(int capacity = 4) => _capacity = capacity < 1 ? 1 : capacity;

    public bool TryGet(string hash, out byte[] jpeg) => _bytes.TryGetValue(hash, out jpeg!);

    public void Put(string hash, byte[] jpeg)
    {
        if (_bytes.ContainsKey(hash))
        {
            _bytes[hash] = jpeg;
            return;
        }

        _bytes[hash] = jpeg;
        _order.AddLast(hash);

        while (_order.Count > _capacity)
        {
            string oldest = _order.First!.Value;
            _order.RemoveFirst();
            _bytes.Remove(oldest);
        }
    }
}
```

- [ ] **Step 4: Run — verify pass.** Run: `dotnet test tests/Klangbruecke.Tests --filter FullyQualifiedName~ArtCacheTests` — PASS.

- [ ] **Step 5: Commit.**

```bash
git add src/Klangbruecke/Companion/ArtCache.cs tests/Klangbruecke.Tests/Companion/ArtCacheTests.cs
git commit -m "feat(companion): ArtCache — album art keyed by hash"
```

---

### Task 3: `TimelineMath` — seek-bar interpolation (pure)

**Files:**
- Create: `src/Klangbruecke/Companion/TimelineMath.cs`
- Test: `tests/Klangbruecke.Tests/Companion/TimelineMathTests.cs`

**Interfaces:**
- Produces: `static long TimelineMath.PositionAt(long basePositionMs, TimeSpan elapsed, double speed, long durationMs)` — the interpolated position, clamped to `[0, durationMs]` when `durationMs > 0`.

- [ ] **Step 1: Write failing tests** in `TimelineMathTests.cs`:

```csharp
using Klangbruecke.Companion;
using Xunit;

namespace Klangbruecke.Tests.Companion;

public sealed class TimelineMathTests
{
    [Fact]
    public void Advances_ByElapsedTimesSpeed()
        => Assert.Equal(32000, TimelineMath.PositionAt(30000, TimeSpan.FromSeconds(2), 1.0, 200000));

    [Fact]
    public void Paused_DoesNotAdvance()
        => Assert.Equal(30000, TimelineMath.PositionAt(30000, TimeSpan.FromSeconds(5), 0.0, 200000));

    [Fact]
    public void ClampsToDuration()
        => Assert.Equal(200000, TimelineMath.PositionAt(199000, TimeSpan.FromSeconds(5), 1.0, 200000));

    [Fact]
    public void NoDuration_DoesNotClamp()
        => Assert.Equal(35000, TimelineMath.PositionAt(30000, TimeSpan.FromSeconds(5), 1.0, 0));
}
```

- [ ] **Step 2: Run — verify fail.** Run: `dotnet test tests/Klangbruecke.Tests --filter FullyQualifiedName~TimelineMathTests` — FAIL.

- [ ] **Step 3: Implement `TimelineMath.cs`.**

```csharp
namespace Klangbruecke.Companion;

/// <summary>
/// The one calculation the advancing seek bar needs, pulled out pure so it is tested without a clock,
/// a scheduler or SMTC. The phone sends a live position on play/pause/seek; the PC advances it locally
/// by <c>elapsed x speed</c> so the bar moves while the phone stays silent (the power constraint).
/// </summary>
internal static class TimelineMath
{
    public static long PositionAt(long basePositionMs, TimeSpan elapsed, double speed, long durationMs)
    {
        long pos = basePositionMs + (long)(elapsed.TotalMilliseconds * speed);
        if (pos < 0) pos = 0;
        if (durationMs > 0 && pos > durationMs) pos = durationMs;
        return pos;
    }
}
```

- [ ] **Step 4: Run — verify pass.** Run: `dotnet test tests/Klangbruecke.Tests --filter FullyQualifiedName~TimelineMathTests` — PASS.

- [ ] **Step 5: Commit.**

```bash
git add src/Klangbruecke/Companion/TimelineMath.cs tests/Klangbruecke.Tests/Companion/TimelineMathTests.cs
git commit -m "feat(companion): TimelineMath — seek-bar interpolation"
```

---

### Task 4: Extend the `ISmtcPublisher` seam + fake (timeline + seek)

**Files:**
- Modify: `src/Klangbruecke/Companion/ISmtcPublisher.cs`
- Modify: `tests/Klangbruecke.Tests/Fakes/FakeSmtcPublisher.cs`

**Interfaces:**
- Produces:
  - `record TimelineState(long PositionMs, long DurationMs, bool IsPlaying, double Speed)`
  - `void ISmtcPublisher.UpdateTimeline(TimelineState timeline)`
  - `event EventHandler<long>? ISmtcPublisher.SeekRequested` (positionMs the user scrubbed to)
  - `FakeSmtcPublisher.Timelines` (recorded), `FakeSmtcPublisher.RaiseSeek(long ms)`
- Consumes: nothing new.

- [ ] **Step 1: Extend `ISmtcPublisher.cs`.** Add the record and members:

```csharp
namespace Klangbruecke.Companion;

/// <summary>Position/duration/speed for the SMTC timeline - the seek bar's geometry and rate.</summary>
internal sealed record TimelineState(long PositionMs, long DurationMs, bool IsPlaying, double Speed);

internal interface ISmtcPublisher : IDisposable
{
    void Publish(MediaSnapshot snapshot);

    /// <summary>Set the SMTC timeline so ModernFlyouts draws and advances the scrubber.</summary>
    void UpdateTimeline(TimelineState timeline);

    event EventHandler<MediaCommand>? CommandRequested;

    /// <summary>The user scrubbed the SMTC seek bar. Forward the target position to the phone.</summary>
    event EventHandler<long>? SeekRequested;
}
```

- [ ] **Step 2: Extend `FakeSmtcPublisher.cs`.** Add recording + a raise:

```csharp
public List<TimelineState> Timelines { get; } = new();

public event EventHandler<long>? SeekRequested;

public void UpdateTimeline(TimelineState timeline) => Timelines.Add(timeline);

/// <summary>The user scrubbed the seek bar. Forwards to whatever the link subscribed.</summary>
public void RaiseSeek(long positionMs) => SeekRequested?.Invoke(this, positionMs);
```

- [ ] **Step 3: Build the test project — verify it compiles.** Run: `dotnet build tests/Klangbruecke.Tests` — expect PASS (the fake now satisfies the extended interface; `SmtcPublisher` will not compile yet and is built in Task 7, so build *only* the test project here — it references the src project, so if the src fails add temporary stubs; see note). 

  Note: `SmtcPublisher` (the real one) implements `ISmtcPublisher`, so adding members breaks its build until Task 7. To keep the tree buildable between tasks, add the two new members to `SmtcPublisher.cs` as minimal stubs in this task (`public void UpdateTimeline(TimelineState timeline) { }` and `public event EventHandler<long>? SeekRequested;`), then flesh them out in Task 7. This keeps every commit compiling.

- [ ] **Step 4: Commit.**

```bash
git add src/Klangbruecke/Companion/ISmtcPublisher.cs src/Klangbruecke/Companion/SmtcPublisher.cs tests/Klangbruecke.Tests/Fakes/FakeSmtcPublisher.cs
git commit -m "feat(companion): ISmtcPublisher gains timeline + seek seam"
```

---

### Task 5: `CompanionLink` — album art request-on-miss + apply

**Files:**
- Modify: `src/Klangbruecke/Companion/CompanionLink.cs`
- Test: `tests/Klangbruecke.Tests/Companion/CompanionLinkTests.cs`

**Interfaces:**
- Consumes: `MediaProtocol.DecodeNowPlaying`, `MediaProtocol.EncodeRequestArt`, `MediaProtocol.TryReadAlbumArt`, `ArtCache`, `FakeSmtcPublisher.Published`, `FakeCompanionTransport.{Sent,Raise}`.
- Produces: on a `NowPlaying` with an `artHash` not in cache, `CompanionLink` sends a `RequestArt` frame; on the matching `AlbumArt` frame it caches the bytes and republishes the snapshot with `Art` set.

- [ ] **Step 1: Write failing tests** in `CompanionLinkTests.cs` (append). Reuse the existing `FrameOf` helper; add a binary-frame helper:

```csharp
/// <summary>An AlbumArt frame as the transport delivers one: [type][2-byte BE hashLen][hash][jpeg], no length prefix.</summary>
private static byte[] AlbumArtFrameOf(string hash, byte[] jpeg)
{
    byte[] h = Encoding.UTF8.GetBytes(hash);
    var frame = new byte[1 + 2 + h.Length + jpeg.Length];
    frame[0] = (byte)MessageType.AlbumArt;
    frame[1] = (byte)(h.Length >> 8);
    frame[2] = (byte)h.Length;
    h.CopyTo(frame, 3);
    jpeg.CopyTo(frame, 3 + h.Length);
    return frame;
}

[Fact]
public async Task NowPlayingWithNewArtHash_RequestsArt()
{
    var t = new FakeCompanionTransport { NextConnectResult = true };
    var p = new FakeSmtcPublisher();
    using var link = new CompanionLink(t, p, NewScheduler());
    await link.StartAsync();
    t.Raise(FrameOf(MessageType.NowPlaying, "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"\",\"durationMs\":0,\"hasSession\":true,\"artHash\":\"h1\"}"));
    Assert.Contains(t.Sent, f => f[4] == (byte)MessageType.RequestArt);
}

[Fact]
public async Task AlbumArtFrame_AttachesBytesToSnapshotAndRepublishes()
{
    var t = new FakeCompanionTransport { NextConnectResult = true };
    var p = new FakeSmtcPublisher();
    using var link = new CompanionLink(t, p, NewScheduler());
    await link.StartAsync();
    t.Raise(FrameOf(MessageType.NowPlaying, "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"\",\"durationMs\":0,\"hasSession\":true,\"artHash\":\"h1\"}"));
    t.Raise(AlbumArtFrameOf("h1", new byte[] { 9, 8, 7 }));
    Assert.Equal(new byte[] { 9, 8, 7 }, p.Published.Last().Art);
}

[Fact]
public async Task ArtHashAlreadyCached_DoesNotRequestAgain()
{
    var t = new FakeCompanionTransport { NextConnectResult = true };
    var p = new FakeSmtcPublisher();
    using var link = new CompanionLink(t, p, NewScheduler());
    await link.StartAsync();
    t.Raise(FrameOf(MessageType.NowPlaying, "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"\",\"durationMs\":0,\"hasSession\":true,\"artHash\":\"h1\"}"));
    t.Raise(AlbumArtFrameOf("h1", new byte[] { 1 }));
    int before = t.Sent.Count(f => f[4] == (byte)MessageType.RequestArt);
    // Same track again (e.g. a metadata refresh): art is cached, no second request.
    t.Raise(FrameOf(MessageType.NowPlaying, "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"\",\"durationMs\":0,\"hasSession\":true,\"artHash\":\"h1\"}"));
    Assert.Equal(before, t.Sent.Count(f => f[4] == (byte)MessageType.RequestArt));
    Assert.Equal(new byte[] { 1 }, p.Published.Last().Art); // served from cache
}
```

- [ ] **Step 2: Run — verify fail.** Run: `dotnet test tests/Klangbruecke.Tests --filter FullyQualifiedName~CompanionLinkTests` — FAIL.

- [ ] **Step 3: Implement in `CompanionLink.cs`.** Add `private readonly ArtCache _artCache = new();`. Replace `OnFrameReceived` to dispatch on type and add the art helpers:

```csharp
private void OnFrameReceived(object? sender, byte[] frame)
{
    try
    {
        if (frame is null || frame.Length == 0) return;

        var type = (MessageType)frame[0];
        var payload = new ReadOnlyMemory<byte>(frame, 1, frame.Length - 1);

        switch (type)
        {
            case MessageType.NowPlaying:
                _snapshot = MediaProtocol.DecodeNowPlaying(payload, _snapshot);
                ResolveArt();
                _publisher.Publish(_snapshot);
                break;

            case MessageType.PlaybackState:
                OnPlaybackState(MediaProtocol.DecodePlaybackState(payload)); // Task 6
                break;

            case MessageType.AlbumArt:
                OnAlbumArt(payload);
                break;

            // Hello / Command / RequestArt: nothing to fold on the PC side.
        }
    }
    catch (Exception ex)
    {
        Log.Error("The companion link failed to handle an inbound frame.", ex);
    }
}

/// <summary>
/// After a NowPlaying, attach cached art if we have it, or ask the phone for it once. A track with no
/// artHash simply has no image. Only a cache-miss sends RequestArt - art is fetched once per track.
/// </summary>
private void ResolveArt()
{
    string? hash = _snapshot.ArtHash;
    if (string.IsNullOrEmpty(hash)) return;

    if (_artCache.TryGet(hash, out byte[] jpeg))
    {
        _snapshot = _snapshot with { Art = jpeg };
    }
    else
    {
        _ = SendAsync(MediaProtocol.EncodeRequestArt(hash), "RequestArt");
    }
}

private void OnAlbumArt(ReadOnlyMemory<byte> payload)
{
    if (!MediaProtocol.TryReadAlbumArt(payload, out string hash, out byte[] jpeg))
    {
        Log.Warn("Dropped a malformed AlbumArt frame.");
        return;
    }

    _artCache.Put(hash, jpeg);

    // Only republish if this is the art for the track showing now; a late reply for an old track is
    // cached but not shown.
    if (hash == _snapshot.ArtHash)
    {
        _snapshot = _snapshot with { Art = jpeg };
        _publisher.Publish(_snapshot);
    }
}
```

Add a temporary stub `private void OnPlaybackState(PlaybackUpdate update) { _snapshot = _snapshot with { IsPlaying = update.IsPlaying }; _publisher.Publish(_snapshot); }` so this task compiles and Phase-2 PlaybackState tests keep passing; Task 6 replaces it.

- [ ] **Step 4: Run — verify pass.** Run: `dotnet test tests/Klangbruecke.Tests --filter FullyQualifiedName~CompanionLinkTests` — PASS (including the Phase-2 tests).

- [ ] **Step 5: Commit.**

```bash
git add src/Klangbruecke/Companion/CompanionLink.cs tests/Klangbruecke.Tests/Companion/CompanionLinkTests.cs
git commit -m "feat(companion): request album art on cache-miss, apply on arrival"
```

---

### Task 6: `CompanionLink` — position fold, advancing interpolation tick, seek forward

**Files:**
- Modify: `src/Klangbruecke/Companion/CompanionLink.cs`
- Test: `tests/Klangbruecke.Tests/Companion/CompanionLinkTests.cs`

**Interfaces:**
- Consumes: `TimelineMath.PositionAt`, `IScheduler.{Now, SchedulePeriodic}`, `ISmtcPublisher.{UpdateTimeline, SeekRequested}`, `MediaProtocol.EncodeSeek`, `TimelineState`.
- Produces: on a `playing` `PlaybackState`, `CompanionLink` pushes an immediate `UpdateTimeline` and arms a 1 s periodic tick that pushes an *advancing* position; on `paused` it pushes once and disarms; on `SeekRequested` it sends a `seek` `Command`.

- [ ] **Step 1: Write failing tests** in `CompanionLinkTests.cs`:

```csharp
[Fact]
public async Task PlaybackState_PushesTimelineImmediately()
{
    var t = new FakeCompanionTransport { NextConnectResult = true };
    var p = new FakeSmtcPublisher();
    using var link = new CompanionLink(t, p, NewScheduler());
    await link.StartAsync();
    t.Raise(FrameOf(MessageType.NowPlaying, "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"\",\"durationMs\":200000,\"hasSession\":true}"));
    t.Raise(FrameOf(MessageType.PlaybackState, "{\"status\":\"playing\",\"positionMs\":30000,\"timestampMs\":0,\"speed\":1.0}"));
    Assert.Equal(30000, p.Timelines.Last().PositionMs);
    Assert.Equal(200000, p.Timelines.Last().DurationMs);
}

[Fact]
public async Task WhilePlaying_TickAdvancesTheSeekBar()
{
    var scheduler = NewScheduler();
    var t = new FakeCompanionTransport { NextConnectResult = true };
    var p = new FakeSmtcPublisher();
    using var link = new CompanionLink(t, p, scheduler);
    await link.StartAsync();
    t.Raise(FrameOf(MessageType.NowPlaying, "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"\",\"durationMs\":200000,\"hasSession\":true}"));
    t.Raise(FrameOf(MessageType.PlaybackState, "{\"status\":\"playing\",\"positionMs\":30000,\"timestampMs\":0,\"speed\":1.0}"));

    scheduler.Advance(TimeSpan.FromSeconds(1));
    Assert.Equal(31000, p.Timelines.Last().PositionMs);
    scheduler.Advance(TimeSpan.FromSeconds(1));
    Assert.Equal(32000, p.Timelines.Last().PositionMs);
}

[Fact]
public async Task Paused_StopsAdvancing()
{
    var scheduler = NewScheduler();
    var t = new FakeCompanionTransport { NextConnectResult = true };
    var p = new FakeSmtcPublisher();
    using var link = new CompanionLink(t, p, scheduler);
    await link.StartAsync();
    t.Raise(FrameOf(MessageType.NowPlaying, "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"\",\"durationMs\":200000,\"hasSession\":true}"));
    t.Raise(FrameOf(MessageType.PlaybackState, "{\"status\":\"paused\",\"positionMs\":45000,\"timestampMs\":0,\"speed\":0.0}"));
    int count = p.Timelines.Count;
    scheduler.Advance(TimeSpan.FromSeconds(5));
    Assert.Equal(count, p.Timelines.Count); // no tick while paused
    Assert.Equal(45000, p.Timelines.Last().PositionMs);
}

[Fact]
public async Task SeekRequested_SendsSeekCommandWithPosition()
{
    var t = new FakeCompanionTransport { NextConnectResult = true };
    var p = new FakeSmtcPublisher();
    using var link = new CompanionLink(t, p, NewScheduler());
    await link.StartAsync();
    p.RaiseSeek(90000);
    byte[]? seek = t.Sent.LastOrDefault(f => f[4] == (byte)MessageType.Command);
    Assert.NotNull(seek);
    Assert.Contains("\"command\":\"seek\"", Encoding.UTF8.GetString(seek!, 5, seek!.Length - 5));
    Assert.Contains("\"positionMs\":90000", Encoding.UTF8.GetString(seek!, 5, seek!.Length - 5));
}

[Fact]
public async Task Disconnect_DisarmsTheTick()
{
    var scheduler = NewScheduler();
    var t = new FakeCompanionTransport { NextConnectResult = true };
    var p = new FakeSmtcPublisher();
    using var link = new CompanionLink(t, p, scheduler);
    await link.StartAsync();
    t.Raise(FrameOf(MessageType.NowPlaying, "{\"title\":\"T\",\"artist\":\"A\",\"album\":\"\",\"durationMs\":200000,\"hasSession\":true}"));
    t.Raise(FrameOf(MessageType.PlaybackState, "{\"status\":\"playing\",\"positionMs\":0,\"timestampMs\":0,\"speed\":1.0}"));
    t.RaiseDisconnected();
    int count = p.Timelines.Count;
    scheduler.Advance(TimeSpan.FromSeconds(5));
    Assert.Equal(count, p.Timelines.Count); // tick stopped when the link dropped
}
```

- [ ] **Step 2: Run — verify fail.** Run: `dotnet test tests/Klangbruecke.Tests --filter FullyQualifiedName~CompanionLinkTests` — FAIL.

- [ ] **Step 3: Implement in `CompanionLink.cs`.** Add fields and wire `SeekRequested` in the constructor:

```csharp
private const int TickSeconds = 1;

private IDisposable? _tick;
private long _basePositionMs;
private DateTimeOffset _baseAt;
private double _speed;
```

In the constructor, after the existing `_publisher.CommandRequested += OnCommandRequested;` add:

```csharp
_publisher.SeekRequested += OnSeekRequested;
```

Replace the Task-5 stub `OnPlaybackState` with the real one, plus the tick and seek handlers:

```csharp
private void OnPlaybackState(PlaybackUpdate update)
{
    _snapshot = _snapshot with { IsPlaying = update.IsPlaying };
    _publisher.Publish(_snapshot);

    // Re-base the local clock from the phone's live position and push it once immediately.
    _basePositionMs = update.PositionMs;
    _baseAt = _scheduler.Now;
    _speed = update.Speed;
    PushTimeline();

    if (update.IsPlaying)
    {
        ArmTick();
    }
    else
    {
        DisarmTick();
    }
}

private void PushTimeline()
{
    long pos = TimelineMath.PositionAt(_basePositionMs, _scheduler.Now - _baseAt, _speed, _snapshot.DurationMs);
    _publisher.UpdateTimeline(new TimelineState(pos, _snapshot.DurationMs, _snapshot.IsPlaying, _speed));
}

private void ArmTick()
{
    if (_tick is not null) return; // already advancing
    _tick = _scheduler.SchedulePeriodic(TimeSpan.FromSeconds(TickSeconds), PushTimeline);
}

private void DisarmTick()
{
    _tick?.Dispose();
    _tick = null;
}

private void OnSeekRequested(object? sender, long positionMs)
    => _ = SendAsync(MediaProtocol.EncodeSeek(positionMs), "seek");
```

Disarm the tick anywhere the surface is torn down. In `OnDisconnected`, before `ScheduleReconnect();`, add `DisarmTick();`. In `Dispose`, unhook the event and dispose the tick — add to the unhook block `_publisher.SeekRequested -= OnSeekRequested;` and after `CancelReconnect();` add `DisarmTick();`.

- [ ] **Step 4: Run — verify pass.** Run: `dotnet test tests/Klangbruecke.Tests --filter FullyQualifiedName~CompanionLinkTests` — PASS.

- [ ] **Step 5: Run the whole PC suite — verify green.** Run: `dotnet test tests/Klangbruecke.Tests` — expect all pass (Phase 2's 4334 baseline + the new Phase 3 tests).

- [ ] **Step 6: Commit.**

```bash
git add src/Klangbruecke/Companion/CompanionLink.cs tests/Klangbruecke.Tests/Companion/CompanionLinkTests.cs
git commit -m "feat(companion): advancing seek-bar tick + scrub-to-seek forwarding"
```

---

### Task 7: `SmtcPublisher` ABI — thumbnail, timeline, PlaybackPositionChangeRequested

**Files:**
- Modify: `src/Klangbruecke/Companion/SmtcPublisher.cs`

No unit test — this is the ABI class below the seam (like the HWND interop), verified on hardware in Task 11. Everything above it is already tested through `FakeSmtcPublisher`.

**Interfaces:**
- Consumes: `MediaSnapshot.{Art, ArtHash}`, `TimelineState`.
- Produces: the real `UpdateTimeline`, the real `SeekRequested`, and a thumbnail on `Publish`.

- [ ] **Step 1: Thumbnail on `Publish`.** Add `using Windows.Storage.Streams;` and `using System.Runtime.InteropServices.WindowsRuntime;` (for `AsBuffer`). Add a field `private string? _publishedArtHash;`. In the `HasSession` branch of `Publish`, after setting the music properties and before `updater.Update();`, set the thumbnail only when the art changed:

```csharp
if (snapshot.ArtHash != _publishedArtHash)
{
    _publishedArtHash = snapshot.ArtHash;
    updater.Thumbnail = snapshot.Art is { Length: > 0 } bytes ? ThumbnailFromBytes(bytes) : null;
}
```

In the no-session branch, reset `_publishedArtHash = null;` alongside the existing `ClearAll()`.

Add the helper (in-memory stream write completes synchronously; the JPEG is a few tens of KB):

```csharp
private static RandomAccessStreamReference ThumbnailFromBytes(byte[] jpeg)
{
    var stream = new InMemoryRandomAccessStream();
    stream.WriteAsync(jpeg.AsBuffer()).AsTask().GetAwaiter().GetResult();
    stream.Seek(0);
    return RandomAccessStreamReference.CreateFromStream(stream);
}
```

- [ ] **Step 2: Real `UpdateTimeline`.** Replace the Task-4 stub with:

```csharp
public void UpdateTimeline(TimelineState timeline)
{
    SystemMediaTransportControls? smtc = _smtc;
    if (smtc is null || _disposed || timeline.DurationMs <= 0) return;

    try
    {
        long pos = Math.Clamp(timeline.PositionMs, 0, timeline.DurationMs);
        smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            Position = TimeSpan.FromMilliseconds(pos),
            MaxSeekTime = TimeSpan.FromMilliseconds(timeline.DurationMs),
            EndTime = TimeSpan.FromMilliseconds(timeline.DurationMs),
        });
        // A non-zero rate while playing is what makes ModernFlyouts interpolate between our pushes.
        smtc.PlaybackRate = timeline.IsPlaying ? (timeline.Speed <= 0 ? 1.0 : timeline.Speed) : 0.0;
    }
    catch (Exception ex)
    {
        Log.Error("The SMTC publisher failed to update the timeline.", ex);
    }
}
```

(Requires `using Windows.Media;` — already present.)

- [ ] **Step 3: Real `SeekRequested`.** Declare the event (replace the Task-4 stub `public event EventHandler<long>? SeekRequested;` — keep the declaration). In the constructor `try` block, after `_smtc.ButtonPressed += OnButtonPressed;`, add:

```csharp
_smtc.PlaybackPositionChangeRequested += OnPositionChangeRequested;
```

Add the handler (fires on a thread-pool thread; it only raises an event, like `OnButtonPressed`):

```csharp
private void OnPositionChangeRequested(
    SystemMediaTransportControls sender,
    PlaybackPositionChangeRequestedEventArgs args)
{
    try
    {
        SeekRequested?.Invoke(this, (long)args.RequestedPlaybackPosition.TotalMilliseconds);
    }
    catch (Exception ex)
    {
        Log.Error("An SMTC SeekRequested handler threw.", ex);
    }
}
```

In `Dispose`, alongside the existing `smtc.ButtonPressed -= OnButtonPressed;` teardown, add `Teardown.Quietly(() => smtc.PlaybackPositionChangeRequested -= OnPositionChangeRequested, "unhook the SMTC seek handler");`.

- [ ] **Step 4: Build the whole solution — verify it compiles.** Run: `dotnet build src/Klangbruecke/Klangbruecke.csproj -c Debug` and `dotnet test tests/Klangbruecke.Tests` — expect build OK and all tests green.

- [ ] **Step 5: Commit.**

```bash
git add src/Klangbruecke/Companion/SmtcPublisher.cs
git commit -m "feat(companion): SMTC thumbnail, timeline, and seek-request wiring"
```

---

### Task 8: Android wire protocol mirror + album-art codec

**Files:**
- Modify: `android/app/src/main/java/klangbruecke/remote/Protocol.kt`
- Create: `android/app/src/main/java/klangbruecke/remote/AlbumArtCodec.kt`

**Interfaces:**
- Produces (Protocol.kt): `TYPE_ALBUM_ART = 0x04`, `TYPE_REQUEST_ART = 0x11`, `COMMAND_SEEK = "seek"`, `encodeNowPlaying(..., artHash: String?)`, `encodeAlbumArt(hash, jpeg)`, `decodeRequestArt(payload): String`, `decodeSeekPositionMs(payload): Long`.
- Produces (AlbumArtCodec): `encode(bitmap: Bitmap?): Pair<String, ByteArray>?` — downscaled JPEG + content hash, or null when there is no art.

- [ ] **Step 1: Extend `Protocol.kt`.** Add constants next to the existing type bytes:

```kotlin
const val TYPE_ALBUM_ART: Byte = 0x04
const val TYPE_REQUEST_ART: Byte = 0x11
const val COMMAND_SEEK = "seek"
```

Add `artHash` to `encodeNowPlaying` (JSONObject omits a null via `put(key, null as Any?)` writing JSON null — acceptable; the C# reads null):

```kotlin
fun encodeNowPlaying(
    title: String,
    artist: String,
    album: String,
    durationMs: Long,
    hasSession: Boolean,
    artHash: String?,
): ByteArray {
    val json = JSONObject()
        .put("title", title)
        .put("artist", artist)
        .put("album", album)
        .put("durationMs", durationMs)
        .put("hasSession", hasSession)
        .put("artHash", artHash ?: JSONObject.NULL)
    return encodeFrame(TYPE_NOW_PLAYING, json.toString().toByteArray(Charsets.UTF_8))
}
```

Add the binary AlbumArt encoder and the two decoders:

```kotlin
/** Binary AlbumArt body: [2-byte BE hashLen][hash UTF-8][jpeg]. Mirrors MediaProtocol.TryReadAlbumArt. */
fun encodeAlbumArt(hash: String, jpeg: ByteArray): ByteArray {
    val h = hash.toByteArray(Charsets.UTF_8)
    val body = ByteArray(2 + h.size + jpeg.size)
    body[0] = (h.size ushr 8).toByte()
    body[1] = h.size.toByte()
    System.arraycopy(h, 0, body, 2, h.size)
    System.arraycopy(jpeg, 0, body, 2 + h.size, jpeg.size)
    return encodeFrame(TYPE_ALBUM_ART, body)
}

/** Reads the requested hash from a RequestArt payload (RequestArtPayload.artHash in the C#). */
fun decodeRequestArt(payload: ByteArray): String =
    JSONObject(String(payload, Charsets.UTF_8)).getString("artHash")

/** Reads positionMs from a seek Command payload (CommandPayload.positionMs in the C#). */
fun decodeSeekPositionMs(payload: ByteArray): Long =
    JSONObject(String(payload, Charsets.UTF_8)).getLong("positionMs")
```

- [ ] **Step 2: Create `AlbumArtCodec.kt`.** Downscale to a ~400 px longest edge, JPEG-encode, and hash the JPEG bytes (SHA-256, first 8 bytes hex — short, stable, content-derived; the PC treats it as opaque):

```kotlin
package klangbruecke.remote

import android.graphics.Bitmap
import java.io.ByteArrayOutputStream
import java.security.MessageDigest

/**
 * Turns a MediaSession album-art bitmap into the wire form: a downscaled (~400 px) JPEG plus a stable
 * content hash. Called once per track change (event-driven) and cached; the JPEG is only sent when the
 * PC asks for it (RequestArt on a cache-miss). Returns null when there is no art.
 */
object AlbumArtCodec {
    private const val MAX_EDGE = 400
    private const val QUALITY = 85

    fun encode(bitmap: Bitmap?): Pair<String, ByteArray>? {
        if (bitmap == null) return null

        val scaled = downscale(bitmap)
        val out = ByteArrayOutputStream()
        scaled.compress(Bitmap.CompressFormat.JPEG, QUALITY, out)
        if (scaled !== bitmap) scaled.recycle()
        val jpeg = out.toByteArray()
        return hash(jpeg) to jpeg
    }

    private fun downscale(bitmap: Bitmap): Bitmap {
        val longest = maxOf(bitmap.width, bitmap.height)
        if (longest <= MAX_EDGE) return bitmap
        val ratio = MAX_EDGE.toFloat() / longest
        return Bitmap.createScaledBitmap(
            bitmap, (bitmap.width * ratio).toInt(), (bitmap.height * ratio).toInt(), true)
    }

    private fun hash(bytes: ByteArray): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(bytes)
        val sb = StringBuilder(16)
        for (i in 0 until 8) sb.append("%02x".format(digest[i]))
        return sb.toString()
    }
}
```

- [ ] **Step 3: Build the APK — verify it compiles.** Run (PowerShell):

```powershell
$env:JAVA_HOME="C:\Program Files\Microsoft\jdk-17.0.20.8-hotspot"
$env:ANDROID_HOME="C:\Users\MYSTRAVIL\AppData\Local\Android\Sdk"
cd android; .\gradlew.bat assembleDebug
```

Expected: BUILD SUCCESSFUL. (The `encodeNowPlaying` signature change will break `RemoteService.kt` until Task 9 — if so, this step's build fails there; fold the Task-9 `sendSnapshot` change in before declaring Step 3 done, or temporarily pass `null`. Cleanest: do Task 9 Step 1 before building.)

- [ ] **Step 4: Commit.**

```bash
git add android/app/src/main/java/klangbruecke/remote/Protocol.kt android/app/src/main/java/klangbruecke/remote/AlbumArtCodec.kt
git commit -m "feat(android): mirror Phase 3 protocol + album-art codec"
```

---

### Task 9: Android `MediaBridge` + `RemoteService` — art, position, seek

**Files:**
- Modify: `android/app/src/main/java/klangbruecke/remote/MediaBridge.kt`
- Modify: `android/app/src/main/java/klangbruecke/remote/RemoteService.kt`

**Interfaces:**
- Consumes: `AlbumArtCodec.encode`, `Protocol.{encodeAlbumArt, decodeRequestArt, decodeSeekPositionMs, encodeNowPlaying}`.
- Produces: `MediaBridge.Snapshot` gains `artHash`, `positionMs`, `timestampMs`, `speed`; `MediaBridge.currentArt(): Pair<String, ByteArray>?`; `MediaBridge.applySeek(ms)`. `RemoteService` sends real art hash + position, answers `RequestArt`, applies `seek`.

- [ ] **Step 1: Extend `MediaBridge`.** Add art computation (cached, keyed by hash) and the new snapshot fields. In `Snapshot`, add `artHash: String?`, `positionMs: Long`, `timestampMs: Long`, `speed: Double` (update `EMPTY` accordingly). Add fields:

```kotlin
private var artHash: String? = null
private var artJpeg: ByteArray? = null
```

Recompute art whenever metadata changes — in `onMetadataChanged`, before invoking `onChanged`:

```kotlin
override fun onMetadataChanged(metadata: MediaMetadata?) {
    refreshArt(metadata)
    onChanged?.invoke()
}
```

Add `refreshArt` and read the bitmap from the metadata (try `METADATA_KEY_ALBUM_ART` then `METADATA_KEY_ART`):

```kotlin
private fun refreshArt(metadata: MediaMetadata?) {
    val bitmap = metadata?.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART)
        ?: metadata?.getBitmap(MediaMetadata.METADATA_KEY_ART)
    val encoded = AlbumArtCodec.encode(bitmap)
    artHash = encoded?.first
    artJpeg = encoded?.second
}
```

Call `refreshArt(c.metadata)` in `bindToFirst` right after binding a new controller so the first snapshot has art. Extend `currentSnapshot()` to fill the new fields from `PlaybackState` (compute the **live** position so the PC re-bases correctly):

```kotlin
fun currentSnapshot(): Snapshot {
    val c = controller ?: return Snapshot.EMPTY
    val metadata = c.metadata
    val state = c.playbackState
    val title = metadata?.getString(MediaMetadata.METADATA_KEY_TITLE).orEmpty()
    val artist = metadata?.getString(MediaMetadata.METADATA_KEY_ARTIST).orEmpty()
    val album = metadata?.getString(MediaMetadata.METADATA_KEY_ALBUM).orEmpty()
    val duration = metadata?.getLong(MediaMetadata.METADATA_KEY_DURATION) ?: 0L
    val isPlaying = state?.state == PlaybackState.STATE_PLAYING
    val speed = state?.playbackSpeed?.toDouble() ?: 1.0
    val now = SystemClock.elapsedRealtime()
    val position = livePosition(state, now, isPlaying, speed)
    return Snapshot(title, artist, album, duration, isPlaying, hasSession = true,
        artHash = artHash, positionMs = position, timestampMs = now, speed = speed)
}

/** Interpolates the framework's last reported position to *now* so the PC gets a live value to re-base from. */
private fun livePosition(state: PlaybackState?, now: Long, isPlaying: Boolean, speed: Double): Long {
    state ?: return 0L
    if (!isPlaying) return state.position
    val elapsed = now - state.lastPositionUpdateTime
    return state.position + (elapsed * speed).toLong()
}
```

Add `currentArt()` and `applySeek()`:

```kotlin
/** The current track's (hash, jpeg), or null if there is no art. Answers a RequestArt. */
fun currentArt(): Pair<String, ByteArray>? {
    val h = artHash ?: return null
    val j = artJpeg ?: return null
    return h to j
}

/** Seeks the active session (Protocol.COMMAND_SEEK). No-op if there is none. */
fun applySeek(positionMs: Long) {
    controller?.transportControls?.seekTo(positionMs)
}
```

Import `android.os.SystemClock`. (`SystemClock` is used only to read the clock, not to schedule anything — no timer, no wakelock; the constraint holds.)

- [ ] **Step 2: Wire `RemoteService`.** In `sendSnapshot`, pass the art hash and real playback values:

```kotlin
private fun sendSnapshot(output: OutputStream, snapshot: MediaBridge.Snapshot) {
    sendFrame(
        output,
        Protocol.encodeNowPlaying(
            snapshot.title, snapshot.artist, snapshot.album,
            snapshot.durationMs, snapshot.hasSession, snapshot.artHash,
        ),
    )
    val status = if (snapshot.isPlaying) Protocol.STATUS_PLAYING else Protocol.STATUS_PAUSED
    sendFrame(output, Protocol.encodePlaybackState(
        status, snapshot.positionMs, snapshot.timestampMs, snapshot.speed))
}
```

Extend `handleFrame` to answer `RequestArt` and apply `seek`:

```kotlin
private fun handleFrame(frame: Protocol.Frame) {
    when (frame.type) {
        Protocol.TYPE_COMMAND -> handleCommand(frame.payload)
        Protocol.TYPE_REQUEST_ART -> handleRequestArt(frame.payload)
    }
}

private fun handleCommand(payload: ByteArray) {
    val action = try {
        Protocol.decodeCommand(payload)
    } catch (e: Exception) {
        Log.w(TAG, "Malformed Command frame ignored", e); return
    }
    if (action == Protocol.COMMAND_SEEK) {
        val ms = try { Protocol.decodeSeekPositionMs(payload) } catch (e: Exception) {
            Log.w(TAG, "Seek without positionMs ignored", e); return
        }
        mediaBridge.applySeek(ms)
    } else {
        mediaBridge.apply(action)
    }
}

private fun handleRequestArt(payload: ByteArray) {
    val requested = try {
        Protocol.decodeRequestArt(payload)
    } catch (e: Exception) {
        Log.w(TAG, "Malformed RequestArt ignored", e); return
    }
    val art = mediaBridge.currentArt() ?: return
    if (art.first != requested) return // asked for a track we've since moved past
    val output = clientOut ?: return
    try {
        sendFrame(output, Protocol.encodeAlbumArt(art.first, art.second))
    } catch (e: IOException) {
        Log.i(TAG, "RequestArt reply failed (peer gone?)", e)
    }
}
```

- [ ] **Step 3: Build the APK — verify.** Run the Task-8 Step-3 gradle command. Expected: BUILD SUCCESSFUL.

- [ ] **Step 4: Commit.**

```bash
git add android/app/src/main/java/klangbruecke/remote/MediaBridge.kt android/app/src/main/java/klangbruecke/remote/RemoteService.kt
git commit -m "feat(android): send album art + live position, apply seek"
```

---

### Task 10: Version bump + build MSIX + build APK

**Files:**
- Modify: `packaging/AppxManifest.xml` (line 15), `src/Klangbruecke/Klangbruecke.csproj` (line 23), `android/app/build.gradle.kts` (versionCode/versionName)

- [ ] **Step 1: Bump versions.** `AppxManifest.xml` `Version="0.3.0.0"` → `Version="0.3.1.0"`; `Klangbruecke.csproj` `<Version>0.3.0.0</Version>` → `<Version>0.3.1.0</Version>`; `build.gradle.kts` `versionCode = 1` → `2`, `versionName = "1.0"` → `"0.3.1"`.

- [ ] **Step 2: Full PC test run.** Run: `dotnet test tests/Klangbruecke.Tests` — all green.

- [ ] **Step 3: Build + sign the MSIX.** Run (Windows PowerShell or pwsh is fine for the build; install needs 5.1):

```powershell
pwsh -File packaging/Build-Msix.ps1 -Configuration Release
```

Expected: ends with `Built: ...artifacts\Klangbruecke.msix`. Confirm `artifacts\Klangbruecke.msix` exists.

- [ ] **Step 4: Build the release APK.**

```powershell
$env:JAVA_HOME="C:\Program Files\Microsoft\jdk-17.0.20.8-hotspot"
$env:ANDROID_HOME="C:\Users\MYSTRAVIL\AppData\Local\Android\Sdk"
cd android; .\gradlew.bat assembleDebug
```

Expected: BUILD SUCCESSFUL; APK at `android/app/build/outputs/apk/debug/app-debug.apk`.

- [ ] **Step 5: Commit.**

```bash
git add packaging/AppxManifest.xml src/Klangbruecke/Klangbruecke.csproj android/app/build.gradle.kts
git commit -m "chore: bump to 0.3.1.0 for Phase 3 (album art + seek)"
```

---

### Task 11: Hardware verification + install

No code — this is the go/no-go the whole plan exists to satisfy (followup #4: prove the scrubber with a real advancing position).

- [ ] **Step 1: Install the APK.** Run: `adb install -r android/app/build/outputs/apk/debug/app-debug.apk`. Confirm `RemoteService` runs (notification shows) and Notification Access is still granted.

- [ ] **Step 2: Install the MSIX (Windows PowerShell 5.1 — NOT pwsh).**

```
powershell.exe -NoProfile -Command "Add-AppxPackage -Path artifacts\Klangbruecke.msix"
```

Confirm: `powershell.exe -NoProfile -Command "(Get-AppxPackage Klangbruecke).Version"` prints `0.3.1.0`. Relaunch: `explorer.exe "shell:AppsFolder\Klangbruecke_vwcm37s2b7kd8!Klangbruecke"`.

- [ ] **Step 3: Album art.** Play a track with cover art on the phone. Open ModernFlyouts / the native overlay. **Confirm the cover renders.** (First connect is ~14 s uncached SDP — wait for it.)

- [ ] **Step 4: Seek bar — the critical unknown.** With a track playing, confirm the **scrubber renders and its position advances** in ModernFlyouts (the PC-side tick pushes an advancing position every second). Then **drag the scrubber** and confirm the phone seeks — verify independently:

```
adb shell dumpsys media_session | findstr /i "state position"
```

The reported position should jump to the scrubbed target. If the scrubber does **not** render at all: check that `UpdateTimelineProperties` is being called with a non-zero `EndTime`/duration and `PlaybackStatus=Playing` (the two things the static Phase-1 probe lacked). Do **not** add a phone-side position stream. If it renders but does not advance, shorten the tick to 500 ms.

- [ ] **Step 5: Transport regression.** Confirm Phase 2 still works: media-key Next/Previous/Play-Pause drive the phone (`dumpsys media_session`), and now-playing text updates live.

- [ ] **Step 6: Record the result in FINDINGS.** Add a `§22` (Phase 3) entry to `docs/FINDINGS.md`: whether the thumbnail rendered, whether the scrubber rendered + advanced + scrubbed-to-seek, the tick cadence used, and any deviation from this plan. Update `docs/superpowers/companion-followups.md` #4 (seek validated) and note art done.

- [ ] **Step 7: Commit the FINDINGS update.**

```bash
git add docs/FINDINGS.md docs/superpowers/companion-followups.md
git commit -m "docs: FINDINGS §22 — Phase 3 album art + seek verified on hardware"
```

---

## Self-review

- **Spec coverage:** album art (lazy fetch/cache/thumbnail) → Tasks 1,2,5,7,8,9; seek (timeline + interpolation + scrub-to-seek) → Tasks 1,3,6,7,9; `artHash` on `NowPlaying` → Tasks 1,8,9; `RequestArt`/`AlbumArt` frames → Tasks 1,8,9; real `PlaybackState` position → Tasks 1,6,9; power constraint (no phone stream) honored by the PC-side tick → Task 6; volume out of scope → not implemented. ✓
- **Type consistency:** `TimelineState(PositionMs, DurationMs, IsPlaying, Speed)` used identically in Tasks 4/6/7; `PlaybackUpdate(IsPlaying, PositionMs, TimestampMs, Speed)` in Tasks 1/6; `MediaProtocol.{EncodeRequestArt, EncodeSeek, DecodeNowPlaying, DecodePlaybackState, TryReadAlbumArt}` consistent across Tasks 1/5/6; AlbumArt binary layout `[2-byte BE hashLen][hash][jpeg]` identical in C# `TryReadAlbumArt`, the C# test helper, the CompanionLink test helper, and Kotlin `encodeAlbumArt`. ✓
- **Wire mirror:** every new C# name has its Kotlin counterpart (Task 1 ↔ Task 8): `RequestArt`/`TYPE_REQUEST_ART`, `AlbumArt`/`TYPE_ALBUM_ART`, `"seek"`/`COMMAND_SEEK`, `artHash`, `positionMs`. ✓
- **Buildable between commits:** Task 4 adds `SmtcPublisher` stubs so the tree compiles before Task 7 fleshes them out; Task 5 adds an `OnPlaybackState` stub before Task 6 replaces it. ✓
