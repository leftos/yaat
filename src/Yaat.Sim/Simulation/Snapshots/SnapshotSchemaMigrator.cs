using Microsoft.Extensions.Logging;
using Yaat.Sim.Asdex;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Simulation.Snapshots;

/// <summary>
/// Thrown when a snapshot's schema version cannot be upgraded to the current version.
/// Callers should fall back to command replay (v1 mode) for this recording.
/// </summary>
public sealed class SnapshotSchemaException(int snapshotVersion, int requiredVersion)
    : Exception(
        $"Snapshot schema version {snapshotVersion} cannot be migrated to version {requiredVersion}. " + "Use command replay mode for this recording."
    )
{
    public int SnapshotVersion { get; } = snapshotVersion;
    public int RequiredVersion { get; } = requiredVersion;
}

/// <summary>
/// Upgrades <see cref="StateSnapshotDto"/> instances from older schema versions
/// to <see cref="CurrentSchemaVersion"/> via a sequential migration chain.
/// Each migration step handles one version increment (e.g., 1→2, 2→3).
/// When a breaking change makes migration impossible, the step throws
/// <see cref="SnapshotSchemaException"/>.
/// </summary>
public static class SnapshotSchemaMigrator
{
    private static readonly ILogger Log = SimLog.CreateLogger("SnapshotSchemaMigrator");

    public const int CurrentSchemaVersion = 41;

    /// <summary>The airtime a pre-V39 transmission time migrates as: those meters stored only the second a call started.</summary>
    public const double LegacyTransmissionSeconds = 3.0;

    /// <summary>The number snapshots before V41 stored for the retired taxi-onto-active-runway ASDE-X alert kind.</summary>
    private const int RetiredTaxiOntoActiveRunwayKind = 2;

