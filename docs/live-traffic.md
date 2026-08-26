# Live traffic shadows — `src/Yaat.Sim/LiveTraffic/`

> Read this before touching `AircraftLiveTraffic`, `LiveTrafficKinematics`, `AircraftState.IsShadow`, the shadow branch in
> `SimulationWorld.Tick`, or the `RecordedLiveTrafficSample` / `RecordedLiveTrafficRemoval` actions.

A **shadow** is an `AircraftState` that mirrors a real aircraft from an external surveillance feed. Its kinematics come
from *samples*, not from `FlightPhysics`; it has no phases, an empty command queue, and rejects every controller
command until it is assumed (which converts it in place into an ordinary simulated aircraft). The sim is feed-agnostic:
it sees `LiveTrafficSample`s for a callsign and never knows where they came from beyond `LiveTrafficSource`.

## Files

| File | Role |
|---|---|
| `LiveTraffic/LiveTrafficSample.cs` | `LiveTrafficSample` (one observation: sim-clock time, lat/lon, altitude, GS, true track, optional VS, source, beacon), `LiveTrafficSource` (`Stars` 4.5 s sweep / `Eram` 12 s / `Asdex` 1 s — `Asdex` means on the ground), `LiveTrafficRemovalReason`. |
| `LiveTraffic/AircraftLiveTraffic.cs` | The satellite on `AircraftState.LiveTraffic`: last sample fields, `SecondsSinceSample` (the only clock `Advance` reads), the previous sample's altitude/time (vertical-speed derivation), `IsCoasting`, `ExternalId`. `ToSnapshot`/`FromSnapshot` ↔ `AircraftLiveTrafficDto`. |
| `LiveTraffic/LiveTrafficKinematics.cs` | `CreateShadow`, `Apply(ac, sample)`, `Advance(ac, dt, weather, simTime)`, coast timing. |
| `Simulation/Snapshots/AircraftLiveTrafficDto.cs` | Nullable `AircraftSnapshotDto.LiveTraffic`; null for simulated aircraft and for older snapshots (no schema bump). |
| `Simulation/RecordedAction.cs` | `RecordedLiveTrafficSample(Callsign, Sample, SpawnState?)`, `RecordedLiveTrafficRemoval(Callsign, Reason)`. |

## Contract

- **`ac.IsShadow` ⇔ `ac.LiveTraffic != null`.** Assuming sets it to null; nothing else may.
- **Tick bypass** (`SimulationWorld.Tick`, per-aircraft loop): a shadow gets `LiveTrafficKinematics.Advance` and the
  `HasBeenAirborne` latch, then `continue` — no `PreTick` (PhaseRunner), no `FlightPhysics.Update`. The latch still runs
  because it decides `FlightPlanStatus.Proposed` vs `Active` in the CRC projection.
- **Positions are re-derived, never integrated.** `Advance` accumulates `SecondsSinceSample += dt` and projects
  `SamplePosition` along `SampleTrueTrack` by `SampleGroundSpeed · t`; altitude = `SampleAltitude + SampleVerticalSpeed · t/60`.
  Applying the same samples at the same sim seconds therefore reproduces the same motion — that is what makes replay exact.
  Motion is 4 Hz because the clock is the physics `dt`, not the per-second `ElapsedSeconds`.
- **Air vector, not track = heading.** `AircraftState.GroundSpeed` is *computed* from IAS→TAS along `TrueHeading` plus the
  cached `WindComponents`. `Advance` therefore writes `TrueHeading = dir(G − W)` and `IndicatedAirspeed = TasToIas(|G − W|)`
  with `W` the room wind at altitude, so `ac.GroundSpeed == SampleGroundSpeed` under any wind and every GS-derived readout
  (datablock, strips, ATPA closure, change-tracker fingerprint) agrees with the motion on the scope. The room wind is not
  the real atmosphere, so the derived IAS carries the wind-model error; accepted. On the surface (`Asdex`) IAS carries
  the wheel speed and heading = track. `Advance` also fills the caches `FlightPhysics.Update` would own:
  `FlightPhysics.RefreshDeclinationCache` (magnetic readouts) and `WindComponents`.
- **Fresh samples win.** `Apply` adopts a newer sample unconditionally (jump > 0.3 nm is logged at Debug), resets the
  clock and the coast flag, refreshes the beacon, and derives vertical speed from Δalt/Δt (EMA-smoothed against the
  previous derived value) when the feed has none. A sample not newer than the stored `ObservedAtSimSeconds` is ignored
  (out of order, or a lower-priority source arriving late). Sample time is **sim seconds**, never wall-clock.
- **Coast, don't freeze.** After two missed sweeps of the sample's source (`CoastAfterSeconds`: STARS 9 s, ERAM 24 s,
  ASDE-X 2 s) `IsCoasting` is set; the aircraft keeps dead-reckoning (a frozen target displayed as a normal track is a
  3.75 nm lie at 450 kt). Removal is the feed host's decision (`SimulationEngine.RemoveLiveTraffic`).
- **Commands are rejected** at the top of `CommandDispatcher.Dispatch` and `DispatchCompound` — before the transparent
  fast path, so `SQ`/ident cannot slip through — with `ASSUME <cs> first — live traffic is not controllable`. Track,
  coordination and delete commands never reach the dispatcher and stay allowed (tracking a real target is normal work).
- **Status**: `AircraftStatusDescriber` shows `LIVE` / `LIVE CST` and nothing else for a shadow.
- **Pilot AI**: `SimulationEngine.TickPilotProactive` skips shadows. The transponder pool never assigns to a shadow — its
  code is whatever the feed reported. A removed shadow is not a completion (`CompletionReason` stays `Active`).

## Recording and replay

`SimulationEngine.ApplyLiveTrafficSample(callsign, sample, spawnState)` applies a sample (creating the aircraft from
`spawnState` — a shadow's `ToSnapshot()` — when it does not exist) and records `RecordedLiveTrafficSample`; the spawn
state rides only on the creating sample. `RemoveLiveTraffic(callsign, reason)` removes a shadow and records
`RecordedLiveTrafficRemoval`. Both are no-ops for assumed aircraft, and neither records while replaying or in playback
(same guard as `RecordGeneratedAircraftSpawn`).

**Samples are pre-tick actions.** Live, samples land in pre-physics of second *t*; `SimulationEngine.IsPreTickAction`
therefore lists `RecordedLiveTrafficSample` next to `RecordedAircraftSpawn`, and every Sim-side replay loop (`Replay`,
`ReplayOneSecond`, `ReplayOneSubTick`) applies them before `TickPrePhysics` of their second. Applying them after the
second would put every replayed second one sample behind and would create the aircraft *after* that second's physics.
Removals apply after the second like other actions. Callers of `ApplyLiveTrafficSample` on a live host must call it from
pre-physics with `ElapsedSeconds` already at the current second so the recorded second matches this placement.

The server brain (`RecordingManager.ApplyRecordedActionCore`) currently ignores both actions — the room-side sync and its
pre-tick replay twin land with the server integration step.

## Tests

`tests/Yaat.Sim.Tests/LiveTraffic/`: `LiveTrafficKinematicsTests` (dead reckoning, 4 Hz motion, air vector under a
100-kt crosswind, coast timing, out-of-order/jump samples, derived VS, surface pose, snapshot round trip, status,
pilot-AI skip), `LiveTrafficCommandGateTests`, `LiveTrafficReplayTests` (live run vs `Replay` vs `ReplayOneSecond`
positions identical to 0.001 nm).
