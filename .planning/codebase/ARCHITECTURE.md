<!-- refreshed: 2026-09-01 -->
# Architecture

**Analysis Date:** 2026-09-01

## System Overview

YAAT is a two-repo, two-process system: a desktop/browser client (this repo, `yaat`) talking over SignalR to a
server (sibling repo `yaat-server`) that owns the authoritative simulation. Both processes reference the same
`Yaat.Sim` class library via project reference — that library is the actual simulation, and it is shared, not
duplicated.

```text
┌───────────────────────────────────────────────────────────────────────────┐
│                        yaat (this repo) — CLIENT SIDE                     │
├───────────────────┬────────────────────┬──────────────────────────────────┤
│  Yaat.Client       │  Yaat.Client.Strips│  Yaat.Client.Tdls               │
│ `src/Yaat.Client/` │`.../Yaat.Client.   │ `.../Yaat.Client.Tdls/`         │
│  Avalonia desktop  │      Strips/`      │  vTDLS UI (PDC)                 │
│  radar/ground/cmd  │  vStrips UI        │                                  │
└─────────┬───────────┴─────────┬──────────┴──────────────┬───────────────────┘
          │                     │                          │
          ▼                     ▼                          ▼
┌───────────────────────────────────────────────────────────────────────────┐
│                     Yaat.Client.Core (`src/Yaat.Client.Core/`)            │
│  ServerConnection (SignalR+JSON DTOs) · UserPreferences · AppLog ·        │
│  ClientVersionGate · UpdateService (Velopack) · CRC alias parsing        │
└──────────────────────────────────┬─────────────────────────────────────────┘
                                    │ project reference
                                    ▼
┌───────────────────────────────────────────────────────────────────────────┐
│                  Yaat.Sim (`src/Yaat.Sim/`) — SHARED SIM LIBRARY          │
│  Phases · Commands · Simulation (world/engine/snapshots) · Pilot ·        │
│  Data (NavData/CIFP/Airspace/MVA/…) · ControllerAi · Speech · Scenarios  │
│  No UI dependency. Referenced by BOTH this repo's client AND             │
│  yaat-server's Yaat.Server project (sibling checkout, `$(YaatSimProject)`)│
└──────────────────────────────────┬─────────────────────────────────────────┘
                                    │ project reference (cross-repo)
                                    ▼
┌───────────────────────────────────────────────────────────────────────────┐
│               yaat-server (sibling repo) — SERVER SIDE                   │
│  TrainingHub (SignalR) → RoomEngine → TickProcessor → SimulationEngine   │
│  (Yaat.Sim) → CrcBroadcastService/DtoConverter → CRC clients (WebSocket) │
└───────────────────────────────────────────────────────────────────────────┘
```

Confirmed from `yaat.slnx` and each `*.csproj`'s `<ProjectReference>`:

- `src/Yaat.Client/Yaat.Client.csproj` → `Yaat.Client.Core`, `Yaat.Sim` (direct ref to Sim, verified in csproj)
- `src/Yaat.Client.Core/Yaat.Client.Core.csproj` → `Yaat.Client.Strips`, `Yaat.Client.Tdls`, `Yaat.Sim`
- `src/Yaat.Client.Strips/Yaat.Client.Strips.csproj` → `Yaat.Sim` only
- `src/Yaat.Client.Tdls/Yaat.Client.Tdls.csproj` → `Yaat.Sim`, `Yaat.Client.Strips`
- `src/Yaat.Sim/Yaat.Sim.csproj` → no project references (leaf library)
- yaat-server's `src/Yaat.Server/Yaat.Server.csproj` → `$(YaatSimProject)` MSBuild property, which resolves to
  the sibling `yaat`'s `src/Yaat.Sim/Yaat.Sim.csproj` (or `extern/yaat/` inside a Docker build context) — this is
  the cross-repo boundary.

Note: `CLAUDE.md` states the reference direction as "`Yaat.Client` → `Yaat.Client.Core` → `Yaat.Client.Strips`/`Yaat.Client.Tdls` → `Yaat.Sim`" — verified accurate. `Yaat.Client` also references `Yaat.Sim` directly (not only transitively through Core), confirmed in `src/Yaat.Client/Yaat.Client.csproj`.

