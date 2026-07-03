# Test Harness, Fixtures & Singleton-Race Protocol

> Read this before writing any Yaat.Sim test, before adding a new test project, or when a test "passes alone but flakes in the
> suite." The harness is its own subsystem: ~420 Sim test files share a dozen reusable `Helpers/`, three test projects, real (never
> synthetic) vNAS/FAA data behind static singletons, and a set of correctness conventions that live nowhere in code comments. Getting
> them wrong does not fail the build — it produces intermittent, hard-to-trace flakes. This is the single reference for those conventions.

## Scope and the iron rules

There are three test projects:

| Project | Tests | Notable harness wiring |
|---|---|---|
| `tests/Yaat.Sim.Tests` | Simulation, commands, physics, phases, navdata, parsers, ground/pathfinder | `ModuleInit` warms CIFP/NavData + `GroundNavigator.ThrowOnOrbit`; `xunit.runner.json` parallelizes collections |
| `tests/Yaat.Client.UI.Tests` | Avalonia headless UI / `MainViewModel` / `UserPreferences` | `ModuleInit` redirects `YAAT_APPDATA_DIR`; `xunit.runner.json` *disables* collection parallelism |
| yaat-server tests (`../yaat-server`) | Server rooms, hub, broadcast | Reached only via `tools/test-all.ps1` — bare `dotnet test` from yaat never builds them |

The non-negotiable rules, each detailed below:

1. **Real data, never synthetic.** Initialize with `TestVnasData.EnsureInitialized()`; never hand-roll stub fixes/profiles.
2. **Call `EnsureInitialized()` in the test class *constructor*** if any test reads a data-backed static singleton (race protection).
3. **Silently skip on missing data** — return early, no `Assert.Skip`, no throw — so a fresh/offline checkout keeps CI green.
4. **Run suites under a 30 s wall clock** (`timeout 30 dotnet test`) so soft hangs surface as failures.
5. **For full-suite confidence run `pwsh tools/test-all.ps1`**, not bare `dotnet test` — only that builds the sibling yaat-server.

## Real data, never synthetic — `TestVnasData.EnsureInitialized()`

(`src/Yaat.Sim/Testing/TestVnasData.cs` — note: this lives in the **production** `Yaat.Sim` assembly under namespace `Yaat.Sim.Testing`,
so add `using Yaat.Sim.Testing;` to reference it.)

`EnsureInitialized()` is the one entry point. It loads the committed JSON/protobuf fixtures from `TestData/` and `Data/` and populates
the data-backed static singletons. One call wires **seven** initialization sinks:

| Initialization call | Source file | Backed singleton / effect |
|---|---|---|
| `AircraftCategorization.Initialize` | `TestData/AircraftSpecs.json` | engine-type → `AircraftCategory` (Jet/TP/Piston/Helicopter) |
| `WakeTurbulenceData.Initialize` | `TestData/AircraftCwt.json` | type code → CWT category |
| `FaaAircraftDatabase.Initialize` | `TestData/FaaAcd.json` | FAA aircraft records (SRS, weight class) |
| `AircraftProfileDatabase.Initialize` | `Data/AircraftProfiles.json` | per-type performance profiles |
| `AircraftSiblingMap.Initialize` | `Data/AircraftProfileSiblings.json` | profile fallback siblings |
| `AircraftPerformance.SetProfileCorrectionAdapter` | (Eurocontrol adapter) | profile correction hook |
| `NavigationDatabase.SetInstance` | `TestVnasData.NavigationDb` | the process-wide nav database (fixes, runways, procedures) |

The first six sinks (the four `Load*` helpers — `LoadAircraftSpecs`/`LoadAircraftCwt`/`LoadFaaAcd`/`LoadAircraftProfiles`, the last of
which wires the profile DB, the sibling map, *and* the correction adapter) run once per process behind a double-checked `_initialized`
flag. **The `NavigationDatabase.SetInstance` call at the bottom of `EnsureInitialized()` runs on *every* call, not just the first** — see
the footgun below.

Synthetic stub fixes/profiles are banned. They compile and pass against themselves while hiding the integration bugs that only appear
against the real nav cycle, the real CIFP procedure shapes, and the real performance profiles. If you genuinely need a controlled
fix/runway set for a parser-only test, construct it with `NavigationDatabase.ForTesting(...)` — and know that `EnsureInitialized()` will
overwrite it back to the real DB if another test in the same process runs after you (again, see the footgun on re-set).

