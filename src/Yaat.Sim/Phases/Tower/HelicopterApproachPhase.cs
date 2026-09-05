using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Helicopter approach to a landing spot from off the airport: the arrival half of a landing
/// clearance to a helipad, ramp, or taxiway point (7110.65 §3-11-6 — "issue a landing clearance
/// in lieu of extended hover-taxi or air-taxi operations"). Air taxi (<see cref="AirTaxiPhase"/>)
/// is a ground movement (AIM §4-3-17.b: "Taxi, hover taxi, and air taxi operations are considered
/// to be ground movements"; 7110.65 §3-11-1.c NOTE: the method for helicopter movements *on
/// airports*; §3-11-3 NOTE corroborates), so an inbound helicopter miles out must not fly it:
/// this phase holds the present altitude direct to the spot (AIM §4-3-17.c.4 — ATC routes
/// helicopters direct to land as near as possible to the destination), descends to the
/// rotorcraft pattern altitude ahead of the field, then flies a 6° final onto the spot at 60 kt
/// and decelerates to a hover. It hands off to <see cref="HelicopterLandingPhase"/> stopped over
/// the spot at the air-taxi height, the same contract as the air taxi it replaces. The
/// publications give no VFR descent gradient or angle: the 90 kt approach ceiling and the 60 kt
/// final are the IFR copter figures (AIM §10-1-2.b.2/.b.3) applied by analogy, the 400 ft/nm
/// transit gradient is the copter climb gradient (AIM §10-1-5.a, §5-4-21.b) used as the nominal
/// descent — steepened as needed to capture the path — and the 6° final is the sim's helicopter
/// glidepath (<see cref="GlideSlopeGeometry.HelicopterAngleDeg"/>).
/// </summary>
public sealed class HelicopterApproachPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("HelicopterApproachPhase");

    private const double ArrivalThresholdNm = 0.01;
    private const double ArrivalHeightToleranceFt = 50.0;
    private const double BrakeStartNm = 0.25;
    private const double LevelOffBufferNm = 1.0;
    private const double FinalSpeedLeadNm = 0.4;
    private const double MinPathCaptureDistanceNm = 0.1;
    private const double SlowSpeedVerticalCapKts = 30.0;
    private const double SlowSpeedDescentCapFpm = 300.0;
    private const double MinFinalDescentFpm = 150.0;
    private const double LogIntervalSeconds = 3.0;

    private readonly double _targetLat;
    private readonly double _targetLon;
    private readonly string? _destinationName;

    private double _fieldElevation;
    private double _holdAltitude;
    private double _timeSinceLastLog;

    public override string Name => "Approach-H";

    /// <summary>The phase owns speed for the whole approach: cruise, the 90/60 kt schedule, and the deceleration to a hover.</summary>
    public override bool ManagesSpeed => true;

    public HelicopterApproachPhase(double targetLat, double targetLon, string? destinationName)
    {
        _targetLat = targetLat;
        _targetLon = targetLon;
        _destinationName = destinationName;
    }

    /// <summary>
    /// Distance from the spot (nm) at which the 6° final begins: the drop from the rotorcraft pattern
    /// altitude to the air-taxi height over the spot along the helicopter glidepath.
    /// </summary>
    internal static double FinalStartNm(AircraftCategory category)
    {
        double dropFt = CategoryPerformance.PatternAltitudeAgl(AircraftCategory.Helicopter) - CategoryPerformance.AirTaxiAltitudeAgl(category);
        return dropFt / GlideSlopeGeometry.FeetPerNm(GlideSlopeGeometry.HelicopterAngleDeg);
    }

    /// <summary>
    /// Distance from the spot (nm) at which the descent from the held altitude to the rotorcraft
    /// pattern altitude begins: the drop at the transit gradient, plus a level buffer before final.
    /// </summary>
    internal static double TopOfDescentNm(double holdAltitude, double fieldElevation)
    {
        double patternAltitude = fieldElevation + CategoryPerformance.PatternAltitudeAgl(AircraftCategory.Helicopter);
        double dropFt = Math.Max(0, holdAltitude - patternAltitude);
        return (dropFt / CategoryPerformance.HelicopterTransitDescentFtPerNm) + LevelOffBufferNm;
    }

    public override void OnStart(PhaseContext ctx)
    {
        // Drop stale steering targets left by whatever the heli was doing before (a hover hold, a
        // heading, an assigned altitude) so the bearing steer below is the only heading authority.
        ctx.Targets.TargetTrueHeading = null;
        ctx.Targets.AssignedMagneticHeading = null;
        ctx.Targets.AssignedAltitude = null;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();

        _fieldElevation = ctx.FieldElevation;
        // Hold what the aircraft has: a VFR helicopter picks its own altitude and a landing clearance
        // is authority to land, not an instruction to descend now. Never below the air-taxi height it
        // will arrive at.
        _holdAltitude = Math.Max(ctx.Aircraft.Altitude, _fieldElevation + CategoryPerformance.AirTaxiAltitudeAgl(ctx.Category));
        ctx.Targets.TargetAltitude = _holdAltitude;
        ctx.Targets.DesiredVerticalRate = null;

        ctx.Targets.TargetSpeed = EnRouteSpeed(ctx);

        Log.LogDebug(
            "[Approach-H] {Callsign}: started → {Dest} at ({Lat:F6},{Lon:F6}), hold={Hold:F0}ft, field={Field:F0}ft, "
                + "tod={Tod:F2}nm, final={Final:F2}nm",
            ctx.Aircraft.Callsign,
            _destinationName ?? "direct",
            _targetLat,
            _targetLon,
            _holdAltitude,
            _fieldElevation,
            TopOfDescentNm(_holdAltitude, _fieldElevation),
            FinalStartNm(ctx.Category)
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (ctx.Aircraft.Ground.IsImmobile)
        {
            ctx.Targets.TargetSpeed = 0;
            return false;
        }

        var target = new LatLon(_targetLat, _targetLon);
        double dist = GeoMath.DistanceNm(ctx.Aircraft.Position, target);
        double finalStart = FinalStartNm(ctx.Category);

        ctx.Targets.TargetTrueHeading = new TrueHeading(GeoMath.BearingTo(ctx.Aircraft.Position, target));
        ApplySteerTurnRate(ctx);
        ApplySpeedSchedule(ctx, dist, finalStart);
        ApplyVerticalProfile(ctx, dist, finalStart);

        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            Log.LogTrace(
                "[Approach-H] {Callsign}: dist={Dist:F2}nm alt={Alt:F0} tgtAlt={TAlt:F0} vs={Vs:F0} gs={Gs:F0} tgtSpd={Tgt:F0}",
                ctx.Aircraft.Callsign,
                dist,
                ctx.Aircraft.Altitude,
                ctx.Targets.TargetAltitude ?? 0,
                ctx.Targets.DesiredVerticalRate ?? 0,
                ctx.Aircraft.GroundSpeed,
                ctx.Targets.TargetSpeed ?? 0
            );
        }

        // Complete only stopped over the spot AND down at the air-taxi height: a clearance received
        // inside top of descent can leave the helicopter high over the spot, and the landing phase's
        // vertical descent must not inherit hundreds of feet — the profile brings it down here first.
        double agl = ctx.Aircraft.Altitude - _fieldElevation;
        bool atArrivalHeight = agl <= CategoryPerformance.AirTaxiAltitudeAgl(ctx.Category) + ArrivalHeightToleranceFt;
        if ((dist <= ArrivalThresholdNm) && (ctx.Aircraft.GroundSpeed <= 2.0) && atArrivalHeight)
        {
            Log.LogDebug("[Approach-H] {Callsign}: over {Dest}, hovering for the landing", ctx.Aircraft.Callsign, _destinationName ?? "destination");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Cruise to the slow-down point, at most 90 kt from there to final (the copter approach-speed
    /// ceiling), the final speed from shortly before the final gate so the aircraft is stabilised on
    /// the path rather than shedding speed on it, then a linear deceleration to a hover across the
    /// brake zone. A controller speed assignment is the en-route speed: a landing clearance is not an
    /// approach clearance, so 7110.65 §5-7-1.d does not cancel it, and a spot arrival has no runway
    /// for §5-7-1.b.4's five-mile point — the schedule here does that job. The final speed never
    /// exceeds a standing assignment (§5-7-1: avoid alternating decreases and increases).
    /// </summary>
    private static void ApplySpeedSchedule(PhaseContext ctx, double dist, double finalStart)
    {
        double enRoute = EnRouteSpeed(ctx);
        double finalSpeed = Math.Min(CategoryPerformance.HelicopterFinalSpeedKts, enRoute);
        if (dist <= BrakeStartNm)
        {
            ctx.Targets.TargetSpeed = finalSpeed * (dist / BrakeStartNm);
            return;
        }

        if (dist <= finalStart + FinalSpeedLeadNm)
        {
            ctx.Targets.TargetSpeed = finalSpeed;
            return;
        }

        ctx.Targets.TargetSpeed =
            (dist <= CategoryPerformance.HelicopterApproachSlowDownNm)
                ? Math.Min(enRoute, CategoryPerformance.HelicopterApproachSpeedMaxKts)
                : enRoute;
    }

    /// <summary>
    /// Level at the held altitude until top of descent, descend to the rotorcraft pattern altitude at
    /// the transit gradient — steepened when the clearance arrived inside top of descent so the pattern
    /// altitude is still captured before the final gate — then ride the 6° glidepath that reaches the
    /// air-taxi height over the spot. Vertical rates follow from gradient × groundspeed so the path is
    /// speed-consistent; below 30 kt the rate is capped (vortex-ring guard) and it never asks for more
    /// than the category descent rate.
    /// </summary>
    private void ApplyVerticalProfile(PhaseContext ctx, double dist, double finalStart)
    {
        double groundSpeedNmPerMin = ctx.Aircraft.GroundSpeed / 60.0;
        double maxRate = CategoryPerformance.DescentRate(ctx.Category);
        double patternAltitude = _fieldElevation + CategoryPerformance.PatternAltitudeAgl(AircraftCategory.Helicopter);

        if (dist > TopOfDescentNm(_holdAltitude, _fieldElevation))
        {
            ctx.Targets.TargetAltitude = _holdAltitude;
            ctx.Targets.DesiredVerticalRate = null;
            return;
        }

        if (dist > finalStart)
        {
            ctx.Targets.TargetAltitude = Math.Min(_holdAltitude, patternAltitude);
            double remainingDropFt = Math.Max(0, ctx.Aircraft.Altitude - ctx.Targets.TargetAltitude.Value);
            double captureGradient = remainingDropFt / Math.Max(dist - finalStart, MinPathCaptureDistanceNm);
            double gradient = Math.Max(CategoryPerformance.HelicopterTransitDescentFtPerNm, captureGradient);
            double transitRate = gradient * groundSpeedNmPerMin;
            ctx.Targets.DesiredVerticalRate = (remainingDropFt > 0) ? -Math.Min(Math.Max(transitRate, MinFinalDescentFpm), maxRate) : null;
            return;
        }

        double airTaxiAltitude = _fieldElevation + CategoryPerformance.AirTaxiAltitudeAgl(ctx.Category);
        double pathAltitude = GlideSlopeGeometry.AltitudeAtDistance(
            dist,
            _fieldElevation,
            CategoryPerformance.AirTaxiAltitudeAgl(ctx.Category),
            GlideSlopeGeometry.HelicopterAngleDeg
        );
        // Ride the path down; an aircraft already below it holds what it has rather than climbing back up.
        ctx.Targets.TargetAltitude = Math.Max(airTaxiAltitude, Math.Min(pathAltitude, Math.Max(ctx.Aircraft.Altitude, airTaxiAltitude)));

        double pathRate = GlideSlopeGeometry.RequiredDescentRate(ctx.Aircraft.GroundSpeed, GlideSlopeGeometry.HelicopterAngleDeg);
        double rate = Math.Min(Math.Max(pathRate, MinFinalDescentFpm), maxRate);
        if (ctx.Aircraft.GroundSpeed < SlowSpeedVerticalCapKts)
        {
            rate = Math.Min(rate, SlowSpeedDescentCapFpm);
        }

        ctx.Targets.DesiredVerticalRate = (ctx.Aircraft.Altitude > ctx.Targets.TargetAltitude) ? -rate : null;
    }

    /// <summary>
    /// Speed for the transit: the controller's assigned speed while one stands, otherwise the type's
    /// level cruise.
    /// </summary>
    private static double EnRouteSpeed(PhaseContext ctx)
    {
        if ((ctx.Targets.HasExplicitSpeedCommand) && (ctx.Targets.AssignedSpeed is { } assigned) && (assigned > 0))
        {
            return assigned;
        }

        return AircraftPerformance.DefaultSpeed(ctx.AircraftType, ctx.Category, ctx.Aircraft.Altitude, null);
    }

    private static void ApplySteerTurnRate(PhaseContext ctx)
    {
        if (ctx.Targets.HasExplicitTurnRate)
        {
            return;
        }

        ctx.Targets.TurnRateOverride = AirTaxiPhase.SteerTurnRate(ctx.Category, ctx.Aircraft.GroundSpeed);
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        if (!ctx.Targets.HasExplicitTurnRate)
        {
            ctx.Targets.TurnRateOverride = null;
        }

        Log.LogDebug("[Approach-H] {Callsign}: ended ({Status})", ctx.Aircraft.Callsign, endStatus);
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // Any airborne manoeuvring command pulls the heli off the approach and hands control to the
        // command queue, exactly as for an air taxi; HPP and a re-issued ATXI/LAND are routed by the
        // dispatcher's tower-command path before this gate.
        return CommandAcceptance.ClearsPhase;
    }

    public override PhaseDto ToSnapshot() =>
        new HelicopterApproachPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            TargetLat = _targetLat,
            TargetLon = _targetLon,
            DestinationName = _destinationName,
            FieldElevation = _fieldElevation,
            HoldAltitude = _holdAltitude,
            TimeSinceLastLog = _timeSinceLastLog,
        };

    public static HelicopterApproachPhase FromSnapshot(HelicopterApproachPhaseDto dto)
    {
        var phase = new HelicopterApproachPhase(dto.TargetLat, dto.TargetLon, dto.DestinationName);
        phase._fieldElevation = dto.FieldElevation;
        phase._holdAltitude = dto.HoldAltitude;
        phase._timeSinceLastLog = dto.TimeSinceLastLog;
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
