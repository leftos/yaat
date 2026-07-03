# Aircraft Data Model: AircraftState, Satellites, ControlTargets, and SimulationWorld

> Read this before touching `AircraftState`, `ControlTargets`, any `Aircraft*.cs` satellite, or `SimulationWorld` — and before adding any new
> per-aircraft field. This is the field-level + mutator-map + three-projections reference. For the *serialization* mechanics (DTO tree shape,
> schema-migrator versions, the `[JsonIgnore]` list, the add-a-field-needs-migration rule), see [snapshots-and-replay.md](snapshots-and-replay.md);
> this doc does not redraw the DTO tree. For who *runs* the fields each tick, see [tick-loop.md](tick-loop.md) and
> [command-pipeline.md](command-pipeline.md), which treat `AircraftState`/`ControlTargets` as opaque members.

## Overview

`AircraftState` (`src/Yaat.Sim/AircraftState.cs:12`) is the single mutable record that every subsystem reads and writes: physics, phases, command
handlers, the track engine, ground ops, and three separate DTO projections. It carries a handful of top-level identity + kinematics fields directly,
then delegates everything else to thirteen cohesive **satellite objects** plus `ControlTargets`. There is no inheritance and no interface — it is a
plain class whose members are mutated in place.

`SimulationWorld` (`src/Yaat.Sim/SimulationWorld.cs:10`) owns the live set of aircraft behind a single reentrant `lock`. It is the only safe surface
for adding, removing, finding, ticking, and draining aircraft. Mutating an `AircraftState` outside the lock (or outside a tick / handler that already
holds it) races the tick loop.

## The aircraft schema at a glance

### Top-level fields on `AircraftState`

These live directly on the record, not in a satellite:

| Field | Type | Meaning |
|---|---|---|
| `Callsign` | `string` (required) | Identity. Case-insensitive across the world set. |
| `AircraftType` | `string` (required) | **Physical** type — drives physics, performance, Tower Cab datablock. Fixed at spawn. |
| `BaseAircraftType` | `string` (computed) | `AircraftType` with the `H/`, `J/`, `S/` wake prefix stripped (`StripTypePrefix`). |
| `ScenarioId`, `Cid`, `AirportId` | `string?` / `string` | Scenario linkage; controller-tag CID; operational airport context for airport-relative commands. |
| `Position` | `LatLon` | Geographic position, degrees. |
| `TrueHeading`, `TrueTrack` | `TrueHeading` | Nose direction vs ground-track direction (equal in calm wind). |
| `Declination`, `DeclinationCachePosition` | `double` / `LatLon?` | Cached magnetic declination + the position it was computed at. `MagneticHeading`/`MagneticTrack` derive from these. |
| `Altitude`, `IndicatedAirspeed`, `VerticalSpeed`, `BankAngle` | `double` | Vertical position, IAS, climb/descent rate, current bank. |
| `GroundSpeed` | `double` (computed) | On-ground = IAS; airborne = IAS→TAS + cached wind vector. |
| `WindComponents` | `(double N, double E)` | Last observed wind in knots; `internal set` — only `FlightPhysics` writes it. |
| `IsOnGround` | `bool` | Ground vs airborne — gates physics branches and conflict detection. |
| `Targets` | `ControlTargets` (**get-only**) | The autopilot panel. See below. |
| `Queue` | `CommandQueue` | Triggered/queued command blocks (free-flight only — phases bypass it). |
| `Phases` | `PhaseList?` | Active phase state machine; `null` = free-flying. See [phases.md](phases.md). |

A handful of cross-phase one-shot booleans and debrief timestamps also live at top level and **are** snapshot-serialized:
`HasMadeInitialContact`, `HasControllerAcknowledgedInitialContact`, `HasLeftStudentFrequency`, `IsClearedIntoBravo`, `HasAnnouncedLinedUpReady`,
`NoLandingClearanceWarningActive`, `SpawnedAtSeconds`, `CompletedAtSeconds`, `CompletionReason`, `CompletionDetail`, `PendingPilotRequest`.

