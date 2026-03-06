# Rewind / Timeline Scrubber Feature

## Context

ATC training requires iteration — the student makes a decision, sees the outcome, and wants to go back to try a different approach. Currently there's no way to undo or revisit past simulation state. This feature adds a command-replay rewind system and a client-side timeline scrubber that lets the user pause, scrub backward to any past point, and either resume live (discarding the future) or play back the recorded session like a tape — watching past commands execute automatically, then taking control at any moment.

This is a cross-repo feature spanning **Yaat.Sim** (seeded RNG), **yaat-server** (action recording, replay logic, hub protocol), and **Yaat.Client** (timeline UI).

---

## Architecture Overview

```
Client                          Server
┌─────────────────────┐        ┌──────────────────────────────────┐
│ Timeline slider     │        │ Tick loop                        │
│ (enabled when       │        │   ├─ tick aircraft               │
│  paused)            │        │   ├─ (playback?) apply next      │
│ Elapsed time label  │        │   │   recorded actions           │
│                     │        │   └─ broadcast updates           │
│ [|<] [-30] [-15]    │        │                                  │
│ [Play] [Take Ctrl]  │        │ RoomEngine records every action  │
│                     │◄───────│ SimulationStateChanged           │
│ Mode: LIVE/PLAYBACK │        │   (isPaused, simRate, elapsed,   │
│                     │        │    isPlayback, tapeEnd)           │
│                     │───────►│ RewindTo(elapsedSeconds)         │
│                     │───────►│ TakeControl()                    │
│                     │◄───────│ TimelineRewound(elapsed, aircraft│
└─────────────────────┘        │    , simState)                   │
                               │                                  │
                               │ ActionLog per ScenarioState      │
                               │   + OriginalScenarioJson         │
                               │   + RngSeed                      │
                               │   + PlaybackCursor               │
                               └──────────────────────────────────┘
```

**Replay strategy**: Instead of cloning entire simulation state, record every user action (commands, spawns, deletes, warps, weather, settings) with its elapsed-seconds timestamp. On rewind, reload the scenario from the original JSON with the same RNG seed, then fast-forward by running ticks silently up to the target time, replaying recorded actions at their timestamps. All broadcasts are suppressed during the fast-forward.

**Why replay over snapshots**:
- Eliminates Clone() on 34 Phase subclasses, 100+ AircraftState fields, PhaseList, CommandBlock closures, ControlTargets, NavigationRoute — thousands of lines of infrastructure
- Zero maintenance burden as new phases/fields are added
- Trivially correct — same code path as normal execution
- No memory overhead for snapshots (~67 MB for 15 min × 50 aircraft)
- Replay cost: 15 min at 1 Hz = 900 ticks of pure math; well under 1 second

**Determinism**: The simulation is deterministic given the same inputs. FlightPhysics, delayed spawns, triggers, generators all produce identical results. The only non-deterministic inputs are user actions (recorded) and RNG calls (seeded).

**Two post-rewind modes**:

| Mode | Behavior |
|------|----------|
| **Playback** | After rewind, sim runs and recorded actions auto-execute at their original timestamps. When elapsed time reaches the end of the tape (the furthest recorded action), the sim pauses automatically. The user watches the session replay like a VCR. |
| **Live** | Normal mode. User issues commands, which are recorded. This is the default after scenario load, and the mode entered after "take control". |

"Take control" exits playback at any point: future recorded actions are discarded (log truncated at current elapsed), and the user is in live mode from that moment. Any user action during playback (command, spawn, delete, etc.) implicitly triggers take-control.

---

## Phase 1: Seeded RNG (Yaat.Sim + yaat-server)

Replace all `Random.Shared` usage in simulation-affecting code with a per-room seeded `Random` instance, ensuring identical RNG sequences on replay.

### 1.1 Add `Random Rng` to `SimulationWorld`

```csharp
// SimulationWorld.cs
public Random Rng { get; set; } = Random.Shared;
```

Default to `Random.Shared` so existing behavior is unchanged until a scenario seeds it.

### 1.2 Make `GenerateBeaconCode` use instance RNG

Change from static to instance method (or add an overload taking `Random`):

