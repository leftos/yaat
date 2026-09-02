# Codebase Concerns

**Analysis Date:** 2026-09-01

This document distinguishes three kinds of findings:
- **[Documented]** — the maintainer already tracks this in `docs/`, `CLAUDE.md`, or memory files. Cited for completeness.
- **[Undocumented]** — found during this pass, not written down anywhere in the repo. Highest value.
- **[Drift]** — documentation and code disagree, or documentation describes a state that has since changed.

## Tech Debt

**God-object simulation engine [Undocumented]:**
- Issue: `src/Yaat.Sim/Simulation/SimulationEngine.cs` is 5,543 lines with 69 public methods — the largest file in the repo by a wide margin (next largest source file is `PatternCommandHandler.cs` at 3,967 lines). It is also the single most frequently touched file in recent history (29 commits touched it in the last 300, more than double the next-highest file).
- Files: `src/Yaat.Sim/Simulation/SimulationEngine.cs`
- Impact: every tick-loop change, every new per-tick feature, and every dual-host (`TickPostPhysics`/`ProcessPostPhysics`) wiring decision (see "Dual tick-execution paths" below) touches this one file, making merge conflicts and regressions concentrate here. Its size also makes it expensive for an agent (or a human) to hold in context when reasoning about a change.
- Fix approach: no active decomposition plan exists in `docs/plans/`. This is worth flagging to the maintainer rather than silently deferred — CLAUDE.md's "Never defer unilaterally" rule applies to raising it, not fixing it here.

**Command-layer files are similarly oversized [Undocumented]:**
- Files: `src/Yaat.Sim/Commands/PatternCommandHandler.cs` (3,967 lines), `src/Yaat.Sim/Commands/CommandParser.cs` (3,797 lines), `src/Yaat.Sim/Commands/CommandDispatcher.cs` (3,657 lines, 16 recent commits), `src/Yaat.Sim/Commands/GroundCommandHandler.cs` (3,041 lines, 16 recent commits), `src/Yaat.Sim/Commands/CommandDescriber.cs` (2,694 lines, 16 recent commits).
- Impact: CLAUDE.md's hard limit is "≤100 lines/function, cyclomatic complexity ≤8" — these limits apply per-function, so large files are not automatically violations, but the concentration of churn in `CommandDispatcher.cs`/`GroundCommandHandler.cs`/`CommandDescriber.cs` (16 commits each in the last 300) suggests these are hot spots where new command behavior keeps landing, increasing the odds of merge conflicts and cross-command regressions.
- Fix approach: no action needed unless function-level complexity limits are being violated; worth a periodic `ast-grep`/complexity audit rather than a rewrite.

**Client-side files carry the same concentration [Undocumented]:**
- Files: `src/Yaat.Client/ViewModels/MainViewModel.cs` (3,915 lines, 20 recent commits — second-most-churned file in the repo), `src/Yaat.Client/Views/MainWindow.axaml.cs` (3,452 lines, 12 recent commits).
- Impact: `MainViewModel.cs` has no dependency injection (per CLAUDE.md: "No DI: MainWindow creates MainViewModel directly"), so its size reflects direct ownership of most client-side orchestration. This is architecturally intentional (documented in `docs/client-mainviewmodel.md`), but the churn rate makes it a natural hotspot for review-time regressions when several features land close together.
- Fix approach: no action needed; flagging so future agents don't underestimate the blast radius of `MainViewModel.cs` edits.

