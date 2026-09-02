# Testing Patterns

**Analysis Date:** 2026-09-01

## Test Framework

**Runner:** xUnit v3 (`xunit.v3` 3.2.2, `xunit.runner.visualstudio` 3.1.5) on **Microsoft.Testing.Platform (MTP)**, not classic VSTest — selected repo-wide via `global.json`:

```json
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```
(`X:/dev/yaat/global.json`, verified — both yaat and yaat-server pin this.) Each test csproj also sets `UseMicrosoftTestingPlatformRunner` (per `docs/test-harness.md`). This means `dotnet test` **does not accept VSTest-style flags** (`--filter "FullyQualifiedName~X"`, `--logger trx`) — those fail with `Unknown option`. Runner-specific options go after a bare `--` separator.

**Assertion library:** Plain xUnit `Assert.*` only — no FluentAssertions, Shouldly, or similar found anywhere under `tests/` (grep for `FluentAssertions|Shouldly` returns nothing outside `bin/` artifacts). Assertions read `Assert.True(result.Success, result.Message)` / `Assert.Equal(expected, actual)`, always passing a failure-context message as the second `Assert.True`/`Assert.False` argument when the boolean alone wouldn't explain the failure (`tests/Yaat.Sim.Tests/AerialRefuelingCommandTests.cs:43,69`).

**Mocking library:** None. No Moq, NSubstitute, or FakeItEasy package reference in any test csproj. See **Mocking** below — the codebase's answer to "what do I mock" is "load real data, don't mock."

**Run commands:**
```bash
dotnet test tests/Yaat.Sim.Tests -- --filter-method "*MyTaxiExitTests*"     # substring of Namespace.Class.Method
dotnet test tests/Yaat.Sim.Tests -- --filter-class "*.MyTaxiExitTests"      # one class
dotnet test tests/Yaat.Sim.Tests -- --filter-not-trait "Category=Nightly"   # exclude a trait
dotnet test tests/Yaat.Sim.Tests -- --report-xunit-trx --report-xunit-trx-filename results.trx
pwsh tools/test-all.ps1                                                    # yaat + yaat-server, excludes Nightly/PathfinderGrid
pwsh tools/test-all.ps1 -Full                                              # …including the heavy sweeps (what CI/nightly run)
```

**One project per invocation:** `dotnet test tests/A tests/B` silently reports `Zero tests ran` and exits 0 — always run projects separately (or the whole solution) and check each `total:` line, per `docs/test-harness.md`.

**Live/captured output:** `dotnet test` shows only per-assembly summaries + failures. To see `ITestOutputHelper`/`SimLogBuilder` output, run the built test executable as a process: `dotnet run --project tests/Yaat.Sim.Tests -c Release -- --filter-method "*Name*" --show-live-output on`.

**Always wrap in a timeout:** `timeout 30 dotnet test ... -- --filter-method "*Name*"` for targeted runs (a filtered YAAT sim test not finishing in 30s is stuck, not slow), `timeout 120` for a bare full-suite run. Never run unbounded, per `CLAUDE.md` and `docs/test-harness.md`.

## Test File Organization

**Three (client-visible) + one (server) test projects:**

| Project | Repo | Scope |
|---|---|---|
| `tests/Yaat.Sim.Tests/` | yaat | Simulation, commands, physics, phases, navdata, parsers, ground/pathfinder — ~420 test files |
| `tests/Yaat.Client.Tests/` | yaat | Client-side non-UI logic |
| `tests/Yaat.Client.UI.Tests/` | yaat | Avalonia headless UI, `MainViewModel`, `UserPreferences` |
| `Yaat.Server.Tests/` | yaat-server (`../yaat-server/tests/Yaat.Server.Tests/`) | Server rooms, hub, broadcast — reached only via `tools/test-all.ps1`, never by a bare yaat `dotnet test` |

**Location/naming:** Test files are **not co-located** with source — they live entirely under `tests/<Project>/`, mirroring the `src/Yaat.Sim/` subdirectory structure for larger subsystems (e.g. `tests/Yaat.Sim.Tests/Simulation/GroundTaxi/`, `tests/Yaat.Sim.Tests/Pathfinding/`). File name = `<Subject><Aspect>Tests.cs` (`AerialRefuelingCommandTests.cs`).