### The thirteen satellites + `ControlTargets`

Each satellite is a separate class with its own `ToSnapshot`/`FromSnapshot`. Read the listed file for field-level detail.

| Member | Type | One-line purpose | File |
|---|---|---|---|
| `FlightPlan` | `AircraftFlightPlan` | Filed plan: dep/dest/route, **filed** `AircraftType`, equipment, rules, cruise, `RevisionNumber`, `CreatedByOwner`. | `AircraftFlightPlan.cs:10` |
| `Ground` | `AircraftGroundOps` | Layout ref, taxi route, parking/taxiway, `Hold` directive, conflict overrides, pushback heading, spawn-readback gate. | `AircraftGroundOps.cs:13` |
| `Transponder` | `AircraftTransponder` | Mode (A/C/S), assigned vs reported beacon code, IDENT timer. | `AircraftTransponder.cs:9` |
| `Track` | `AircraftTrack` | Ownership + handoff: `Owner`, `HandoffPeer`, `HandoffRedirectedBy`, the H-state booleans, `Pointout`. | `AircraftTrack.cs:10` |
| `Stars` | `AircraftStarsState` | Per-track STARS display: scratchpads (CRC + ASDE-X), temp/pilot altitudes, display inhibitions, TPA, per-TCP shared dict. | `AircraftStarsState.cs:10` |
| `Eram` | `AircraftEramState` | ERAM-side display mirrored to CRC: leader/dwell overrides, interim/procedure altitudes, pending pointouts. | `AircraftEramState.cs:9` |
| `Approach` | `AircraftApproachState` | Controller-issued expectation, deferred clearance pending fix arrival, visual-approach pilot reports, per-aircraft FAS-reduction distance (`FinalApproachFasReachGateNm`, lazily assigned on first final when variety is enabled). | `AircraftApproachState.cs:11` |
| `Procedure` | `AircraftProcedure` | SID/STAR state: active proc + runway, via-mode flags, DSR speed/expedite flags, `LastProcedureSpeedKts`. | `AircraftProcedure.cs:9` |
| `Pattern` | `AircraftPattern` | Per-aircraft pattern overrides (downwind offset, pattern altitude); null fields fall back to category defaults. | `AircraftPattern.cs:11` |
| `Clearance` | `AircraftClearance` | Departure-clearance fields originating from CRC; null per-field until issued. | `AircraftClearance.cs:9` |
| `HoldAnnotation` | `AircraftHoldAnnotation` | CRC-side hold annotation drawn over the radar target. | `AircraftHoldAnnotation.cs:9` |
| `Ghost` | `AircraftGhostTrack` | Phantom/overlay tracking: distinguishes overlaid scenario aircraft from pure DA/VP phantom data blocks; `IsVehicle`. | `AircraftGhostTrack.cs:15` |
| `Voice` | `AircraftVoice` | CRC voice config (Unknown/Full/ReceiveOnly/TextOnly) + `TdlsDumped`. | `AircraftVoice.cs:10` |
| `Targets` | `ControlTargets` | The autopilot panel physics reads each tick. See next section. | `ControlTargets.cs:13` |

### `ControlTargets` — the autopilot panel

`ControlTargets` (`src/Yaat.Sim/ControlTargets.cs:13`) is what handlers and phases write and what `FlightPhysics` reads. Two conceptual groups:

- **`Target*` (active setpoints)** — `TargetTrueHeading`, `TargetAltitude`, `TargetSpeed`, `TargetMach`, `DesiredVerticalRate`, `DesiredDecelRate`,
  `PreferredTurnDirection`, `TurnRateOverride`, plus the `*Floor`/`*Ceiling` envelope clamps (`AltitudeFloor`/`AltitudeCeiling`,
  `SpeedFloor`/`SpeedCeiling`). Physics steers/climbs/accelerates toward these.
