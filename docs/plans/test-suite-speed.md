# Test suite speed

Current baseline (2026-09-04, Release, 16 cores, quiet machine, median of 3 — measured by
`tools/measure-test-loop.ps1`; full table in [tunit-migration.md](tunit-migration.md)):

| | |
|---|---|
| `tools/test-all.ps1`, both repos | **69.9s** |
| Sim.Tests, dev filter (9,363 tests) | **57.2s** wall, 489s CPU |
| Sim.Tests, unfiltered (10,586 tests) | 71.9s wall, 585s CPU |
| One filtered class (30 tests) | 2.3s |
| Incremental rebuild after a test edit | 3.3s |

Historical (2026-08-26): ~2m50s for the gate; Sim.Tests 92s, 1,358s CPU, 8,686 tests. The tasks
below did that work — CPU is down 64%, wall down 38%.

**The suite is near its scheduling limit, and more parallelism cannot help.** 811s of test-work
over 16 workers has a hard floor of 50.7s; actual is 57.2s, i.e. **~89% scheduling efficiency**.
`tools/analyze-test-schedule.py` LPT-packs a TRX under both a per-class and a per-test scheduler and
finds **no difference between them** — the heaviest class (36.3s) sits below the divisibility bound,
so class grouping never becomes the constraint.

> **Do not read CPU/wall as core utilisation.** It is now 8.6× of 16, down from 14.8×, which looks
> like idle capacity and is not: the optimisations below removed CPU work (−64%) faster than wall
> time (−38%), so the ratio fell while the schedule stayed just as full. CPU/wall measures how
> CPU-bound the tests are. To ask whether the *scheduler* is leaving capacity on the table, simulate
> the packing — that is what `analyze-test-schedule.py` exists for. This misreading was made and
> caught here; it nearly reversed a recommendation.

305 tests (>1s each) account for 89% of CPU; 72 tests (>5s) for 44%. These are
`SimulationEngine.Replay(recording, N)` E2E tests re-simulating from t=0 (287 sites, median N=200s, max 2,835s).

Decisions:
- Snapshot-seek already exists as **hybrid replay** (`Replay(recording, 0)` → `RestoreFromSnapshot` → `ReplayOneSecond`; `docs/e2e-tdd-issue-debugging.md` §5b, 51 test files use it). Converting from-zero `Replay(recording, N)` tests to it is a per-test judgment (hybrid tests only the post-T slice and can false-pass a fix that alters the path before T), so no blanket conversion was done.
- Stay on xunit.v3 **3.2.2** (not 4.0.0): Avalonia.Headless.XUnit 12.1.0 pins `xunit.v3.extensibility.core 3.2.2`.
- **Stay on xunit.v3, not TUnit** (evaluated 2026-09-03/04, [tunit-migration.md](tunit-migration.md)): the scheduling prize measures 0.0s, and TUnit's source generator adds ~5s to every incremental build at 9,306 cases — a penalty that grows linearly with test count. The edit-run loop would go from ~5.6s to ~10.4s.

## Tasks

### A. Run test assemblies concurrently — xunit v3 + Microsoft.Testing.Platform
- [x] `Yaat.Sim.Tests`: xunit 2.9.3 → xunit.v3 3.2.2, `MartinCostello.Logging.XUnit` → `.v3`, drop `using Xunit.Abstractions`
- [x] `Yaat.Server.Tests`: same migration (+ drop unused `coverlet.collector`)
- [x] `UseMicrosoftTestingPlatformRunner=true` on all four test projects; `global.json` `test.runner` in both repos
- [x] `tools/test-all.ps1`: MTP filter syntax (`--filter-not-trait`); modules run in parallel (yaat solution 131s → 106s)
- [x] CI: `ci.yml` (both repos), `nightly-taxi-grid.yml` — filter + TRX flags; `nightly-review.yml` prose example
- [x] Docs/skills: `CLAUDE.md`, `docs/test-harness.md#running-tests`, `docs/logging.md`, `test-fix`, `stt-pipeline-debugging`, …
- [x] Fixed: `GlobalKeyHookService` ran SharpHook's hook thread as a foreground thread — MTP refused to exit
      (`Foreground threads were left running`) after any UI test that built a `MainWindow`; now `runAsyncOnBackgroundThread: true`
- [x] Server GC on Sim/Server test projects: Sim suite 92s → 80s (workstation GC left 16 test threads in `PollGCWorker`)

### B. Profile the tick loop under replay, fix hot spots
- [x] `dotnet-trace` CPU + gc-verbose on `Issue172Ual2164TerminusTests` (20.8s in-suite). Method + aggregation scripts:
      `scratchpad/speedscope_agg.py` (CPU per thread from speedscope JSON), `.tmp/alloc-aggregator.Program.cs` (TraceEvent
      AllocationTick → type + nearest Yaat frame). Findings for that test (9.3s CPU on the test thread, 4.4 GB sampled allocations):
      - 29% `RecordingArchive.LoadSnapshotAircraftForSpawnSynthesis` — every snapshot fully decoded (Brotli → `string` → JSON) to
        find first-seen callsigns; snapshot 0 decoded three times. 43% of all allocations.
      - 8% `MagneticDeclination.GetDeclination` — each `Geo` WMM evaluation allocates ~100 KB `double[,]`; 371 MB per test.
      - 23% `TaxiPathfinder.ResolveExplicitPathDetailed` for one TAXI command (`RunBoundedDetour` 1.3s); `ImmutableHashSet<int>`
        visited-set churn ~350 MB (the set was replaced in D; the detour search itself remains a follow-up).
      - 35% of thread CPU in `Thread.PollGCWorker` + finalizer thread 3.3s in `SharedArrayPool.Trim` (gen2 pressure) → Server GC above.
      - One-time per process: CIFP parsing ~860 MB of `ReadLine` strings (SIDs parsed twice: `LoadSids` + `GetSupplementarySids`),
        `AirspaceDatabase.LoadDefault` 0.75s (lazily, on the physics path).
