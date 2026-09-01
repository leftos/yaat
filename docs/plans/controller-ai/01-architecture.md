# 01 — Core Architecture

Part of [Controller AI + Soak Harness](README.md). Covers the core abstractions, the command sink and
its two hosts, the replay contract, and enablement/gating. Positions/handoffs are in
[02](02-positions-and-handoffs.md); the decision framework in [03](03-decision-framework.md).

## Summary

One deterministic rule-based controller-brain library in `Yaat.Sim` (new namespace
`Yaat.Sim.ControllerAi`), hosted by two thin consumers:

1. **Headless** — the soak runner ([07](07-soak-runner.md)) driving the RoomEngine path via
   `HeadlessRoomHost`, running scenarios for many sim-hours and asserting on findings.
2. **Live** — a per-room hook in `TickProcessor.ProcessPostPhysics`, so a dev can watch the AI
   control a live session ([09](09-live-attach.md)).

Design principles, all derived from the current codebase:

- **Brains issue canonical commands only; phases execute everything.** Exactly the pilot-AI posture:
  no parallel execution model, no direct state mutation. Every action goes through the same dispatch
  path a human's command does.
- **AI positions are real identities** (`TrackOwner` + TCP from ArtccConfig) exercising the real
  TRACK/HO/ACCEPT/HFR/REL machinery, so a human can staff any single position and everything else
  keeps working (partial staffing).
- **Live-only brain, host-dispatched commands, exactly-once recording.** Brains never run during
  replay; replays re-drive the AI's *recorded commands*. Determinism of a fresh run comes from
  seeding, not from replaying decisions — so brain logic can evolve without breaking old recordings.
- **A rejected AI command is a bug signal.** `CommandResult` failures are first-class findings, not
  silent retries.

## Core types

All in `src/Yaat.Sim/ControllerAi/` unless noted.

```csharp
enum ControlRole { Ground, Local, Approach, Center }   // ClearanceDelivery folded into Ground for v1

sealed record AiPositionConfig(
    ControlRole Role,
    TrackOwner Identity,          // the REAL identity used for TRACK/HO/ACCEPT
    Tcp? Tcp,                     // null for tower-cab positions with no TCP
    string PositionId,            // ArtccConfig PositionConfig.Id
    string Callsign,              // e.g. OAK_GND
    IReadOnlyList<string> AirportIds);   // jurisdiction airports (tower-cab roles)

interface IPositionBrain
{
    ControlRole Role { get; }
    AiPositionConfig Position { get; }
    void Tick(AiTickContext ctx);         // once per sim-second when enabled
    void Reset();                         // snapshot restore / scenario reload / staffing resume
}

sealed class AiTickContext
{
    IReadOnlyList<AircraftState> Snapshot;   // World.GetSnapshot(), read on-tick only
    SimScenarioState Scenario;
    AiWorldView World;                        // jurisdiction/scoping queries (02)
    IAiCommandSink Sink;                      // the ONLY way to act
    IAiStaffing Staffing;                     // who is human vs AI vs unstaffed (02)
    AiCoordinationBus Coordination;           // GC↔LC ledger (02)
    SerializableRandom AiRng;                 // dedicated seeded stream (03)
    double ElapsedSeconds;
    // + weather, ground layouts, runway config, anomaly log
}
```

### Stateful brains with re-derivable memory

**Decision: stateful per-position objects (`GroundBrain`, `LocalBrain`, …) holding per-aircraft
working memory (`AiAircraftMemo`), with the invariant that all memory is either re-derivable from
world state or safe to reset.**

Why not stateless per-tick functions: pacing, bounded retries, in-flight-command tracking, and
coordination requests are inherently temporal — pure functions of the snapshot would either re-issue
commands every tick or require encoding all that bookkeeping onto `AircraftState`, polluting the sim
model.

Why the memory does **not** round-trip snapshots for correctness: replays never run the brain, so
snapshot fidelity of brain memory is not a replay requirement. The one boundary that matters is
**rewind-then-resume-live in a dev room**: on snapshot restore, `AiControllerService.Reset()` drops
all memos and re-derives intent from world state — phase + ownership + open pilot requests answer
"where was I with this aircraft" for every case that matters (`Ground.HeldForRelease`,
`HoldingShortPhase`, open `PilotPendingRequest`s all survive snapshots already). Transient pacing
timers and retry counters reset; this shifts timing only, never correctness.

**Documented v1 limitation:** bit-identical determinism is guaranteed for a full run from t=0 with a
fixed seed, not across a rewind boundary. A later milestone can add a `ControllerAiStateDto` to
`ScenarioSnapshotDto` if soak triage ever needs rewind-exact reproduction.

## The command sink and the dispatch/replay contract

Brains never dispatch. They emit requests through one interface; the **host dispatches**:

```csharp
sealed record AiCommandRequest(AiPositionConfig From, string Callsign, string Canonical, AiIntent Intent);
sealed record AiCommandOutcome(AiCommandRequest Request, bool Success, string? Reason,
    CanonicalCommandType? RejectedType);

interface IAiCommandSink
{
    void Issue(AiCommandRequest request);
    IReadOnlyList<AiCommandOutcome> DrainOutcomes();   // consumed at the START of the next brain tick
}
```