```csharp
public uint GenerateBeaconCode()
{
    var rng = Rng;
    uint code = 0;
    for (int i = 0; i < 4; i++)
        code = (code * 10) + (uint)rng.Next(0, 8);
    return code;
}
```

Update `GenerateUniqueCid()` similarly (already instance, just replace `Random.Shared` with `Rng`).

### 1.3 Thread `Random` through `AircraftGenerator`

`AircraftGenerator.Generate()` and its internal helpers (`ResolveType`, `GenerateCallsign`) in `src/Yaat.Sim/Scenarios/AircraftGenerator.cs` use `Random.Shared`. Add a `Random? rng = null` parameter, defaulting to `Random.Shared`:

```csharp
public static (AircraftState? State, string? Error) Generate(
    SpawnRequest request,
    string? primaryAirportId,
    IFixLookup fixes,
    IRunwayLookup runways,
    IReadOnlyCollection<AircraftState> existingAircraft,
    AirportGroundLayout? groundLayout = null,
    Random? rng = null)
```

All internal `Random.Shared` calls become `(rng ?? Random.Shared)`.

### 1.4 Thread `Random` through server-side TickProcessor

`TickProcessor.AdvanceGenerator()` and `ResolveWeight()` use `Random.Shared`. Pass the room's `World.Rng` through:

- `AdvanceGenerator(GeneratorState gen, Random rng)` — jitter calculation
- `ResolveWeight(ScenarioGenerator config, Random rng)` — random weight class

### 1.5 Store seed on `ScenarioState`

```csharp
// ScenarioState.cs
public int RngSeed { get; init; }
```

Set at scenario load time in `ScenarioLifecycleService.LoadScenarioAsync()`:

```csharp
var rngSeed = Random.Shared.Next();
room.World.Rng = new Random(rngSeed);
var scenario = new ScenarioState { ..., RngSeed = rngSeed };
```

### 1.6 RNG call sites (complete list)

| File | Call site | Change |
|------|-----------|--------|
| `Yaat.Sim/SimulationWorld.cs:153` | `GenerateBeaconCode` | Use `Rng` |
| `Yaat.Sim/SimulationWorld.cs:177` | `GenerateUniqueCid` | Use `Rng` |
| `Yaat.Sim/Scenarios/AircraftGenerator.cs:347` | `ResolveType` | Use `rng` param |
| `Yaat.Sim/Scenarios/AircraftGenerator.cs:359-398` | `GenerateCallsign` (6 calls) | Use `rng` param |
| `Yaat.Server/Simulation/TickProcessor.cs:299` | `AdvanceGenerator` jitter | Use `rng` param |
| `Yaat.Server/Simulation/TickProcessor.cs:316` | `ResolveWeight` roll | Use `rng` param |

**Not changed** (not simulation-affecting):
- `NegotiateHandler.cs:79` — CRC token generation (auth, not sim)
- `TrainingRoomManager.cs:236` — room ID generation (not sim)
- `BeaconCodePool` — if it uses RNG, thread through similarly

### 1.7 Files modified

- `src/Yaat.Sim/SimulationWorld.cs` — add `Rng`, use in `GenerateBeaconCode`/`GenerateUniqueCid`
- `src/Yaat.Sim/Scenarios/AircraftGenerator.cs` — add `rng` param, thread through
- `Yaat.Server/Simulation/TickProcessor.cs` — pass `room.World.Rng` to generator helpers
- `Yaat.Server/Simulation/ScenarioLifecycleService.cs` — seed RNG at load time
- `Yaat.Server/Simulation/ScenarioState.cs` — add `RngSeed`
- `Yaat.Server/Scenarios/ScenarioLoader.cs` — pass `Random` to beacon code generation

### 1.8 Tests

- Verify determinism: load same scenario with same seed twice, tick N times, assert identical aircraft positions and callsigns
- Verify different seeds produce different beacon codes/callsigns

---

## Phase 2: Action Recording (yaat-server)

Record every state-changing user action with its elapsed-seconds timestamp.

### 2.1 `RecordedAction` types

