# Rewind / Timeline Scrubber Feature

## Context

ATC training requires iteration — the student makes a decision, sees the outcome, and wants to go back to try a different approach. Currently there's no way to undo or revisit past simulation state. This feature adds periodic state snapshots during simulation and a client-side timeline scrubber that lets the user pause, scrub backward to any past point, and resume from there (discarding the future timeline).

This is a cross-repo feature spanning **Yaat.Sim** (snapshot/clone infrastructure), **yaat-server** (timeline storage, rewind logic, hub protocol), and **Yaat.Client** (timeline UI).

---

## Architecture Overview

```
Client                          Server
┌─────────────────────┐        ┌──────────────────────────────────┐
│ Timeline slider     │        │ Tick loop                        │
│ (enabled when       │        │   ├─ tick aircraft               │
│  paused)            │        │   ├─ CaptureSnapshot()           │
│                     │        │   └─ broadcast updates           │
│ Elapsed time label  │        │                                  │
│                     │◄───────│ SimulationStateChanged           │
│ Quick rewind        │        │   (isPaused, simRate, elapsed)   │
│ buttons (-15s,-30s) │        │                                  │
│                     │───────►│ RewindTo(elapsedSeconds)         │
│                     │◄───────│ TimelineRewound(elapsed, aircraft)│
└─────────────────────┘        │                                  │
                               │ SnapshotTimeline per session     │
                               │   (rolling 15-min window)        │
                               └──────────────────────────────────┘
```

**Snapshot strategy**: Full-state clone every tick (1 Hz wall-clock). Server stores up to 15 minutes of simulation time in a rolling window (~67 MB for 50 aircraft). On rewind, the nearest snapshot is restored, future snapshots are discarded, and the sim resumes from that point.

---

## Phase 1: Snapshot Infrastructure (Yaat.Sim)

### 1.1 Add `Phase.Clone()` abstract method

Add to `Phase` base class (`src/Yaat.Sim/Phases/Phase.cs`):
```csharp
public abstract Phase Clone();
```

Each of the 15 concrete Phase subclasses implements `Clone()`, copying all private mutable fields and base-class state (`Status`, `ElapsedSeconds`, clearance requirements). Within the same class, private fields of another instance are accessible, so this is straightforward.

Phase subclasses to implement (15 total):
- Tower: `LinedUpAndWaitingPhase`, `TakeoffPhase`, `InitialClimbPhase`, `FinalApproachPhase`, `LandingPhase`, `GoAroundPhase`, `TouchAndGoPhase`, `StopAndGoPhase`, `LowApproachPhase`, `HoldPresentPositionPhase`, `HoldAtFixPhase`
- Pattern: `UpwindPhase`, `CrosswindPhase`, `DownwindPhase`, `BasePhase`

### 1.2 Add `SourceCommands` to `CommandBlock`

In `src/Yaat.Sim/CommandQueue.cs`, add to `CommandBlock`:
```csharp
public List<ParsedCommand>? SourceCommands { get; init; }
```

Update `CommandDispatcher.DispatchCompound` (in `src/Yaat.Sim/Commands/CommandDispatcher.cs`) to set `SourceCommands = commands.ToList()` when building each block. This enables reconstructing `ApplyAction` on restore.

### 1.3 Make `PhaseList.CurrentIndex` settable internally

`PhaseList.CurrentIndex` has `private set`. Add an internal setter or a `RestoreIndex(int)` method for snapshot restoration:
```csharp
// Option: internal method
internal void RestoreIndex(int index) => CurrentIndex = index;
```

### 1.4 Add `AircraftState.Clone()` method

In `src/Yaat.Sim/AircraftState.cs`, add a `Clone()` method that deep-copies:
- All scalar/string fields (trivial value copy)
- `ControlTargets`: clone each `NavigationTarget` in the route list; copy all nullable value fields
- `CommandQueue`: clone each `CommandBlock` (copy `Trigger` by ref since it's immutable init-only, clone `TrackedCommand` list, copy `SourceCommands` by ref since `ParsedCommand` records are immutable, **skip `ApplyAction`** — it's reconstructed on restore)
- `PhaseList`: clone each `Phase` via `Phase.Clone()`, copy scalar fields, restore `CurrentIndex`

### 1.5 Add `CommandBlock` restore helper

A static method to reconstruct `ApplyAction` from `SourceCommands`:
```csharp
// In CommandDispatcher, make BuildApplyAction internal/public:
internal static Action<AircraftState> BuildApplyAction(List<ParsedCommand> commands)
```

On restore, iterate unapplied blocks and call this to rebuild their `ApplyAction`.

