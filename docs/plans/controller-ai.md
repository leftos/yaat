# Controller AI for Solo Training — Design Plan

## Context

In YAAT solo-training mode a student controls **one** tower position; the other positions that an
RPO/Instructor would normally play are currently absent. A student training **Ground** has no Local
controller departing/landing the aircraft they taxi, and no one to coordinate runway crossings or
intersection departures with. The goal is an **AI controller** that plays an adjacent position so a
solo student gets a realistic two-position tower.

The feature is large, so it is staged. This plan delivers **Milestone 1: an autonomous AI "Local
Control"** — a deterministic, replay-safe controller that sequences and clears traffic in the
background (CTO / LUAW / CLAND / pattern-entry / go-around) while the student trains Ground. There is
**no human↔AI coordination yet** (that is M2); the AI simply behaves like a believable Local on its
own. The architecture is deliberately the mirror image of YAAT's existing **pilot AI**: a
deterministic per-tick brain (`PilotProactive` → `ControllerProactive`) whose only outputs are the
same canonical commands a human issues, with any speech/UI as a cosmetic layer on top.

### Decisions locked with the user (this brainstorm)
- **Brain:** deterministic rule-based per-tick state machine (no LLM in the sim core).
- **Coordination:** deferred to M2; M1 is autonomous-only.
- **First scope:** autonomous AI Local while student trains Ground.
- **Authority:** phase-derived **soft** ownership (no hard dispatch block).

### Decisions taken with stated assumptions (override at approval if desired)
- **Departure separation rule:** ship the **flat 6000-ft conservative baseline** (single Cat-III
  bucket), not the full SRS-category 3000/4500/6000-ft derivation. Easily upgraded later; the
  baseline only ever errs toward *more* spacing.
- **Enablement field:** a single `bool AiLocalControlEnabled` on `SimScenarioState` (not a
  `List<string> AiControlledPositions`) — M1 automates exactly one position.
- **Enablement UX:** a Settings toggle pushed on scenario load, mirroring `SoloTrainingMode`.

### Milestone roadmap (M2+ are NOT in this plan, listed for context)
- **M1 (this plan):** autonomous AI Local; phase-derived soft ownership; replay-safe.
- **M2:** structured (UI/command) coordination — runway crossings, intersection departures, APREQs,
  explicit handoffs — as a first-class request object (Requested → Approved/Denied → Acked),
  bidirectional. (Voice interphone is a later layer on top of the same structured object.)
- **M3+:** other position pairings (Clearance+Ground, Local+Approach), voice interphone.

---

## Milestone 1 design

### 1. The decision brain — `ControllerProactive` (Yaat.Sim)

A new **static** class mirroring `Pilot/PilotProactive.cs`: pure per-tick functions, no per-aircraft
object, no internal mutable state (everything persists on `AircraftState` / `SimScenarioState`). Each
tick, for each aircraft the AI Local nominally owns, it evaluates the decision rules (§5) and, when a
rule fires, issues a canonical command through an injected `issue` delegate (§3). It also performs the
ownership **take/handback** at the two transfer points (§2).

- Lives at `src/Yaat.Sim/Controller/ControllerProactive.cs`.
- Entry point shape: `Tick(AircraftState ac, SimScenarioState scenario, IReadOnlyList<AircraftState> world, Func<AircraftState,string,CommandResult> issue, <lookup delegates>)`.
- Aircraft iteration order is stabilized by `(SpawnedAtSeconds, Callsign ordinal)` so multi-aircraft
  ties (e.g. two arrivals on final) resolve deterministically.

### 2. Phase-derived soft ownership — `ControllerOwnershipDeriver` (Yaat.Sim)

`src/Yaat.Sim/Controller/ControllerOwnershipDeriver.cs` — pure function
`DeriveNominalController(AircraftState) → NominalController { None, Ground, Local }` over the
aircraft's current phase type (+ `IsOnGround`). Mapping (phase classes confirmed in this worktree):