- [x] `RecordingArchive`: deserialize straight from the Brotli stream; decode snapshot 0 once; spawn synthesis scans later snapshots
      with `JsonDocument` and deserializes only first-seen aircraft
- [x] `MagneticDeclination`: process-wide 0.02° grid cache, evaluated at cell centres (order-independent → reproducible replays)
- [x] `RecordingLoader.Load` memoized per path (engine never mutates `Actions`; `FromSnapshot` copies DTOs)
- [x] Guard: targeted replay/archive classes (102 tests) + `tools/test-all.ps1`

### C. Budget-bounded loop audit
- [x] 161 fixed-budget tick loops with no early exit, in 94 files (`scratchpad/loop_audit2.py`), ranked by class duration
- [x] Verdict: every costly one is a **window invariant** (min-distance over 300s, spawn counts over an hour, max-altitude over a
      climb window, stall counting) — breaking early would change what's proven; `PatternDirectionResetTests` already breaks.
      The cost in the slow classes is the `Replay(recording, N)` seek, not the loop. **No conversions made.**

### D. Follow-ups from the profile
- [x] Pathfinder: `PartialRoute.VisitedNodeIds` is a `VisitedNodeSet` (sorted `int[]`, copy-on-add) instead of `ImmutableHashSet<int>`
- [x] CIFP: `CifpAirportIndex` — one byte-range scan per file per process; airport-scoped parsers no longer stream the whole 50 MB file
      per call (the supplementary-cycle file is a different file, so both cycles were paying a full scan per airport per procedure type)
- [x] Test `ModuleInit` warms `AirspaceDatabase.Default` / `MilitaryRouteDatabase.Default` on a background task (the server already does)
- [x] `docs/plans/MAIN.md` index created

### E. The fixed floor of a filtered run (2026-09-04)

A filtered run costs the same whether it runs 0 tests or 30 — the floor, not the tests, is what a
tight TDD loop pays. Decomposed with `--filter-class "*.NoSuchClass"` plus temporary stopwatches in
`ModuleInit`, against a shape-matched synthetic xunit project (`tools/gen-synthetic-suite.py`) as
the control:

| Phase | Cost | Ours? |
|---|---:|---|
| process start → `ModuleInit` (11 assemblies) | ~32 ms | no |
| **live HTTPS GET to `configuration.vnas.vatsim.net`** | **~240 ms** | **yes — fixed** |
| rest of `ModuleInit` (CIFP resolve 9 ms, flags) | ~10 ms | yes |
| xunit init + discovery + JIT over the assembly graph | ~2.17 s | mostly no |

- [x] The vNAS config fetch ran unconditionally in `NavDataPathResolver.ResolveCore`, before any
      cache check and regardless of `AllowDownload` — so every test process paid a TLS handshake and
      the suite depended on VATSIM being reachable. Now fetched only when a download could follow;
      `ModuleInit` passes `AllowDownload: false`. **Floor 2.42 s → 2.11 s; one class 2.58 s → 2.23 s.**
      Guarded by `NavDataPathResolverTests.TestProcess_ResolvesNavData_WithoutContactingVnas`
      asserting `ConfigFetchCount == 0` (an assertion on the call, not on a duration).
- [x] Measured, rejected: **ReadyToRun**. `dotnet publish -p:PublishReadyToRun=true` cuts the floor
      2.53 s → 2.21 s, but only for a *published* exe, so the whole dev loop would have to stop
      using `dotnet test`. Not worth the workflow change for ~0.3 s.

The remaining ~2.17 s is CLR startup plus xunit reflection discovery over YAAT's assembly graph —
the same synthetic project at the same test count does that phase in ~0.87 s, so ~1.3 s is the cost
of our assembly graph specifically. Not reducible without changing framework or splitting the
assembly. (Source-generated discovery *is* the one thing that would attack it — see
[tunit-migration.md](tunit-migration.md) for why the build-time cost outweighs it anyway.)

Note for anyone re-measuring: `--list-tests` is a poor proxy for discovery. On the synthetic project
it costs ~1.06 s more than a zero-match run at the same test count, almost all of it printing ~9,300
lines to the console.

## Follow-ups (not done)
- The bounded-detour search itself (`SegmentExpander.RunBoundedDetour`, >1s for one TAXI resolution) — algorithmic; needs its own profile.
- Per-test review of from-zero `Replay(recording, N)` sites that could legitimately be hybrid replay (see Decisions).
