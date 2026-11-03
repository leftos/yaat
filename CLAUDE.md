# CLAUDE.md

## Project Overview

**YAAT (Yet Another ATC Trainer)** — the instructor/RPO desktop client for VATSIM air traffic control training; `yaat-server` hosts the rooms and speaks CRC's wire protocol. Product framing lives in `README.md`.

### Constraints

- **Tech stack**: .NET 10, C# (nullable, implicit usings), Avalonia UI 12.1.0, CommunityToolkit.Mvvm, SignalR, MessagePack for CRC — `Yaat.Sim` has no UI dependencies; SkiaSharp arrives transitively via `Avalonia.Skia`, never as a direct package reference
- **Aviation accuracy**: every change touching aviation logic is reviewed by `aviation-sim-expert` against FAA 7110.65 / AIM, read from the local references — never web search
- **TDD covers sim changes too**: commands, physics, phases, navdata and parsers get a failing test first like a bug fix (the `test-fix` skill; worked examples in `docs/e2e-tdd-issue-debugging.md`), on real navdata, never synthetic stubs
- **Determinism**: recordings must replay exactly; AI brains never run in replay, and the same seed reproduces byte-for-byte
- **Cross-repo verification**: `pwsh tools/test-all.ps1` builds and tests both repos; a `Yaat.Sim` signature change that breaks the sibling is invisible to a bare `dotnet test`. Run it bare where you'd otherwise run the whole suite: it re-runs itself once under one heavy gate (900 s, 2700 s with `-Full`; log `.tmp/test-all.log`), so never wrap it in `tools/gate.ps1` yourself. Before renaming or re-signing a public `Yaat.Sim` member, `rg` its name under `../yaat-server` and make the server edits by hand.

Gate: run every dotnet build/test/run as pwsh tools/gate.ps1 -Log .tmp/<name>.log -TimeoutSeconds <n> -Slot heavy -- <cmd> (30 filtered test, 120 full test, 300 build; test-all.ps1 is called bare and gates itself); verdicts in ~/.claude/docs/gate.md.

A gated command for a worktree runs with `--cd <worktree root>` and paths relative to it (`pwsh tools/gate.ps1 run --cd X:/dev/yaat.wt/<name>/yaat --log .tmp/<log>.log --timeout <n> --slot heavy -- <command>`), never from the main checkout naming the worktree's paths, or it is not routed and runs on this PC.

## Build & Run

```bash
dotnet workload restore                                 # Once per clone: installs wasm-tools (the .Web projects need it)
dotnet build                                            # Build entire solution
dotnet run --project src/Yaat.Client                    # Run client (needs yaat-server; see the port note below)
dotnet run --project tools/Yaat.GuideCapture            # Regenerate USER_GUIDE.md screenshots into docs/user-guide/img/ (or `-- --scene <name>`)
pwsh tools/test-all.ps1                                 # Build + test both yaat and yaat-server, self-gated (excludes Nightly + PathfinderGrid sweeps for speed)
pwsh tools/test-all.ps1 -Full                           # ...including the heavy Nightly + PathfinderGrid sweeps (CI/nightly run these)
qodana scan --results-dir .tmp/qodana-results           # Static analysis (local only)
```

.NET 10 SDK required. Solution uses `.slnx` format (`yaat.slnx`). Close Yaat.Client before builds to avoid DLL lock warnings.

**`wasm-tools` is a build prerequisite.** `yaat.slnx` includes `tools/Yaat.VStrips.Web` and `tools/Yaat.VTdls.Web` (Avalonia Browser/WASM), so a solution-wide `dotnet build` fails on a fresh clone until `dotnet workload restore` installs the workload.

**Server port: `dotnet run --project src/Yaat.Server` binds `http://localhost:5130`,** not 5000. 5130 comes from the first `launchSettings.json` profile (which also sets `ASPNETCORE_ENVIRONMENT=Development`); 5000 is only the Docker/production bind. The client's auto-connect default is still `http://localhost:5000`, so point it at 5130 when running the server from source.

## Logs

Read log files first before speculating about runtime errors:

- **Client**: `%LOCALAPPDATA%/yaat/yaat-client.log`
- **Server**: `src/Yaat.Server/bin/Debug/net10.0/yaat-server.log` (relative to yaat-server repo root)

## Architecture

> **Full annotated file tree: [`docs/architecture.md`](docs/architecture.md).** Read it when you need to locate files or understand project structure. Keep it up-to-date before each commit (the `architecture-updater` agent checks it).
>
> **Exploring the codebase?** Start from the docs map ([`docs/README.md`](docs/README.md) → `architecture.md` Task Index → the matching subsystem doc), not source from scratch. When delegating exploration to a subagent, use the user-level `Explore` agent (it follows this docs-first protocol automatically); otherwise put "read `docs/architecture.md` + the relevant `docs/*.md` first" in the agent's prompt.

