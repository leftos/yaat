using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// VFR follow phase: the follower pursues another VFR aircraft in free flight,
/// matching heading toward the lead's position and the lead's speed with
/// distance-based spacing correction. Altitude is left unchanged — real pilots
/// told "follow traffic" maintain their current/assigned altitude (often staying
/// visually above the lead), and the pattern phases take over altitude on join.
///
/// When the lead is in a pattern phase and the follower is within
/// <see cref="JoinRangeNm"/> of the lead's downwind abeam point, within
/// <see cref="MaxJoinGapNm"/> of the lead itself, and on the same side of the
/// runway as the pattern, this phase swaps itself out for a full pattern circuit
/// (PatternEntryPhase → DownwindPhase → BasePhase → FinalApproachPhase → LandingPhase)
/// copying the lead's runway, direction, and altitude — after which the existing
/// <see cref="AirborneFollowHelper"/> machinery in the pattern phases takes over.
/// </summary>
public sealed class VfrFollowPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("VfrFollowPhase");

    /// <summary>Distance from the lead's downwind abeam point at which we auto-join the pattern.</summary>
    public const double JoinRangeNm = 3.0;

    /// <summary>Maximum distance follower-to-lead allowed at pattern join — guards against joining a stale pattern when the lead has moved.</summary>
    public const double MaxJoinGapNm = 5.0;

    public string TargetCallsign { get; private set; }

    public override string Name => "VFR Follow";
    public override bool ManagesSpeed => true;

    public VfrFollowPhase(string targetCallsign)
    {
        TargetCallsign = targetCallsign;
    }

    /// <summary>Update the follow target without recreating the phase.</summary>
    public void UpdateTarget(string targetCallsign)
    {
        TargetCallsign = targetCallsign;
    }

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.NavigationRoute.Clear();
        ctx.Targets.PreferredTurnDirection = null;
        Log.LogDebug("[VfrFollow] {Callsign}: following {Target}", ctx.Aircraft.Callsign, TargetCallsign);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Lead-not-found / lead-on-ground / runaway-distance checks are shared
        // with pattern-phase followers via AirborneFollowHelper.CheckLeadLifecycle.
        // It mutates Approach.FollowingCallsign + the runaway state on the follower
        // and emits the appropriate pilot transmission. When it returns true, this
        // phase has nothing left to do.
        if (AirborneFollowHelper.CheckLeadLifecycle(ctx))
        {
            return true;
        }

        // CheckLeadLifecycle already verified the lead exists.
        var lead = ctx.AircraftLookup!.Invoke(TargetCallsign)!;
        double gapNm = GeoMath.DistanceNm(ctx.Aircraft.Position, lead.Position);

        // If the lead is in a pattern, see if we're close enough to join.
        if (TryJoinLeadPattern(ctx, lead, gapNm))
        {
            // Phase list has been replaced — this phase is no longer current.
            return true;
        }

        // Free pursuit: steer toward the lead and match speed with spacing correction.
        // Altitude is deliberately not touched — the controller's last assignment stands.
        double targetBearing = GeoMath.BearingTo(ctx.Aircraft.Position, lead.Position);
        ctx.Targets.TargetTrueHeading = new TrueHeading(targetBearing);

        double minSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
        double? adjusted = AirborneFollowHelper.AdjustedFreeFlightSpeed(
            ctx.Aircraft,
            lead,
            minSpeed,
            ctx.SoloTrainingMode,
            ctx.RpoShowPilotSpeech,
            Log
        );
        if (adjusted is null)
        {
            // Helper has already added a one-shot "unable to maintain separation"
            // warning and cleared Approach.FollowingCallsign. End the phase so the
            // helper isn't re-entered every tick (which would re-spam the warning).
            return true;
        }
        ctx.Targets.TargetSpeed = adjusted;

        return false;
    }

    /// <summary>
    /// If the lead is in a pattern phase and the follower is close enough to the
    /// lead's pattern entry, rebuild the follower's phase list with a pattern
    /// circuit copying the lead's runway/direction/altitude and return true.
    /// </summary>
    private bool TryJoinLeadPattern(PhaseContext ctx, AircraftState lead, double gapToLeadNm)
    {
        // Extract pattern waypoints from the lead's current phase.
        var leadWaypoints = ExtractPatternWaypoints(lead);
        if (leadWaypoints is null)
        {
            return false;
        }

        var leadRunway = lead.Phases?.AssignedRunway;
        if (leadRunway is null)
        {
            return false;
        }

        // Gate 1: follower must be close to the lead's downwind abeam point.
        double distToEntry = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(leadWaypoints.DownwindAbeamLat, leadWaypoints.DownwindAbeamLon));
        if (distToEntry > JoinRangeNm)
        {
            return false;
        }

        // Gate 2: and reasonably close to the lead itself. Guards against joining
        // a stale pattern fix when the lead has already moved on (e.g., turning base).
        if (gapToLeadNm > MaxJoinGapNm)
        {
            return false;
        }

        // Gate 3: follower must be on the pattern side of the runway centerline.
        // A follower on the opposite side would have to cross final to reach
        // the abeam point — a real pilot would refuse, so reject the auto-join.
        if (!IsOnPatternSide(ctx.Aircraft, leadRunway, leadWaypoints.Direction))
        {
            return false;
        }

        Log.LogDebug(
            "[VfrFollow] {Callsign}: joining pattern copied from {Lead} on runway {Rwy}, direction {Dir}, dist={Dist:F2}nm",
            ctx.Aircraft.Callsign,
            TargetCallsign,
            leadRunway.Designator,
            leadWaypoints.Direction,
            distToEntry
        );

        // Build the pattern circuit using the follower's own category (spacing
        // depends on what *we* can fly, not the lead).
        var airportRunways = NavigationDatabase.Instance.GetRunways(leadRunway.AirportId);
        var circuit = PatternBuilder.BuildCircuit(
            leadRunway,
            ctx.Category,
            leadWaypoints.Direction,
            PatternEntryLeg.Downwind,
            touchAndGo: false,
            finalDistanceNm: null,
            patternSizeNm: null,
            altitudeOverrideFt: leadWaypoints.PatternAltitude,
            airportRunways: airportRunways
        );

        // If the follower is already established on the downwind leg (track
        // aligned with downwind heading and past the abeam point), skip
        // PatternEntryPhase and engage the circuit's DownwindPhase directly.
        // Routing such a follower through PatternEntryPhase would command a
        // turn toward the lead-in waypoint (which sits behind the aircraft on
        // the reciprocal heading), making it fly backward.
        double trackToDownwindDelta = ctx.Aircraft.TrueTrack.AbsAngleTo(leadWaypoints.DownwindHeading);
        double aircraftAlongTrack = GeoMath.AlongTrackDistanceNm(
            ctx.Aircraft.Position,
            new LatLon(leadWaypoints.ThresholdLat, leadWaypoints.ThresholdLon),
            leadWaypoints.DownwindHeading
        );
        double abeamAlongTrack = GeoMath.AlongTrackDistanceNm(
            new LatLon(leadWaypoints.DownwindAbeamLat, leadWaypoints.DownwindAbeamLon),
            new LatLon(leadWaypoints.ThresholdLat, leadWaypoints.ThresholdLon),
            leadWaypoints.DownwindHeading
        );
        bool alreadyOnDownwind = trackToDownwindDelta <= 30.0 && aircraftAlongTrack >= abeamAlongTrack;

        // Replace the follower's phase list entirely.
        var phases = ctx.Aircraft.Phases ?? new PhaseList();
        phases.Clear(ctx);
        ctx.Aircraft.Phases = new PhaseList
        {
            AssignedRunway = leadRunway,
            TrafficDirection = leadWaypoints.Direction,
            PatternRunway = leadRunway,
        };
        if (!alreadyOnDownwind)
        {
            TrueHeading reverseDownwind = leadWaypoints.DownwindHeading.ToReciprocal();
            var leadIn = GeoMath.ProjectPoint(leadWaypoints.DownwindAbeamLat, leadWaypoints.DownwindAbeamLon, reverseDownwind, 1.0);
            var entry = new PatternEntryPhase
            {
                EntryLat = leadWaypoints.DownwindAbeamLat,
                EntryLon = leadWaypoints.DownwindAbeamLon,
                PatternAltitude = leadWaypoints.PatternAltitude,
                Kind = PatternEntryPhase.ClassifyDownwindEntry(
                    ctx.Aircraft.Position,
                    ctx.Aircraft.TrueTrack,
                    new LatLon(leadRunway.ThresholdLatitude, leadRunway.ThresholdLongitude),
                    leadRunway.TrueHeading,
                    leadWaypoints.DownwindHeading,
                    leadWaypoints.Direction
                ),
                LeadInLat = leadIn.Lat,
                LeadInLon = leadIn.Lon,
            };
            ctx.Aircraft.Phases.Add(entry);
        }
        foreach (var p in circuit)
        {
            ctx.Aircraft.Phases.Add(p);
        }

        // Preserve the follow target so the pattern phases keep adjusting spacing.
        // Reset runaway tracking — the pattern phases use AirborneFollowHelper for
        // tighter spacing than the free-flight pursuit, so the gap dynamics that
        // applied during VfrFollowPhase no longer apply. Without this reset, a
        // follower whose gap was creeping outward by 1-2 ft / s under loose free-
        // flight spacing would carry that runaway timer into PatternEntry and trip
        // a false-positive cancel before the new spacing has time to converge.
        ctx.Aircraft.Approach.FollowingCallsign = TargetCallsign;
        AirborneFollowHelper.ResetRunawayTracking(ctx.Aircraft);
        ctx.Aircraft.Procedure.DestinationRunway = leadRunway.Designator;

        // Start the first phase in the new list.
        ctx.Aircraft.Phases.Start(ctx);
        return true;
    }

    /// <summary>
    /// Returns the lead's current pattern waypoints if the lead is in a pattern
    /// leg phase (Downwind/Base/Crosswind/Upwind). When the lead is in
    /// <see cref="PatternEntryPhase"/> — navigating to downwind abeam before
    /// the real circuit begins — the waypoints already exist on the next
    /// pattern-leg phase in the phase list (populated by
    /// <see cref="PatternBuilder.BuildCircuit"/>), so we look ahead.
    /// When the lead is on <see cref="FinalApproachPhase"/> or
    /// <see cref="LandingPhase"/> (still airborne), we look back through the
    /// completed pattern legs — all pattern-leg phases share the same
    /// <see cref="PatternWaypoints"/> instance, so the most recent completed
    /// Base/Downwind still carries it.
    /// </summary>
    private static PatternWaypoints? ExtractPatternWaypoints(AircraftState lead)
    {
        var current = lead.Phases?.CurrentPhase;
        var fromCurrent = WaypointsOf(current);
        if (fromCurrent is not null)
        {
            return fromCurrent;
        }

        if (lead.Phases is not { } phases)
        {
            return null;
        }

        if (current is PatternEntryPhase)
        {
            for (int i = phases.CurrentIndex + 1; i < phases.Phases.Count; i++)
            {
                var waypoints = WaypointsOf(phases.Phases[i]);
                if (waypoints is not null)
                {
                    return waypoints;
                }
            }
        }

        // Lead on final or rolling out (still airborne) — look back for the
        // most recent pattern leg whose waypoints are still attached.
        if ((current is FinalApproachPhase || current is LandingPhase) && !lead.IsOnGround)
        {
            for (int i = phases.CurrentIndex - 1; i >= 0; i--)
            {
                var waypoints = WaypointsOf(phases.Phases[i]);
                if (waypoints is not null)
                {
                    return waypoints;
                }
            }
        }

        return null;
    }

    private static PatternWaypoints? WaypointsOf(Phase? phase) =>
        phase switch
        {
            DownwindPhase d => d.Waypoints,
            BasePhase b => b.Waypoints,
            CrosswindPhase c => c.Waypoints,
            UpwindPhase u => u.Waypoints,
            _ => null,
        };

    /// <summary>
    /// Returns true if <paramref name="follower"/> is on the same side of the
    /// runway centerline as the pattern (the side the downwind lies on).
    /// A left pattern has downwind to the left of the runway when viewed in the
    /// direction of landing; follower must be on that same side.
    /// </summary>
    private static bool IsOnPatternSide(AircraftState follower, RunwayInfo runway, PatternDirection direction)
    {
        // Signed cross-track distance from the runway centerline: positive = right
        // of runway heading, negative = left.
        double crossTrack = GeoMath.SignedCrossTrackDistanceNm(
            follower.Position,
            new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            runway.TrueHeading
        );
        return direction == PatternDirection.Left ? crossTrack <= 0 : crossTrack >= 0;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Follow => CommandAcceptance.Allowed,
            // Altitude/speed adjustments don't cancel the follow — controllers
            // adjust trailing-aircraft separation without breaking the visual.
            CanonicalCommandType.ClimbMaintain
            or CanonicalCommandType.DescendMaintain
            or CanonicalCommandType.Speed
            or CanonicalCommandType.Mach
            or CanonicalCommandType.ReduceToFinalApproachSpeed
            or CanonicalCommandType.ResumeNormalSpeed
            or CanonicalCommandType.DeleteSpeedRestrictions => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            // Any other command (heading/pattern-leg/etc.) clears this phase
            // and hands control back to the controller's direct targets.
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new VfrFollowPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            TargetCallsign = TargetCallsign,
        };

    public static VfrFollowPhase FromSnapshot(VfrFollowPhaseDto dto)
    {
        var phase = new VfrFollowPhase(dto.TargetCallsign) { Status = (PhaseStatus)dto.Status, ElapsedSeconds = dto.ElapsedSeconds };
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
