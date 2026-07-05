# Solo Training Evaluation & Scoring

> Read this before touching `src/Yaat.Sim/Training/SoloTrainingEvaluator.cs`, `AircraftCompletion.cs`,
> `AircraftDebriefCoachingTemplates.cs`, the server callers `TickProcessor.ProcessSoloTrainingEvaluation` /
> `TrainingHub.GetSessionReport`, or anything that emits a scoring finding. This is the rule-to-code map for the
> solo-training scorer: it turns "7110.65 §X-Y-Z" or "the report shows a finding I don't recognize" into the exact
> helper that produced it.

Solo-training scoring grades a student controller's session against FAA separation, advisory, runway, wake, and
approach rules. The whole subsystem is one Yaat.Sim class — `SoloTrainingEvaluator` — plus its data records in
`AircraftCompletion.cs` and the coaching-note templates in `AircraftDebriefCoachingTemplates.cs`. The two callers
that drive it live in yaat-server.

This doc owns the evaluator's rule catalog, event lifecycle, scoring math, and the same-runway / wake tracker. For
the **upstream `ApproachScore`** that feeds the Approaches bucket, see
[approach-and-pattern-geometry.md](approach-and-pattern-geometry.md). For the **pilot TTS side** (pilot
transmissions, readbacks) — orthogonal to scoring — see [solo-training-pilot-speech.md](solo-training-pilot-speech.md).
For the aircraft fields the evaluator reads, see [aircraft-data-model.md](aircraft-data-model.md). For room/tick
plumbing on the server, see [server-rooms-and-hub.md](server-rooms-and-hub.md) and [tick-loop.md](tick-loop.md).

## The two entry points

`SoloTrainingEvaluator` is a per-room object held by the room's `ActiveSim`. It has two public surfaces plus two
state-mutators:

| Method | Caller | Cadence | What it does |
|---|---|---|---|
| `Evaluate(aircraft, elapsed, airspace, serviceContext)` | `TickProcessor.ProcessSoloTrainingEvaluation` | every sim-second | Samples every airborne pair + runway op, upserts findings, deactivates stale ones, returns newly-raised notices. |
| `BuildReport(soloMode, elapsed, approachReport, debriefContext)` | `TrainingHub.GetSessionReport` | on demand (poll) | Tallies the five score buckets, grades, builds coaching notes and per-aircraft debriefs. |
| `RecordControllerCommand(aircraft, command, elapsed, knownAircraft)` | `RoomEngine` (after a successful dispatch) | per accepted command | Captures **proofs** (RTIS / SAFAL / CWT / field-in-sight) that clear findings. |
| `Reset()` | `RecordingManager`, `ScenarioLifecycleService` | scenario load / rewind / restore | Clears all events, proofs, the tracker, and the debrief cache. |

`Evaluate` and `RecordControllerCommand` write to the evaluator's mutable state (`_events`, the proof collections,
the runway tracker); `BuildReport` is a read of that state plus the externally-supplied `ApproachReportData`.

### Per-tick `Evaluate` (`SoloTrainingEvaluator.cs:174`)

For each sim-second the server passes the world snapshot, elapsed seconds, the airspace database, and a
`SoloTrainingServiceContext`:

1. Filter to `IsEligibleAirborneTarget` (`:786`): not on ground, Mode-C transponder, not an unsupported track.
2. **Pairwise loop** over eligible aircraft (`:185`). For each pair, skip if covered by an accepted visual follow
   (`IsCoveredByVisualFollow`, `:838`), else:
   - `SamplePair` (`:902`) resolves a separation requirement and a severity; `Upsert` records it.
   - `SampleAdvisoryPair` (`:965`) emits a traffic-advisory / safety-alert finding when no matching proof exists.
   - If the pair has no separation requirement, `SampleNoMinimaAdvisoryPair` (`:1003`) still flags a missing
     traffic-advisory-service finding when the two are proximate (≤3 NM and ≤1000 ft).
3. **Visual-approach loop** (`:233`): each aircraft on a `VIS*` approach without a field-in-sight proof gets a
   "Visual approach field proof missing" finding (`SampleVisualApproach`, `:1052`).