- **`Assigned*` (controller-intent record)** — `AssignedMagneticHeading`, `AssignedAltitude`, `AssignedSpeed`, plus the `HasExplicitSpeedCommand` /
  `HasExplicitTurnRate` latches. These record what the controller explicitly commanded so auto-scheduling (altitude-based speed, pattern turn rate)
  does not silently override an instruction.

`NavigationRoute` is a **get-only** `List<NavigationTarget>` — the DCT waypoint queue. Each `NavigationTarget` (`ControlTargets.cs:156`) carries the
fix name, position, CIFP altitude/speed restrictions, fly-over flag, and four `Revert*` fields used to restore target/assigned values after sequencing
past a CFIX or drawn-route fix.

## Three field categories

Every field on the aircraft falls into one of three buckets. Pick the right one when adding a field:

1. **Serialized** — has a counterpart in the snapshot DTO and round-trips through `ToSnapshot`/`FromSnapshot`. This is the default for any state
   that must survive a recording / replay / rewind. Example: almost every field above.
2. **Runtime-only (`[JsonIgnore]`)** — deliberately not serialized; reconstructed on the first tick or via a separate carrier field. Only two exist:
   - `AircraftState.DeclinationCachePosition` (`AircraftState.cs:63`) — `null` after a round-trip; re-warms on the first tick.
   - `AircraftGroundOps.Layout` (`AircraftGroundOps.cs:20`) — the heavy `AirportGroundLayout` object, stored separately in recording archives. Its
     `LayoutAirportId` (`AircraftGroundOps.cs:28`) **does** round-trip so `SimulationEngine` can re-resolve the layout on restore.
3. **Transient per-tick drain lists** — get-only `List<…>` outboxes producers fill during the tick and `SimulationWorld` empties after. They are
   **not** in the snapshot tree and must not be relied on across a replay round-trip:
   `PendingWarnings`, `PendingNotifications`, `PendingPilotSpeech`, `PendingPilotReadbacks`, `PendingPilotTransmissions`, `PendingApproachScores`,
   `PendingObservations`, `DeferredDispatches`, `PositionHistory` (all on `AircraftState`, lines 120–278). `DeferredDispatches` and `PositionHistory`
   are the exceptions that *do* serialize (they are durable state, not outboxes) — see `ToSnapshot` at `AircraftState.cs:395,412`.

## Mutator map — who writes the load-bearing fields

The recurring question is "who writes field X". The convention: **command handlers set `Assigned*`/satellite intent; physics and phases read it and
write the kinematics.** Phases own `ControlTargets` while active; free-flight commands write it through the dispatch pipeline.