    /// <summary>
    /// Migrates a snapshot to <see cref="CurrentSchemaVersion"/> in place.
    /// No-op if already current. Throws <see cref="SnapshotSchemaException"/>
    /// if the schema is too old or too new to migrate.
    /// </summary>
    public static void Migrate(StateSnapshotDto snapshot)
    {
        if (snapshot.SchemaVersion == CurrentSchemaVersion)
        {
            return;
        }

        if (snapshot.SchemaVersion > CurrentSchemaVersion)
        {
            throw new SnapshotSchemaException(snapshot.SchemaVersion, CurrentSchemaVersion);
        }

        // Sequential migration chain: apply each step in order.
        // V1→V2: Added ServerSnapshotDto (consolidation, conflicts, beacon pool).
        //   No data transformation — V1 snapshots have Server = null, which
        //   RestoreFromSnapshot handles gracefully by clearing state.
        // V2→V3: Added AircraftFlightPlanDto.CreatedByOwner. No data transformation —
        //   legacy snapshots default to null, which makes TickFlightPlanCreatorAutoTrack
        //   a no-op for those aircraft (preserving prior replay behavior).
        // V3→V4: Split actual vs filed aircraft type — added AircraftFlightPlanDto.AircraftType.
        //   Legacy snapshots default the new field to "". Seed it from the parent aircraft's
        //   AircraftType so STARS/ASDE-X/FP-Editor still display a type when replaying old
        //   recordings (where filed and actual were always the same single field).
        // V4→V5: Added AircraftSnapshotDto.PendingPilotRequest. No data transformation —
        //   older snapshots default to null, meaning no unsatisfied pilot request was active.
        // V5→V6: Added AircraftSnapshotDto.HasLeftStudentFrequency. No data transformation —
        //   older snapshots default to false, preserving previous in-service behavior.
        // V6→V7: Added HoldShortPointDto.ClearedByAutoCross to track AutoCross-driven
        //   clearance. No data transformation — older snapshots default to false, which
        //   matches pre-feature semantics: a subsequent AutoCross-OFF toggle on replay
        //   will not revert any of their pre-cleared hold-shorts.
        // V7→V8: Added AircraftSnapshotDto.SpawnedAtSeconds / CompletedAtSeconds /
        //   CompletionReasonValue / CompletionDetail for M12.4 per-aircraft debrief.
        //   No data transformation — older snapshots default to spawn-at-0 / not-completed,
        //   which makes time-on-frequency report from session start and shows the aircraft
        //   as Active in the debrief tab until current-session lifecycle hooks fire.
        // V8→V9: Added AircraftGhostTrackDto.IsOverlay so the operator-facing Aircraft List
        //   can keep ghost overlays on real scenario aircraft visible while still hiding
        //   pure phantom DA/VP data blocks. No data transformation — older snapshots
        //   default to false; for replay correctness that is the safe choice because the
        //   field only affects the YAAT Aircraft List filter, not simulation behavior.
        // V9→V10: Added AircraftSnapshotDto.Note (instructor freetext datablock note).
        //   No data transformation — older snapshots default to "" (no note).
        // V10→V11: Added ScenarioSnapshotDto.MetarReissuanceEnabled + WeatherSourceJson so dynamic
        //   METAR re-issuance and v2 weather-timeline evolution survive a snapshot-based rewind.
        //   No data transformation — older snapshots default to false / null, matching the prior
        //   behavior where both stopped after rewind until weather was reloaded.
        // V11→V12: Added HoldShortPointDto.Reason + HoldingShortPhaseDto.Reason so the hold-short
        //   reason (DestinationRunway / RunwayCrossing / ExplicitHoldShort) survives a snapshot
        //   restore instead of being reclassified. No data transformation — older snapshots default
        //   to null, and FromSnapshot falls back to the prior hardcoded reason (ExplicitHoldShort for
        //   taxi-route hold-shorts, RunwayCrossing for HoldingShortPhase), preserving restore behavior.
        // V12→V13: Added PhaseListDto.RequestedExitTaxiway and started restoring RequestedExit in
        //   PhaseList.FromSnapshot (it was serialized lossily — side only — and never restored, so a
        //   controller's EXIT preference was dropped across rewind/reconnect). No data transformation —
        //   older snapshots default RequestedExitTaxiway to null; a legacy RequestedExit side now restores
        //   instead of being silently discarded.
        // V13→V14: Added AircraftSnapshotDto.DataBlock (STARS Track Reposition surveillance/datablock
        //   split). No data transformation — older snapshots default to a Bound datablock (no parked
        //   unsupported datablock), exactly the pre-feature behavior.
        // V14→V15: Reshaped ApproachScoreDto to faithfully round-trip ApproachScore and started
        //   restoring AircraftSnapshotDto.ActiveApproachScore in AircraftState.FromSnapshot (it was
        //   serialized lossily and never restored, so an approach established-but-not-yet-landed lost
        //   its score across rewind/reload and the subsequent landing was never stamped). No data
        //   transformation — the removed phantom fields are ignored on read and the added fields
        //   default cleanly (AircraftType to "", others to 0/false); a legacy ActiveApproachScore now
        //   restores best-effort instead of being silently dropped.
        // V15→V16: Added AircraftTransponderDto.CommandedSquawkVfr, the RPO-display latch that suppresses
        //   the assigned-vs-reported beacon-code mismatch flash after a squawk-VFR instruction. No data
        //   transformation — older snapshots default to false (unlatched), matching the prior behavior
        //   where the flash was never suppressed.
        // V16→V17: Added AircraftSnapshotDto.MilitaryRoute (AP/1B military training route clearance
        //   state) and the MilitaryRoute phase DTO. No data transformation — older snapshots default
        //   to "not on a military route", exactly the pre-feature behavior.
        // V17→V18: Removed RunwayInfoDto.LengthFt. It carried one end's published landing distance
        //   available and was read as a physical runway length; the length is now derived from the
        //   runway's own end coordinates, which every snapshot already carries. No data transformation —
        //   the field is ignored on read, and the derived length is strictly more correct than the value
        //   it replaces (at KSJC 12R/30L, 11,001 ft of pavement rather than an 8,587 ft landing distance).
        // V18→V19: Runway ends carry their own landing threshold elevation (from the CIFP) instead of both
        //   reading airport elevation, and RunwayInfoDto gained a nullable AirportElevationFt so traffic
        //   pattern altitude keeps its own field datum. No data transformation — older snapshots have no
        //   AirportElevationFt, and RunwayInfo.FromSnapshot falls back to the mean of the two ends, which
        //   in those snapshots are both airport elevation. That reproduces their pattern altitudes exactly.
        // V19→V20: MidfieldCrossingPhaseDto.BiasTurnToPatternSide (bool) replaced by InitialTurn (nullable
        //   TurnDirection ordinal), and CrossAtPatternAltitude added. The bias bool is ignored on read: a
        //   snapshot taken mid-crossing restores with no initial-turn bias, which only affects the first
        //   seconds of the turn onto the join heading (the phase releases the bias as soon as it is roughly
        //   pointed at the midfield target, and the target itself is unchanged). CrossAtPatternAltitude
        //   defaults to false — the pre-feature crossing altitude rule — so those snapshots fly as before.
        // V20→V21: ScenarioSnapshotDto.MagneticModelDateUtc (a day) replaced by SessionStartUtc (the instant t=0 is
        //   anchored to; the magnetic day is derived from it). The old field is ignored on read; a legacy snapshot
        //   restores with the session start the loader resolved from the manifest — the same day, so declinations
        //   are unchanged.
        // V21→V22: Added ServerSnapshotDto.Strips and .Tdls — the flight strips and the vTDLS session became
        //   engine state instead of the room's. No data transformation: both default to null, and
        //   RestoreFromSnapshot reads a null section as "restore empty", which is what those snapshots'
        //   engines held. A same-room rewind of an older recording therefore starts with no strips and no
        //   PDCs and rebuilds them from the recorded requests and the host's auto-print steps.
        // V22→V23: Added ServerSnapshotDto.TowerLists — the tower lists' dwell entries (which aircraft each STARS
        //   P-list holds and the elapsed second it came into range at) became engine state instead of the room's.
        //   No data transformation: it defaults to null and RestoreFromSnapshot reads a null section as "restore
        //   empty", which is what those snapshots carried. A restore of an older recording therefore starts with
        //   empty P-lists and the proximity step refills them on the next second, at that second — the entry order
        //   those lists sort on is only exact from a V23 snapshot onward. Since the lists were keyed by (facility,
        //   list id), a section written before that carries no TowerListEntriesDto.FacilityId, and a list id alone
        //   cannot be attributed to a facility (ZOA's FAT and NCT both declare "P1"): such a list is dropped whole
        //   on restore, with one aggregated warning naming it. No bump — the field is optional with a clean default
        //   and there is nothing to transform, since the facility a V23 list belonged to was never recorded.
        // V23→V24: SharedStateDto.IsQueriedUntil (the absolute instant CRC sent for the STARS query flash) replaced by
        //   QueriedUntilElapsedSeconds (session time). The legacy field has no meaningful mapping — it was minted on the
        //   sending client's clock during a session that has since ended — so it is ignored on read and the new field
        //   stays null: a restored older recording shows no query flash rather than one that expired long ago.
        //   The action log carries the same DTO on RecordedStarsSharedStateChange and Migrate never walks it — there the
        //   legacy property is simply an unmapped member the deserializer skips, which lands on the same no-flash outcome.
        // V24→V25: Added ControlTargetsDto.SpeedCommandIsControllerIssued, which separates a speed a human controller
        //   assigned from one a scenario preset or an AI position assigned (both set HasExplicitSpeedCommand alike).
        //   No data transformation — the field is optional and older snapshots default to false, i.e. "no controller owns
        //   this speed". That is the safe default: it never claims controller ownership for an assignment whose origin the
        //   snapshot never recorded, and the first controller speed command after the restore sets it.
        //   The same bump also added AircraftApproachStateDto.SameRunwayProtectionCeilingKts and
        //   SameRunwayProtectionDisplacedCeilingKts, which record the speed ceiling the same-runway arrival-protection pass
        //   stamped and the one it stamped over (so the release puts a published crossing-speed restriction back rather than
        //   deleting it). Equally optional and equally undemanding on read: an older snapshot restores with no ceiling
        //   attributed to the pass, which then re-engages on the next tick if the conflict it protects against is still
        //   predicted.
        // V25→V26: PushbackPhaseDto now carries one tug move: its Kind (Push or Pull; older snapshots default to Push,
        //   which every pushback written before tug moves was), Shape and shape fields, the Tight/Creep/DwellBefore
        //   flags, StartsAtStand, the planned end, the stand push-off's facing amendment, and the move's progress.
        //   The pre-tug-move fields (TargetHeading, TargetLatitude/Longitude, PullForwardLatitude/Longitude,
        //   PullingForward, StartLat/StartLon, TotalDistToTarget, ReachedTarget, IsAligned) keep their JSON names as
        //   read-only Legacy* properties. No data transformation here: a DTO with no Shape is an older snapshot, and
        //   PushbackPhase.FromSnapshot builds the equivalent move from its legacy fields, finishing it on the first
        //   tick because that needs the live aircraft pose.
        // V26→V27: Added AircraftApproachStateDto.SameRunwayProtectionFasInstructed, the latch recording that the
        //   simulated tower has already told this arrival to reduce to final approach speed (§5-7-3.f), which the
        //   same-runway protection holds through the §5-7-1.b.4 window instead of releasing there. No data
        //   transformation — the field is optional and older snapshots default to false: the pass simply re-issues the
        //   instruction on the next tick if the conflict persists and the follower is still outside 5 nm.
        // V27→V28: Added AircraftApproachStateDto.SameRunwayProtectionDropoutSeconds, the debounce clock the same-runway
        //   protection runs while an arrival it owns is momentarily outside the ±45° track window that makes a
        //   not-yet-cleared follower part of the runway's stream. The pass holds the reduction silently for that long
        //   instead of releasing it and issuing it again a tick later. No data transformation — the field is optional and
        //   older snapshots default to 0, which reads as "the drop-out starts now": a restore taken in the middle of one
        //   holds the reduction for the full hysteresis again rather than releasing it early.
        // V28→V29: Added the QT Coast Track fields to AircraftEramStateDto (IsCoastTrack, CoastLat/CoastLon,
        //   CoastStartSeconds, CoastAltitude, CoastSpeed, CoastTrueCourse, CoastRoute). No data transformation — the
        //   fields are optional and older snapshots default to not coasting, which is what every track written before the
        //   coast existed was.
        // V29→V30: AircraftHoldAnnotationDto.Efc became nullable, null meaning no EFC, so an ERAM EFC of 0000 can be
        //   stored. Before V30 the field was a plain int whose 0 meant no EFC, so a V29-or-older snapshot's 0 is rewritten
        //   to null here. The same DTO rides RecordedHoldAnnotationChange in the action log, which Migrate never walks;
        //   RecordingSchemaUpgrader rewrites those Efc 0s to null when the recording's snapshots predate V30.
        // V30→V31: flight plan gains AltitudeFix / AltitudeAfterFixFeet / AltitudeFixPassed / AltitudeFixApproached,
        //   IntrafacilityRemarks, CruiseMach, IsSpeedClassified; the Remarks key now holds the interfacility remarks; no data
        //   transformation.
        // V31: Added the ERAM vertical-conformance latch to AircraftEramStateDto (ReachedAssignedAltitude, ConformanceKey).
        //   No data transformation: both are optional, and an older snapshot's false/null reads as never evaluated, so the
        //   next tick stores the current assignment and re-evaluates the latch, as for a new aircraft. No bump — the fields
        //   are optional with a clean default and there is nothing to transform.
        // V31→V32: the per-aircraft ERAM IsDwellLocked, LeaderDirection, LeaderLength and DriHaloType became per-sector
        //   state (AircraftEramStateDto.SectorDisplays, keyed by facility and sector). No data transformation: the legacy
        //   values name no sector, so they are skipped on read and every sector restores at CRC's defaults (no dwell lock,
        //   the default leader, no halo). The step exists so the version names the reinterpretation.
        // V32: Added ScenarioSnapshotDto.SoloRpoCommandsAllowed. No bump — additive and optional, an older snapshot reads false.
        // V32→V33: AircraftGroundOpsDto.IsScriptedDeparture replaced by InitialCallup (the initial call-up plan the loader
        //   sets) and SpawnTaxiway added. A scripted departure never called, so it maps to None; any other aircraft whose
        //   call-up decision was still open and that still sits at its stand (current phase AtParking) would have made the
        //   stand call, so it maps to StandCall; the rest (an airborne arrival never decided) to None. The legacy field is
        //   nulled so a rewritten snapshot drops it. SpawnTaxiway stays null: the pre-snap position was never recorded.
        // V33: Added CommandBlockDto.IsScenarioScripted. No bump — additive and optional, an older snapshot reads false.
        // V33: Added StripBayRackSnapshotDto.AutoSort. No bump — additive and optional, an older snapshot reads false, and
        //   false is left out of the JSON.
        // V33: Added ScenarioSnapshotDto.ActiveRunways. No bump — additive and optional, and the field is left out of the
        //   JSON while the room has named no runways, so an older snapshot reads no runways and an empty room writes none.
        if (snapshot.SchemaVersion < 4)
        {
            foreach (AircraftSnapshotDto ac in snapshot.Aircraft)
            {
                // A legacy snapshot can carry a null FlightPlan (the lenient resolver permits it);
                // there is nothing to seed then, so skip rather than dereference.
                if (ac.FlightPlan is { AircraftType: null or "" })
                {
                    ac.FlightPlan.AircraftType = ac.AircraftType;
                }
            }
        }

        if (snapshot.SchemaVersion < 30)
        {
            foreach (AircraftSnapshotDto ac in snapshot.Aircraft)
            {
                if (ac.HoldAnnotation is { Efc: 0 } hold)
                {
                    hold.Efc = null;
                }
            }
        }

        if (snapshot.SchemaVersion < 33)
        {
            foreach (AircraftSnapshotDto ac in snapshot.Aircraft)
            {
                // A legacy snapshot can carry a null Ground (the lenient resolver permits it); nothing to derive then.
                if (ac.Ground is { } ground)
                {
                    ground.InitialCallup = LegacyInitialCallup(ground, IsAtParking(ac.Phases));
                    ground.LegacyIsScriptedDeparture = null;
                }
            }
        }

        // V33→V34: BlockTriggerDto.Radial renamed TrueRadial, and IssuedRadial (the magnetic radial as issued) added. The old
        //   engine compared Radial as a true bearing from the fix, so it carries into TrueRadial and old replays fire where they
        //   did. Only an FRD trigger's Radial was surely the issued radial (every ReachFrdPoint came from AT {fix}{radial}{dist});
        //   an InterceptRadial's may be JAWY's true segment course, so its IssuedRadial stays null.
        if (snapshot.SchemaVersion < 34)
        {
            foreach (AircraftSnapshotDto ac in snapshot.Aircraft)
            {
                MigrateLegacyRadials(ac.Queue);
            }
        }

        // V34→V35: ScenarioSnapshotDto.AutoCrossRunway (bool) replaced by AutoCrossRunwayMode (Off / All / InactiveOnly). The
        //   old switch pre-cleared every crossing when on, so true maps to All and false (or a snapshot without it) to Off,
        //   and those replays clear exactly the crossings they did. The legacy field is nulled so a rewritten snapshot drops it.
        if (snapshot.SchemaVersion < 35)
        {
            snapshot.Scenario.AutoCrossRunwayMode =
                (snapshot.Scenario.LegacyAutoCrossRunway == true) ? AutoCrossRunwayMode.All : AutoCrossRunwayMode.Off;
            snapshot.Scenario.LegacyAutoCrossRunway = null;
        }

        // V35→V36: the single scenario-wide ScenarioSnapshotDto.AsdexSafetyLogicConfig replaced by AsdexSafetyLogicConfigs (one
        //   per ASDE-X facility), and AsdexSafetyAlertDto gained FacilityId. A legacy config and the legacy alerts move under one
        //   facility: the snapshot's PrimaryAirportId, a four-letter id losing its leading ICAO "K". With no PrimaryAirportId
        //   there is no facility to file them under, so both are dropped with a warning; the next tick re-raises whatever alert
        //   a configuration still standing warrants. The legacy field is nulled so a rewritten snapshot drops it.
        if (snapshot.SchemaVersion < 36)
        {
            MigrateLegacyAsdexSafetyLogic(snapshot.Scenario);
        }

        // V36→V37: NavigationTargetDto.SpeedTransmissionId added, the transmission that set a fix's speed restriction, apart
        //   from CrossingTransmissionId, which now answers for the crossing altitude only. Through V36 the crossing stamp also
        //   marked the speed, so it is copied onto every fix that carries both it and a speed restriction, in the live route and
        //   every route a phase keeps, and those replays keep or restore the speeds they did.
        if (snapshot.SchemaVersion < 37)
        {
            foreach (AircraftSnapshotDto ac in snapshot.Aircraft)
            {
                MigrateLegacySpeedStamps(ac);
            }
        }

        // V37→V38: ControlTargetsDto.SpeedMaxHeld added, true while SMAX ("maintain maximum forward speed") is in force, and
        //   NavigationTargetDto.RevertSpeedMax, true on a crossing fix past which SMAX is armed again. No aircraft before V38
        //   could have been given SMAX, so both members' absence restores false and needs no step.

        // V38→V39: StateSnapshotDto.FrequencyTransmissionTimes (the second each transmission started, the meter then
        //   counting transmissions) replaced by FrequencyTransmissions (start and duration, the meter now judging dead air).
        //   Each stored time becomes a LegacyTransmissionSeconds transmission from that second; a snapshot without times
        //   restores an empty meter. The legacy field is nulled so a rewritten snapshot drops it.
        if (snapshot.SchemaVersion < 39)
        {
            MigrateLegacyFrequencyTransmissionTimes(snapshot);
        }

        // V39→V40: FollowingPhaseDto.HasBeenOnClearedRunway (the follower had been on some cleared runway, the whole clearance
        //   spent once clear of all) replaced by BeenOnClearedRunways (each runway spent on its own once clear of it). A true
        //   flag marks every cleared runway been-on, so each is spent once the follower is clear of it, as all were together;
        //   false or absent marks none. The legacy field is nulled so a rewritten snapshot drops it.
        if (snapshot.SchemaVersion < 40)
        {
            foreach (FollowingPhaseDto follow in snapshot.Aircraft.SelectMany(static ac => ac.Phases?.Phases ?? []).OfType<FollowingPhaseDto>())
            {
                MigrateLegacyBeenOnClearedRunway(follow);
            }
        }

        // V40→V41: AsdexAlertKind lost TaxiOntoActiveRunway (stored as 2): an aircraft taxiing onto an active runway now
        //   raises an occupied-runway alert, as vNAS does; the alert texts became vNAS's; AsdexSafetyAlertDto.GoAround was
        //   added, whose absence reads false. A standing alert of the retired kind restores as OccupiedRunway (1) under its
        //   stored id, which the detector no longer raises, so the next tick clears it and raises the pair's alert. Every
        //   other standing alert's third line is rewritten to the plain current text ("OCCUPIED RWY" → "RWY OCCUPIED").
        //   A standing closed-runway alert (kind 0) is dropped instead: it does not say whether its aircraft was an
        //   arrival, which decides between "RWY CLOSED" and "RWY CLOSED, GO AROUND" and GoAround, and only the detector's
        //   arrival box over a live aircraft can tell. A restore that kept it would keep GoAround false until it cleared,
        //   because ApplyAsdexDetection never rewrites a standing alert; dropped, the next tick raises it again under the
        //   same id with the right role, at the cost of one extra aural.
        if (snapshot.SchemaVersion < 41)
        {
            List<AsdexSafetyAlertDto>? alerts = snapshot.Scenario.ActiveAsdexAlerts;
            if (alerts is not null)
            {
                alerts.RemoveAll(alert => alert.Kind == AsdexAlertKind.ClosedRunway);
                foreach (AsdexSafetyAlertDto alert in alerts)
                {
                    MigrateLegacyAsdexAlert(alert);
                }
            }
        }

        snapshot.SchemaVersion = CurrentSchemaVersion;
    }

