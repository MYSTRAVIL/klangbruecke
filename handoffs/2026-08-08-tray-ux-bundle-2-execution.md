# Handoff — Tray UX bundle 2 (unattended subagent-driven build)
_Written 2026-08-08. The user is away; this session runs the build unattended._

## Goal
Implement "tray UX bundle 2" for Klangbruecke: (1) **left-click opens the tray menu**, (2) **event
sounds** (synthesized chimes on Connected/Disconnected/Degraded, toggleable, default on), (3)
**auto-pick** among a remembered set of phones (first-present-wins). It is fully specced and planned —
your job is to BUILD it via subagent-driven-development, then stop.

## Current status
- On `main` @ `a85057c`, pushed to origin. Suite: **4290 tests green, zero warnings**.
- Bundle 1 (Connect Now, Open Logs, About, Check for Updates, Copy Diagnostics + follow-ups) already
  shipped and merged; released as **v0.2.3** (installed 0.2.3.0); `main` is **0.2.4.0 in-dev**.
- Bundle 2 spec + plan are written, committed, pushed. **Nothing of bundle 2 is implemented yet.**

## Decisions made (locked — do not relitigate)
- Auto-pick = **first-present-wins, NO ranking**; a present incumbent is never switched away from.
- Sounds fire on **Connected / Disconnected / Degraded only**; **default ON**; a "Sounds" tray toggle
  silences them; chimes are **synthesized WAVs** (generator + full code in the plan).
- The phone submenu becomes **multi-check** (a remembered set); `SelectPhone`/`DeselectPhone` are
  **replaced** by `SetPhoneRemembered`/`ClearRememberedPhones`.
- Auto-pick is a **resolver layer over the existing single-active-phone machinery** — do NOT rework
  `LinkMonitor` to watch many phones at once.

## Next steps (ordered)
1. Verify `git rev-parse --short HEAD` == `a85057c` on `main`, working tree clean, and `dotnet test` is
   green (4290). (If HEAD moved, still branch from current `main`.)
2. Create the feature branch: `git checkout -b tray-ux-bundle-2`.
3. Invoke the **superpowers:subagent-driven-development** skill and execute the plan
   `docs/superpowers/plans/2026-08-08-tray-ux-bundle-2.md` task-by-task (9 tasks): per-task implementer +
   per-task review (spec + quality) + fix loop, then a whole-branch review on the most capable model
   (one fix wave if needed).
4. On completion (suite green + whole-branch review clean): `git push -u origin tray-ux-bundle-2`, then
   STOP. Leave a clear final summary (below).

## Key files & paths
- Plan (your task list; full code for the pure units + the WAV generator):
  `docs/superpowers/plans/2026-08-08-tray-ux-bundle-2.md`
- Spec: `docs/superpowers/specs/2026-08-08-tray-ux-bundle-2-design.md`
- Empirical constraints from this session: `docs/FINDINGS.md` (esp. §8 unpackaged AccessViolation; §17
  dropped features — AVRCP/battery/music-codec are NOT in scope).
- Repo root: `c:\Users\MYSTRAVIL\Documents\Programming\Projects\klangbruecke`

## Open questions / risks / gotchas — READ BEFORE STARTING
- **DO NOT merge to `main`, DO NOT cut a release, DO NOT bump the version.** Push the feature branch and
  stop. Merge + release + version bump are the user's, after their hardware smoke.
- **DO NOT run or launch the app** — an unpackaged `dotnet run` dies in the music half with an
  uncatchable `AccessViolationException` (FINDINGS §8). `dotnet test` is safe.
- **Hardware smokes are the user's and are NOT blockers.** Left-click opening the menu, chimes being
  audible, and auto-pick with two real phones cannot be verified headless. Get to *builds + suite green +
  whole-branch review clean*; list these as pending for the user.
- **Subagent git-state hazard (project memory `subagent-git-state-hazard`):** forbid EVERY implementer
  and reviewer subagent from `git checkout`/`switch`/`branch`/`reset`/`stash`/`merge` — only `git add` +
  `git commit` on `tray-ux-bundle-2`. After each task, verify the branch (`git rev-parse --abbrev-ref
  HEAD` == `tray-ux-bundle-2`).
