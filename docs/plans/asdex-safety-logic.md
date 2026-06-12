# ASDE-X Safety Logic + history/coast + scope markers

Unblocks the Bucket F items in [`crc-protocol-support.md`](./crc-protocol-support.md) now
that the new vNAS ASDE-X docs (`docs/crc/asdex.md`, `docs/crc/said-saab.md`) give the
authoritative spec. Scope decided with the user 2026-06-12.

## Grounding (verified in code)

- Runway footprint polygons + closed-state + active runway-config + inhibited arrival-alert
  position ids arrive **from the student's CRC** via `UpdateAsdexSafetyLogicConfiguration`
  and are stored at `AsdexRoomState.SafetyLogicConfig` (`AsdexSafetyLogicConfig(runways:
  List<AsdexRunwayConfig{Id, Area(lat,lon)[], IsClosed}>, RunwayConfigurationId,
  InhibitedArrivalAlertPositionIds)`). So runway occupancy = point-in-polygon, no YAAT-side
  runway geometry needed.
- Wire DTOs ready: `AsdexAlertDto{Id, MessageLines, AircraftIds, RunwayIds, AuralAlerts:int[],
  PlayAuralAlert}`, `AsdexHoldBarDto{Id, Points, Status: Suppressed|Active}`.
- Broadcast stubs: `CrcBroadcastService.BuildAsdexHoldBarsData` (empty list) and the
  `AsdexAlerts` topic (returns null, comment "future AsdexSafetyLogicDetector"). New topic
  wiring also needs `DeleteAsdexAlerts` pairing.
- Detector lives in **Yaat.Sim** (per plan + "Sim owns logic"); yaat-server broadcasts it.
- Per-tick hook: `TickProcessor` PostPhysics fan-out (same place as `ProcessConflictAlerts`).

## Investigation outcomes (2026-06-12)

- **aviation-sim-expert spec** (full ASDE-X Safety Logic): conditions feasible from the
  CRC-supplied runway footprint polygons = closed-runway, occupied-runway,
  taxi-onto-active-runway. Converging-runways needs a runway-pair convergence relationship
  that is **not in any reachable data** (ARTCC `HoldShortRunwayPairs` is empty/opaque) →
  out of scope. Landing-on-taxiway needs taxiway footprint polygons — **available via
  `AirportGroundLayout`**, so in scope with extra plumbing. Detector is a stateless per-tick
  classifier; stable alert Id = `prefix + sortedRunwayIds + sortedCallsigns`. Key
  approximation: arrival "still airborne" ≈ AGL ceiling ~300 ft (7110.65 §3-6-4). Per-position
  arrival-alert inhibition needs a `RunwayId→TowerPositionId` map CRC does not send — honour
  only per-aircraft `AsdexAlertsInhibited` for now.
- **hold-bar geometry**: NOT present in any of video maps / ARTCC config / ground GeoJSON /
  vNAS models. Real vNAS synthesises segments server-side. Reachable for YAAT only by
  synthesising perpendicular segments at `AirportGroundLayout` `RunwayHoldShort` nodes.

## Tasks

### 1. ASDE-X Safety Logic detector (Yaat.Sim) — conditions 1/2/4/5 — DONE
- [x] aviation-sim-expert spec (see above).
- [x] `AsdexSafetyLogicDetector` in Yaat.Sim (closed-runway, occupied, taxi-onto-active,
      taxiway-landing; converging skipped). Point-in-polygon occupancy; aligned occupancy
      claims the runway at any ground speed (LUAW); true-heading alignment via local variation;
      honours per-aircraft `AsdexAlertsInhibited`. Taxiway-landing uses ground-graph centerline
      proximity. 11 TDD tests (each condition triggering + non-triggering, inhibition, stable id).
- [x] aviation-sim-expert review applied (H1 LUAW gap, M2 speed gate, M3 true-vs-magnetic frame).
- [x] yaat-server: per-tick `TickProcessor.ProcessAsdexAlerts` runs the detector, diffs vs
      `AsdexRoomState.ActiveSafetyAlerts`, broadcasts `ReceiveAsdexAlerts`/`DeleteAsdexAlerts`;
      taxiways from `room.World.GroundLayout`, variation from `MagneticDeclination`, field
      elevation from `FieldElevationResolver`. `DtoConverter.ToAsdexAlert` mapping.

### 2. Hold bars — DEFERRED (user decision 2026-06-12)
- Geometry not reachable; keep the `BuildAsdexHoldBarsData` empty-list stub.
- [ ] Backlog: synthesise hold-bar segments perpendicular to taxiways at `AirportGroundLayout`
      `RunwayHoldShort` nodes; `Status` Active/Suppressed from runway occupancy (Suppressed at
      intersections of other active runways unless LAHSO). Needs its own aviation review.

### 3. Target history trails (server) — DONE
- [x] `DtoConverter.BuildSurfaceHistory` populates `HistoryLocations` for ASDE-X + SAID targets
      from the existing `AircraftState.PositionHistory` (5-sim-second sampler shared with STARS
      history), newest-first, capped at 5 (vNAS reference convention). Tests in
      `DtoConverterSurfaceHistoryTests`.

### 4. Coasted/dropped tracks (server)
- [ ] 45s coast timer on ASDE-X track signal-loss/disconnect; populate `CoastListId` +
      `CoastTimeout` (currently hardcoded null); CST in field E. Extend `CrcVisibilityTracker`.

### 6. SAID vertical display range (server) — DONE
- [x] SAID surface display now limited to **2,500 ft AGL** (field-relative, above the SAID airport's
      field elevation), replacing the prior fixed 1,500 ft MSL ceiling that ignored elevation and
      hid all targets at elevated fields. 600 ft hysteresis band retained. `CrcVisibilityTracker`
      resolves the airport elevation via `_navDb.GetAirportElevation` in both `GetVisibleSaidAirports`
      (stateless) and `EvaluateSaid` (stateful). Dropped the unused `SaidAirportInfo.Ceiling` field
      and `SaidDefaultCeiling`. Tests in `CrcVisibilitySaidTests` (incl. AGL-not-MSL regression).
      User request 2026-06-12.

### 5. STARS scope-marker pins (client — Yaat.Client)
- [ ] Instructor-radar `.ff`/`.marker`-style: user-pinned arbitrary fixes/NAVAIDs, persisted
      per-view in `UserPreferences`, rendered as a third tier in `RadarRenderer.DrawFixes`
      alongside auto programmed-fix highlights. No server/broadcast involvement.

## Notes
- Update `crc-protocol-support.md` Bucket F checkboxes as items land.
- Aviation review MANDATORY for items 1, 4 (anything touching runway occupancy / alerts).
- Delete this file once all tasks ship (promote durable design notes into the relevant docs).
