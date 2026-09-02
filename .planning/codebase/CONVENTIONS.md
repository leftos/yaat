# Coding Conventions

**Analysis Date:** 2026-09-01

## Naming Patterns

**Files:**
- One primary type per file, file name matches the type name (`CommandDispatcher.cs` → `class CommandDispatcher`).
- Test files mirror the type under test with a `Tests` suffix (`AerialRefuelingCommandTests.cs`), grouped into subdirectories mirroring `src/Yaat.Sim/` (`tests/Yaat.Sim.Tests/Simulation/GroundTaxi/`, `tests/Yaat.Sim.Tests/Pathfinding/`).
- Shared test infrastructure lives in `tests/Yaat.Sim.Tests/Helpers/` (e.g. `TestDispatch.cs`, `RecordingLoader.cs`, `SimLogBuilder.cs`) — check there before writing a new helper.

**Functions/Methods:** PascalCase everywhere (`Dispatch`, `TickPhase`, `GetSnapshot`). Test method names are full sentences in PascalCase describing behavior, e.g. `Car_ClearsTheTankerOntoTheTrackAtItsPublishedBlock`, `Car_PublishedBlock_IsArmedAsAnAltitudeFloorAndCeiling` (`tests/Yaat.Sim.Tests/AerialRefuelingCommandTests.cs:64-79`). Underscore-separated clauses inside the PascalCase name are the norm for test methods; no other code uses underscores in identifiers.

**Variables:** camelCase locals (`aircraft`, `entry`, `parsed`). MVVM backing fields for `[ObservableProperty]` use `_camelCase` and the source generator produces the `PascalCase` public property (per `CLAUDE.md` and confirmed in `src/Yaat.Client/ViewModels/ArrivalGeneratorsEditorViewModel.cs:31-40`).

**Private fields (non-MVVM):** `_camelCase`, always `private readonly` where possible — e.g. `src/Yaat.Sim/Phases/AirspaceBoundaryHoldPhase.cs:30` (`private readonly List<NavigationTarget> _originalRoute = [];`), `src/Yaat.Sim/Phases/RunwayInfo.cs:17,43`.

**Types:** PascalCase for classes/records/interfaces/enums. DTOs are `sealed record` or `record` with positional or init-only properties, suffixed `Dto` — see `src/Yaat.Client.Core/Services/ServerConnection.cs:950` (`AircraftDto`), `:1200` (`ScenarioSummaryDto`), `:1233` (`RoomStateDto`).

**Loggers:** Every `Yaat.Sim` static/instance class that logs declares `private static readonly ILogger Log = SimLog.CreateLogger("ClassName");` with the class's own name as the category string — `src/Yaat.Sim/Commands/ApproachCommandHandler.cs:15`, `src/Yaat.Sim/Commands/CommandDispatcher.cs:48`. This is mandatory per `CLAUDE.md`, never optional.

## Code Style

**Formatting:** CSharpier (`dotnet csharpier format .`) is authoritative for C# layout, configured via `.csharpierrc` at the repo root with `printWidth: 150` (`X:/dev/yaat/.csharpierrc`). Never run bare `dotnet format` per `CLAUDE.md` — only `dotnet format style` / `dotnet format analyzers` (both run by the pre-commit hook) plus CSharpier for whitespace/layout.

**Line length:** 150 chars, enforced two ways: `.editorconfig` (`max_line_length = 150` under `[*]`, `X:/dev/yaat/.editorconfig:2`) and CSharpier's `printWidth: 150`.

**Braces:** `.editorconfig` sets `csharp_prefer_braces = true:warning` (`.editorconfig:5`) — every `if`/`for`/`while`/etc. gets braces, confirmed throughout `src/Yaat.Sim/Commands/CommandDispatcher.cs`.

**Qodana suppressions:** `.editorconfig` carries a block of `resharper_*_highlighting = none` overrides with inline comments explaining each false-positive category (DTO/serialization properties, cross-project public APIs, MVVM partial methods/XAML bindings, aviation acronym naming, test default-argument readability) — `.editorconfig:8-33`. These are intentional codebase-wide suppressions, not evidence the underlying pattern is discouraged.

**Linting/analyzers:** `dotnet format style` and `dotnet format analyzers` both run in `prek.toml` (`tools/hooks/dotnet-format-wrapper.sh`, `X:/dev/yaat/prek.toml:41-53`) as pre-commit gates, followed by a full `dotnet build -p:TreatWarningsAsErrors=true` (`prek.toml:97-101`) — zero warnings is a hard commit gate, not just a CI check.

## Import Organization

**Style:** `using` directives at top of file, no visible enforced ordering tool (no `.editorconfig` `dotnet_sort_system_directives_first` rule found) — observed order in `tests/Yaat.Sim.Tests/AerialRefuelingCommandTests.cs:1-8` is alphabetical by full namespace with `Xunit` first, then `Yaat.Sim.*` namespaces alphabetically.

**No relative imports:** Per `CLAUDE.md`, "Absolute imports only — no relative (`..`) paths" is a hard rule; C# doesn't have file-relative imports so this manifests as fully-qualified `using Yaat.Sim.X;` namespaces rather than nested-type access chains.

**No path aliases:** Not used; C# `using` directives reference full namespaces (`Yaat.Sim.Commands`, `Yaat.Sim.Phases`, `Yaat.Sim.Pilot`).

## Error Handling