`NavigationDb` (the property) loads `NavData.dat` + CIFP lazily, caches for the process lifetime, and uses **double-check locking** so a
concurrent test class never observes a partially-built instance (one resolved without CIFP). It returns `null` when `NavData.dat` is
absent — a test needing nav data must check for null and skip silently.

## Data resolution and offline fallbacks

`NavData.dat` and the FAA CIFP are resolved through `NavDataPathResolver` / `CifpPathResolver` with a three-tier fallback:

1. **Cache hit** — `NavDataPathResolver.CachedPath` / `CifpPathResolver.CachedPath` (the per-user vNAS/FAA cache, already current).
2. **Single download** — `EnsureCurrent` / `EnsureCurrentCycle` fetch the current AIRAC cycle once.
3. **Bundled fallback** — `TestData/NavData.dat` and `TestData/FAACIFP18.gz` (with their `*-manifest.json`), so a fresh, fully offline
   checkout still has nav/procedure data without any network.

Two environment variables force offline behavior (each accepts `1` or `true`):

| Variable | Effect |
|---|---|
| `YAAT_SKIP_NAVDATA_DOWNLOAD` | Skip the vNAS NavData download; cache → bundled `TestData/NavData.dat` only |
| `YAAT_SKIP_CIFP_DOWNLOAD` | Skip the FAA CIFP download; cache → bundled `TestData/FAACIFP18.gz` only |

Refresh the committed NavData pin with `python tools/refresh-navdata.py` (writes `TestData/NavData.dat` + `navdata-manifest.json`).

### The CIFP decompression temp-file dance

`TestVnasData.DecompressGzip` decompresses the bundled `.gz` to a **per-process** file `yaat-test-FAACIFP18-<pid>` in the temp dir, then
holds a read-only `FileStream` sentinel opened **without** `FileShare.Delete` for the process lifetime. On Windows this is what stops a
*concurrently running* test process's cleanup sweep from deleting a file that is still live (Windows refuses `File.Delete` on a handle
not opened with `FileShare.Delete`; `NavigationDatabase` opens the file independently with `FileShare.Read`, which is compatible).
`SweepStaleCifpTempFiles` reaps files leaked by killed processes across five name patterns (the current `yaat-test-FAACIFP18-*` plus four
legacy patterns from call sites that used to roll their own decompress helper). **Do not add a sixth `DecompressGzip` elsewhere** — route
all CIFP decompression through `TestVnasData` so the sentinel/sweep contract holds.

## The two ModuleInitializers

Each test assembly runs a `[ModuleInitializer]` at assembly load, before any test method or fixture. They do different jobs; pick the
right one for a new test project.

### Sim — `tests/Yaat.Sim.Tests/ModuleInit.cs`

