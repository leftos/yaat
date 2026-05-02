using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Classifies how a pattern entry is joining the traffic pattern, so the client
/// can render informative status text (e.g. "direct left downwind 28R" vs
/// "45 to left downwind 28R"). Computed at phase construction by the caller.
/// </summary>
public enum PatternEntryKind
{
    /// <summary>Joining downwind from roughly along the downwind course (≤30° angular delta).</summary>
    Direct,

    /// <summary>Classic 45° intercept to downwind (AIM 4-3-3), >30° up to 75°.</summary>
    FortyFive,

    /// <summary>Entering downwind from a crosswind-like angle (>75°), typically after a teardrop or from the opposite side.</summary>
    Crosswind,

    /// <summary>Joining on the upwind leg (parallel to departure end), used for go-around re-entry and wrong-side corrections.</summary>
    Upwind,

    /// <summary>Joining directly onto base.</summary>
    Base,

    /// <summary>Joining directly onto final (straight-in).</summary>
    Final,
}

/// <summary>
/// Navigates an airborne aircraft to a pattern entry point, descending to pattern
/// altitude and decelerating to pattern speed. Inserted before the first pattern
/// leg phase (downwind, base, etc.) when the aircraft is far from the pattern.
/// Completes when the entry point is reached (NavigationRoute drained by FlightPhysics).
/// </summary>
public sealed class PatternEntryPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("PatternEntryPhase");

    public required double EntryLat { get; init; }
    public required double EntryLon { get; init; }
    public required double PatternAltitude { get; init; }

    /// <summary>
    /// How the aircraft is joining the pattern. Set by the caller at construction
    /// based on aircraft track vs downwind course and the target entry leg.
    /// </summary>
    public required PatternEntryKind Kind { get; init; }

    /// <summary>
    /// Optional lead-in waypoint placed before the entry point so the aircraft
    /// aligns with the leg heading before reaching the entry point.
    /// </summary>
    public double? LeadInLat { get; init; }
    public double? LeadInLon { get; init; }

    private bool _hasAnnouncedInitialCall;

    public override string Name => "Pattern Entry";
    public override bool ManagesSpeed => true;

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Targets.NavigationRoute.Clear();

        if (LeadInLat is not null && LeadInLon is not null)
        {
            ctx.Targets.NavigationRoute.Add(new NavigationTarget { Position = new LatLon(LeadInLat.Value, LeadInLon.Value), Name = "PTN-LEADIN" });
        }

        ctx.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Position = new LatLon(EntryLat, EntryLon),
                Name = "PTN-ENTRY",
                AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, (int)PatternAltitude),
            }
        );
        ctx.Targets.TargetTrueHeading = null;
        if (!ctx.Targets.HasExplicitTurnRate)
        {
            ctx.Targets.TurnRateOverride = null;
        }
        ctx.Targets.PreferredTurnDirection = null;

        // Set target altitude; UpdateDescentPlanning in FlightPhysics computes
        // the required descent rate from the AltitudeRestriction on PTN-ENTRY.
        ctx.Targets.TargetAltitude = PatternAltitude;
        if (ctx.Aircraft.Altitude < PatternAltitude - 100)
        {
            ctx.Targets.DesiredVerticalRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);
        }

        // Decelerate toward pattern speed
        ctx.Targets.TargetSpeed = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);

        double dist = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(EntryLat, EntryLon));
        Log.LogDebug(
            "[PatternEntry] {Callsign}: navigating to entry, dist={Dist:F1}nm, alt={Alt:F0}ft, tgtAlt={TgtAlt:F0}ft",
            ctx.Aircraft.Callsign,
            dist,
            ctx.Aircraft.Altitude,
            PatternAltitude
        );

        // Initial-contact closed-traffic request for VFR pattern aircraft. Anchors distance/bearing
        // to the runway threshold (closest stable airport reference available to the phase).
        if (
            ctx.SoloTrainingMode
            && ctx.Aircraft.FlightPlan.IsVfr
            && !_hasAnnouncedInitialCall
            && !ctx.Aircraft.HasMadeInitialContact
            && ctx.Runway is not null
        )
        {
            var airportPos = new LatLon(ctx.Runway.ThresholdLatitude, ctx.Runway.ThresholdLongitude);
            int altitudeFt = (int)Math.Round(ctx.Aircraft.Altitude);
            ctx.Aircraft.PendingNotifications.Add(PilotResponder.BuildClosedTrafficRequest(ctx.Aircraft, airportPos, altitudeFt));
            _hasAnnouncedInitialCall = true;
            ctx.Aircraft.HasMadeInitialContact = true;
        }
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // If the aircraft is following, close the gap during entry rather
        // than waiting until DownwindPhase to engage spacing control. Use
        // free-flight desired spacing (wider than pattern-tight) because the
        // aircraft isn't on a pattern leg yet — real pilots don't tighten to
        // pattern rhythm until they're actually on the downwind. DownwindSpeed
        // is the fixed baseline each tick; feeding the previous TargetSpeed
        // back in would compound the ±20 kt clamp over ticks.
        if (ctx.Targets.TargetSpeed is not null)
        {
            double normalSpeed = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);
            double minSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            var adjusted = AirborneFollowHelper.GetAdjustedSpeedFreeFlight(ctx, normalSpeed, minSpeed);
            if (adjusted is not null)
            {
                ctx.Targets.TargetSpeed = adjusted.Value;
            }
        }

        // FlightPhysics drains NavigationRoute as waypoints are reached
        return ctx.Targets.NavigationRoute.Count == 0;
    }

    public override PhaseDto ToSnapshot() =>
        new PatternEntryPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            EntryLat = EntryLat,
            EntryLon = EntryLon,
            PatternAltitude = PatternAltitude,
            Kind = (int)Kind,
            LeadInLat = LeadInLat,
            LeadInLon = LeadInLon,
            HasAnnouncedInitialCall = _hasAnnouncedInitialCall,
        };

    public static PatternEntryPhase FromSnapshot(PatternEntryPhaseDto dto)
    {
        var phase = new PatternEntryPhase
        {
            EntryLat = dto.EntryLat,
            EntryLon = dto.EntryLon,
            PatternAltitude = dto.PatternAltitude,
            Kind = (PatternEntryKind)dto.Kind,
            LeadInLat = dto.LeadInLat,
            LeadInLon = dto.LeadInLon,
            _hasAnnouncedInitialCall = dto.HasAnnouncedInitialCall,
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        return phase;
    }

    /// <summary>
    /// Classifies a downwind entry by the angular delta between the aircraft's
    /// current track and the downwind course. Thresholds reflect AIM 4-3-3:
    /// the recommended 45° entry lives in roughly 20°–60° off-course; below
    /// 20° the aircraft is effectively along downwind (direct); above 60°
    /// the geometry is more perpendicular than a 45° intercept (crosswind).
    /// For non-downwind target legs, callers should pass the leg directly
    /// (Upwind/Base/Final) rather than calling this.
    /// </summary>
    public static PatternEntryKind ClassifyDownwindEntry(TrueHeading aircraftTrack, TrueHeading downwindCourse)
    {
        double delta = aircraftTrack.AbsAngleTo(downwindCourse);
        if (delta <= 20.0)
        {
            return PatternEntryKind.Direct;
        }
        if (delta <= 60.0)
        {
            return PatternEntryKind.FortyFive;
        }
        return PatternEntryKind.Crosswind;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ClearedToLand => CommandAcceptance.Allowed,
            CanonicalCommandType.LandAndHoldShort => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForOption => CommandAcceptance.Allowed,
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.Follow => CommandAcceptance.Allowed,
            CanonicalCommandType.MakeShortApproach => CommandAcceptance.Allowed,
            CanonicalCommandType.MakeNormalApproach => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.ClearsPhase,
        };
    }
}
