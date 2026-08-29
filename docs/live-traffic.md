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
| `LiveTraffic/AircraftLiveTraffic.cs` | The satellite on `AircraftState.LiveTraffic`: last sample fields, `SecondsSinceSample` (the only clock `Advance` reads), a bounded `History` of the last 24 samples (vertical-speed derivation, level/hold detection at assume), the feed's latest clearance fields (assigned/interim altitude, cleared heading/speed, clearance text), `IsCoasting`, `ExternalId`. `ToSnapshot`/`FromSnapshot` ↔ `AircraftLiveTrafficDto`. |
| `LiveTraffic/LiveTrafficKinematics.cs` | `CreateShadow`, `Apply(ac, sample)`, `Advance(ac, dt, weather, simTime)`, coast timing. |
| `Simulation/Snapshots/AircraftLiveTrafficDto.cs` | Nullable `AircraftSnapshotDto.LiveTraffic`; null for simulated aircraft and for older snapshots (no schema bump). |
| `Simulation/RecordedAction.cs` | `RecordedLiveTrafficSample(Callsign, Sample, SpawnState?)`, `RecordedLiveTrafficRemoval(Callsign, Reason)`. |
| `LiveTraffic/LiveTrafficAssumer.cs` | `ASSUME`: the in-place hand-off from shadow to simulated aircraft (below). |

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
- **A sample is aged to the second it is applied in.** `SimulationEngine.ApplyLiveTrafficSample` / `ApplyRecordedLiveTrafficSample`
  call `LiveTrafficKinematics.Resync(ac, ElapsedSeconds, weather)` after `Apply`: `SecondsSinceSample = now − ObservedAtSimSeconds`,
  then `Advance(0)`. A sample that was already 6 s old when it arrived (feed latency) therefore puts the target where the aircraft
  is now, and replay — same sample, same second — ages it identically.
- **Fresh samples win.** `Apply` adopts a newer sample unconditionally (jump > 0.3 nm is logged at Debug), resets the
  clock and the coast flag, refreshes the beacon, and derives vertical speed from Δalt/Δt (EMA-smoothed against the
  previous derived value) when the feed has none. A sample not newer than the stored `ObservedAtSimSeconds` is ignored
  (out of order, or a lower-priority source arriving late). Sample time is **sim seconds**, never wall-clock.
- **Coast, don't freeze.** After two missed sweeps of the sample's source (`CoastAfterSeconds`: STARS 9 s, ERAM 24 s,
  ASDE-X 2 s) — or at once when the source itself flagged the sample as a coast (`LiveTrafficSample.SourceCoasting`:
  STARS `coasting` status, ERAM `coastIndicator`) — `IsCoasting` is set; the aircraft keeps dead-reckoning (a frozen target displayed as a normal track is a
  3.75 nm lie at 450 kt). Removal is the feed host's decision (`SimulationEngine.RemoveLiveTraffic`).
- **Commands are rejected** at the top of `CommandDispatcher.Dispatch` and `DispatchCompound` — before the transparent
  fast path, so `SQ`/ident cannot slip through — with `ASSUME <cs> first — live traffic is not controllable`. Track,
  coordination and delete commands never reach the dispatcher and stay allowed (tracking a real target is normal work).
- **Status**: `AircraftStatusDescriber` shows `LIVE` / `LIVE CST` and nothing else for a shadow.
- **Pilot AI**: `SimulationEngine.TickPilotProactive` skips shadows. The transponder pool never assigns to a shadow — its
  code is whatever the feed reported. A removed shadow is not a completion (`CompletionReason` stays `Active`).

## `ASSUME` — `LiveTrafficAssumer`

`CommandDispatcher.Dispatch`/`DispatchCompound` route a lone `AssumeCommand` to `LiveTrafficAssumer.Assume(aircraft, ctx)`
*before* the shadow gate (so it is the one command a shadow accepts; `ASSUME ; H 180` is rejected like any compound). It goes
through the normal `HandleStandardCmd` path on the server, so it is recorded as a `RecordedCommand` and replays through the
same dispatcher in both brains with no extra arm. **Never refused** (owner decision): the RPO always gets control; on a
non-shadow it fails with "not live traffic".

Order of business: `Advance(0)` to make the pose current → `LiveTraffic = null`, queue and deferred dispatches cleared →
squawk note (7700/7600) → coasting note → `SeedState`:

