# Test Harness, Fixtures & Singleton-Race Protocol

> Read this before writing any Yaat.Sim test, before adding a new test project, or when a test "passes alone but flakes in the
> suite." The harness is its own subsystem: ~420 Sim test files share a dozen reusable `Helpers/`, three test projects, real (never
> synthetic) vNAS/FAA data behind static singletons, and a set of correctness conventions that live nowhere in code comments. Getting
> them wrong does not fail the build — it produces intermittent, hard-to-trace flakes. This is the single reference for those conventions.
>
> Not sure whether a behaviour is already pinned, or which folder and shape a new test takes? That is [`test-map.md`](./test-map.md), the almanac of test classes by subsystem; this doc is the harness underneath them.

## Scope and the iron rules

The test projects:

| Project | Tests | Notable harness wiring |
|---|---|---|
| `tests/Yaat.Sim.Tests` | Simulation, commands, physics, phases, navdata, parsers, ground/pathfinder | `ModuleInit` loads the aircraft tables, warms CIFP/NavData, registers the lazy `NavigationDatabase` test default loader + `GroundNavigator.ThrowOnOrbit`; `xunit.runner.json` parallelizes collections |
| `tests/Yaat.Client.UI.Tests` | Avalonia headless UI / `MainViewModel` / `UserPreferences` | `ModuleInit` redirects `YAAT_APPDATA_DIR` and registers the lazy `NavigationDatabase` test default loader; `xunit.runner.json` *disables* collection parallelism |
| `tests/Yaat.LayoutInspector.Tests` | LayoutInspector CLI parsing and tick-recording merge | None — plain unit tests over the tool project |
| yaat-server tests (`../yaat-server`) | Server rooms, hub, broadcast | Reached only via `tools/test-all.ps1` — bare `dotnet test` from yaat never builds them |

The non-negotiable rules, each detailed below:

1. **Real data, never synthetic.** Initialize with `TestVnasData.EnsureInitialized()`; never hand-roll stub fixes/profiles.
2. **Call `EnsureInitialized()` in the test class *constructor*** if any test reads a data-backed static singleton (race protection).
3. **Silently skip on missing data** — return early, no `Assert.Skip`, no throw — so a fresh/offline checkout keeps CI green. **Exception: NavData, CIFP and the committed aircraft tables are hard preconditions.** `ModuleInit` throws if it cannot resolve `NavData.dat` or the CIFP, because most of this suite asserts against real navdata and procedures, and a run without them reports green having proved nothing.

   `LoadAircraftSpecs`, `LoadAircraftCwt` and `LoadFaaAcd` likewise throw when `AircraftSpecs.json`, `AircraftCwt.json` or `FaaAcd.json` is missing from the test data folder or holds no entries (a broken checkout or a wrong `SetTestDataDir`), so no test runs on a half-loaded table. Ground layouts, recordings, ARTCC configs and the rest still skip silently.