    /// <summary>Turns a pre-V40 follow's been-on flag into the runways it marks been on: every cleared runway, or none.</summary>
    private static void MigrateLegacyBeenOnClearedRunway(FollowingPhaseDto follow)
    {
        if (follow.LegacyHasBeenOnClearedRunway == true)
        {
            follow.BeenOnClearedRunways = [.. follow.CrossingClearedRunways];
        }

        follow.LegacyHasBeenOnClearedRunway = null;
    }

    /// <summary>Turns a pre-V39 snapshot's transmission start times into transmissions of <see cref="LegacyTransmissionSeconds"/>.</summary>
    private static void MigrateLegacyFrequencyTransmissionTimes(StateSnapshotDto snapshot)
    {
        if (snapshot.LegacyFrequencyTransmissionTimes is { Count: > 0 } times)
        {
            snapshot.FrequencyTransmissions =
            [
                .. times.Select(start => new FrequencyTransmissionDto { Start = start, Duration = LegacyTransmissionSeconds }),
            ];
        }

        snapshot.LegacyFrequencyTransmissionTimes = null;
    }

    /// <summary>
    /// Maps a pre-V41 alert's retired kind to OccupiedRunway and its pre-V41 third line to the current text. Closed-runway
    /// alerts are dropped before this runs, so none reaches it.
    /// </summary>
    private static void MigrateLegacyAsdexAlert(AsdexSafetyAlertDto alert)
    {
        if ((int)alert.Kind == RetiredTaxiOntoActiveRunwayKind)
        {
            alert.Kind = AsdexAlertKind.OccupiedRunway;
        }

        if ((alert.MessageLines.Count > 2) && LegacyAsdexAlertTexts.TryGetValue(alert.MessageLines[2], out string? current))
        {
            alert.MessageLines[2] = current;
        }
    }