1. **Runway kind** via `RunwayOccupancy.ClassifyByGeometry` over the room layout's, destination's and departure's runways
   (`RunwayOccupancy.AlignedEnd` orients each pavement to the track). On the ground: `Departing` → `TakeoffPhase`;
   `OnSurface` > 30 kt → `RunwayExitPhase`; otherwise a phase-less ground aircraft (`Ground.Layout` attached,
   `GroundSpawnSnap` when off-runway) with `TargetSpeed` = wheel speed. Airborne `Landing` (< 50 ft) → `LandingPhase`
   with `LandingClearance = ClearedToLand` (the real aircraft has one; §3-10-5).
2. **VFR** (VFR plan, or 1200 with no plan): heading hold (feed cleared heading first); level → altitude to the 100 ft,
   climbing/descending → keep the rate to the next §91.159 VFR cruising altitude (`NextVfrCruisingAltitude`, odd/even
   thousands + 500 by magnetic course; a descent that would end below 3 500 ft MSL levels instead). Done.
4. **Hold**: a `HOLD` token in `ClearanceText` or the filed route, or ≥ 270° of accumulated turn over ≤ 90 s of history
   within 3 nm → heading + altitude hold and a "reissue holding or a rejoin" warning (§4-6-1). Done.
5. **Lateral**, first match wins: established on a final (inside the approach gate = `ApproachGateDatabase`
   min-intercept − `InterceptPaddingNm`, displaced threshold honoured; ≤ 10° off the final, ≤ 0.3 nm cross-track, not
   climbing) → `ApproachCommandHandler.TryClearedApproach` with the runway as the `DestinationRunway` hint (no landing
   clearance implied); else inside the gate, ≤ 30°/≤ 1 nm, field VMC (≥ 1 000 ft / 3 sm from the room METAR, VMC assumed
   when unknown) → visual approach with field-in-sight set; else aligned within 10 nm → heading hold + "on vectors to the
   runway final" warning. Then **initial climb** (climbing ≥ 300 fpm, < 3 nm of the track-aligned departure runway end,
   ≤ 30° aligned) → feed cleared heading else runway heading, filed SID restrictions activated when the route names one
   (§5-6-3: never turned direct to an enroute fix off the runway — this deliberately precedes the rejoin). Then the filed
   route: `NextFixAhead` (closest leg → its end fix, one skip when that fix is abeam/behind, ±45° of track,
   ≥ max(2 nm, 30 s)) installs `NavigationRoute` from that fix; `ArrivalRouteResolver.ApplyAltitudeProfile` within 30 nm
   of the destination, `TryActivateFiledSid` within 30 nm of the departure. Else heading hold with an "assumed on vectors"
   warning.
6. **Speed** (after lateral, so a STAR speed restriction cannot clobber it): below 10 000 ft the seed is clamped between
   the type's approach speed and 250 KIAS (§5-7-1.b.4 — an arrival slowed for the approach is never sped back up;
   §91.117); above, 0.75–1.25× the type's default (no Vmo data). Feed `ClearedSpeedKts` overrides; `TargetMach` from the
   air vector at/above FL240.
7. **Vertical** (skipped when an approach was installed — the approach owns the vertical path): level when the last 3
   samples span ≤ 200 ft and |VS| ≤ 400 fpm (Mode C is 100-ft quantised), or while coasting → altitude to the 100 ft
   (never a hemispheric snap). Climbing → keep the rate to interim/assigned/filed, else the next 1 000 ft up. Descending →
   keep the rate to interim/assigned, else the first at/at-or-below restriction on the installed route below the aircraft,
   else `max(MVA floor, destination elevation + 2 000 ft rounded up)`; when none of those exist (no MVA coverage, no
   destination) the aircraft is **levelled off** with a warning rather than seeded an unbounded descent (§5-6-1.a.3).
   Targets are rounded *down* to the 100 ft so a descending aircraft never gets a target above itself. `AssignedAltitude`
   mirrors the target.

Nothing is transmitted at assume. The `CommandResult` message summarises the seeded state; caveats go to
`PendingWarnings` (amber). Tests: `LiveTrafficAssumeTests`.

## Shadows as runway and ground participants