- **Model selection:** Tasks 1, 2, 3, 5, 6 carry complete code → cheapest tier (haiku) implementers;
  Tasks 7, 8, 9 are integration → standard (sonnet); the whole-branch review → most capable (opus). End
  every subagent prompt with "Do not spawn subagents — do this work yourself."
- **You are headless (`claude -p`):** run the ENTIRE SDD flow to completion in this one session — it is
  continuous by design (no user checkpoints, no "should I continue?"). Do not hand off again; if you
  truly must, it has to be headless too.
- **Task 7 is the big one** (ConnectionManager resolver + removing `SelectPhone`/`DeselectPhone` + test +
  `Harness` updates). **Task 4** adds `ILinkMonitor.ReadLinkStatusForAsync` so the resolver's presence
  reads are fakeable — build it before Task 7. The plan's self-review has a cross-task note: after Task 7
  and Task 9, grep the tree for any remaining `SelectPhone`/`DeselectPhone` references — there must be
  zero.
- The SDD helper scripts live under the subagent-driven-development skill dir
  (`scripts/sdd-workspace`, `task-brief`, `review-package`); the plan's own workspace is
  `.superpowers/sdd/2026-08-08-tray-ux-bundle-2/` (gitignored). Keep a ledger there.

## Final summary to leave for the user
When done, write a short report: what built, the final test count, the whole-branch review outcome, the
pushed branch name, and the pending hardware-smoke checklist (left-click menu; the three chimes on
connect/disconnect/degrade; auto-pick with two phones — connect whichever is on, and that an incumbent
isn't dropped when the other appears). The user will smoke-test, then decide merge + release + version
bump.

## Decisions made without you (headless run, 2026-08-08)
Full detail + rationale in the ledger: `.superpowers/sdd/2026-08-08-tray-ux-bundle-2/progress.md`.
- **D1 (Task 7 ordering):** Task 7 removed `SelectPhone`/`DeselectPhone` AND, in the same commit, updated
  the two TrayContext call sites + the `<see cref>` doc ref — leaving TrayContext calling removed methods
  would have failed Task 7's zero-warning build gate. Task 9 then did the checkable-submenu redesign. Net:
  zero `SelectPhone`/`DeselectPhone` references after Task 7.
- **D2 (Task 7 review, CRITICAL, fixed):** the async resolver was missing the brief-mandated *superseded*
  discipline and enumerated the live remembered set across the `await`. Fixed with a snapshot + an
  `_resolveGeneration` token (checked after each await / before acting; bumped at resolve entry and in
  `SetActivePhone`/`ClearRememberedPhones`).
- **D3 (Task 7 review, Important, deferred→resolved):** the `SetPhoneRemembered` already-remembered→force-switch
  asymmetry was deferred to Task 9; Task 9's toggle (`SetPhoneRemembered(id, !Contains(id))`) means clicking a
  remembered phone REMOVES it, so the force-switch branch is never user-reachable — the final review confirmed
  no "click-twice" wart. The branch is kept because the re-pick grant tests exercise it.
- **D4 (final fix wave):** the whole-branch (opus) review caught two real defects every per-task review missed:
  (1) **CRITICAL** — `SoundPlayer` matched WAV resources by bare suffix, so `"connect.wav"` also matched
  `"disconnect.wav"` → `.Single()` threw at construction → an **uncatchable startup crash** (the app would never
  launch); fixed to match `"." + fileName`. (2) **IMPORTANT** — unchecking the *last* remembered phone left the
  bridge up and let `Migrate` resurrect it on restart; fixed to tear down (delegate to `ClearRememberedPhones`).
  Fixing (2) required reordering `Selecting_a_different_phone_moves_the_hands_free_role` to switch via
  add-new-then-remove-old (the realistic multi-remember order) — assertions unchanged. Two regression tests added.

## Outcome (headless run complete)
Branch **`tray-ux-bundle-2`** pushed to origin (11 commits, `daf8ae0..4be49a5`). Controller-verified at HEAD:
`dotnet build` 0 warnings / 0 errors; `dotnet test` **4319 passed, 0 failed**. Whole-branch review clean after
one fix wave. NOT merged, NO release, NO version bump — left to the user after the hardware smoke.