- **Local:** `LineUpPhase`, `LinedUpAndWaitingPhase`, `TakeoffPhase`, `InitialClimbPhase`,
  `DepartureProcedurePhase`; `FinalApproachPhase`, `LandingPhase`, `LowApproachPhase`,
  `TouchAndGoPhase`, `StopAndGoPhase`, `GoAroundPhase`, `RunwayHoldingPhase` (LAHSO),
  `HelicopterTakeoff/LandingPhase`; all `Phases/Pattern/*` (`PatternEntryPhase`, `Upwind`,
  `Crosswind`, `Downwind`, `Base`, `MidfieldCrossing`, `TeardropReentry`, `VfrFollow`).
- **Ground:** all `Phases/Ground/*` (`Pushback*`, `HoldingAfterPushback`, `Taxiing`, `Following`,
  `AirTaxi`, `AtParking`, `HoldingInPosition`, `CrossingRunway`, `ClearRunway`, `RunwayExit`,
  `HoldingAfterExit`) and `HoldingShortPhase`.
- **None (not AI-owned in M1):** APP/CTR-context phases (`ApproachNavigationPhase`,
  `InterceptCoursePhase`, `ProcedureTurnPhase`, `HoldingPatternPhase`) unless tied to tower pattern
  context. Free-flying (`Phases is null`): airborne-near-field → Local, on-ground → Ground.

**Transfer points** (where the AI takes/hands back):
1. **Ground → Local:** `HoldingShortPhase` → first tower phase (departure "ready" at the hold short).
2. **Local → Ground:** `LandingPhase` → `RunwayExitPhase` (after rollout; `PhaseRunner` auto-appends
   `RunwayExitPhase → HoldingAfterExitPhase`).