- **Ground conflict** (`GroundConflictDetector`): a moving shadow classifies as `MovementState.External` — an obstacle
  every simulated ground aircraft yields to (closing-proximity stop/trail limits, crossing resolution; in a mutual stop the
  simulated aircraft is always the holder), never a subject (`ApplyMinLimit` returns for shadows). A stopped shadow is
  `Stationary` (passable with wingtip clearance, like a parked aircraft). A surface track coasting longer than
  `ExternalCoastGraceSeconds` (10 s) is dropped from the sweep so a dead feed target cannot pin traffic. Runway priority:
  the detector writes the per-tick `[JsonIgnore]` flag `Ground.ExternalOnRunway` from `RunwayOccupancy.ClassifyBest` over
  the layout airport's runways (`RunwayOccupancy.AirportRunways`), and `IsOnRunway` reads it for shadows.
- **Runway advisories** (`RunwaySafetyAdvisor`): a shadow **on the runway surface** (`RunwayOccupancy.Classify` =
  `OnSurface`: lined up, holding, or a landed rollout) makes a landing-family clearance warn "live traffic on runway —
  not clear" (3-10-3.a.1 / 3-10-5.e; `WarnIfLiveTrafficOnRunway`, both overloads). A shadow still airborne on the final
  is sequencing (3-10-6.a), not an occupant, and belongs to `WarnIfTrafficOnFinal` (called on LUAW from
  `DepartureClearanceHandler`): the closest shadow on that runway's final within `OnFinalAdvisoryNm` (6 nm) is the
  3-9-4.d traffic to issue to the LUAW aircraft (one-way; the text gives the phrase). `RunwayOccupancy.IsOnFinal` = approach
  side, ≤ 30° off the final course, cross-track inside a 10° wedge (0.3–1 nm), not climbing; `ClosestFinal` picks one
  runway between parallels (OAK 28L/28R are 0.4 nm apart). Simulated arrivals stay covered by their clearance state.