- Sets `GroundNavigator.ThrowOnOrbit = true`. A pure-pursuit *orbit* (the navigator circling a node it can't converge on) becomes a hard
  test failure instead of the graceful recovery the shipping app does (the app leaves `ThrowOnOrbit` false to avoid crashing a live
  session). This turns a class of silent ground-following bugs into loud test failures.
- Calls `TestVnasData.SetTestDataDir(<BaseDirectory>/TestData)`, then warms CIFP and NavData once (`CifpPathResolver.EnsureCurrentCycle`
  / `NavDataPathResolver.EnsureCurrent` with the bundled fallbacks) so the first test touching `NavigationDb` doesn't pay the resolve.

### Client.UI — `tests/Yaat.Client.UI.Tests/ModuleInit.cs`

- Creates a unique temp dir `%TEMP%/yaat-ui-tests/<guid>`, writes a `.yaat-pid` marker, and sets `YAAT_APPDATA_DIR` to it. Every YAAT
  path that derives from `%LOCALAPPDATA%/yaat` routes through `YaatPaths` and so lands in this temp dir — **tests never touch the
  developer's real `preferences.json`, logs, or vNAS cache.**
- Cleanup is PID-aware: a startup sweep deletes sibling dirs whose owning process has exited; concurrent runs are safe because their PIDs
  are still live. The logger holds files open at exit, so in-process delete often leaves the dir behind for the next run's sweep to reap.

**Any test project that touches `UserPreferences`, `AppLog`, `MainViewModel`, or a per-user cache MUST install the `YAAT_APPDATA_DIR`
ModuleInitializer** (copy the Client.UI pattern) or it corrupts the developer's real app state. See [logging.md](logging.md) for the
`AppLog` side.

## The static-singleton race protocol

xUnit runs test **classes** in parallel within an assembly (the Sim project's `xunit.runner.json` sets
`parallelizeTestCollections: true`). The data-backed singletons that `TestVnasData.EnsureInitialized()` populates —
`AircraftProfileDatabase`, `AircraftSiblingMap`, `AircraftCategorization`, `WakeTurbulenceData`, `FaaAircraftDatabase`,
`NavigationDatabase` — are *process-global*. So a class that **reads** one of them can race a class that is **mid-initialization** of the
same singleton.

**Symptom:** a value mismatch where both sides should have come from the same loaded table — classically `Expected 98 / Actual 96.5`,
where `96.5` is the default-fallback a lookup returns before the profile is loaded. The test **passes in isolation** and **flakes only in
the full suite**, because in isolation nothing else is racing the init.

**Fix:** call `TestVnasData.EnsureInitialized()` in the racing class's **constructor**, not just inside the test methods. The constructor
runs before any `[Fact]` body, so the lookup is pinned to a fully-loaded state before the first read:

```csharp
public sealed class MyProfileReadingTests
{
    public MyProfileReadingTests()
    {
        TestVnasData.EnsureInitialized();   // pin singletons before any test body runs
    }
    // ... [Fact]s ...
}
```

Never assume a singleton starts empty — another test class is always one tick away from populating it. This applies to anything that
reads SRS/CWT/category data (e.g. `SoloTrainingEvaluator`'s CWT resolution falls through
`WakeTurbulenceData → FaaAircraftDatabase → AircraftCategorization`, and its SRS resolution through
`FaaAircraftDatabase → AircraftCategorization` — both subject to exactly this race).

### Why `EnsureInitialized()` re-sets the nav DB every call

The tail of `EnsureInitialized()` re-runs `NavigationDatabase.SetInstance(navDb)` on **every** invocation. Parser tests legitimately swap
in a synthetic `NavigationDatabase.ForTesting()` instance for their own controlled fix set; without the unconditional re-set, a real-data
test that ran *after* such a parser test would silently inherit the synthetic DB. The re-set restores the real DB at the top of each
class that asks for it. (For per-test scoping that does not stomp the global, `NavigationDatabase` also offers an `AsyncLocal` scoped
override — see `NavigationDatabase.Instance` / `InstanceOrNull` — but `EnsureInitialized` deliberately targets the process-wide default.)

## The `xunit.runner.json` Content-copy gotcha

`xunit.runner.json` only takes effect if it sits **next to the test DLL in `bin/`**. xUnit does not read it from the source tree. Each
test csproj must therefore have a `Content` include copying it:

```xml
<Content Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />
```

If that include is missing, xUnit silently ignores the file and falls back to its parallel-collection defaults. For
`tests/Yaat.Client.UI.Tests` — whose `xunit.runner.json` sets `parallelizeTestCollections: false` precisely so the
`UserPreferences` fixtures don't race the shared `preferences.json` — a missing copy means collections parallelize and races appear. The
explanatory comment lives in the Client.UI csproj; the Sim csproj has the same include without comment (the Sim project *wants*
collection parallelism, so its `xunit.runner.json` sets `parallelizeTestCollections: true`).

**Diagnostic order:** an `Expected [...] / Actual []` (empty-collection) `UserPreferences` failure is almost always a missing/stale
`bin/xunit.runner.json` copy, not a bug in `UserPreferences.Save`. Confirm the copy exists in the output directory before suspecting save
logic.

## Logging in tests

`SimLog` defaults to `NullLoggerFactory` — **all `Yaat.Sim` log output is silently swallowed in tests.** To see logs, wire xUnit output
via `SimLogBuilder` (`tests/Yaat.Sim.Tests/Helpers/SimLogBuilder.cs`):

```csharp
SimLogBuilder.CreateForTest(output)               // ITestOutputHelper from the ctor
    .EnableCategory("GroundNavigator", LogLevel.Debug)   // substring match, case-insensitive
    .EnableCategory("TaxiPathfinder", LogLevel.Trace)
    .InitializeSimLog();
```

`InitializeSimLog()` calls `SimLog.InitializeForTest`, which sets **only** the `AsyncLocal` scoped factory — it does *not* touch the
process-wide static. That keeps the test's `ITestOutputHelper` from leaking into the static fallback, where it would outlive the test and
NRE in unrelated parallel tests after disposal. Never call `SimLog.Initialize` from a test (that poisons the static). Run with output
captured:

```bash
timeout 30 dotnet test --logger "console;verbosity=detailed" 2>&1 | tee .tmp/test.log
```

See [logging.md](logging.md) for the full `SimLog`/`AppLog` model and the `DeferredLogger` resolution order.

## Ground-layout and airport fixtures

`TestAirportGroundData` (`tests/Yaat.Sim.Tests/Helpers/TestAirportGroundData.cs`) is the `IAirportGroundData` backed by
`TestData/<id>.geojson`. It accepts ICAO (`KOAK`) or short-code (`OAK`) ids, and caches the parse+fillet result per `(FilletMode,
airportId)` per process (~500 ms OAK, ~2700 ms SFO) since layouts are immutable after construction. The parameterless constructor uses
`FilletMode.Standard` (the shipping fillet generator), so the ~150 existing call sites build the production graph; pass `FilletMode.None`
explicitly for raw, unfilleted-graph tests. `GetLayout` returns `null` for an unknown airport — callers skip silently.

To add a new airport: fetch its GeoJSON from the vNAS map API (`https://data-api.vnas.vatsim.net/api/training/airports/{FAA}/map`) and
drop it in `TestData/<lowercase-short-id>.geojson`. See `AirportLayoutDownloader` and `docs/ground/README.md` for the ground stack.

`TestArtccConfig.LoadZoa()` (`Helpers/TestArtccConfig.cs`) loads the committed ZOA ARTCC snapshot
(`TestData/artcc-zoa-snapshot.json`, refreshed via `tools/refresh-artcc-snapshot.py`) for tests exercising replay-time TCP/ERAM
resolution. It returns `null` when the snapshot is absent — same silent-skip convention.

**Raw `GeoJsonParser.Parse` needs the nav DB only for runway crossings.** `GeoJsonParser.Parse(airportId, geoJson, runwayAirportCode)`
runs `RunwayCrossingDetector.DetectRunwayCrossings`, which reads `NavigationDatabase.Instance` — so a non-null `runwayAirportCode` (e.g.
`"OAK"`) throws `NavigationDatabase not initialized` in a test that hasn't loaded nav data. A test that only needs the graph or fillet
arcs (taxiway topology, `TurnAngleDeg`) should pass `runwayAirportCode: null` to skip the runway-crossing pass while still producing
nodes/edges/arcs (`OneWayResolverTests`, `GroundArcRenderFilterTests`, `DtoConverterGroundLayoutTests` all do). Use the harness's
pre-initialized layout only when you actually need runway crossings.

## Debugging aircraft movement — `TickRecorder`

`TickRecorder` (`Helpers/TickRecorder.cs`) captures per-tick aircraft state to a JSON document (with embedded type/wingspan/length/color
metadata) for visualization. Attach it to one or more aircraft, `Record(t)` each simulated second, then `WriteJson(".tmp/run.json")`:

```csharp
using var _ = TickRecorder.Attach(engine, ".tmp/scenario.json", "N152SP", "N569SX");
```

Feed the output to the LayoutInspector `--ticks` overlay or to [tick-animator.md](tick-animator.md) to watch landings, exits, and taxi
paths play back. `NearestNodeHelper` adds diagnostic logging for graph-snapping investigations.

## Pathfinder and taxi-coverage infrastructure

The ground/taxi suites assert against three pieces of specialized infrastructure. Read this before touching a taxi-coverage test or you
won't know what the assertions mean.

### `OracleAutoRouter` — the completeness oracle

`Helpers/OracleAutoRouter.cs` is a **verbatim copy of production `AutoRouter`'s A\* loop with exactly one change**: production keys its
best-g-score (closed) set by node id alone, while the oracle keys by `(nodeId, arrival-bearing-bucket)`. Because onward-edge admissibility
(`GeometricAdmissibility.IsAdmissible`) depends on arrival bearing, bucketing by bearing explores a *superset* of production's states —
so the oracle is **strictly more complete**: it can only ever find an equal-or-better route, or reach a destination production declares
unreachable. **Any diff between oracle and production is therefore exactly a case where production's node-id-only pruning loses.** Setting
`bearingBucketDeg <= 0` disables dedup entirely (pure exhaustive search), the gold ground truth used to spot-check the bucketed oracle.
**Keep the oracle in lock-step with `AutoRouter.cs`** or it stops being a valid ground truth.

### `TaxiBudgetDeriver` / `TaxiBudgetEvaluator` — spin detection without flaking

`TaxiBudgetDeriver` (`Helpers/TaxiBudgetDeriver.cs`) inspects the optimal A\* route (`TaxiPathfinder.FindRoute`) for an
origin→destination pair and derives two budgets:

- **Time budget** — `optimalTimeSec × TimeFudgeMultiplier (1.5) + cornerCount × SecondsPerCorner (4 s) + StartupOverheadSec (15 s)`. The
  `optimalTimeSec` term is **arc-aware**: each segment is timed at `min(nominalTaxiKts, segment.MaxSafeSpeedKts)`, so a jet crawling a
  tight fillet at a GA-sized ramp is capped at the arc's safe speed (potentially 5–10 kts), not the nominal 30. A flat
  `distance / nominalKts` budget under-allows such routes by 2–3×.
- **Turn budget** — `optimalTurnDeg + segmentCount × PerSegmentTurnOverheadDeg (30°) + TurnSlackDeg (60°)`. `optimalTurnDeg` sums both the
  intra-segment sweep (arc edges rotate heading along their length — a single arc can turn 90° with no segment join) **and** the join
  turn at each segment boundary. The per-segment overhead is load-bearing: pure-pursuit micro-correction adds ~15–25°/segment regardless
  of geometry, so a 110-segment route picks up ~2000° of harmless "noise" turn. A multiplier-only turn budget underflows long clean
  routes by 6–8×.

`TaxiBudgetEvaluator` (`Helpers/TaxiBudgetEvaluator.cs`) is the per-tick observer: fed one `AircraftState` per simulated second, it
accumulates `CumulativePathFt`, `CumulativeAbsTurnDeg`, and `MaxConsecutiveZeroProgressSec` (ground speed below `1.0` kt while **not** in a
legitimate stop — `HoldingShortPhase`, `CrossingRunwayPhase`, `AtParkingPhase`). The budgets are deliberately generous: a real spin or
stall blows past them by 5–10×, so they catch the bug class without flaking on legitimately slow corners. **If a regression slips through,
tighten the multipliers — do NOT replace them with hand-tuned per-pair numbers.** The arc-aware time floor and per-segment turn overhead
are the load-bearing parts; flatten them and you re-introduce the false failures they exist to prevent.

### `FilletComparisonGates` — structural and reachability gates

`Helpers/FilletComparisonGates.cs` evaluates a filleted layout against its pre-fillet input: `ValidateStructural` (no missing-node edges,
no degenerate/coincident intersections, positive arc radii/distances, no orphan nodes), `RepairCountersZero` (no orphan rescues, no
duplicate-arc/parallel-bypass removals — i.e. the generator produced clean output without repair passes), and reachability gates
(`ReachableStableIdsFromHoldShorts`, `SurvivingStableIds`, `ParkingReachableToHoldShort`). It also indexes corner-arc min-radii by
`(junction, taxiway-pair, bearing-bucket)` so two fillet generators can be compared corner-for-corner. See
[ground/fillet-generator.md](ground/fillet-generator.md) for the generator these gates protect.

### The heavy categories

`[Trait("Category", "Nightly")]` (per-spot taxi-coverage grid sweeps, e.g. `TaxiCoverageOakGridTests` / `TaxiCoverageSfoGridTests`) and
`[Trait("Category", "PathfinderGrid")]` (the state-aware-pruning necessity oracle sweep) are excluded from the default `test-all.ps1` run
for speed. The default filter is `Category!=Nightly&Category!=PathfinderGrid` (untagged tests still run — a trait-inequality filter only
drops explicitly tagged tests). Pass `pwsh tools/test-all.ps1 -Full` to include them (CI/nightly do).

## Other fixtures

- `TestDispatch.Context(...)` (`Helpers/TestDispatch.cs`) — a concise factory for `DispatchContext`. **Production constructs
  `DispatchContext` with no optional params** so the compiler enforces wiring when a new context field lands; the test factory uses
  optional params so tests that don't care about ground layout/weather/lookup stay terse. See
  [command-pipeline.md](command-pipeline.md) for what `DispatchContext` carries.
- `RecordingLoader` (`Helpers/RecordingLoader.cs`) — loads v4 recording archives and legacy bug-report bundles transparently
  (`Load` → base recording with no snapshots; `OpenArchive` → on-demand snapshot reader). The replay surface itself (ReplayFromStartTo /
  FastForwardTo / hybrid replay, WAIT presets, bug-bundle triage) is owned by [e2e-tdd-issue-debugging.md](e2e-tdd-issue-debugging.md) and
  [snapshots-and-replay.md](snapshots-and-replay.md) — go there for the replay flow, not here.

## The 30-second timeout discipline

Always run suites under a wall clock: `timeout 30 dotnet test ...`. A YAAT sim suite that hasn't finished in 30 s is almost always
**stuck**, not merely slow — a broken graph topology or an infinite pathfinder loop, not a heavy computation (the heavy grid sweeps are
gated behind the `Nightly`/`PathfinderGrid` traits and excluded by default). Treat a timeout as a hung-test failure to diagnose, not a
budget to raise.

## Writing your first sim test

A minimal end-to-end skeleton that ties the conventions together — constructor `EnsureInitialized`, real ground layout, silent skip on
missing data, run under 30 s:

```csharp
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Testing;          // TestVnasData lives in the Yaat.Sim assembly
// using Yaat.Sim.Tests.Helpers;  // already a global using in this project

namespace Yaat.Sim.Tests;

public sealed class MyTaxiExitTests
{
    public MyTaxiExitTests()
    {
        // Pin the data-backed singletons BEFORE any [Fact] body — required for any test
        // that reads profiles/categories/nav data, or it can race a parallel class.
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Aircraft_Exits_28L_Toward_Parking()
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;   // silent skip: no GeoJSON on a fresh/offline checkout — keep CI green
        }

        // Optional: surface Yaat.Sim logs for this test only (NOT SimLog.Initialize).
        // SimLogBuilder.CreateForTest(output).EnableCategory("GroundNavigator", LogLevel.Debug).InitializeSimLog();

        // ... build the engine, spawn an aircraft, tick, assert against the real layout ...
        // ... attach a TickRecorder if you want a LayoutInspector/TickAnimator overlay ...
    }
}
```

Run it: `timeout 30 dotnet test --filter "FullyQualifiedName~MyTaxiExitTests" 2>&1 | tee .tmp/test.log`. Before declaring a change done,
verify the cross-repo build with `pwsh tools/test-all.ps1`.

## Footguns and pitfalls

- **Static-singleton race.** A class reading a `TestVnasData`-populated singleton (`AircraftProfileDatabase`, `AircraftSiblingMap`,
  `NavigationDatabase`, …) can race a class mid-init. Symptom: `Expected 98 / Actual 96.5` (default-fallback), passes alone, flakes in the
  suite. **Fix: `TestVnasData.EnsureInitialized()` in the class *constructor*.**
- **`xunit.runner.json` must be Content-copied to `bin/` in each csproj** or xUnit ignores it. The Client.UI project relies on
  `parallelizeTestCollections: false` to stop `UserPreferences` racing the shared `preferences.json`; the Sim project sets it `true`
  (collection parallelism *on*), which is exactly why the singleton race exists for Sim tests. Diagnose `Expected [...] / Actual []`
  failures here first.
- **Two ModuleInitializers, different jobs.** Sim's warms CIFP/NavData and sets `GroundNavigator.ThrowOnOrbit = true` (orbits become hard
  failures, unlike the shipping app). Client.UI's redirects `YAAT_APPDATA_DIR` to a PID-marked temp dir. **Any project touching
  `UserPreferences`/`AppLog`/`MainViewModel`/per-user cache MUST have the `YAAT_APPDATA_DIR` initializer** or it corrupts the developer's
  real state.
