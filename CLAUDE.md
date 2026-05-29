# CLAUDE.md

## Project Overview

YAAT (Yet Another ATC Trainer) — instructor/RPO desktop client for ATC training. Connects to [yaat-server](https://github.com/leftos/yaat-server) via SignalR; server feeds CRC via SignalR+MessagePack. Uses ATCTrainer scenarios directly (same JSON format via vNAS data-api).

## Build & Run

```bash
dotnet build                                            # Build entire solution
dotnet run --project src/Yaat.Client                    # Run client (needs yaat-server at localhost:5000)
dotnet run --project tools/Yaat.Scratch                 # Ad-hoc throwaway scratchpad (intentionally empty placeholder)
dotnet run --project tools/Yaat.SpeechSandbox           # Speech sandbox GUI (or `-- --pipeline <wav>`, `--lmkit-stt`, `--lmkit-models`, `--lmkit-gpus`, `--yaat-catalog`, `--llm-probe`)
dotnet run --project tools/Yaat.GuideCapture            # Regenerate USER_GUIDE.md screenshots into docs/user-guide/img/ (or `-- --scene <name>`)
pwsh tools/test-all.ps1                                 # Build + test both yaat and yaat-server
qodana scan --results-dir .tmp/qodana-results           # Static analysis (local only)
```

.NET 10 SDK required. Solution uses `.slnx` format (`yaat.slnx`). Close Yaat.Client before builds to avoid DLL lock warnings.

## Layout Inspector Tool

`tools/Yaat.LayoutInspector/` loads an airport GeoJSON and queries the ground graph, renders interactive HTML maps, and analyzes per-tick CSVs from `TickRecorder`. Use it to understand airport topology when debugging ground/exit/taxi bugs, and to inspect aircraft trajectories after a failing test writes a tick CSV.

```bash
# Graph queries
dotnet run --project tools/Yaat.LayoutInspector -- <geojson-path> [--node N] [--taxiway T] [--runway 28R] [--exits 28R] [--bfs N T] [--parking] [--spots] [--json] [--dump]

# Interactive HTML with optional tick overlay
dotnet run --project tools/Yaat.LayoutInspector -- <geojson-path> --html .tmp/out.html [--ticks .tmp/rollout.csv] [--html-taxiway T] [--html-runway 28R]

# Tick-table text analysis (absorbed from the old Yaat.TickInspector)
dotnet run --project tools/Yaat.LayoutInspector -- <geojson-path> --ticks .tmp/rollout.csv --tick-table --tick-ref SFO/28L --tick-hold-shorts K,D,Q
```

Key use cases: trace multi-hop exit paths (`--bfs 230 T`), find all exits from a runway (`--exits 28R`), inspect node connectivity (`--node N`), verify hold-short runway IDs (`--runway 28R`), dump entire airport to JSON for grepping (`--dump > .tmp/oak.json`), inspect a recorded aircraft trajectory against a runway reference (`--tick-table --tick-ref`). See `docs/e2e-tdd-issue-debugging.md` for detailed examples.

## Bug Bundle Tool

`tools/bug_bundle.py` inspects, extracts, installs, and validates v4 bug bundles (`*.yaat-bug-report-bundle.zip`, `*-recording.zip`). Requires `brotli` (`pip install brotli`). Use it whenever a bundle is attached to an issue — faster than writing throwaway C# or unzipping by hand.

```bash
python tools/bug_bundle.py info <bundle.zip>                           # manifest summary + aircraft at t=0
python tools/bug_bundle.py snapshot <bundle.zip> --at 182              # snapshot nearest to t=182s
python tools/bug_bundle.py snapshot <bundle.zip> --at 182 --callsign X # filter to one aircraft
python tools/bug_bundle.py track <bundle.zip> --pair N436MS N9225L --start 115 --end 220 # time-series + gap/runaway timer between two callsigns
python tools/bug_bundle.py actions <bundle.zip>                        # timeline of user actions
python tools/bug_bundle.py history <bundle.zip> --callsign N42416      # per-aircraft chronology: commands + phase / route / target / approach changes
python tools/bug_bundle.py phases <bundle.zip> --callsign N9225L       # phase-transition timeline only
python tools/bug_bundle.py commands <bundle.zip> --callsign N42416     # actions filtered to one recipient callsign
python tools/bug_bundle.py logs <bundle.zip>                           # extract yaat-client/server logs to .tmp/
python tools/bug_bundle.py scenario <bundle.zip> --show summary        # one-line summary table of all aircraft (callsign/type/start/presets)
python tools/bug_bundle.py scenario <bundle.zip> --aircraft N346G --show presets   # preset commands for one aircraft
python tools/bug_bundle.py scenario <bundle.zip> --aircraft N346G --show spawns    # starting conditions (parking/fix/etc) for one aircraft
python tools/bug_bundle.py install <local.zip> --issue 134 --desc slug # copy to TestData/ with naming convention
python tools/bug_bundle.py install --issue 134 --desc slug             # fetch from GitHub issue via gh
python tools/bug_bundle.py validate <bundle.zip>                       # manifest + decompression integrity
```

For single-aircraft triage start with `history --callsign X` — it replaces 5+ targeted `snapshot --at` calls with one chronological view.

When a `.yaat-bug-report-bundle.zip` or `*-recording.zip` path appears in user input — or any other bundle-shaped filename — invoke the `bug-bundle` skill before running any `bug_bundle.py` subcommand. The skill carries the full reference and keeps you from re-deriving subcommand syntax from memory.

Full subcommand list: `info`, `snapshot`, `track`, `actions`, `history`, `phases`, `commands`, `scenario`, `weather`, `layouts`, `logs`, `trim`, `install`, `validate`. See `.claude/skills/bug-bundle/SKILL.md` for the full reference.

**Extend the tools when you find yourself doing repeat custom work.** If you write more than two ad-hoc Python or C# snippets that pull the same kind of data from a bundle, layout, or snapshot — coordinate position vs runway centerline, list of all exits with current occupancy, two-aircraft positional comparison over a snapshot range — turn that snippet into a subcommand of `tools/bug_bundle.py` or `tools/Yaat.LayoutInspector/` first, then use it. The next agent will have the same investigation; bake the lookup into the tool rather than re-deriving it. Keep custom snippets only for genuinely one-off questions.

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
- **Completeness (MANDATORY):** Every `CanonicalCommandType` must exist in `CommandScheme.Default()` and `CommandRegistry.All`. Tests enforce this.
- **Altitude arguments:** Always use `AltitudeResolver.Resolve()` for parsing altitude arguments in commands. It handles both shorthand (`15` → 1500ft) and full (`1500` → 1500ft) formats, plus AGL notation (`KOAK+010`).

### Command Input UX

Unified parse-once architecture in `CommandInputController`:
- `ParseCommandInput()` produces a `CommandInputParseResult` (verb, definition, param index, typed args)
- **Autocomplete**: `ArgumentSuggester` consumes the parse result → dropdown value suggestions
- **Signature help**: `SignatureHelpState` consumes the parse result → inline parameter hints

All commands including rewrite verbs (e.g. `RWY`) go through `CommandRegistry` — no special-case code paths needed.

## Tech Stack

.NET 10, C# (nullable, implicit usings) | Avalonia UI 11.2.5 (Fluent dark) | CommunityToolkit.Mvvm 8.4.0 | SignalR.Client 10.0.3

## Related Repositories

| Repo | Path | Purpose |
|------|------|---------|
| yaat-server | `..\yaat-server` | ASP.NET Core server, simulation engine, CRC protocol |
| vzoa | `..\vzoa` | vZOA training files (auxiliary). Airport ground GeoJSONs are fetched from the vNAS data-api by `AirportLayoutDownloader`; do not read the sibling repo. |
| vatsim-server-rs | `..\vatsim-server-rs` | CRC protocol reference (wire format, DTO ordering) — **read-only emulation**, use vNAS messaging-master for mutation-capable methods |
| lc-trainer | `..\lc-trainer` | Previous WPF trainer (**NOT trusted** — needs expert review) |
| vatsim-vnas | `..\vatsim-vnas` | vNAS source: common (GeoCalc), data (nav/scenarios), messaging (CRC DTOs — definitive reference) |

**vNAS APIs:** Config: `https://configuration.vnas.vatsim.net/` | Data: `https://data-api.vnas.vatsim.net/api/artccs/{id}` | Airport ground map: `https://data-api.vnas.vatsim.net/api/training/airports/{FAA}/map` (used by `AirportLayoutDownloader`, cached at `%LOCALAPPDATA%/yaat/cache/airports/`)

## Reference Docs

- `docs/atctrainer-scenario-examples/` — Real ATCTrainer scenario JSONs (reference for scenario format)
- `docs/crc/` — CRC controller manual (STARS, Tower Cab, vStrips)
- `docs/vnas-artcc-config-examples/` — Real ARTCC config JSONs (facility hierarchy, positions, coordination channels)
- `../yaat-server/docs/crc-decompiled/` (gitignored) — ILSpy-decompiled CRC.dll. Key: `DisplayElementTracks.cs`, `ConsolidationManager.cs`, `Track.cs`, `TrackOwnerExtensions.cs`
- [`docs/scenario-validation.md`](docs/scenario-validation.md) — Validating scenario preset commands. Download via `tools/refresh-scenarios.py`, test via `VnasScenarioParseTests`.
- [`docs/discord-integration.md`](docs/discord-integration.md) — Discord bot and GitHub Actions workflows
- [`docs/landing-and-runway-exit.md`](docs/landing-and-runway-exit.md) — **Landing rollout & runway exit design**. Read before touching LandingPhase, RunwayExitPhase, or GroundNavigator. Covers the analog (non-node-based) approach, braking strategy, virtual segments, and anti-patterns.
- [`docs/ground/`](docs/ground/README.md) — **Ground stack design & architecture** (the three taxi layers; index + V1→V2 transition). Read the relevant layer doc before touching it: [`fillet-generator.md`](docs/ground/fillet-generator.md) (graph geometry — `FilletArcGeneratorV2`, `Data/Airport/Fillet/V2/*`; plan-then-execute, edge-split, `CutId`/`FilletEndpoint`, corner-chord gotcha), [`pathfinder.md`](docs/ground/pathfinder.md) (routing — `TaxiPathfinderV2`, `Data/Airport/V2/*`; explicit vs auto mode, admissibility, look-ahead, membership arcs, mandatory-connector insertion), [`navigator.md`](docs/ground/navigator.md) (per-tick following — `GroundNavigator`; closed-form arc playback, pure-pursuit, slow-turn synthesis, Legacy-fillet compensations under V2 review). Docs describe **V2 as the architecture**; V1 is the runtime default being removed at the joint flip.
- [`docs/speech-recognition-pipeline.md`](docs/speech-recognition-pipeline.md) — **Push-to-talk speech recognition reference**. Read before touching anything under `src/Yaat.Sim/Speech/`, `WhisperSttEngine`, `LocalLlm*`, `SpeechRecognitionService`, or `tools/Yaat.SpeechSandbox`. Covers audio capture, LM-Kit Whisper, biasing prompt, rule mapper, LLM fallback, and current ground-ops coverage.
- [`docs/solo-training-pilot-speech.md`](docs/solo-training-pilot-speech.md) — **Solo-training pilot speech pipeline reference** (TTS output side). Read before touching `src/Yaat.Sim/Pilot/`, `AircraftState.PendingPilotTransmissions`, `SimulationWorld.ActiveFrequency`, or `src/Yaat.Client/Services/PilotVoiceService.cs`. Covers the `PilotTransmission` typed queue, frequency airtime serialization, awaited-readback priority, `PilotSpeechText` dual output, `ProducesPilotUnable` routing, `PilotRequestTracker` reminders, and Piper voice install.
- [`docs/flight-strips.md`](docs/flight-strips.md) — **Flight strips end-to-end reference**. Read before touching `FlightStripState`, `StripItemDto`, `CrcClientState.Strips.cs`, `HandleStripCmd`/`HandleHalfStrip*` in `RoomEngine`, `GetAccessibleStripBay`, or any strip-related command in `src/Yaat.Sim/Commands/`. Covers the server state model, bay resolution (including the tower-vs-TRACON facility gotcha), `STRIP`/`AN`/`HSC`/`HSA`/`HSD` commands, half-strip dual-mode operation, CRC topic broadcast, and known gaps.
- [`docs/phases.md`](docs/phases.md) — **Phase system reference**. Read before touching anything under `src/Yaat.Sim/Phases/`. Covers the base contract (`OnStart`/`OnTick`/`CanAcceptCommand`/`ManagesSpeed`), `CommandAcceptance` semantics (Allowed/Rejected/ClearsPhase), `PhaseRunner` lifecycle and auto-append rules (LAHSO, post-landing exits, pattern auto-cycle), `PhaseList` persistent metadata, the four-step contract for adding a snapshotted phase, and pitfalls (two GoAround construction sites, `ManagesSpeed` contagion, dry-run-then-clear).
- [`docs/snapshots-and-replay.md`](docs/snapshots-and-replay.md) — **Snapshots, recording, and replay reference**. Read before touching `Simulation/Snapshots/`, `Simulation/Replay/`, `RecordingArchive`, `SnapshotSchemaMigrator`, or debugging a bug bundle. Covers the DTO tree, schema migrator versions, `[JsonIgnore]` runtime-only fields (`Ground.Layout`, `DeclinationCachePosition`), recording archive layout, the replay surface (`ReplayFromStartTo` / `FastForwardTo` / `ReplayRange` / `ReplayOneSecond`, `SnapshotDiff`, `ReplayTrackApplier`), bug-bundle structure, and the looped-from-scratch footgun.
- [`docs/tick-loop.md`](docs/tick-loop.md) — **Per-tick execution order**. Read before adding work to the tick path or changing broadcast cadence. Covers the PrePhysics → Physics×4 → PostPhysics structure, `FlightPhysics.Update`'s 8/10 ordered steps (nav → planning → heading → altitude → speed → position → queue → observations), where `PhaseRunner` runs vs physics, ground vs airborne conflict detection placement, broadcast cadence (deltas only, once per sim-second), and the no-client-physics rule.
- [`docs/command-pipeline.md`](docs/command-pipeline.md) — **Command pipeline end-to-end**. Read before touching `MainViewModel.SendCommandAsync`, `CommandSchemeParser`, `CommandDispatcher`, `RoomEngine.SendCommandAsync`, or any `*CommandHandler.cs`. Walks `UA H180 AT FIX1` from input through partial-callsign resolution, parsing (`;` sequential / `,` parallel / `LV`/`AT` triggers), the four `RoomEngine` paths (Track / Coordination / Strip / Standard), `CommandDispatcher` phase gate + dry-run + dimension-aware queue clearing, `CommandQueue` triggers, and `DispatchContext` bundling.
- [`docs/crc-display-state.md`](docs/crc-display-state.md) — **CRC display state and broadcast architecture**. Read before touching `CrcClientState`, `CrcBroadcastService`, `CrcVisibilityTracker`, `DtoConverter`, or `CrcDtos*.cs`. Covers per-connection state, the topic catalog, periodic vs event-driven broadcasts, the WebSocket lifecycle (negotiate / handshake / 12-element StartSession), `CrcVisibilityTracker` floors and hysteresis, the `CrcSessionLifecycle` helper, and the six-step contract for adding a field that survives mid-session subscribes and snapshot round-trips.

### CIFP / ARINC 424 Reference Parsers

Two open-source CIFP parsers are cloned (git-untracked) into `reference/cifp/` as authoritative references for ARINC 424 column offsets, field meanings, and approach/SID/STAR record handling. Read these before changing `src/Yaat.Sim/Data/Vnas/CifpParser.cs`:

- **`reference/cifp/cifparse/`** — [misterrodg/cifparse](https://github.com/misterrodg/cifparse) — Python parser. The canonical source for column widths. Procedure leg widths are in `src/cifparse/records/procedure/widths.py` (`PrimaryIndices` class).
- **`reference/cifp/parseCifp/`** — [rstory1/parseCifp](https://github.com/rstory1/parseCifp) — Perl parser used by ZOA reference tooling.

If a column offset in YAAT's parser disagrees with `cifparse`, **trust cifparse**. YAAT's parser had a systematic +0/-1 off-by-one in procedure leg fields (arc_radius, theta, rho, course, dist_time, alt_1, alt_2) — see git log for the fix. Re-clone with:
```bash
mkdir -p reference/cifp && cd reference/cifp
git clone --depth 1 https://github.com/misterrodg/cifparse.git
git clone --depth 1 https://github.com/rstory1/parseCifp.git
```

Use `tools/Yaat.CifpInspector` to inspect parsed CIFP procedures from the command line — useful for diagnosing extraction bugs without writing throwaway scratch code.

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

## Problem Solving

- **Revert broken fixes immediately**: When a fix attempt breaks tests or makes things worse, revert and try a different approach. Never iterate on a broken approach more than twice without stepping back to reassess.
- **Verify before implementing**: Before implementing a fix, verify the approach against actual code and official docs. Never rely on unverified claims from sub-agents or assumptions about schemas/APIs. When in doubt, read the source.
- **Follow plans sequentially**: When the user provides a plan or ordered list of tasks, follow it top-down sequentially. Do not skip items, reorder, or take shortcuts through the list.

## Rules

### Testing
- **TDD for bugs and sim changes**: Write the failing test first, confirm it fails, fix, confirm it passes. Applies to bug fixes, commands, physics, phases, navdata, parsers — anything in simulation-critical code. See `docs/e2e-tdd-issue-debugging.md`.
- **No guessing at root causes**: Reproduce with a test first. Use real airport layouts and E2E tests. Do not speculate.
- **No synthetic data in tests**: Use `TestVnasData.EnsureInitialized()` (loads NavData.dat and CIFP via `NavDataPathResolver` / `CifpPathResolver` — one resolve per test process at assembly load in `tests/Yaat.Sim.Tests/ModuleInit.cs`, with bundled `TestData/NavData.dat` and `FAACIFP18.gz` as offline fallbacks). Refresh pins: `python tools/refresh-navdata.py`, `python tools/refresh-scenarios.py` (scenarios). Set `YAAT_SKIP_NAVDATA_DOWNLOAD=1` or `YAAT_SKIP_CIFP_DOWNLOAD=1` to skip vNAS/FAA download and use bundles only. Synthetic stubs hide integration problems. If test data files absent, silently skip.
- **Test access**: Making `internal` members `public` for tests is fine. No reflection or `InternalsVisibleTo` hacks.
- **SimLog in tests**: `SimLog` falls back to `NullLoggerFactory` by default — all Yaat.Sim log output is silently swallowed in tests. To see logs, use `SimLogBuilder.CreateForTest(output).EnableCategory("ClassName", LogLevel.Debug).InitializeSimLog()`. Run with `dotnet test --logger "console;verbosity=detailed" 2>&1 | tee .tmp/test-output.log` to capture output.
- **Test timeouts**: Always run test suites with `timeout 30` (e.g., `timeout 30 dotnet test ...`) to catch soft hangs from broken graph topology or infinite pathfinder loops. A test suite that hasn't finished in 30s is stuck, not slow.
- **Static singleton races**: xUnit runs test classes in parallel by default. Tests that read static singletons populated by `TestVnasData.EnsureInitialized()` — `NavigationDatabase`, `AircraftProfileDatabase`, `AircraftSiblingMap`, `AirlineFleetData`, etc. — can race when one class is mid-initialization while another reads the singleton. **Symptom**: a test passes in isolation but fails intermittently when run alongside profile-loading classes; the failure shows mismatched values where both should come from the same lookup (e.g., `Expected 98 / Actual 96.5` because `DecelRate` returned the default-fallback in one call and the loaded profile in the next). **Fix**: call `TestVnasData.EnsureInitialized()` in the racing test class's constructor so the lookup is pinned to a stable state before any test method body runs. Never assume singletons are empty — another test class is always one tick away from populating them.

### Code Style
- **Robust over expedient**: Always choose the most robust solution, not the simplest shortcut. When multiple approaches exist, prefer correctness and maintainability over expedience.
- **Line width**: 150 chars (CSharpier configured accordingly)
- **Boolean expressions**: Parenthesize to disambiguate — `(a.X) || (b.Y >= c + d)` not `a.X || b.Y >= c + d`
- **No newlines in text strings**: Never split `Text="..."`, `Content="..."`, or interpolated strings across lines in `.axaml`/`.cs` — indentation whitespace shows at runtime.
- **No optional parameters**: Make params required so the compiler enforces wiring. Optional params hide missing integration.
- **No repurposing DTO fields**: Add new fields with clear names. Remove dead fields entirely.

### Debugging
- **Add logging freely**: When investigating a bug, always add `Log.LogDebug` statements to trace execution rather than guessing from the code. This is always allowed and encouraged — logging is the primary debugging tool for graph/geometry issues. Use `--debug-fillets` in LayoutInspector to enable debug output.

### Error Handling
- Never swallow exceptions. Log with `AppLog` (client) or `ILogger` (Sim).
- Yaat.Sim static classes: `private static readonly ILogger Log = SimLog.CreateLogger("ClassName");` — never optional.

### Build & Format
- **Tee all output**: Always pipe `dotnet build`/`dotnet test`/`dotnet run` output through `tee` to `.tmp/` so results can be reviewed without re-running. Use a generic name (e.g. `.tmp/build.log`) unless you need to compare multiple runs, then use a unique name.
- **Build after edits, test after fixes**: Run `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tee .tmp/build.log` after edits and `timeout 30 dotnet test 2>&1 | tee .tmp/test.log` after fixes. Ensure zero warnings and all tests pass before committing.
- **Cross-repo verification**: When you'd otherwise run "the whole test suite" (after confirming targeted tests pass), run `pwsh tools/test-all.ps1` instead of bare `dotnet test`. It builds and tests both yaat and yaat-server. Catches signature changes in `Yaat.Sim` that break the sibling repo — bare `dotnet test` only sees yaat.
- **Warnings are errors**: Build with `dotnet build -p:TreatWarningsAsErrors=true` before committing.
- **No `-q` flag**: Never pass `-q` to any dotnet command — causes spurious errors.
- **Pre-commit**: Automated via `prek` (`prek.toml`). Runs: trailing-whitespace fix, EOF newline fix, merge conflict check, private key detection, large file check, `dotnet format style`, `dotnet format analyzers`, `dotnet csharpier format .`, `dotnet build -p:TreatWarningsAsErrors=true`. Run `prek run` manually to check; hooks fire automatically on `git commit`. Do NOT run bare `dotnet format`. Do NOT pass `-v q`, `--nologo`, or extra flags to `dotnet format`.

### Documentation
- Update `USER_GUIDE.md` before committing user-facing changes.
- Update `COMMANDS.md` whenever a command is added, removed, aliased, or changes behavior/arguments. This is the canonical user-facing command reference — both the **Quick Reference** tables and the **Detailed Command Documentation** section must stay in sync with the code (`CommandRegistry.All`, `CanonicalCommandType`, handler behavior).
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
- **Mempalace search**: `mcp__mempalace__mempalace_search` (filter `wing="yaat"`) over 162K long-form drawers — prior investigations, design docs, bug reports. Complements auto-memory: auto-memory = curated rules, mempalace = case files. KG/tunnel layers are empty — skip `kg_query`, `find_tunnels`, `traverse`.