### 1.6 Files modified
- `src/Yaat.Sim/Phases/Phase.cs` — add `Clone()` abstract
- `src/Yaat.Sim/Phases/Tower/*.cs` (11 files) — implement `Clone()`
- `src/Yaat.Sim/Phases/Pattern/*.cs` (4 files) — implement `Clone()`
- `src/Yaat.Sim/CommandQueue.cs` — add `SourceCommands` to `CommandBlock`
- `src/Yaat.Sim/Commands/CommandDispatcher.cs` — set `SourceCommands`, expose `BuildApplyAction`
- `src/Yaat.Sim/AircraftState.cs` — add `Clone()`
- `src/Yaat.Sim/ControlTargets.cs` — add `Clone()` (or inline in AircraftState.Clone)
- `src/Yaat.Sim/Phases/PhaseList.cs` — add `RestoreIndex()`, add `Clone()`
- `src/Yaat.Sim/SimulationWorld.cs` — add `ReplaceScenarioAircraft(scenarioId, List<AircraftState>)`

### 1.7 Tests
- Roundtrip clone for each Phase subclass (clone, verify all fields match)
- Roundtrip clone for AircraftState with command queue and phases
- Verify `ApplyAction` reconstruction from `SourceCommands`

---

## Phase 2: Timeline Storage (yaat-server)

### 2.1 `ScenarioSnapshot` type

In yaat-server (not Yaat.Sim — it wraps server-side session state too):
```csharp
public sealed class ScenarioSnapshot
{
    public required double ElapsedSeconds { get; init; }
    public required List<AircraftState> Aircraft { get; init; }  // cloned
    public required List<DelayedSpawn> RemainingDelayedSpawns { get; init; }
    public required List<ScheduledTrigger> RemainingTriggers { get; init; }
}
```

### 2.2 `SnapshotTimeline` class

Rolling window of snapshots, keyed by elapsed seconds:
- `Add(snapshot)` — append, evict snapshots older than `MaxRetentionSeconds` (default 900s = 15 min)
- `GetNearestBefore(elapsedSeconds)` — find closest snapshot at or before target time
- `TruncateAfter(elapsedSeconds)` — discard future (for rewind)
- `Clear()` — reset on scenario delete

Stored on `ScenarioSession`:
```csharp
public SnapshotTimeline Timeline { get; } = new();
```

### 2.3 Store original scenario data

Add to `ScenarioSession`:
```csharp
public List<DelayedSpawn> OriginalDelayedSpawns { get; init; } = [];
public List<ScheduledTrigger> OriginalTriggers { get; init; } = [];
```

Populated at scenario load time. Used to reconstruct delayed/trigger queues on rewind: filter for `SpawnAtSeconds > targetElapsed` / `FireAtSeconds > targetElapsed`.

### 2.4 Capture snapshots in tick loop

In `SimulationHostedService.RunTickLoop`, after `TickScenario` completes:
```csharp
var aircraft = _world.GetSnapshotByScenario(session.ScenarioId)
    .Select(ac => ac.Clone()).ToList();
session.Timeline.Add(new ScenarioSnapshot
{
    ElapsedSeconds = session.ElapsedSeconds,
    Aircraft = aircraft,
    RemainingDelayedSpawns = session.DelayedQueue.ToList(),
    RemainingTriggers = session.TriggerQueue.ToList(),
});
```

### 2.5 Files modified
- New: `Yaat.Server/Simulation/ScenarioSnapshot.cs`
- New: `Yaat.Server/Simulation/SnapshotTimeline.cs`
- `Yaat.Server/Simulation/ScenarioSession.cs` — add `Timeline`, `OriginalDelayedSpawns`, `OriginalTriggers`
- `Yaat.Server/Simulation/SimulationHostedService.cs` — snapshot capture in tick loop, populate originals at load time

---

## Phase 3: Rewind Logic (yaat-server)

### 3.1 `Rewind()` method on `SimulationHostedService`

```
Rewind(connectionId, targetSeconds):
  1. Look up session for this client
  2. Find nearest snapshot at or before targetSeconds
  3. Pause the session
  4. Remove all aircraft for this scenario from SimulationWorld
  5. Clear CRC visibility state for removed aircraft
  6. Clone each aircraft from snapshot, add to SimulationWorld
  7. Reconstruct ApplyAction on unapplied CommandBlocks
  8. Restore session.ElapsedSeconds
  9. Restore delayed queue (filter originals for SpawnAtSeconds > target)
  10. Restore trigger queue (filter originals for FireAtSeconds > target)
  11. Truncate timeline after this snapshot
  12. Broadcast TimelineRewound to all clients in scenario
```

### 3.2 CRC state reset

On rewind, call `_crcVisibility.Remove(callsign)` for all affected aircraft. CRC state rebuilds automatically on the next tick via `Evaluate()`. Brief visual discontinuity on CRC (acceptable for training).