| Field group | Written by | Notes |
|---|---|---|
| `Position`, `TrueHeading`, `TrueTrack`, `Altitude`, `IndicatedAirspeed`, `VerticalSpeed`, `BankAngle` | `FlightPhysics.Update` | Integrated each sub-tick from `ControlTargets`. |
| `WindComponents` | `FlightPhysics.Update` (`FlightPhysics.cs:1041`) | `internal set` — no other writer. Feeds `GroundSpeed`. |
| `Declination`, `DeclinationCachePosition` | `FlightPhysics.Update` (`FlightPhysics.cs:82-83`) | Recomputed only when the aircraft has moved ~1 nm; out-of-range positions log and skip. |
| `ControlTargets.Target*` (in-phase) | The active phase, each tick | Phases write `ctx.Targets` directly — see [phases.md](phases.md). |
| `ControlTargets.Target*` / `Assigned*` (free-flight) | Command handlers via `CommandDispatcher.ApplyCommand` | `FlightCommandHandler`, `NavigationCommandHandler`, etc. — see [command-pipeline.md](command-pipeline.md). |
| `Procedure.*`, `Pattern.*` | Approach / departure / pattern command handlers and the phases they spawn | DSR via-mode, expedite, `LastProcedureSpeedKts`. |
| `Approach.*`, `Clearance.*` | `ApproachCommandHandler`, `DepartureClearanceHandler` | Expectation + deferred clearance state. |
| `Stars.*`, `Eram.*`, `HoldAnnotation.*`, `Voice.*` | STARS/ERAM display command handlers | Display-only mutations; mirrored to CRC. |
| `Track.*` | `TrackEngine` (via `TrackCommandHandler` / `ReplayTrackApplier`) | Ownership/handoff — bypasses `CommandDispatcher` entirely. |
| `Ground.Hold`, `Ground.AssignedTaxiRoute`, `Ground.CurrentTaxiway`, `Ground.SpeedLimit` | Ground command handlers + `GroundConflictDetector` | `SpeedLimit` is reset each tick before conflict detection. |
| `FlightPlan.RevisionNumber`, `FlightPlan.*` | `SimulationEngine.AmendFlightPlan` (entered via `RoomEngine.AmendFlightPlan` on the server) | `SimulationEngine.cs:1431` bumps `RevisionNumber++` on every amend, even an empty one (matches CRC). |
| `SpawnedAtSeconds` | `SimulationEngine` at every production spawn path | Tests that call `AddAircraft` directly leave it 0. |
| `CompletedAtSeconds` / `CompletionReason` / `CompletionDetail` | `LandingPhase` (touchdown → `Landed`, `LandingPhase.cs:547-548`), `ContactCommandHandler` (CT/FCA → `HandedOff`, `ContactCommandHandler.cs:100-101`) | Set together; `RemoveAircraft` keys completion off `CompletionReason != Active`. Explicit deletes (`HandleDelete`, replay `Delete`) do **not** stamp a reason — a deleted aircraft leaves with `CompletionReason == Active` and is therefore **not** added to the completed-aircraft list. (The `CompletionReason.Dropped` enum value is currently unused — no production path writes it.) |
| `Cid` | `SimulationWorld.AddAircraft` if empty (`SimulationWorld.cs:56-59`) | Auto-generates a unique 3-digit CID. |

## Three projections of `AircraftState`

A single field is meaningless until it reaches a consumer, and there are **three independent projections** — adding a field to one does not propagate
it to the others. Decide all three at add-time.

| Projection | Type(s) | Built / restored by | Carries |
|---|---|---|---|
| **Full snapshot / replay** | `AircraftSnapshotDto` (`src/Yaat.Sim/Simulation/Snapshots/AircraftSnapshotDto.cs:8`) | `AircraftState.ToSnapshot()` / `FromSnapshot()` | The complete durable state — every serialized field above. Round-trips through recordings, rewind, and bug bundles. See [snapshots-and-replay.md](snapshots-and-replay.md). |
| **Live SignalR wire** | server builds `AircraftStateDto` (`yaat-server: src/Yaat.Server/Dtos/TrainingDtos.cs:3`); client deserializes the same shape into the `AircraftDto` record (`src/Yaat.Client.Core/Services/ServerConnection.cs:723`) | `DtoConverter.ToTrainingDto(ac, …)` (`yaat-server: src/Yaat.Server/Simulation/DtoConverter.cs:620`) | A **flattened** per-tick view for the operator client: lat/lon, heading, the resolved phase/route/runway strings, ground state, track ownership, scratchpads, smart-status. Only what the radar/Aircraft-List UI needs — far less than the snapshot. |
| **CRC display** | the `CrcDtos.cs` MessagePack types (`yaat-server: src/Yaat.Server/Dtos/CrcDtos.cs`) | `DtoConverter.ToStarsTrack` / `ToFlightPlan` / `ToTowerCab` / … (`DtoConverter.cs`) | The CRC-facing STARS track, flight plan, ERAM, and Tower Cab views. Each method hand-reads individual satellite fields. |

The live-wire projection is name-distinct on each side: the server *authors* `AircraftStateDto` and the client *deserializes* it into `AircraftDto`.
The two records must stay field-compatible by JSON property name — there is no `new AircraftDto(...)` call anywhere; the client receives it purely via
the SignalR `AircraftUpdated` / `AircraftSpawned` deserialization.

## Structural quirks

