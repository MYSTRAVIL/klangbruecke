# Stage 1 — status at the end of the unattended session

_Written 2026-08-05 by the headless continuation session, just before shutting the machine down._

**Branch `stage-1-connection-manager` is complete, green, packaged, installed — and not merged.**
It should not be merged until you have run the hardware smoke test below. Everything else is done.

---

## Where things stand

| | |
|---|---|
| Branch | `stage-1-connection-manager`, `e8b0f2b..45261d9`, 53 commits |
| Tasks | **19 of 19 complete**, including the final whole-branch review and its fix wave |
| Tests | **4244, all green, zero warnings** (the suite was 153 when Stage 1 started) |
| Package | **0.2.0.0 built, installed, and running in your tray right now** |
| Merged | **No.** That is your call, after the smoke test |

The working tree is clean apart from an untracked `.vscode/`.

## What Stage 1 actually gives you

A connection lifecycle that recovers unattended — the thing the predecessor app never did. The
machine is decomposed rather than one seven-state table: a link machine, a suppression latch, one
controller per half (music and calls), a pure projection to the seven reported state names, and a
`ConnectionManager` that owns intent, the 3 s grace window, the 30 s reconcile, and resume.
`TrayContext` is now a view — every menu item calls exactly one manager method.

Two behaviours are worth naming because they are the whole point:

- **A call ending restarts the route, it does not reconnect the phone.** A cellular call invalidates
  the capture endpoint without closing the A2DP connection, so the music half goes `Up → Linked` and
  waits for the endpoint to come back. That is `FINDINGS.md` §13.
- **A range exit and return recovers on its own**, from either the watcher edge or the 30 s poll,
  with the poll debounced so one failed read cannot tear down a working route.

## The hardware smoke test — the only part left, and it needs you

The app is installed and running. Task 18 steps 1–3 are done and the packaged build is confirmed:

```
Base directory: C:\Program Files\WindowsApps\Klangbruecke_0.2.0.0_x64__vwcm37s2b7kd8\
```

Steps 4 and 5 need a phone in range, a real cellular call, and someone listening. **I deliberately
did not fake them, and deliberately did not write the `FINDINGS.md` section that records what they
observe** — writing that from expectation would put a measurement into this project's most
load-bearing document that nobody ever took.

Full detail is in `.superpowers/sdd/2026-08-05-stage-1-connection-manager/task-18-report.md`, under
**"What the hardware smoke test must still record"**. The short version:

1. **Does music return unattended after a call ends?** And **did the A2DP connection close or stay
   open** at call end? This is the one assumption the design rests on that no measurement covers
   (spec risk #4). If it closes, the design still degrades correctly — the music half falls back to
   `Backoff` and reconnects — just more slowly than intended.
2. **Which notification callback carries the A2DP endpoint's arrival, and what is the endpoint's
   *name* at that moment?** Presence is a friendly-name match, and a rename arrives on a callback the
   monitor deliberately rejects. Record the name, not only which callback fired.
3. **How large are the `OnDeviceStateChanged` bursts really?** The earlier 40-callback census was
   entirely `OnDefaultDeviceChanged`, which the monitor filters out — so burst volume has never been
   measured, and both the UI-thread cost bound and the logging-volume bound rest on that gap.
4. **Eyeball the menu.** Three deliberate behaviours ship with zero automated coverage because
   `TrayContext` is untestable: the phone tick marks the selected phone, Disconnect is enabled only
   when a phone is selected, and Exit is last. Also check the two new labels — `Route calls to PC
   (needs MSIX)` greyed when unpackaged, and `Phone (music needs MSIX)`.

Also: place the call and **check audio in both directions.** The mic half fails silently otherwise.

Expect `Hands-free role claimed. The call transport reported not connected…` — that wording is
correct and `not connected` there is **success**, not failure (`FINDINGS.md` §12).

## What the session found that you did not ask for

**The last review before merge caught the predecessor app's defining bug, alive on the branch built
to fix it.** The reconcile's polled `Present → Absent` updated the link machine and the latch but
never told the music half. So the poll — which exists precisely as the backstop for a watcher edge
that never arrives — did not tear the music half down when it fired. Because `OnLinkPresentAsync`
only acts from `Off`, the phone's *return* edge was then refused while the half sat in `Backoff` with
its backoff pinned at 60 s, and the tray reported `Discovering` while the app hammered the radio.

It was predicted from your own hardware log: the packaged run's link watcher fired a false
"device appeared" for a phone that was not there, the poll correctly corrected it on the second tick
— and the retries carried on to 17:03:13 and would not have stopped. Fixed in `99a8c4c`, pinned by
four tests.

**A measured counterexample changed `FINDINGS.md` §4.** With the phone disconnected and its HF
endpoints gone, `Line (MYSTRAPIX9 A2DP SNK)` still enumerated `Present=True, Status=OK`. Two agents
reproduced it independently, hours apart. So endpoint presence is *strong evidence* of a live
connection, not proof — §4 now says so, and the six code comments that still gave the old reason were
corrected. **Nothing in Stage 1 depended on presence being proof**; that was checked across every
read site. The operational advice is unchanged: verify with `Get-PnpDevice`, never a UI indicator.

## Two things to know before you touch it

- **A rare test failure has been seen twice** (once at 4196/4197, once at 4234/4235 by an independent
  agent in a clean clone) and **both times the failing test's name was lost because the output was
  piped through `grep`.** It did not appear in ~30 runs today. Run the suite unfiltered, or with
  `--logger "console;verbosity=detailed"`. Capturing the name once is the entire cost of closing it.
- **The running process predates the last three commits.** Those commits are comments and one status
  string, no behaviour, and the version did not move — so a rebuild would be a same-version reinstall.
  I left the install alone deliberately: it is the artifact the smoke test needs.

## Recommended next steps

1. Run the smoke test above. If it passes, add the `FINDINGS.md` section it produces and merge.
2. **If it fails, stop rather than patching around it.** Every real defect in this project has been in
   code that compiled and had never been executed; a failed smoke test is that class of finding.
3. Stage 2 candidates, in the order the reviews argued for them: split the grace window and the
   reconcile out of `ConnectionManager` (~1400 lines, three supersession shapes — and the seam is
   exactly where the Critical above lived, which would have made it a compile-time obligation instead
   of a grep); give the unpackaged run somewhere to stop instead of retrying forever
   (`CallsPolicy.ShouldRegister` is orphaned and its own banner comment states the fix); and dispose
   the `WasapiDeviceFactory` adapters' `MMDevice`s, the one deferred item whose cost grows with use.

## Where the record lives

- **Ledger — read this first:** `.superpowers/sdd/2026-08-05-stage-1-connection-manager/progress.md`.
  Every commit range, every deferred minor with a file:line, every controller decision made without
  you, and five hard-won lessons about mutation testing that are worth promoting into the project's
  conventions.
- Task reports (gitignored, on disk only): same directory, `task-N-report.md` and
  `final-fix-wave-report.md`.
- I did **not** delete the workspace, though the process says to delete it after a clean final review:
  the branch is not merged, and the hardware test still needs what is in there.
