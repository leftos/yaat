# Test suite speed

Baseline (2026-08-26, Release, 16 cores, `tools/test-all.ps1`): ~2m50s wall.
Build 17s → Client.Tests 18s → Client.UI.Tests 21s → **Sim.Tests 92s** → Server.Tests 17s, strictly sequential.

Sim.Tests: 8,686 tests, 1,358s CPU over 92s wall (14.8× on 16 cores — already saturated).
305 tests (>1s each) account for 89% of CPU; 72 tests (>5s) for 44%. These are
`SimulationEngine.Replay(recording, N)` E2E tests re-simulating from t=0 (287 sites, median N=200s, max 2,835s).
Fresh per-test timings: `.tmp/trx-fresh/sim.trx` in the main checkout.

Decisions:
- Snapshot-seek in `Replay` deliberately **not** pursued — it changes what the parity tests prove.
- Stay on xunit.v3 **3.2.2** (not 4.0.0): Avalonia.Headless.XUnit 12.1.0 pins `xunit.v3.extensibility.core 3.2.2`.

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
        visited-set churn ~350 MB. **Not changed** — algorithmic, sim-behaviour-sensitive; see follow-ups.
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

## Follow-ups (not done)
- Pathfinder allocation: `AutoRouter.RunAstar` builds an `ImmutableHashSet<int>` per expanded `PartialRoute`; a bounded-detour
  resolution can cost >1s. Needs its own profile + a behaviour-preserving redesign (e.g. bitset visited-sets), not a tweak.
- `NavigationDatabase.GetSupplementarySids` re-parses the CIFP SID file that `LoadSids` already parsed (~220 MB of strings per process).
- `AirspaceDatabase.LoadDefault` runs lazily from `FlightPhysics.RegulatorySpeedLimit` — 0.75s inside the first tick of every process.
- Snapshot-seek `Replay` variant (see Decisions).
