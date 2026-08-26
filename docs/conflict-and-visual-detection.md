# Conflict, Alert & Visual-Acquisition Detection

> Read this before touching `ConflictAlertDetector`, `GroundConflictDetector`, `AtpaProcessor`/`AtpaVolumeGeometry`,
> `VisualDetection`/`VisualAcquisition`, `WakeTurbulenceData`, or their server consumers in `TickProcessor` and
> `CrcBroadcastService`. **These are four unrelated mechanisms that only share a theme — threshold geometry.** They
> share no code, run at different points in the tick, and a change to one does not apply to another. This doc is the
> consolidated tuning-constant reference and the run-location / suppression-rule map.

## Scope — four detectors, one theme

| Detector | File | Answers | Output | Behavior change? |
|---|---|---|---|---|
| Airborne Conflict Alert (CA) | `ConflictAlertDetector.cs` | "Are two associated tracks losing separation?" | STARS `ActiveConflict` set, broadcast to CRC | No — display alert only |
| Ground conflict / proximity | `GroundConflictDetector.cs` | "Will two ground movers collide?" | `Ground.SpeedLimit` cap | **Yes** — slows/stops taxiing aircraft |
| ATPA in-trail sequencing | `Data/Vnas/AtpaProcessor.cs` | "Is this arrival too close behind its leader?" | STARS ATPA cone advisory in CRC broadcast | No — advisory display |
| Visual acquisition | `VisualDetection.cs` / `VisualAcquisition.cs` | "Can the pilot see the field / traffic?" | Drives pilot "in sight" reports (RTIS/RFIS, FOLLOW) | Pilot reports / approach state |

They are easy to conflate because all four read "two things near each other" and all four live in `Yaat.Sim`. They
do not. Ground conflict is the only one that alters aircraft motion; CA and ATPA are pure CRC display; visual
acquisition drives pilot speech and `Approach.HasReported*InSight` flags.

## Where each runs in the tick

See [tick-loop.md](tick-loop.md) for the full PrePhysics → Physics×4 → PostPhysics structure and the broadcast cadence.
These four hook in at four different points:

- **Ground conflict** — inside the **physics sub-tick, before `FlightPhysics.Update`**:
  `GroundConflictDetector.ApplySpeedLimits(_aircraft, GroundLayout, deltaSeconds)` at `SimulationWorld.cs:256`. It runs
  4× per sim-second (once per 0.25 s sub-tick) so the freshly-written `Ground.SpeedLimit` is consumed by physics in the
  same sub-tick. This is the only detector that runs more than once per sim-second.
- **Airborne CA** — **PostPhysics**, server-side. Two sibling passes registered in the PostPhysics fan-out:
  `Run("Post.ConflictAlerts", ProcessConflictAlerts)` (terminal STARS) and
  `Run("Post.EramConflictAlerts", ProcessEramConflictAlerts)` (en-route ERAM STCA). Each runs once per
  sim-second after all physics has integrated and maintains its own conflict set
  (`SimulationEngine.ConflictAlerts` vs `EramConflicts`).
- **Visual acquisition (field & FOLLOW traffic)** — **PostPhysics**, server-side: the field-acquisition block in
  `TickProcessor` (`~:1232–1360`). Plus a per-aircraft re-check inside the physics step via
  `PilotObservationUpdater.Update` (step 10 of `FlightPhysics.Update`, runs after `UpdateCommandQueue`) for pending
  `TrafficAcquisitionObservation` / `FieldAcquisitionObservation` queued by RTIS/RFIS. A bare forced verb
  (`FOLLOWF`/`RTISF`) with no typed callsign consumes that still-pending `TrafficAcquisitionObservation` as its target
  and clears it — the force declares the traffic acquired, so the "still looking" state is superseded rather than
  waiting for a later acquisition.
- **ATPA** — **not in the tick at all**. It is computed during the CRC broadcast pass:
  `CrcBroadcastService.ComputeAtpaResults` (`CrcBroadcastService.cs:1255`) calls `AtpaProcessor.Process` against the
  current snapshot each broadcast. It is display-cadence work, not physics.

---

## Airborne Conflict Alert — `ConflictAlertDetector`

`ConflictAlertDetector.Detect(aircraft, context)` is a static `O(n²)` pairwise scan. The model: predict each aircraft
5 s ahead by straight-line extrapolation, then alert on a pair when current **or** predicted separation violates the
threshold and the pair is not diverging and neither track is in an approach-corridor suppression volume.

### Eligibility — `FilterEligible`

A track must pass all four gates to be considered (`ConflictAlertDetector.cs:116`):

1. **Airborne** — `!ac.IsOnGround`.
2. **Mode C** — `ac.Transponder.Mode == "C"` (case-insensitive). Mode-A / standby tracks are not associated.
3. **Not CA-inhibited** — `!ac.Stars.IsCaInhibited`.
4. **Supported** — `!ac.Ghost.IsUnsupported` (user-typed FP ghosts and unsupported tracks excluded).

### Prediction & the in-conflict test — `Predict` / `IsInConflict`

- **`Predict`** (`:147`) projects position along `TrueTrack` by `GroundSpeed × 5 s` and altitude by
  `VerticalSpeed × 5 s`. Linear; no turn or level-off modeling.
- **Divergence suppression first** (`:178`) — if `IsDiverging`, return no-alert immediately. See the footgun below:
  **parallel is not diverging**.
- **Approach-corridor suppression second** (`:186`) — if **either** track is inside **any** corridor volume, suppress.
  Purely geometric; ignores phase and active approach (footgun below).
- **Hysteresis latch** (`:191`) — if the pair `Id` is already in `context.ExistingConflictIds`, it stays alerted until
  separation exceeds the **clear** thresholds (3.3 nm **and** 1100 ft). This prevents an alert from flickering at the
  threshold boundary.
- **New alert** (`:196`) — for a fresh pair, alert if **current OR predicted** separation is below 3.0 nm **and**
  1000 ft.

### Pair identity & consumption

- `MakeConflictId(a, b)` (`:238`) builds `"CA_{first}_{second}"` with the two callsigns ordinally sorted, so the same
  pair yields the same `Id` regardless of iteration order — this is what makes the hysteresis latch stable.
- The server consumer (`TickProcessor.ProcessConflictAlerts`) diffs `detected` against
  `room.ActiveSim.ConflictAlerts.Conflicts`: new pairs become `ActiveConflict` rows and are broadcast via
  `BroadcastConflictAlertsAsync`; pairs that drop out are removed and broadcast as cleared. It seeds
  `ExistingConflictIds` from the live conflict-set keys, which is how the hysteresis state survives across ticks.