### 3.3 Hub protocol changes

**TrainingHub new methods:**
- `RewindTo(double elapsedSeconds)` → `RewindResultDto(bool Success, string? Message)`
- `GetTimelineInfo()` → `TimelineInfoDto(double Earliest, double Latest, double Current, bool IsAvailable)`

**SimulationStateChanged extended:**
Change signature from `(bool isPaused, int simRate)` to `(bool isPaused, int simRate, double elapsedSeconds)`. SignalR JSON protocol handles extra fields gracefully — existing clients that bind 2 params ignore the third.

**New server→client event:**
- `TimelineRewound(double elapsedSeconds, List<AircraftDto> aircraft)` — client clears and rebuilds aircraft state

### 3.4 Multi-client behavior

- Rewind pauses the simulation, affecting all clients on the scenario
- Any connected client can trigger rewind
- All clients receive `TimelineRewound` and see the restored state

### 3.5 Files modified
- `Yaat.Server/Simulation/SimulationHostedService.cs` — `Rewind()` method, extended broadcasts
- `Yaat.Server/Hubs/TrainingHub.cs` — `RewindTo`, `GetTimelineInfo` methods
- `Yaat.Server/Hubs/TrainingDtos.cs` (or equivalent) — new DTOs

---

## Phase 4: Client Protocol & UI (Yaat.Client)

### 4.1 ServerConnection additions

- Subscribe to extended `SimulationStateChanged` (3 params)
- Subscribe to `TimelineRewound` event
- Add `RewindToAsync(double)` and `GetTimelineInfoAsync()` hub calls
- New events: `TimelineRewound`, elapsed time tracking

### 4.2 ViewModel additions (MainViewModel)

```csharp
[ObservableProperty] private double _scenarioElapsedSeconds;
[ObservableProperty] private double _timelineEarliest;
[ObservableProperty] private double _timelineLatest;
[ObservableProperty] private bool _isTimelineAvailable;

// Formatted elapsed time ("02:35")
public string ElapsedTimeDisplay => FormatElapsed(ScenarioElapsedSeconds);

[RelayCommand] private async Task RewindTo(double seconds);
[RelayCommand] private async Task RewindQuick(int secondsBack);
```

### 4.3 Timeline UI

Add a timeline bar between the scenario bar and the aircraft data grid in `MainWindow.axaml`:
- Elapsed time label (mm:ss format)
- Slider from `TimelineEarliest` to `TimelineLatest`, tracking `ScenarioElapsedSeconds`
- Slider enabled only when paused
- Quick rewind buttons: -15s, -30s
- Visible only when a scenario is loaded and timeline data is available

### 4.4 Slider interaction

- While playing: slider tracks server-reported elapsed time (read-only)
- While paused: slider is interactive; on release, debounce (300ms) then call `RewindToAsync`
- Guard against feedback loops: use a `_isApplyingServerUpdate` flag to suppress `ValueChanged` during server-driven updates

### 4.5 TimelineRewound handler

On receiving `TimelineRewound(elapsed, aircraft)`:
1. Clear `Aircraft` observable collection
2. Rebuild from the provided aircraft DTOs
3. Update elapsed time and timeline bounds
4. Update UI (slider position, labels)

### 4.6 Files modified
- `src/Yaat.Client/Services/ServerConnection.cs` — new events, hub calls, extended subscription
- `src/Yaat.Client/ViewModels/MainViewModel.cs` — timeline properties, commands, handlers
- `src/Yaat.Client/Views/MainWindow.axaml` — timeline bar UI

---

## Implementation Order

```
Phase 1 (Yaat.Sim: Clone infrastructure)
  └─► Phase 2 (yaat-server: timeline storage)
        └─► Phase 3 (yaat-server: rewind logic + hub protocol)
              └─► Phase 4 (Yaat.Client: protocol + UI)
```

Each phase is testable independently. Phase 1 has unit tests for clone correctness. Phase 2-3 can be tested via hub calls before the client UI exists. Phase 4 is the visual layer.

---

## Verification

1. **Unit tests (Phase 1)**: Clone roundtrip for every Phase subclass, AircraftState with active command queue and phase list, ApplyAction reconstruction
2. **Integration test (Phase 2-3)**: Load scenario, tick 30 seconds, rewind to 15s, verify aircraft positions match the snapshot at 15s, verify delayed queue restored correctly
3. **Manual test (Phase 4)**: Load a scenario, let it run 60s, pause, drag slider to 30s mark, verify aircraft jump to correct positions, press play, verify simulation resumes from 30s
4. **Memory test**: Load a 50-aircraft scenario, let it run 15 minutes, verify memory usage is reasonable (<100MB for snapshots)