4. **Run suites under a 120 s ceiling** (`pwsh tools/gate.ps1 -Log .tmp/test.log -TimeoutSeconds 120 -Slot heavy -- dotnet test`) so soft hangs surface as failures.
5. **Runner options go after `--`** (Microsoft.Testing.Platform): `dotnet test -- --filter-method "*Name*"`; see [Running tests](#running-tests).
6. **For full-suite confidence run `pwsh tools/test-all.ps1`**, not bare `dotnet test` — only that builds the sibling yaat-server.

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
absent — but in this assembly that cannot happen: `ModuleInit` has already thrown. The null checks in existing tests are harmless and
still guard the CIFP-absent case; new tests need not add one for NavData.

## Data resolution and offline fallbacks

`NavData.dat` and the FAA CIFP are resolved through `NavDataPathResolver` / `CifpPathResolver` with a three-tier fallback:

1. **Cache hit** — `NavDataPathResolver.CachedPath` / `CifpPathResolver.CachedPath` (the per-user vNAS/FAA cache, already current).
2. **Single download** — `EnsureCurrent` / `EnsureCurrentCycle` fetch the current AIRAC cycle once.
3. **Bundled fallback** — `TestData/NavData.dat` and `TestData/FAACIFP18.gz` (with their `*-manifest.json`), so a fresh, fully offline
   checkout still has nav/procedure data without any network.

**NavData is required, and normally resolves without touching the network.** `ModuleInit` resolves
with `AllowDownload: false` when a local copy exists — `%LOCALAPPDATA%/yaat/cache/NavData.dat` or the
**committed** `TestData/NavData.dat` — and only permits a download when neither is present. If the
resolve still yields nothing, it **throws** and the assembly fails to load: see iron rule 3.

Why the offline default: resolving with downloads enabled fetches the vNAS *config* over HTTPS
first, costing ~240 ms of TLS handshake on **every** test process (~10% of the fixed cost of a
filtered run), and makes the suite depend on VATSIM being reachable.
`NavDataPathResolverTests.TestProcess_ResolvesNavData_WithoutContactingVnas` asserts
`NavDataPathResolver.ConfigFetchCount` stays zero, so this cannot silently regress.

The trade-off: tests no longer warn when the bundled NavData serial is behind what vNAS publishes.
`python tools/refresh-navdata.py` is the signal for that — run it to move the pin (writes
`TestData/NavData.dat` + `navdata-manifest.json`).

The committed airport ground layouts (`TestData/<ID>.geojson`) are pinned the same way. `python tools/refresh-test-layouts.py` re-fetches each one from the vNAS training-airport map API (the endpoint `AirportLayoutDownloader` uses) and rewrites it with LF line endings.

Files whose stem is not a plain 3–4 letter airport ID (`issue172-sfo`, `sfo-b1short`) are deliberate snapshots and are skipped, and an airport the API has no map for is kept as committed. `--check` reports what would change and exits 1 if anything would.

`TestData/TowerCabMaps/<id>.geojson` holds an airport's tower-cab videomap (`oak.geojson`, read by `TowerCabVideoMapTests`). It lives in a subfolder because several tests read every top-level `TestData/*.geojson` as a ground layout, which a videomap is not. The same script refreshes it (the ARTCC for each fixture is in its `TOWER_CAB_ARTCC`); a response that is not JSON or not a FeatureCollection prints why to stderr and keeps the fixture. `TestAirportGroundData` parses any airport that has a committed map here (OAK) on it, as the server parses it on the fetched map, so the hold-short bars sit on the painted lines (a malformed map throws rather than falling back to the unsnapped graph). `PinnedSfoGroundData` builds with `VideoMap: null`. A test that needs the raw unsnapped positions parses with a null map itself (`RunwayPaintSnapTests.ParseOak(map: null, FilletMode.Standard)`).

**CIFP is required too, and downloads stay enabled on purpose.** `ModuleInit` throws if it cannot
resolve one. Unlike NavData it needs no offline-first switch: `CifpPathResolver` already returns a
current-cycle cache hit *before* considering the network, which is why it costs ~9 ms rather than
~240 ms. And CIFP is AIRAC-cycle-specific — the committed `TestData/FAACIFP18.gz` goes stale every
28 days — so fetching the current cycle when it is not cached is worth a one-off round trip.

| Variable | Effect |
|---|---|
| `YAAT_SKIP_CIFP_DOWNLOAD` | Skip the FAA CIFP download; current-cycle cache → stale cache → bundled `TestData/FAACIFP18.gz`. If none of those exist, `ModuleInit` throws rather than running without procedures. |
| `YAAT_SKIP_NAVDATA_DOWNLOAD` | Still honoured for the no-local-copy case, but normally inert: NavData resolves offline whenever a local copy exists |

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
- Calls `TestVnasData.SetTestDataDir(<BaseDirectory>/TestData)` and loads the aircraft tables (`TestVnasData.EnsureAircraftDataInitialized`), so no test reads a category, CWT or profile from an empty table. Then it warms CIFP and NavData once (`CifpPathResolver.EnsureCurrentCycle` / `NavDataPathResolver.EnsureCurrent` with the bundled fallbacks), so the first test touching `NavigationDb` doesn't pay the resolve.
- Warms `AirspaceDatabase.Default` and `MilitaryRouteDatabase.Default` on a background task, as the server does at startup —
  otherwise the first test to tick physics pays the airspace GeoJSON parse inside its own timing.

### Client.UI — `tests/Yaat.Client.UI.Tests/ModuleInit.cs`

- Creates a unique temp dir `%TEMP%/yaat-ui-tests/<guid>`, writes a `.yaat-pid` marker, and sets `YAAT_APPDATA_DIR` to it. Every YAAT
  path that derives from `%LOCALAPPDATA%/yaat` routes through `YaatPaths` and so lands in this temp dir — **tests never touch the
  developer's real `preferences.json`, logs, or vNAS cache.**
- Cleanup is PID-aware: a startup sweep deletes sibling dirs whose owning process has exited, and dirs with no readable marker only once nothing has written them for an hour (`MarkerlessGraceAge`).

  The file is linked into four test assemblies that `dotnet test` starts concurrently, and each creates its dir a moment before writing the marker, so a younger markerless dir may belong to a live sibling. The logger holds files open at exit, so in-process delete often leaves the dir behind for the next run's sweep to reap.

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

**Collection order:** the Sim project's collections start in a fixed order, heaviest first by the weights in `tests/Yaat.Sim.Tests/collection-weights.json`, ties and collections missing from the table by key (`CollectionWeightOrderer`; [test-suite-speed.md](test-suite-speed.md)). An order-dependent test therefore fails the same way every run rather than at random; the start order is fixed, but classes still run in parallel, so a race between them is still timing-dependent.

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

A Yaat.Sim.Tests class that never calls `EnsureInitialized()` still reads the real database: `ModuleInit` registers `NavigationDatabase.SetTestDefaultLoader` over `TestVnasData.LoadRealDataWithoutInstalling()`, so the first read that finds no default installed loads it and installs it as the default.

`Yaat.Client.UI.Tests` registers the same loader. `Yaat.Client.Tests` registers none: it links no committed NavData/CIFP and resolves them online, so a loader there would only re-word a failure, and a class there that reads the database calls `EnsureInitialized()` in its constructor.

To find every test that depends on the order the database was installed in, add an untracked assembly-level `BeforeAfterTestAttribute` to the project whose `After` empties `NavigationDatabase`'s default (by reflection on `_default._installed`). Every order-dependent reader then fails with certainty in one suite run (it found the last such class in 1m22s). Delete the probe afterwards.

### Per-test navigation database (yaat-server tests)

yaat-server's test collections run in parallel, so no test sets the process-wide navigation database. An assembly-level xunit hook (`Harness/NavigationDatabaseTestHolderAttribute.cs`) calls `NavigationDatabase.OpenTestHolder()` before every test and closes it after; its static constructor sets an empty `ForTesting()` default when none exists, so a test that never builds a harness still finds one.

`RoomEngineTestHarness` installs its database with `NavigationDatabase.SetTestInstance(db)`. Lookup precedence is `ScopedOverride` (innermost scope) → the per-test holder → the process-wide default. `TestVnasData.LoadRealDataWithoutInstalling()` loads the real navdata and the aircraft tables without touching the default, which is what `RoomEngineTestHarness.ResolveRealNavDb()` uses.

**Never build a harness in a constructor, field initialiser, class fixture or `IAsyncLifetime`.** xunit constructs the class before the hook runs, so `SetTestInstance` throws there ("No per-test NavigationDatabase holder is open"). Build it in the test body or in a helper the body calls, async helpers included.

Two collections serialize the classes that cannot share a process with the rest. `LoadSensitive` (`Harness/LoadSensitiveCollection.cs`, `DisableParallelization = true`) holds the timing classes that wait on real concurrent work with a fixed timeout, plus the SWIM correlator soak; xunit runs it alone after the parallel collections, so the suite's own load cannot push their waits past the timeout.

`ProcessLogging` (`Harness/ProcessLoggingCollection.cs`) holds the classes that redirect stderr, write through the log writer, or clean log files beside the test binary (`RollingLogWriterTests`, `FileLoggingSetupTests`, `FileLoggerRoomScopeTests`, `DataRetentionHostedServiceTests`); its classes run one at a time, in parallel with the other collections. A new class with either need joins the matching collection.

## Running tests

Every test project uses xunit.v3 on the Microsoft.Testing.Platform runner (`UseMicrosoftTestingPlatformRunner` in each csproj;
`global.json` in both repos tells `dotnet test` to use it). Consequences:

- `dotnet test yaat.slnx` runs the test assemblies **concurrently** (Sim, Client, Client.UI in parallel; yaat-server's suite the same way).
- Runner options follow a `--` separator. The VSTest options are gone — `--filter "..."` and `--logger "..."` fail with `Unknown option`.
- **One project per invocation.** `dotnet test tests/A tests/B` does not run both: it reports `Zero tests ran` and exits 0, which
  reads as a pass. Run the projects separately (or the solution) and check each summary's `total:` is non-zero.

| Want | Command |
|------|---------|
| One test / substring of `Namespace.Class.Method` | `dotnet test --project tests/Yaat.Sim.Tests -- --filter-method "*MyTaxiExitTests*"` |
| One class | `dotnet test --project tests/Yaat.Sim.Tests -- --filter-class "*.MyTaxiExitTests"` |
| Several patterns | `-- --filter-method "*Verbalizer*" "*PhraseologyMapperTrace*"` (repeat values after one switch) |
| Exclude / include a trait | `-- --filter-not-trait "Category=Nightly"` / `-- --filter-trait "Category=Nightly"` |
| TRX report | `-- --report-xunit-trx --report-xunit-trx-filename results.trx` (lands in `bin/<cfg>/net10.0/TestResults/`) |
| See `ITestOutputHelper` / `SimLogBuilder` output | run the project as a process: `dotnet run --project tests/Yaat.Sim.Tests -c Release -- --filter-method "*Name*" --show-live-output on` |

`--filter-class` accepts `*` only at the start or end of a pattern, never in the middle.

`dotnet test` prints only per-assembly summaries and failures; captured test output never reaches its console, which is why the live-output
row uses `dotnet run --project` (or the built `Yaat.Sim.Tests.exe` directly). `xunit.runner.json` is still honoured by v3 for
parallelism settings; `showLiveOutput` there is the config-file equivalent of `--show-live-output on`.

### Throwaway probes

A probe compiles into a test project without touching tracked files via `-p:CustomAfterMicrosoftCommonTargets=<file>`: the MSBuild file adds the probe source to `Compile`, so nothing in the project or its sources is edited. Delete the file when done.

The same targets file can swap a source file for a variant with `<Compile Remove>` / `<Compile Include>`, building the variant without touching tracked files. A probe run goes through the gate with `--local --reason "<why>"` (the gate refuses `--local` without a reason).

A routed gate snapshot omits the gitignored `.tmp`, so a probe compiled from `.tmp` needs `--local --reason`. Afterwards rebuild the test project so the probe types leave `bin/obj`.

Two `--local` builds, one of a yaat test project and one of a yaat-server test project, cannot run at once: both write `Yaat.Sim.dll` and collide with CS2012. Run them in sequence.

### Linux runs: CI investigation and cross-platform verification

CI runs on `ubuntu-latest` (`.github/workflows/ci.yml`). A test can pass on Windows and fail there: floating-point results differ in the last bits between the platforms' runtimes, and a value that is zero on paper can land on either side of it (see the footgun below). Two ways to run the suite on Linux from this machine, both against the Windows Release build (the test assemblies are portable, so any .NET 10 runtime runs them):

- **WSL** (Ubuntu, `dotnet-sdk-10.0` from Ubuntu's archive; install with `wsl -u root apt-get install -y dotnet-sdk-10.0`). The quick one: no image, starts in seconds. It reads the build over `/mnt/x`, which is fine for a filtered run.

  `wsl -e bash -c "rm -rf /tmp/yaat-ci && mkdir -p /tmp/yaat-ci && cd /tmp && YAAT_APPDATA_DIR=/tmp/yaat-ci dotnet /mnt/x/<repo path>/tests/Yaat.Sim.Tests/bin/Release/net10.0/Yaat.Sim.Tests.dll --results-directory /tmp/yaat-ci/r --filter-class '*Name*'"`
- **Docker** (`mcr.microsoft.com/dotnet/sdk:10.0`, the image family CI's runtime comes from). The closer match to CI's runtime patch level; use it to confirm a WSL result, or when WSL and CI disagree.

  `docker run --rm -e YAAT_APPDATA_DIR=/tmp/appdata -v <bin dir>:/app:ro mcr.microsoft.com/dotnet/sdk:10.0 dotnet /app/Yaat.Sim.Tests.dll --results-directory /tmp/r --filter-method "*Name*"`

Both set `YAAT_APPDATA_DIR` to a fresh directory, so NavData resolves to the committed `TestData/NavData.dat` as on CI rather than to a newer user cache (CIFP downloads, as on CI). Use either for a Linux-only CI red, and to check a change to numeric code with a zero or tie boundary (a stop distance, a sign test, a sort key that can tie) before it lands.

Loop a Linux-only failure alone as well as inside its class: a race that is lost only on a cold code path passes once its classmates have warmed it (YAAT-662's pipe-dispose test was red 44 of 48 alone and 0 of 50 in its class).

Give each concurrent run its own temp and app-data directories (`TMPDIR`, `XDG_DATA_HOME`, `XDG_CONFIG_HOME`, `DOTNET_CLI_HOME`): runs sharing one crashed at startup with `Internal CLR error`. Run the Windows-built test DLL directly, as above; `dotnet test` from WSL against a Windows checkout fails MSBuild evaluation.

## The `xunit.runner.json` Content-copy gotcha

`xunit.runner.json` only takes effect if it sits **next to the test DLL in `bin/`**. xUnit does not read it from the source tree. Each
test csproj must therefore have a `Content` include copying it:

```xml
<Content Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />
```

If that include is missing, xUnit silently ignores the file and falls back to its parallel-collection defaults. For
`tests/Yaat.Client.UI.Tests` — whose `xunit.runner.json` sets `parallelizeTestCollections: false` precisely so the
per-test `UserPreferences.FilePath` switch (`[PreferencesFilePerTest]`) isn't raced by a parallel test — a missing copy means collections parallelize and races appear. The
explanatory comment lives in the Client.UI csproj; the Sim csproj has the same include without comment (the Sim project *wants*
collection parallelism, so its `xunit.runner.json` sets `parallelizeTestCollections: true`).

**Diagnostic order:** an `Expected [...] / Actual []` (empty-collection) `UserPreferences` failure is almost always a missing/stale
`bin/xunit.runner.json` copy, not a bug in `UserPreferences.Save`. Confirm the copy exists in the output directory before suspecting save
logic.

## Avalonia headless UI tests

`Yaat.Client.UI.Tests` drives real windows under `Avalonia.Headless`. Three properties of that platform turn missing test hygiene into
order- and load-dependent flakes:

- **Renders happen only on render-timer ticks.** `Dispatcher.UIThread.RunJobs()` and `window.UpdateLayout()` pump layout but never force
  a render, and anything computed in `MapCanvasBase.Render` → `CreateRenderSnapshot` (free-form datablock deconfliction advances one
  damped-spring step per frame, `ResolvedDataBlockOffset`, …) only moves when a frame actually renders. A test that reads render-derived
  state must drive frames explicitly — `canvas.InvalidateVisual(); Dispatcher.UIThread.RunJobs();
  AvaloniaHeadlessPlatform.ForceRenderTimerTick();` in a loop until the value is stable for two consecutive frames (`SettleDeconfliction`
  in `Views/DataBlockStatePersistenceTests.cs`). Without that, the frame count before the assertion depends on wall-clock timer ticks —
  machine load and what ran earlier in the process — even though the solver itself is deterministic per frame.
- **Hit testing reads the compositor's last processed frame, one tick behind a posted rebuild.** Only one render batch is in flight at a time, so a change whose rebuild is posted at default priority (below the render op) can need two ticks before `InputHitTest` sees it: after one forced tick the new controls are laid out but a pointer press still lands on the old scene.

  Before a press or drag at a layout-derived point, render until the point hits the intended target (`RenderUntilHit` in `Automation/AutomationPointerTests.cs`), with a frame count as the hang guard. Building the test project with `AvaloniaHeadlessPlatformOptions.Fps = 1` takes the real render timer out of the race and turns this class of flake red every time.
- **Every test gets its own `preferences.json`, bound per `UserPreferences` instance.** `ModuleInit` sets `YAAT_APPDATA_DIR` once per *process*; the assembly-level `[PreferencesFilePerTest]` (`Helpers/PreferencesFilePerTestAttribute.cs`) then calls `UserPreferences.UseFile` before each test with a fresh, empty file at `<per-process root>/tests/<guid>/preferences.json`.

  An instance reads and writes only the file it was created on, so the `MainViewModel` constructor's restore of persisted UI state (vStrips split, pop-out flags, zoom) starts from defaults and a test never restores preferences in `finally`. `Views/PreferencesIsolationTests.cs` (ordered by `Helpers/MethodNameOrderer.cs`) pins it. `tests/Yaat.Client.Tests` has no hook and uses the default path.
  - **Why a file per test and not a restore scope:** `WindowGeometryHelper` persists geometry through a *debounced* `DispatcherTimer` auto-save, and a hidden window (the main window is hidden, never closed) fires it during the *next* Avalonia test's pumping, so a scope that restores the file after a test cannot contain it. Binding each instance to its file does.
  - **Create `UserPreferences` in the test body.** xUnit v3 builds the test class before `Before` runs, so a constructor or field initializer that creates `UserPreferences` binds to the previous test's file.
  - "Passes alone, flakes in the suite" is still possible from timing: tab-content materialization can lag extra dispatcher/layout passes under load, which is why `WaitForDockedGroundCanvas` is a bounded pump loop with a state-dump `Assert.Fail` rather than a single pass. Helpers that construct a `MainViewModel` still normalize non-preference ambient state before asserting a baseline (`VStripsSplitHostTests.NewSplitHost`).
- **A pointer parked over a control re-enters it on the next render frame.** Each frame re-hit-tests the last pointer position, so a test that hovers a chip, closes and reopens its menu, then asserts the hover state is gone sees `PointerEntered` fire again whenever a frame lands before the assertion. Move the pointer to empty window (`top.MouseMove` near the bottom-right corner) before the reopen, then force the frames (`SpeedFlyoutTests.Reopen_ClearsTheBoxAndThePointedChip`, YAAT-1044).
- **A `PipeDirectory` over an in-process `AutomationHost` runs on `HeldClock`.** `PipeClient`'s request timeout races a held answer, and its 5 s connect timeout races the host's thread-pool listen loop, measured 5.3 s late under a burn.

  A test that does not need the client's timer to fire holds the clock, passes `timeoutMs` at the host's maximum to `wait_for` (whose handler times itself on a real stopwatch), and gives each wait a hang guard of minutes (`WaitUntilToolTests`, `WaitForAndFilePickTests`; YAAT-1045, YAAT-1053).

  To measure the starvation, run the test exe under `load-burn` and lower it to BelowNormal when its `yaat-automation-test-*` folder appears: the suspend rig freezes every thread in step and left this race green.
- **A context menu's rows share the window as their top level.** Check `PointerTargets.Inside` before hovering a row, or the synthetic pointer lands outside the window (YAAT-904).
- **A desktop-gated test passes on the builder by being skipped.** A test gated on `InteractiveDesktop.MissingReason` skips in session 0, so a routed run reporting it green proved nothing. Prove it with `--local --reason "desktop-gated UI test"` (YAAT-904, YAAT-985).
- **Never `await` a `HeadlessUnitTestSession.Dispatch(...)` and then `Dispose()` the session in the same async flow.** The session
  completes the dispatched task on its own dispatcher-loop thread and plain `await` continuations inline onto the completing thread, so
  the disposing code runs *on* the loop it is about to `Task.Wait()` — a permanent self-deadlock (process never exits). A dump shows
  `Dispose → Task.Wait` directly above `DispatchCore → TrySetResult` on one stack. `ConfigureAwaitOptions.ForceYielding` does **not**
  prevent this (it only covers the already-completed-at-await fast path). Hop the continuation off the session thread with
  `.ContinueWith(t => t.GetAwaiter().GetResult(), TaskScheduler.Default)` — a `ContinueWith` without `ExecuteSynchronously` is always
  queued, never inlined. `tools/Yaat.GuideCapture/Program.cs` is the worked example.
- **Never race a `DispatcherTimer`-armed change against a poll.** A due `DispatcherTimer` runs ahead of an `InvokeAsync` posted before it when the UI thread is busy, so a test that arms a timer to flip state "during" a `wait_until`/`wait_for` can have the change land before the first poll, so the wait never sees the pre-change state (a `landed` wait, which needs an airborne poll first, then never holds and runs to its timeout).

  Script the change inside the polled read instead: the state provider for `wait_until`, a wrapped roots provider for `wait_for`, so the first poll always sees the pre-change state and the test asserts the change ran before the answer (`WaitUntilAcross` in `Automation/AutomationWaitUntilTests.cs` is the pattern). The default isolation is `PerTest`: the dispatcher is recreated for each test.
- **The automation test client's timeouts are hang guards, never deadlines.** `AutomationPipeTestClient.ReplyHangGuard` and `AutomationHostFixture.ConnectHangGuard` are 120 s, well past `wait_until`'s 30 s default `timeoutMs`, so a starved test process (BelowNormal under load, a listen loop the thread pool has not run yet) does not turn a slow answer into a pipe `TimeoutException`; no test relies on them firing.

  A test that expects a timeout (an unmet wait, no pipe) passes its own short one (300 ms in the existing cases), and a wait that never holds then surfaces as a `not met` result.
- **Reproduce BelowNormal starvation by suspending the test process, not by burning CPU.** Start the test exe, wait for the test's own `%TEMP%` folder to appear (`yaat-automation-test-*`, `yaat-launch-*`) as the trigger, freeze its process tree with `NtSuspendProcess` for a few seconds, then let it run. A wall-clock bound the freeze spans fails with the issue's exact message; a CPU burn of the same length left `AppToolsTests` green.

  `AutomationHostTests.Dispose_NoClientCanConnectAfterwards_Repeated` (Nightly, 20,000 cycles) guards a Unix-only dispose race: it goes red on the gate's routed macOS builder and never on Windows, so prove a change to the pipe transport with a routed run, not `--local`. The rig is `.tmp/rig/suspend-rig.ps1` (untracked, kept in the working tree); its `-Exe` takes an absolute path.
- **Running the suspend rig on a routed gate worktree needs `--local`.** A routed gate build leaves no local `bin/`, and the remote builder is macOS while the rig is Windows-only, so build and run the rig with `--local --reason "suspend rig: Windows-only, routed build leaves no bin"`.
- **For an automation wait, run the rig with `-FreezeMs 1000 -RunMs 200`.** A freeze lands between two polls of a 100 ms wait; the default 2500/60 mostly lands before poll 2, so it never separates the first read from a later one.
- **A wait-handler test runs pump-then-hold on an injected clock.** `AutomationHostFixture.StartHost(roots, state, clock)` hands the host a timer-counting `FakeTimeProvider`: `Dispatcher.UIThread.RunJobs` until poll N, then block on the reply off the UI thread, and advance the clock only once the timer the step means is armed (the poll timer, then the deadline).

  `AutomationWaitUntilClientLogTests.ClientLogMatches_Timeout_ReportsLinesRead` is the pattern; the test project references `Microsoft.Extensions.TimeProvider.Testing`.
- **A race between two threads of one test process is reproduced by holding one of them, not by suspending or burning.** Freezing the whole process keeps both threads in step, and a CPU burn or a UI-thread stall leaves work that advances only on dispatcher pumps where it was.

  Hold the background step across the act (a gate the probe releases before the read) to turn it red every time: the old `FavoriteButtonClick_InPaletteMode_ReachesCommandDispatch` race went 5/5 red that way and 0/110 under idle runs and burns.
- **A test that blocks the UI thread on work that marshals back to it waits for that work to queue, then pumps.** One `RunJobs()` after a fixed wait misses a background thread that starts late, and nothing pumps it after that.

  Wait without pumping until the job is queued (`Dispatcher.UIThread.HasJobsWithPriority(DispatcherPriority.Send)` for an `Invoke`) or the call has returned, under a hang guard, then `RunJobs()`: `MainViewModelSpeechContextThreadingTests` is the pattern. The headless dispatcher thread is a thread-pool worker.
- **A timeout the code under test arms is tested on `HeldClock`, never raced against a wall-clock stub delay.** `HeldClock` (yaat-server `tests/Yaat.ClientDriver.Mcp.Tests/RecordingFakes.cs`) holds each timer until `Fire(due)` and lists the live due times in `Pending`; `ManualClock` beside it fires every timer at once.

  The test starts the call, awaits the stub seeing the request (a hang guard), asserts `Pending`, then fires: `BatchToolsTests.MethodStep_TimeoutMs_IsTheAnswerTimeout` and `RecordToolsTests.Mark_SimClockBlocked_ReturnsWithinTheWaitAndNullSim` are the pattern. A stub that never answers uses `Timeout.InfiniteTimeSpan`, so no stub timer can beat the one under test.
- **A UI test that asserts `StatusText` awaits the startup nav-data load.** A test that asserts `MainViewModel.StatusText`, or a tool message built from it (`AutomationTools.DataBlock.cs`, `AutomationTools.Connection.cs`), awaits `vm.NavDataInitialization` right after constructing the view model and before any act, and sets any sentinel value only after the await.

  The constructor starts the background navigation-data load, which writes `StatusText` when it finishes and otherwise races the assertion (it turned CI red at 5f8719410).
- **A UI test that clears or asserts the terminal awaits the startup CRC alias load.** The constructor also starts the CRC alias load, which adds a "CRC aliases: N loaded from …" line to `TerminalEntries` when the user's CRC install has aliases.

  `vm.CrcAliasLoad` is the most recent load and completes only once that line is in the terminal, so a test that clears or asserts `TerminalEntries` awaits it right after constructing the view model (`MainViewModelClearTerminalOnScenarioLoadTests.WithSetting`). Without the await the line landed after the clear in loaded full runs.

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
pwsh tools/gate.ps1 -Log .tmp/test.log -TimeoutSeconds 120 -Slot heavy -- dotnet run --project tests/Yaat.Sim.Tests -c Release -- --filter-method "*<TestName>*" --show-live-output on
```

See [logging.md](logging.md) for the full `SimLog`/`AppLog` model and the `DeferredLogger` resolution order.

## Ground-layout and airport fixtures

`TestAirportGroundData` (`tests/Yaat.Sim.Tests/Helpers/TestAirportGroundData.cs`) is the `IAirportGroundData` backed by
`TestData/<id>.geojson`. It accepts ICAO (`KOAK`) or short-code (`OAK`) ids, and caches the parse+fillet result per `(FilletMode,
airportId)` per process (~500 ms OAK, ~2700 ms SFO) since layouts are immutable after construction. The parameterless constructor uses
`FilletMode.Standard` (the shipping fillet generator), so the ~150 existing call sites build the production graph; pass `FilletMode.None`
explicitly for raw, unfilleted-graph tests. `GetLayout` returns `null` for an unknown airport — callers skip silently. `TestAirportGroundData.LoadUncached(id)` is a fresh standard-fillet parse shared with no one, for a test that measures work the planner caches per layout instance (`AlleyPushPlan_SecondPlanReusesLayoutWork` proves reuse by `GC.GetAllocatedBytesForCurrentThread()` on one, not by time).

A test engine takes a **push-plan store** as a required argument: `new SimulationEngine(groundData, PushPlanStore.CreateShipped())` (a `SimulationWorld` likewise), so a test that exercises AI Ground's push rule plans with the shipped seed and envelopes. Pass a store built on a counting or throwing `PushPlanStore.StandPlanner` to see what is planned and when (`PushPlanStoreTests`); share one store between engines to model a server room's reload.

`GetLayout` passes the **real airport code** to `GeoJsonParser.Parse`, matching both production call sites, so runway widths come from
navdata instead of the 150 ft default. It calls `TestVnasData.EnsureInitialized()` first: the layout is cached per `(FilletMode,
airportId)` per process, so leaving the width dependent on whether some other test class had already loaded nav data would bake one
arbitrary graph in for the whole run. When nav data is genuinely unavailable (fresh/offline checkout) it falls back to a null code and
the default width, so the silent-skip convention still holds.

**Runway width is load-bearing on graph shape.** It sizes the on-runway rectangle, which decides which nodes classify as on-runway and
therefore where hold-short nodes seat, and it renumbers the nodes `RunwayCrossingDetector` creates (SFO's real 200 ft vs the old 150 ft
default moved its hold-short standoff 250 ft → 280 ft). **Never hardcode a node id in a test**: resolve it by meaning with `Helpers/GroundNodeLookup.cs` (`HoldShortBar(layout, runway, taxiway)`, `Junction(layout, a, b)`, `Parking(layout, name)`, `OnTaxiway(layout, taxiway, from, distanceFt, toward)`, plus `nearestTo` overloads where a name matches more than one node), or via `AirportGroundLayout.FindIntersectionNode` or `Helpers/TestLayoutNodes.cs`. Every lookup throws, naming the airport and what it looked for, when nothing matches, when more than one node matches, or when a walk along a taxiway stops short of the distance asked for. It never falls back to a nearby node.

To add a new airport: fetch its GeoJSON from the vNAS map API (`https://data-api.vnas.vatsim.net/api/training/airports/{FAA}/map`) and
drop it in `TestData/<lowercase-short-id>.geojson`. See `AirportLayoutDownloader` and `docs/ground/README.md` for the ground stack.

`TestArtccConfig.LoadZoa()` (`Helpers/TestArtccConfig.cs`) loads the committed ZOA ARTCC snapshot (`TestData/artcc-zoa-snapshot.json`, refreshed via `tools/refresh-artcc-snapshot.py`) for tests exercising replay-time TCP/ERAM resolution.

It returns `null` when the snapshot is absent — same silent-skip convention. ASDE-X tests that need the full ZOA config with its runway configurations use `ZoaArtccConfig.Full` (`Simulation/Asdex/ZoaArtccConfig.cs`) instead, which reads `docs/vnas-artcc-config-examples/zoa.json`.

`SfoGroundHarness` (`Helpers/SfoGroundHarness.cs`) is the multi-aircraft KSFO fixture behind the SFO ground-technique tests
(`SfoVideoRoutePinTests`, `SfoDepartureFunnelTests`, `SfoSixAlleyChoreographyTests`, `SfoSimultaneousAlleyPushTests`,
`SfoYankeePushTests`). `Build(output, autoCrossMode)` returns an `SfoGround(Engine, Layout)` record (null ⇒ skip) with `RngSeed = 42`;
spawns resolve everything **by name** — `SpawnParked(gate)`, `SpawnAtSpot(spot)`, `SpawnAtHoldShort(rwy, twy, toward)`,
`SpawnAtJunction(twyA, twyB)` — and go through one `SpawnAt(node, heading, phase)` that starts the phase and adds the aircraft to the
world. `TickUntil(engine, predicate, maxSeconds, each)` drives `TickOneSecond` and returns the second the predicate first held or -1;
callers treat -1 as failure, never as "still waiting". `AssertHoldShorts(route, (target, reason, cleared)…)` is order-preserving and
dumps the route on mismatch; `HoldShortMatches` accepts the `01R/19L` pair form. `DeadlockGuard(aircraft…)` fails a run on a mutual
yield (both `AutoYieldTarget`/`Hold.YieldTarget` at each other, both under 1 kt, ≥ 30 s) or a global stall (everyone under 1 kt for 60 s
with nobody in a legitimate stop phase) — it keys on `AutoYieldTarget`, so a detector branch that holds without annotating escapes the
mutual-yield check and is only caught by the stall net. Flake rules the harness bakes in: names not ids; state or latch gates rather than
"saw the phase once"; `GroundSpeed < 5` for "slowed to the bar" and `< 1` only after two stationary seconds; budgets are failure
timeouts, never trimmed.

`SfoGroundHarness.Build` re-initialises `SimLog`, so call `SimLogBuilder.EnableCategory` (and `InitializeSimLog`) **after** `Build`, or the categories it enabled are gone.

To sample each physics sub-tick of a second rather than only its end, drive the spine by hand in place of `TickOneSecond`: `engine.BeginSecond()`, `engine.OpenSecond(engine.BareHost)`, `engine.RunPrePhysics(engine.BareHost)`, `engine.RunPhysicsSubTick` once per sub-tick (`PhysicsSubTickRate` times, four), `engine.RunPostPhysics(engine.BareHost)`, then `engine.RunEndOfSecond(engine.BareHost)`, reading the state between sub-ticks.

`TickOneSecond` runs that sequence through `SimulationEngine.RunSecond`. `TurnAboutAtRunwayBarFootprintTests.TickOneSecondBySubTick(engine, afterSubTick)` is that hand-driven sequence as a helper that mirrors `RunSecond`, calling back after each sub-tick; a test that samples sub-ticks uses it rather than repeating the sequence, and must be changed with `RunSecond` when the spine's steps change.

A ground stop test that needs the speed on every sub-tick uses `Helpers/SubTickDriver.cs` (`TickSubTicksUntil(engine, callsign, until, maxSeconds, eachSubTick)`: the same hand-driven sequence, calling back after each sub-tick, returning the aircraft once `until` holds or null) with `Helpers/SpeedLossWatch.cs`, which samples the largest speed lost in one sub-tick. It leaves out only the final take of a stop, from ≤ 3 kt (`TaxiingPhase.StartNodeHoldArmSpeedKts`) to 0, so a dead stop from speed shows.

**Raw `GeoJsonParser.Parse` needs the nav DB only for runway crossings.** `GeoJsonParser.Parse(airportId, geoJson, runwayAirportCode)`
runs `RunwayCrossingDetector.DetectRunwayCrossings`, which reads `NavigationDatabase.Instance` — so a non-null `runwayAirportCode` (e.g.
`"OAK"`) throws `NavigationDatabase not initialized` in a test that hasn't loaded nav data. A test that only needs the graph or fillet
arcs (taxiway topology, `TurnAngleDeg`) should pass `runwayAirportCode: null` to skip the runway-crossing pass while still producing
nodes/edges/arcs (`OneWayResolverTests`, `GroundArcRenderFilterTests`, `DtoConverterGroundLayoutTests` all do). This applies to tests
calling `GeoJsonParser.Parse` **directly** (its last required input is a `LayoutInputs`; `TestStandLeadIns.For(id)` gives an airport's shipped lead-ins, `VideoMap: null` the no-map case); the `TestAirportGroundData` harness always passes the real code and the airport's committed videomap (see above), because a graph
built against a 150 ft placeholder width is not the graph production builds.

## Ticking a phase by hand — steer *and* integrate, at 0.25 s

A phase never moves the aircraft on its own. `GroundNavigator` (and every other phase) only *steers*: it writes `TrueHeading`,
`Targets.TargetSpeed`, and adjusts IAS; `FlightPhysics.Update` → `UpdatePosition` (the `IsOnGround` branch) integrates position from IAS
along heading. A phase-level test that loops only `PhaseRunner.Tick(aircraft, ctx)` therefore sits frozen at its spawn position with IAS
pinned at a constant (e.g. the slow-turn speed) forever. Mirror the engine's split every tick:

```csharp
PhaseRunner.Tick(aircraft, ctx);
FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
```

`StartNodeHoldShortArmingTests` is the worked example — real SFO layout, route from `TaxiPathfinder.ResolveExplicitPath`, aircraft placed
with `GeoMath.ProjectPoint` off the route's own `DepartureBearing`. Older `GroundPhaseTests` sidestep the split by teleporting the aircraft
between nodes by hand.

**Use `DeltaSeconds = 0.25`, never 1 s, when integrating a ground roll.** The shipping sim runs four 0.25 s physics sub-ticks per second
(1.25 kt of acceleration granularity). At the rotation gate (`GroundFrame.IasForGroundSpeed(gs) >= vr`) float epsilons —
`WindInterpolator.TasToIas` returns 144.999… for 145 at sea level, `HeadwindComponentKts` makes a "10 kt" headwind 9.9996 — tip the
`>=` comparison differently between calm and windy runs. With 1 s ticks (5 kt quantization) that shifts rotation by a whole tick and a
tailwind-vs-calm roll-distance ratio reads 1.07 instead of 1.14, which looks exactly like a headwind-sign bug that isn't there. Tick at
0.25 s like `GroundWindFrameTests`, and don't tighten assert tolerances below one sub-tick of acceleration.

`LandingExitDecelTests` is the rig for a runway exit's brake-to-rest stop, which only holds on sub-ticks (the navigator publishes the stop segment's speed on the arrival sub-tick): `FlyFromExitStart` restores a `RunwayExitPhase` at the start of an exit's first edge (an arc's entry speed, or a quarter along a straight at taxi speed) and steps phase-then-physics at `1 / SimulationEngine.PhysicsSubTickRate` until the exit completes. `CtxFor` builds that context with the field elevation from navdata (`GetAirportElevation`, the runway's own only for a synthetic runway), since a wrong elevation shifts the ground-speed/IAS conversion the stop depends on. `FlyC172Exit` stages a real exit by flying a short-final landing with `EXIT H` until the phase commits it, so the path and hold-short node are the sim's own. `SubTickStopCurveKts(rate, distNm)` is the closed-form speed from which braking at `rate` on sub-ticks stops `distNm` ahead, for starting an aircraft exactly on the stop curve.

## Debugging aircraft movement — `TickRecorder`

To see what happens inside a second, step it by hand: `BeginSecond()`, `TickPrePhysics()`, four `RunPhysicsSubTick(0.25, i)`, then `TickPostPhysics()`, reading the aircraft after each sub-tick. `engine.TickPhysics(1.0 / SimulationEngine.PhysicsSubTickRate)` steps one sub-tick, phases included. A deceleration check made once per second can hide a single sub-tick at another rate, or catch it, depending on which sub-tick a phase change lands on, so sample each sub-tick when a rate matters.

`GroundConflictDetector.DebugSink` receives the detector's `[Pair]` / `[SameEdgeTrailing]` / `[Convergence]` / `[ApplyMinLimit]` trace, which does not go through `ILogger`. It also fires while a recording replays, so `SimulationEngine.ReplayOneSubTick()` with the sink set shows the detector's view of a bug bundle one sub-tick at a time.

`TickRecorder` (`Helpers/TickRecorder.cs`) captures per-tick aircraft state to a JSON document (with embedded type/wingspan/length/color
metadata) for visualization. Attach it to one or more aircraft, `Record(t)` each simulated second, then `WriteJson(".tmp/run.json")`:

```csharp
using var _ = TickRecorder.Attach(engine, ".tmp/scenario.json", "N152SP", "N569SX");
```

Feed the output to the LayoutInspector `--ticks` overlay ([tick-animator.md](tick-animator.md)) to watch landings, exits, and taxi
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

- **Time budget** — `optimalTimeSec × TimeFudgeMultiplier (2.0) + cornerCount × SecondsPerCorner(cat) + StartupOverheadSec(cat)`, where the
  two overheads are derived from the physics constants rather than tuned by hand: `StartupOverheadSec = TaxiSpeed / TaxiAccelRate` (jet 30 s —
  the 0→nominal run the distance/speed term assumes is free) and `SecondsPerCorner = (TaxiSpeed − TaxiCornerSpeed) × (1/TaxiAccelRate +
  1/TaxiDecelRate)` (jet 18 s — the brake-and-regain a corner costs over cruising through it). A corner is a join turning more than
  `CornerAngleDeg` **or an arc segment sweeping more than it** — on the fillet graph every real corner is an arc, so counting joins alone
  gives `cornerCount = 0` on long SFO routes. The
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

`[Trait("Category", "Nightly")]` (per-spot taxi-coverage grid sweeps, e.g. `TaxiCoverageOakGridTests` / `TaxiCoverageSfoGridTests`) and `[Trait("Category", "PathfinderGrid")]` (the state-aware-pruning necessity oracle sweep) are excluded from the default `test-all.ps1` run for speed.

`[Trait("Category", "Desktop")]` (`AutomationClientZOrderTests`, which launch the real client on the interactive desktop, and `InteractiveDesktopTests`' check of the desktop probe; Windows only, so never in the Linux CI) is excluded too, because it puts windows on the developer's screen.

Run without an interactive desktop (session 0, a locked or disconnected session, a headless builder), each of its tests skips with the reason `InteractiveDesktop.MissingReason()` gives instead of failing; the probe's own check skips on the facts it does not probe (not Windows, session 0), so a real desktop still checks the probe.

The `AutomationPointerTests` whose assertions need `SyntheticMouse` input to reach a shown window (the hover, drag and quick-commands reorder tests) skip on the same probe; `InteractiveDesktop.cs` lives in `tests/Yaat.Client.UI.Tests/Helpers/`, and yaat-server's client-driver MCP tests link it, so both projects share one copy. The pipe's argument validation and refusals in that class run everywhere.

The default run passes `--filter-not-trait` for each of the three (untagged tests still run — a trait-inequality filter only drops explicitly tagged tests). Pass `pwsh tools/test-all.ps1 -Full` to include them (CI/nightly run Nightly and PathfinderGrid).

## Other fixtures

- `TestDispatch.Context(...)` (`Helpers/TestDispatch.cs`) — a concise factory for `DispatchContext`. **Production constructs
  `DispatchContext` with no optional params** so the compiler enforces wiring when a new context field lands; the test factory uses
  optional params so tests that don't care about ground layout/weather/lookup stay terse. See
  [command-pipeline.md](command-pipeline.md) for what `DispatchContext` carries.
- `RecordingLoader` (`Helpers/RecordingLoader.cs`) — loads v4 recording archives and legacy bug-report bundles transparently
  (`Load` → base recording with no snapshots, **memoized per file path for the test process** — `SessionRecording` is init-only and
  the engine never mutates `Actions`, so parallel classes share one decode; `OpenArchive` → on-demand snapshot reader). The replay surface itself (ReplayFromStartTo /
  FastForwardTo / hybrid replay, WAIT presets, bug-bundle triage) is owned by [e2e-tdd-issue-debugging.md](e2e-tdd-issue-debugging.md) and
  [snapshots-and-replay.md](snapshots-and-replay.md) — go there for the replay flow, not here.
- `RecordingGroundData` (`Helpers/RecordingGroundData.cs`) — the ground data every recording test whose archive bundles an airport layout builds its engine on, through `Evergreen(why)` or `VersionPinned(archivePath, why)`; `why` is required.

  **Evergreen** is behaviour that must keep holding on today's airport (a regression test for a sim, pathfinder or parser fix): it runs on `TestData/<id>.geojson` and is maintained as the layout changes. When a parser change renumbers nodes, its fixture's recorded `#id` TAXI commands and snapshot routes are remapped to today's ids.

  **Version-pinned** is behaviour that only makes sense on the airport as recorded (a restored mid-taxi route, recorded node ids): it runs on the archive's bundled layout, and its fixture's ids are never remapped. An airport the archive does not bundle falls back to `TestAirportGroundData`.

  An archive can mix node-id spaces: a bundled layout or recorded `#id` commands carry the ids of the unsnapped graph, while the harness's OAK is snapped, where a bar seated on a shape point or minted on the paint renumbers nodes. For an unsnapped A/B comparison, parse OAK with a null map (`RunwayPaintSnapTests.ParseOak(map: null, ...)`) and run the same recording on both layouts; compare by meaning (`GroundNodeLookup`), never by id.

  A pinned test diverges from the server on purpose (see [snapshots-and-replay.md](snapshots-and-replay.md)): every test archive is pre-format, and a pre-format bundled layout lacks runway coordinates, edge shapes and the runway-end dictionaries, so a pinned test reads runway geometry from `NavigationDatabase`.

  `Simulation/RecordingArchiveLayoutGuardTests` fails a test file that opens a layout-carrying archive without one of the two factories, reads `ReadLayout`/`ReadAllLayouts` directly, or builds `new TestAirportGroundData()` for it.

## The 30-second timeout discipline

Always run suites under a ceiling: `pwsh tools/gate.ps1 -Log .tmp/test.log -TimeoutSeconds 30 -Slot heavy -- dotnet test ...`.

The gate counts the 30 s on a load-adjusted clock (wall time on an idle machine, slower while other agents load it; `CLAUDE.md` "Build after edits"), so a YAAT sim suite that passes it is almost always **stuck**, not merely slow: a broken graph topology or an infinite pathfinder loop, not a heavy computation (the heavy grid sweeps are gated behind the `Nightly`/`PathfinderGrid` traits and excluded by default).

Treat a `STALLED` or `TIMED OUT` kill as a hung-test failure to diagnose, not a budget to raise; a `BACKSTOP` kill with a low "machine free" figure was a busy machine, so re-run it once alone. `pwsh tools/test-all.ps1` names the hung host itself before the gate's 120 s stall kill (see the `test-all.ps1` bullet below).

## Timing budgets measure thread CPU time

A timing budget on synchronous, single-thread work measures the thread's CPU time with `Yaat.Sim.Diagnostics.ThreadCpuTime` (`Current()` deltas or `Measure(Action)`), not `Stopwatch`, so the test does not fail when unrelated load slows the machine down (the machine usually runs several agents at once).

The failure message says "ms of thread CPU time". The test projects run server GC, whose collections run on their own threads and are not charged to the test thread, so a budget adds the `GC.GetTotalPauseDuration()` delta over the measured span to the thread CPU figure.

On Windows the clock advances only at the scheduler tick (about 15.6 ms), so a budget under about 50 ms means nothing there. Work that hops threads (`await`, `Parallel`, `Task.Run`, native worker threads) cannot be measured this way: keep wall time for it, and for anything that really is about wall time (UI-thread blocking, debounce and settle windows, hang guards). Never substitute process CPU time, which counts the other tests running in parallel.

**Loop budgets: event-bounded vs budget-bounded.** Most recording-replay tests are *event-bounded* — they tick until "reaches phase X" /
"exits `LineUpPhase`" / "segment > N" and `break`; the `for (t <= 600)` bound is a failure timeout, not a cost. Trimming it saves nothing
on a green run and only risks hiding a bug that surfaces later. A *budget-bounded* loop asserts every iteration with no early exit
(`ExitOverlapTests.ExitingAircraft_NeverOverlap`) and runs the full window even when green — those are the real trim candidates. Read the
loop body before proposing a budget cut: a `break;` inside an `if (event)` block means leave it alone.

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
        // ... attach a TickRecorder if you want a LayoutInspector --ticks overlay ...
    }
}
```

Run it: `pwsh tools/gate.ps1 -Log .tmp/test.log -TimeoutSeconds 30 -Slot heavy -- dotnet test -- --filter-method "*MyTaxiExitTests*"`. Before declaring a change done, verify the cross-repo build with `pwsh tools/test-all.ps1`.

## Footguns and pitfalls

- **A pinned value must not carry the process's day.** `MagneticDeclination.EvaluationDateUtc` and `SimScenarioState.ProcessDayUtc` are the UTC day the test process started, and a session start goes into the snapshot (`SimScenarioState.SessionStartUtc`).

  A test that compares against a value pinned across runs (a hash, a golden) passes a fixed `DateTime` with `DateTimeKind.Utc` as the session start, or it breaks every day at 00:00Z. Tests comparing two loads made in one process may use `EvaluationDateUtc`.
- **Static-singleton race.** A class reading a `TestVnasData`-populated singleton (`AircraftProfileDatabase`, `AircraftSiblingMap`,
  `NavigationDatabase`, …) can race a class mid-init. Symptom: `Expected 98 / Actual 96.5` (default-fallback), passes alone, flakes in the
  suite. **Fix: `TestVnasData.EnsureInitialized()` in the class *constructor*.**
- **A type staged with the wrong category's rates.** `AircraftCategorization.Categorize` returns `Jet` for an empty table, so a test that read a category before the aircraft tables loaded staged a piston as a jet whenever its case was the first table reader in the process (`FirmRateHoldShortTests`' C172 case). Sim's `ModuleInit` now loads the tables before any test. A test project without that, or a rig whose header rates differ from the rates it reads later, is the place to look.
- **A value a phase keys on `floor(ElapsedSeconds)` depends on which quarter-second the phase starts in.** A test needing a non-zero second ticks until it gets one.
- **One-second sampling counts the whole second in which a position-based window closes.** Exclude a second that ends past the boundary.
- **A tolerance on where a taxi stop comes to rest is `GroundNavigator.SetBackStopMarginFt`,** not a hand-picked sub-foot value.
- **An OAK test that replays a recording against today's layout and is pinned to a second drifts whenever bars or exit classification change.** Wait on the phase event instead.
- **A yaat-server test room's aircraft CIDs are random unless the seed is pinned.** Rooms seed their RNG from `Random.Shared` (`ScenarioSeedSource.RngSeed()`; `RoomEngineTestHarness` never pins it), so each aircraft draws a random 3-digit CID.

  A test that types a 3-digit flight ID can collide with a drawn CID and flake. Pin the seed with `harness.SeedSource.Pin(...)`, as `EramConformance.BuildAsync` does, or type a CID no aircraft holds (and assert that precondition). `EramConformanceQaTests` and `EramFlidResolutionTests.FreeCid` flaked this way.
- **`xunit.runner.json` must be Content-copied to `bin/` in each csproj** or xUnit ignores it. The Client.UI project relies on
  `parallelizeTestCollections: false` to keep the per-test `UserPreferences.FilePath` switch single-threaded; the Sim project sets it `true`
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
  tests) and run the project as a process with `--show-live-output on` (see [Running tests](#running-tests)).
- **Silent-skip on missing data is mandatory** for everything but NavData, CIFP and the committed aircraft tables (`AircraftSpecs.json`, `AircraftCwt.json`, `FaaAcd.json`), which throw. When GeoJSON/recording/ARTCC-snapshot is absent (fresh checkout, offline),
  return early — **no `Assert.Skip`, no exception** — so CI stays green. `TestVnasData.NavigationDb`, `TestAirportGroundData.GetLayout`,
  and `TestArtccConfig.LoadZoa` all return `null` for exactly this.
- **CIFP decompression uses a per-process sentinel-file dance.** A `yaat-test-FAACIFP18-<pid>` file held open without `FileShare.Delete`
  blocks a concurrent process's sweep from deleting a live file on Windows; `SweepStaleCifpTempFiles` reaps leaks across five legacy name
  patterns. **Don't add another `DecompressGzip` helper — route through `TestVnasData`.**
- **Run under a 120 s ceiling** (`pwsh tools/gate.ps1 -Log .tmp/test.log -TimeoutSeconds 120 -Slot heavy -- dotnet test`). A suite that the gate kills as `STALLED` or `TIMED OUT` is usually stuck on broken graph topology or an infinite pathfinder loop, not slow.
- **Use `pwsh tools/test-all.ps1`, not bare `dotnet test`, for full-suite confidence.** Bare `dotnet test` from yaat never builds the
  sibling yaat-server, so a `Yaat.Sim` signature change that breaks the server passes locally and fails in CI. The heavy
  `Nightly`/`PathfinderGrid` categories are excluded by default; pass `-Full` for the complete set.
- **`test-all.ps1` names a hung test host and keeps its stacks.** It restores the local dotnet tools first (`dotnet tool restore`; `dotnet-dump` comes from `dotnet-tools.json`), then `Wait-TestJobs` polls the test hosts every 5 s (`Find-TestHosts` in `tools/TestAllWatchdog.ps1`: the `*.Tests` and `testhost` processes under the script's own process tree). A host that has used no CPU for 90 s, under the gate's 120 s stall kill so it fires first, prints `test-all: STALLED <name> pid <pid>: no CPU for 90 s`, the process tree and the last 40 lines of each suite log.

  It then takes a Full dump with `dotnet-dump` into `<checkout>/.tmp/dumps/<name>-<pid>-<stamp>.dmp` (60 s timeout; an attach that hangs is killed and the run goes on without a dump), prints `clrstack -all` from it, kills the test hosts and fails the run with exit 1. A failed `dotnet tool restore` does not stop the run: a stall is still reported and killed, only without a dump. A run the gate kills first returns nothing from `.tmp/dumps/`, as for the Mac crash dumps below.
- **A test host that crashes on the Mac builder leaves its evidence in the test-all log.** On macOS, `test-all.ps1` sets the runtime's dump-on-crash variables (Mini dump plus a JSON crash report, in `~/yaat-crashdumps` on the builder), then prints each new dump's path and each crash report's content after the suites, and copies both into the checkout's `.tmp/dumps/`.

  A failed routed run returns that folder to the PC through the gate's `failureOutputs` glob for yaat (`.tmp/dumps/**`, in the PC's gate `remote.json`), so the dump and the report land in the worktree's `.tmp/dumps/`; a run the gate kills (ceiling, stall) returns nothing, and a bare routed `dotnet test` gets none of this.
- **`TaxiBudgetDeriver`/`TaxiBudgetEvaluator` budgets are deliberately generous** (real spins exceed them 5–10×). If a regression slips
  through, tighten the multipliers — do NOT hand-tune per-pair numbers. The arc-aware per-segment time floor and per-segment turn overhead
  are load-bearing; flat formulas under-allow tight-fillet and long clean routes by 2–8×.
- **`OracleAutoRouter` is a verbatim copy of `AutoRouter`'s A\* with one change** (closed-set keyed by `(node, bearing-bucket)`), making it
  strictly more complete than production. Any oracle-vs-production diff is exactly where production's node-id-only pruning loses. Keep it
  in lock-step with `AutoRouter.cs`.
- **Making `internal` members `public` for tests is fine.** No reflection, no `InternalsVisibleTo` hacks.
- **A branch on `x < 0` where `x` is zero on paper depends on the platform.** Positions on Linux and Windows drift apart by a few ulps over a taxi, so a distance that is a quantity minus itself (a stop placed on the aircraft) comes out `-7e-15` on one and `+7e-15` on the other, and CI on Linux goes red where Windows stays green.

  Keep such a quantity clearly signed (place the stop by its real offset), never compare it with zero; reproduce with a Linux run ([Linux runs](#linux-runs-ci-investigation-and-cross-platform-verification)).
- **Pilot transmissions land in different lists depending on the `PhaseContext` flags, and the engine drains them every second.**
  `PilotResponder.RouteSoloOrRpoTransmission` writes `PendingPilotTransmissions` (solo, student on frequency), `PendingPilotSpeech` (RPO
  with pilot speech shown; a `PilotSpeechText`, so a test asserts on `.TerminalForRpo`, the line the terminal shows) or `PendingWarnings` (RPO default — what a hand-built `PhaseContext` with the flags at their defaults gets).
  A bare phase-tick test therefore asserts on `PendingWarnings`, never `PendingNotifications`. At engine level the spine drains
  `PendingWarnings`/`PendingNotifications` inside `TickOneSecond`/`ReplayOneSecond`, so a per-second scan of the aircraft's lists sees
  nothing — subscribe to `engine.WarningEmitted` (and `TerminalEntryEmitted`) before the first tick instead. `Issue429FatApproachesTests`
  and `Issue186ZhuAusStarApproachTests.RunJoinTest` show the shape; the latter had a dead per-tick scan for months.
- **A phase-only tick loop never moves the aircraft.** Phases steer (heading, `TargetSpeed`, IAS); `FlightPhysics.Update` integrates
  position. Tick both per iteration or the aircraft freezes at spawn with IAS pinned. Ground-roll integrations use `DeltaSeconds = 0.25`
  — 1 s ticks plus the `TasToIas`/headwind float epsilon flip the rotation gate by a whole tick and fake a headwind-sign bug. See
  [Ticking a phase by hand](#ticking-a-phase-by-hand--steer-and-integrate-at-025-s).
- **Headless renders only on render-timer ticks.** `RunJobs`/`UpdateLayout` never force one; drive `InvalidateVisual` + `RunJobs` +
  `AvaloniaHeadlessPlatform.ForceRenderTimerTick()` to convergence before reading render-derived state.
- **Every UI test runs on its own empty `preferences.json`** (`[PreferencesFilePerTest]`, bound per `UserPreferences` instance), so no test restores preferences; create `UserPreferences` in the test body, not a constructor or field initializer. A fresh VM failing its *first* default-state assertion is then a real default or a file the test wrote itself, not leakage. Details in [Avalonia headless UI tests](#avalonia-headless-ui-tests).
- **Never `await` a `HeadlessUnitTestSession.Dispatch` then `Dispose` the session in the same flow** — the continuation inlines onto the
  session loop and `Dispose` deadlocks on itself. Hop via `ContinueWith(..., TaskScheduler.Default)`; `ForceYielding` does not help.
- **Event-bounded replay loops (`break` on event) have a failure timeout, not a cost** — don't trim their budget; only loops that assert
  every iteration with no early exit get faster.
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
  explicit `TAXI`). Because a replayed rejection is only logged, the symptom gets misattributed to whatever commit landed at the same
  rebase. Confirm with a **layout A/B** (swap `git show <refresh-commit>^:tests/.../sfo.geojson` back in and re-run). A recording test that
  must stay on the recorded airport is `RecordingGroundData.VersionPinned`, which needs no `TestData` geojson; a pure pathfinding test that
  opens no archive pins a committed full-precision snapshot via `Helpers/PinnedSfoGroundData.cs`. Build gate-coverage routes from taxiway **names**
  (`FindIntersectionNode`, `TaxiPathfinder.FindRoute`), not hardcoded node IDs. The `ActionRouter` logs a rejected replayed command at
  Debug (category `ActionRouter`) and, when the record says live accepted it, a `replay-fidelity` warning.
- **OAK C/B is a 14.5° merge, so a test of a bare `TAXI B` from C must say where the aircraft is and measure the nose, not the centre.** B leaves node 193 at 126.6° while C runs at 112.1°, so the two pavements overlap along the approach and a B738's nose can already lie on B's centreline short of the junction node. The layout carries no taxiway width, so assert the nose's distance to B's centreline and the distance short of the node, never "off B's edge".

  A rolling start needs the speed set after the route (`TAXI A C D`, then `SPD n`, as `BareTaxiFromRollingOnCTests` does) and the initial speed set directly, as a snapshot restore leaves it: accelerating from rest on A reaches only about 20 kt before C, because A bends.

  Issue the `TAXI B` the first second the centre is within the distance under test, and judge the result by the placement and the measured stop against an independently computed firm-rate stop (`v²/2a` plus half a physics step), never against the code's own figure. A test that spawns the aircraft already stopped inside the setback is a different case (it holds where it stands, never unable).
- **A recording whose student TAXI turned an aircraft about on its taxiway diverges when the type's gear does not fit.**

  A controller-issued TAXI whose only route lies behind an aircraft whose gear does not fit a turn about on its taxiway (`TurnAboutFit`, at a reversal of 135° or more: a B738, CRJ2, C25A, DH8D or AT76, for example; never one held after a pushback) is refused ("no room to turn around", see [ground/pathfinder.md](ground/pathfinder.md#where-it-sits--entry-points)), so a recording made when it turned about replays a rejected command and the aircraft stays put until a later command moves it.

  Re-pose the test rather than loosen the gate: give the TAXI before the aircraft passes the junction its route turns at, as the `Issue172Jbu577*` tests do (`Issue172Jbu577TaxiSpinTests.ActionsWithTaxiGBMovedEarlier` moves JBU577's `TAXI G B HS B` from t=444 to t=410, before the H/G junction, and `SimulationEngine.ArmReplay` replays the edited list), or use a type whose gear fits (a C172 or C208). A scripted TAXI (preset, AI controller, `TAXIAUTO`) is never refused this way.

- **A fixture that stages an aircraft at a runway bar must stage it on the taxiway side.** `TaxiCoverageRunner.TaxiwayDepartureHeading(layout, holdShort)` points from the bar toward the far node of its non-runway edge leading farthest from the runway's centreline; it once took the first non-runway edge, which at a bar can run toward the runway, so a spawn "short of the bar" faced the runway and started inside the hold line. The layout is a required argument so the runway is found.

  The same trap sat in helpers that picked an approach point from the runway side of the bar. A test that places an aircraft short of a bar checks which side of the line it is on (`FollowingPhase.IsInsideHoldLine`, or `HoldLineFootprint` for the body's nose, wingtips and tail), since a turn about from rest now refuses to carry any part past a line and an aircraft that starts inside it is judged differently (`TurnAboutAtRunwayBarFootprintTests` stages with every part short of the line).