**Dual tick-execution paths (server vs. standalone/replay) [Documented, still worth restating]:**
- Issue: `SimulationEngine.TickPostPhysics` is reached only by the standalone `TickOneSecond` and replay drivers; the live server's `TickProcessor.ProcessPostPhysics` (yaat-server repo) runs its own separately-ordered list. A step added only to `TickPostPhysics` runs correctly in tests and replay but silently does nothing on the live server.
- Files: `src/Yaat.Sim/Simulation/SimulationEngine.cs` (`TickPostPhysics`), `docs/tick-loop.md:28-53` (the documented contract and required "both hosts call it" pattern), archived post-mortem `docs/plans/archive/post-physics-ownership-refactor.md`.
- Impact: this exact bug already happened once — pilot-proactive request reminders ran dark on the production server for roughly 2.5 months (`docs/tick-loop.md:44`) because the logic was added only to `TickPostPhysics` and never wired into `ProcessPostPhysics` in yaat-server. Because the two hosts live in different repos (this file lives here; the server's `TickProcessor` lives in `yaat-server`), a `git grep` inside this repo alone cannot reveal whether new per-tick logic reached the server path.
- Current mitigation: `docs/tick-loop.md` prescribes two safe shapes (void-both-hosts-call-it, or compute-and-return like `TickPrePhysics`), and recommends a `RoomEngineTestHarness` parity test (e.g. `PilotProactiveServerParityTests`) as the only test shape that can actually catch a server-path gap — a Yaat.Sim-only `TickOneSecond` test cannot.
- Fix approach: no code fix needed; this is a process/review discipline item. Worth calling out explicitly here because it is the highest-severity *cross-repo* footgun in the codebase and is easy for an agent working from `src/Yaat.Sim/` alone to miss, since the second half of the contract (the server calling site) is not in this repo.

**Cross-repo coupling via sibling project reference [Documented in CLAUDE.md, elaborated here]:**
- Issue: `Yaat.Sim` (this repo) is referenced by `yaat-server` (sibling repo, `..\yaat-server`) via a plain project reference, not a package. There is no dependency-version pinning between the two repos — yaat-server always builds against whatever `Yaat.Sim` source is checked out at `X:/dev/yaat` (or the matching commit in its own worktree).
- Files: cross-repo signature changes are called out in CLAUDE.md ("Cross-repo Yaat.Sim signature landings" in the feedback memory index) and enforced procedurally by `pwsh tools/test-all.ps1`, which builds and tests both repos together.
- Impact: any public-surface change to `Yaat.Sim` (new required constructor arg, renamed method, new abstract member) can compile cleanly in this repo's own test suite while silently breaking `yaat-server` until someone runs the cross-repo build. Bare `dotnet test` in this repo does not catch it (explicitly noted in CLAUDE.md's "Cross-repo verification" rule).
- Current mitigation: `tools/test-all.ps1` runs both repos' builds/tests together and is the mandated verification step before "whole suite" runs; `prek`'s pre-commit hook also builds `yaat.slnx`, which pulls in the sibling `yaat-server` project (documented in CLAUDE.md's "prek's build hook is cross-repo" note), so a broken cross-repo signature is caught at commit time *if* the sibling repo's working tree is in a compatible state.
- Fix approach: no fix needed — this is an accepted architecture (shared library via sibling checkout, not a package feed) appropriate for a two-repo solo-maintained project. The main residual risk is a partial-commit scenario where `yaat-server`'s working tree has *uncommitted* changes that depend on a `Yaat.Sim` change being committed here (see CLAUDE.md's stash-before-partial-commit guidance) — this is a documented workflow footgun, not a code defect.

**Test-fixture flakiness from mutated process-global flags [Documented in-code]:**
- Issue: `Skw3078FixComparisonCapture` is a diagnostic-artifact-generation test class, not an assertion test, that flips `GroundConflictDetector`'s process-global `WingspanLateralCheck` flags for a full replay. Running it in the normal parallel suite races any other ground-conflict test (the in-code comment names `Issue234Spot7AConflictTests` specifically) because xUnit parallelizes test classes by default and the flags are process-global with no other mutator.
- Files: `tests/Yaat.Sim.Tests/Simulation/Skw3078FixComparisonCapture.cs:36-47`
- Impact: both of its two `[Fact]`s are permanently `Skip`-attributed to keep the flags at production defaults everywhere; they must be manually un-skipped and run in isolation to regenerate the `.tmp/*.json` LayoutInspector artifacts. This is a narrow, self-contained instance of a broader pattern flagged in CLAUDE.md's "Static singleton races" testing rule (parallel test classes racing shared static state) — worth being aware of before adding new process-global mutable flags to any detector class.
- Fix approach: none needed; the skip + comment is itself the correct mitigation. Cited here so a future agent doesn't "fix" the flakiness by un-skipping without reading the comment first.