Subsystem docs: before exploring or editing a subsystem, open its doc from [docs/README.md](docs/README.md) (Subsystem docs, Reference material, Reference repos), then the docs/architecture.md Task Index.

**Yaat.Sim** is shared by every client project and by yaat-server.

- **Yaat.Sim** (`src/Yaat.Sim/`) — Shared simulation library. All aviation logic: flight physics, phases, pattern geometry, performance constants, command dispatch, command queue. No UI deps.
- **Yaat.Client** (`src/Yaat.Client/`) — Avalonia desktop app: radar/ground rendering, `MainViewModel`, command input, the windows the instructor drives. MVVM (CommunityToolkit.Mvvm), SkiaSharp rendering. **Sole owner of the LM-Kit.NET dependency** (speech) — nothing else in `src/` may reference it.
- **Yaat.Client.Core** (`src/Yaat.Client.Core/`) — LM-Kit-free client core: `ServerConnection` (SignalR + DTOs), `UserPreferences`, `AppLog`, `ClientVersionGate`, `UpdateService`, CRC alias parsing, `ConnectViewModel`. Split out so the browser/WASM front-ends and the desktop app share it.
- **Yaat.Client.Strips** (`src/Yaat.Client.Strips/`) — vStrips UI (strip bays, find, printing).
- **Yaat.Client.Tdls** (`src/Yaat.Client.Tdls/`) — vTDLS UI; references Yaat.Client.Strips.
- **Yaat.VStrips.Web / Yaat.VTdls.Web** (`tools/`) — Avalonia Browser (WASM) front-ends over Strips/Tdls. **yaat-server builds and hosts these** from its `src/Yaat.Server/Dockerfile` via `extern/yaat/`; they are why `wasm-tools` is a build prerequisite here.
- **yaat-server** (`..\yaat-server\`) — ASP.NET Core server. SignalR comms, CRC protocol, training rooms, scenario loading. References Yaat.Sim via sibling project ref.

Project-reference direction: `Yaat.Client` → `Yaat.Client.Core` → `Yaat.Client.Strips`/`Yaat.Client.Tdls` → `Yaat.Sim`. Adding a client type used by both the desktop app and a web front-end means putting it in Core (or Strips/Tdls), not `Yaat.Client`.

### Key Patterns

- **SignalR**: JSON on `/hubs/training`; DTOs as records in `src/Yaat.Client.Core/Services/ServerConnection.cs`. Hub API signatures are in the code — read `ServerConnection.cs` (client) and `TrainingHub.cs` (server).
- **MVVM**: `[ObservableProperty]`/`[RelayCommand]`; `_camelCase` fields → `PascalCase` props
- **Threading**: SignalR callbacks → `Dispatcher.UIThread.Post()` to marshal to UI
- **NavigationDatabase**: Static singleton. `Initialize(navData)` at startup; tests get the real one from the lazy `SetTestDefaultLoader` and install another only through `ScopedOverride`.
- **No DI**: `MainWindow` creates `MainViewModel` directly
- **Snapshots**: `SimulationWorld.GetSnapshot()` → shallow copy; treat as read-only

## Aviation Realism — MANDATORY

**Every feature touching aviation must be reviewed by `aviation-sim-expert`** (via `Agent` with `subagent_type: "aviation-sim-expert"`, through the `aviation-review-gate` skill, which carries the standard local-references preamble). This is not optional.

**Scope:** flight physics, pilot AI, ATC logic, radio comms, aircraft performance, airspace rules, phase transitions, command dispatch, ground ops, conflict detection, trigger conditions, any automatic aircraft behavior.

**Do not guess aviation details.** Use FAA 7110.65, AIM, ICAO Doc 4444 as authorities.

Aviation constants: validated category values live in docs/flight-physics.md (CategoryPerformance); a change to one goes through the aviation-review-gate skill.

**Local FAA references — DO NOT web-search:**

- **7110.65**: `.claude/reference/faa/7110.65/` (index: `INDEX.md`)
- **AIM**: `.claude/reference/faa/aim/` (index: `INDEX.md`)
- **Semantic search**: the main checkout's `.claude/reference/faa/` is indexed by ck (`mcp__ck__semantic_search` / `mcp__ck__hybrid_search` with `path` set to that folder in the main checkout, not a worktree copy, which would start a fresh index); a hit is a pointer, confirmed by reading the paragraph before it is cited.

## Project Skills

**Prefer the skill over ad-hoc CLI**: `layout-inspect` (never compose LayoutInspector flags from memory), `bug-bundle` (any `*-recording.zip` / `*.yaat-bug-report-bundle.zip`, before running any `tools/bug_bundle.py` subcommand), `stt-pipeline-debugging` (any speech sample or "why doesn't this transcript match"), `guide-capture` (any GuideCapture screenshot scene).

Landing (cross-repo cherry-pick, hooks exception, push, issue refs): /ship with the yaat-nextup profile.

## Problem Solving

- **Revert what made things worse, keep what is correct**: When a fix attempt breaks tests or degrades behavior, revert it and try a different approach; never iterate on a broken approach more than twice without stepping back to reassess. Do not revert a fix that is correct just because an unrelated test is red, a replay recording desynced, or the user sounds unsure — verify first, then keep it and fix the actual cause.

## Rules

### Testing

Tests: writing or debugging one, read tests/CLAUDE.md and docs/test-harness.md first.

- **No synthetic data in tests**: Use `TestVnasData.EnsureInitialized()`. Tests pin the committed `tests/Yaat.Sim.Tests/TestData/NavData.dat` and `FAACIFP18.gz` (`PinToBundled` on the resolver options; `tests/Yaat.Sim.Tests/ModuleInit.cs` resolves them once per test process at assembly load), never the user cache or the network, whatever the machine holds. The supplementary prior-cycle chain uses committed files only.

  **NavData and CIFP are hard preconditions: `ModuleInit` throws if it cannot resolve either** — most of the suite asserts against real navdata and procedures, so a run without them is green having proved nothing. Synthetic stubs hide integration problems. Other missing test data (layouts, recordings, ARTCC configs) still silently skips. Resolution order and the repin steps: `docs/test-harness.md` and `tests/CLAUDE.md`.
- **Test runner CLI (Microsoft.Testing.Platform)**: `global.json` selects the MTP runner, so `dotnet test` runs the test assemblies concurrently and takes runner options after a `--` separator: `dotnet test -- --filter-method "*Name*"` (substring of `Namespace.Class.Method`), `--filter-class "*.ClassName"`, `--filter-trait "Category=Nightly"` / `--filter-not-trait ...`, `--report-xunit-trx`.

  The VSTest forms (`--filter "FullyQualifiedName~X"`, `--logger trx`) are rejected with `Unknown option`. Test output: **SimLog in tests** in `tests/CLAUDE.md`.

### Code Style

- **Style is a build gate**: `Directory.Build.props` sets `EnforceCodeStyleInBuild` + `TreatWarningsAsErrors`, so a `warning`-level `.editorconfig` rule breaks the build; CI also fails on info-level style findings and on `dotnet csharpier check .`. CSharpier formats `.cs` and `.axaml`.
- **Boolean expressions**: Parenthesize to disambiguate — `(a.X) || (b.Y >= c + d)` not `a.X || b.Y >= c + d`
- **No repurposing DTO fields**: Add new fields with clear names. Remove dead fields entirely.
- **Per-user paths via `YaatPaths`**: every `%LOCALAPPDATA%/yaat` path routes through `YaatPaths.AppDataRoot` / `YaatPaths.Combine(...)` (in `Yaat.Sim`), never raw `Environment.GetFolderPath`. Test-project consequences are in `tests/CLAUDE.md`.

### Debugging

- **Add logging freely**: When investigating a bug, always add `Log.LogDebug` statements to trace execution rather than guessing from the code. This is always allowed and encouraged — logging is the primary debugging tool for graph/geometry issues.

### Error Handling

- Yaat.Sim static classes: `private static readonly ILogger Log = SimLog.CreateLogger("ClassName");` — never optional.

### Build & Format

- **No `-q` flag**: Never pass `-q` to any dotnet command — causes spurious errors.
- **prek**: commit hooks run only the checks scoped to the staged files; the pre-push stage runs the gated `dotnet build` itself, so a `git push` runs bare. Run bare `prek run` to check; never `prek run --all-files`, whose stash/reapply can drop unstaged work. Do NOT run bare `dotnet format`, and do NOT pass `-v q`, `--nologo`, or extra flags to it.

### Documentation

- Update `USER_GUIDE.md` before committing user-facing changes.

### Git & Issues

- **Commit directly to `main`, feature branches by marker**: the sole maintainer commits and pushes directly to `main` in both yaat and yaat-server. Work under a feature marker (`branch: feat/<name>` in a Linear project's content) lives on `feat/<name>` in both repos with a feature PR into `main` each, by the user-level route (`nextup` §3 "Feature branches", `/ship` Phase 2F). Branches and PRs outside a marker only when the user asks.
- **Cross-repo completeness**: Features spanning both repos must be implemented together — no half-done features.
- **Plans**: Linear team YAAT, which also plans yaat-server; the active programme is YAAT Scope ([`docs/plans/yaat-scope/README.md`](docs/plans/yaat-scope/README.md)), then controller AI ([`docs/plans/controller-ai/README.md`](docs/plans/controller-ai/README.md)).