4. **Safety-alert overuse** (`:249`): a SAFAL proof that never matched a real safety-severity pair becomes a
   "Safety alert overused" finding (`SampleSafetyAlertOveruse`, `:1351`).
5. **Runway / wake** (`:259`): `_sameRunwayTracker.Evaluate` returns runway-separation and wake findings plus the
   live wake contexts; both feed `Upsert`.
6. **Deactivation pass** (`:288`): any tracked event whose `Id` was *not* re-observed this tick is marked
   `IsActive = false`. This is the lifecycle linchpin — see [Footguns](#footguns--pitfalls).

`Evaluate` returns only the **newly-raised** notices (a finding seen for the first time, or one whose severity just
escalated). `TickProcessor` broadcasts each as a terminal entry (`TickProcessor.cs:321`). The full timeline is
reconstructed later by `BuildReport`.

### On-demand `BuildReport` (`SoloTrainingEvaluator.cs:300`)

`TrainingHub.GetSessionReport` (`TrainingHub.cs:659`) calls `ApproachEvaluator.BuildReport` first (to get the
`ApproachReportData`), assembles an `AircraftDebriefContext` from the live snapshot + completed-aircraft registry +
primary airport, then calls `SoloTrainingEvaluator.BuildReport`. That method:

- materializes the timeline (every `TrackedEvent.ToEvent()`), and the active subset sorted by severity then exposure;
- computes the five bucket losses (`ComputeEventLoss` / `ComputeApproachLoss` / `ComputeRecoveryLoss`);
- clamps `score = 100 − Σ bucketLost` to `[0, 100]` and grades it (`GradeFor`, `:1495`);
- builds the top-4 coaching notes (`BuildCoachingNotes`, `:1505`) and the per-aircraft debriefs.

## Data flow & cross-repo wiring

```
RoomEngine.SendCommandAsync (accepted command, solo mode)
        └─ SoloTrainingEvaluator.RecordControllerCommand  → proofs

TickProcessor.ProcessSoloTrainingEvaluation (every tick, solo mode)
        └─ SoloTrainingEvaluator.Evaluate(snapshot, elapsed, AirspaceDatabase.Default, ServiceContext)
                                                                     │ ApproachEvaluator (separate object)
TrainingHub.GetSessionReport (poll)                                  │   ← RecordEstablishment/RecordLanding
        ├─ ApproachEvaluator.BuildReport(elapsed) ──────────────────►│      from TickProcessor approach-score drain
        └─ SoloTrainingEvaluator.BuildReport(soloMode, elapsed, approachReport, debriefContext)
                                                          └─ World.GetSnapshot() + World.GetCompletedAircraft()
```

`AirspaceDatabase.Default` supplies the Class B/C volumes that gate the airspace-specific separation rules.

`SoloTrainingServiceContext` (`:65`) bundles two inputs:

- `InitialContactEligibilityContext` (`Pilot/PilotInitialContactEligibility.cs:6`) — student TCP, position type,
  ARTCC, primary airport, and the initial-contact transfer catalog. Drives `IsStudentServiceRecipient` (`:1090`):
  advisory/visual/wake findings only fire for aircraft that have made initial contact, have not left the student's
  frequency, and are eligible to be the student's traffic.
- `WakeDirectiveCatalog` (`Data/WakeDirectiveCatalog.cs`) — per-ARTCC overrides that can *require* or *suppress* a
  wake advisory / interval (`SampleWakeAdvisoryProofs`, `:1120`).

`AircraftDebriefContext` (`Training/AircraftCompletion.cs:94`) bundles the live `ActiveAircraft`, the
`CompletedAircraft` registry, and the `PrimaryAirportId`. The registry is `SimulationWorld.GetCompletedAircraft`
(`SimulationWorld.cs:136`), a FIFO list of `CompletedAircraftRecord` capped at `CompletedAircraftCapacity = 500`
(`SimulationWorld.cs:25`): when an aircraft is removed, its callsign, type, filed endpoints, spawn/completion times,
and completion reason/detail are preserved so a landed/handed-off/dropped aircraft still appears on the debrief.

## The event model

Three record/class types carry a finding through its lifecycle:

| Type | Where | Role |
|---|---|---|
| `TrainingEventSample` (private record, `:1581`) | produced by each `Sample*`/`Create*` helper | one tick's *observation* of a potential finding |
| `TrackedEvent` (private class, `:1599`) | stored in `_events`, keyed by stable `Id` | the *accumulated* finding (severity-monotonic, growing exposure) |
| `SoloTrainingEvent` (public record, `:29`) | `TrackedEvent.ToEvent()` | the immutable DTO shipped in the report |

`_events` is `Dictionary<string, TrackedEvent>` keyed case-insensitively by a **stable Id**. Stable Ids are what let
the same physical situation upsert into one finding tick after tick instead of spawning a new one each second:

- `MakePairEventId(ruleName, a, b)` (`:1426`) — orders the two callsigns so `A_B` and `B_A` collapse to one key.
- `MakeAdvisoryEventId` / `MakeSafetyAlertEventId` (`:1440`, `:1446`) — directional (recipient → target).
- `MakeRunwayEventId(preceding, succeeding, rule, relation, elapsed)` (`SameRunwaySeparationTracker`, `:3427`) — keyed
  on relation kind, both runway keys, both callsigns, rule reference, and the operation's trigger time.

`Upsert` (`:1411`) is the single write path:

- New Id → `TrackedEvent.FromSample` is added, and the new event is returned (it's a notice).
- Existing Id → `TrackedEvent.Update` raises (never lowers) severity, refreshes the title/description/measurements,
  bumps `LastObservedAtSeconds`, and re-activates it. A notice is returned **only if severity escalated**.

Severity is **monotonic** (`TrackedEvent.Update`, `:1640`): `Severity = max(old, new)`. `ExposureSeconds`
(`ToEvent`, `:1665`) is `LastObserved − Started`, which keeps growing for as long as the finding stays active. Because
the score is exposure-weighted, a situation that ever reached `Safety` keeps bleeding points until it deactivates.

The severity ladder is `Coach < Warning < Safety` (`SoloTrainingEventSeverity`, `:22`).

## The scoring criteria catalog

Each check emits a finding tagged with a `SoloTrainingEventCategory` (`:13`) and a rule reference string. The
category decides which score bucket absorbs the loss. Every `RuleReference` below is a verbatim string literal in the
code.

### Separation (airborne radar)

`ResolveRequirement` (`:688`) picks the applicable rule from the two aircraft's flight rules and the airspace they're
in (or project into over a 30 s / 60 s look-ahead). `SamplePair` (`:902`) then classifies severity: a current
violation is `Safety`; a 30 s-projected violation or being within the `WarningMargin` (1.10×) is `Warning`; a
60 s-projected violation is `Coach`. `Violates` (`:1405`) requires *both* horizontal **and** vertical to be under
minima.

| Criterion (`SeparationRequirement.Name`) | Rule reference | When it applies | Minima |
|---|---|---|---|
| IFR radar separation | `7110.65 §5-5-4, §5-5-5, §4-5-1` | both aircraft IFR (VFR-on-top excluded — see below) | 3.0 NM / 1000 ft |
| Class B target-resolution separation | `7110.65 §7-9-4` | pair in Class B, neither large/turbojet | 0.25 NM / 500 ft |
| Class B large/turbojet separation | `7110.65 §7-9-4` | pair in Class B, one is large (>19,000 lb) or turbojet | 1.5 NM / 500 ft |
| Class C (outer-area) IFR/VFR target-resolution | `7110.65 §7-8-2; 7110.65 §7-8-3; AIM §3-2-4` | Class C (or within 20 NM outer area) and one IFR + one VFR | 0.25 NM / 500 ft |

`IsLargeOrTurbojet` (`:821`) reads MTOW / engine class from `FaaAircraftDatabase`, falling back to
`AircraftCategorization`. The Class C outer-area test (`IsInClassCOuterArea`, `:756`) treats any Class C airport
within 20 NM and below the volume ceiling as outer area.

**VFR-on-top is the VFR party for separation.** `ResolveRequirement` computes
`aVfrForSeparation = FlightPlan.IsVfr || FlightPlan.Altitude.IsVfrOnTop` (not bare `IsVfr`). A VFR-on-top
aircraft is an *IFR* flight everywhere else in the sim (clearances, SIDs, phraseology — AIM §4-4-8), but it is
**never provided IFR separation** (7110.65 §7-3-1 NOTE 2), so for the minima above it is treated as the VFR side:
outside Class B/C an OTP + IFR pair gets no separation minimum (advisory only); in Class B it takes the reduced
Class B VFR standard (0.25/500 or 1.5/500), *not* 3/1000; in Class C an OTP + IFR pair gets target resolution
while OTP + OTP / OTP + VFR fall through to advisory-only (Class C doesn't separate VFR-from-VFR, unlike Class B).
The airborne CA / ERAM STCA detectors are deliberately *not* gated on this — safety alerts are a first-priority
duty to all aircraft (§2-1-6), including VFR-on-top.

### Advisory / Visual

These are **proof-based**: the finding fires when the geometry warrants the service and clears when the controller
issued the matching command. The constructors set the category to `AdvisoryVisual`.

| Criterion | Rule reference | Trigger | Clearing proof |
|---|---|---|---|
| Traffic advisory needed/missing (`CreateAdvisorySample`, `:1275`) | `7110.65 §2-1-21` (Class B → `§7-9-5, §2-1-21`; Class C → `§7-8-2, §2-1-21`, via `AdvisoryRuleReference` `:1346`) | a separation pair below `Safety` severity, recipient is a student-service aircraft | accepted RTIS (`_advisoryProofs`) for that recipient→target |
| Safety alert missing (`CreateSafetyAlertSample`, `:1314`) | `7110.65 §2-1-6` | separation pair at `Safety` severity | accepted SAFAL (`_safetyAlertProofs`) for that recipient→target |
| Traffic advisory service needed (`SampleNoMinimaAdvisoryPair`, `:1003`) | `7110.65 §2-1-21` (Class C → `7110.65 §7-8-2, §2-1-21`) | proximate (≤3 NM and ≤1000 ft) but no separation *minimum* applies | accepted RTIS |
| Visual approach field proof missing (`SampleVisualApproach`, `:1052`) | `7110.65 §7-4-3; AIM §5-4-23` | aircraft on a `VIS*` approach with no following traffic and no field-in-sight | RFIS / pilot field-in-sight (`_fieldAdvisoryProofs`, `HasReportedFieldInSight`) |
| Wake turbulence advisory missing (`CreateWakeAdvisorySample`, `:1253`) | `7110.65 §2-1-20; {source rule}` | a runway op generates a CWT wake context for a student-service recipient | accepted CWT — caution-wake on CTO/CLAND or a `WakeAdvisoryCommand` (`_wakeAdvisoryProofs`) |
| Safety alert overused (`SampleSafetyAlertOveruse`, `:1351`) | `7110.65 §2-1-6` | a SAFAL proof never matched a live safety-severity pair | n/a (it *is* the misuse finding) |
| Traffic advisory imprecise (`SampleImpreciseAdvisories`) | `7110.65 §2-1-21` | an accepted structured RTIS resolved its target but graded `Imprecise` (within tolerance, beyond the "spot-on" bands) | n/a — `Coach` severity, it *is* the accuracy note (the recipient→target advisory itself is still proven) |

`RecordControllerCommand` (`:97`) is the proof capture. It walks the immediately-applied commands of an accepted
compound and:

- `ContactCommand` / `FrequencyChangeApprovedCommand` → sets `HasLeftStudentFrequency` (aircraft stops being a
  student-service recipient).
- `ReportFieldAdvisoryCommand` → adds to `_fieldAdvisoryProofs`.
- `ReportTrafficAdvisoryCommand` → resolves the structured target via `TrafficAdvisoryMatcher` (best-candidate
  within tolerance, not exact match) and adds a recipient→target key to `_advisoryProofs`. If the match graded
  `Imprecise`, also appends an `ImpreciseAdvisoryProof` that `SampleImpreciseAdvisories` turns into a one-shot
  `Coach` "Traffic advisory imprecise" note. The matching tolerances live in `TrafficAdvisoryMatcher` (clock
  ±2 sectors / ±4 when the recipient is maneuvering, distance ±2 NM, direction ±1 octant, altitude ±500 ft;
  altitude optional). See FAA 7110.65 §2-1-21 and AIM §4-1-15 / FIG 4-1-2 for why an exact clock is wrong.
- `ReportTrafficRelativeCommand` / `ReportTrafficPatternCommand` / `ReportTrafficLandmarkCommand` (the VFR-style
  descriptive forms) → resolve through the corresponding `TrafficAdvisoryMatcher.Resolve{Relative,Pattern,Landmark}TrafficTarget`
  resolvers and feed the **same** `RecordResolvedAdvisory` path as the clock form — recipient→target proof plus the
  graded `Imprecise` Coach note. Relative position uses an octant gate (±1 octant / ±45°, ±2 NM); pattern matches the
  candidate's classified leg/side/distance (±2 NM); landmark uses 2 NM proximity to the resolved fix. The relative-octant
  form is an intentional informal VFR-tower convention (not codified in 7110.65, which defines only clock and cardinal
  azimuth — AIM §4-1-15) and must not be "corrected" to clock-only; the pattern (§3-10-4) and landmark (§2-1-21.b.1)
  forms are grounded in published phraseology.
- `SafetyAlertCommand` → appends a `SafetyAlertProof` (tracked for overuse).
- `WakeAdvisoryCommand` / `ClearedForTakeoffCommand{CautionWakeTurbulence}` / `ClearedToLandCommand{CautionWakeTurbulence}`
  → appends a `WakeAdvisoryProof`.

### Runway / Wake — same-runway separation tracker

`SameRunwaySeparationTracker` (private nested class, `:1678`) is the densest piece. It is a stateful per-tick machine
holding `_previousStates`, `_lastOperationByRunway`, `_recentOperations`, and `_activeViolations`.

**Operation detection** (`DetectOperation`, `:1838`). Per aircraft it builds an `AircraftRunwayState` (`:3452`) from
the assigned runway and current phase, computing `AlongThresholdFt` (signed distance along the runway centerline). A
**Departure** op is detected when an aircraft starts its takeoff roll (`TakeoffPhase`) on a runway; a **Landing** op
when an arrival on `FinalApproachPhase`/`LandingPhase` crosses the threshold (`AlongThresholdFt` goes ≥ 0). First
observation seeds an op from the current phase (`TrySeedOperation`, `:1875`).

Note the **two `OperationKind` enums** — the tracker's private `OperationKind { Departure, Landing }` (`:3524`) is the
runway state-machine kind and is unrelated to the public `Training.OperationKind { Unknown, Departure, Arrival,
Transit }` (`AircraftCompletion.cs:27`) used for the debrief UX label.

**Runway relation** (`TryResolveRunwayRelation`, `:3080`) classifies a pair of runways into `RunwayRelationKind`
(`:3540`):

| Relation | Detected by | Example |
|---|---|---|
| `SameActive` | identical runway key | both on 28R |
| `OppositeDirectionSamePavement` | same physical pavement (`Id.Overlaps`) | 28L vs 10R |
| `Intersecting` | `RunwayIntersectionCalculator.FindIntersection` | crossing runways |
| `ProjectedConverging` | `FindProjectedFlightPathIntersection` within `ProjectedConvergingRunwayLimitNm = 1.0` | converging departure paths |

**The 4×4 op-pair matrix.** For each new op, `CreateRunwaySeparationViolations` (`:1890`) checks the preceding op on
the same runway plus the recent ops on related runways, and `TryCreateViolation` (`:1943`) dispatches on
`(succeeding.Kind, preceding.Kind)` and the relation. Each cell carries its own 7110.65 reference:

| | Preceding Departure | Preceding Landing |
|---|---|---|
| **Succeeding Departure** (same runway) | Dep behind dep — `§3-9-6(a)`, spacing `RequiredDepartureBehindDepartureFt` | Dep behind landing — `§3-9-6(b)`, preceding clear of runway |
| **Succeeding Landing** (same runway) | Arr behind dep — `§3-10-3(a)(2)`, `RequiredArrivalBehindDepartureFt` | Arr behind landing — `§3-10-3(a)(1)`, `RequiredLandingBehindLandingExceptionFt` |

The same four cells are repeated for `OppositeDirectionSamePavement` (`§3-9-6` / `§3-10-3`, `TryCreateOppositeDirectionViolation`
`:2410`), `Intersecting` (`§3-9-8` / `§3-10-4`, `TryCreateIntersectingRunwayViolation` `:2464`), and
`ProjectedConverging` (`§3-9-9` / `§3-10-4`, `TryCreateConvergingRunwayViolation` `:2520`). Intersecting/converging
cases clear when the preceding aircraft passed the computed intersection (`HasPassedIntersection`, `:3256`).

**SRS required-spacing tables** (Single Runway Separation, distance-based, `SrsCategory I/II/III`):

| Rule | I behind I | any II involved | any III involved |
|---|---|---|---|
| `RequiredDepartureBehindDepartureFt` (`:3272`) | 3000 ft | 4500 ft | 6000 ft |
| `RequiredArrivalBehindDepartureFt` (`:3287`) | 3000 ft | 4500 ft (succeeding II) | 6000 ft |
| `RequiredLandingBehindLandingExceptionFt` (`:3297`) | 3000 ft | 4500 ft (succeeding II) | `null` (no exception — must be clear) |

`ResolveSrsCategory` (`:3307`) reads the FAA `Srs` field, else derives I/II from MTOW ≤ 12,500 lb + prop + engine
count (helicopters → I), defaulting to III.

**Wake violations** are time-or-distance based and use CWT categories `A`–`I` (`CwtCategory`, `:3555`, ordinal
`A=1…I=9`):

- Departure wake interval (`TryResolveDepartureWakeRequirement`, `:2848`) — `§3-9-6(f)/(g)` same/parallel, `§3-9-7(a)`
  intersection-departure, in seconds (120/180/240). Satisfied by elapsed time **or** the directly-behind CWT distance
  (`RequiredDirectlyBehindWakeNm`, `:3149`).
- Approach wake spacing (`CreateApproachWakeViolations`, `:2089`) — `§5-5-4(h)`, distance in NM from
  `RequiredApproachWakeNm` (`:3186`), for same-runway or close-parallel (<2,500 ft) arrivals.
- Projected-flight-path wake (`TryResolveProjectedFlightPathWakeRequirement`, `:2940`) — `§3-9-8(a)(4)` /
  `§3-9-9(a)(3)` / `§3-10-4(a)(3)`, seconds from `RequiredProjectedFlightPathWakeSeconds` (`:2977`).

`ResolveCwtCategory` (`:3352`) reads `WakeTurbulenceData.GetCwt` then `FaaAircraftDatabase`'s `Cwt`, falling back to a
category from `AircraftCategorization`.

Per-ARTCC `WakeDirectiveCatalog` rules can `SuppressWakeAdvisory`, `RequireWakeAdvisory`, or `SuppressWakeInterval`
(`SampleWakeAdvisoryProofs` `:1120`, `IsWakeIntervalSuppressed` `:1793`). The wake-advisory finding is also overuse-
aware: when a recipient has exactly one candidate context and a matching CWT proof, the finding is suppressed
(`:1206`).

### Approaches — the `ApproachEvaluator` boundary

The Approaches bucket is the **only** bucket whose data is sourced outside `SoloTrainingEvaluator`.
`ApproachEvaluator` (`src/Yaat.Sim/Phases/ApproachEvaluator.cs`) is a separate per-room object. The server drains
`ApproachScore` objects (intercept angle/distance legality, glide-slope deviation, forced flag) from the world's
per-tick outbox (`DrainAllApproachScores` in `ProcessApproachScores`) and calls `RecordLanding` (`TickProcessor.cs:1533`)
or `RecordEstablishment` (`:1539`). It grades each approach A–F by demerits (`ComputeGrade`, `ApproachEvaluator.cs:100`)
and produces an `ApproachReportData`.

`SoloTrainingEvaluator.BuildReport` takes that `ApproachReportData` as a parameter and turns per-approach grades into a
loss via `ComputeApproachLoss` (`:1475`): `F → 8`, `D → 5`, `C → 2`, else `0`. The evaluator never grades approaches
itself. Where `ApproachScore` originates (intercept geometry, glide-slope) is owned by
[approach-and-pattern-geometry.md](approach-and-pattern-geometry.md).

## Scoring & grading — the five buckets

`BuildReport` (`:316`) sums losses into five buckets, each clamped at its cap:

| Bucket | Cap | Loss source |
|---|---|---|
| Separation | 45 | `ComputeEventLoss(Separation)` |
| Runway / Wake | 30 | `ComputeEventLoss(RunwayWake)` |
| Advisory / Visual | 15 | `ComputeEventLoss(AdvisoryVisual)` |
| Approaches | 10 | `ComputeApproachLoss(approachReport)` |
| Recovery | 10 | `ComputeRecoveryLoss(timeline)` |

`ComputeEventLoss` (`:1452`) is a base + exposure formula per finding in the category:

| Severity | Base | Exposure add-on |
|---|---|---|
| Safety | 12 | `min(10, ⌊exposure / 5⌋)` |
| Warning | 6 | `min(5, ⌊exposure / 10⌋)` |
| Coach | 2 | 0 |

`ComputeRecoveryLoss` (`:1492`) is `2 × (count of still-active Safety findings)`. `score = clamp(100 − Σ lost, 0, 100)`,
graded by `GradeFor` (`:1495`): `≥90 A`, `≥80 B`, `≥70 C`, `≥60 D`, else `F`.

## Per-aircraft debrief & the FNV cache

`BuildAircraftDebriefs` (`:344`) produces one `AircraftDebriefData` (`AircraftCompletion.cs:67`) row per active and
completed aircraft. Findings are grouped by participating callsign from `SoloTrainingEvent.Callsigns`, so a shared
separation finding shows in **both** aircraft's debrief blocks (shared blame). Each row tallies findings by category
and severity, picks the top finding (highest severity, then longest exposure — `CompareForTop`, `:618`), and renders a
one-line coaching note via `AircraftDebriefCoachingTemplates.Build`.

Completed-aircraft handling (`:388`): a callsign that is also live is skipped (the live entry wins); within the
completed list, only the latest `CompletedAtSeconds` record per callsign is kept (handles delete-then-respawn).

`ClassifyOperation` (`:628`) maps filed departure/destination against the primary airport into the public
`OperationKind` (`Departure` if dep matches, `Arrival` if dest matches, `Transit`/`Unknown` otherwise), tolerating the
optional `K`/`P` ICAO prefix (`AirportIdMatches`, `:658`).

**The FNV debrief cache.** `BuildReport` runs on a server poll cadence (≈2–5 s) while the inputs usually haven't
changed, so the debrief result is memoized behind a 64-bit FNV-1a structural hash. `ComputeDebriefInputsHash`
(`:424`) folds in, for every active aircraft, its callsign / cid / `AircraftType` / `FlightPlan.Departure` /
`FlightPlan.Destination` / `CompletionReason` / `CompletionDetail` / spawn & completion times; for every completed
record its callsign / completion time / reason; the primary airport; and for every timeline event its `Id` /
`Severity` / `IsActive`. If the hash matches the previous call, `_lastDebriefs` is returned unchanged. The invariant:
**every field `BuildDebriefRow` reads must be in the hash**, or a changed-but-unhashed field returns a stale cached
debrief.

## Reset semantics

`Reset()` (`:676`) clears `_events`, all four proof collections, the same-runway tracker (`Reset`, `:1785`), and the
debrief cache. The server calls it on scenario load, rewind, and snapshot restore from `RecordingManager` and
`ScenarioLifecycleService`. The tracker carries the most per-tick state, so any new tracker field that isn't cleared
in `SameRunwaySeparationTracker.Reset` leaks across scenarios.

## Adding a new scoring criterion

1. Pick a `SoloTrainingEventCategory` (decides the bucket) and decide the severity logic.
2. Emit a `TrainingEventSample` from a new `Sample*` helper with a **stable `Id`** (reuse a `Make*EventId` builder so
   the same situation collapses to one finding).
3. Register the Id in `observedThisTick` in `Evaluate` every tick the finding is valid, or the end-of-`Evaluate`
   deactivation pass will flicker it active→inactive.
4. If the finding clears on a controller action rather than on geometry, add the proof side to
   `RecordControllerCommand` and a `Has*Proof` check.
5. Add the `RuleReference` string and confirm the bucket math (`ComputeEventLoss` already handles any category).
6. Add a TDD test in `tests/Yaat.Sim.Tests/SoloTrainingEvaluatorTests.cs`, and get `aviation-sim-expert` sign-off —
   every rule here is aviation-review-sensitive.

## Footguns & pitfalls

- **Findings clear on proof, not on geometry.** Separation/advisory/wake findings do not clear because spacing
  recovered — they clear because the controller issued the matching command (RTIS / SAFAL / CWT / field-in-sight),
  captured by `RecordControllerCommand` into `_advisoryProofs` / `_safetyAlertProofs` / `_wakeAdvisoryProofs` /
  `_fieldAdvisoryProofs`. Edit one side without the other and a finding either never fires or never clears.
- **Stable-Id + `observedThisTick` lifecycle.** An event must be re-sampled (its Id re-added to `observedThisTick`)
  every tick it remains valid. A new criterion that forgets to register its Id flickers active→inactive every tick.
- **`Upsert` is severity-monotonic.** `TrackedEvent.Update` only raises severity, and `ExposureSeconds` keeps growing
  while the finding is active. Because the loss is exposure-weighted, a once-`Safety` finding keeps bleeding points
  until it deactivates.
- **The FNV debrief cache trap.** `ComputeDebriefInputsHash` must hash *every* field `BuildDebriefRow` reads (type,
  filed endpoints, completion reason/detail, plus each timeline event's Id/severity/active flag). Add a field to the
  debrief row without adding it to the hash and `BuildReport` returns a stale cached list.
- **Two `OperationKind` enums.** The public `Training.OperationKind` (`Unknown/Departure/Arrival/Transit`, a UX label
  from the filed plan) is unrelated to the tracker's private `OperationKind` (`Departure/Landing`, the runway state
  machine) despite the shared name.
- **`Reset()` must clear new tracker state.** `SameRunwaySeparationTracker` is a stateful per-tick machine; a new
  field not cleared in its `Reset` leaks across scenario loads / restores.
- **SRS/CWT category resolution falls through three sources** (`FaaAircraftDatabase` → `WakeTurbulenceData` →
  `AircraftCategorization`). The static-singleton race in CLAUDE.md applies: a category test that reads these before
  `TestVnasData.EnsureInitialized()` can get the default-fallback category. Pin the singleton in the test class
  constructor.
- **The two callers live in yaat-server.** `Evaluate` is called from `TickProcessor.ProcessSoloTrainingEvaluation` and
  `BuildReport` from `TrainingHub.GetSessionReport`. A signature change to either breaks the sibling repo and is only
  caught by `tools/test-all.ps1`, not bare `dotnet test` from yaat.
- **Same-pavement opposite-direction ≠ intersecting.** "28L vs 10R" is `OppositeDirectionSamePavement` (resolved via
  `Id.Overlaps`), a distinct relation kind from `Intersecting`/`ProjectedConverging` (resolved via
  `RunwayIntersectionCalculator`). Easy to conflate.
- **The Recovery bucket double-counts by design.** A still-active `Safety` finding already lost points in its own
  category *and* adds 2 to Recovery (`ComputeRecoveryLoss`). This is intentional emphasis on unresolved safety
  situations, not a bug to "fix."