    /// <summary>The pre-V41 third lines of the alert kinds that are kept and the texts that replaced them.</summary>
    private static readonly Dictionary<string, string> LegacyAsdexAlertTexts = new(StringComparer.Ordinal)
    {
        ["OCCUPIED RWY"] = AsdexSafetyLogicDetector.OccupiedRunwayText,
        ["RWY INCURSION"] = AsdexSafetyLogicDetector.OccupiedRunwayText,
    };

    /// <summary>Gives each fix a pre-V37 aircraft carries with a crossing stamp and a speed restriction that stamp as its speed stamp.</summary>
    private static void MigrateLegacySpeedStamps(AircraftSnapshotDto ac)
    {
        foreach (NavigationTargetDto fix in LegacyRoutes(ac).SelectMany(route => route))
        {
            if ((fix.CrossingTransmissionId is { } stamp) && (fix.SpeedRestriction is not null))
            {
                fix.SpeedTransmissionId = stamp;
            }
        }
    }

    /// <summary>
    /// The navigation routes a snapshot carries for <paramref name="ac"/>: the live route, the departure clearance's, and the
    /// routes its phases keep to fly or resume later. A legacy snapshot can carry a null Targets or Phases (the lenient
    /// resolver permits it), which has none.
    /// </summary>
    private static IEnumerable<List<NavigationTargetDto>> LegacyRoutes(AircraftSnapshotDto ac)
    {
        if (ac.Targets is { NavigationRoute: { } live })
        {
            yield return live;
        }

        if (ac.Phases is not { } phases)
        {
            yield break;
        }

        if (phases.DepartureClearance is { DepartureRoute: { } cleared })
        {
            yield return cleared;
        }

        foreach (PhaseDto phase in phases.Phases ?? [])
        {
            List<NavigationTargetDto>? kept = phase switch
            {
                AirspaceBoundaryHoldPhaseDto hold => hold.OriginalRoute,
                InitialClimbPhaseDto climb => climb.DepartureRoute,
                DepartureProcedurePhaseDto procedure => procedure.PostRoute,
                _ => null,
            };
            if (kept is not null)
            {
                yield return kept;
            }
        }
    }

