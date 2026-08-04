# Handoff — Klangbruecke, after Stage 0

Written 2026-08-04. Stage 0 is merged to `main` and the app works. This is what the next session
needs to know that the repo does not already say.

## Read these first, in this order

1. `CLAUDE.md` — project instructions. **One of its three traps is now known to be partly wrong; see below.**
2. `docs/FINDINGS.md` — the empirical record. Thirteen sections. §8, §11, §12, §13 are from validation day.
3. `docs/superpowers/specs/2026-08-04-connection-lifecycle-design.md` — Stage 1's design, plus a
   "What Stage 0 learned that Stage 1 must honour" section at the end that is the most important
   part of this handoff.

## State

Both halves work, verified on hardware with the packaged build (`0.1.0.2`, MSIX, sideloaded):

- **Music** — A2DP sink connects, transport correlated to the phone by Bluetooth address, routed to
  a chosen output, torn down cleanly when the phone reclaims the radio.
- **Calls** — HFP hands-free role claimed, phone offers the PC as a call audio device, real
  cellular call routes with audio both directions.

153 tests. `main` is clean. Nothing is pushed to any remote.

## What is not built, in priority order

**1. Reconnect. This is the whole of Stage 1 and the project's original reason for existing.**

There is no state machine — no `DeviceWatcher`, no retry, no backoff, no sleep/resume handling. The
app connects when told and stays connected until something stops it. `Settings.AutoReconnect` is
written by the tray menu and read by nothing.

It is fully designed in the spec: states, transitions, the 3-second grace window that separates a
deliberate disconnect from walking out of range, backoff schedule, and a 30-second reconcile loop
as the backstop for missed WinRT events. The design was approved before Stage 0 ran; Stage 0's
observations are recorded at the end of the spec and some of them constrain it.

**Concretely demonstrated cost:** a phone call tears down the A2DP route (§13) and nothing brings
it back. One call silently costs the music bridge until the user re-picks the phone from the tray.

**2. `ConnectAsync` returns False** after a successful registration. Calls work regardless, so this
may be benign — it plausibly returns false with no call in progress. Unexplained. Do not assume
benign without testing; it is also the candidate for what the Limited Access Feature gate actually
covers.

**3. Outgoing call audio is degraded.** Not this app's fault — identical under Thy Phone. VoiceMeeter,
the microphone, and the cellular network are each eliminated by direct test (§11). Leading suspect
is a narrowband SCO codec forced by a **2021 MediaTek RZ616 driver** (1.5.21.157, 2021-12-27), the
only non-Microsoft component in the stack.

`packaging/Measure-CallBandwidth.ps1` measures it. **The trap:** a cellular call is narrowband at
the network level, so it reads ~4 kHz whatever Bluetooth negotiated — it will confirm the hypothesis
whether or not the hypothesis is true. Only a **wideband** call (WhatsApp, Signal, Telegram,
FaceTime Audio) from a second phone is conclusive. The user has two phones.

**4. Tray device selection for calls.** The user asked for it and it is undecided. Picking a call
device from the tray means the app changing the **system-wide default communications device** via
`IPolicyConfig`, an undocumented COM interface — a much bigger behavioural footprint than anything
the app does today. The user has not answered. Ask before building.

## Corrections to the project's own instructions

**`CLAUDE.md` trap #1 is partly wrong.** It says "Do not implement LAF token generation — Microsoft
removed `PhoneLineTransportDevice` from the LAF list. It needs no token."

The instruction's *conclusion* holds for `RegisterApp()`, but its *reason* is false. The Limited
Access Feature gate is live on this machine — `CallTransportService.ProbeLimitedAccessFeature`
reports `Unavailable` on every connect, and the feature id is present in
`HKLM\...\AppModel\LimitedAccessFeatures` with a value matching Sefirah's constant byte for byte.
`RegisterApp()` succeeds anyway, so no token is needed *for registration*. It may yet be needed for
`ConnectAsync`. Sefirah performs the unlock precisely when the build is below 22000, i.e. on
Windows 10, which this machine is.

Do not implement token generation without asking — it is a standing instruction and the user's call.

**`FINDINGS.md` §2's premise is confirmed, after being doubted.** Sideloading a restricted
capability needs no Microsoft approval. MyPhone has no Store listing at all and `RegisterApp` works
for it.

