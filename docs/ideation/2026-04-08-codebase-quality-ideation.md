---
date: 2026-04-08
topic: codebase-quality
focus: codebase structure and quality evaluation for development and debugging
---

# Ideation: Codebase Structure & Quality

## Codebase Context

YAAT is a .NET 10 ATC trainer desktop app (Avalonia UI, SkiaSharp rendering, SignalR). Three projects: Yaat.Sim (shared simulation engine, no UI deps), Yaat.Client (Avalonia desktop UI), yaat-server (separate repo). ~2700+ tests across repos. Single developer, unreleased software, no backwards-compat needed. M2 (tower ops) is next milestone.

Key patterns and pain points:
- MVVM via CommunityToolkit.Mvvm, no DI container
- Two command input systems (autocomplete + signature help) must stay in sync manually; special rewrite verbs need dedicated code paths in both
- Two command registries (CommandScheme.Default() + CommandMetadata.AllCommands) must stay in sync; tests enforce parity
- Pre-commit is a 4-step manual chain: dotnet format style, analyzers, csharpier, build with TreatWarningsAsErrors
- docs/architecture.md must be manually updated before each commit
- Static singletons (NavigationDatabase, SimLog) used throughout Yaat.Sim
- ScenarioValidator validates syntax only, not runtime behavior
- LayoutInspector is a powerful debugging tool but doesn't assert anything
- Yaat.Sim is fully decoupled from UI, but no CLI runner exists for headless sim execution

## Ranked Ideas

### 1. Pre-commit hook via prek
**Description:** Wire the existing 4-step pre-commit chain (`dotnet format style` -> `dotnet format analyzers` -> `dotnet csharpier format .` -> `dotnet build -p:TreatWarningsAsErrors=true`) into an actual git hook using `prek`, which is already recommended in the global CLAUDE.md but not configured in this repo.
**Rationale:** 15 minutes of setup, permanent value. Turns a memory-dependent process into an enforced gate. Both adversarial critics unanimously kept this.
**Downsides:** Slightly slower commits (build runs every time). Can be bypassed with --no-verify if needed.
**Confidence:** 95%
**Complexity:** Low
**Status:** Explored — implemented 2026-04-08

### 2. Unify CommandScheme + CommandMetadata into single registry
**Description:** Merge the two command registries that must stay in sync into one. Tests currently enforce parity -- the existence of those tests proves the duplication causes real drift. A single registry eliminates the failure mode entirely and deletes the sync-enforcement tests.
**Rationale:** Small, bounded refactor. Every new command currently touches two registries. Reduces cognitive load and removes a class of "forgot to register" bugs.
**Downsides:** One-time migration effort. Need to decide which registry's shape wins.
**Confidence:** 80%
**Complexity:** Medium
**Status:** Explored — deprioritized 2026-04-08. CommandScheme.Default() already derives from CommandRegistry.All via .ToDictionary(); they can't drift. The parity tests are a safety net, not evidence of real drift. Adding a new command only touches CommandRegistry.Build(). ROI of flattening is modest (~80 lines deleted, ~8 files touched) for marginal ongoing benefit. The real maintenance hazard is #3 (dual command input pipeline), not this.

### 3. Unify dual command input pipeline
**Description:** Merge autocomplete (ArgumentSuggester + UpdateSuggestions) and signature help (SignatureHelpState + UpdateSignatureHelp) into a single parse-once, render-twice architecture. Both systems parse the same command text independently; special rewrite verbs need dedicated code paths in both.
**Rationale:** 4/6 ideation agents flagged this. CLAUDE.md itself documents it as a maintenance hazard. Every new command or verb doubles integration work.
**Downsides:** Medium-large refactor touching the entire command input path. Risk of introducing input UX regressions.
**Confidence:** 75%
**Complexity:** Medium-High
**Status:** Explored — brainstormed 2026-04-08. Design: parse-once, render-twice via shared CommandInputParseResult record carrying resolved CommandDefinition. Add RWY to CommandRegistry as proper entry (two overloads) to eliminate all hardcoded RWY special cases. Plan at docs/plans/open-issues/unify-command-input-pipeline.md.