Track-scoped commands carry the `AS {tcp}` prefix (the existing acting-position mechanism
`RoomEngine.RecordAndDispatch` already uses for CRC-originated commands) so replays re-drive them
under the right identity. Outcomes are drained next tick rather than returned inline, which makes the
two hosts uniform and keeps async dispatch off the hot path.

**The locked contract** (shared with [07](07-soak-runner.md)):

- The host dispatches every `AiCommandRequest` through **`RoomEngine.SendCommandAsync`** with a
  synthetic connection id `"AI:{positionId}"` and initials `"AI"`. `connectionId` is just a string
  key (`RoomEngine.SendCommandAsync`, RoomEngine.cs:826) — the AI's commands get production routing,
  `Record(new RecordedCommand(...))`, terminal echo, and broadcast identically to a human's.
- **Brains never run during replay or playback.** Guards: headless entry point is a separate
  `SimulationEngine.TickControllerAi()` never called by replay paths; live entry point checks
  `IsPlaybackMode` and `room.IsBroadcastSuppressed` (temp replay engines). Replays re-drive the
  recorded `AS`-prefixed commands from the ActionLog with the AI off.
- The soak runner dispatches requests **between ticks** (awaiting `SendCommandAsync` before
  `AdvanceOneSecond`), like a human typing between updates. The live host's `ProcessControllerAi`
  collects requests during its tick slot and dispatches after the tick body completes, capturing
  results into the outcome queue — the tick thread never blocks on dispatch.
- Whatever pilot-request bookkeeping normal dispatch performs must fire for AI commands too; the
  HFR auto-CTO precedent calls `PilotRequestTracker.ApplyControllerResponse` explicitly
  (SimulationEngine.cs:4146) — CA0 verifies which paths need it.

A pure-Yaat.Sim sink (`EngineAiCommandSink`: `CommandParser.ParseCompound` → `DispatchContext` with
`IsScenarioScripted: true` → `CommandDispatcher.DispatchCompound`, recording to `scenario.ActionLog`
guarded by `!_isReplayingRecordedActions`) is retained **only for brain unit tests** in
`tests/Yaat.Sim.Tests`. Its verb coverage (aviation verbs certainly; track/coordination verbs only if
the CA0 routing-parity spike finds the sim-side engines sufficient) bounds what brain unit tests can
exercise; anything beyond that is integration-tested through `HeadlessRoomHost`.

### Routing-parity spike (CA0 prerequisite)

