using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Crosses midfield from the wrong side to the correct pattern side. Per
/// AIM 4-3-3.1.b and AC 90-66B §11.3-§11.4: small pistons/helicopters cross
/// at pattern altitude (1000 AGL); large and turbine-powered aircraft cross
/// at pattern altitude + 500 ft. Turboprops/jets are handed off to
/// <see cref="TeardropReentryPhase"/> afterward to descend to TPA; pistons
/// and helicopters drop directly into <see cref="DownwindPhase"/>.
/// </summary>
public sealed class MidfieldCrossingPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("MidfieldCrossingPhase");

    private const double ArrivalNm = 0.5;
    private const double LargeTurbineAltitudeOffsetFt = 500.0;

    private double _targetLat;
    private double _targetLon;

    public PatternWaypoints? Waypoints { get; set; }

    /// <summary>
    /// When true, the initial join turn is biased toward the assigned pattern side
    /// (<see cref="PatternWaypoints.Direction"/>) rather than the geometric shortest way.
    /// Set for the cross-runway departure join (climb out on the departure runway, then
    /// turn the correct way onto the pattern runway's downwind). Left false for arrival /
    /// wrong-side joins, which keep their established shortest-turn behavior.
    /// </summary>
    public bool BiasTurnToPatternSide { get; init; }

    public override string Name => "MidfieldCrossing";
    public override bool ManagesSpeed => true;

    public override void OnStart(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return;
        }

        // Target: midfield point on the correct side (midpoint of downwind leg)
        _targetLat = (Waypoints.DownwindStartLat + Waypoints.DownwindAbeamLat) / 2.0;
        _targetLon = (Waypoints.DownwindStartLon + Waypoints.DownwindAbeamLon) / 2.0;

        // Set heading toward midfield target
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(_targetLat, _targetLon));
        ctx.Targets.TargetTrueHeading = new TrueHeading(bearing);
        // Cross-runway departure join: bias the initial turn toward the assigned pattern
        // side so the aircraft turns the correct way onto the pattern instead of the
        // geometric shortest way (which can roll across the extended centerline /
        // departure path the wrong direction — AIM 4-3-3, 4-3-5). Released in OnTick once
        // roughly pointed at the target; DownwindPhase clears it on entry regardless.
        // Arrival / wrong-side joins keep their established shortest-turn behavior.
        ctx.Targets.PreferredTurnDirection = BiasTurnToPatternSide
            ? (Waypoints.Direction == PatternDirection.Left ? TurnDirection.Left : TurnDirection.Right)
            : null;
        ctx.Targets.NavigationRoute.Clear();

        // Large/turbine cross at TPA+500 (AIM 4-3-3.1.b); pistons/helicopters
        // cross at pattern altitude (AC 90-66B §11.3-§11.4).
        double crossingAlt = ctx.Category is AircraftCategory.Jet or AircraftCategory.Turboprop
            ? Waypoints.PatternAltitude + LargeTurbineAltitudeOffsetFt
            : Waypoints.PatternAltitude;
        ctx.Targets.TargetAltitude = crossingAlt;
        ctx.Targets.DesiredVerticalRate = null;

        // Downwind speed for the category
        ctx.Targets.TargetSpeed = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);

        Log.LogDebug("[MidfieldCrossing] {Callsign}: started, cat={Cat}, crossingAlt={Alt:F0}ft", ctx.Aircraft.Callsign, ctx.Category, crossingAlt);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Continuously update heading toward the midfield target
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(_targetLat, _targetLon));
        ctx.Targets.TargetTrueHeading = new TrueHeading(bearing);

        // Once roughly pointed at the target, release the initial-turn bias so ongoing
        // small corrections take the natural (shortest) direction and a moving bearing
        // can't force a wrong-way loop.
        if (
            (ctx.Targets.PreferredTurnDirection is not null)
            && (Math.Abs(GeoMath.SignedBearingDifference(ctx.Aircraft.TrueHeading.Degrees, bearing)) < 30.0)
        )
        {
            ctx.Targets.PreferredTurnDirection = null;
        }

        double dist = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(_targetLat, _targetLon));

        bool complete = dist < ArrivalNm;
        if (complete)
        {
            Log.LogDebug("[MidfieldCrossing] {Callsign}: midfield reached, transitioning to downwind", ctx.Aircraft.Callsign);
        }

        return complete;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return CommandAcceptance.ClearsPhase;
    }

    public override PhaseDto ToSnapshot() =>
        new MidfieldCrossingPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            Waypoints = Waypoints?.ToSnapshot(),
            TargetLat = _targetLat,
            TargetLon = _targetLon,
            BiasTurnToPatternSide = BiasTurnToPatternSide,
        };

    public static MidfieldCrossingPhase FromSnapshot(MidfieldCrossingPhaseDto dto)
    {
        var phase = new MidfieldCrossingPhase
        {
            Waypoints = dto.Waypoints is not null ? PatternWaypoints.FromSnapshot(dto.Waypoints) : null,
            BiasTurnToPatternSide = dto.BiasTurnToPatternSide,
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase._targetLat = dto.TargetLat;
        phase._targetLon = dto.TargetLon;
        return phase;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
