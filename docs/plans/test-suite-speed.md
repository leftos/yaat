# YAAT-1965: Yaat.Sim.Tests profile, per-test cost of the all-layout sweeps

Tree: worktree `X:/dev/yaat.wt/yaat-1965/yaat` at 26fd8c399, Release build, MTP executable `tests/Yaat.Sim.Tests/bin/Release/net10.0/Yaat.Sim.Tests.exe`, 16 logical cores. Every timed run went through `tools/gate.ps1 … --slot critical --alone --local --reason "YAAT-1965 suite profile"`, and every log header shows `holds the machine alone (critical)`. Process CPU is user plus kernel from `Process.TotalProcessorTime` of the test executable. 181 committed `TestData/*.geojson` layouts.

Sweep ids used below:

- S1: `RunwayCrossingDetectorTests.EveryCommittedLayout_EveryRouteOntoARunwayFromBeyondItsHoldingDistance_CrossesABarOfThatRunway`
- S2: `RunwayCrossingDetectorTests.EveryCommittedLayout_NoAngledBarSitsInsideAnotherRunwaysHoldingArea`
- S3: `Helpers.TestLayoutCoverageTests.RunwayOnlyFixtures_MatchTheCommittedList`
- S4: `Helpers.TestAirportGroundDataCaseTests.EveryCommittedGeoJson_ResolvesRegardlessOfFilenameCase` (reads every layout's text and does not parse it)
- P5: `Helpers.TestLayoutCoverageTests.RecordingsDeclaringAGroundLayout_ResolveToATaxiCapableFixture`. This is not an all-layout sweep: it reads every recording's manifest and parses only the declared airports. It is listed because it shares the layout cache.

No other test in Yaat.Sim.Tests enumerates `TestData/*.geojson`. The fixed-list loops (`PushThenTaxiSweepTests`, `Req1MembershipArcSweepTests`, `StateAwarePruningNecessityTests` and others) name their airports.

## SWEEPS

Isolated cost: the test run alone in its own process, 3 repeats. The marginal cost is the mean process CPU minus the mean of a one-trivial-test baseline (`ScenarioAirportCollectorTests.AirportsOf_IcaoAndFaaIds_FoldToOneFaaId`: 2.59 s CPU, range 2.45–2.81; 1.81 s wall; ~465 MB peak WS). Every layout parse is cached per process (`TestAirportGroundData.Cache`), so the first sweep to run pays for the parse. The isolated marginals therefore overlap and do not add.

| Sweep | Process CPU, mean (range) s | Marginal CPU s | Share of suite CPU (2,010.9 s) | Wall, mean s | Peak WS MB | Own work CPU, excl. parse (trace ms: alone / all-sweeps run) | Own share of suite CPU | Allocations, own (sampled MB / ticks) | In-suite duration (TRX wall s: pair 2 / pair 3) |
|---|---|---|---|---|---|---|---|---|---|
| S1 | 11.45 (10.44–12.03) | 8.87 | 0.44% | 9.34 | 587–589 | 117 / 54 | 0.003–0.006% | 4.3 / 42 | 0.207 / 0.231 |
| S2 | 11.51 (10.97–12.05) | 8.92 | 0.44% | 9.45 | 584–585 | 104 / 185 | 0.005–0.009% | 5.7 / 56 | 0.367 / 13.756 |
| S3 | 11.00 (10.88–11.09) | 8.41 | 0.42% | 8.66 | 585–590 | 35 / 46 | 0.002% | 0.2 / 2 | 5.918 / 0.002 |
| S4 | 2.73 (2.66–2.78) | 0.14 | 0.007% | 1.98 | 453–459 | n/a / 31 | 0.002% | 25.4 / 217 | 0.118 / 0.033 |
| P5 (related) | 7.23 (7.03–7.50) | 4.64 | 0.23% | 5.83 | 530–573 | 84 / 99 | 0.004–0.005% | 44.9 / 291 | 1.557 / 2.710 |
| All five in one process | 12.62 (12.52–12.77) | 10.03 | 0.50% | 9.91 | 604–605 | 415 summed | 0.02% | 80.5 summed | 8.17 / 16.73 summed |

The shared layout parse, from the all-sweeps CPU trace: 9,747 ms of 14,006 ms trace CPU (69.6%) sits under `TestAirportGroundData.Parse`. That figure includes up to 612 ms of `TestVnasData.EnsureInitialized`, which the first parse triggers, leaving about 9.1 s of layout parse, or about 50 ms per layout. Parse allocations are 1,089.7 MB (9,846 ticks), which includes 283.8 MB of CIFP loading the first parse triggers; about 806 MB is attributable to the layouts. Peak working set is about 140 MB above the baseline once all 181 layouts are cached.

In the suite, whichever of S2 or S3 starts first pays the parse: S3 took 5.92 s in pair 2 and S2 took 13.76 s in pair 3. Every later sweep then takes 0.002–0.37 s.

Per-sweep traces (ms of trace CPU; parse means the bucket under `TestAirportGroundData.Parse`):

| Trace | Total | Parse | Own | ModuleInit | Other (runtime, JIT, host) |
|---|---|---|---|---|---|
| S1 alone | 12,535 | 8,392 (66.9%) | 117 | 1,352 | 2,675 |
| S2 alone | 10,019 | 6,915 (69.0%) | 104 | 1,033 | 1,967 |
| S3 alone | 8,696 | 6,395 (73.5%) | 35 | 872 | 1,395 |
| P5 alone | 6,861 | 3,532 (51.5%) | 84 | 1,146 | 2,098 |
| all sweeps | 14,006 | 9,747 (69.6%) | S1 54, S2 185, S3 46, P5 99, S4 31 | 1,231 | 2,613 |

## SUITE TOTALS

Scope: the default local run, `--filter-not-trait Category=Nightly|PathfinderGrid|Desktop`. 20,128 results, 0 failed, 4 skipped.

| Run | Order | Process CPU s | User s | Kernel s | Wall s | Peak WS MB |
|---|---|---|---|---|---|---|
| suite, pair 1 | 1st | 1,967.77 | 1,859.45 | 108.31 | 462.00 | 7,525 |
| suite minus S1–S4+P5, pair 1 | 2nd | 1,783.94 | 1,675.19 | 108.75 | 409.81 | 7,994 |
| suite, pair 2 | 1st | 1,922.44 | 1,815.75 | 106.69 | 424.25 | 7,712 |
| suite minus, pair 2 | 2nd | 1,672.75 | 1,557.78 | 114.97 | 407.07 | 7,444 |
| suite minus, pair 3 | 1st | 1,751.77 | 1,655.89 | 95.88 | 418.20 | 7,896 |
| suite, pair 3 | 2nd | 2,142.36 | 1,945.42 | 196.94 | 401.38 | 8,645 |
| **suite, mean** | | **2,010.86** | | | **429.2** | |
| **suite minus, mean** | | **1,736.15** | | | **411.7** | |

The suite-minus-suite difference (+184, +250 and +391 s CPU; +274 and +443 s of TRX test-work in pairs 2 and 3) is not the sweeps' cost. The TRX split around the first sweep's start puts the extra test-work in tests that ended before any sweep started:

| Pair | Ended before first sweep | Spans it | Started after it |
|---|---|---|---|
| 2 | +218.3 s (+14.6%) | +58.0 s | −2.3 s (−0.3%) |
| 3 | +173.3 s (+19.9%) | +121.2 s | +147.7 s (+9.0%) |

Runs of the same configuration differ by as much. Comparing suite-minus pair 3 with pair 2, tests that ended before the same cut differ by +300.2 s (+17.9%). Total test-work between the two plain suite runs differs by 445 s. Run-to-run CPU spread is 220 s on the full suite and 111 s on suite-minus, about ±5%. The sweeps' measured 10.0 s (0.5%) is below that floor, so a suite-level A/B cannot resolve it.

## HOT FRAMES

All-sweeps CPU trace (`.tmp/prof/all-sweeps-cpu.speedscope.json`). The busiest thread is a .NET ThreadPool thread: 11,227.7 ms CPU over 12,855.8 ms wall (cpu/wall 87.3%). The next two threads are 1,178.6 ms (9.1%) and 1,128.3 ms (8.7%); the other 12 threads are under 175 ms each. The whole parse runs serially on one thread.

Self-CPU on the busiest thread:

| ms | % of thread | Frame |
|---|---|---|
| 2,143.4 | 19.1% | `Thread.<PollGC>g__PollGCWorker` (GC done on the allocating thread) |
| 1,918.2 | 17.1% | `Fillet.FilletPlanCutRedirect.ExtendWithStableAnchors` |
| 1,549.5 | 13.8% | `Fillet.SharedArmTangentPass.ApplyGlobalCoincidentCutCoalesce` |
| 1,216.1 | 10.8% | `GeoJsonParser.FindNearestConnectorTarget` |
| 423.3 | 3.8% | `Interop+BCrypt.BCryptCreateHash` |
| 331.8 | 3.0% | `Enumerable.Any` |
| 210.0 | 1.9% | `RuntimeCustomAttributeData.GetCustomAttributeRecords` |
| 196.3 | 1.7% | `Monitor.Enter_Slowpath` |
| 184.3 | 1.6% | `CubicBezier.ArcLengthNm` |
| 140.7 | 1.3% | `GeoMath.FootOfPerpendicular` |
| 122.8 | 1.1% | `TaxiwayGraphBuilder.FindLineStringIntersections` |

Inclusive CPU, all threads (% of 14,006 ms):

| ms | % | Frame |
|---|---|---|
| 9,746.7 | 69.6% | `TestAirportGroundData.Parse` |
| 8,997.3 | 64.2% | `GeoJsonParser.BuildLayout` |
| 6,548.5 | 46.8% | `FilletArcGenerator.Apply` |
| 4,751.0 | 33.9% | `Fillet.FilletPlanBuilder.Build` |
| 1,979.4 | 14.1% | `Fillet.FilletPlanCutRedirect.ExtendWithStableAnchors` |
| 1,601.9 | 11.4% | `Fillet.SharedArmTangentPass.ApplyGlobalCoincidentCutCoalesce` |
| 1,519.7 | 10.9% | `GeoJsonParser.FindNearestConnectorTarget` (via `ConnectParkingToTaxiway` → `ConnectToNearestTaxiway`, 1,514.0) |
| 1,159.8 | 8.3% | `ModuleInit.<Initialize>` (every process; `AirspaceDatabase.LoadDefault` 1,052.9) |
| 1,035.9 | 7.4% | `Fillet.FilletPlanExecutor.Execute` |
| 761.8 | 5.4% | `Fillet.FilletEdgeSplitPlanner.Plan` |
| 612.1 | 4.4% | `TestVnasData.EnsureInitialized` |
| 539.2 | 3.8% | `Fillet.FilletPlanExecutor.AddEdge` |

The same top four self frames lead the S1, S2 and S3 traces: PollGCWorker 20.6–27.1%, ExtendWithStableAnchors 12.0–16.4%, ApplyGlobalCoincidentCutCoalesce 8.8–12.4%, FindNearestConnectorTarget 6.5–10.2%.

## ALLOCATIONS

From the gc-verbose trace of all five sweeps in one process (`.tmp/prof/all-sweeps-gc-verbose.nettrace`): 17,744 ticks, 2,227.8 MB sampled, about one tick per 100 KB. The byte columns are estimates; rank by them and do not quote them as exact.

By bucket (a tick goes to the first bucket found on its stack, in this order):

| Bucket | MB | Ticks |
|---|---|---|
| layout parse (`TestAirportGroundData.Parse`) | 1,089.7 | 9,846 |
| ModuleInit (airspace, aircraft table, military routes) | 607.1 | 2,861 |
| no Yaat frame (runtime, host) | 450.5 | 4,429 |
| P5 own | 44.9 | 291 |
| S4 own | 25.4 | 217 |
| S2 own | 5.7 | 56 |
| S1 own | 4.3 | 42 |
| S3 own | 0.2 | 2 |

Nearest `Yaat.*` frame, top rows (from the skill's `alloc_ticks.cs`):

| MB | Ticks | Frame | Owner |
|---|---|---|---|
| 247.9 | 1,250 | `AirspaceDatabase.ReadGeoJsonText` | ModuleInit |
| 160.1 | 3 | `AirspaceDatabase.FromGeoJson` | ModuleInit |
| 142.2 | 1,354 | `AirspaceDatabase.ParsePolygon` | ModuleInit |
| 133.8 | 1,316 | `Fillet.ArmCutResolver.Resolve` | parse |
| 112.8 | 1,103 | `CifpParser.ParseRunwayEnds` | first parse (CIFP load) |
| 109.3 | 1,075 | `CifpParser.ParseNavaids` | first parse (CIFP load) |
| 67.2 | 660 | `Fillet.FilletEdgeSplitPlanner.Plan` | parse |
| 61.7 | 103 | `CifpAirportIndex.Build` | first parse (CIFP load) |
| 46.4 | 457 | `Fillet.TaxiwayWalk.Walk` | parse |
| 44.6 | 288 | `TestLayoutCoverageTests.ReadManifest` | P5 |
| 42.5 | 410 | `Fillet.FilletPlanExecutor.MaterializeCutNodes` | parse |
| 32.0 | 263 | `TestAirportGroundData.Parse` (file text) | parse |
| 28.5 | 278 | `Fillet.FilletPlanExecutor.Execute` | parse |
| 27.8 | 273 | `AirportGroundLayout.RebuildAdjacencyLists` | parse |
| 25.6 | 95 | `NavigationDatabase.BuildIndex` | ModuleInit |
| 25.2 | 215 | `TestAirportGroundData.GetSourceGeoJson` | S4 |
| 24.0 | 236 | `Fillet.FilletPlanConsistency.ValidateCutReferences` | parse |
| 22.3 | 219 | `GeoJsonParser.FindConnectedGateAnchor` | parse |
| 22.1 | 217 | `Fillet.SharedArmTangentPass.ApplyIntraArmCoalesce` | parse |
| 21.1 | 207 | `Fillet.FilletPlanExecutor.AddEdge` | parse |

The sweeps' own top frames are `RunwayCrossingDetectorTests.PathInFromBar` for S2 (3.5 MB / 34 ticks) and `RunwayCrossingDetectorTests.FindUnbarredEscapes` for S1 (2.6 MB / 25 ticks).

GC in the same run: 43 GCs (gen0 12, gen1 25, gen2 6; 38 non-concurrent, 5 background), 522 ms from suspension to restart in total, heap count 1.

## CANDIDATE TRIMS

None of these is a fixed-budget replay loop: the sweeps are single passes over the layouts with no tick window. Each trim is a separate issue for the owner to approve; none is implemented. Savings are per test process.

| # | Trim | Measured saving | Coverage it would change |
|---|---|---|---|
| T1 | Tag S1, S2, S3 and S4 `Category=Nightly`, so default local and per-PR runs exclude them while CI nightly keeps them. | Between 8.87 s (S1 alone) and 10.03 s (all five together) of CPU, 0.44–0.50% of the 2,010.9 s suite; about 806 MB of parse allocation and about 140 MB of peak WS. In-suite, 5.92 s or 13.76 s of one test thread's wall (whichever sweep pays the parse). The suite A/B cannot resolve this saving: it is below the ±5% run-to-run spread. Layouts other tests parse anyway are not saved; the in-suite pair-2 cost of 5.92 s against 6.85 s isolated (S3 wall minus baseline) puts that overlap at about 14%. | Default and PR runs would stop checking, for every committed layout, unbarred escapes onto a runway (S1), angled bars inside another runway's holding area and bar-order inversions (S2), the runway-only fixture list (S3) and case-insensitive fixture resolution (S4). Nightly keeps all four. |
| T2 | S4: assert that each fixture resolves to its path without reading its text through `GetSourceGeoJson`. | At most 0.14 s CPU (S4's whole marginal) and 25.4 MB (217 ticks). | `GetSourceGeoJson`'s read would no longer be exercised by this test. Path resolution, which the test's doc comment names as its subject, keeps its coverage. |
| T3 | P5: read each recording's `manifest.json` without first copying a nested bundle archive into a `MemoryStream`. | At most 84–99 ms CPU and 44.6 MB (288 ticks) in `ReadManifest`. | None if the same manifests are read. P5 is not an all-layout sweep. |

## OBSERVATIONS

1. The sweeps' own logic is cheap: 31–185 ms CPU each and 0.2–5.7 MB of allocation. Their cost is the first full parse of all 181 layouts with the standard fillets: about 9 s CPU on one thread and about 806 MB of allocation. The parse lives in production code (`FilletPlanCutRedirect.ExtendWithStableAnchors`, `SharedArmTangentPass.ApplyGlobalCoincidentCutCoalesce`, `GeoJsonParser.FindNearestConnectorTarget`), so speeding those frames up is not a test trim.
2. `TestAirportGroundData.GetLayout` holds the process-wide `CacheLock` across each `Parse`. The 181 parses therefore run serially on one thread (cpu/wall 87–91% on a single thread, with every other thread under 10%). While they run, any other test's `GetLayout`, even a cache hit, waits on that lock. The wait time on other threads was not measured.
3. `tests/Yaat.Sim.Tests/collection-weights.json` (generated 2026-10-19, before 21bd846c7) weights `TestLayoutCoverageTests` at 29 and `RunwayCrossingDetectorTests` at 13. In the suite, one test of these classes now runs 5.9–13.8 s.
4. `Yaat.Sim.Tests.csproj` sets `<ServerGarbageCollector>true</ServerGarbageCollector>`, but the documented MSBuild property is `ServerGarbageCollection` (https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector#workstation-vs-server). The generated `Yaat.Sim.Tests.runtimeconfig.json` has no `System.GC.Server`, and `DOTNET_gcServer` is not set anywhere in the repo, so the suite runs workstation GC. The gc-verbose trace is consistent with that (heap count 1), as is PollGCWorker taking 19–27% of the busiest thread's self-CPU. The property came in with 597e254da. Its effect on the suite was not measured here.
5. The suite now costs 1,672.75–2,142.36 s of process CPU and 401.4–462.0 s of wall (default scope, local, 20,128 results). The dotnet-test-suite-profiling skill still states 811 s of test-work and a 57.2 s suite.
6. Ungated processes kept running beside the alone gates: 5.0–6.1 cores busy outside the gate at the start of the sweep and suite gates, and 11.0 cores at the start of the CPU-trace gate. The trace millisecond figures are inflated relative to the accounting runs (all-sweeps 14.0 s in the trace against 12.6 s accounted). Use the trace for proportions and the accounting table for absolute CPU. The pair-3 full-suite run also had kernel time 197 s against 96–115 s in the other five runs.

Artifacts (in the worktree's ignored `.tmp/prof/`): `cpu-sweeps.csv`, `cpu-suite.csv`, `{S1,S2,S3,P5,all-sweeps}-cpu.nettrace` with `.speedscope.json`, `all-sweeps-gc-verbose.nettrace`, and the gate logs `cpu-sweeps.log`, `cpu-suite.log`, `phase3.log`, `cpu-traces.log`, `alloc.log`, `alloc-buckets.log`, `gcmode.log`. The four suite TRX files (about 385 MB each) were deleted after their numbers were extracted.
