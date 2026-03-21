using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Upwind leg: climb from runway heading to pattern altitude,
/// continue past departure end to crosswind turn point.
/// Completes when reaching the crosswind turn waypoint.
/// </summary>
public sealed class UpwindPhase : Phase
{
    private const double ArrivalNm = 0.3;

    private double _targetLat;
    private double _targetLon;
    private TrueHeading _upwindHeading;
    private double _minTurnAltitude;

    public PatternWaypoints? Waypoints { get; set; }

    /// <summary>
    /// When true, the aircraft continues on the current heading past the turn point
    /// until a turn command (TC) or another EXT-clearing command is issued.
    /// </summary>
    public bool IsExtended { get; set; }

    public override string Name => "Upwind";

    public override void OnStart(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return;
        }

        _targetLat = Waypoints.CrosswindTurnLat;
        _targetLon = Waypoints.CrosswindTurnLon;
        _upwindHeading = Waypoints.UpwindHeading;
        _minTurnAltitude = Waypoints.PatternAltitude - 300;

        ctx.Targets.TargetTrueHeading = Waypoints.UpwindHeading;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.TurnRateOverride = CategoryPerformance.PatternTurnRate(ctx.Category);
        ctx.Targets.NavigationRoute.Clear();

        // Climb to pattern altitude
        ctx.Targets.TargetAltitude = Waypoints.PatternAltitude;
        ctx.Targets.DesiredVerticalRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);

        // Accelerate toward downwind speed
        ctx.Targets.TargetSpeed = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);

        ctx.Logger.LogDebug(
            "[Upwind] {Callsign}: started, hdg={Hdg:F0}, patternAlt={Alt:F0}ft, extended={Ext}",
            ctx.Aircraft.Callsign,
            Waypoints.UpwindHeading.Degrees,
            Waypoints.PatternAltitude,
            IsExtended
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (IsExtended)
        {
            return false;
        }

        double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);

        // Check if the aircraft has already passed the crosswind turn point.
        // After takeoff + initial climb, the aircraft may be past it.
        // Detect this by checking if the bearing to the target is behind us
        // (more than 90° off our upwind heading).
        double bearingToTarget = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);
        double bearingDiff = Math.Abs(GeoMath.SignedBearingDifference(bearingToTarget, _upwindHeading.Degrees));
        bool targetIsBehind = bearingDiff > 90.0;

        // AIM 4-3-2: crosswind turn requires being within 300ft of pattern altitude
        bool altitudeReached = ctx.Aircraft.Altitude >= _minTurnAltitude;
        bool complete = (dist < ArrivalNm || targetIsBehind) && altitudeReached;
        if (complete)
        {
            ctx.Logger.LogDebug(
                "[Upwind] {Callsign}: crosswind turn point {Reason}, alt={Alt:F0}ft",
                ctx.Aircraft.Callsign,
                targetIsBehind ? "passed (behind aircraft)" : "reached",
                ctx.Aircraft.Altitude
            );
        }

        return complete;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ClearedToLand => CommandAcceptance.Allowed,
            CanonicalCommandType.LandAndHoldShort => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForOption => CommandAcceptance.Allowed,
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new UpwindPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            Waypoints = Waypoints?.ToSnapshot(),
            IsExtended = IsExtended,
            TargetLat = _targetLat,
            TargetLon = _targetLon,
            UpwindHeadingDeg = _upwindHeading.Degrees,
            MinTurnAltitude = _minTurnAltitude,
        };

    public static UpwindPhase FromSnapshot(UpwindPhaseDto dto)
    {
        var phase = new UpwindPhase
        {
            Waypoints = dto.Waypoints is not null ? PatternWaypoints.FromSnapshot(dto.Waypoints) : null,
            IsExtended = dto.IsExtended,
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase._targetLat = dto.TargetLat;
        phase._targetLon = dto.TargetLon;
        phase._upwindHeading = new TrueHeading(dto.UpwindHeadingDeg);
        phase._minTurnAltitude = dto.MinTurnAltitude;
        return phase;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