## Known Bugs

No open, unresolved functional bugs were found tracked in `docs/plans/open-issues/`. The three files present there are administrative pointers to work that is either in progress in the sibling repo or already resolved:
- `docs/plans/open-issues/150-live-traffic-swim.md` — pointer to design docs living in `yaat-server`; states remaining work is the "DVR" playback-scrub feature (`09-live-sessions.md` in yaat-server), explicitly **not built** as of this writing. Not a bug — a tracked, partially-shipped feature.
- `docs/plans/open-issues/172-taxi-crossing-holdshort-and-directionality.md` — marked "W1–W7 all implemented and verified (2026-06-03)" with a follow-up resolution note dated 2026-06-04; the file itself says it "can be archived once merged." **[Drift]**: this plan file is stale housekeeping — it documents a resolved issue but has not been moved to `docs/plans/archive/` as CLAUDE.md's plan-hygiene rule ("Don't leave finished plans sitting in `docs/plans/` indefinitely — either promote or archive") calls for.
- `docs/plans/open-issues/fillet-s-turn-connectors.md` — explicitly titled "superseded for #236"; the fix shipped via a different mechanism (navigator speed-hold + pathfinder transition-arc exemption) and the file documents *why the originally-planned approach was rejected*. **[Drift]**: same archival gap as #172 — this is closed work sitting in the open-issues directory.

**Scenario parser known-failures are pre-classified, not bugs [Documented]:**
- `docs/scenario-validation-known-failures.md` catalogs 231 of 68,722 presets (99.7% parse rate, last full run 2026-03-12) as instructor-authored typos or unsupported free-text notes in ATCTrainer source scenarios (e.g. `AT BHAWK QQ P110` — invalid `P` prefix on TEMPALT, `WIAT 10 HO PUB_APP` — misspelled `WAIT`). The maintainer has explicitly classified these as *not* parser bugs.
- **[Undocumented drift note]:** the "Last full run" date (2026-03-12) predates today (2026-09-01) by nearly six months, and this repo's CHANGELOG shows substantial command-pipeline and chaining changes shipped since then (e.g. "Chained commands are hardened" in v0.12.23-beta, various `CTO`/`CTOC` behavior changes in Unreleased). The known-failures list has not been regenerated since those changes; some of the 231 catalogued failures may now parse differently (better or worse) than recorded. Re-running `python tools/validate-all-scenarios.py` (per CLAUDE.md, lives in the yaat-server repo) would confirm whether the list is still accurate.

## Security Considerations

**No secrets were read or inspected as part of this analysis**, per the forbidden-files policy. A cursory listing shows the usual `.gitignore`-covered surface (`.env`-style files, if any, were not enumerated to avoid even incidental exposure).