## The thing most worth internalising

**Every real defect this project has hit was in code that compiled and had never been executed.**
Six in the original scaffold: the `AudioPlaybackConnection` access violation, the tray menu
cancelling its own display forever, the dev-cert script using a .NET Core API in a script that must
run under Windows PowerShell 5.1, a pfx password file that `*.pfx` did not match in `.gitignore`,
and the missing `RequestAccessAsync()` that blocked the calls half from the beginning.

Eight more were in Stage 0's *plan text* — none in the implementations. Writing complete code into
the plan made tasks fast to execute and moved the bugs into a document that got one self-review,
while the code got a dedicated adversarial reviewer per task. **Stage 1's plan should specify
interfaces, test cases and constraints, and let implementers write the bodies.**

And on validation day, four confident diagnoses of the `RegisterApp` failure were wrong, each
killed by a direct test, before the right answer turned out to be a missing method call. The
generalisable lesson: reach for the boring explanation before the architectural one, and state
hypotheses as hypotheses. §12 records all four deliberately.

## Practical notes that will save an hour

- **`dotnet run` is not a dev loop for the music half.** `AudioPlaybackConnection.TryCreateFromId`
  terminates an unpackaged process with an uncatchable `AccessViolationException` (§8). Every
  music-side change costs a full MSIX install cycle. `AudioSinkPolicy` gates this in two places —
  do not remove either "to let it try". The calls half *can* be exercised unpackaged as far as
  enumeration and `FromId`.
- **Build and install:** `./packaging/Build-Msix.ps1`, then `Add-AppxPackage` from **Windows
  PowerShell**, not pwsh — the `Appx` module does not load in PowerShell 7.
- **Bump the version in both `Klangbruecke.csproj` and `packaging/AppxManifest.xml`** or the install
  will not upgrade. The log records version and build time, which is how you tell builds apart.
- **The log is at `%LOCALAPPDATA%\Klangbruecke\logs\`** for both packaged and unpackaged runs — they
  share one file, because the manifest disables Desktop Bridge write virtualization (§9). **Read the
  last `Base directory:` line before believing anything**, and scope greps with `-Tail 300`.
- **Errors log twice by design** — once with the stack via `Log.Error`, once as the status the tray
  shows. A stackless `[ERR]` with no stack-bearing partner above it is the defect.
- **Check the single-instance mutex, not process names.** A `dotnet run` instance is `dotnet.exe`,
  not `Klangbruecke`:
  `try { [System.Threading.Mutex]::OpenExisting('Local\Klangbruecke.SingleInstance').Dispose(); 'HELD' } catch { 'FREE' }`

## Riskiest code, and why

`AudioRouter` constructs `WasapiCapture`/`WasapiOut` inline with no seam, so **none of its behaviour
is testable**. Five properties there are load-bearing and defended by comments alone — reverting any
one leaves all 153 tests green:

1. `_session` published before `StartRecording`/`Play`
2. `EndSession()` before `Report()` in both stopped handlers
3. `_session = null` before the unsubscribes in `Stop()`
4. the stale-session check inside the posted teardown lambda
5. the `ReferenceEquals(sender, …)` guards

Two real defects were found there **by live hardware probes after each survived three review
rounds**. Treat "reviewed repeatedly" as weak evidence for anything behind that seam.

Related: NAudio's `WasapiOut` raises `PlaybackStopped` **on the play thread** when no
`SynchronizationContext` was captured, and `PlayThread` assigns `Stopped` only on its one clean
fall-through — so a handler calling `Stop()`/`Dispose()` self-joins and deadlocks, and every
abnormal exit lands in that window, including `audioClient.Stop()` throwing when the phone leaves
range. Teardown is posted through `IUiDispatcher` for exactly this reason. **Any Stage 1
auto-reconnect calling `Start()` from a threadpool thread would have hit it**; production avoided it
only by field-initializer ordering.

## Suggested first move

Brainstorm and plan Stage 1 against the spec, honouring the constraints in its final section —
especially introducing the `IAudioSinkService` / `ICallTransportService` / `IAudioRouter` seam,
which is what makes the state machine testable and `AudioRouter` guardable at all. That seam is the
point of those interfaces, not tidiness.