    /// <summary>Files a pre-V36 snapshot's single safety-logic configuration and its alerts under the primary airport's facility.</summary>
    private static void MigrateLegacyAsdexSafetyLogic(ScenarioSnapshotDto scenario)
    {
        if ((scenario.LegacyAsdexSafetyLogicConfig is null) && (scenario.ActiveAsdexAlerts is not { Count: > 0 }))
        {
            return;
        }

        if (scenario.PrimaryAirportId is not { Length: > 0 } primaryAirportId)
        {
            Log.LogWarning(
                "Snapshot before schema 36 has an ASDE-X safety-logic configuration or alerts "
                    + "but no primary airport to file them under; dropping them"
            );
            scenario.LegacyAsdexSafetyLogicConfig = null;
            scenario.ActiveAsdexAlerts = null;
            return;
        }

        string facilityId = NavigationDatabase.NormalizeAirport(primaryAirportId);
        if (scenario.LegacyAsdexSafetyLogicConfig is { } legacy)
        {
            scenario.AsdexSafetyLogicConfigs = new Dictionary<string, AsdexSafetyLogicConfigDto>(StringComparer.Ordinal) { [facilityId] = legacy };
        }

        foreach (AsdexSafetyAlertDto alert in scenario.ActiveAsdexAlerts ?? [])
        {
            alert.FacilityId = facilityId;
        }

        scenario.LegacyAsdexSafetyLogicConfig = null;
    }