- **Solo evaluator** (`SoloTrainingEvaluator.ResolveShadowRunwayUse`): a shadow's runway is the one it geometrically uses
  at its destination (else departure) airport via `ClassifyBest` (`Crossing` included — on the pavement is not clear),
  and its `RunwayUseKind` stands in for the phase: `Departing` → departure roll; `OnFinal` (synthesized from
  `ClosestFinal` within 6 nm, matching a simulated arrival's `FinalApproachPhase` window) / `ShortFinal` / `Landing` →
  arrival approach; `Landing|OnSurface` → landing after threshold; not clear while `Landing|OnSurface|Crossing`. After
  liftoff the observer's `DepartedOnRunway` latch keeps it a `Departing` on the latched runway within 1 nm of the
  departure end, so the §3-9-6 / §3-10-3.a.2 landmarks are scored. Airborne separation already included shadows.
- **Runway-use observer** (`SimulationEngine.TickLiveTrafficRunwayUse`, once per second in `TickPostPhysics` **and** the
  server's `ProcessPostPhysics`): classifies each shadow against the primary airport, else its destination/departure
  airport. The edge airborne `Landing` → on the ground stamps `CompletionReason.Landed` and sets
  `LiveTraffic.LandedOnRunway`, which makes `Classify` read the rollout as `OnSurface` (geometry cannot tell an 80-kt
  rollout from a takeoff roll); it clears once airborne again or off the pavement. The edge `Departing` → airborne sets
  `DepartedOnRunway` (cleared on the ground or > 1 nm past the departure end). `LatchedRunwayAirport/Designator` remember
  which runway. All are serialized so a restored room keeps the edge state; the later feed removal records a
  `CompletedAircraftRecord` for the debrief.
- **Conflict alerts** (`ConflictAlertDetector.IsPairEligible`, shared by `EramConflictDetector`): never shadow↔shadow
  (real pairs are separated by things the sim cannot see — visual, dependent approaches, MARSA — and inter-source
  offsets would manufacture continuous alerts); shadow↔simulated only when the shadow is IFR (filed, not VFR), not
  coasting, and not inside an internal-airport approach corridor. `CASUP <other>` (a track command, so it works on a
  shadow) toggles suppression for one pair from either side; stored in `Stars.CaSuppressedWith` (serialized, replayed as
  an ordinary recorded track command). Tests: `LiveTrafficConflictAlertTests`.
- **Roll start** (`RunwayOccupancy.IsRolling`): a surface shadow aligned on the pavement is `Departing` past 35 kt, or past
  20 kt while accelerating ≥ 2.5 kt/s over the last 4 s of feed samples (`LiveTrafficKinematics.GroundAcceleration`, a
  least-squares slope of the reported ground speeds — positions are too noisy; `History` now carries `GroundSpeedKts`).
  That halves the jet-vs-light-single spread of the §3-9-6 / §3-10-3 clocks and seeds `TakeoffPhase` on `ASSUME` earlier.
  A decelerating rejected takeoff drops back to `OnSurface`, and `OccupiedRunwayGoAround.WillBeFlying` projects the
  occupant to rotation speed with the measured acceleration (type ground acceleration when there is none), so an RTO
  never reads as "about to be airborne". The landing-clearance advisory names a rolling shadow separately with the
  3-10-3.a.2 landmark wording.
- **Rotorcraft** (`RunwayOccupancy.ClassifyAirborneRotorcraft`): a helicopter shadow over the pavement below the 100 ft
  air-taxi ceiling is a surface movement (§3-11-3 NOTE) — `Landing` when descending, `OnSurface` below hover-taxi speed
  or along the axis, `Crossing` across it at air-taxi speed; never `ShortFinal` or `Departing`. A preceding rotorcraft has
  no §3-10-3 landmark exception in `OccupiedRunwayGoAround`, and the landing-clearance advisory treats a shadow still in
  the air over the runway (`Landing`) as not clear. `ASSUME` on a descending helicopter installs `HelicopterLandingPhase`.
- Not yet: arrival-runway inference for display, follow-helper lead runway. Tests: `LiveTrafficParticipationTests`.

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

The server brain mirrors this: `RecordingManager.ReconstructViaServerTick` applies each second's pre-tick actions
(`SimulationEngine.IsPreTickAction` — `RecordedAircraftSpawn` and `RecordedLiveTrafficSample`, the latter via
`SimulationEngine.ApplyRecordedLiveTrafficSample`, public for this) between `RoomEngine.BeginSecond` (the `ElapsedSeconds`
increment) and `RunSecondPhysics`, and skips them when the generic post-second cursor reaches them; forward tape playback does
the same through `ApplyPreTickPlaybackActions(second)`, which `SimulationHostedService.ProcessRoomSecond` calls in the same
slot. The increment must come first: `ApplyRecordedLiveTrafficSample` resyncs against `ElapsedSeconds`, and a sample carrying
feed latency resynced at *t−1* dead-reckons one second behind the live session (#404 — invisible with zero-latency samples
because of the `max(0, …)` clamp in `LiveTrafficKinematics.Resync`). Removals apply post-second. A spawn-carrying sample also
drops the callsign from the change tracker so a recurring callsign is first-seen again. The live sync stays inert during both
(`IsBroadcastSuppressed` / `IsPlaybackMode`) — otherwise it would spawn unrecorded shadows on top of the tape.

**Feed status.** `ShadowTrafficSync.BroadcastStatusIfChanged` (post-physics) computes `LiveTrafficStatusDto(FeedConfigured,
Connected, LastMessageAgeSeconds, TracksInScope)` from `LiveTrafficStore.FeedConfigured` / `ReportFeedState`
and the room's last scoped query, keeps it on `RoomLiveTrafficState.LastStatus` (seeded to joiners via `RoomStateDto`; before
the first tick `TrainingHub.BuildRoomState` synthesises one from the store so a joiner learns the gate immediately), and
broadcasts `LiveTrafficStatusChanged` immediately when the feed flags or the count change, else at most every 5 s while the
age moves. Client: `ServerConnection.LiveTrafficStatusChanged` → `MainViewModel.LiveTrafficStatus` (display is the client step).

**Server gate.** `FeedConfigured` is the per-deployment `LiveTrafficOptions.Enabled` flag (`LiveTraffic:Enabled`, env
`LiveTraffic__Enabled`; default false, `appsettings.Development.json` turns it on, `docker-compose.yml` reads
`LIVE_TRAFFIC_ENABLED`). `SimControlService.SetLiveTrafficEnabled(true)` refuses with "not enabled on this server" when it is
off (disabling is always allowed); the client binds the session checkbox's `IsVisible` to `MainViewModel.LiveTrafficAvailable`
(`LiveTrafficStatus?.FeedConfigured == true`, reset on `ClearRoomState`), so a gated server never shows the toggle.
`tools/bug_bundle.py actions` / `history` render the live-traffic actions compactly (`LIVE` / `LIVERM` / `LIVEST` tags).
**Export scrub:** when a LADD list is in force (`ILaddListSource`, the ingest service), `RecordingManager.ExportRecordingArchive`
runs `LaddRecordingScrubber` first — a listed shadow becomes `LADD01`, `LADD02`, … on every action, command text, chat and
terminal line, its spawn state loses the feed identity, and the snapshots are regenerated from the scrubbed actions. Replay stays
deterministic (the shadow still flies); the live room's own log is untouched. Covers the aircraft listed *after* it was in a
room, which the parse-time filter cannot.
Every sample also carries its feed provenance — `Instance` (the TRACON / ARTCC / airport that produced the observation) and
`ObservedAtUtc` (the source's own time) — and the room records a `RecordedLiveTrafficStatus` (wall clock, connected, message
age, in-scope count) with every status broadcast while live traffic is on. Neither affects replay; they are what maps a
bundle's sim seconds back to the real-world feed window (see *Reproducing a report* below).

## Server room integration (yaat-server `src/Yaat.Server/LiveTraffic/`)

- **`LiveTrafficStore`** — process-wide singleton of `LiveTrack`s (one immutable record per real aircraft, up to one `LiveView`
  per source; `Freshest` picks the latest). 1° grid index on the freshest position; `Query(ILiveTrafficScope, buffer)`. Written by
  the SWIM ingest below; tests fill it directly.
- **`RoomLiveTrafficScope.Build`** — what the room sees. The student's *own* facility is found by position callsign in the
  facility tree (`TrackOwner.FacilityId` is the STARS facility — a tower position's owner is its TRACON). Center room → the
  `ArtccBoundaryDatabase` polygon + 30 nm, no ceiling. Tower / TRACON room → 25 nm around each of the facility's towers ∪ their
  Class B/C volumes, under `max(tower-cab aircraftVisibilityCeiling, Class B/C top + 2 000)` (tower; AIM 3-2-4 — the outer
  area runs up to the delegated airspace ceiling) or 15 000 ft (TRACON; delegated airspace tops out ~10–17k). No facility or
  geometry → 60 nm around the primary airport under the TRACON ceiling. A non-zero `LiveTrafficCeilingFt` setting always wins.
  Reviewed by aviation-sim-expert 2026-08-26. The boundaries are the FAA NASR ARB LOW+HIGH strata (one ring each, union for
  containment) built by `tools/build-artcc-boundaries.py` (re-run per 28-day cycle; Honolulu, San Juan and the oceanic centers
  carry a single UNLIMITED volume; Guam has no ARB segments and is absent), so the 30 nm buffer only covers the handoff band.
- **`ShadowTrafficSync.Sync`** — the last pre-physics step (`TickProcessor` `Pre.LiveTraffic`), so a sample lands at second *t*,
  is recorded at *t*, and replays pre-tick at *t*. Inert while `IsBroadcastSuppressed` (rewind reconstruction) or in tape
  playback — the recorded samples own the world then. **Time anchor**: `(wallUtc, ElapsedSeconds)` set on the first sync and
  re-set whenever the cadence breaks (elapsed did not advance by exactly one, or > 5 s of wall clock passed — pause, rewind,
  stall); a sample lands at `anchor.Elapsed + (ObservedAtUtc − anchor.Wall)`, clamped to ≤ now. Per track (callsign order): a
  callsign owned by a simulated or assumed aircraft is skipped with one terminal line; a `DEL`-suppressed one is skipped; a new
  one spawns through `SimulationEngine.ApplyLiveTrafficSample` with the `LiveTrafficAircraftFactory` template (type from the
  feed, else the FP, else `ZZZZ`; reported beacon `MarkUsed`; no track owner; `ExternalId` = GUFI; surface tracks get the primary
  `AirportId`) followed by the shared spawn broadcast + `AfterAircraftSpawned`; an existing shadow just gets the sample.
- **Removal** — staleness is **sample age, never store presence**: a view older than its source's window (STARS 15 s ≈ 3 scans,
  ERAM 50 s ≈ 4 sweeps — twice the sim's ERAM coast, kept as SWIM-cadence margin — ASDE-X 5 s) is skipped, so a feed that keeps
  repeating an old view neither spawns nor refreshes a shadow, and a shadow whose last applied `ObservedAtSimSeconds` is older
  than the window is removed (coasting meanwhile): `Dropped` when the store lost the track, `Stale` when it still holds a stale
  view, else `OutOfScope`. Teardown mirrors auto-delete in order: `RemoveLiveTraffic`
  (world + recording) → assignments → delayed queue → change tracker → beacon `Release` → `AircraftDeleted` + CRC disconnect.
  `DEL` on a shadow removes it as `Deleted` and adds it to `RoomLiveTrafficState.Suppressed` until live traffic is toggled;
  turning the setting off removes every shadow as `Disabled` (assumed aircraft stay).
- **Wire** — `AircraftStateDto.IsLiveTraffic` / `LiveTrafficStale` / `LiveTrafficSource` (+ client `AircraftDto`), all three in
  `TrainingDtoFingerprint`. Shadows are excluded from auto-TDLS, auto arrival strips and the rolling-call strip
  (`IsDepartureAircraft` / `IsArrivalCandidate` / `IsApproachDepartureCandidate`).

## SWIM ingest (yaat-server `src/Yaat.Server/LiveTraffic/Swim/`)

The feed is the FAA SWIM Cloud Distribution Service (SCDS): two Solace queues, nationwide — STDDS (TAIS terminal tracks +
TRACON flight-plan blocks, SMES/ASDE-X surface position reports) and SFDPS (FIXM en-route tracks + full flight plans). Design and
measurements: yaat-server `docs/plans/live-traffic-swim/04-swim-ingest.md`; deployment: yaat-server `SELF_HOSTING.md`.

- **Transport** — `SwimIngestHostedService` runs, per configured product (`Swim:Stdds` / `Swim:Sfdps`, only while
  `LiveTraffic:Enabled`), a `SolaceSwimFeedSource` (Solace .NET API, AutoAck queue flow, TLS against the bundled DigiCert root,
  exponential reconnect) feeding a bounded drop-oldest channel, and a worker that appends the raw body to the rolling
  `SwimRawLogWriter` window (Brotli per product-hour, one stream per file — a restart inside the hour opens a file named by its first message's second rather than appending — size/age capped; the only history SCDS offers), peeks the root element for
  metrics, parses, and correlates. `SwimReplaySource` drives the same pipeline from raw-log files (the parser/correlator harness
  and the seam for the repro harness in plan 07).
- **Parsing** — `SwimMessageParser` dispatches on the document element's local name (`TATrackAndFlightPlan`, `asdexMsg`,
  `MessageCollection`) to one forward-only `XmlReader` pass each; unknown children are skipped, unknown roots and malformed bodies
  yield null. Emptied FIXM attributes (`nasRouteText=""`, `arrivalPoint=""`) read as absent, never as a value, and `ZZZZ`
  never replaces a real airport. Records are partial by design (TAIS sends track-only, plan-only and enhanced-data-only records; ASDE-X partial
  reports carry a position and little else; every SFDPS message is a delta), so the typed records under `Messages/` are nullable
  throughout. Fixtures in `tests/Yaat.Server.Tests/LiveTraffic/Swim/Fixtures/` are real messages from the 2026-08-28 capture
  (airline callsigns only, ICAO24s replaced).
- **Privacy (LADD)** — the SCDS agreement obliges subscribers to filter aircraft on the FAA's Limiting Aircraft Data Displayed
  list (registration, call sign, Mode S address, historical data included). `LaddList.Load` reads the deployment's `yaat-ladd/1`
  JSON (`LiveTraffic:LaddListPath`, built by yaat-server `tools/refresh-ladd.py`, shipped by `deploy-ladd.ps1`) and **fails
  closed**: a missing, unreadable, empty or > 45-day-old list makes the ingest service log `SWIM ingest refused`, set
  `FeedConfigured=false` (room toggle hidden) and never open a flow. `LaddFilter.Apply` runs between parse and correlate and drops
  every TAIS record, ASDE-X report or SFDPS flight carrying a listed identity, so blocked aircraft never reach the correlator's
  indices, the store, a recording or a room; identities accumulate across monthly lists (ever listed = blocked), which is what
  makes replaying an old raw log safe with the current list. The list itself is restricted data: never in the repo or the image.
- **Correlation** — `SwimTrackCorrelator` keys tracks by callsign and resolves identity-less records through
  `(TRACON, track number)`, `(airport, ASDE-X track)`, ICAO24 and ERAM GUFI indices learned from earlier records. One `LiveView`
  per source with **sticky instance** ownership (another TRACON/ARTCC may overwrite only after 12 s / 24 s / 2 s of silence),
  backwards-position (radar sources only — surface turnarounds are real) and older-observation skips, a **stale skip** for positions already 5× the source timeout old when they
  arrive (a durable queue draining its backlog after a reconnect — the record's identity and plan parts still apply), TAIS `terminated`+`delete` and `drop` status dropping the STARS view, track-number
  reuse under a new callsign dropping the old flight's view (turnaround), SFDPS `DROPPED`/`COMPLETED`/`CANCELLED` ending the ERAM
  view, vehicles / `UNKN` / `OPS*` / `PO\d+` ignored. ASDE-X also tracks arrivals on final and departures after liftoff, and
  the ASDE-X view means "on the surface" (`LiveTrafficSample.IsOnGround`), so a report whose Mode S ground bit (`status/gbs`)
  is clear and whose altitude is more than 400 ft above the field (`AirportElevationLookup`, uncorrected pressure altitude
  hence the margin) drops the surface view and leaves STARS/ERAM in charge until the aircraft is back on the ground. An ERAM
  **callsign amendment** (`flightIdentificationPrevious`, or a GUFI that already names a callsign ERAM has been reporting)
  folds the old entry into the new callsign — views the new one lacks, empty plan fields, every index key — and retires
  the old, so a target never shows twice. A GUFI-keyed flight-plan index (SFDPS publishes plans hours ahead; 6 h TTL)
  fills route / filed altitude / speed / equipment into tracks fill-if-empty when TAIS or ASDE-X links the GUFI. `Reap()` (every
  60 s) removes views past 10× their timeout, evicts viewless tracks together with every index entry (10 min after the last
  activity, or 6 h for an entry whose plan is still waiting to activate — an ERAM COMPLETED/DROPPED/CANCELLED marks the entry
  `Ended` so a finished flight never lingers for the plan TTL), and expires uncorrelated plans; every change is published to
  `LiveTrafficStore` as an immutable `LiveTrack`. `SwimCorrelatorSoakTests` drives generated nationwide-shaped traffic under
  a fake clock (a small fleet for 9 simulated hours on every run; the full fleet for 24 h under the `Nightly` trait) and asserts
  that store, plan index and identity indices plateau.
- **Health** — `LiveTrafficStore.ReportFeedState` (any product connected, last message time) feeds the room status broadcasts;
  meter `Yaat.Server.Swim` carries message counts by root element, drops, broker lag, handle time and store gauges; a
  `SWIM store: …` log line every minute summarises the correlator's counters.

## Reproducing a report (yaat-server plan 07)

"Live traffic did something odd at 17:42Z at NCT" is answered offline from the server's rolling raw log, never by waiting
for the sky to repeat itself:

1. `python tools/bug_bundle.py live-status <bundle> --callsign X` — the feed-health series over the session and, from the
   callsign's samples, the UTC window and facility instance to slice by (prints the `swim-slice.ps1` line to run).
2. `.\swim-slice.ps1 -From <utc> -To <utc> -Artcc ZOA -Facility NCT` — copies the raw-log hour files covering the window
   off the droplet's `yaat-swim-raw` volume (must be inside the retention window; SCDS has no history) and runs yaat-server
   `tools/Yaat.SwimSlice cut`: the facility's TAIS and ASDE-X batches, the SFDPS flights inside its geometry, and every
   flight-plan message for the callsigns/GUFIs the slice carries (from before the window too, so correlation is warm).
3. `dotnet run --project tools/Yaat.SwimSlice -- trace --callsign X <slice>` (yaat-server) — the raw TAIS/ASDE-X/SFDPS records
   for the callsign interleaved with the correlator's decisions (view written, sticky-rejected, backwards, unresolved, rekeyed,
   dropped, terminated, ended, evicted) and the store's final view of it.
4. A test: `SwimSliceReplay.Load(files, store, ladd, clock)` + `AdvanceTo(utc)` asserts on the store at an instant;
   `SwimReplayHarness.Attach(room, files, ladd)` + `StepRoom(n)` pushes the slice through a real `RoomEngineTestHarness` room
   (its own store and clock) so shadows spawn, coast and get removed exactly as live. Fix; keep a regression fixture only as a
   hand-built raw log from the scrubbed parser fixtures (`SwimReplayHarness.WriteRawLog`).

Raw captures and slices are FAA data: they live under yaat-server `.tmp/` and are never committed or attached to issues.
The same `SwimIngestPipeline` (parse → LADD → correlate) runs live ingest, the harness and the capture test, so what the
harness shows is what the server did.

## Client (yaat `src/Yaat.Client*`)

- **Model** — `AircraftModel.IsLiveTraffic` / `LiveTrafficStale` / `LiveTrafficSource`, copied in `FromDto` and `UpdateFromDto`.
  The assume hand-off flips `IsLiveTraffic` in the same `AircraftUpdated`, so every surface below re-evaluates at once.
- **Applicability** — `AircraftCommandApplicability.IsControllable(ac)` (`!IsLiveTraffic`) gates every maneuver predicate, so
  the phase-aware menu builders offer nothing for a shadow; `CanAssume(ac)` = airborne shadow. `LiveTrafficMenuItems.Add`
  (Views/) appends "Assume control" / "Assume and track" (two commands — the server doesn't couple `ASSUME` and `TRACK`) and
  each right-click surface (`RadarView.ContextMenus`, `DataGridView.axaml.cs`, `GroundView.axaml.cs`) branches on
  `IsLiveTraffic` to keep only Track / Coordination / Data Block / Display / Delete for shadows.
- **Rendering** — `TargetRenderer.DrawPositionSymbol` draws a shadow with `_shadowSymbolPaint` (dashed outline circle);
  `ResolveTargetColors` applies `StaleAlpha` (128) to symbol and datablock when `LiveTrafficStale`. `GroundRenderer.DrawAircraft`
  uses the stroke `_shadowAircraftPaint` for shadows with the same stale alpha. Datablock content is untouched (`Status` = `LIVE` /
  `LIVE CST` from `AircraftStatusDescriber`).
- **Aircraft List** — `MainViewModel.IsAircraftVisible(ac, showOnlyActive, filter, LiveTrafficListFilter)`; the tri-state
  (`All` / `HideLive` / `OnlyLive`, `Yaat.Client.Models.LiveTrafficListFilter`) persists via `UserPreferences.LiveTrafficListFilter`
  and is picked from the status-bar indicator's context flyout (three radio adapters `LiveTrafficListShowAll/HideLive/OnlyLive`).
- **Session flyout / status bar** — `SessionLiveTrafficEnabled` + `SessionLiveTrafficCeilingFt` (both under the echo guard;
  a refused enable reverts the checkbox and prints the reason). The sim-rate picker binds `IsEnabled` to
  `!SessionLiveTrafficEnabled` (`SimRateToolTip`). `LiveTrafficStatusText` (`FormatLiveTrafficStatus`) renders
  `LiveTrafficStatusDto` as `LIVE · n tracks · age s` / `LIVE · disconnected` / `LIVE · not configured`, visible only while the
  session flag is on (`IsLiveTrafficStatusVisible`).

## Tests

`tests/Yaat.Client.Tests/`: `AircraftCommandApplicabilityTests` (shadow → only Assume; surface shadow → nothing; assumed →
normal), `AircraftViewFilterTests` (tri-state), `LiveTrafficStatusTextTests`; `tests/Yaat.Client.UI.Tests/Views/TargetRendererColorTests`
(stale alpha).

`tests/Yaat.Sim.Tests/LiveTraffic/`: `LiveTrafficKinematicsTests` (dead reckoning, 4 Hz motion, air vector under a
100-kt crosswind, coast timing, out-of-order/jump samples, derived VS, surface pose, snapshot round trip, status,
pilot-AI skip), `LiveTrafficCommandGateTests`, `LiveTrafficReplayTests` (live run vs `Replay` vs `ReplayOneSecond`
positions identical to 0.001 nm).
