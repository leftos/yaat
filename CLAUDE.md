# CLAUDE.md

## Project Overview

YAAT (Yet Another ATC Trainer) — instructor/RPO desktop client for ATC training. Connects to [yaat-server](https://github.com/leftos/yaat-server) via SignalR; server feeds CRC via SignalR+MessagePack.

YAAT uses ATCTrainer scenarios directly — all vNAS data-api resources (scenarios, weather profiles, airport data) are compatible. Scenarios fetched from `https://data-api.vnas.vatsim.net/api/training/scenarios/{id}` are the same ATCTrainer JSON format that `ScenarioLoader.Load()` parses. Do not treat ATCTrainer and YAAT scenarios as different formats.

## Build & Run

```bash
dotnet build                            # Build entire solution
dotnet run --project src/Yaat.Client    # Run client (needs yaat-server at localhost:5000)
```

.NET 10 SDK required. Solution uses `.slnx` format (`yaat.slnx`).

## Logs

Read log files first before speculating about runtime errors:
- **Client**: `%LOCALAPPDATA%/yaat/yaat-client.log`
- **Server**: `src/Yaat.Server/bin/Debug/net10.0/yaat-server.log` (relative to yaat-server repo root)

## Architecture

> **Full annotated file tree: [`docs/architecture.md`](docs/architecture.md).** Read it when you need to locate files or understand project structure. Keep it up-to-date before each commit.

Three projects across two repos. **Yaat.Sim** is shared by both Yaat.Client and yaat-server.

- **Yaat.Sim** (`src/Yaat.Sim/`) — Shared simulation library. Owns all aviation logic: flight physics, phases, pattern geometry, performance constants, command dispatch, command queue. No UI deps.
- **Yaat.Client** (`src/Yaat.Client/`) — Avalonia desktop app. MVVM with CommunityToolkit.Mvvm, SkiaSharp rendering (ground/radar views), SignalR JSON client.
- **yaat-server** (`..\yaat-server\`) — ASP.NET Core server. SignalR comms, CRC protocol, training rooms, scenario loading, broadcast fan-out. References Yaat.Sim via sibling project ref.

### Key Patterns

- **SignalR**: JSON protocol on `/hubs/training`; DTOs as records in `ServerConnection.cs`
- **MVVM**: `[ObservableProperty]`/`[RelayCommand]` from CommunityToolkit.Mvvm; `_camelCase` fields → `PascalCase` props
- **Threading**: SignalR callbacks on background thread; marshal to UI via `Dispatcher.UIThread.Post()`
- **No DI**: `MainWindow` creates `MainViewModel` directly
- **Snapshots**: `SimulationWorld.GetSnapshot()` returns shallow copy; treat as read-only

### Command Pipeline

1. User input → `MainViewModel.SendCommandAsync()` resolves callsign via partial match
2. `CommandSchemeParser.ParseCompound()`: `;` = sequential blocks, `,` = parallel, `LV`/`AT` = conditions
3. Translated to canonical format → sent to server via `SendCommand(callsign, canonical)`
4. Server builds `CommandQueue` of `CommandBlock`s; `FlightPhysics.UpdateCommandQueue()` checks triggers each tick

**Track commands** (TRACK, DROP, HO, ACCEPT, etc.) bypass CommandDispatcher — RoomEngine routes to TrackCommandHandler, mutating ownership fields. `AS` prefix resolves RPO identity.

**Coordination commands** (RD, RDH, RDR, RDACK, RDAUTO) bypass CommandDispatcher — RoomEngine routes to CoordinationCommandHandler. Coordination channels are loaded from ARTCC config on scenario load. Items auto-expire (5min after ack, 2min warning) and are removed on radar acquisition (TRACK).

### Command Rules

- Match existing ATCTrainer/VICE names where applicable. See `docs/command-aliases/reference.md` for the full comparison (🟢 marks YAAT-only commands). Source JSON extracts and the build script that produces them are in the same directory.
- **Completeness (MANDATORY):** Every `CanonicalCommandType` must exist in: `CommandScheme.Default()`, `CommandMetadata.AllCommands`. Tests in `tests/Yaat.Client.Tests/CommandSchemeCompletenessTests.cs` enforce this.

### Command Input UX

Two systems assist the user while typing commands — keep them in sync:

- **Autocomplete** (dropdown suggestions): `ArgumentSuggester` — suggests values for the current argument position (runway designators, fix names, literal keywords like `TAXI`).
- **Signature help** (inline parameter hint, a.k.a. "intellisense"): `SignatureHelpState` + `CommandSignatureSet` — shows the command's parameter signature with the active parameter highlighted and a description below.

Both are driven from `CommandInputController`: `UpdateSuggestions()` for autocomplete, `UpdateSignatureHelp()` for signature help. Registry-based commands (`CommandRegistry`) get both automatically via `CommandDefinition` overloads. **Special rewrite verbs** (e.g. `RWY`) are not in the registry, so both systems handle them with dedicated code paths — when adding or changing a rewrite verb, update both `ArgumentSuggester` and `UpdateSignatureHelp`.

### Communication Flow

```
YAAT Client ──SignalR JSON──> yaat-server <──SignalR+MessagePack── CRC
  /hubs/training                              /hubs/client
```

### SignalR Hub API

**Client→Server (room lifecycle):** `CreateRoom(cid, initials, artccId)`, `JoinRoom(roomId, cid, initials, artccId)`, `LeaveRoom`, `GetActiveRooms`

**Client→Server (scenario/sim):** `LoadScenario`, `UnloadScenarioAircraft`, `ConfirmUnloadScenario`, `SendCommand`, `SpawnAircraft`, `DeleteAircraft`, `PauseSimulation`, `ResumeSimulation`, `SetSimRate`, `SetAutoAcceptDelay`, `SetAutoDeleteMode`, `SendChat`, `LoadWeather(weatherJson) → CommandResultDto`, `ClearWeather()`

**Client→Server (data queries):** `GetAirportGroundLayout(airportId)`, `GetFacilityVideoMaps(artccId, facilityId)`, `GetFacilityVideoMapsForArtcc(artccId)`

**Client→Server (assignments):** `AssignAircraft(callsigns, targetConnectionId)`, `UnassignAircraft(callsigns)`, `GetAircraftAssignments`

**Client→Server (CRC management):** `GetCrcLobbyClients`, `PullCrcClient(clientId)`, `KickCrcClient(clientId)`, `GetCrcRoomMembers`

**Client→Server (admin):** `AdminAuthenticate`, `AdminGetScenarios`, `AdminSetScenarioFilter`, `Heartbeat`

**Server→Client:** `AircraftUpdated`, `AircraftSpawned`, `AircraftDeleted`, `SimulationStateChanged`, `TerminalBroadcast`, `RoomMemberChanged`, `CrcLobbyChanged`, `CrcRoomMembersChanged`, `WeatherChanged(WeatherChangedDto)`, `PositionDisplayChanged(PositionDisplayConfigDto)`, `AircraftAssignmentsChanged(AircraftAssignmentsDto)`

## Tech Stack

- .NET 10, C# with nullable enabled, implicit usings
- Avalonia UI 11.2.5 (Fluent dark) + DataGrid
- CommunityToolkit.Mvvm 8.4.0
- Microsoft.AspNetCore.SignalR.Client 10.0.3

## Related Repositories

| Repo | Path | Purpose |
|------|------|---------|
| yaat-server | `..\yaat-server` | ASP.NET Core server, simulation engine, CRC protocol |
| vzoa | `X:\dev\vzoa` | vZOA training files; airport GeoJSON at `training-files/atctrainer-airport-files/` |
| vatsim-server-rs | `X:\dev\vatsim-server-rs` | Rust CRC protocol reference (DTO ordering, varint framing) — **read-only emulation server**, see caveat below |
| lc-trainer | `X:\dev\lc-trainer` | Previous WPF trainer (**NOT trusted** — aviation details need expert review) |

> **vatsim-server-rs is a read-only emulation server.** It was designed to feed CRC with data, not to accept mutations. Many CRC→server methods (CreateFlightPlan, AmendFlightPlan, track operations, etc.) are stubbed with nil-ack responses because the Rust server never needed to implement them. YAAT's server needs full two-way interaction — students and RPOs create flight plans, amend them, drop tracks, etc. Use vatsim-server-rs as reference for wire format, DTO field ordering, and subscription/broadcast patterns, but evaluate feature needs independently. For mutation-capable methods, use the vNAS messaging-master interfaces and data-master models as the authoritative reference instead.

**vNAS source reference** (`X:\dev\vatsim-vnas\`):
- **common** — `GeoCalc`, `NavCalc`, `GeoPoint`, etc.
- **data** — Navigation, scenarios, aircraft specs, facility config models
- **messaging** — CRC hub DTOs/commands/topics (definitive reference)

**vNAS APIs:**
- Config: `https://configuration.vnas.vatsim.net/` (serials, URLs, environment endpoints)
- Data: `https://data-api.vnas.vatsim.net/api/artccs/{id}` (full ARTCC facility config)

## Reference Docs

- `docs/atctrainer-scenario-examples/` — 9 real ATCTrainer scenario JSON files (all "Easy" difficulty). Use as reference for scenario format when building scenario loading/parsing.
- `docs/crc/` — CRC (Consolidated Radar Client) controller manual pages scraped from vNAS docs. Covers overview, STARS terminal radar, Tower Cab mode, and vStrips. Use as reference for understanding CRC display behavior and terminology when implementing CRC protocol features.
- `docs/vnas-artcc-config-examples/` — Real vNAS ARTCC configuration JSON files (e.g., `zoa.json`). Contains facility hierarchy, positions, TCPs, STARS lists with coordination channels, ASDEX/TowerCab configs. Use as reference for parsing ARTCC API responses and understanding coordination channel adaptation.

## Discord Integration

### GitHub Actions Workflows (`.github/workflows/`)

| Workflow | Trigger | What it does |
|----------|---------|--------------|
| `discord-push.yml` | Push to `master` | Posts commit list embed to `DISCORD_WEBHOOK_URL` |
| `discord-nightly.yml` | Cron 02:00 PT (+ manual) | Claude Haiku summarizes yaat + yaat-server commits since last digest; posts to `DISCORD_NIGHTLY_WEBHOOK_URL` |
| `discord-docs.yml` | Push to `master` (INSTALL/README/USER_GUIDE) + manual | Clears + reposts doc content to dedicated channels via bot token; USER_GUIDE posts ToC only |

### Discord Bot (`tools/discord-bot/`)

Cloudflare Worker (JS, no framework) deployed as `yaat-discord-bot`. State in KV namespace `THREAD_ISSUES`.

**Slash commands** (restricted to `DISCORD_ALLOWED_USER_ID`):
- `/create-issue` — creates GitHub issue labeled `bug` from forum thread
- `/create-feature-request` — creates GitHub issue labeled `enhancement`
- `/resolve` / `/unresolve` — manually toggle resolved state (checkmark title prefix + reaction)

Re-running a slash command in an already-linked thread triggers an immediate comment sync instead.

**Auto-sync** (cron every 15min): New non-bot thread replies → GitHub issue comments.

**GitHub → Discord** (webhook on `issues` events at `/github`):
- Labels (`in progress`, `completed`, `wontfix`, `not a bug`, `duplicate`) → status message posted to linked thread
- Terminal labels/close → per-type emoji prefix on title (✅/🚫/❌/♻️), matching reaction, thread archived
- Issue reopened → emoji prefix removed, thread unarchived

**KV mappings:** `threadId → {issueNumber, issueUrl, guildId, lastSyncedMessageId}` and reverse `issue:{N} → threadId`.

**Secrets** (Cloudflare): `DISCORD_PUBLIC_KEY`, `DISCORD_BOT_TOKEN`, `GITHUB_TOKEN`, `DISCORD_ALLOWED_USER_ID`, `GITHUB_WEBHOOK_SECRET`

**Deploy:** `cd tools/discord-bot && pnpm install && pnpm run deploy`. Register commands: `DISCORD_APP_ID=<id> DISCORD_BOT_TOKEN=<token> pnpm run register -- --guild <guild-id>`.

## Aviation Realism — MANDATORY

**Every feature touching aviation must be reviewed by `aviation-sim-expert`** (via `Agent` with `subagent_type: "aviation-sim-expert"`). This is not optional.

**Use aviation-sim-expert when touching:** flight physics, pilot AI behavior, ATC logic, radio comms, aircraft performance, airspace rules, scenario data, phase transitions, command dispatch affecting aviation, ground operations, conflict detection, trigger conditions, or any automatic aircraft behavior.

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

- **Never read secrets files into context.** Do not `Read` `.env`, credentials files, key files, or any file likely to contain secrets. If you need to know what variables a secrets file defines, parse it for **key names only** (e.g., `grep` for `^[A-Z_]+=` and strip values). Exposing secret values in the conversation context is a leak — treat it as a hard error.
- **No guessing at root causes**: When debugging, reproduce the problem with a test first. Use real airport layouts and E2E tests (see `docs/e2e-testing.md`). Do not speculate about causes — write a failing test that demonstrates the bug, then fix it.
- **Unreleased software**: YAAT has no public release and no external users. Do not add backwards-compatibility shims, migration paths, deprecated aliases, or dual config formats. There is nothing to stay compatible with. Delete and replace freely.
- **User Guide**: Update `USER_GUIDE.md` before committing user-facing changes.
- **Comparison doc**: Update `docs/yaat-vs-atctrainer.md` before committing changes that add, remove, or change commands, features, or behavioral differences vs ATCTrainer.
- **No newlines in text strings**: Never split literal text across lines in `.axaml` or `.cs` files. The indentation whitespace becomes visible at runtime (huge gaps in UI text). Keep `Text="..."`, `Content="..."`, and interpolated strings on one line, even if long.
- **Window geometry**: Every window must persist its position/size via `WindowGeometryHelper(window, preferences, "Name", defaultW, defaultH).Restore()`. New window names automatically use the `WindowGeometries` dictionary in `UserPreferences` — no need to add named properties.
- **Error Handling**: Never swallow exceptions. Log with `AppLog` (client) or `ILogger` (Sim). In Yaat.Sim static classes, use a static `private static readonly ILogger Log = SimLog.CreateLogger("ClassName");` — never make loggers optional parameters.
- **Line width**: 150 characters, not 80 or 120. CSharpier is configured accordingly.
- **Warnings are errors**: CI builds with `/warnaserror`. Always build with `dotnet build -p:TreatWarningsAsErrors=true` locally before committing to catch issues CI will reject.
- **Pre-commit formatting**: Run `dotnet format style`, `dotnet format analyzers`, then `dotnet csharpier format .` before each commit, followed by a final `dotnet build -p:TreatWarningsAsErrors=true` to verify nothing broke. Do NOT run bare `dotnet format` (its whitespace rules fight with CSharpier).
- **Commits**: `fix:`/`feat:`/`add:`/`docs:`/`ref:`/`test:` etc. Imperative, ≤72 chars.
- **Memory Updates**: Distill Explore agent findings into auto-memory at `C:\Users\Leftos\.claude\projects\X--dev-yaat\memory\`.
- **Milestone Roadmap**: See `docs/plans/main-plan.md`. M0/M1 complete; M2 (tower ops) next.
- **Issue triage plans**: When running parallel agents to explore/plan all open GitHub issues, each agent must write its plan as a markdown file in `docs/plans/open-issues/` (e.g., `issue-23-wait-command.md`). This makes plans persistent and actionable across sessions.
- **Issue completion**: After implementing an issue fix, delete its plan file from `docs/plans/open-issues/` and create a PR linked to the GitHub issue (`Closes #N`).
- **Cross-repo completeness**: Features that span both Yaat.Client/Yaat.Sim and yaat-server must be implemented in both repos in the same task. Do not implement the client half and leave the server half as a note — do all the work together so the feature is functional end-to-end.