**ARTCC entitlement enforcement is recent and narrow in scope [Documented, cited for context]:**
- The CHANGELOG's `Unreleased > Fixed` section states: "The server now refuses to open a room, or fetch a catalog scenario, for an ARTCC the signed-in controller isn't entitled to, instead of trusting the client's ARTCC." This is a server-side authorization fix that shipped very recently (still in `Unreleased`).
- Impact: this indicates the trust boundary between client-supplied ARTCC selection and server-side authorization was, until this fix, enforced only nominally. Worth noting that similarly client-trusting patterns may exist elsewhere in the SignalR hub surface (`ServerConnection.cs` / `TrainingHub.cs`, per CLAUDE.md's SignalR key-pattern note) — this analysis did not audit every hub method for the same class of gap, since that requires reading the sibling `yaat-server` repo's `TrainingHub.cs`, out of scope for a single-repo pass.
- Fix approach: none needed here; flagging as a class of risk worth a targeted audit pass across `yaat-server`'s hub methods (out of scope for this document, which covers the `yaat` repo only).

## Performance Bottlenecks

No specific hot-path bottleneck was independently identified in this pass beyond what is already covered by the project's own profiling tooling:

**Dedicated profiling skill exists and encodes prior findings [Documented]:**
- `dotnet-test-suite-profiling` (project skill) and the memory note `test_suite_profiling_method.md` record that dotnet-trace-based CPU/allocation profiling has already been used to identify per-thread hot leaves in the test suite; "never trim invariant loops" is called out as a guardrail against naive performance fixes that would weaken test coverage.
- Fix approach: use the existing skill rather than ad-hoc profiling if a future performance concern arises in the test suite; this document does not duplicate that work.

**Tick-loop single-thread constraint [Documented]:**
- `docs/tick-loop.md` and the memory note `tick_thread_sync_io_overruns.md` establish that `RunTickLoop` runs all rooms on one thread in the live server, so nothing reachable from `TickPhysics` may block on network I/O. This is an architectural constraint rather than an active bug, but any new per-tick feature (live traffic ingestion, SWIM correlation, etc.) that performs synchronous I/O on that path would silently stall every room on the server. No violation was found during this pass; flagging as a standing constraint any future phase touching the tick loop must respect.

## Fragile Areas

**Fillet/pathfinder geometry is replay-coupled [Documented, high blast radius]:**
- Files: `src/Yaat.Sim/Data/Airport/AirportGroundLayout.cs` (3,354 lines), `src/Yaat.Sim/Data/Airport/Pathfinding/SegmentExpander.cs` (3,230 lines, 9 recent commits), fillet generator code referenced throughout `docs/ground/fillet-generator.md`.
- Why fragile: the memory note `fillet_node_ids_geometry_coupled.md` states snapshot DTOs persist fillet-minted tangent-cut node IDs, so *any* fillet-geometry change time-shifts every existing replay recording. This was directly observed during the #172 investigation (`docs/plans/open-issues/172-taxi-crossing-holdshort-and-directionality.md:8-15`): a routine SFO GeoJSON re-serialization (truncated coordinate precision, no semantic change) silently renumbered fillet/spot nodes and broke a recorded replay (`SegmentExpander: "Cannot reach destination from end of taxi path"`), initially misattributed to an unrelated code change before being correctly traced to test-fixture drift.
- Safe modification: per the #172 postmortem, any airport GeoJSON refresh (not just fillet-generator code changes) can silently desync existing recordings. The fix pattern used there — pinning a full-precision snapshot as a dedicated test fixture (`PinnedSfoGroundData`, `TestData/issue172-sfo.geojson`) and deriving guard tests by taxiway *name* rather than node ID — is the documented safe pattern for tests that must survive future geometry refreshes.
- Test coverage: guarded by `FilletCornerSpanGuardTests` / `GroundArcBezierPlaybackGuardTests` (named in `docs/plans/open-issues/fillet-s-turn-connectors.md:26`), plus the `Issue172*` test classes.

**Static-singleton test races [Documented]:**
- Files: any test reading `NavigationDatabase`, `AircraftProfileDatabase`, `AircraftSiblingMap`, `AirlineFleetData` — populated by `TestVnasData.EnsureInitialized()`.
- Why fragile: xUnit parallelizes test classes by default; a test class can read a static singleton while another class is mid-populating it, producing intermittent, hard-to-reproduce mismatches (CLAUDE.md gives a concrete example: `DecelRate` returning a default-fallback value in one call and the loaded profile value in the next, within the same test run).
- Safe modification: call `TestVnasData.EnsureInitialized()` in the racing test class's constructor to pin state before any test method runs; never assume a singleton starts empty.

**MainViewModel / MainWindow as unmediated orchestration hubs [Documented as intentional]:**
- Files: `src/Yaat.Client/ViewModels/MainViewModel.cs`, `src/Yaat.Client/Views/MainWindow.axaml.cs`.
- Why fragile: no dependency injection (CLAUDE.md: "No DI: MainWindow creates MainViewModel directly"), so all client-side wiring for new SignalR-driven features funnels through these two files. Combined with their high churn rate (20 and 12 commits respectively in the last 300), simultaneous feature work here has elevated merge-conflict risk. This is an accepted tradeoff for a solo-maintained desktop app, not a defect, but worth flagging for anyone planning parallel work that touches client wiring — CLAUDE.md's parallel-agent partitioning rule ("partition by directory or file set with zero overlap") is especially important here given the file's centrality.

## Scaling Limits

Not applicable in the traditional sense — YAAT is a training-simulator client/server pair, not a scaled web service. The closest analogue is the tick-loop single-thread-per-server constraint documented above under Performance Bottlenecks, which caps concurrent-room throughput on one server process; no numeric ceiling was found documented anywhere in this repo (the yaat-server repo would be the authoritative source for room-count capacity, out of scope here).

## Dependencies at Risk

**LM-Kit.NET is a hard, isolated dependency with its own maintenance checklist [Documented]:**
- Package: LM-Kit.NET 2026.7.4, confined by CLAUDE.md to `Yaat.Client` and `Yaat.SpeechSandbox` only ("nothing else in `src/` may reference it").
- Risk: version bumps require a documented multi-step checklist (SQLite pin, `BackendVersion` lockstep, CUDA version alignment) per the memory note `lmkit_bump_sqlite_pin_and_cuda_version.md` and `docs/speech-recognition-pipeline.md`. This is a third-party commercial SDK with tight coupling to native backend versions (SQLite, CUDA), making routine dependency bumps riskier than typical NuGet package updates.
- Migration plan: none needed/planned; flagging as a dependency that cannot be bumped mechanically (e.g., via Dependabot) without following the documented checklist.

## Missing Critical Features

**Live-session DVR/scrub-back is explicitly unbuilt [Documented]:**
- Problem: `docs/plans/open-issues/150-live-traffic-swim.md` states the pause/scrub-back-through-server-feed-log capability ("DVR over the server's raw-log window") is *designed* (`09-live-sessions.md` in yaat-server) but *not built*, while the surrounding live-session feature (Start Live Session, LIVE/PAUSED/PLAYBACK badge, Go Live) shipped 2026-08-29.
- Blocks: full parity between live-traffic sessions and the recording/replay experience available for authored scenarios.

## Test Coverage Gaps

**Cross-repo signature changes are only caught by the opt-in combined test runner [Undocumented as a gap, though the mitigation is documented]:**
- What's not tested by default: `dotnet test` run bare in this repo (the natural default for an agent verifying a change) does not build or test `yaat-server`, so a `Yaat.Sim` public-API change that breaks the sibling repo passes locally.
- Files: any public surface in `src/Yaat.Sim/`.
- Risk: an agent or contributor who runs the "obvious" verification command (`dotnet test`) and sees green has not verified cross-repo compatibility; only `pwsh tools/test-all.ps1` or the `prek` pre-commit hook (which builds `yaat.slnx`, pulling in `yaat-server`) catch this.
- Priority: Low as an actual risk (CLAUDE.md already prescribes `tools/test-all.ps1` as the standard "whole suite" replacement and the pre-commit hook provides a backstop), but worth stating explicitly since nothing in the repo enforces `test-all.ps1` usage other than convention — there is no CI gate visible in this repo (`yaat.slnx`-based CI would need to be checked in `yaat-server` or a `.github/workflows/` directory not examined in this pass, since CI config wasn't part of this focus area).

**Diagnostic-only test class permanently skipped [Documented in-code, restated for visibility]:**
- What's not tested: `Skw3078FixComparisonCapture.Capture_Before` / `Capture_After` (see Tech Debt above) are permanently `[Fact(Skip = ...)]` and only produce diagnostic artifacts, not assertions — they were never meant to run in CI, so this is not a coverage gap in the traditional sense, but it means the specific SKW3078 regression scenario has no standing automated assertion of its own beyond `FilletDiagnosticTests.SKW3078_TaxiAtoB10_AdvancesPastFormerStallSegment` (referenced in the class's doc comment).
- Priority: Low — the real assertion test (`FilletDiagnosticTests`) exists separately; this class is correctly scoped as artifact-generation tooling.

---

*Concerns audit: 2026-09-01*