- **`Targets` is get-only and restored in place.** `AircraftState.Targets` is `{ get; }` (`AircraftState.cs:117`). It cannot be reassigned from a
  snapshot — restore mutates the existing instance via the static `ControlTargets.RestoreFrom(dto, ac.Targets)` (`AircraftState.cs:327`,
  `ControlTargets.cs:126`). The same in-place pattern applies to the get-only lists: `NavigationRoute` restores with `Clear()` + `Add` per element
  (`ControlTargets.cs:145-152`), and `PositionHistory` / `DeferredDispatches` are appended into the existing list in `FromSnapshot`. By contrast the
  thirteen satellites are reassigned wholesale (`FlightPlan = AircraftFlightPlan.FromSnapshot(dto.FlightPlan)`, etc.).
- **Two `AircraftType` fields, different drivers.** `AircraftState.AircraftType` (`AircraftState.cs:15`) is the **physical** type — fixed at spawn,
  drives physics/performance, the Tower Cab datablock, and the operator Aircraft List. `AircraftFlightPlan.AircraftType` (`AircraftFlightPlan.cs:22`)
  is the **filed** type — mutable by instructor amendment, displayed by STARS/ASDE-X/strips/ERAM/the FP editor. They intentionally do **not**
  cross-fill. Both expose a `BaseAircraftType` that strips the `H/`/`J/`/`S/` wake prefix via `AircraftState.StripTypePrefix` (`AircraftState.cs:22`).

## SimulationWorld as the owner

`SimulationWorld` is the live container. Everything funnels through one reentrant `_lock`.

- **`AddAircraft` (`SimulationWorld.cs:52`)** is **replacement-safe, not append-safe**. It `RemoveAll`s any existing entry with the same callsign
  (case-insensitive) before appending and logs a warning, then purges any matching `CompletedAircraftRecord`. A user-typed VP/DA "ghost" is discarded
  when a real scenario/runtime spawn arrives with the same callsign — spawn wins. Re-adding a callsign is therefore **not** idempotent
  state-preserving. (See also the command-pipeline.md pitfall.)
- **`RemoveAircraft` (`SimulationWorld.cs:89`)** appends a `CompletedAircraftRecord` for any aircraft whose `CompletionReason != Active`, then trims
  the FIFO to `CompletedAircraftCapacity` (500). The debrief tab reads `GetCompletedAircraft()` after the aircraft leaves the live set.
- **`Tick` (`SimulationWorld.cs:215`)** runs `GroundConflictDetector.ApplySpeedLimits` once for the whole set, then loops aircraft calling
  `FlightPhysics.Update` exactly **once** per aircraft with the supplied `deltaSeconds`. It does **not** sub-tick — the 4× physics sub-stepping is
  driven by `SimulationEngine` (`PhysicsSubTickRate = 4`), which calls `World.Tick` repeatedly with a 0.25 s delta. The local `Lookup` closure is
  passed in so phases can resolve follow targets; the reentrant lock makes `FindAircraft` callable from inside the tick. See
  [tick-loop.md](tick-loop.md) for the per-aircraft step order.
- **`GetSnapshot` (`SimulationWorld.cs:169`)** returns a shallow `List` copy — a new list whose elements are the **live mutable** `AircraftState`
  instances. It is the read-only view every broadcast/projection iterates.
- **Drain surface** — `DrainAllWarnings`, `DrainAllNotifications`, `DrainAllPilotSpeech`, `DrainAllPilotReadbacks`, `DrainReadyPilotTransmissions`,
  `DrainAllApproachScores` (`SimulationWorld.cs:272-433`) each empty the corresponding per-aircraft transient list under the lock. The frequency
  airtime serialization (`ActiveFrequency.Enqueue` / `TryDequeueReady`, plus `ExpectPilotReadback` / `AcknowledgeControllerResponse`) lives here too —
  see [solo-training-pilot-speech.md](solo-training-pilot-speech.md).

## Adding a field to an aircraft — checklist