- **Two consumers, two shapes.** CRC gets new/cleared *diffs* on the `StarsShortTermConflicts` topic (above). The YAAT
  client's own radar view gets the **full set** on the `/hubs/training` `ConflictAlertsChanged` broadcast, built by
  `TrainingBroadcastService.BuildConflictAlerts` and pushed by `TickProcessor.BroadcastConflictAlertsIfChanged` — which
  guards on a signature of the sorted pair ids, because the detector re-runs every sim-second and would otherwise push an
  identical payload per tick. `TrainingHub`'s `RoomStateDto` seeds the same list so a client joining mid-conflict sees it
  immediately. `TrainingRoom.LastBroadcastConflictSignature` starts at `""` (not null) so a conflict-free room doesn't
  push a pointless empty payload on its first tick. Client-side rendering: see
  [radar-rendering.md](radar-rendering.md) § Conflict alerts.
- **Only terminal CA reaches the YAAT radar.** `EramConflictDetector`'s separate set (`ESTCA_` ids, 4-minute lookahead,
  5 nm minima) is *not* wired to `ConflictAlertsChanged`. This matters: the client draws a fixed 3 nm ring, which would
  be actively misleading for an en-route pair. Anything that later routes ERAM pairs into that field must revisit the
  ring radius.

### Approach-corridor suppression volumes — `BuildCorridors`

CA is suppressed for traffic established on final at an internal airport (where 3 nm/1000 ft is expected and would fire
constantly). `BuildCorridors(internalAirports, navDb)` (`:84`) builds **two corridors per physical runway** (one per
runway end), each anchored at that end's threshold and extending out along the **reciprocal** of runway heading. Airport
lookup tries the bare LID first, then a `"K"`-prefixed ICAO fallback (mirrors `ApproachGateDatabase`). The internal-
airport list comes from `starsConfig.InternalAirports`.

`IsInsideCorridor` (`:219`) is a centerline box test:

- **Cross-track** ≤ 2.0 nm half-width (so a 4 nm-wide corridor).
- **Along-track** between 0 (threshold) and 30 nm.
- **Altitude** between field elevation and a sloped ceiling: `fieldElev + alongTrack × 318 ft/nm + 1500 ft`, i.e. the
  3° glideslope (tan 3° × 6076.12 ≈ 318 ft/nm) plus 1500 ft of headroom.

---

## ERAM STCA — `EramConflictDetector`

The en-route (Center) Short-Term Conflict Alert. A **separate detector and conflict set** from the terminal CA
above — same "two associated tracks losing separation" theme, but tuned to ERAM's model (docs/crc/eram.md §377-383)
rather than STARS's. Shares only the eligibility gate (`ConflictAlertDetector.IsEligible` — airborne, Mode C, not
CA-inhibited, supported). Output is `SimulationEngine.EramConflicts` (`EramConflictState`, keyed by `ESTCA_{a}_{b}`),
maintained by `TickProcessor.ProcessEramConflictAlerts` and published on the `EramShortTermConflicts` topic.

The model differs from terminal CA on every axis:

- **4-minute look-ahead** (vs 5 s). This is the defining feature: real ERAM flashes minutes before a loss, so a
  converging pair 25 nm apart *now* is caught. `LookAheadSeconds = 240`.
- **Swept closest-point-of-approach** for the lateral test (vs single-endpoint prediction). `LateralClosestApproachNm`
  solves the CPA analytically in a local tangent plane centred on aircraft A — exact for constant-velocity motion, and
  a diverging pair naturally clamps to its current (t=0) separation.
- **Lateral 5 nm, or 3 nm when both targets ≤ FL230** (reduced-separation airspace) — vs the flat terminal 3 nm.
  Uses present altitude (§379 is present-tense).
- **Data-block-altitude vertical envelope** (vs a point). Each target's altitude over the window is the interval
  `[min(current, dataBlockAlt), max(current, dataBlockAlt)]`; a pair conflicts vertically iff the two intervals come
  within 1000 ft (`gap < 1000` — 1000 ft exactly is still separation). This implements §381 (a descending target is
  assumed to level off at its assignment) and §383 (a level target may move toward a differing assignment "at any
  time"). Data-block altitude = `Eram.LocalInterimAltitude ?? ProcedureAltitude ?? InterimAltitude` (CRC field-B
  precedence, **hundreds of feet** → ×100) else the filed `FlightPlan.CruiseAltitude` (feet); if neither, a
  vertical-speed projection (deliberately conservative — see the source comment).
- **No approach-corridor suppression** (terminal-specific).
- **Hysteresis:** an active alert holds until the CPA ≥ latMin + 0.3 nm OR the vertical gap ≥ 1100 ft.

### §377 facility gate (per subscriber, not in the detector)

The detector produces the **facility-agnostic** room-wide set. The §377 rule — "alerts are only generated if one of
the two targets is owned by a controller in *your* ERAM facility" — is applied per subscriber in `CrcBroadcastService`:
`ProcessEramConflictAlerts` stamps each `EramActiveConflict` with the owning ERAM facility of both targets
(`ac.Track.Owner.FacilityId`, refreshed each tick so a handoff is reflected), and the broadcast/data-block paths gate
on `OwnerFacility{A,B} == subscriber.FacilityId`. New-alert broadcasts are facility-filtered; **deletes go out
unfiltered** (an unknown id is a harmless no-op, and this prevents an orphaned flash if a target was handed off before
the conflict cleared). The send-once FDB `ConflictStatus` is refreshed via the coarse per-aircraft "in any ERAM
conflict" trigger, then each subscriber recomputes its own gated status. **Known gap:** because the topic broadcast is
event-driven (new/cleared), a handoff of an aircraft *mid-conflict* does not re-attribute the RDB flash to the newly
owning facility until the conflict re-forms; the FDB path (per-tick) is unaffected.

### Controlled-vs-uncontrolled (Mode-C intruder → CDB)

The detector pairs any two eligible targets regardless of tracking, so `ProcessEramConflictAlerts` classifies each
pair by ownership/correlation to fill `EramActiveConflict.IntruderCallsign`:

- **Both untracked** (`Track.Owner == null` on both) → dropped: a conflict alert protects a *controlled* aircraft
  (7110.65 §2-1-6, §5-13-1) and the §377 gate has no owned target to attach to.
- **One side untracked *and* uncorrelated** (no owner **and** `FlightPlan.HasFlightPlan == false`) → that side is the
  **Mode-C intruder**. The tracked side's FDB status becomes `ControlledUncontrolled` (flashes) and the intruder's
  data-block `Format` becomes `Cdb` — a callsign-less `TFC`+beacon block (docs/crc/eram.md §844-852), *replacing* its
  limited data block rather than adding a second one.
- **Otherwise** (both tracked, or the unowned side still has a filed flight plan) → a normal `ControlledControlled`
  STCA; a correlated-but-unowned target flashes an ordinary data block, not a CDB.

The classification is bundled per subscriber into `EramConflictRoles` (ControlledControlled / ControlledUncontrolled /
Intruders sets, §377-gated) and consumed by `DtoConverter.ToEramDataBlock`. The intruder's CDB renders entirely from
its existing `EramTarget` fields (beacon, Mode-C altitude, vertical rate) — no new target data. YAAT models no
controller altitude filter, so the 1000 ft vertical envelope bounds intruder relevance instead (§5-13-5 simplification).