- **`EnsureInitialized()` re-sets the nav DB on *every* call**, not just the first, because parser tests swap in a synthetic
  `NavigationDatabase.ForTesting()` and a later real-data test would otherwise inherit it.
- **`SimLog` swallows everything by default** (`NullLoggerFactory`). Use `SimLogBuilder.CreateForTest(output).EnableCategory(...)
  .InitializeSimLog()` (which uses `InitializeForTest`, scoped — not `Initialize`, which poisons the process-wide static for parallel
  tests) and run with `--logger "console;verbosity=detailed"`.
- **Silent-skip on missing data is mandatory.** When NavData/CIFP/GeoJSON/recording/ARTCC-snapshot is absent (fresh checkout, offline),
  return early — **no `Assert.Skip`, no exception** — so CI stays green. `TestVnasData.NavigationDb`, `TestAirportGroundData.GetLayout`,
  and `TestArtccConfig.LoadZoa` all return `null` for exactly this.
- **CIFP decompression uses a per-process sentinel-file dance.** A `yaat-test-FAACIFP18-<pid>` file held open without `FileShare.Delete`
  blocks a concurrent process's sweep from deleting a live file on Windows; `SweepStaleCifpTempFiles` reaps leaks across five legacy name
  patterns. **Don't add another `DecompressGzip` helper — route through `TestVnasData`.**