    /// <summary>Moves each trigger's pre-V34 <c>Radial</c> into <see cref="BlockTriggerDto.TrueRadial"/>; a legacy queue may be null.</summary>
    private static void MigrateLegacyRadials(CommandQueueDto? queue)
    {
        if (queue?.Blocks is not { } blocks)
        {
            return;
        }

        foreach (CommandBlockDto block in blocks)
        {
            if (block.Trigger is not { LegacyRadial: { } legacyRadial } trigger)
            {
                continue;
            }

            trigger.TrueRadial = legacyRadial;
            if (trigger.Type == (int)BlockTriggerType.ReachFrdPoint)
            {
                trigger.IssuedRadial = legacyRadial;
            }

            trigger.LegacyRadial = null;
        }
    }

    private static InitialCallupPlan LegacyInitialCallup(AircraftGroundOpsDto ground, bool atParking)
    {
        bool decisionOpen = (ground.LegacyIsScriptedDeparture != true) && !ground.InitialCallupDecisionProcessed;
        return (decisionOpen && atParking) ? InitialCallupPlan.StandCall : InitialCallupPlan.None;
    }

    /// <summary>Whether the aircraft's current phase is <see cref="AtParkingPhaseDto"/>; a legacy snapshot may carry no phase list.</summary>
    private static bool IsAtParking(PhaseListDto? phases)
    {
        if (phases?.Phases is not { } list)
        {
            return false;
        }

        return (phases.CurrentIndex >= 0) && (phases.CurrentIndex < list.Count) && (list[phases.CurrentIndex] is AtParkingPhaseDto);
    }
}
