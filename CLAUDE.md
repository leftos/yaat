# CLAUDE.md

## Project Overview

YAAT (Yet Another ATC Trainer) — instructor/RPO desktop client for ATC training. Connects to [yaat-server](https://github.com/leftos/yaat-server) via SignalR; server feeds CRC via SignalR+MessagePack. Uses ATCTrainer scenarios directly (same JSON format via vNAS data-api).

## Build & Run

```bash
dotnet build                            # Build entire solution
dotnet run --project src/Yaat.Client    # Run client (needs yaat-server at localhost:5000)
dotnet run --project tools/Yaat.Scratch # Ad-hoc testing (throwaway console project referencing Yaat.Sim)
pwsh tools/test-all.ps1                 # Build + test both yaat and yaat-server
qodana scan --results-dir .tmp/qodana-results  # Static analysis (local only)
```

.NET 10 SDK required. Solution uses `.slnx` format (`yaat.slnx`). Close Yaat.Client before builds to avoid DLL lock warnings.

## Logs

Read log files first before speculating about runtime errors:
- **Client**: `%LOCALAPPDATA%/yaat/yaat-client.log`
- **Server**: `src/Yaat.Server/bin/Debug/net10.0/yaat-server.log` (relative to yaat-server repo root)

## Architecture

> **Full annotated file tree: [`docs/architecture.md`](docs/architecture.md).** Read it when you need to locate files or understand project structure. Keep it up-to-date before each commit.

Three projects across two repos. **Yaat.Sim** is shared by both Yaat.Client and yaat-server.

- **Yaat.Sim** (`src/Yaat.Sim/`) — Shared simulation library. All aviation logic: flight physics, phases, pattern geometry, performance constants, command dispatch, command queue. No UI deps.
- **Yaat.Client** (`src/Yaat.Client/`) — Avalonia desktop app. MVVM (CommunityToolkit.Mvvm), SkiaSharp rendering, SignalR JSON client.
- **yaat-server** (`..\yaat-server\`) — ASP.NET Core server. SignalR comms, CRC protocol, training rooms, scenario loading. References Yaat.Sim via sibling project ref.

### Key Patterns

- **SignalR**: JSON on `/hubs/training`; DTOs as records in `ServerConnection.cs`. Hub API signatures are in the code — read `ServerConnection.cs` (client) and `TrainingHub.cs` (server).
- **MVVM**: `[ObservableProperty]`/`[RelayCommand]`; `_camelCase` fields → `PascalCase` props
- **Threading**: SignalR callbacks → `Dispatcher.UIThread.Post()` to marshal to UI
- **NavigationDatabase**: Static singleton. `Initialize(navData)` at startup; tests use `SetInstance(db)`.
- **No DI**: `MainWindow` creates `MainViewModel` directly
- **Snapshots**: `SimulationWorld.GetSnapshot()` → shallow copy; treat as read-only

### Command Pipeline

1. User input → `MainViewModel.SendCommandAsync()` resolves callsign via partial match
2. `CommandSchemeParser.ParseCompound()`: `;` = sequential, `,` = parallel, `LV`/`AT` = conditions
3. Canonical format → server via `SendCommand(callsign, canonical)`
4. Server builds `CommandQueue` of `CommandBlock`s; `FlightPhysics.UpdateCommandQueue()` checks triggers each tick

**Track commands** (TRACK, DROP, HO, ACCEPT) bypass CommandDispatcher → TrackCommandHandler. `AS` prefix resolves RPO identity.

**Coordination commands** (RD, RDH, RDR, RDACK, RDAUTO) bypass CommandDispatcher → CoordinationCommandHandler. Channels from ARTCC config. Auto-expire 5min after ack.

### Command Rules

- Match ATCTrainer/VICE names where applicable. See `docs/command-aliases/reference.md`.
- **Completeness (MANDATORY):** Every `CanonicalCommandType` must exist in `CommandScheme.Default()` and `CommandMetadata.AllCommands`. Tests enforce this.
- **Altitude arguments:** Always use `AltitudeResolver.Resolve()` for parsing altitude arguments in commands. It handles both shorthand (`15` → 1500ft) and full (`1500` → 1500ft) formats, plus AGL notation (`KOAK+010`).

### Command Input UX

Two systems in `CommandInputController` — keep them in sync:
- **Autocomplete**: `ArgumentSuggester` → `UpdateSuggestions()` — dropdown value suggestions
- **Signature help**: `SignatureHelpState` + `CommandSignatureSet` → `UpdateSignatureHelp()` — inline parameter hints

Registry commands (`CommandRegistry`) get both automatically. **Special rewrite verbs** (e.g. `RWY`) need dedicated code paths in both systems.

## Tech Stack

.NET 10, C# (nullable, implicit usings) | Avalonia UI 11.2.5 (Fluent dark) | CommunityToolkit.Mvvm 8.4.0 | SignalR.Client 10.0.3

## Related Repositories

| Repo | Path | Purpose |
|------|------|---------|
| yaat-server | `..\yaat-server` | ASP.NET Core server, simulation engine, CRC protocol |
| vzoa | `X:\dev\vzoa` | vZOA training files; airport GeoJSON at `training-files/atctrainer-airport-files/` |
| vatsim-server-rs | `X:\dev\vatsim-server-rs` | CRC protocol reference (wire format, DTO ordering) — **read-only emulation**, use vNAS messaging-master for mutation-capable methods |
| lc-trainer | `X:\dev\lc-trainer` | Previous WPF trainer (**NOT trusted** — needs expert review) |
| vatsim-vnas | `X:\dev\vatsim-vnas` | vNAS source: common (GeoCalc), data (nav/scenarios), messaging (CRC DTOs — definitive reference) |

**vNAS APIs:** Config: `https://configuration.vnas.vatsim.net/` | Data: `https://data-api.vnas.vatsim.net/api/artccs/{id}`

## Reference Docs

- `docs/atctrainer-scenario-examples/` — Real ATCTrainer scenario JSONs (reference for scenario format)
- `docs/crc/` — CRC controller manual (STARS, Tower Cab, vStrips)
- `docs/vnas-artcc-config-examples/` — Real ARTCC config JSONs (facility hierarchy, positions, coordination channels)
- `../yaat-server/docs/crc-decompiled/` (gitignored) — ILSpy-decompiled CRC.dll. Key: `DisplayElementTracks.cs`, `ConsolidationManager.cs`, `Track.cs`, `TrackOwnerExtensions.cs`
- [`docs/scenario-validation.md`](docs/scenario-validation.md) — Validating scenario preset commands. Download via `tools/refresh-scenarios.py`, test via `VnasScenarioParseTests`.
- [`docs/discord-integration.md`](docs/discord-integration.md) — Discord bot and GitHub Actions workflows

## Aviation Realism — MANDATORY

**Every feature touching aviation must be reviewed by `aviation-sim-expert`** (via `Agent` with `subagent_type: "aviation-sim-expert"`). This is not optional.

**Scope:** flight physics, pilot AI, ATC logic, radio comms, aircraft performance, airspace rules, phase transitions, command dispatch, ground ops, conflict detection, trigger conditions, any automatic aircraft behavior.

**Do not guess aviation details.** Use FAA 7110.65, AIM, ICAO Doc 4444 as authorities.

**Local FAA references — DO NOT web-search:**
- **7110.65**: `.claude/reference/faa/7110.65/` (index: `INDEX.md`)
- **AIM**: `.claude/reference/faa/aim/` (index: `INDEX.md`)

When invoking aviation-sim-expert, always include:
> "IMPORTANT: The FAA 7110.65 and AIM are available as local markdown files in the repo. Read them directly via Read/Grep/Glob at `.claude/reference/faa/7110.65/` and `.claude/reference/faa/aim/`. Do NOT use web search tools to look up 7110.65 or AIM content."

## Recommended Agents

| Agent | When to use |
|-------|-------------|
| `aviation-sim-expert` | **Any aviation logic** (see above) |
| `csharp-developer` | Non-trivial C# in Yaat.Sim or Yaat.Client |
| `code-reviewer` | Before committing significant changes |
| `debugger` | Runtime failures, SignalR issues, phase state bugs |
| `test-automator` | Test fixtures for commands, phases, physics, geo |
| `refactoring-specialist` | Restructuring while preserving behavior |
| `architect-reviewer` | Design decisions: phases, command queue, DTOs, API surface |
| `performance-engineer` | Tick performance, broadcast throughput, conflict detection |
| `websocket-engineer` | SignalR lifecycle, CRC WebSocket protocol |
| `game-developer` | Sim loop design, tick-based updates |
| `documentation-engineer` | USER_GUIDE.md, scenario format, command reference |

## Rules

### Testing
- **TDD for bugs and sim changes**: Write the failing test first, confirm it fails, fix, confirm it passes. Applies to bug fixes, commands, physics, phases, navdata, parsers — anything in simulation-critical code. See `docs/e2e-tdd-issue-debugging.md`.
- **No guessing at root causes**: Reproduce with a test first. Use real airport layouts and E2E tests. Do not speculate.
- **No synthetic data in tests**: Use `TestVnasData.EnsureInitialized()` (loads real `NavData.dat` and `FAACIFP18.gz` from `tests/Yaat.Sim.Tests/TestData/`). Synthetic stubs hide integration problems. If test data files absent, silently skip.
- **Test access**: Making `internal` members `public` for tests is fine. No reflection or `InternalsVisibleTo` hacks.

### Code Style
- **Line width**: 150 chars (CSharpier configured accordingly)
- **Boolean expressions**: Parenthesize to disambiguate — `(a.X) || (b.Y >= c + d)` not `a.X || b.Y >= c + d`
- **No newlines in text strings**: Never split `Text="..."`, `Content="..."`, or interpolated strings across lines in `.axaml`/`.cs` — indentation whitespace shows at runtime.
- **No optional parameters**: Make params required so the compiler enforces wiring. Optional params hide missing integration.
- **No repurposing DTO fields**: Add new fields with clear names. Remove dead fields entirely.

### Error Handling
- Never swallow exceptions. Log with `AppLog` (client) or `ILogger` (Sim).
- Yaat.Sim static classes: `private static readonly ILogger Log = SimLog.CreateLogger("ClassName");` — never optional.

### Build & Format
- **Warnings are errors**: Build with `dotnet build -p:TreatWarningsAsErrors=true` before committing.
- **No `-q` flag**: Never pass `-q` to any dotnet command — causes spurious errors.
- **Pre-commit**: `dotnet format style` → `dotnet format analyzers` → `dotnet csharpier format .` → `dotnet build -p:TreatWarningsAsErrors=true`. Do NOT run bare `dotnet format`. Do NOT pass `-v q`, `--nologo`, or extra flags to `dotnet format`.

### Documentation
- Update `USER_GUIDE.md` before committing user-facing changes.
- Update `docs/yaat-vs-atctrainer.md` when commands/features/behavior vs ATCTrainer changes.
- Update `docs/architecture.md` before each commit.

### Git & Issues
- **Commits**: `fix:`/`feat:`/`add:`/`docs:`/`ref:`/`test:` etc. Imperative, ≤72 chars.
- **Cross-repo issues**: GitHub issues tracked on **yaat** repo. In yaat-server commits use full URL `Closes https://github.com/leftos/yaat/issues/N`, never bare `Closes #N`.
- **Cross-repo completeness**: Features spanning both repos must be implemented together — no half-done features.
- **Issue plans**: Write plans to `docs/plans/open-issues/`. Delete plan file after implementing.
- **Milestone Roadmap**: `docs/plans/main-plan.md`. M0/M1 complete; M2 (tower ops) next.

### Misc
- **Unreleased software**: No backwards-compat shims, migration paths, or deprecated aliases. Delete and replace freely.
- **Window geometry**: Every window uses `WindowGeometryHelper(window, preferences, "Name", defaultW, defaultH).Restore()`.
- **Memory Updates**: Distill findings into auto-memory at `C:\Users\Leftos\.claude\projects\X--dev-yaat\memory\`.