- **Run under a 30 s wall clock** (`timeout 30 dotnet test`). A suite that hasn't finished is usually stuck on broken graph topology or an
  infinite pathfinder loop, not slow.
- **Use `pwsh tools/test-all.ps1`, not bare `dotnet test`, for full-suite confidence.** Bare `dotnet test` from yaat never builds the
  sibling yaat-server, so a `Yaat.Sim` signature change that breaks the server passes locally and fails in CI. The heavy
  `Nightly`/`PathfinderGrid` categories are excluded by default; pass `-Full` for the complete set.
- **`TaxiBudgetDeriver`/`TaxiBudgetEvaluator` budgets are deliberately generous** (real spins exceed them 5–10×). If a regression slips
  through, tighten the multipliers — do NOT hand-tune per-pair numbers. The arc-aware per-segment time floor and per-segment turn overhead
  are load-bearing; flat formulas under-allow tight-fillet and long clean routes by 2–8×.
- **`OracleAutoRouter` is a verbatim copy of `AutoRouter`'s A\* with one change** (closed-set keyed by `(node, bearing-bucket)`), making it
  strictly more complete than production. Any oracle-vs-production diff is exactly where production's node-id-only pruning loses. Keep it
  in lock-step with `AutoRouter.cs`.
- **Making `internal` members `public` for tests is fine.** No reflection, no `InternalsVisibleTo` hacks.
- **A recording replay silently runs with `GroundLayout == null` if the destination airport's geojson is missing from `TestData/`.**
  `TestAirportGroundData.GetLayout` returns null for a missing file (no download fallback), and the replay runs anyway — a landed aircraft
  falls into `RunwayExitPhase`'s layout-less analog-rollout fallback (`StartExitNavigation` bails on null `ctx.GroundLayout`, `TickRolling`
  steers along runway heading), which can drift off the field and trip the pure-pursuit **orbit** guard (`ThrowOnOrbit=true` →
  `InvalidOperationException`) — often on an *incidental* ground aircraft, not the one the test asserts on, so a bisect falsely pins the orbit
  to an unrelated commit. Diagnose by printing `engine.World.GroundLayout?.AirportId` (null); fix by committing the map: `curl -sS -o
  tests/Yaat.Sim.Tests/TestData/<faa>.geojson https://data-api.vnas.vatsim.net/api/training/airports/<FAA>/map`.
- **A `TestData/*.geojson` refresh can flip a borderline taxi route via coordinate-precision truncation.** A vNAS re-download can
  re-serialize coordinates at truncated precision (geometry identical to sub-meter, but every feature differs textually and fillet/spot nodes
  renumber + shift). A sub-meter shift can flip a marginal route so `SegmentExpander` rejects it ("Cannot reach destination from end of taxi
  path"), the replayed command is rejected, and the aircraft "never taxis" (stuck in `HoldingAfterPushbackPhase`, which only exits on an
  explicit `TAXI`). Because `ReplayCommand` swallows the rejection, the symptom gets misattributed to whatever commit landed at the same
  rebase. Confirm with a **layout A/B** (swap `git show <refresh-commit>^:tests/.../sfo.geojson` back in and re-run). Pin the fixture to a
  committed full-precision snapshot via `Helpers/PinnedSfoGroundData.cs`, and build gate-coverage routes from taxiway **names**
  (`FindIntersectionNode`, `TaxiPathfinder.FindRoute`), not hardcoded node IDs. `SimulationEngine.ReplayCommand` logs rejected replayed
  commands at Debug (category `SimulationEngine`).