**Soft** = ownership only routes pilot-speech/datablock attribution (§6); it never blocks the student
from issuing a command to an AI-owned aircraft (matches today's free dispatch). M1's runway-crossing
safety falls out of the AI's **occupancy gate** (§5 Gate B): if the student taxis an aircraft across
the active runway, the AI sees it occupied and withholds CTO — de-facto safe behavior without an
approval handshake. The handshake is M2.

### 3. Issuing + recording a command (replay-safety crux)

The brain runs **live-only** and never during replay; on replay the AI's previously-recorded commands
re-drive the sim — identical philosophy to `RecordedAircraftSpawn`, so brain logic can evolve without
breaking historical recordings.

There are **two mutually-exclusive tick worlds** (verified):
- **Live server:** `SimulationHostedService` → `RoomEngine.AdvanceOneSecond` →
  `TickProcessor.ProcessPostPhysics` (`yaat-server/.../TickProcessor.cs:119`). This path **never**
  calls `SimulationEngine.TickPostPhysics`. Commands here are recorded via
  `RoomEngine.Record → Recording.Record` (`RoomEngine.cs:75,743`).
- **Replay / tests / standalone-sim:** `SimulationEngine.TickPostPhysics`
  (`src/Yaat.Sim/Simulation/SimulationEngine.cs:644`, where `PilotProactive.Tick*` already hooks at
  653-656). The only in-sim recording sink is `scenario.ActionLog.Add(...)`
  (`RecordGeneratedAircraftSpawn`, `SimulationEngine.cs:2686-2694`).

Because the two worlds never both run, exactly-once recording is guaranteed by routing through the
**host's own sink** via the injected `issue` delegate:
- **Sim host** supplies `SimulationEngine.IssueControllerCommand(ac, command)` — dispatches through a
  `DispatchContext` copied from `SendCommand` (`SimulationEngine.cs:1265-1281`) +
  `CommandDispatcher.DispatchCompound`, then appends a `RecordedCommand(ElapsedSeconds, callsign,
  command, Initials="AI", ConnectionId="AI:LOCAL")` to `scenario.ActionLog`, **guarded** by
  `!_isReplayingRecordedActions && !scenario.IsPlaybackMode` (copy the guard at `:2689`).
- **Server host** (`TickProcessor.ProcessControllerAi`) supplies a delegate that dispatches through
  the same shared `CommandDispatcher` and records via `RoomEngine.Record(new RecordedCommand(...,
  "AI", "AI:LOCAL"))` — exactly the human-command path (`RoomEngine.cs:743`), so AI commands persist
  and round-trip identically to human ones.

`IssueControllerCommand` must **NOT** (a) call `SoloTrainingEvaluator.RecordControllerCommand` (that
is the *student's* scorecard) or (b) queue a solo pilot readback / arm the frequency airtime gate (AI
commands are not student-audible). Reserved identity constants live in
`src/Yaat.Sim/Controller/ControllerAiIdentity.cs` (`Initials="AI"`, `ConnectionId="AI:LOCAL"`, plus a
factory for the synthetic AI-Local `TrackOwner`). `ConnectionId` is inert for these verbs (only
track/AS replay reads it; AI Local issues none).

### 4. Live-vs-replay gate

- Sim hook in `TickPostPhysics` solo block: gate on `!_isReplayingRecordedActions`
  (`SimulationEngine.cs:66`, set true in all three replay paths at 868/1097/1179, restored in
  `finally`). Same pattern as `ProcessGenerators` (`:1903`).
- Server hook in `ProcessPostPhysics`: gate on `IsPlaybackMode == false && SoloTrainingMode &&
  AiLocalControlEnabled`.

### 5. Decision rules (aviation-reviewed; 7110.65 grounded)

Conservative, single-active-runway, deterministic predicates. Shared primitives: `prevDep`/`arrival`
state, `distDownRwy`, `distToThresh`, `clearOfRwy`, CWT class via `WakeTurbulenceData.GetCwt`.

- **CTO** (`MAY_ISSUE_CTO = readyAtRunway AND A AND B AND C`):
  - **A — dep-behind-dep (3-9-6.a):** `airborne(prevDep) AND distDownRwy(prevDep) ≥ 6000 ft`
    (flat Cat-III baseline).
  - **B — occupancy / arrival (3-9-6.b, 3-9-5):** all landing aircraft `clearOfRwy`, **or** nearest
    inbound arrival `distToThresh ≥ 6 NM` (anticipated-separation gap-shoot). This gate is also what
    makes the AI defer to a student runway crossing.
  - **C — wake on departure (TBL 5-5):** **time** gate `(now − t_departRoll) ≥ 120 s` behind
    Super/Heavy/B757, else 0. Use CWT only to *identify* the class; do **not** use the on-approach
    mile table for departures.
  - **LUAW (3-9-4):** issue only when the sole blocker is a near-term Gate B with the conflicting
    aircraft already on/clearing the runway, within a 90-second imminence guard; else hold short.
- **CLAND** (`MAY_ISSUE_CLAND = establishedInbound AND L1 AND L2 AND L3`):
  - **L1 (preceding arrival, 3-10-3.a.1):** `clearOfRwy(prev)` or far enough to anticipate.
  - **L2 (preceding departure, 3-10-3.a.2):** departure airborne past runway end / SRS distance, or
    anticipated.
  - **L3 (wake/radar in-trail, 5-5-4.h):** `gap ≥ max(WakeTurbulenceData.OnApproachWakeSeparationNm,
    3.0 NM)` — reuse YAAT's existing on-approach CWT table verbatim so the AI enforces exactly the
    spacing the arrival generator creates. Withhold = simply don't issue CLAND yet (aircraft
    continues final); never withhold against a clear runway (3-10-8).
- **Go-around** at `distToThresh ≤ 1.0 NM`: runway occupied inside the threshold (incl. a LUAW
  aircraft — 3-10-6.b), in-trail/wake gap lost, or preceding arrival still rolling and won't clear.
  Never cancel a *rolling* departure for sequencing (3-9-11) — the go-around always goes to the
  arrival.