---

## Ground conflict detection — `GroundConflictDetector`

This is the only motion-affecting detector. **The resolution algorithms (the seven `PairKind` handlers, the
`SameEdgeHeadOn` deadlock-break, the converging-merge arbitration) are owned by [ground/navigator.md](ground/navigator.md)** — read
that for the per-kind behavior, the `[Classify]` / `[Pair]` diagnostic logging, and how the navigator honors the cap.
Every close-range conflict resolves **one-holds-one-goes** (deterministic holder, never both stopped). Summary only here:

`ApplySpeedLimits` (`GroundConflictDetector.cs:113`) clears every aircraft's `Ground.SpeedLimit` to `null` at the start
of the sub-tick, then for each pair classifies into exactly **one** of seven `PairKind`s (`:97`) and runs that handler,
writing the minimum surviving cap onto `Ground.SpeedLimit`:

| `PairKind` | Meaning |
|---|---|
| `Distant` | Too far apart to interact — no cap |
| `Stationary` | One/both parked or held — wingspan-lateral-clearance bypass lets movers pass |
| `Pushback` | A pushing aircraft is involved. A pusher hard-stops for a moving/taxiing/pushing aircraft in its rear arc, but a **genuinely parked/held** neighbor (`IsParkedOrHeld`) is a passable obstacle — it routes through the graduated closing logic (`ApplyClosingLimit`) so a gate pushback clears an aircraft parked at the adjacent gate, stopping only within actual collision distance |
| `SameEdgeTrailing` | Same edge, same direction (`ToNodeId` matches) — trailing aircraft caps to follow |
| `SameEdgeHeadOn` | Same edge, opposite direction — deterministic holder picked (more remaining route, callsign tie-break) |
| `Converging` | Routes share an upcoming node from different edges — yielder (farther from the node) holds, merge-order leader (nearer) proceeds through first |
| `Crossing` | Paths cross |

The navigator reads `Ground.SpeedLimit` via `ClampBySpeedLimit` (`GroundNavigator.cs:1113`) and never overruns it.

For `Converging` and `SameEdgeTrailing` the chosen yielder also gets a **display-only** annotation —
`Ground.AutoYieldTarget` (the other callsign) plus `Ground.AutoYieldIsFollowing` (true for in-trail). Both reset to
their defaults at the top of `ApplySpeedLimits` and re-derive each tick, exactly like `SpeedLimit`. The annotation
itself does **not** set `Hold`/`IsImmobile` and does not move the aircraft — the `SpeedLimit` cap does that (for a
converging-merge yielder within stop distance the cap is a full stop until the leader pulls away, then it follows in
trail). The annotation drives the client's "→{target} (auto)" ground datablock badge and the "Yielding to" /
"Following" right-click wording, distinct from a controller GIVEWAY. The wire carries them on
`AircraftStateDto.AutoYieldTarget`/`AutoYieldIsFollowing` (in the `TrainingDtoFingerprint` so the badge updates live).

---

## ATPA in-trail sequencing — `AtpaProcessor` + `AtpaVolumeGeometry`

ATPA (Automated Terminal Proximity Alert) advises the controller when an arrival is closer than the required wake/radar
separation behind the aircraft ahead in the same approach volume. `AtpaProcessor.Process` (`AtpaProcessor.cs:25`)
returns a `Dictionary<callsign, AtpaResult>` consumed by `CrcBroadcastService` and surfaced in the STARS datablock.

### Exclusive single-volume association