**Shared helpers:** `tests/Yaat.Sim.Tests/Helpers/` holds ~18 reusable fixtures/utilities (not test classes themselves, though a couple like `TestLayoutCoverageTests.cs` are hybrid): `SimLogBuilder.cs`, `TestDispatch.cs`, `RecordingLoader.cs`, `TickRecorder.cs`, `LayoutCloner.cs`, `TestAirportGroundData.cs`, `PinnedSfoGroundData.cs`, `ParsedCommandDummyFactory.cs`, `RouteGeometryAsserts.cs`, `TaxiBudgetEvaluator.cs`/`TaxiBudgetDeriver.cs`, `NearestNodeHelper.cs`, `OracleAutoRouter.cs`, `TestAiPositions.cs`, `TestArtccConfig.cs`, `TestLayoutNodes.cs`, `FilletComparisonGates.cs`. Check this directory before writing new test infrastructure — duplication here is exactly the kind of repeat-custom-work `CLAUDE.md` says to consolidate into a tool.

## Test Structure

**Suite organization (real example, `tests/Yaat.Sim.Tests/AerialRefuelingCommandTests.cs`):**
```csharp
[Collection("NavDbMutator")]
public sealed class AerialRefuelingCommandTests
{
    public AerialRefuelingCommandTests()
    {
        TestVnasData.EnsureInitialized();   // constructor-time singleton pin, not per-[Fact]
    }

    private static AircraftState AircraftOn(MilitaryRouteVariant variant) { /* fixture builder */ }
    private static CommandResult Apply(AircraftState aircraft, string text) { /* parse + dispatch + assert-parsed */ }
    private static void TickPhase(AircraftState aircraft) { /* drive one phase tick */ }

    [Fact]
    public void Car_ClearsTheTankerOntoTheTrackAtItsPublishedBlock()
    {
        var aircraft = AircraftOnAr1();
        var result = Apply(aircraft, "CAR AR1");
        Assert.True(result.Success, result.Message);
        Assert.Equal("AR1", aircraft.MilitaryRoute.Designator);
        // ...
    }
}
```

**Patterns:**
- Constructor-based setup (no `[Fact]`-level `Arrange` boilerplate for data init) — every test class that reads a data-backed singleton calls `TestVnasData.EnsureInitialized()` in its constructor, not inside individual test methods (see **Static-singleton race protocol** below).
- Private `static` builder/helper methods at the top of the class scope test data construction and common actions (`AircraftOn`, `Apply`, `TickPhase`) — these are file-local, not promoted to `Helpers/` unless reused across multiple test classes.
- `[Collection("...")]` attribute used to opt specific test classes out of default parallelism when they mutate global state (e.g. `"NavDbMutator"` groups tests that swap `NavigationDatabase.SetInstance`) — xUnit runs classes within the same named collection sequentially relative to each other.
- Class-level `<summary>` XML doc cites the governing FAA regulation section the test suite is validating (`AerialRefuelingCommandTests.cs:11-14`), a pattern specific to this codebase's aviation-realism requirement.
- Test method names are full sentences describing expected behavior, not `MethodName_Scenario_Expected` triads — e.g. `Car_ClearsTheTankerOntoTheTrackAtItsPublishedBlock`.

## Mocking

**No mocking framework used.** The codebase's explicit policy (`docs/test-harness.md`, rule 1 of the "iron rules"): **"Real data, never synthetic."** Initialize via `TestVnasData.EnsureInitialized()` (`src/Yaat.Sim/Testing/TestVnasData.cs`) rather than hand-rolling stub fixes/profiles/aircraft data.

**What "mocking" looks like here:** `TestVnasData.EnsureInitialized()` loads committed real-data fixtures (`TestData/AircraftSpecs.json`, `TestData/AircraftCwt.json`, `TestData/FaaAcd.json`, `Data/AircraftProfiles.json`, `TestData/NavData.dat`, `TestData/FAACIFP18.gz`) into process-wide static singletons: `AircraftCategorization`, `WakeTurbulenceData`, `FaaAircraftDatabase`, `AircraftProfileDatabase`, `AircraftSiblingMap`, `NavigationDatabase`. A three-tier fallback (cache → single download → bundled fallback file) keeps this offline-capable; `YAAT_SKIP_NAVDATA_DOWNLOAD=1` / `YAAT_SKIP_CIFP_DOWNLOAD=1` force the bundled-fixture tier.