- **Sequencing brain (3-9-5):** one tunable — `distToThresh(arr) ≥ 6 NM` and runway clear → launch
  the departure (gap-shoot); `< 6 NM` → land the arrival first, departure waits (LUAW if imminent).

**Explicitly deferred (flag, do not build in M1):** intersecting/converging runways (3-9-8/9,
3-10-4), parallel-dependent ops, intersection departures, LAHSO, opposite-direction ops, safety-logic
"full core alert" relaxations. Assume the no-safety-logic conservative branch everywhere.

### 6. Pilot-speech interaction

The pilot-speech gate `PilotInitialContactEligibility.CanInitiateWithStudent`
(`src/Yaat.Sim/Pilot/PilotInitialContactEligibility.cs:31`) already keys on `aircraft.Track.Owner` vs
`scenario.StudentPosition`. So when the AI takes an aircraft (Ground→Local transfer), it sets
`Track.Owner` to the synthetic AI-Local `TrackOwner`; pilots then stop soliciting the student Ground.
On handback (Local→Ground), it restores `Track.Owner` to the student so taxi-request speech resumes.
**No change to `PilotInitialContactEligibility` itself.** AI-owned aircraft produce no
student-directed audio (the AI never "talks" in M1).

### 7. Configuration / enablement

Add `bool AiLocalControlEnabled` to `SimScenarioState` (near `SoloTrainingMode`, `:68`) and round-trip
it exactly like `SoloTrainingMode`:
- `SimScenarioState.ToSnapshot()` + `ScenarioSnapshotDto` (new optional `bool`, default `false`, **no
  migration** per snapshots-and-replay rules) + `SimulationEngine.RestoreFromSnapshot`.
- Live toggle via a new `RecordedSettingChange("AiLocalControlEnabled", …)`: add
  `SimulationEngine.ApplySettingChange` case (~`:3027`), `SimControlService.SetAiLocalControl` (mirror
  `SetSoloTrainingMode`), `RoomEngine.SetAiLocalControl`, `TrainingHub.SetAiLocalControl` RPC.
- Client: `ServerConnection` RPC, a `SettingsViewModel` toggle, and a push-on-load in
  `MainViewModel.Scenario.cs` alongside `SendSoloTrainingMode()`.

### 8. Determinism

M1 brain needs **no RNG** — fully deterministic over phase/position/`ElapsedSeconds` with stable
`(SpawnedAtSeconds, Callsign)` tie-breaking. (If varied AI reaction timing is wanted later, add a
dedicated live-only `ControllerAiRng` and bake the sampled delay into the `RecordedCommand`, mirroring
`ReactionDelaySeconds` — **deferred**, do not build in M1.)

---

## New files
- `src/Yaat.Sim/Controller/ControllerProactive.cs` — deterministic per-tick brain (§1, §5).
- `src/Yaat.Sim/Controller/ControllerOwnershipDeriver.cs` — `DeriveNominalController` + `enum NominalController` (§2).
- `src/Yaat.Sim/Controller/ControllerAiIdentity.cs` — reserved `Initials`/`ConnectionId` + AI `TrackOwner` factory (§3).

## Files to modify
- `src/Yaat.Sim/Simulation/SimulationEngine.cs` — `IssueControllerCommand`; `ControllerProactive.Tick`
  hook in `TickPostPhysics` solo block (gated `!_isReplayingRecordedActions`); `ApplySettingChange`
  case; snapshot map for `AiLocalControlEnabled`.
- `src/Yaat.Sim/Simulation/SimScenarioState.cs` — `AiLocalControlEnabled` + `ToSnapshot` map.
- `src/Yaat.Sim/Simulation/Snapshots/ScenarioSnapshotDto.cs` — optional `AiLocalControlEnabled`.
- `yaat-server/src/Yaat.Server/Simulation/TickProcessor.cs` — `ProcessControllerAi(room)` sibling of
  `ProcessPilotProactive` in `ProcessPostPhysics`, gated; supplies the RoomEngine-record `issue` delegate.
