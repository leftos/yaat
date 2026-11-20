# Yaat.Sim core file map

Per-file map of the root-level files of `src/Yaat.Sim/` and its small folders with no subsystem doc of their own (`Asdex/`, `Situation/`, `Training/`, `Logging/`, `Diagnostics/`): aircraft state and its satellites, flight physics, the conflict and ground detectors, visual detection.

Aircraft performance, live traffic, weather and wind, and the landing files are in their subsystem docs' File maps ([aircraft-performance.md](aircraft-performance.md), [live-traffic.md](live-traffic.md), [weather-and-wind.md](weather-and-wind.md), [landing-and-runway-exit.md](landing-and-runway-exit.md)). The Task Index and the layers are in [architecture.md](architecture.md); how the aircraft model fits together is [aircraft-data-model.md](aircraft-data-model.md).

## File map

One entry per file; paths are relative to `src/Yaat.Sim/`.

```
# Core
AircraftState.cs               # Mutable aircraft entity. Identity + kinematics flat at top; cohesive
                               # state grouped into sub-objects (FlightPlan, Transponder, Ground, Track,
                               # Stars, Eram, Approach, Procedure, Pattern, Clearance, HoldAnnotation,
                               # Ghost, DataBlock, Voice). DataBlock = STARS Track Reposition (TRK RPOS)
                               # surveillance/datablock split: Parked emits a second wire StarsTrackDto.
                               # Each sub-object owns its own ToSnapshot/FromSnapshot pair
                               # with a matching DTO under Simulation/Snapshots/.
                               # DeclinationCachePosition (LatLon?): null = "not cached"; [JsonIgnore] here, carried by AircraftSnapshotDto.
                               # Ground.Layout is [JsonIgnore]; Ground.LayoutAirportId preserves the
                               # reference so archive restore can reattach.
                               # PendingObservations: ephemeral pilot-side "watch for condition" state (not persisted in snapshots)
                               # WindSpeedKts: computed wind magnitude (sqrt of N²+E² components) used for pattern flyability floor
                               # LiveTraffic (AircraftLiveTraffic?): non-null ⇔ IsShadow — a real aircraft mirrored from a feed,
                               # driven by LiveTraffic/LiveTrafficKinematics instead of FlightPhysics. See live-traffic.md.
                               # AppliedTurnRateDegPerSec (double?): the signed turn rate under TurnModel.Ramp, null under Instant; BankAngle derives from it; carried by AircraftSnapshotDto (omitted while null, no schema bump)
                               # AssumedFromLiveTraffic (bool, snapshotted, default false): set by LiveTrafficAssumer.Assume as LiveTraffic is cleared —
                               # the marker UNASSUME requires (a scenario aircraft with the same callsign is not restorable)
                               # FOOTGUN: changes here must be mirrored in AircraftSnapshotDto + SnapshotSchemaMigrator
AltitudeRestrictionInForce.cs  # Whether a route altitude restriction binds the aircraft now, by its `CifpAltitudeRestriction.Source`: a controller's crossing always, a pattern-phase point while that phase flies, a published approach altitude once an approach clearance is active or a deferred one's connecting fix is behind the aircraft, a published SID/STAR altitude only under a climb/descend via (or a SID departure with no altitude at all; `ProcedureAltitudesInForce` is the SID-departure reader, also gating `DepartureProcedurePhase.ApplyLegAltitudeCap`); read by the step planners and `ApplyFixConstraints`; `IsShownLatent` adds the climb via's "except maintain" top and the descend via's bottom for the nav-route overlay
ControlTargets.cs              # Autopilot targets: heading, altitude, speed (IAS), NavigationRoute
                               # DesiredVerticalRate (phase/instructor) vs PlannedVerticalRate (FlightPhysics step-climb/descent
                               # planners, recomputed and cleared every tick): DesiredVerticalRate wins when both are set.
                               # PlannedVerticalRate is deliberately absent from ControlTargetsDto — re-derived on the first
                               # tick after a restore, so it's an exception to the "mirror in ControlTargetsDto" footgun below.
TaxiEdgeTrail.cs               # The taxi edges a ground aircraft drove, oldest first (TaxiTrailEdge: NodeA, NodeB, LengthFt, EntryNodeId; Resolve(layout)),
                               # capped near 3,000 ft (CapFt), written by SimulationEngine.TickTaxiEdgeTrails once per second while it moves; 1 Hz samples,
                               # so consecutive edges need not share a node; cleared when airborne or warped. EntryNodeId is the end node the aircraft
                               # entered the edge from, required: Record throws when it is not an end of the edge, and EntryNodeOf picks it (the node
                               # shared with the newest edge, else the end behind the track). Read by GroundConflictDetector's lead-chain closing rules.
AircraftGroundOps.cs           # Ground sub-object of AircraftState (AircraftState.Ground; snapshot AircraftGroundOpsDto): layout reference ([JsonIgnore] Layout + LayoutAirportId), assigned taxi route, TaxiEdgeTrail ([JsonIgnore]; snapshot TaxiEdgeTrail list, null when empty), parking spot and current taxiway, hold directive, auto-delete flags, expedite/commanded taxi speed, ground-conflict speed limit and auto-yield, runway queue position, pushback/towbar headings and ForcedTowIgnoresParked,
                               # the solo initial call-up (InitialCallup plan, InitialCallupDecisionProcessed, SpawnTaxiway, PushedBackFrom, PushEndSpot, PresetTaxiStop, VfrDepartureDirection), taxi-in call (AwaitingTaxiInCall, ReleasedToGround),
                               # hold-for-release (HeldForRelease, ReleasedForDeparture, ReleasedAtSeconds, ReleasedAtSpawnGate) and the CFR window (ReleaseWindowStartUtc/EndUtc)
AircraftPattern.cs             # Aircraft pattern state; PendingLandingClearance carries PatternRunwayId / PatternAltitudeFt for runway-change clearance routing; PendingRunwayCrossing / WarnedRunwayCrossings (NoteRunwayCrossing, MarkRunwayCrossingWarned) track the once-per-runway-and-side "crosses runway" warning
ArmedReportsDto.cs             # ArmedReportLegs flags (Crosswind 1, Downwind 2, Midfield 4, Base 8, Final 16) and the wire record ArmedReportsDto(Legs, FinalMiles, Fix, ReachingAltitudeFt, LeavingAltitudeFt) with From(AircraftApproachState): a read-only projection of the REPORT arms (null when none armed); the approach state stays their home, so it has no snapshot field
NavRouteFixDto.cs              # Wire record (Name/Lat/Lon/RestrictionLines/RestrictionTokens) for the client "Show nav route" overlay; carries server positions + pre-formatted crossing-restriction labels (`RestrictionLines` as text, `RestrictionTokens` as per-line `NavRouteRestrictionToken`s that mark a latent altitude or published speed; null tokens from an older server). Empty Name = synthetic arc vertex. Referenced by both AircraftStateDto (server) and AircraftDto (client)
NavRouteRestrictionToken.cs    # Wire record (Text/Latent) for one token of a crossing-restriction label line on the "Show nav route" overlay; Latent = a published restriction on the route but not in force now, which the client draws dimmed
NavRouteSpeedLatency.cs       # `LatentAlongRoute`: walks an aircraft's route fix by fix and marks each published SID/STAR speed latent when an ATC speed or DELETE SPEED RESTRICTIONS is in force there (judge `FlightPhysics.PublishedSpeedApplies`, shared with physics), past crossings that revert or resume; feeds the speed tokens' `Latent` and the server nav-route fingerprint. Does not read queued `AT fix` speed blocks
NavRouteShapeDto.cs           # Wire record (Kind/Points/Labels/LabelLat/LabelLon: the label anchor) for active-procedure geometry on the "Show nav route" overlay: hold racetracks (solid, or dashed while queued), orbit circles, procedure turns, open-ended SID coded-leg vectors and the military route corridor — paths the flat NavRouteFixDto route can't express. NavRouteShapeKind styles the shape (its newer kinds, the route-end and final-approach courses, heading vector and queued DEPART/JRAD, are all produced by the projector; looks in `Yaat.Client/Views/NavRouteShapeLook.cs`)
NavRouteOverlayProjector.cs   # Projects an aircraft's active phase (HoldingPatternPhase/ProcedureTurnPhase/DepartureProcedurePhase/MilitaryRoutePhase) into NavRouteShapeDto shapes (racetrack, PT barb, chained coded-leg vectors w/ restriction labels, MTR corridor polygon), plus queued holds from the command queue (dashed PendingHoldRacetrack) and VFR/airspace-boundary orbits (OrbitCircle), a `RouteEndCourse` line past the last fix when `FlightPhysics.ClassifyRouteEnd` says TerminalCourse, and a `HeadingVector` for a route-less airborne aircraft in InitialClimbPhase or no phase, a solid `FinalApproachCourse` for a cleared or pending approach and a dashed `ExpectedFinalApproachCourse` for an EA (none once `AirborneFollowHelper.HasEndedTheApproach`), and dashed `PendingDepartHeading` / `PendingJoinRadial` lines for queued DEPART and JRADO/JRADI (`RouteEndCourse` is withheld when a queued hold fires at the last fix); `BuildPendingShapeKey` feeds the server nav-route fingerprint. Pure geometry; fed into AircraftStateDto.NavRouteShapes by DtoConverter
ProcedureLeg.cs                # Typed ARINC-424 procedure leg (path terminator + course/altitude/turn, + DME/along-track distance or radial termination for CD/VD/FD/FC/CR/VR, + the CR/VR radial's navaid declination) flown by DepartureProcedurePhase; built by ProcedureLegResolver
                               # NavigationTarget: Position (LatLon) + optional AltitudeRestriction + SpeedRestriction (for SID/STAR via mode); IsSyntheticArcName(name) flags ARCnn arc-densification vertices
                               # TargetMach: when set, UpdateSpeed recomputes equivalent IAS each tick (Mach hold)
LatLon.cs                      # Readonly record struct: public LatLon(double Lat, double Lon). The canonical coordinate type
LatLonBounds.cs                # Internal: axis-aligned lat/lon bbox pre-filter shared by AirspaceVolume + MvaSector (O(1) reject before ray-cast)
                               # across Yaat.Sim / Yaat.Client / yaat-server. Field names match CRC Point DTO. No implicit tuple conversion
                               # (forces explicit `new LatLon(lat, lon)` at external-JSON boundaries so argument swaps don't slip through)
Callsign.cs                    # Static IsValid(string?): regex ^[A-Z0-9\-]{1,7}$. Boundary check used by STARS DA/VP/FP creation
                               # to reject typos like "*T <fix>" before they create stray flight plans.
PlannedAltitude.cs             # Value type for the filed flight-plan altitude (notation axis): single / block / VFR /
                               # VFR-on-top / above, in feet. Mirrors vNAS common/ParsedAltitude minus RawValue. Distinct from
                               # ControlTargets.AssignedAltitude (current ATC clearance) and FlightRules (IFR/VFR rules axis).
                               # UntilFix(feet, fix, afterFixFeet) builds ERAM's fix-qualified form (AltitudeFix / AfterFixFeet,
                               # init-only; data only — AircraftFlightPlan's latch picks the altitude in effect for Field B and QF).
AircraftFlightPlan.cs          # Flight plan sub-object of AircraftState; FormatSpeedField(knots, mach, classified) writes the filed speed as ERAM does (SC / Mddd / knots / empty), shared by the client displays and the server's ERAM speed readout
FlightPlanAltitude.cs          # Parser + formatter for the CRC altitude grammar used in FP forms and STARS DA/VP:
                               # `VFR` (rules-only), `VFR/045` / `OTP/120` (rules + altitude), `045` (IFR + altitude), blank.
                               # Parse → (Rules, PlannedAltitude); Format(PlannedAltitude) → text (incl. block NNNBNNN). OTP is
                               # VFR rules + VFR-on-top notation. FromRulesAndFeet builds a PlannedAltitude from rules + a plain int.
                               # Also `A170` (above) and `170/SJC/110` (fix-qualified; ParseFixQualified is the one grammar ERAM
                               # AM ALT uses too, through Data/EramFixResolver).
Data/EramFixResolver.cs        # ERAM fix forms shared by the Sim and the server: fix name, FRD and lat/long checks (IsFixName,
                               # IsFrdForm, ParseLatLong, IsAltitudeFixForm), ParseLocation (a field 68 location → position) and
                               # Resolve(fix) → position for the altitude-fix latch.
FlightPlanParking.cs           # Reads where an arrival parks from its flight-plan remarks (`/V/ GATE F14, 28R`, `/V/ PARKING SIGNATURE, 28R`, `PARKING AT CUTTER`) into a ParkingRemark (area and/or stand). A VATSIM scenario convention (no FAA field). Read by Phases/ArrivalExitSide.cs and by GroundCommandHandler for `@GATE`.
FlightPlanVoice.cs             # Couples FP remarks ↔ voice type (AircraftVoice.Type: 1=Full/2=ReceiveOnly/3=TextOnly). Remarks are
                               # canonical: ParseVoiceType reads a /v//r//t/ marker (full implied when absent), ApplyVoiceMarker writes it.
                               # A VATSIM convention (no FAA field). Driven by ERAM QB /v|/r|/t and the SetVoiceType hub; derived on amend + load.
RouteSplicer.cs                # ERAM AM RTE route-splice grammar (docs/crc/eram.md Table 8): join/resume/replace over [dep]+enroute+[dest]
                               # with a dotted anchor list, plus [ / ] (↑/↓) departure/destination swap. Splice semantics only (no SID/STAR expansion).
FlightPhysics.cs               # Static 8-step Update: navigation→descentPlan→climbPlan→speedPlan→heading→altitude→speed→position→queue; PhysicsTickOptions carries the per-tick scenario inputs (solo/RPO routing flags, magnetic-model day, TurnModel); also declares the TurnModel enum (Instant | Ramp); `ClassifyRouteEnd` (internal, returns `RouteEndLateral`) is the route-end branch test shared by `ApplyRouteEndLateral` and `NavRouteOverlayProjector`
                               # UpdateSpeedPlanning: proactive speed look-ahead for procedure fixes (mirrors descent/climb planning)
                               # Auto speed schedule: skipped when ActiveApproach or ManagesSpeed (pattern phases)
                               # 14 CFR 91.117: 250 KIAS cap below 10,000 ft (and, in a descent to a cleared altitude below it, a lead above 10,000 ft; UpdateAltitude levels at 10,000 ft as a backstop) in UpdateSpeed() and ApplyFixConstraints(); public
                               #   RegulatorySpeedLimit(aircraft) adds the 200 KIAS Class B shelf cap. A TargetSpeed above the limit
                               #   stays standing at the cap (ArriveAtGoal) and is taken up when the cap lifts; an aircraft with no
                               #   target over the limit slows to it (BoundsCorrectionTarget, whose limit arm returns an exact
                               #   assignment standing behind the cap, so it is flown again when the cap lifts); a chained SPD completes on
                               #   IsSpeedAssignmentHeldAtSpeedLimit
                               # Type speed limit: EffectiveSpeedLimit = min(RegulatorySpeedLimit, EnvelopeSpeedLimit (Vmo/Mmo via
                               #   AircraftPerformance.MaxOperatingIasKts)); ControlTargets.SpeedLimitsLifted (SPDN) lifts both
                               # HoldMaximumForwardSpeed (SMAX, ControlTargets.SpeedMaxHeld): each tick writes Vmo at the present altitude (banded types: SpeedMaxAheadKts leads into a lower band) - 10 kt or Mmo - 0.02 (0.9 Vne, helicopter; SpeedMaxKts) as the speed goal;
                               #   SpeedLimitWithin drops the 91.117 cap but keeps Vmo/Mmo
                               # UpdateSpeed: physics is the sole integrator of ground speed. SpeedChangeRate splits airborne
                               #   AircraftPerformance accel/decel from on-ground CategoryPerformance.TaxiAccelRate/TaxiDecelRate,
                               #   each overridable per aircraft via ControlTargets.DesiredAccelRate/DesiredDecelRate (a tug move
                               #   publishes the towbar rate while it has the aircraft); GroundNavigator only publishes
                               #   TargetSpeed/DesiredDecelRate for physics to close (roll phases — TakeoffPhase,
                               #   RunwayHoldingPhase, etc. — still write IndicatedAirspeed directly by design)
                               # Wind physics: TAS = IasToTas(IAS, alt); GS/Track derived from TAS + wind vector; WCA applied to nav
                               # ApplyFixConstraints: SID/STAR via-mode constraint enforcement at waypoints (ApplyViaClearanceFixConstraints leaves a slowdown to the planner); SequencingThreshold: turn anticipation / NavArrivalNm before a fix; SequencingDistanceNm: route distance to that point, where the planners aim
                               # Bank angle: computed in UpdateHeading from atan(TAS × turnRate × coeff); sign follows turn direction. Under TurnModel.Ramp the rate is AircraftState.AppliedTurnRateDegPerSec, rolled in and out (UsesTurnRamp, ApplyRampedTurn); FlyByTurn/FlyByTurnFor add the roll-in lead to fly-by anticipation and arc steering
                               # Expedite: IsExpediting → direction-split rate (climb ×1.15 / descent ×2.0, per-category caps+floor); Mach hold: TargetMach → recompute IAS each tick
TurnShape.cs                   # Static: the shape of an airborne turn shared by every turn-start decision — RadiusNm, LeadNm (the roll-in lead under TurnModel.Ramp, 0 under Instant), InterceptLeadNm; TurnLeadInput record (ground speed, signed turn, rate, category, model, applied rate)
GeoMath.cs                     # Static: DistanceNm (haversine), BearingTo, TurnHeadingToward, GenerateArcPoints (RF/AF), PointInRing (even-odd ray-cast)
                               # Each primary function has scalar (double, double, double, double) and LatLon (LatLon, LatLon) overloads
                               # FootOfPerpendicular returns (LatLon Foot, double AlongNm, bool Clamped)
ClientKind.cs                  # Static constants: Main / VStrips / VTdls identify which YAAT app a SignalR client is running; TowerCab marks a Tower Cab 3D viewer listed in Room Members from the CRC hub. DisplayName() labels one on its own (Room Members badge); DisplaySuffix() appends to a sentence.
ClientVersions.cs              # Compares client version strings for the server's version gate. Numeric major.minor.patch only — the -beta suffix carries no ordering. Fails open on anything unparseable.
                               # Sent on CreateRoom/JoinRoom; stored in RoomMember.Kind; DisplaySuffix appends e.g.
                               # " (Flight Strips)" / " (vTDLS)" to terminal-broadcast verbs ("joined the room (vTDLS)").
SimLog.cs                      # Static logger factory for Yaat.Sim; Initialize(ILoggerFactory) at startup
Logging/SessionLogAnonymizer.cs # LogParticipant (CID, real name, initials, plus OtherRealNames/OtherInitials for a person seen under several) and the anonymizer that swaps each for a stable pseudonym (A0, A1, … B0; a person's index in the first-seen participant list), whole-word so CIDs inside TCP codes survive; a "(from XX)" sender the list does not hold becomes ??; shared by yaat-server's GetSessionServerLog and the client's ClientLogScrubber so both bundle logs use the same pseudonyms; Anonymize takes a keep set (CommandVerbs: every command name and alias) so initials that spell a command never wipe command text, while a listed sender's initials in a "(from XX)" slot still get the pseudonym (PseudonymsByInitials)
Logging/RecordingPseudonymizer.cs # Pure rewrite of a recording's action log, terminal log and bookmarks for a bug report bundle: the initials slot, the assign/unassign lines and the "Assigned to XY." error become the participant list's pseudonyms (?? for an unlisted person), terminal and chat text goes through SessionLogAnonymizer.Anonymize; "", AI, CRC, YAAT, other system senders, position callsigns and an " (override)" suffix are kept; free text inside an action is left as recorded; run by yaat-server's RecordingManager.ScrubForExport when ExportRecording's anonymize flag is set, and by the client for bookmarks
Logging/CrcDisplayName.cs      # Builds a CRC client's display name "CALLSIGN (Real Name)" (yaat-server's CRC session), parses the real name back out (the client's bundle participant fallback) and tells a position callsign (IsPositionCallsign) from a person
Diagnostics/ThreadCpuTime.cs   # Static: the calling thread's CPU time (GetThreadTimes / clock_gettime(CLOCK_THREAD_CPUTIME_ID)); timing-budget tests measure with it, not Stopwatch (docs/test-harness.md)
SerializableRandom.cs          # Xoshiro256** PRNG with serializable state (RngState record); drop-in Random replacement
DeterministicHash.cs           # Internal: Mix (murmur3 fmix32, spreads bits before a modulo), Fnv1a(salt, callsign), the per-callsign draw behind fixed per-aircraft choices (release auto-CTO jitter with an empty salt, the after-taxi-arrival and release-request delays, the VFR departure direction); replay-safe, no RNG state
InitialCallupPlan.cs           # Enum: the solo initial call a spawn makes (None / StandCall / AfterPush / AfterTaxiArrival / RunwaySayOnly / RunwayNoPreset), set once at load by Scenarios/InitialCallupClassifier; AircraftGroundOps.InitialCallup
PresetTaxiStop.cs              # Record: the stop a spawn's timed TAXI preset ends at (Spot / TaxiwayHoldShort / RouteEnd + name + held-on taxiway), recorded at load for an AfterTaxiArrival plan; AircraftGroundOps.PresetTaxiStop (+ PresetTaxiStopDto)
SimulationWorld.cs             # Thread-safe aircraft collection; GetSnapshot, Tick, DrainWarnings; PushPlans (the PushPlanStore its constructor takes: a room's own, shared by every engine the room builds)
                               # WeatherProfile? Weather — passed to FlightPhysics.Update() each tick
                               # TurnModel — cached from SimScenarioState.TurnModel before each tick; goes into PhysicsTickOptions
                               # Rng (snapshotted) + live-only ReactionDelayRng / ReleaseJitterRng (NOT snapshotted):
                               # command-run delay + REL spawn jitter sample off the dedicated RNGs and bake the value into
                               # the recording, so replay reproduces it without perturbing the shared Rng stream.
CommandQueue.cs                # CommandBlock (trigger + closure + TrackedCommands), BlockTrigger; the per-aircraft transmission id (LastTransmissionId, CommandBlock.TransmissionId)
                               # CommandDimension flags (Lateral|Vertical|Speed) for dimension-aware queue clearing
                               # BlockTriggerType.AfterCycleTerminator + TriggerTerminatorObserved fire after the next option or go-around (OTG); a full-stop landing can never satisfy it, so FlightPhysics.DiscardMissedCycleTerminatorBlocks marks it TriggerMissed and drops it with a cancellation warning
                               # ReadyToAdvance: lateral gates block advancement; altitude/speed are fire-and-forget
                               # SourceCommandText on CommandBlock/DeferredDispatch for snapshot restore
                               # DiscardChainRemainder: fire-time failure aborts the same-dispatch chain remainder (see docs/command-chaining.md)
Situation/AircraftSituation.cs # The aircraft's situation for context-menu quick commands (append-only numeric enum, sent on AircraftUpdated)
Situation/SituationClassifier.cs # Classify(ac, simTime, previous) → AircraftSituation: live traffic, then phase type (a turn takes the phase it resumes, a standalone turn keeps the stored flight-rules situation), then flight rules and the inbound/departing predicates with per-state hysteresis
Situation/AircraftSituationState.cs # Satellite: the stored situation, the situation flags, the runway to cross next (NextCrossingRunway), the named exits ahead on final and the rollout (ExitsAhead), where its driven taxi route meets other aircraft's (MeetPoints), the most recent liftoff time and WasOnGround; written by SimulationEngine.TickSituation, snapshotted as Simulation/Snapshots/AircraftSituationStateDto.cs
Situation/SituationFlags.cs # [Flags] inputs for the quick-list visibility rules (nearing the departure bar, holding short of the departure runway, inside the FAF, rollout decelerating, field / traffic in sight, cleared for takeoff, past V1, cleared for the approach with descent, given an EAPP, established on the expected final, a deferred approach clearance pending); append-only bits
Situation/ExpectedFinalGeometry.cs # IsEstablished(…): whether an airborne aircraft is established on the final course of its expected approach by geometry alone (course offset, track, outside the FAF within the localizer range, altitude floor, nearer this course than another aligned runway's); the input of SituationFlags.EstablishedOnExpectedFinal
Situation/SituationFlagCalculator.cs # Compute(ac, situation, previous, …) → SituationFlags and NextCrossingRunway(ac, situation, groundLayout, activeRunways) → the runway, by its active end, whose bar is the next uncleared one on the taxi route (null while a previous runway is not yet crossed, §3-7-2.c), once a second in the Situation step; the latches and bands read the previous flags
AircraftStatusDescriber.cs     # Pure AircraftState→text projection for the Aircraft List "Info" column.
                               # Describe(AircraftState) / Describe(AircraftStatusView); server computes once
                               # per broadcast → AircraftDto.SmartStatus (client just displays it), TickRecorder
                               # calls it too. One implementation so all surfaces agree.
RunwayDepartureQueue.cs        # Static per-hold-short departure-queue ranker (one runway-end label per line via DepartureDesignator: phase departure runway → own destination bar → bar display name, never a combined pavement id). UpdatePositions(world) runs
                               # each sim-second in SimulationEngine.TickPrePhysics (the per-second hook the
                               # live server shares — it never calls SimulationEngine.TickPostPhysics),
                               # writing 1-based AircraftGroundOps.RunwayQueuePosition + RunwayQueueRunway +
                               # RunwayQueueIntersection per aircraft (0/"" = not in line; even a lone #1
                               # counts). Drives the ground-datablock "{runway} #N" suffix (e.g. "28R #2",
                               # "28R@E #2" off an intersection) + Info-column "(#N)". Second pass (RankFollowers)
                               # ranks a follower (FollowingPhase, or HoldingShortPhase with the follow queued behind
                               # it) directly behind its leader when the leader is in a line, the follower's own route
                               # ends at that bar, at any distance from it (ProximityNm gates only an aircraft ranked on its own route),
                               # iterated to a fixpoint so chains rank all the way down; it takes the leader's line and is ranked on its own
                               # distance to the bar, in the holding-short tier only while it holds at that bar, else the taxiing tier; a
                               # follower that cannot inherit (leader lined up, different bar) falls back to its own route like a taxier.
GroundRollProfile.cs           # GroundRollProfile: the takeoff-roll spool ramp (idle → steady accel over the category's spool time) with
                               # closed forms SpeedAt/DistanceKtSecondsAt/TimeAtSpeed/TimeToCoverKtSeconds; the roll phases integrate it
                               # and every roll predictor (WillBeFlying, PrecedingDepartureBlock, RejectedTakeoff) projects on it.
GroundOutline.cs               # Plan-view aircraft outline for ground clearance (fuselage with a 30 ft tug lead on a pull, wing, tailplane) in a flat GroundOutlineFrame; GroundOutlineSize carries length, wingspan, tug lead and the aircraft's design group (which picks the pair's floor buffer); Clearance between two outlines, ClearanceBetween two aircraft. GroundConflictDetector sweeps it along a tug move's RemainingPath against parked/held neighbours; GroundCommandHandler refuses a tow that starts already touching one
GroundOutlineSweep.cs          # The per-sample outline sweep (Sweep) and floor rule (FloorFt) shared by GroundConflictDetector.TugMoveFoulsParkedAt (a tow under way) and TugPlanBuilder.Judge (candidate templates against TugRequest.ParkedNeighbours at plan time), so planner and detector cannot disagree. The floor's buffer is PairWingtipBufferFt (SmallPairWingtipBufferFt 10 ft for an ADG I/II pair, else WingtipBufferFt 25 ft, AC 150/5300-13B Table 5-1); WingtipBufferFt stays 25 ft for the detector's other readers. Tests: tests/Yaat.Sim.Tests/GroundOutlineSweepFloorTests.cs
GroundConflictDetector.cs      # Static pairwise ground proximity → SpeedLimit overrides. Runway priority by a per-pass RunwayRank (ComputeRank: runway user via RunwayOccupancy.ClassifyByPhase, vacating via RunwayOccupancy.IsVacating, else rest; LiesInPath exempts a yielder in the vacater's path);
                               # tug move vs parked/held neighbour: the outline sweep over the move's continuing run (TugMoveFoulsParkedAt +
                               # TugRunContinuation, floor anchored to the move's start) and a braking limit down to the towbar rate rather than
                               # a hard stop (TugMoveLimit/TugMoveBrakingLimitKts, margin from PushbackSpeed; floored each pass at the live speed less
                               # one detector interval of towbar braking, TowbarBrakingFloorKts, so a fouled-point's foot-or-two of jitter between
                               # path samples cannot ask for more braking than the towbar rate in one pass); compared for a yield target
                               # (ShowTugMoveYield) against the move's commanded main-gear speed (PushbackPhase.CommandedGearSpeedKts — the tug's pace
                               # times cos of the nose-gear steer angle), not the tug's own pace, since a turn slows the gear below it; an active
                               # PushbackPhase is never Stationary. WouldDeadlock's push-outranks-taxi rule is scoped to HasRampPriority (leg 1 of a stand push-off only).
                               # A forced tow (AircraftGroundOps.ForcedTowIgnoresParked, PUSHF/PUSHMF) does not stop for an aircraft at a stand or resting
                               # after a push (IsParkedAtStandOrAfterPush: AtParkingPhase or HoldingAfterPushbackPhase); one held or holding on the
                               # pavement still stops it, and moving traffic still limits it.
                               # GiveWayStop: a GIVEWAY's give-way point — how far the held aircraft still taxis along its route before its centre is within the
                               # pair's lateral clearance of the target's track through the nearest node the target still has to clear, or its nose (half its length ahead) within the
                               # target's half of it; GiveWayJunctionOf is the one judgement of that node (and of the release) over the whole of both routes, with no
                               # look-ahead cap, and a rejoin in trail is no crossing; GiveWayRouteVerdictOf adds the head-on half (decided at the from-node of the held
                               # aircraft's current edge when the traffic comes down it, else at the first shared node of the held route, whose nodes, start node and cut
                               # fillet corners included, HeldPath gathers) and takes the route a deferred TAXI would build; null with a noStopReason when the routes share no such junction (an overload takes the held route and the
                               # target's track explicitly: FollowingPhase's give-way at the merge). Every route the detector reads is FollowingPhase.DrivenRouteOf,
                               # so a FOLLOWG follower counts by its follow or clearing route. A follower is never limited against an aircraft in its lead chain
                               # (IsInLeadChainOf); a lead is exempt from its follower only while it is not closing on it (LeadChainExemptionOf,
                               # ClosingOnFollower: pushing toward it, or re-entering its own TaxiEdgeTrail by retracing, returning over a fillet arc, or turned about on its
                               # newest edge — judged from TaxiTrailEdge.EntryNodeId); a closing lead is limited, and takes the follower's limit when only the follower is (OneSidedPair).
                               # live-traffic shadows = MovementState.External (obstacle, never subject; Ground.ExternalOnRunway by geometry).
                               # Single-pass pair classifier (SameEdgeTrailing/SameEdgeHeadOn/
                               # Converging/Crossing/Pushback/Stationary). Honors Ground.Hold
                               # (HoldPosition or GiveWay) via IsImmobile + speed gate for
                               # parked-obstacle classification. Stationary-named phases
                               # (LineUpPhase, HoldingInPositionPhase) only count as Stationary
                               # while IsAtRest (GroundSpeed < HeldStationarySpeedKts AND no positive
                               # TargetSpeed), so rolling or pinned-but-commanding LineUpPhase aircraft
                               # don't skip conflict checking (#409).
                               # Every close-range conflict resolves one-holds-one-goes
                               # (deterministic holder, never both stopped), incl.
                               # converging-merge arbitration — except a parallel-lane pass
                               # (HasParallelTrackLateralRoom: tracks within 20° of parallel/anti-parallel,
                               # each with more room from the other's track than half-spans + WingtipBufferFt,
                               # now and projected along the route segment over the pair's stopping time, same side of the track),
                               # where ComputeClosingLimit's moving-obstacle lateral bypass and ResolveHeadOn's
                               # 300 ft ring both stand down and neither aircraft holds. DebugSink logs the
                               # specific hold kind so the controller GIVEWAY relationship is observable
                               # ("ControllerGiveWay A→B" pair line).
                               # A crossing pair's closing direction is the bearing to a point ahead along the route the aircraft drives
                               # (ClosingDirectionDeg), not its nose; a mutual stop's last tie-break is the aircraft with the other on its right (ICAO Annex 2 3.2.2.7.1).
                               # MeetCandidates / MeetPointsOf: where each on-ground aircraft's driven route meets the others' (the first junction both reach within
                               # ConvergenceLookaheadFt, FindSharedUpcomingNode, no ETA test; pairs whose route starts are over MeetPairRangeFt apart skipped), the
                               # per-aircraft GroundMeetPointDto list TickSituation stores as AircraftSituationState.MeetPoints; read-only
GroundMeetCandidate.cs         # One on-ground aircraft with a driven route as MeetCandidates builds it (aircraft, route, route-start position, airport id), the input MeetPointsOf pairs
GroundMeetPointDto.cs          # Wire record (Callsign/NodeId/Position) for one other aircraft whose driven route meets this one's; AircraftSituationState.MeetPoints carries the list to the client
HoldDirective.cs               # Structured ground-hold directive: HoldKind { HoldPosition,
                               # GiveWay } + optional YieldTarget callsign. Replaces the
                               # historical IsHeld+GiveWayTarget pair on AircraftGroundOps.
                               # Construct via HoldDirective.HoldPosition or
                               # HoldDirective.GiveWay(target). IsGiveWayFor(callsign)
                               # tests the pair relationship for the conflict detector.
GiveWayConstants.cs            # Auto-release tuning for direct GIVEWAY holds (FlightPhysics.UpdateGiveWayResume):
                               # safety-timeout (300s), target-stationary threshold (30s),
                               # stationary speed threshold, timeout clear-distance. They apply only
                               # while the routes neither cross at a node the target has to clear (GiveWayJunctionOf) nor run head-on
                               # (GiveWayRouteVerdictOf). Direct holds only — a deferred GIVEWAY or queued GiveWay block has no safety net
                               # (route verdict first, then the geometry) and deferred BEHIND keeps pure-geometry release. A direct hold
                               # also releases on GroundConflictDetector.TargetReachesMergeFirst
                               # (target on its last taxiway into the merge, nearer it, same way out).
ConflictAlertDetector.cs       # Static STARS CA detection: 3nm/1000ft thresholds, 5s extrapolation, hysteresis, approach suppression;
                               # IsPairEligible = live-traffic shadow policy (shared with the ERAM detector); CASUP suppresses an active alert in ConflictAlertState, not here
EramConflictDetector.cs        # Static ERAM (en-route) STCA detection: 5nm lateral (3nm at/below FL230) + 1000ft vertical, 4-min extrapolation, uses assigned/interim data-block altitudes, scoped per ERAM facility
EramConflictState.cs           # Per-facility ERAM conflict-alert state (active STCA pairs) driving the Center data-block flash
Asdex/AsdexSafetyLogicDetector.cs  # Static ASDE-X Safety Logic detection (Detect takes and returns the pair memory, AsdexDetection, so it depends on the previous tick): closed-runway (one per aircraft), occupied-runway (one per conflicting pair, an aircraft on an active runway in front of a claimant included), opposite-direction (OppDirTaxi / OppDirDeparture / OppDirLander, replacing the pair's occupied-runway alert; the pair memory then holds RWY OCCUPIED back until one leaves the runway), converging (a landing against a departure, or two landings, on runways whose centrelines cross ahead of both; carries AsdexSafetyAlert.RunwayEnds), taxiway-landing incursions → CRC surface alerts; closedRunwayAlertsOnly (the LIMITED runway configuration) keeps ClosedRunway alone;
                                    # Detect takes AsdexSafetyLogicRunways (navdata-resolved surfaces + footprints, from SimulationEngine's per-facility runway cache); who counts as a runway user, an occupant and an arrival is AsdexRunwayUsers, shared with the hold bars. ArrivalAglCeilingFt (300) is the taxiway-landing check's ceiling alone
Asdex/AsdexSafetyLogicConfig.cs    # AsdexSafetyLogicConfig (Runways, RunwayConfigurationId, InhibitedArrivalAlertPositionIds, RunwayConfigurationChosen: set once a CRC push names a different configuration, stops the re-pick; InhibitStartSeconds: position id → sim second its arrival-alert inhibit was set, lapsing after InhibitLapseSeconds (180); SupersededConfigurationIds: configuration id → sim second an ARWY re-pick moved away from it, a push carrying one within SupersededIdWindowSeconds (10) is stale, not a choice; all three JsonIgnore, so off the wire and the recording, kept in snapshots; NoSeconds / Seconds build the ordinal maps) + AsdexRunwayConfig (Id, AreaPoints as LatLon,
                                    # IsClosed): one facility's safety-logic configuration, seeded at load and replaced by a CRC surface display's push, Sim-native. Scenario state
                                    # (SimScenarioState.AsdexSafetyLogicConfigs, keyed by facility); ToSnapshot / FromSnapshot map it to ScenarioSnapshotDto's AsdexSafetyLogicConfigDto.
Asdex/AsdexSafetyLogicSeed.cs      # Build(ArtccConfigRoot, NavigationDatabase, ActiveRunways): the per-facility configuration a scenario load starts on, derived (not recorded) from each facility's asdexConfiguration
                                    # and navdata runways — position pair ids ("28R-10L"), then "{end1}-{end2}" for runways no position names; pavement rectangles, all open; the runway configuration is the
                                    # named one SelectRunwayConfigurationId picks from the active runways ("" = LIMITED for a facility with none). Repick re-picks on an active-runway change unless a user chose;
                                    # NamesRunwayConfiguration is the per-tick LIMITED gate (an id that names no configuration is LIMITED); OccupiedRunwayAlertingIsLive(config, configs, airportId) is the
                                    # airport-level question the RunwaySafetyAdvisor stand-down and the solo runway-user rule ask (facility via ArtccConfigResolver.FindAirportFacility)
Asdex/AsdexConfiguredTaxiways.cs   # Pure: Filter(segments, configured) limits the ground layout's taxiway segments to the names the airport sidecar's asdexLandingTaxiways lists (case-insensitive); a null set (no list) keeps every taxiway, an empty set keeps none, so no taxiway-landing alert
Asdex/AsdexHoldBarBuilder.cs       # Pure: the fixed hold-bar set of a facility's safety-logic runways over the ground layout (AsdexHoldBar: id, two points, protected runway; AsdexHoldBarRunway: a runway with its footprint). ResolveRunways pairs the configuration's runways with navdata; Build makes one bar per taxiway hold-short node at the runway edge, merges overlapping same-side bars and adds the runway-runway bars. Ids are deterministic from the layout
Asdex/AsdexHoldBarStatusEvaluator.cs # Pure and stateless: ActiveBarIds names the bars a runway user has ahead (AsdexHoldBarState: a bar plus IsActive); who is a runway user is AsdexRunwayUsers
Asdex/AsdexRunwayUsers.cs          # Pure: the one runway-user rule every ASDE-X alert kind and the hold bars read (thresholds 6,000 ft / 1,000 ft / 1,500 ft arrival box, 50 kt, 200 ft over the runway; UsesOf, IsUserOf, Occupies), the AsdexRunwayUserRole ranking (Role: phase, or kinematics for a live-traffic shadow) and First, the alert ordering; ConvergesAhead / ConvergingBeyondEndFt (1 NM) decide whether two uses of two runways converge
Asdex/AsdexRunwayAxis.cs           # A runway's centreline frame (along / lateral offsets, true track against the navdata true bearing, height over the mean of the end elevations; TryCrossing: where two runways' unbounded centrelines meet, none within ParallelToleranceDeg of parallel) shared by the hold-bar builder, evaluator and runway-user rule
Asdex/SurfaceMembership.cs         # ASDE-X / SAID hysteresis membership, pure: EvaluateAsdex / EvaluateSaid (enter at range + ceiling, leave at ceiling
                                    # + 600 ft; SAID ceiling = field elevation + 2,500 ft; a pure phantom is a member of nothing; ids no longer configured drop
                                    # out). SurfaceAirports.Resolve(config, navDb) builds the airport sets (SAID ceilings precomputed), cached by
                                    # SimulationEngine.TickSurfaceMembership and EvaluateSurfaceMembership(aircraft) (every spawn, every successful
                                    # aircraft command), which write AircraftStarsState.VisibleAsdexAirports / VisibleSaidAirports.
                                    # MayEnterAsdex / MayEnterSaid are the public enter tests (no hysteresis) EvaluateAsdex / EvaluateSaid use and the server's
                                    # stateless subscribe-replay helpers wrap; SaidSurfaceAirport.From(SaidAirportInfo, NavigationDatabase) holds the SAID
                                    # ceiling math (field elevation + 2,500 ft). yaat-server's CrcVisibilityTracker diffs these sets, so hysteresis lives only here.
                                    # yaat-server's DtoConverter projects it to and from the CRC wire DTO
Training/SameRunwaySeparation.cs   # SrsCategory + 7110.65 §3-9-6/§3-10-3 landmark distances and the satisfied-predicates (crossed end / airborne /
                                   # landed-and-past-landmark) + landing/departure-family occupant tests, shared by the evaluator and OccupiedRunwayGoAround
Training/SoloTrainingEvaluator.cs  # Solo-training scorecard: FAA separation, wake, runway-operation separation, runway-user scoring (an aircraft on, crossing or past the hold line of a runway at another's takeoff or landing), structured traffic-advisory/safety-alert/wake-advisory/field-proof events, ARTCC WakeDirectives, Class C outer-area/no-minima advisory scoring, active timeline, report buckets
Training/AircraftCompletion.cs     # Per-aircraft lifecycle stamps: spawn time, completion time, completion reason (Landed / Handed off / Dropped / Transited — overflight past its exit radius / Departed — departure past the session's auto-delete distance), filed route + operation classification used by the Session Report Aircraft tab.
Training/AircraftDebriefCoachingTemplates.cs  # Pure templates: one-line coaching note per completion reason + severity profile, consumed when aggregating per-aircraft debrief blocks from existing findings.
MagneticDeclination.cs         # Static: NOAA World Magnetic Model (WMM) declination via the Geo library, memoized on a 0.02° grid (cell-centre eval) per evaluation day; TrueToMagnetic/MagneticToTrue conversion.
                               # Sim state passes the scenario's MagneticModelDateUtc (the day of its SessionStartUtc, recorded with the session so replays never drift); display-only callers use EvaluationDateUtc (the process day, or the day a test process fixed with PinEvaluationDate)
MinimumSeparation.cs           # Static: Compute(MinSepTrack, MinSepTrack) → MinSepConverging / MinSepDiverging / MinSepInvalid; the STARS min-sep projection (CRC's 5 s steps, at most 760, planar degrees), pure; drawn by the Scope's MinSepRenderer
VisualDetection.cs             # Static: TryAcquireAirport, TryAcquireAirportForRunway, TryAcquireTraffic, IsOccludedByBank
                               # Maintained-contact variants (already-in-sight, weather-only incl. visibility-collapse × 1.25 tolerance): TryMaintainAirportContact, TryMaintainTrafficContact
                               # Airport visibility envelope AirportVisibilityRangeNm (vis × 0.869 × 1.5 × max(1, AGL/3000ft) — Koschmieder slab; shared by acquire + maintain; traffic keeps the literal cap)
                               # Returns VisualAcquisitionResult { Acquired, Reason, DistanceNm, MaxRangeNm }
                               # VisualAcquisitionFailure enum: InClassA, AboveCeiling, MixedCeiling, BehindOwnship, OccludedByBank, OutOfRange, OppositeSideOfRunway
                               # Forward hemisphere (traffic ±110°, airport ±120°), visibility, ceiling, bank angle occlusion (7110.65 §7-4-4.c.2), WTG-based traffic range
                               # FL180 gate on airport (visual approach eligibility) but NOT traffic (pilots can see in Class A)
VisualAcquisition.cs           # Static helpers: TryAcquireTraffic(ownship, target, weather), TryMaintainTrafficContact(...) and TryAcquireAirport(ownship, weather)
                               # Bundle METAR/elevation/bank-angle lookup around VisualDetection so ALL traffic paths (RTIS first-check,
                               # PilotObservationUpdater re-check, CheckLeadLifecycle, TickVisualDetection) use identical ownship-nearest-station
                               # inputs. TryAcquireAirport returns null when the destination is missing or not in the nav db (caller drops the observation).
PilotObservation.cs            # Abstract record PilotObservation + TrafficAcquisitionObservation(TargetCallsign, IssuedAtSeconds, ArmedAirborne) + FieldAcquisitionObservation
                               # Pilot-side "watch for a condition" state — populated when RTIS/RFIS soft-fail (pilot keeps looking)
                               # Extension points for future "report leaving altitude", "report passing fix", etc.
PilotObservationUpdater.cs     # Static per-tick evaluator called from FlightPhysics.Update after UpdateCommandQueue
                               # Re-runs VisualAcquisition.TryAcquireTraffic / TryAcquireAirport; on success sets the matching
                               # HasReported* flag and pushes the in-sight pilot readback through PilotResponder.RouteRpoSayReadback:
                               # RPO+RpoShowPilotSpeech → PendingPilotSpeech (green, the written TerminalForRpo line of BuildTrafficInSight/BuildFieldInSight),
                               # solo → typed PendingPilotTransmissions for delayed SAY/audio; RPO default → PendingPilotReadbacks only
                               # (SAY channel, "Have <target> in sight" / "Have the field in sight").
                               # Silently drops observations whose target has left the sim or whose
                               # destination is no longer lookupable.
WakeTurbulenceData.cs          # Static: CWT code lookup from AircraftCwt.json; wake minima (TBL 5-5-2) + alerted visual TrafficDetectionRangeNm (9-arcmin silhouette model, clamp [1.5, 12] nm)
```