**Controlled fix/runway sets:** For parser-only tests that need a deliberately small, controlled navigation dataset (not the full real DB), use `NavigationDatabase.ForTesting(...)` — this constructs a synthetic-but-explicit `NavigationDatabase` instance rather than mocking an interface. Caution: `TestVnasData.EnsureInitialized()` unconditionally re-runs `NavigationDatabase.SetInstance(realDb)` on every call (not just the first), so a real-data test running after a `ForTesting()` test in the same process will silently restore the real DB — this is deliberate, not a bug, per `docs/test-harness.md`.

**Command dispatch "mock":** `Helpers/TestDispatch.cs` and `Helpers/ParsedCommandDummyFactory.cs` build minimal-but-real `CommandDispatcher` contexts (`TestDispatch.Context(Random.Shared)` in `AerialRefuelingCommandTests.cs:46`) rather than mocking `CommandDispatcher` itself.

**What NOT to mock (codebase-specific extension of the global rule):** Never stub aircraft performance profiles, CIFP procedures, fixes/airways, or airport ground layouts — these integration surfaces are exactly where bugs hide (per `docs/test-harness.md`: "Synthetic stubs hide integration problems").

## Fixtures and Factories

**Real-data fixture files:** `tests/Yaat.Sim.Tests/TestData/` holds bundled offline fallbacks (`NavData.dat`, `FAACIFP18.gz`, `AircraftSpecs.json`, `AircraftCwt.json`, `FaaAcd.json`) plus their `*-manifest.json` freshness pins. Refresh via `python tools/refresh-navdata.py` (NavData) — scenario fixtures are refreshed from the yaat-server repo's `python tools/validate-all-scenarios.py`.

**Recording/replay fixtures:** `tests/Yaat.Sim.Tests/TestData/*.zip` are recorded bug-bundle/replay fixtures (up to ~14MB, under a 20MB `check-added-large-files` ceiling in `prek.toml`). Deduped via `tools/Yaat.RecordingConsolidator` (see the `consolidate-recordings` skill) — do not commit near-duplicate recordings by hand.

**Ground layout fixtures:** `Helpers/PinnedSfoGroundData.cs` and `Helpers/TestAirportGroundData.cs` provide a pinned, known-good airport layout for ground/taxi tests rather than downloading live vNAS data-api layouts per test run.

**Location:** All fixtures live under `tests/Yaat.Sim.Tests/TestData/` or `Helpers/`; no separate top-level `fixtures/` or `factories/` directory.

## Coverage

**No enforced coverage threshold found** in any csproj, CI workflow, or `prek.toml` — `--report-xunit-trx` produces per-run TRX reports (`bin/<cfg>/net10.0/TestResults/`) but no coverage-percentage gate is configured.

## Test Types

**Unit tests:** The bulk of `tests/Yaat.Sim.Tests/` — command parsing/dispatch, physics tick math, phase transitions, pathfinder/ground logic — run against a real (or `ForTesting()`-scoped) `NavigationDatabase` and real performance profiles, so "unit" here still integrates real domain data rather than isolating with mocks.

**Integration/E2E tests:** Replay-driven E2E tests reconstruct a bug scenario from a recorded bundle and tick the simulation forward to a target point (`docs/e2e-tdd-issue-debugging.md`), verifying the fix reproduces and resolves against a real recorded trajectory. `RecordingLoader.cs` and `TickRecorder.cs` in `Helpers/` support this. Per user-feedback memory (`feedback_recording_e2e_verify_state_reached.md`), a replay E2E test must assert the target state was actually reached, not merely that no exception was thrown.

**Grid/sweep tests:** Gated by `[Trait("Category", "Nightly")]` (per-spot taxi-coverage grid sweeps, e.g. `tests/Yaat.Sim.Tests/Simulation/GroundTaxi/TaxiCoverageOakGridTests.cs:31`) or `[Trait("Category", "PathfinderGrid")]` (`tests/Yaat.Sim.Tests/PathfinderGrid/StateAwarePruningNecessityTests.cs:27`, `tests/Yaat.Sim.Tests/Pathfinding/Req1MembershipArcSweepTests.cs:25`) — excluded from the default local/PR run (`tools/test-all.ps1` without `-Full`), included in CI/nightly (`-Full`).