```csharp
// New file: Yaat.Server/Simulation/ActionLog.cs

public abstract record RecordedAction(double ElapsedSeconds);

public sealed record RecordedCommand(
    double ElapsedSeconds, string Callsign, string Command, string Initials, string ConnectionId
) : RecordedAction(ElapsedSeconds);

public sealed record RecordedSpawn(
    double ElapsedSeconds, string Args
) : RecordedAction(ElapsedSeconds);

public sealed record RecordedDelete(
    double ElapsedSeconds, string Callsign
) : RecordedAction(ElapsedSeconds);

public sealed record RecordedWarp(
    double ElapsedSeconds, string Callsign, double Latitude, double Longitude, double Heading
) : RecordedAction(ElapsedSeconds);

public sealed record RecordedAmendFlightPlan(
    double ElapsedSeconds, string Callsign, FlightPlanAmendmentDto Amendment
) : RecordedAction(ElapsedSeconds);

public sealed record RecordedWeatherChange(
    double ElapsedSeconds, string? WeatherJson
) : RecordedAction(ElapsedSeconds);

public sealed record RecordedSettingChange(
    double ElapsedSeconds, string Setting, string? Value
) : RecordedAction(ElapsedSeconds);
```

Settings covered by `RecordedSettingChange`:
- `AutoAcceptDelay` (int seconds)
- `AutoDeleteMode` (string? mode)
- `AutoClearedToLand` (bool)
- `AutoCrossRunway` (bool)
- `ValidateDctFixes` (bool)

### 2.2 `SessionRecording` — serializable replay file

The full recording is a self-contained JSON document that can be saved/loaded/shared:

```csharp
// New file: Yaat.Server/Simulation/SessionRecording.cs

public sealed class SessionRecording
{
    public required string ScenarioJson { get; init; }
    public required int RngSeed { get; init; }
    public required string? WeatherJson { get; init; }
    public required List<RecordedAction> Actions { get; init; }
    public required double TotalElapsedSeconds { get; init; }

    // Metadata (informational, not needed for replay)
    public string? ScenarioName { get; init; }
    public string? ScenarioId { get; init; }
    public string? ArtccId { get; init; }
    public DateTime? RecordedAtUtc { get; init; }
    public string? RecordedBy { get; init; }
}
```

`ScenarioJson` is the original scenario. `WeatherJson` is the weather loaded at scenario start (if any — mid-session weather changes are in the action log). `Actions` is the full ordered action list. This is everything needed to deterministically reproduce the entire session.

### 2.3 Store on `ScenarioState`

```csharp
// ScenarioState.cs additions
public string OriginalScenarioJson { get; init; } = "";
public List<RecordedAction> ActionLog { get; } = [];

// Playback state
public bool IsPlaybackMode { get; set; }
public int PlaybackCursor { get; set; }  // index of next action to apply
public double PlaybackEndSeconds { get; set; }  // elapsed time of last recorded action
```

`OriginalScenarioJson` is set at load time in `ScenarioLifecycleService.LoadScenarioAsync()`.

### 2.4 Record actions in `RoomEngine`

Add a helper:

```csharp
private void Record(RecordedAction action)
{
    var scenario = Room.ActiveScenario;
    if (scenario is null || scenario.IsPlaybackMode)
        return;  // don't re-record during playback
    scenario.ActionLog.Add(action);
}
```

Insert `Record(...)` calls at each action site:

| Method | Action recorded |
|--------|----------------|
| `SendCommandAsync` (after successful dispatch) | `RecordedCommand` |
| `HandleSpawnAircraftAsync` | `RecordedSpawn` |
| `DeleteAircraft` | `RecordedDelete` |
| `WarpAircraft` | `RecordedWarp` |
| `AmendFlightPlan` | `RecordedAmendFlightPlan` |
| `LoadWeather` | `RecordedWeatherChange(weatherJson)` |
| `ClearWeather` | `RecordedWeatherChange(null)` |
| `SetAutoAcceptDelay` | `RecordedSettingChange` |
| `SetAutoDeleteMode` | `RecordedSettingChange` |
| `SetAutoClearedToLand` | `RecordedSettingChange` |
| `SetAutoCrossRunway` | `RecordedSettingChange` |