- `yaat-server/src/Yaat.Server/Simulation/SimControlService.cs` — `SetAiLocalControl` (records setting change).
- `yaat-server/src/Yaat.Server/Simulation/RoomEngine.cs` — `SetAiLocalControl`.
- `yaat-server/src/Yaat.Server/Hubs/TrainingHub.cs` — `SetAiLocalControl` RPC.
- Client: `ServerConnection`, `SettingsViewModel`, `MainViewModel.Scenario.cs` (push-on-load).
- Docs: `COMMANDS.md` (none added, but note AI-issued verbs), `docs/yaat-vs-atctrainer.md`,
  `USER_GUIDE.md` (solo AI Local toggle), `docs/architecture.md` (+ a new `docs/controller-ai.md`
  subsystem doc), `CHANGELOG.md` (user-visible: "Solo training: optional AI Local controller").

## Open risks (resolve during implementation)
- **R1 Recording sink unification:** confirm whether `scenario.ActionLog` and `RoomEngine.Recording`
  are one list or two; route the live-server AI command through `RoomEngine.Record` (human path) and
  the standalone-sim path through `scenario.ActionLog`. Verify exactly-once with a record-then-replay
  test before building rules.
- **R2 `Track.Owner` collision:** `TickProcessor.ProcessDeferredAutoTrack` (`:1250`) and the
  flight-plan-creator autotrack also write `Track.Owner` for departures. Add a precedence rule so AI
  ownership isn't stomped when `AiLocalControlEnabled` (e.g. AI owns only phase-derived-Local aircraft;
  suppress airport-based autotrack for those).
- **R3 Scorecard isolation:** AI commands must never hit `SoloTrainingEvaluator.RecordControllerCommand`.
- **R4 Audio isolation:** AI commands must never queue solo readbacks / arm the airtime gate.
- **R5 Scope discipline:** AI issues zero handoff/coordination commands in M1; ownership is derived + soft.

---

## Verification

**TDD order (build guardrails first):**
1. **Replay-safety harness first (R1):** a `[Fact]` that enables `AiLocalControlEnabled`, ticks live
   until the AI issues a CTO, captures the recording, then `ReplayFromStartTo`/`ReplayOneSecond`-replays
   and asserts the identical command fires at the identical `ElapsedSeconds` with `Initials="AI"` —
   and that the brain does **not** run during replay. This proves the determinism story before any rules.
2. **Ownership unit tests:** `DeriveNominalController` over representative phases incl. both transfer
   points; assert `Track.Owner` take/handback and that pilot check-in to the student is suppressed for
   AI-owned aircraft and resumes on handback.
3. **Decision-rule tests (§5)** with real airport layouts via `TestVnasData.EnsureInitialized()`:
   dep-behind-dep 6000-ft gate, occupancy/gap-shoot Gate B, wake 120-s gate, CLAND L1–L3 incl. the
   `OnApproachWakeSeparationNm` floor, go-around at ≤1 NM, and the 6-NM sequencing fork. Each as a
   failing-test-first TDD pair.
4. **Re-review** the final rule implementation with `aviation-sim-expert` (mandatory for aviation logic).

**Suite / cross-repo:**
- `timeout 30 dotnet test` on the new Controller tests, then `pwsh tools/test-all.ps1` (Yaat.Sim
  signature changes touch yaat-server).
- `dotnet build -p:TreatWarningsAsErrors=true` clean before commit; `prek run`.

**Manual end-to-end:**
- `dotnet run --project src/Yaat.Client` against local yaat-server, load a Ground-training solo
  scenario with a couple of departures + an arrival, enable the AI Local toggle, and confirm the AI
  departs/lands traffic in the background, holds departures while you taxi an aircraft across the
  active runway, and hands landed aircraft back to you for taxi-in (pilot taxi requests appear).
- Record the session, then rewind/replay and confirm identical AI behavior.