**Headless UI tests:** `tests/Yaat.Client.UI.Tests/` drives real Avalonia windows via `Avalonia.Headless`. Renders happen only on render-timer ticks — `Dispatcher.UIThread.RunJobs()` + `window.UpdateLayout()` pump layout but do not force a render, a documented footgun in `docs/test-harness.md`.

**Cross-repo server tests:** `../yaat-server/tests/Yaat.Server.Tests/` — never reached by a bare `dotnet test` from the yaat repo. Only `pwsh tools/test-all.ps1` (or `-ServerDir <path>` for worktree checkouts) builds and runs it. This is the mechanism that catches `Yaat.Sim` signature changes that compile in yaat but break the sibling server repo.

## Common Patterns

**Static-singleton race protocol (the most important non-obvious pattern in this codebase):** xUnit parallelizes test *classes* within `Yaat.Sim.Tests` (`xunit.runner.json` sets `parallelizeTestCollections: true`, Content-copied to `bin/` via each csproj's `<Content Include="xunit.runner.json" .../>`). Data-backed singletons populated by `TestVnasData.EnsureInitialized()` are process-global, so a class **reading** one can race a class **mid-initializing** it.

```csharp
public sealed class MyProfileReadingTests
{
    public MyProfileReadingTests()
    {
        TestVnasData.EnsureInitialized();   // pin singletons before any test body runs
    }
    // [Fact]s follow
}
```
Symptom of getting this wrong: a value mismatch where both sides should read the same table (classically `Expected 98 / Actual 96.5`, `96.5` being the default-fallback returned before the profile loads). The test **passes alone, flakes only in the full suite** — always call `EnsureInitialized()` in the constructor, never assume a singleton starts empty.

`tests/Yaat.Client.UI.Tests/xunit.runner.json` sets `parallelizeTestCollections: false` (opposite of Sim) specifically because `UserPreferences` fixtures race the shared `preferences.json`. Diagnostic order for an `Expected [...] / Actual []` UserPreferences failure: confirm `xunit.runner.json` was actually Content-copied to `bin/` before suspecting `UserPreferences.Save` logic — a missing copy silently reverts to default parallelism.

**Per-user path isolation in UI tests:** Any test project touching `UserPreferences`, `AppLog`, `MainViewModel`, or a per-user cache MUST set `YAAT_APPDATA_DIR` to a unique temp dir in a `[ModuleInitializer]` (pattern in `tests/Yaat.Client.UI.Tests/ModuleInit.cs`), since every `%LOCALAPPDATA%/yaat` path routes through `YaatPaths`. Cleanup is PID-aware (sweeps dead sibling dirs, skips live ones).

**`[ModuleInitializer]` warm-up (Sim):** `tests/Yaat.Sim.Tests/ModuleInit.cs` sets `GroundNavigator.ThrowOnOrbit = true` (turns a silent ground-following recovery into a hard test failure — the shipping app leaves this `false`), calls `TestVnasData.SetTestDataDir(...)` + warms CIFP/NavData once, and pre-warms `AirspaceDatabase.Default`/`MilitaryRouteDatabase.Default` on a background task.

**Silent-skip on missing data:** A test needing real nav/CIFP data that finds `NavigationDb` returns `null` (data absent, e.g. fresh offline checkout) must return early with no assertion — no `Assert.Skip`, no throw — so CI without network access stays green.

**Async testing:** No distinct pattern beyond standard `async Task` test methods with `await`; the harness's async-sensitive pieces (double-checked locking in `NavigationDb`, `SemaphoreSlim` + `Task.WhenAll` in `ScenarioValidator`) are documented in `docs/test-harness.md` rather than shown as a reusable test idiom.

**Error/exception testing:** Expected-failure paths assert on the `CommandResult`/`ParseResult` shape (`Assert.False(result.Success, ...)` / `Assert.True(parsed.IsSuccess, parsed.Reason)`) rather than `Assert.Throws<T>`, consistent with the "Result objects over exceptions for expected failure paths" convention in CONVENTIONS.md.

---

*Testing analysis: 2026-09-01*