**Not recorded** (don't affect sim state):
- `PauseSimulation` / `ResumeSimulation` / `SetSimRate` — elapsed time is tracked independently; pause/rate only affect wall-clock speed
- `SendChat` — display only
- Track/coordination commands — these mutate `Owner`/`HandoffPeer` etc. on aircraft, which are replay-affecting. **Record these too** as `RecordedCommand` (they go through `SendCommandAsync`)
- Consolidation commands — go through `SendCommandAsync`, already recorded

### 2.5 Files modified

- New: `Yaat.Server/Simulation/ActionLog.cs`
- New: `Yaat.Server/Simulation/SessionRecording.cs`
- `Yaat.Server/Simulation/ScenarioState.cs` — add `OriginalScenarioJson`, `ActionLog`, playback state fields
- `Yaat.Server/Simulation/ScenarioLifecycleService.cs` — store `OriginalScenarioJson` at load
- `Yaat.Server/Simulation/RoomEngine.cs` — add `Record()` helper, insert recording calls

### 2.6 Tests

- Load scenario, send commands, verify ActionLog contains correct entries with correct elapsed times
- Verify non-recorded actions (pause/resume/chat) don't appear in the log
- Verify `Record()` is suppressed during playback mode

---

## Phase 3: Rewind, Playback & Save/Load (yaat-server)

### 3.1 `Rewind()` method on `RoomEngine`

```
Rewind(targetElapsedSeconds):
  1. Pause the simulation
  2. Save the full ActionLog and compute PlaybackEndSeconds (max elapsed of any action)
  3. Clear the room:
     a. Remove all aircraft from World
     b. Clear ChangeTracker, ConflictAlerts, LineNumbers, BeaconCodePool
     c. Clear CRC visibility state for all aircraft
     d. Clear ASDEX state, flight strip state
  4. Re-seed RNG: room.World.Rng = new Random(scenario.RngSeed)
  5. Re-load scenario from OriginalScenarioJson (via ScenarioLifecycleService)
     - This creates a fresh ScenarioState with queues, generators, etc.
     - Preserve: OriginalScenarioJson, RngSeed, full ActionLog on the new ScenarioState
  6. Set IsBroadcastSuppressed = true on RoomEngine
  7. Fast-forward: for each elapsed second from 1 to targetElapsedSeconds:
     a. Apply any RecordedActions at this elapsed second
     b. ProcessTick(1.0)
     c. Advance scenario.ElapsedSeconds
  8. Set IsBroadcastSuppressed = false
  9. Enter playback mode:
     - scenario.IsPlaybackMode = true
     - scenario.PlaybackCursor = index of first action with ElapsedSeconds > target
     - scenario.PlaybackEndSeconds = max elapsed of any recorded action
  10. Rebuild CRC visibility state (auto-rebuilds on next tick via Evaluate())
  11. Broadcast TimelineRewound to all clients in room
  12. Broadcast full aircraft state to all clients
  13. Sim stays paused — client presses Play to start playback
```

### 3.2 Playback tick integration

During playback mode, the normal tick loop in `SimulationHostedService` drives playback. On each tick, before `room.Engine.ProcessTick()`:

```csharp
// In SimulationHostedService.RunTickLoop, after advancing ElapsedSeconds:
if (scenario.IsPlaybackMode)
{
    room.Engine.ApplyPlaybackActions(scenario.ElapsedSeconds);

    // Auto-pause at end of tape
    if (scenario.ElapsedSeconds >= scenario.PlaybackEndSeconds)
    {
        scenario.IsPaused = true;
        scenario.IsPlaybackMode = false;
        broadcast.BroadcastSimState(room);  // notifies clients of pause + mode change
    }
}
```

`ApplyPlaybackActions` advances `PlaybackCursor` through the log, applying all actions whose `ElapsedSeconds <= currentElapsed`:

```csharp
public void ApplyPlaybackActions(double currentElapsed)
{
    var scenario = Room.ActiveScenario;
    if (scenario is null) return;

    var log = scenario.ActionLog;
    while (scenario.PlaybackCursor < log.Count
        && log[scenario.PlaybackCursor].ElapsedSeconds <= currentElapsed)
    {
        ApplyRecordedAction(log[scenario.PlaybackCursor]);
        scenario.PlaybackCursor++;
    }
}
```

### 3.3 Take control

"Take control" exits playback: the user wants to diverge from the recording.

```
TakeControl():
  1. Truncate ActionLog: remove all entries with ElapsedSeconds > current
  2. Set IsPlaybackMode = false, PlaybackCursor = 0
  3. Broadcast mode change to all clients
```

**Implicit take-control**: Any state-changing user action during playback (command, spawn, delete, warp, weather, settings) triggers `TakeControl()` first, then applies the new action. This means the user can just start issuing commands — they don't need to explicitly click "Take Control" first.

### 3.4 Broadcast suppression

During the fast-forward phase of rewind (step 6-8), all broadcasts must be silenced. Add a flag:

```csharp
// RoomEngine.cs
public bool IsBroadcastSuppressed { get; set; }
```

`ITrainingBroadcast` and `ICrcBroadcast` check this flag and no-op when true. Alternatively, wrap the broadcast services with a suppression decorator during replay.

The simpler approach: `SimulationHostedService.RunTickLoop` already handles broadcasts separately from ticks. During the fast-forward, we call `room.Engine.ProcessTick()` directly without the broadcast step. The only concern is broadcasts triggered *within* command handlers (terminal entries). These can be silenced by checking `IsBroadcastSuppressed` in the code paths, or by having `BroadcastTerminalEntry` check the flag.

**During playback** (normal tick loop, not fast-forward), broadcasts are NOT suppressed. The user sees aircraft move in real time and sees terminal entries as commands auto-execute — this is the point of playback.

### 3.5 Applying recorded actions

```csharp
private void ApplyRecordedAction(RecordedAction action)
{
    switch (action)
    {
        case RecordedCommand cmd:
            ReplayCommand(cmd.ConnectionId, cmd.Callsign, cmd.Command, cmd.Initials);
            break;
        case RecordedSpawn spawn:
            _lifecycle.HandleSpawnAircraftAsync(Room, spawn.Args);
            break;
        case RecordedDelete del:
            Room.World.RemoveAircraft(del.Callsign);
            break;
        case RecordedWarp warp:
            WarpAircraft(warp.Callsign, warp.Latitude, warp.Longitude, warp.Heading);
            break;
        case RecordedAmendFlightPlan amend:
            AmendFlightPlan(amend.Callsign, amend.Amendment);
            break;
        case RecordedWeatherChange weather:
            if (weather.WeatherJson is not null)
                LoadWeather(weather.WeatherJson);
            else
                ClearWeather();
            break;
        case RecordedSettingChange setting:
            ApplySettingChange(setting.Setting, setting.Value);
            break;
    }
}
```

`ReplayCommand` is the same as `SendCommandAsync` but skips recording. During fast-forward it also skips broadcasting (via `IsBroadcastSuppressed`). During live playback it broadcasts normally so the user sees terminal entries.

### 3.6 Save / Load recordings

**Save**: `RoomEngine.ExportRecording()` builds a `SessionRecording` from the current `ScenarioState`:

```csharp
public SessionRecording ExportRecording()
{
    var scenario = Room.ActiveScenario!;
    return new SessionRecording
    {
        ScenarioJson = scenario.OriginalScenarioJson,
        RngSeed = scenario.RngSeed,
        WeatherJson = Room.Weather is not null ? JsonSerializer.Serialize(Room.Weather) : null,
        Actions = [.. scenario.ActionLog],
        TotalElapsedSeconds = scenario.ElapsedSeconds,
        ScenarioName = scenario.ScenarioName,
        ScenarioId = scenario.ScenarioId,
        ArtccId = scenario.ArtccId,
        RecordedAtUtc = DateTime.UtcNow,
    };
}
```

**Load**: `RoomEngine.LoadRecording(SessionRecording)`:

```
LoadRecording(recording):
  1. Load the scenario from recording.ScenarioJson with recording.RngSeed
  2. If recording.WeatherJson is set, load that weather
  3. Set ActionLog = recording.Actions
  4. Set PlaybackEndSeconds = recording.TotalElapsedSeconds
  5. Enter playback mode at t=0 (PlaybackCursor = 0)
  6. Sim starts paused — client presses Play to watch
```

The recording file is JSON, saved to / loaded from disk by the client. The server exposes hub methods; the client handles the file I/O.

### 3.7 CRC state reset

On rewind, CRC visibility state auto-rebuilds on the next tick via `CrcVisibilityTracker.Evaluate()`. The brief visual discontinuity (one tick) is acceptable for training.

Clear `Room.ChangeTracker` so the first post-rewind tick treats all aircraft as new, triggering full CRC updates.

### 3.8 Multi-client behavior

- Rewind pauses the simulation, affecting all clients on the scenario
- Any connected client can trigger rewind or take control
- All clients receive `TimelineRewound` and see the restored state
- All clients see the playback/live mode indicator

### 3.9 Files modified

- `Yaat.Server/Simulation/RoomEngine.cs` — `Rewind()`, `ApplyPlaybackActions()`, `TakeControl()`, `ApplyRecordedAction()`, `ReplayCommand()`, `ExportRecording()`, `LoadRecording()`, `IsBroadcastSuppressed`
- `Yaat.Server/Simulation/ScenarioLifecycleService.cs` — support re-loading from stored JSON with preserved state
- `Yaat.Server/Simulation/SimulationHostedService.cs` — playback action application + auto-pause in tick loop
- `Yaat.Server/Simulation/TrainingBroadcastService.cs` — check `IsBroadcastSuppressed`
- `Yaat.Server/Simulation/CrcBroadcastService.cs` — check `IsBroadcastSuppressed`

### 3.10 Tests

- Load scenario, tick 30s, send commands at t=10 and t=20, rewind to t=15: verify aircraft match state at t=15 with first command applied but not second
- Rewind to t=0: verify state matches fresh scenario load
- **Playback**: rewind to t=10, resume, verify command at t=20 auto-executes at the right time
- **Auto-pause at tape end**: rewind to t=0, play, verify sim pauses at original elapsed time
- **Take control**: rewind to t=10, resume playback, take control at t=15, verify ActionLog truncated, verify new commands record normally
- **Implicit take-control**: rewind to t=10, resume playback, send a new command at t=15, verify playback exits and command applies
- Verify RNG determinism: rewind and replay produces identical beacon codes
- Verify weather state is correct after rewind through a weather change
- **Save/load roundtrip**: export recording, load it in a new room, play back, verify identical state at end

---

## Phase 4: Hub Protocol (yaat-server + Yaat.Client)

### 4.1 Hub method additions

**TrainingHub new client->server methods:**
- `RewindTo(double elapsedSeconds)` -> `RewindResultDto(bool Success, string? Message)`
- `TakeControl()` -> `void`
- `GetTimelineInfo()` -> `TimelineInfoDto(double Current, double TapeEnd, bool IsPlayback, bool IsAvailable)`
- `ExportRecording()` -> `string` (JSON of `SessionRecording`)
- `LoadRecording(string recordingJson)` -> `RewindResultDto`

`Current` is `scenario.ElapsedSeconds`. `TapeEnd` is `PlaybackEndSeconds` (0 when in live mode). `IsPlayback` reflects the current mode.

### 4.2 Extend `SimulationStateChanged`

Change broadcast from `(bool isPaused, int simRate)` to include elapsed time and playback state:

```csharp
// TrainingBroadcastService.cs
_ = _hub.Clients.Group(room.GroupName).SendAsync(
    "SimulationStateChanged",
    scenario.IsPaused,
    (int)scenario.SimRate,
    scenario.ElapsedSeconds,
    scenario.IsPlaybackMode,
    scenario.PlaybackEndSeconds);
```

SignalR JSON protocol handles extra fields gracefully — existing clients that bind 2 params ignore the rest.

### 4.3 New server->client events

- `TimelineRewound(double elapsedSeconds, List<AircraftStateDto> aircraft, bool isPlayback, double tapeEnd)` — client clears and rebuilds aircraft state
- `PlaybackModeChanged(bool isPlayback, double tapeEnd)` — sent on take-control or auto-pause at tape end

### 4.4 Files modified

- `Yaat.Server/Hubs/TrainingHub.cs` — `RewindTo`, `TakeControl`, `GetTimelineInfo`, `ExportRecording`, `LoadRecording`
- `Yaat.Server/Dtos/TrainingDtos.cs` — `RewindResultDto`, `TimelineInfoDto`
- `Yaat.Server/Simulation/TrainingBroadcastService.cs` — extended `SimulationStateChanged`, new events
- `src/Yaat.Client/Services/ServerConnection.cs` — new events, hub calls

---

## Phase 5: Client Protocol & UI (Yaat.Client)

### 5.1 ServerConnection additions

- Subscribe to extended `SimulationStateChanged` (5 params: isPaused, simRate, elapsed, isPlayback, tapeEnd)
- Subscribe to `TimelineRewound` and `PlaybackModeChanged` events
- Add hub calls: `RewindToAsync(double)`, `TakeControlAsync()`, `GetTimelineInfoAsync()`, `ExportRecordingAsync()`, `LoadRecordingAsync(string)`
- New events for each

### 5.2 ViewModel additions (MainViewModel.Timeline.cs partial)

New partial file to keep the main VM manageable:

```csharp
[ObservableProperty] private double _scenarioElapsedSeconds;
[ObservableProperty] private bool _isTimelineAvailable;
[ObservableProperty] private bool _isPlaybackMode;
[ObservableProperty] private double _playbackTapeEnd;

// Formatted elapsed time ("02:35")
public string ElapsedTimeDisplay => FormatElapsed(ScenarioElapsedSeconds);

// Formatted tape end ("02:35") — shown during playback
public string TapeEndDisplay => FormatElapsed(PlaybackTapeEnd);

[RelayCommand] private async Task RewindTo(double seconds);
[RelayCommand] private async Task RewindQuick(int secondsBack);
[RelayCommand] private async Task TakeControl();
[RelayCommand] private async Task SaveRecording();
[RelayCommand] private async Task LoadRecording();
```

### 5.3 Timeline UI

Add a timeline bar between the scenario bar and the aircraft data grid in `MainWindow.axaml`:
- Elapsed time label (mm:ss format)
- Slider from 0 to `PlaybackTapeEnd` (in playback) or `ScenarioElapsedSeconds` (in live)
- Slider enabled only when paused
- Quick rewind buttons: `|<` (to start), -30s, -15s
- Mode indicator: "PLAYBACK" or "LIVE"
- "Take Control" button (visible only during playback)
- Visible only when a scenario is loaded

### 5.4 Slider interaction

- While playing (live mode): slider tracks server-reported elapsed time (read-only), max = current elapsed
- While playing (playback mode): slider tracks elapsed time, max = tape end, read-only
- While paused: slider is interactive; on release, debounce (300ms) then call `RewindToAsync`
- Guard against feedback loops: `_isApplyingServerUpdate` flag suppresses `ValueChanged` during server-driven updates
- In playback mode, slider shows the tape end as the max, with current position moving toward it
- In live mode, slider max grows with elapsed time

### 5.5 Save / Load UI

**Save**: Menu item under Scenario menu: "Save Recording...". Opens a file save dialog (`.yaat-recording` extension, JSON content). Calls `ExportRecordingAsync()`, writes result to disk.

**Load**: Menu item: "Load Recording...". Opens a file picker, reads JSON, calls `LoadRecordingAsync(json)`. Server loads the scenario and enters playback mode. Client updates UI from the `TimelineRewound` event.

The `.yaat-recording` files are plain JSON (the `SessionRecording` type), human-readable and shareable.

### 5.6 TimelineRewound handler

On receiving `TimelineRewound(elapsed, aircraft, isPlayback, tapeEnd)`:
1. Clear `Aircraft` observable collection
2. Rebuild from the provided aircraft DTOs
3. Update elapsed time, playback mode, tape end
4. Update UI (slider position, labels, mode indicator)

### 5.7 PlaybackModeChanged handler

On receiving `PlaybackModeChanged(isPlayback, tapeEnd)`:
1. Update `IsPlaybackMode` and `PlaybackTapeEnd`
2. Update UI (mode indicator, take-control button visibility)

### 5.8 Files modified

- `src/Yaat.Client/Services/ServerConnection.cs` — new events, hub calls, extended subscription
- New: `src/Yaat.Client/ViewModels/MainViewModel.Timeline.cs` — timeline properties, commands, handlers
- `src/Yaat.Client/Views/MainWindow.axaml` — timeline bar UI, menu items
- `src/Yaat.Client/Views/MainWindow.axaml.cs` — slider interaction wiring

---

## Implementation Order

```
Phase 1 (Seeded RNG — Yaat.Sim + yaat-server)
  └─> Phase 2 (Action recording — yaat-server)
        └─> Phase 3 (Rewind logic — yaat-server)
              └─> Phase 4 (Hub protocol — yaat-server + Yaat.Client)
                    └─> Phase 5 (Client UI — Yaat.Client)
```

Phases 1-2 are independently testable and low-risk. Phase 3 is the core logic. Phase 4-5 are the protocol + visual layer.

---

## Edge Cases

- **Rewind past a weather change**: The action log includes `RecordedWeatherChange`. Replay applies it at the correct timestamp, so weather state is consistent.
- **Rewind past a spawn/delete**: Scenario reload restarts with original aircraft. Recorded spawns/deletes replay at their timestamps.
- **Generators with randomized intervals**: Seeded RNG ensures identical spawn timing on replay.
- **Track commands (HO, ACCEPT, DROP)**: Recorded as `RecordedCommand`, replayed through the same dispatch path. Track ownership reconstructed identically.
- **Multiple rewinds**: Each rewind re-loads from scratch. The full ActionLog is preserved (not truncated) so the user can always play back from any point.
- **Rewind to t=0**: Equivalent to reloading the scenario. Enters playback mode with cursor at the start.
- **Take control then rewind again**: Take-control truncates the log at the current time. New commands are recorded from there. A subsequent rewind replays the (now-shorter) log. This lets the user try different approaches from the same branch point.
- **Take control at tape end**: The sim already auto-pauses at tape end and exits playback mode, so the user is effectively in live mode. They can just start issuing commands.
- **New command during playback**: Implicitly triggers take-control. The recorded future is discarded, the new command applies and is recorded. Seamless transition.
- **Very long scenarios**: 60 min = 3600 ticks. Still < 5 seconds of replay on modern hardware. Acceptable for a training tool.
- **Loading a recording with missing ARTCC/nav data**: The scenario JSON may reference airports, fixes, or procedures the server hasn't cached. `LoadScenarioAsync` already handles ARTCC config loading. If nav data is missing (different AIRAC cycle), commands referencing unknown fixes will fail gracefully as they do in normal operation.
- **Recording file compatibility**: The `SessionRecording` JSON schema may evolve. Include a version field if needed later; for now, the format is simple enough that backward compat is not a concern (unreleased software).
- **Playback broadcasts**: During playback (not fast-forward), terminal entries and command responses broadcast normally. The user sees the commands executing in the terminal as if they were being issued live. This makes playback informational/educational — the user can watch the sequence of instructions and aircraft responses.

---

## Verification

1. **Unit tests (Phase 1)**: RNG determinism — same seed produces same beacon codes and callsigns across two runs
2. **Unit tests (Phase 2)**: Action recording — verify all action types logged with correct timestamps; verify recording suppressed during playback mode
3. **Integration test (Phase 3 — rewind)**: Load scenario, tick 30s with commands at t=10/t=20, rewind to t=15, verify state matches. Rewind to t=0, verify matches fresh load.
4. **Integration test (Phase 3 — playback)**: Rewind to t=10, resume, verify command at t=20 auto-executes. Verify sim auto-pauses at tape end. Verify terminal entries appear during playback.
5. **Integration test (Phase 3 — take control)**: Rewind to t=10, resume playback, take control at t=15, verify log truncated, new commands record normally. Also test implicit take-control via issuing a command during playback.
6. **Integration test (Phase 3 — save/load)**: Export recording, load in new room, play back, verify identical aircraft state at end. Verify metadata fields populated.
7. **Determinism test**: Run scenario 60s, rewind to 0, play back 60s — all aircraft positions, callsigns, and beacon codes must match exactly.
8. **Manual test (Phase 5)**: Load a scenario, run 60s, pause, drag slider to 30s, verify aircraft jump, press play, watch commands auto-execute in terminal, verify pause at tape end. Click "Take Control", issue new commands, verify live mode. Save recording, load in fresh session, verify playback.