1. **Pick the right home.** A new field belongs in the satellite whose concern it shares (ground → `AircraftGroundOps`, STARS display →
   `AircraftStarsState`, …), or at top level only if it is genuinely aircraft-wide identity/kinematics. Don't repurpose an existing field — add a new
   one with a clear name and a doc comment.
2. **Pick the category.** Serialized (default), `[JsonIgnore]` runtime-only (then provide a carrier or first-tick rebuild), or a transient drain list
   (then add the matching `Drain*` call in `SimulationWorld`). See the three-categories section.
3. **Wire `ToSnapshot` / `FromSnapshot`** on the owning class and add the property to its DTO. Decide whether a schema-migrator bump is needed — that
   rule is owned by [snapshots-and-replay.md](snapshots-and-replay.md). Non-required init properties on the DTO let old recordings deserialize cleanly.
4. **Decide the live-wire projection.** If the operator client must see it, add it to **both** the server `AircraftStateDto` and the client
   `AircraftDto` record (matching property name) and populate it in `DtoConverter.ToTrainingDto`. Skipping this means the field round-trips in a bug
   bundle but never reaches a running client.
5. **Decide the CRC projection.** If a CRC display must show it, wire the relevant `DtoConverter.To*` method and the `CrcDtos.cs` MessagePack type.
6. **Mind get-only restore-in-place.** If the field lives under `ControlTargets` or another get-only member, restore it by mutating the existing
   instance (extend `ControlTargets.RestoreFrom`), not by reassigning the property.

## Footguns and pitfalls

- **Three projections, not one.** A field added to `AircraftSnapshotDto` (so it survives replay) is **not** automatically on the live SignalR
  `AircraftStateDto`/`AircraftDto` nor in any CRC `DtoConverter` output. It can round-trip in a bug bundle yet never appear on a running client or CRC
  display. Decide all three at add-time.
- **`GetSnapshot()` is a shallow copy.** The `AircraftState` objects inside the returned list are the **live mutable** instances. Callers must treat
  them as read-only — mutating one outside the `_lock` races the tick loop.
- **`Targets` cannot be reassigned.** It is `{ get; }` and is restored via `ControlTargets.RestoreFrom(dto, ac.Targets)` mutating in place. The same
  applies to the get-only lists (`NavigationRoute`, `PositionHistory`, `DeferredDispatches`), which restore via `Clear()` + `Add`.
- **Two `AircraftType` fields with different drivers.** `AircraftState.AircraftType` is physical (fixed at spawn → physics/Tower Cab/Aircraft List);
  `AircraftFlightPlan.AircraftType` is filed (mutable by amendment → STARS/ASDE-X/strips/ERAM/FP editor). They do not cross-fill. `BaseAircraftType`
  strips the wake prefix.
- **Per-tick drain lists are an outbox, not durable state.** `PendingWarnings`, `PendingNotifications`, `PendingPilotSpeech`,
  `PendingPilotReadbacks`, `PendingPilotTransmissions`, `PendingApproachScores`, and `PendingObservations` are not in the snapshot tree. Producers
  fill them during the tick; `SimulationWorld.Drain*` empties them after. Don't expect them to survive a replay round-trip.
- **`[JsonIgnore]` fields need a carrier.** `Ground.Layout` is ignored but `Ground.LayoutAirportId` round-trips so the engine can re-resolve the
  layout; `DeclinationCachePosition` is `null` after a round-trip and re-warms on the first tick. Adding a `[JsonIgnore]` field with no carrier and no
  first-tick rebuild silently loses it.
- **`AddAircraft` replaces, it does not append.** A user-typed FP ghost (and any scratchpads / track ownership on it) is discarded when a real spawn
  arrives with the same callsign, and the matching completed record is purged. Re-adding a callsign is not idempotent. A logged replacement is
  expected when a scenario spawn collides with a ghost; two scenario spawn paths firing for one callsign is a bug.
- **`SimulationWorld` is the only safe mutation surface.** The `_lock` is reentrant so `FindAircraft` is callable from inside `Tick()`, but all field
  mutation outside a tick / handler that already holds the lock is unsafe.