### 4. Headless simulation runner
**Description:** Build a CLI tool that loads a scenario, executes a script of timed commands, and outputs structured state logs -- no Avalonia required. Foundation for CI regression tests, benchmarking, and batch validation.
**Rationale:** Yaat.Sim is already fully decoupled from UI. Yaat.Scratch exists as an ad-hoc console project but isn't structured for repeatable runs. Unlocks automated scenario smoke tests in CI.
**Downsides:** Needs to replicate enough of the server's scenario loading and tick loop to be useful. Scope creep risk.
**Confidence:** 80%
**Complexity:** Medium
**Status:** Unexplored

### 5. Airport layout test harness from LayoutInspector
**Description:** Promote LayoutInspector from a manual debugging tool to a test fixture generator. Given a GeoJSON airport, auto-generate tests asserting: all runways have exits, exits reach taxiways, A* pathfinding succeeds between runway-to-parking pairs, fillet arcs produce valid geometry.
**Rationale:** The tool already does all the queries -- it just doesn't assert anything. Ground topology bugs are geometry-sensitive and hard to catch without automated coverage.
**Downsides:** Generated tests may be brittle to GeoJSON changes. Need to choose which airports to cover.
**Confidence:** 75%
**Complexity:** Low-Medium
**Status:** Unexplored

### 6. Batch sim-accelerated scenario validation
**Description:** Run downloaded vNAS scenarios through the sim at 10-50x speed, collecting: command parse failures, phase errors, stuck aircraft, pathfinding failures. Produce per-scenario health reports. Depends on #4 (headless runner).
**Rationale:** Current ScenarioValidator validates syntax only. Many bugs only manifest at runtime (SID that doesn't connect, ground path that dead-ends).
**Downsides:** Requires #4 first. Slow to run across many scenarios. False positives from incomplete scenario data.
**Confidence:** 65%
**Complexity:** Medium (after #4 exists)
**Status:** Unexplored

## Rejection Summary

| # | Idea | Reason Rejected |
|---|------|-----------------|
| 1 | Auto-generate architecture.md | Marginal gain; manual update takes 2 min; generated version loses intent |
| 2 | Source-generate command dispatchers | Roslyn source gen is high-complexity infra for single dev |
| 3 | Replace static singletons (standalone) | Massive refactor; 2700 tests pass today; do when needed |
| 4 | Shared DTO contracts assembly | Protocol not stable; adds versioning overhead |
| 5 | Phase-command unification | Phases and commands have fundamentally different lifecycles |
| 6 | Session debrief/scoring | Feature work, premature before core sim complete |
| 7 | Predictive separation advisor | Full project; high false-positive risk; premature |
| 8 | Offline single-user mode | Not interested at the moment (user decision) |
| 9 | Multi-airport ground layouts | Build when second airport needed |
| 10 | Deterministic replay diffing | Needs headless runner first; current tests suffice |
| 11 | Scenario fuzzer | Targeted tests beat random fuzzing for structured command space |
| 12 | Command coverage matrix | Tests already enforce completeness |
| 13 | Hot-reload sim parameters | Rebuild takes seconds; runtime-editable constants can be wrong |
| 14 | Taxi pathfinder caching | Profile first; A* on airport graphs is microseconds |
| 15 | Wind interpolation caching | Profile first; unlikely bottleneck |
| 16 | Command trigger short-circuiting | Profile first; tens of aircraft, not thousands |
| 17 | Ground view render culling | Profile first; fix when frame drops observed |
| 18 | Snapshot delta compression | Premature optimization |
| 19 | Recording archive streaming | Profile first |
| 20 | Concurrent room ticking | Profile first; do when multi-room needs it |
| 21 | Embed GeoJSON in test project | Already partially done; low impact |
| 22 | Scenario-embedded macros | Feature work, not quality/structure |
| 23 | ATIS generation | Feature work |
| 24 | Command replay drill mode | Feature work |
| 25 | Observer/replay sharing | Feature work |
| 26 | FAA reference auto-linker | Low ROI |
| 27 | ServerConnection typed DTOs | Covered by shared contracts (also cut) |
| 28 | Smart status bar | Already partially exists |
| 29 | Command input test harness | Addressed by unifying the systems (#3) |

## Session Log
- 2026-04-08: Initial ideation -- 47 raw ideas from 6 agents, 3 cross-cutting syntheses, 2 adversarial critics. 6 survivors after filtering. User removed offline mode and predictive advisor.
