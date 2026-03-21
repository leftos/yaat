using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Crosswind leg: turn from upwind heading to crosswind heading,
/// fly to downwind start point. Continues climb to pattern altitude.
/// Completes when reaching the downwind start waypoint.
/// </summary>
public sealed class CrosswindPhase : Phase
{
    private const double ArrivalNm = 0.3;

    private double _targetLat;
    private double _targetLon;
    private TrueHeading _crosswindHeading;

    public PatternWaypoints? Waypoints { get; set; }

    /// <summary>
    /// When true, the aircraft continues on the current heading past the turn point
    /// until a turn command (TD) or another EXT-clearing command is issued.
    /// </summary>
    public bool IsExtended { get; set; }

    public override string Name => "Crosswind";

    public override void OnStart(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return;
        }

        _targetLat = Waypoints.DownwindStartLat;
        _targetLon = Waypoints.DownwindStartLon;
        _crosswindHeading = Waypoints.CrosswindHeading;

        var turnDir = Waypoints.Direction == PatternDirection.Left ? TurnDirection.Left : TurnDirection.Right;

        ctx.Targets.TargetTrueHeading = Waypoints.CrosswindHeading;
        ctx.Targets.PreferredTurnDirection = turnDir;
        ctx.Targets.TurnRateOverride = CategoryPerformance.PatternTurnRate(ctx.Category);
        ctx.Targets.NavigationRoute.Clear();

        // Continue climbing to pattern altitude if not there yet
        if (ctx.Aircraft.Altitude < Waypoints.PatternAltitude - 50)
        {
            ctx.Targets.TargetAltitude = Waypoints.PatternAltitude;
            ctx.Targets.DesiredVerticalRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);
        }

        ctx.Logger.LogDebug(
            "[Crosswind] {Callsign}: started, hdg={Hdg:F0}, alt={Alt:F0}ft",
            ctx.Aircraft.Callsign,
            Waypoints.CrosswindHeading.Degrees,
            ctx.Aircraft.Altitude
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (IsExtended)
        {
            return false;
        }

        double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);

        // Check if the aircraft has already passed the downwind start point.
        // Detect by checking if the bearing to the target is behind us
        // (more than 90° off our crosswind heading).
        double bearingToTarget = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _targetLat, _targetLon);
        double bearingDiff = Math.Abs(GeoMath.SignedBearingDifference(bearingToTarget, _crosswindHeading.Degrees));
        bool targetIsBehind = bearingDiff > 90.0;

        bool complete = dist < ArrivalNm || targetIsBehind;
        if (complete)
        {
            ctx.Logger.LogDebug(
                "[Crosswind] {Callsign}: downwind start {Reason}, alt={Alt:F0}ft",
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
        new CrosswindPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            Waypoints = Waypoints?.ToSnapshot(),
            IsExtended = IsExtended,
            TargetLat = _targetLat,
            TargetLon = _targetLon,
            CrosswindHeadingDeg = _crosswindHeading.Degrees,
        };

    public static CrosswindPhase FromSnapshot(CrosswindPhaseDto dto)
    {
        var phase = new CrosswindPhase
        {
            Waypoints = dto.Waypoints is not null ? PatternWaypoints.FromSnapshot(dto.Waypoints) : null,
            IsExtended = dto.IsExtended,
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase._targetLat = dto.TargetLat;
        phase._targetLon = dto.TargetLon;
        phase._crosswindHeading = new TrueHeading(dto.CrosswindHeadingDeg);
        return phase;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