ATPA in-trail monitoring is strictly **per final approach course** (7110.65 §5-9-5/§5-9-6: in-trail separation
applies to "the same final approach course"; *different*-final pairs get a separate diagonal standard). So `Process`
runs in **two phases**: an aircraft is associated to **exactly one** volume, then paired only within it. This
matters where adapted volumes physically overlap — closely-spaced or convergent finals (KOAK 30 vs 28R are only
~18° apart, and the OAK 30 volume's 90° `MaximumHeadingDeviation` + 3 nm half-width reach across the 28R final).
Without exclusive association an aircraft falls inside both boxes and gets coned in-trail against traffic on the
*other* runway — the wrong separation standard for that pair.

**Active-volume filter (first).** `Process` first drops **disabled** volumes via `AtpaVolumeGeometry.IsActiveVolume`.
vNAS disables a volume by repointing its `airportId` at an unrelated airport (e.g. the SFO side-by volumes set to
`OVE`) while leaving the threshold at the real runway, so a volume with a **non-empty** `airportId` that resolves no
runway end within 0.5 nm of its threshold is treated as inactive and never captures traffic or competes in best-fit.
A volume with no `airportId` (legacy/synthetic) or any volume when the nav DB is unavailable stays active.

**Phase 1 — membership.** For each aircraft, every active volume it passes the **four candidate filters** below is a
candidate; it is assigned to the single **best-fit** one. The fit score (`AtpaProcessor.FitScore`, lower = better)
sums **heading deviation** (normalized to the 30° established tolerance) and absolute **cross-track distance** to the
centerline (normalized to a **fixed** `CrossTrackFitReferenceNm` ≈ 1 nm — *not* the volume's own half-width, which
would discount a geometrically wide volume and could pull a track nearest its own centerline into a wider neighbor on
closely-spaced parallels). Both terms are required: heading-deviation alone degenerates for true parallels (28L/28R
share a course), cross-track alone can misjudge convergent finals. On a geometric tie (within `FitTieEpsilon`), the
**scratchpad runway** breaks it — `ScratchpadMatchesVolumeRunway` parses a runway from `Stars.Scratchpad1` (tolerant
suffix match: the lossy scenario `I8R` matches canonical `28R`, `7L` matches `17L`) against
`AtpaVolumeGeometry.VolumeRunwayDesignator`; an empty/unparseable scratchpad just doesn't fire, leaving association to
geometry. Final tie-break is input order, for determinism. Re-association is recomputed statelessly each broadcast
(no hysteresis): an established track sits at `cross ≈ 0` on its own centerline, anchoring its own-volume score near
zero, so the assignment is stable without persisted state.

The four candidate filters:

1. **`AtpaVolumeGeometry.IsInside`** — a threshold-anchored rectangle that extends **OUTBOUND** from the threshold
   (the reciprocal of the approach course — aircraft established on the final sit behind the threshold relative to the
   landing direction; projecting along the course instead excludes every real arrival): altitude between `Floor` and
   `Ceiling` (**in hundreds of feet** — see footgun), ground track within `MaximumHeadingDeviation` of the volume's true
   approach course, along-track 0..`Length` nm out the final, cross-track within `WidthLeft`/`WidthRight` (**in feet**).
   The true approach course comes from **`VolumeTrueHeadingDeg`**, which resolves the actual runway true heading from the
   configured threshold (the config's `magneticHeading` is rounded to the runway designator, so declination-converting it
   would rotate the volume off closely-spaced parallels and pull the neighboring runway's traffic in); it falls back to
   the configured heading as true when no runway matches.
2. **`IsExcludedByTcp`** (`AtpaProcessor.cs:174`) — drops aircraft whose track owner's `{Subset}{SectorId}` TCP code
   matches one of the volume's `ExcludedTcpIds`. The excluded ULIDs are resolved to codes via the same ULID→`{Subset}{SectorId}`
   map (`BuildTcpCodeMap`) used for the monitor/alert cones, then compared against the owner's code.
3. **`ClassifyScratchpad`** — matches `Stars.Scratchpad1`/`Scratchpad2` (selected by the config's `ScratchPadNumber`
   of `"One"`/`"Two"`) against a configured `Entry`, returning the rule's `Type`. **`Exclude`** drops the track from the
   volume entirely (candidate filter — `continue`); **`Ineligible`** keeps it in the chain as a valid lead/reference for
   the track behind it but flags it `SubjectEligible = false` so `PairVolume` emits no cone *for* it. Any non-`Ineligible`
   type is treated as the stricter `Exclude`. An ineligible track interleaved between two arrivals therefore anchors the
   trailing aircraft's cone at the *nearer* (ineligible) lead, not the next eligible one beyond it.
4. **`IsEstablishedOnApproach`** (`AtpaVolumeGeometry.cs:77`) — airborne, `VerticalSpeed ≤ 100 fpm` (`MaxVerticalSpeedFpm`),
   and track within ±30° (`ApproachHeadingTolerance`) of the volume heading. Filters out departures, overflights, and
   vectored traffic that merely pass through the box.

### Sort, pair, and required separation

**Phase 2 — pairing.** `PairVolume` takes only the aircraft *assigned* to that volume in Phase 1, sorted ascending by
along-track distance from threshold (lead at index 0). Each follower (`i ≥ 1`) is paired with the aircraft immediately
ahead (`i-1`). Actual separation is the slant `DistanceNm`; required separation comes from `ComputeRequiredSeparation`:

- The radar floor is **2.5 nm** when `TwoPointFiveApproachEnabled` **and** the follower's along-final distance from
  the threshold is within `TwoPointFiveApproachDistance` nm (vNAS "Reduced Separation Final Approach Distance", ~10);
  outside that distance — or when reduced separation is off — the floor is **3.0 nm**. `TwoPointFiveApproachDistance`
  defaults to 0 in older configs that omit it, so reduced separation simply doesn't apply there.
- The wake (CWT) minimum still binds on top via `max(floor, wake)` — a matrix keyed on the **lead** and **follower**
  weight classes (derived from CWT, falling back to `AircraftCategory`):

| Lead \ Follower | Super | Heavy | Large | Small |
|---|---|---|---|---|
| **Super** | 6.0 | 6.0 | 7.0 | 8.0 |
| **Heavy** | — | 4.0 | 5.0 | 6.0 |
| **Large / smaller** | 3.0 | 3.0 | 3.0 | 3.0 |

**Cone state** (`AtpaConeState`, set by `DetermineConeState`) is the live advisory level, mapped by `DtoConverter` to the
STARS track's `TpaType` (Key 30 / `RemoteTpaType`), which is what CRC actually switches on to draw the cone: **Monitor**
when spacing is healthy, **Warning** (caution/yellow) when a loss is predicted within 45 s, **Alert** (orange) when
already lost or predicted within 24 s. The two **static** adaptation lists tell CRC which positions may see each cone:
`AtpaMonitorTcps` = the volume's `AlertAndMonitor` TCPs; `AtpaAlertTcps` = its `Alert` **and** `AlertAndMonitor` TCPs
(the vNAS `AtpaConeType` enum is `{ Alert, AlertAndMonitor }` — not `Monitor`). Each follower's result is keyed by
callsign; because Phase 1 association is exclusive, an aircraft is paired in at most one volume — real STARS shows only
one ATPA pairing per aircraft.

---

## Visual acquisition — `VisualDetection` + `VisualAcquisition`

`VisualDetection` decides whether a pilot can see the field or another aircraft, per FAA 7110.65 §7-4-3 / §7-4-4 and AIM
§5-4-23 / §4-4-15 (citations are in the source). `VisualAcquisition` is the thin wrapper that bundles the METAR /
elevation / bank-angle inputs so every caller feeds identical inputs. Weather sourcing differs by target: **airport**
(RFIS) acquisition uses the destination's METAR (the destination field is the thing being looked at); **traffic** (RTIS)
acquisition uses the air mass at the ownship's position — `WeatherProfile.GetWeatherNearPosition` resolves the nearest
reporting station within `MetarInterpolator.MaxInterpolationRangeNm` (50 nm), and that station's field elevation is the
AGL→MSL datum for its cloud bases. **All four traffic paths share the ownship-nearest sourcing** via the
`VisualAcquisition` wrappers: the RTIS command first-check, `PilotObservationUpdater` (per-tick re-check),
`AirborneFollowHelper.CheckLeadLifecycle` (follow lifecycle, physics sub-tick), and
`SimulationEngine.TickVisualDetection`'s traffic branch (per-second acquire/maintain). Destination METAR is
field-only — a destination-sourced traffic check would judge the same follower against two different cloud decks /
visibilities in the same tick as the lifecycle check (#344's lost→regained transmission flap). An overflight with no
destination (or a departure bound far away) therefore still gets local-weather gating; with no station within range the
air mass is unknown and acquisition falls back to clear-sky behavior.

Every attempt returns a `VisualAcquisitionResult` carrying `Acquired`, a `VisualAcquisitionFailure` reason, the computed
`DistanceNm`, the `MaxRangeNm` used, and (for ceiling failures) the binding BKN/OVC `CloudLayer` so messages can name it.

### Initial acquisition vs maintained contact

This split is the central design point and the source of a footgun:

- **Initial acquisition** (`TryAcquireAirport` / `TryAcquireAirportForRunway` / `TryAcquireTraffic`) runs the **full
  ordered ladder** of checks and is called with the live `BankAngle`.
- **Maintained contact** (`TryMaintainAirportContact` / `TryMaintainTrafficContact`) runs **weather-only** — the Class A
  floor (airport only), the obscuring BKN/OVC layer, and a **flight-visibility collapse**: when METAR visibility is
  reported below the censored 10 SM maximum and the distance to the target exceeds `visibility × 0.869 × 1.25` nm
  (`MaintainVisibilityToleranceFactor` — a stateless tracking credit that suppresses threshold chatter, since one tick
  of boundary noise irreversibly cancels a follow), contact is genuinely lost per AIM §5-5-11.a.3's "at all times"
  obligation (fog/haze is weather, not finding-geometry). It **deliberately skips the target-finding geometry**
  (`BehindOwnship`, `OppositeSideOfRunway`, `OccludedByBank`, the type-based traffic detection range, and the airport
  horizon/conspicuity caps). The server calls the airport variant once `Approach.HasReportedFieldInSight` is true, and
  the traffic variant once `Approach.HasReportedTrafficInSight` is true. Rationale: the airport reference point is a
  single lat/lon proxy for a multi-acre polygon; at threshold crossing the ARP falls behind the nose, and a geometric
  check would falsely report "lost sight of the field" while the runway is directly under the cockpit. Likewise a lead
  that merely opens the gap is *increasing* separation — never the follower's cue to break off (AIM §5-5-12.a.2).
  The airport visibility distance is measured to the **assigned runway threshold** when known (else the ARP): at a
  sprawling field the ARP can sit beyond a collapsed visibility range while the landing runway is right off the nose.
  The airport maintain threshold is the same `AirportVisibilityRangeNm` envelope the acquisition path uses, times the
  ×1.25 credit — see "The visibility envelope" below.

### Lost-visual-reference consequence — `VisualApproachHelper` (issue #343)

AIM §5-5-11.a.3 is a disjunction: a pilot on a visual approach must at all times have EITHER the airport OR the
preceding aircraft in sight. The detectors above only flip the two in-sight flags; the *consequence* is owned by a
single engine, `Phases/Tower/VisualApproachHelper`, fed from three sites: `CheckLeadLifecycle` (follow lifecycle,
physics sub-tick), `TickVisualDetection`'s field- and traffic-maintain loss branches, and a per-second backstop at the
end of each `TickVisualDetection` iteration (catches "nothing in sight with no loss event this tick", e.g. the lead
landed while the field was never reported — after the field re-acquisition attempt got its chance). The matrix:

- **Lost the lead, field held** — a separation-responsibility handback, not a termination (7110.65 §7-4-3.c.3): the
  follow clears with **no leg freeze** (the clearance authorizes proceeding to the airport), the pilot transmits
  "lost sight of the traffic, field in sight", and a `PendingWarnings` line tells the instructor radar separation must
  be reassumed. Solo separation scoring re-arms automatically (`SoloTrainingEvaluator.IsCoveredByVisualFollow`).
  Non-visual follows (VFR pattern FOLLOW) keep the historical `CancelFollowHoldingLeg` freeze.
- **Lost the field, lead held** — legal steady state (§7-4-3.c.2 NOTE: a follower never needed the field report).
  Silent on frequency; log only.
- **Lost everything** — the visual is no longer legal and **ends**. Committed (Final/Landing/HelicopterLanding/
  LowApproach/Base at/below 1000 ft AGL — `CommittedAglFt`; Base counts because its level-off heading points across
  the final approach course) → go-around via `GoAroundHelper.Trigger` with the reason SPOKEN (AIM §5-5-5.a.2).
  Otherwise → level off at the nearest 100 ft, hold present heading AND present speed (releasing the speed target
  would auto-accelerate a jet back toward 250 in the terminal area), transmit "unable the visual … request vectors"
  (AIM §5-5-11.a.5 — the pilot advises and asks; pilots don't know the MVA, so no self-initiated climb), register the
  transmission as a pending Approach request (suppresses the proactive "request approach assignment" nag and arms the
  standard unanswered-request follow-up) and become a plain vectored IFR target. Both paths run `VoidVisualApproach`:
  `ActiveApproach`, the issued `LandingClearance` AND the pre-armed `Pattern.PendingLandingClearance` nulled, both
  flags and the follow cleared, pending acquisition observations dropped — so a weather flicker cannot silently
  resurrect an approach the controller believes is dead. The runway identity (`AssignedRunway` and
  `Procedure.DestinationRunway`) is kept — the controller usually re-clears the same runway. A fresh CVA re-arms
  everything.

Supporting invariants:

- **CVA preserves its gating basis.** `TryClearedVisualApproach` restores the in-sight flag(s) the acquisition gate
  consumed after `ClearExistingPhases` wipes them (traffic flag only for a FOLLOW clearance). Without this every CVA
  would open with nothing in sight and the backstop would end it immediately; it also removes the post-CVA re-report
  chatter.
- **Any go-around off a visual voids the clearance** (`InstallGoAroundPhases`), covering the manual `GA` command and
  the spacing break-off, not just the lost-reference path.
- **Losing sight consumes the traffic report; a command-driven follow cancel does not.** `HandleTrafficContactLost`
  and `VoidVisualApproach` clear `HasReportedTrafficInSight`; `ClearFollowState` deliberately leaves it alone — the
  dispatcher's generic phase-clear runs before every phase-clearing command including `FOLLOW` itself, whose bare form
  gates on the report, and a vectored-off follower still sees the traffic it called.
- **Every phase that can host a follow watches the lead.** `ApproachNavigationPhase` and `InterceptCoursePhase` now
  call `CheckLeadLifecycle` like the pattern phases and `FinalApproachPhase` do; all call sites bail out of the
  current tick when it fires, because a cancel can now replace or clear the phase list mid-tick.
- **CVA weather gates** (7110.65 §7-4-3.b / AIM §5-5-11.b.1): with a reported METAR, CVA rejects below 1000 ft
  ceiling / 3 SM, and the wide-angle pattern-entry geometry additionally requires ceiling ≥ 2500 ft AGL so the 2000 ft
  IFR downwind isn't authored inside the deck (which would trip the lost-reference consequence immediately). No METAR →
  allowed (§7-4-3.c weather-not-available). `CVAF` bypasses both.

### The airport-acquisition ladder — `TryAcquireAirportCore`

Computed range first: `maxRange = min(horizon, airportSizeCap, AirportVisibilityRangeNm)` where
`horizon = 0.5 × 1.23 × √(altitudeAGL)` nm (geometric horizon scaled by `HorizonScaleFactor = 0.5` for haze/scan/FOV),
`airportSizeCap` from `VisualAcquisition.AirportSizeCapNm`, and `AirportVisibilityRangeNm` is the slant-range
visibility envelope, binding **only when reported below 10 SM** — `10SM` is the US METAR reporting maximum and means
"10 or more" (`UnrestrictedVisibilitySm`), so a clear-day METAR does not cap the range.

**The visibility envelope** (`AirportVisibilityRangeNm`, airport-only — issue #345):
`vis × 0.869 × 1.5 × max(1, AGL / 3000 ft)`. Surface METAR visibility is a surface-horizontal point statistic and a
systematically pessimistic estimate of the flight visibility toward a lit runway complex (AIM §7-1-15.b: prevailing
visibility is the value exceeded over only *half* the horizon circle — the paragraph's own example has one quadrant at
2 while the rest is 3; §7-1-15.c: the *lower* of two observations is used when tower and surface disagree) — the 1.5
credit absorbs that pessimism, bounded so the fully-immersed pilot never sees the field materially beyond required
flight visibility. The height ratio is a Koschmieder slab: sub-10SM visibility is definitionally a surface-based obscuration
(fog/haze/smoke) capped by the boundary layer (`ObscurationTopFt = 3000`), so above it the line of sight crosses
proportionally less of the murk. Result: a legally-clearable 1000/3 visual (7110.65 §7-4-3.b) is reachable —
~3.9 nm at/below 3000 AGL, ~6.5 nm at 5000, ~11.7 nm at 9000. The envelope is shared verbatim with the airport
maintain check (which multiplies the ×1.25 tracking credit on top), which **proves** acquire-then-immediately-lose
can never happen at any altitude; descent stability holds for normal 300–500 ft/nm gradients
(`Airport_AcquireRangeAlwaysInsideMaintainEnvelope` pins the invariant). **Traffic keeps the strict literal cap** —
point-target detection is contrast/resolution-limited and the extended-object slant relief does not transfer.

Then the ordered failure checks:

1. `InClassA` — altitude ≥ 18000 ft (no visual approaches in Class A, 7110.65 §7-2-1.a).
2. `AboveCeiling` — at/above any BKN/OVC layer (`FindBindingCeilingAbove`).
3. `BehindOwnship` — bearing to field outside the ±120° field of view (`AirportForwardHemisphereDeg`; wider than the
   ±110° traffic gate because a runway complex is visible out the side window without being foveated — AIM §8-1-6.c.2
   arc plus a full head turn — covering a standard downwind through abeam-the-numbers).
4. `OccludedByBank` — high-wing occlusion during a turn (see below).
5. `OutOfRange` — `distance > maxRange`.
6. `OppositeSideOfRunway` (runway variant only) — bearing from airport to aircraft more than ±120° off the runway's
   reciprocal, i.e. the aircraft would have to overfly the field to reach the approach end.

### The traffic-acquisition ladder — `TryAcquireTraffic`

No Class A gate (pilots can see traffic in Class A; only visual *separation* is prohibited). `maxRange` =
`WakeTurbulenceData.TrafficDetectionRangeNm(target)` clamped by visibility (again only when reported below the 10 SM
maximum). Checks in order: `MixedCeiling`
(`FindObstructingLayerBetween` — a BKN/OVC base strictly between the two altitudes, so one aircraft is above the deck
and the other below), `BehindOwnship` (±110° — the AIM §8-1-6.c.2 ±100° eyes-only arc plus a ~10° head-turn credit;
RTIS is an alerted search and the AIM's recommended scan starts "over the left shoulder" (§8-1-6.c.3), but a point
target must still be foveated, so the credit is smaller than the airport's +20°), `OccludedByBank`, `OutOfRange`.
Level-flight wing occlusion in the 90–110° aft-of-abeam wedge is deliberately un-modeled (no high/low-wing attribute
exists; AIM §8-1-8.j.1's "lift a wing" maneuver is the real-world mitigation).

### Bank-angle occlusion — `IsOccludedByBank`

(`VisualDetection.cs:250`) Models the high wing blocking a target during a turn. No occlusion below 15° bank
(`MinBankForOcclusion`) or for targets within the ±10° nose cone (`NoseConeDeg`). A right bank raises the **left** wing
(and vice versa); a target on the high-wing side is occluded if it is at or below ownship altitude plus a buffer:
1000 ft for banks ≥ 25° (`SteepBankThreshold` / `SteepBankAltBuffer`), else 500 ft (`ModerateBankAltBuffer`).

### Cloud-layer obstruction — two different helpers

- `FindObstructingLayerBetween` (`:374`) — used for **traffic**: returns the lowest BKN/OVC layer whose **base MSL lies
  strictly between** the two aircraft altitudes. FEW/SCT are ignored (too gappy to reliably block).
- `FindBindingCeilingAbove` (`:414`) — used for **airport**: returns the lowest BKN/OVC layer the **aircraft is at or
  above** (the deck obstructs the downward view of the field).

Both convert layer bases to MSL as `BaseFeetAgl + airportElevation`.

### Airport conspicuity cap — `VisualAcquisition.AirportSizeCapNm`

(`VisualAcquisition.cs:78`) Larger / multi-runway hubs are visible farther than a single GA strip. The cap is linear in
the max pairwise distance between runway endpoints: `10 + maxExtentNm × 5`, clamped to [15, 25] nm. A field with no
runways in the nav DB returns the 15 nm floor.

---

## Wake turbulence & traffic detection range — `WakeTurbulenceData`

`WakeTurbulenceData` (`WakeTurbulenceData.cs`) maps ICAO type designators to FAA CWT codes (A–I) and is the data source
for both the ATPA wake matrix and the visual traffic-detection range. It **must be `Initialize()`'d from
`AircraftCwt.json` before `GetCwt` works** (footgun below).

`TrafficDetectionRangeNm(type, fallbackCategory)` (`:36`) resolves range in three tiers:

1. **Physical dimensions** — `FaaAircraftDatabase.Get(type)` → `ComputeRangeFromDimensions`. A small-angle silhouette
   model: `silhouette = √(wingspan² + 0.7·length² + 0.3·tailHeight²)`, `range = silhouette / 0.002618 rad` (9 arcmin
   alerted-search threshold), converted to nm and clamped to [1.5, 12] nm. The threshold is calibrated for the model's
   only consumer — the RTIS path, an *alerted* directed search (controller supplies clock/distance/type, 7110.65
   §2-1-21.a; the pilot foveates the called sector, AIM §8-1-6.c.2) — with a notch of margin over the ~8 arcmin foveal
   best case as an implicit ground-clutter allowance. Environmental degradation is *not* baked in: visibility, cloud
   layers, bank occlusion, and the scan hemisphere each have their own checks. The 1.5 nm floor is a data-quality guard
   against partial ACD records (nothing real computes that low); the 12 nm ceiling marks where a pure angular model
   stops being valid (beyond it, acquisition is contrast-limited by atmospheric extinction, which the model cannot
   represent — deliberately not scaled by reported visibility). Empirical alerted-acquisition band (unalerted GA
   ~1–1.5 nm, alerted median ~2.5–3.5 nm) per the Lincoln Laboratory / AC 90-48-era studies (external source).
   Example outputs: C172 → 2.7 nm, CRJ7 → 6.4 nm, B738 → 10.1 nm, heavies → clamped 12 nm.
2. **CWT bucket** — fixed values per CWT code A–I (12.0 down to 2.7 nm), derived by running the same formula on a
   representative type per bucket. CWT D is the 41-type heavy bucket (A124/IL76/B741-class) and E is exactly B752/B753 —
   the same D/E identity as the wake GOTCHA below (a stale mapping once swapped their detection ranges too).
3. **Aircraft category** — broad Jet/Turboprop/Piston/Helicopter fallback when the type is in neither table.

`WakeTurbulenceData.OnApproachWakeSeparationNm` is the single shared on-approach wake-separation source: both `AtpaProcessor.ComputeRequiredSeparation` (precise per-type CWT) and the arrival-generator spawn gap (`SimulationEngine.SpacingGapNm`, coarse `WakeClass(WakeClass)` fallback — follower type isn't chosen yet at gap time) call it. It encodes FAA 7110.65 TBL 5-5-2 mile-based CWT minima, keyed by `(leaderCWT, followerCWT)` A-I; a pair absent from the table is 0 (no wake add-on) and the caller floors at the radar minimum. These mile-based minima **replace**, not add to, the legacy time-based wake minima; wake still binds under 2.5 nm final (5-5-4 para 10) — don't drop it.

**GOTCHA — CWT category → weight class** (verified against `tests/Yaat.Sim.Tests/TestData/AircraftCwt.json` `weightCode`): `A→Super; B,C,D→Heavy; E,F,G→Large; H,I→Small`. **CWT D is HEAVY** (B744/A339/IL76/A400/AN22), NOT Large — a stale mapping had D→Large, silently zeroing heavy-widebody wake spacing in the generator (fixed). **B757 is CWT E**, not D. Regional jets (CRJ7/CRJ9/E170) are CWT G → Large.

---

## Tuning & thresholds reference

The payload for aviation review. Every magic number, where it lives, its units, and its meaning. When tuning, change it
here and have `aviation-sim-expert` review against the local FAA references.

### Airborne CA — `ConflictAlertDetector.cs`

| Constant | Value | Units | Meaning |
|---|---|---|---|
| `PredictionSeconds` | 5.0 | s | Straight-line extrapolation horizon |
| `HorizontalNm` | 3.0 | nm | Lateral alert threshold |
| `VerticalFt` | 1000 | ft | Vertical alert threshold |
| `HysteresisHorizontalNm` | 3.3 | nm | Must exceed to clear an existing alert |
| `HysteresisVerticalFt` | 1100 | ft | Must exceed to clear an existing alert |
| `ApproachZoneHalfWidthNm` | 2.0 | nm | Corridor half-width (4 nm full width) |
| `ApproachZoneLengthNm` | 30.0 | nm | Corridor length along extended centerline |
| `ApproachZoneCeilingAboveGsFt` | 1500 | ft | Headroom above glideslope for the corridor ceiling |
| `GlideSlopeFtPerNm` | 318.0 | ft/nm | 3° glideslope slope (tan 3° × 6076.12) |

### ERAM STCA — `EramConflictDetector.cs`

| Constant | Value | Units | Meaning |
|---|---|---|---|
| `LookAheadSeconds` | 240.0 | s | Four-minute trajectory probe |
| `LateralNm` | 5.0 | nm | En-route lateral threshold |
| `ReducedLateralNm` | 3.0 | nm | Lateral threshold when both targets ≤ FL230 |
| `ReducedSeparationCeilingFt` | 23000 | ft | FL230 reduced-separation ceiling |
| `VerticalFt` | 1000 | ft | Vertical envelope-gap threshold |
| `ClearLateralMarginNm` | 0.3 | nm | Hysteresis: added to latMin to clear an active alert |
| `ClearVerticalFt` | 1100 | ft | Hysteresis: vertical gap to clear an active alert |

### ATPA — `AtpaVolumeGeometry.cs` + per-volume `AtpaVolumeConfig`

| Constant / field | Value | Units | Meaning |
|---|---|---|---|
| `ApproachHeadingTolerance` | 30.0 | deg | Established-on-approach track tolerance |
| `MaxVerticalSpeedFpm` | 100.0 | fpm | Above this VS, aircraft is excluded (still climbing) |
| `Floor` / `Ceiling` | per-config | **hundreds of ft** | Volume altitude band |
| `WidthLeft` / `WidthRight` | per-config | **feet** | Cross-track half-widths |
| `Length` | per-config | nm | Along-track length |
| `MaximumHeadingDeviation` | per-config | deg | Membership track tolerance |
| `CrossTrackFitReferenceNm` | 1.0 | nm | Fixed cross-track normalizer in the best-fit `FitScore` |
| `TwoPointFiveApproachDistance` | per-config (~10) | nm | Within this distance of the threshold the floor is 2.5 nm (with `TwoPointFiveApproachEnabled`); else 3.0 |
| Wake matrix | 3.0–8.0 | nm | Required separation by lead/follower weight class (binds on top via `max(floor, wake)`) |

### Visual acquisition — `VisualDetection.cs` + `VisualAcquisition.cs`

| Constant | Value | Units | Meaning |
|---|---|---|---|
| `ClassAFloorFt` | 18000 | ft | No visual approaches at/above (airport only) |
| `HorizonNmPerSqrtFt` | 1.23 | nm/√ft | Geometric horizon coefficient |
| `HorizonScaleFactor` | 0.5 | — | Horizon derate for haze/scan/FOV |
| `MinBankForOcclusion` | 15.0 | deg | Below this, no bank occlusion |
| `SteepBankThreshold` | 25.0 | deg | Bank at/above uses the larger altitude buffer |
| `SteepBankAltBuffer` | 1000 | ft | Occlusion altitude buffer for steep banks |
| `ModerateBankAltBuffer` | 500 | ft | Occlusion altitude buffer for moderate banks |
| `NoseConeDeg` | 10.0 | deg | Target within nose cone is always visible |
| `TrafficForwardHemisphereDeg` | 90.0 | deg | Traffic scan gate (point target must be in the forward scan) |
| `AirportForwardHemisphereDeg` | 120.0 | deg | Airport field-of-view gate (AIM §8-1-6.c.2 arc + head turn) |
| `SmToNm` | 0.869 | — | Statute-mile → nm for METAR visibility |
| `MetarParser.UnrestrictedVisibilitySm` | 10.0 | SM | Reported vis at/above is right-censored ("10 or more") → no range cap |
| `AirportSizeCap` floor/ceiling | 15 / 25 | nm | Conspicuity cap clamp |
| `SizeCap` intercept/slope | 10 / 5 | nm, nm/nm | Conspicuity cap line in runway extent |

### Wake / detection-range — `WakeTurbulenceData.cs`

| Constant | Value | Units | Meaning |
|---|---|---|---|
| `DetectionThresholdRad` | 0.002618 | rad | 9 arcmin alerted-search first-detection angle |
| `MinRangeNm` / `MaxRangeNm` | 1.5 / 12.0 | nm | Data-quality floor / contrast-limit ceiling |
| Silhouette weights | wingspan 1.0, length² 0.7, tail² 0.3 | — | Variance-weight blend of dimensions |
| CWT bucket ranges | 12.0 (A–D) … 2.7 (I) | nm | Per-CWT fallback detection range |

---

## Footguns & pitfalls

- **Four mechanisms, not one subsystem.** Airborne CA = STARS display only; ground conflict = writes `Ground.SpeedLimit`
  and changes taxi motion; ATPA = advisory cone in the CRC broadcast; visual acquisition = pilot "in sight" reports.
  They share no code and run at four different tick stages. Treating a change to one as applying to another is a trap.

- **Parallel is *not* diverging.** `ConflictAlertDetector.IsDiverging` (`:160`) returns false when separation is constant
  in both dimensions (`atLeastOneGrows` is false). So a same-track pair holding a steady 2 nm in trail **fires** an
  alert. Tightening this to also suppress constant-separation pairs is the classic false-positive over-correction — it
  would also suppress genuine same-altitude converging-to-parallel geometry.

- **Approach-corridor suppression is purely geometric.** `IsInAnyApproachCorridor` (`:202`) consults neither phase, nor
  active approach, nor destination. Any track merely flying through *any* internal airport's 4 nm × 30 nm × glideslope
  box suppresses CA — even an overflight not landing there.

- **ATPA membership is exclusive — one aircraft, one volume.** `Process` Phase 1 assigns each established track to its
  single best-fit volume *before* pairing, so a track is never sequenced in-trail against traffic on another final. Do
  **not** revert to per-volume independent scanning: where adapted volumes overlap (KOAK 30/28R ~18° apart; the OAK 30
  volume's 90° `MaximumHeadingDeviation` + 3 nm half-width reach onto the 28R final) that produced a bogus cross-runway
  cone displaying the *wrong* separation standard (7110.65 §5-9-6 diagonal, not §5-9-5 in-trail). The fit metric must
  keep **both** heading-deviation and cross-track terms — dropping cross-track breaks true parallels (28L/28R share a
  course); the scratchpad runway is only a tie-break, never a hard gate (it's frequently empty for vectored-to-visual).

- **`ExcludedTcpIds` matches on `{Subset}{SectorId}`, not the ULID.** The volume's `ExcludedTcpIds` hold ULIDs, but
  `Track.Owner` carries only `Subset`/`SectorId`. Rather than propagate the ULID onto `TrackOwner`, `IsExcludedByTcp`
  resolves each excluded ULID to its `{Subset}{SectorId}` code (via `BuildTcpCodeMap`) and matches that against the
  owner's code — the same identity model `TrackOwner.IsTcp` uses. Two STARS TCPs sharing a code (a malformed config)
  would be indistinguishable here, the same exposure the monitor/alert cone resolution already has.

- **Altitude units differ per detector.** `AtpaVolumeConfig.Floor`/`Ceiling` are in **hundreds of feet**
  (`AtpaVolumeGeometry.cs:18` divides aircraft altitude by 100 before comparing); CA thresholds are **raw feet**; cloud
  layers are stored **AGL** as `BaseFeetAgl` and must be added to airport elevation to compare against MSL aircraft
  altitude (`FindBindingCeilingAbove` / `FindObstructingLayerBetween`). ATPA `WidthLeft`/`WidthRight` are **feet** while
  `Length` is **nm**. Mixing these up is the easiest mistake in this code.

- **`TryMaintainAirportContact` / `TryMaintainTrafficContact` skip the finding-geometry on purpose.** They run weather
  only: Class A + ceiling (+ the visibility-collapse distance check). Re-adding `BehindOwnship` / `OppositeSideOfRunway` /
  `OccludedByBank` / the type-detection or horizon/conspicuity range caps here regresses the "lost sight of the field at
  threshold crossing" and "lost sight of the traffic when the lead pulls ahead" fixes — the single-point ARP proxy falls
  behind the nose during the flare, and a faster lead legitimately opens the gap beyond its type detection range. The
  only distance that may break maintained contact is the flight-visibility range (`visibility × 0.869 × 1.25` nm when
  reported below 10 SM; the airport datum is min(ARP, assigned threshold) so maintain can never out-reach acquisition) —
  that one is weather. The initial-acquisition path keeps all checks.

- **Ground classification keys on phase *name* strings.** `Classify`/`IsStationaryPhase` (`GroundConflictDetector.cs:325`/`:959`)
  match literals like `"LinedUpAndWaiting"`, `"At Parking"`, and the prefix `"Holding Short"`. Renaming a phase
  silently breaks classification with **no compile error**. See [phases.md](phases.md) for the phase-name contract.
  `IsOnRunway` (runway priority in `ResolveCrossing`/`ComputeClosingLimit`) no longer string-matches: it is
  `RunwayOccupancy.OccupiesSurface(RunwayOccupancy.ClassifyByPhase(ac, runway: null))`
  (`src/Yaat.Sim/Simulation/RunwayOccupancy.cs`) — phase *types* (takeoff, landing, line-up family, runway exit on the
  centerline). No geometry is consulted for phase-driven aircraft; a holding-in-position aircraft is therefore never "on
  the runway" for priority purposes. Live-traffic shadows are the exception: they classify as `MovementState.External`
  (an obstacle, never a subject) and `IsOnRunway` reads `Ground.ExternalOnRunway`, a per-tick flag the detector fills from
  `RunwayOccupancy.ClassifyBest` over the layout airport's runways — see [live-traffic.md](live-traffic.md).

- **`WakeTurbulenceData` must be `Initialize()`'d first.** Before `AircraftCwt.json` is loaded, `GetCwt` returns null and
  both the ATPA matrix and the visual traffic range fall back to `AircraftCategory`. This is a static-singleton
  init-order race — a test that reads it before initialization gets the fallback value (see the documented
  static-singleton-race class of bugs in CLAUDE.md).

- **Weather inputs are runtime-only, not in the aircraft snapshot.** The METAR (cloud layers + visibility) that drives
  visual acquisition comes from `room.Weather` at runtime (`TickProcessor.cs:1197`), not from any per-aircraft snapshot
  field. (ATPA reads no weather at all — its established filter is purely kinematic: airborne, `VerticalSpeed`, and
  heading.) `BankAngle` itself **does** round-trip (`AircraftState.cs:254`), but a
  replayed snapshot without the same room weather reproduces acquisition outcomes differently. See
  [snapshots-and-replay.md](snapshots-and-replay.md) for what does and does not survive a round-trip, and
  [flight-physics.md](flight-physics.md) for how `BankAngle` is produced each tick.

## See also

- [tick-loop.md](tick-loop.md) — per-tick ordering: where ground (pre-physics sub-tick), CA (PostPhysics), and visual
  (PostPhysics + observation step) run, and the broadcast cadence ATPA rides.
- [ground/navigator.md](ground/navigator.md) — the authoritative reference for `GroundConflictDetector`'s resolution
  algorithms (per-`PairKind` handlers, `SameEdgeHeadOn` deadlock-break, converging-merge arbitration) and how the
  navigator honors `Ground.SpeedLimit`.
- [flight-physics.md](flight-physics.md) — `BankAngle`, `GroundSpeed`, `VerticalSpeed`, and `TrueTrack` production (the
  inputs these detectors extrapolate from).
- [phases.md](phases.md) — phase names and the contract that `GroundConflictDetector` string-matches against.
- [crc-display-state.md](crc-display-state.md) — how CA `ActiveConflict`s and ATPA results reach the CRC STARS display.
- [snapshots-and-replay.md](snapshots-and-replay.md) — serialized vs runtime-only state for replay fidelity.