Track and coordination routing currently lives server-side (`TrackCommandHandler`,
`CoordinationCommandHandler`) wrapping sim-side engines (`TrackEngine` is in `Yaat.Sim.Commands`;
`SimulationEngine`'s deferred dispatch already "handles the track engine directly, mirroring
DispatchSinglePreset"). CA0 must either verify the sim-side engines cover TRACK/HO/ACCEPT/DROP/PO +
the RD family headless, or extract a shared router used by the test sink. Cross-repo change, planned
together per house rules.

## Hosting

`AiControllerService` (Yaat.Sim) owns the brain list and ticks enabled brains **in deterministic
order**: fixed role order Ground → Local → Approach → Center (upstream positions act first within a
tick, so e.g. a crossing approval granted by Local this tick is visible to Ground next tick —
one-tick coordination latency is deliberate and realistic), ordinal by `PositionId` within a role.

- **Headless:** `SimulationEngine.TickControllerAi()` — public, called by the host after
  `TickPostPhysics()`; internally guarded by `scenario.ControllerAi is not null &&
  !_isReplayingRecordedActions && !scenario.IsPlaybackMode`. It is *not* called from inside
  `TickPostPhysics` (which live never runs but replay engines do) — keeping it a separate entry point
  makes "brain never runs in replay" structural.
- **Live:** `TickProcessor.ProcessControllerAi(room)` — new `Run("Post.ControllerAi", …)` entry
  placed **after `Post.PilotProactive`** (same-tick pilot check-ins visible) **and before the
  warnings/pilot-speech drains** (AI-triggered readbacks drain the same tick). Skips entirely when
  `room.IsBroadcastSuppressed`. Registers in the `TickTimings` bucket system from day one — AI work
  must stay well inside the shared 800 ms per-wall-tick budget across all rooms.

Per-tick work is one snapshot scan per brain with pacing early-outs, O(aircraft). Per-sim-second
cadence (never sub-tick).

## Position instantiation from ArtccConfig

`AiPositionResolver.Resolve(ArtccConfig, primaryAirportId, ControllerAiConfig)
→ IReadOnlyList<AiPositionConfig>`:

- **Role inference** (no ground/local/approach/center enum exists today — it is implicit in facility
  config): facility `Type` picks the family; within a tower cab, `PositionConfig.Callsign` suffix
  (`_GND` → Ground, `_TWR` → Local, `_DEL` → skipped v1); positions with `StarsConfiguration` →
  Approach; positions with `EramConfiguration.SectorId` → Center.
- An explicit per-scenario **override table** in `ControllerAiConfig` handles naming oddities (the
  inference is heuristic; overrides make it deterministic where configs are weird — e.g. combined
  TWR/GND sharing a TCP).
- Each AI position's `TrackOwner` is built with the same factories the rest of the code uses, so
  `MatchesPosition`, auto-accept skip checks, and CRC display work unmodified.
- Resolution runs at scenario load and on config change.

## Configuration and enablement

- **`SimScenarioState.ControllerAi : ControllerAiConfig?`** (null = feature off):
  `{ int Seed; List<string> EnabledPositionIds; Dictionary<string, ControlRole> RoleOverrides;
  pacing overrides }`. Persisted in `ScenarioSnapshotDto` so rewinds/replays know AI was on (brains
  still never run in replay).
- **Server gating is process-level, defense in depth** (details in [09](09-live-attach.md)): config
  key `Yaat:ControllerAi:Enabled`, default false; the AI service registrations are simply not added
  to DI unless true, and startup refuses the flag under `ASPNETCORE_ENVIRONMENT == Production`.
  There is no hub method that can enable it remotely when the flag is off. Published builds and
  public servers never see the feature.
- **Headless:** the soak runner constructs `ControllerAiConfig` directly — no gating needed.
- Client UI: none initially; a dev-room command suffices ([09](09-live-attach.md)).

## Proposed file map (core)

- `src/Yaat.Sim/ControllerAi/`: `ControlRole.cs`, `AiPositionConfig.cs`, `AiPositionResolver.cs`,
  `AiControllerService.cs`, `AiTickContext.cs`, `IPositionBrain.cs`, `IAiCommandSink.cs`,
  `EngineAiCommandSink.cs` (test sink), `IAiStaffing.cs`, `PositionJurisdiction.cs`,
  `AiWorldView.cs`, `AiPacing.cs`, `AiAircraftMemo.cs`, `AiAnomaly.cs`, `AiCoordinationBus.cs`,
  `Brains/GroundBrain.cs`, `Brains/LocalBrain.cs`, `Rules/…`
- `src/Yaat.Sim/Simulation/SimulationEngine.cs` — `TickControllerAi()`; `SimScenarioState.cs` —
  config + `AiRng` + anomaly log + snapshot DTO mapping.
- `yaat-server/src/Yaat.Server/Simulation/`: `TickProcessor.cs` (`ProcessControllerAi`,
  auto-accept/pointout skips), `ControllerAi/RoomAiCommandSink.cs`, `ControllerAi/RoomAiStaffing.cs`.
- `tests/Yaat.Sim.Tests/ControllerAi/…`

## Risks owned by this doc

- Headless routing parity for track/coordination verbs (CA0 spike; may need shared-router
  extraction, cross-repo).
- Dispatch outside the tick body: verify `RoomEngine.SendCommandAsync` re-entrancy assumptions from
  the runner/live-hook call sites (the auto-CTO precedent is engine-internal, not RoomEngine).
- Solo-training evaluation must not score AI-issued commands as student actions
  (`IsScenarioScripted` should already exclude them on the engine path; the `SendCommandAsync` path
  needs explicit verification).
- Rewind-determinism limitation (memo reset) — accepted v1, revisit only if triage demands it.

## CA0 implementation notes (2026-09-01)

- `AiTickContext` ships without a `Coordination` member; the coordination bus arrives with CA2. `AiRng` is owned by
  `AiControllerService` (seeded from `ControllerAiConfig.Seed`, re-seeded on `Reset()`), not by the scenario, and is
  not snapshotted.
- `SimulationEngine.TickControllerAi()` is a separate public entry the host calls after its sim-second
  (`RoomEngine.AdvanceLiveSecond` does; pure-engine tests call `TickOneSecond(); TickControllerAi();`). It guards on
  `Scenario.ControllerAi`, playback mode and the replay flag, so an AI-driven recording replays its recorded AI
  commands instead of re-running the brains.
- `AiCommandRequest` carries no `AS` prefix. The AI connection id (`AiConnectionId.Format(positionId)`) names the
  acting position, and both replay resolvers (`ReplayTrackApplier.ResolveEffectiveIdentity`,
  `TrackCommandHandler.ResolveEffectiveIdentity`) resolve it from the ARTCC config, so no student facility is needed.
- Engine-side sink coverage (`SimulationEngine.DispatchAiCommand`): aviation compounds through the live pipeline under
  `DispatchOrigin.ControllerAi` (with the reaction-delay deferral baked into the recorded command), track verbs
  through `TrackEngine`; every other recorded-command kind (coordination, strips, consolidation, spawn control, …)
  is refused with "only the live server dispatches it". The parity spike (`AiSinkRoutingParityTests`) pins
  TAXIAUTO / CTO / TRACK / DROP identical through the room and the engine.
- `SimScenarioState.AiAnomalies` is the ledger; `SoakEpisodeRunner` streams its transitions to `anomalies.jsonl`
  and folds opened/instant counts into `report.json` tiers (progress = stuck + unanswered + handoff; safety =
  conflict alert; controller = rejected command).