**Fail loud, never swallow:** Per `CLAUDE.md`, exceptions are never silently caught. `src/Yaat.Sim/Commands/CommandDispatcher.cs` has zero bare `catch (Exception)` blocks — errors surface via `CommandResult.Failure(...)` return values rather than exceptions for expected parse/validation failures, and via `Log.LogWarning(...)` (`CommandDispatcher.cs:1174`, `:2515`) for anomalies that are logged but not fatal.

**Result objects over exceptions for expected failure paths:** Commands return a `CommandResult` with `Success`/`Message`/`Reason` rather than throwing — see `tests/Yaat.Sim.Tests/AerialRefuelingCommandTests.cs:43-47` (`Assert.True(parsed.IsSuccess, parsed.Reason)`), `:69` (`Assert.True(result.Success, result.Message)`). Exceptions are reserved for programmer-error/invariant violations (e.g. `GroundNavigator.ThrowOnOrbit` in tests, per `docs/test-harness.md`).

**Logging over throwing for recoverable anomalies:** `Log.LogWarning` calls in `CommandDispatcher.cs` (lines 1174, 2515) record unexpected-but-survivable conditions without aborting the tick.

**Required non-optional logger fields:** `CLAUDE.md` mandates `Log` fields on every Yaat.Sim static class handling errors — "never optional." Client code uses `AppLog` (see `docs/logging.md` for the split between `SimLog` and `AppLog`).

## Comments

**Self-documenting code preferred:** Per `CLAUDE.md`, comments explaining *what* code does are a refactor signal, not a comment opportunity. Comments observed in the codebase explain *why*, not *what* — e.g. `AerialRefuelingCommandTests.cs:32` (`// Back the aircraft up along the inbound bearing so the entry point is genuinely ahead.`), `AerialRefuelingCommandTests.cs:71` (`// AR1 publishes FL240/FL310.`).

**No milestone/roadmap references in source comments:** `CLAUDE.md` explicitly forbids `M10.1`, `MVP`, `(future PR)`, `for now…` style comments in `.cs`/`.ts`/`.py` files — that context belongs in `docs/plans/*.md`.

**No commented-out code:** Delete instead of commenting out, per `CLAUDE.md`.

**XML doc comments (`///`):** Used liberally on public types and non-trivial members, especially in `Yaat.Sim` — e.g. the class-level `<summary>` on `AerialRefuelingCommandTests` (`AerialRefuelingCommandTests.cs:11-14`) that cites the FAA regulation (`FAA JO 7110.65 §9-2-13`) the test enforces, and the multi-paragraph `<summary>` on `TestVnasData` (`src/Yaat.Sim/Testing/TestVnasData.cs:10-18`) documenting call contract and side effects. Google-style docstring equivalence (params/returns documented where non-trivial) is expected per `CLAUDE.md`'s "Google-style docstrings on non-trivial public APIs" rule, adapted to C# XML-doc form.

## Function Design

**Size limit:** ≤100 lines/function, cyclomatic complexity ≤8 (hard limit per `CLAUDE.md`; not separately enforced by a Roslyn analyzer found in this repo — treat as a review-time discipline backed by the pre-commit `dotnet build -p:TreatWarningsAsErrors=true` + `dotnet format analyzers` gate).

**Parameters:** ≤5 positional params (hard limit per `CLAUDE.md`). No optional parameters anywhere — `CLAUDE.md`'s "No optional parameters" rule is explicit: "Make params required so the compiler enforces wiring." Confirmed pattern: helper methods like `Apply(AircraftState aircraft, string text)` and `TickPhase(AircraftState aircraft)` in `AerialRefuelingCommandTests.cs:42,52` take only required args.

**Return values:** Command-handling methods return typed result objects (`CommandResult`, `ParseResult<T>`) rather than tuples/nullable-and-throw hybrids — `parsed.IsSuccess`/`parsed.Reason`/`parsed.Value` and `result.Success`/`result.Message` in `AerialRefuelingCommandTests.cs:43-47,69`.

## Module Design

**Static classes with explicit `Initialize`/`SetInstance` entry points:** Data-backed singletons (`NavigationDatabase`, `AircraftProfileDatabase`, `AircraftCategorization`, `WakeTurbulenceData`, `FaaAircraftDatabase`) are static classes populated once via an explicit initializer rather than DI — consistent with `CLAUDE.md`'s "No DI: `MainWindow` creates `MainViewModel` directly" pattern extended into `Yaat.Sim`.

**No barrel files:** No `index.ts`-equivalent re-export files found; each namespace is referenced directly via its full `using`.

**DTOs are `record`/`sealed record` with no repurposed fields:** New wire fields get new, clearly-named properties; `CLAUDE.md` explicitly forbids repurposing an existing DTO field for a new meaning — remove dead fields entirely instead (confirmed by the DTO shapes in `src/Yaat.Client.Core/Services/ServerConnection.cs:950-1281`, which are flat, single-purpose positional records).

**Project-reference direction is a one-way DAG:** `Yaat.Client` → `Yaat.Client.Core` → `Yaat.Client.Strips`/`Yaat.Client.Tdls` → `Yaat.Sim`. New client types shared by desktop + web front-ends belong in `Yaat.Client.Core` (or `Strips`/`Tdls`), never in `Yaat.Client` — enforced by project references, not an analyzer, so violating it manifests as a compile error in the WASM front-ends.

**No repurposed optional args to avoid touching call sites:** every signature change is expected to update every call site (`CLAUDE.md` "Scrutinize optional arguments").

---

*Convention analysis: 2026-09-01*