Two more client surfaces exist as browser/WASM front ends, hosted from the *server* repo, not this doc's runtime:
`tools/Yaat.VStrips.Web` and `tools/Yaat.VTdls.Web` (Avalonia Browser). yaat-server's Dockerfile builds these out
of `extern/yaat/` (a vendored copy of this repo) — they are why `wasm-tools` is a build prerequisite here even
though the desktop app doesn't need it.

## Component Responsibilities

| Component | Responsibility | File/Dir |
|-----------|----------------|------|
| Yaat.Sim | All simulation logic: aircraft state, physics, phases, command parsing/dispatch, nav data, pilot AI/speech, scenario loading, snapshots/replay | `src/Yaat.Sim/` |
| Yaat.Client | Desktop shell: MVVM view models, SkiaSharp radar/ground rendering, command input UX, speech (STT/TTS), settings, window management | `src/Yaat.Client/` |
| Yaat.Client.Core | LM-Kit-free shared client services consumed by both desktop and browser front-ends: SignalR connection, auth, preferences, logging, update checks | `src/Yaat.Client.Core/` |
| Yaat.Client.Strips | vStrips UI (flight strip bays), WASM-clean (no Avalonia.Desktop/Velopack/file IO) | `src/Yaat.Client.Strips/` |
| Yaat.Client.Tdls | vTDLS UI (PDC/DCL), WASM-clean, depends on Strips only for the shared font registration | `src/Yaat.Client.Tdls/` |
| Yaat.Server (yaat-server) | Thin comms layer: SignalR hub, room lifecycle, CRC protocol translation, drives the 1 Hz tick loop that calls into `Yaat.Sim` | `..\yaat-server\src\Yaat.Server\` |

## Pattern Overview

**Overall:** Client-authoritative-nothing / server-authoritative-simulation, split across a shared "sim engine"
library. The client is a thin MVVM presentation layer over snapshots pushed from the server; it never runs
physics itself (confirmed in `docs/tick-loop.md`: "The client does **not** run physics — it receives broadcast
snapshots and animates between them"). All aviation logic lives in `Yaat.Sim`, consumed identically by the client
(for local scratch/tools/tests) and by the server (for the live tick loop).

**Key Characteristics:**
- **Sim owns logic, server is thin comms** (verified project-wide convention, also stated in `CLAUDE.md`) — `Yaat.Sim` has zero project references and no UI dependency, making it embeddable in tests, tools, and the server.
- **Phases own control** — while a `Phase` object is active for an aircraft (`CurrentPhase != null`), it writes directly to `ControlTargets` each sub-tick and bypasses the command queue (`docs/tick-loop.md`: "phases write directly to `ctx.Targets` and never enqueue commands").
- **MVVM with CommunityToolkit.Mvvm** throughout the client tier — `[ObservableProperty]`/`[RelayCommand]` source generators, no hand-written `INotifyPropertyChanged`.
- **No DI container** — `MainWindow` constructs `MainViewModel` directly (verified pattern referenced in `CLAUDE.md`; no `IServiceCollection`/`IServiceProvider` wiring found under `src/Yaat.Client/`).
- **Static singletons for shared read-only data** — `NavigationDatabase` is the canonical example: `Initialize(navData)` once at startup, `SetInstance(db)` swap for tests.

## Layers

**Yaat.Sim (simulation core):**
- Purpose: aircraft/world state, flight physics, command parsing and dispatch, phase state machines, nav/airspace/weather data, pilot AI and phraseology, scenario load/generation, snapshot/replay serialization
- Location: `src/Yaat.Sim/`
- Contains: `Simulation/` (world, engine, snapshots, replay), `Phases/` (per-phase behavior, subdirs `Approach/Ground/Pattern/Tower`), `Commands/` (parser, dispatcher, per-domain `*CommandHandler.cs`), `Data/` (NavData, CIFP, airspace, MVA, airline/aircraft reference data), `Pilot/`, `Speech/`, `ControllerAi/`, `Scenarios/`, `LiveTraffic/`, `Asdex/`, `Training/`, `Testing/` (test-only helpers shipped in the library for `TestVnasData`/`SimLogBuilder`)
- Depends on: nothing project-local (leaf project); external NuGet packages only
- Used by: `Yaat.Client`, `Yaat.Client.Core`, `Yaat.Client.Strips`, `Yaat.Client.Tdls`, all `tests/*`, most of `tools/*`, and (cross-repo) `yaat-server`'s `Yaat.Server`

**Yaat.Client.Core (shared client services):**
- Purpose: the subset of client code that needs Avalonia.Desktop/Velopack/file-system access but must stay LM-Kit-free so browser/WASM builds can consume it
- Location: `src/Yaat.Client.Core/`
- Contains: `Services/` (`ServerConnection.cs` — the SignalR client and DTO surface; `VatsimAuthClient.cs`; `UserPreferences.cs`; `UpdateService.cs`; `ClientVersionGate.cs`; CRC alias parsing), `Logging/` (`AppLog.cs`), `Models/`, `ViewModels/ConnectViewModel.cs`, `Views/` (connect dialog, window geometry/group-raise helpers)
- Depends on: `Yaat.Client.Strips`, `Yaat.Client.Tdls`, `Yaat.Sim`
- Used by: `Yaat.Client`; conceptually also by the WASM front-ends via Strips/Tdls (those consume Strips/Tdls directly, not Core, to stay Win32-clean — Core itself is Avalonia.Desktop-tied)

**Yaat.Client / Yaat.Client.Strips / Yaat.Client.Tdls (presentation):**
- Purpose: MVVM view models and Avalonia views for the desktop app and the two WASM-clean sub-UIs (strips, TDLS)
- Location: `src/Yaat.Client/`, `src/Yaat.Client.Strips/`, `src/Yaat.Client.Tdls/`
- Contains: `Models/`, `Services/`, `ViewModels/`, `Views/` in each
- Depends on: `Yaat.Client.Core` (desktop only) and/or `Yaat.Sim` directly
- Used by: end users (desktop) and `tools/Yaat.VStrips.Web`/`tools/Yaat.VTdls.Web` (browser, Strips/Tdls only)

**yaat-server (cross-repo, sibling checkout):**
- Purpose: hosts the SignalR hub, drives the 1 Hz tick loop, brokers CRC protocol, persists rooms
- Location: `..\yaat-server\src\Yaat.Server\`
- Contains (per `ls`): `Hubs/` (`TrainingHub.cs`, CRC WebSocket/session state), `Simulation/` (`RoomEngine.cs`, `TickProcessor`-related, `CrcBroadcastService.cs`, `DtoConverter.cs`, command handlers that bypass `Yaat.Sim`'s `CommandDispatcher`), `Auth/`, `Data/`, `Dtos/`, `LiveTraffic/`, `Udp/`, `Soak/`
- Depends on: `Yaat.Sim` via `$(YaatSimProject)` (cross-repo project reference)
- Used by: CRC clients (WebSocket) and this repo's `Yaat.Client`/browser front-ends (SignalR)

## Data Flow

### Primary Command Path (client → server → aircraft state → broadcast)

Traced against `docs/command-pipeline.md` and `src/Yaat.Sim/Commands/CommandDispatcher.cs`:

1. User types a command in `CommandInputView` → `MainViewModel.SendCommandAsync` (`src/Yaat.Client/ViewModels/MainViewModel.cs`) resolves the partial callsign, expands macros, and runs `CommandSchemeParser.ParseCompound` (`src/Yaat.Sim/Commands/CommandSchemeParser.cs`) to canonicalize the text client-side before sending.
2. Sends `SendCommand(callsign, canonicalString, initials)` over SignalR to `TrainingHub.SendCommand` (yaat-server `src/Yaat.Server/Hubs/TrainingHub.cs`).
3. `TrainingHub` resolves the connection's `RoomEngine` and calls `RoomEngine.SendCommandAsync` (yaat-server `src/Yaat.Server/Simulation/RoomEngine.cs`), which re-parses via `CommandParser.ParseCompound` — the server is authoritative regardless of what the client canonicalized.
4. `RoomEngine.SendCommandAsync` is a branch chain: track commands (TRACK/DROP/HO/ACCEPT) and coordination commands (RD/RDH/RDR/RDACK/RDAUTO) bypass `CommandDispatcher` entirely via `TrackCommandHandler`/`CoordinationCommandHandler`; everything else falls through to `HandleStandardCmd`, which calls into `Yaat.Sim`'s `CommandDispatcher.ApplyCommand` (`src/Yaat.Sim/Commands/CommandDispatcher.cs`).
5. `CommandDispatcher` builds/extends a `CommandQueue` of `CommandBlock`s (or, for immediate commands, mutates `ControlTargets` directly); each per-domain `*CommandHandler.cs` under `src/Yaat.Sim/Commands/` owns one command family.
6. Server's 1 Hz `PeriodicTimer` (`SimulationHostedService`, per `docs/tick-loop.md`) ticks each unpaused room: `SimulationEngine.TickPrePhysics` → `TickPhysics` ×4 sub-ticks (`SimulationWorld.Tick` → `PhaseRunner.Tick` → `FlightPhysics.Update`, which itself runs `UpdateCommandQueue` as step 9 to evaluate deferred triggers) → server-only `TickProcessor.ProcessPostPhysics`.
7. `AircraftChangeTracker`/`DtoConverter` (yaat-server `Simulation/`) diff state and `CrcBroadcastService`/SignalR broadcast the update once per sim-second to all connected clients (desktop + CRC).
8. Client's `ServerConnection` (`src/Yaat.Client.Core/Services/ServerConnection.cs`) receives the broadcast on a SignalR callback thread and marshals to the UI thread via `Dispatcher.UIThread.Post()`; `MainViewModel`'s aircraft dictionary updates, and `RadarViewModel`/`GroundViewModel` animate between snapshots (client never runs physics).

### Phase-Driven Control (per sub-tick, inside step 6 above)

1. `PhaseRunner.Tick(phases, ctx)` runs first — an aircraft's `CurrentPhase` (e.g. `LandingPhase`, `TaxiingPhase`) writes directly to `ControlTargets`, bypassing the command queue entirely while active.
2. `GroundConflictDetector.ApplySpeedLimits` caps target speed for ground proximity.
3. `FlightPhysics.Update` consumes the freshly-written `ControlTargets` and integrates position/heading/altitude/speed (8-10 ordered steps documented in `docs/tick-loop.md`).

**State Management:**
- Server: `SimulationWorld`/`AircraftState` inside each room's `SimulationEngine` is the single source of truth; `GetSnapshot()` returns a shallow, read-only-by-convention copy for broadcast.
- Client: no independent simulation state — `AircraftModel` (`src/Yaat.Client/Models/AircraftModel.cs`) wraps the latest `AircraftDto` from broadcast; `MainViewModel` partials (`MainViewModel.Rooms.cs`, `.Aircraft.cs`, `.Scenario.cs`, etc.) mirror server-pushed state into observable collections.

## Key Abstractions

**Phase (`Yaat.Sim.Phases.Phase`):**
- Purpose: represents a discrete aircraft control regime (taxiing, pattern leg, approach, landing rollout) that owns `ControlTargets` while active
- Examples: `src/Yaat.Sim/Phases/Ground/TaxiingPhase.cs`, `src/Yaat.Sim/Phases/Approach/*`, `src/Yaat.Sim/Phases/Pattern/*`, `src/Yaat.Sim/Phases/Tower/*`
- Pattern: base contract in `Phase.cs`; registered in `PhaseList.cs`; lifecycle driven by `PhaseRunner.cs`; serialized via `PhaseSnapshotDto.cs` (`[JsonDerivedType]` per phase — a new phase must be added here or snapshots silently drop it)

**Command / CommandBlock / CommandQueue:**
- Purpose: deferred/chainable instructor commands (`;` sequential, `,` parallel, `LV`/`AT` trigger conditions)
- Examples: `src/Yaat.Sim/Commands/CommandSchemeParser.cs`, `CommandDispatcher.cs`, `CommandRegistry.cs`, `CanonicalCommandType.cs`
- Pattern: every `CanonicalCommandType` must appear in both `CommandScheme.Default()` and `CommandRegistry.All` — enforced by tests (`docs/architecture.md` "Integration Footguns")

**DTO record + `[JsonDerivedType]`/MessagePack pair:**
- Purpose: wire contracts between client/server (SignalR JSON) and server/CRC (MessagePack)
- Examples: `src/Yaat.Client.Core/Services/ServerConnection.cs` (client-side DTO records), yaat-server `Dtos/` and `CrcDtos*.cs`
- Pattern: additive-only for CRC ("CRC is additive with explicit Delete* per topic" — project convention); snapshot DTOs require a `SnapshotSchemaMigrator` migration on any `AircraftState` field change

**Static singleton reference database:**
- Purpose: large, load-once, read-many reference data (nav fixes/airways/procedures, aircraft performance, airspace boundaries)
- Examples: `NavigationDatabase` (`src/Yaat.Sim/Data/Vnas/`), `AircraftProfileDatabase`, `AircraftSiblingMap`, `AirlineFleetData`
- Pattern: `Initialize(...)` once at process startup; tests use `SetInstance(db)` or `TestVnasData.EnsureInitialized()`; **race hazard** — xUnit parallelizes test classes, so any test reading these singletons must call `TestVnasData.EnsureInitialized()` in its constructor (documented footgun, not a defect to fix casually)

## Entry Points

**Desktop client:**
- Location: `src/Yaat.Client/Program.cs` → `App.axaml.cs` (`OnFrameworkInitializationCompleted`)
- Triggers: OS process launch (`dotnet run --project src/Yaat.Client` or the installed app)
- Responsibilities: builds the Avalonia app, registers process-wide window hotkeys (`WindowHotkeys.EnsureRegistered()`), and — only on the real desktop lifetime, never the headless UI test host — installs the SharpHook global PTT key hook and constructs `MainWindow`/`MainViewModel` directly (no DI)

**Server host (cross-repo):**
- Location: `..\yaat-server\src\Yaat.Server\Program.cs` → `YaatHost.BuildAsync(args)` → `app.Run()`
- Triggers: process launch (`dotnet run --project src/Yaat.Server`, binds `http://localhost:5130` in Development per `launchSettings.json`)
- Responsibilities: builds the ASP.NET Core host, maps `/hubs/training` (`TrainingHub`), starts `SimulationHostedService` (1 Hz `PeriodicTimer` tick loop), hosts CRC WebSocket endpoints and the vStrips/vTDLS WASM front-ends from `wwwroot`

**Browser/WASM front-ends (built here, hosted by server repo):**
- Location: `tools/Yaat.VStrips.Web/Program.cs`, `tools/Yaat.VTdls.Web/Program.cs`
- Triggers: browser page load of the WASM bundle yaat-server serves out of `extern/yaat/`
- Responsibilities: bootstrap `Yaat.Client.Strips`/`Yaat.Client.Tdls` view models over a browser-native `BrowserStripsTransport`/`BrowserTdlsTransport` (own `HubConnection`, independent of `ServerConnection`)

## Architectural Constraints

- **Threading (server):** the tick loop is single-threaded per the documented contract — `RunTickLoop runs all rooms on one thread` (memory finding, `tick_thread_sync_io_overruns.md`); nothing reachable from `TickPhysics` may block on network I/O.
- **Threading (client):** SignalR callbacks arrive off the UI thread; every handler that touches an `ObservableProperty` must marshal via `Dispatcher.UIThread.Post()` (`CLAUDE.md` convention, consistent with `MainViewModel` partials observed).
- **Global state:** `NavigationDatabase`, `AircraftProfileDatabase`, `AircraftSiblingMap`, `AirlineFleetData` are process-wide static singletons in `Yaat.Sim.Data` — shared across every room in the same server process and across every test in the same test process.
- **Two-brain risk (documented, guarded):** `SimulationEngine.TickPostPhysics` (used by tests/replay) and yaat-server's `TickProcessor.ProcessPostPhysics` (used live) are two independent per-tick orchestration paths over the same `Yaat.Sim` engine methods. Per-tick sim logic must live as a public `SimulationEngine.Tick*` method both hosts call — logic added only inside `TickPostPhysics` silently never runs live (`docs/tick-loop.md`, and memory finding `server_post_physics_two_brains.md`).
- **Cross-repo coupling:** any change to `Yaat.Sim`'s public surface (new field on `AircraftState`, changed method signature) can break yaat-server at compile time even though `dotnet test` in this repo alone won't catch it — `CLAUDE.md` mandates `pwsh tools/test-all.ps1` (builds+tests both repos) before considering cross-cutting changes done.

## Anti-Patterns

### Private orchestration hiding per-tick logic

**What happens:** Adding new per-tick simulation behavior as a private method called only from `TickPostPhysics` (test/replay path) or only from yaat-server's `ProcessPostPhysics` (live path), instead of as a public `SimulationEngine.Tick*` method both hosts call.
**Why it's wrong:** The two hosts diverge silently. `docs/tick-loop.md` documents a real incident: "the pilot-proactive request reminders once ran dark on the server for months this way" — tests passed because they exercised the test-only path, but the feature never fired in production.
**Do this instead:** Add a public `SimulationEngine.TickX` method; have both `TickPostPhysics` and yaat-server's `ProcessPostPhysics` call it (void form) or consume its return value (diff form, for broadcast-driving results). Guard with a yaat-server harness test (`RoomEngineTestHarness`) that goes red if the engine method is no-op'd — a Yaat.Sim-only test cannot catch a server-path gap.

### Phases enqueuing commands instead of writing targets

**What happens:** A phase implementation that tries to push a `CommandBlock` onto the aircraft's command queue instead of writing `ControlTargets` fields directly.
**Why it's wrong:** Phases run *before* physics inside `PhaseRunner.Tick`, and physics reads `ControlTargets` fresh every sub-tick; the command queue is evaluated later, as *step 9* of `FlightPhysics.Update` (`UpdateCommandQueue`), only for aircraft not currently under phase control. Routing phase behavior through the queue would either be ignored (phase still owns the aircraft) or fight the phase for control.
**Do this instead:** Write directly to `ctx.Targets` from inside the phase's `Tick` method — this is the documented, universal phase contract (`docs/tick-loop.md`, `docs/phases.md`).

## Error Handling

**Strategy:** fail loud, log everything — `CLAUDE.md` mandates "Never swallow exceptions. Log with `AppLog` (client) or `ILogger` (Sim)"; empty catch blocks are treated as a defect class, not a style nit.

**Patterns:**
- `Yaat.Sim` static classes each hold `private static readonly ILogger Log = SimLog.CreateLogger("ClassName");` — never optional, verified as a hard project rule.
- `CommandResult` (`src/Yaat.Sim/Commands/CommandDispatcher.cs`) is the uniform command outcome type: `Success`, optional `Message`, optional `RejectedCommandType`, optional `Advisory` (instructor-facing note distinct from pilot phraseology), `NoDispatcherArm` flag for "handler exists but no arm matched in this context."
- Client-side: `AppLog` wraps `SimLog`; `UiThreadWatchdog` (`src/Yaat.Client/Services/UiThreadWatchdog.cs`) detects a stalled Avalonia dispatcher (>2s) and, past 15s, writes a native minidump + full managed stack capture — a deliberate crash-diagnosis layer, not exception handling per se.

## Cross-Cutting Concerns

**Logging:** `SimLog` (Yaat.Sim) falls back to a null logger by default in tests — deliberate, to keep test output quiet; enable per-category via `SimLogBuilder.CreateForTest(...).EnableCategory(...)` when debugging. Client uses `AppLog`, writing to `%LOCALAPPDATA%/yaat/yaat-client.log` with 3-generation rotation on launch. See `docs/logging.md` for the full contract.

**Validation:** command completeness is compiler/test-enforced (`CanonicalCommandType` must exist in both `CommandScheme.Default()` and `CommandRegistry.All`); altitude parsing must always route through `AltitudeResolver.Resolve()` rather than ad hoc parsing (project rule in `CLAUDE.md`).

**Authentication:** VATSIM Connect (OAuth2) is brokered by yaat-server; the server is the sole identity authority (`docs/vatsim-auth.md`). The client's `VatsimAuthClient` (`src/Yaat.Client.Core/Services/VatsimAuthClient.cs`) performs a system-browser + loopback handoff and persists per-server sessions; SignalR connects with the resulting access token.

---

*Architecture analysis: 2026-09-01*
