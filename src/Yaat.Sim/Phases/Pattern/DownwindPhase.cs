using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Downwind leg: fly opposite runway heading at pattern altitude.
/// Maintains downwind speed, level flight.
/// Completes when reaching the base turn waypoint.
/// </summary>
public sealed class DownwindPhase : Phase
{
    private const double ArrivalNm = 0.3;
    private const double AbeamThresholdNm = 0.3;

    /// <summary>Feet of altitude per nm for a 3° glideslope (standard rule of thumb).</summary>
    private const double GlideslopeFtPerNm = 300.0;

    private double _targetLat;
    private double _targetLon;
    private bool _pastAbeam;
    private double _altitudeFloor;

    public PatternWaypoints? Waypoints { get; set; }

    /// <summary>
    /// If true, the downwind leg is extended beyond the normal base turn point.
    /// Aircraft continues on downwind heading until told to turn base (TB command).
    /// </summary>
    public bool IsExtended { get; set; }

    public override string Name => "Downwind";

    public override void OnStart(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return;
        }

        _targetLat = Waypoints.BaseTurnLat;
        _targetLon = Waypoints.BaseTurnLon;
        _pastAbeam = false;

        var turnDir = Waypoints.Direction == PatternDirection.Left
            ? TurnDirection.Left : TurnDirection.Right;

        ctx.Targets.TargetHeading = Waypoints.DownwindHeading;
        ctx.Targets.PreferredTurnDirection = turnDir;
        ctx.Targets.NavigationRoute.Clear();

        // Level off at pattern altitude
        ctx.Targets.TargetAltitude = Waypoints.PatternAltitude;
        ctx.Targets.DesiredVerticalRate = null;

        // Downwind speed
        ctx.Targets.TargetSpeed = CategoryPerformance.DownwindSpeed(ctx.Category);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Begin descent when abeam the approach end of the runway
        if (!_pastAbeam && Waypoints is not null)
        {
            double distToAbeam = FlightPhysics.DistanceNm(
                ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
                Waypoints.DownwindAbeamLat, Waypoints.DownwindAbeamLon);

            if (distToAbeam < AbeamThresholdNm)
            {
                _pastAbeam = true;
                double descentRate = CategoryPerformance.PatternDescentRate(
                    ctx.Category);
                ctx.Targets.DesiredVerticalRate = -descentRate;

                // Target: 60% of the way from threshold to pattern altitude
                double thresholdElev =
                    ctx.Runway?.ElevationFt ?? ctx.FieldElevation;
                double midAlt = thresholdElev
                    + (Waypoints.PatternAltitude - thresholdElev) * 0.6;
                ctx.Targets.TargetAltitude = midAlt;

                // Compute altitude floor for extended downwind: the altitude
                // at which the aircraft would intercept a 3° glideslope from
                // the approximate final approach distance to the threshold.
                double patternSize =
                    CategoryPerformance.PatternSizeNm(ctx.Category);
                double baseExt =
                    CategoryPerformance.BaseExtensionNm(ctx.Category);
                double finalApproachDist = Math.Sqrt(
                    patternSize * patternSize + baseExt * baseExt);
                _altitudeFloor =
                    thresholdElev + finalApproachDist * GlideslopeFtPerNm;

                // Begin decelerating toward base speed
                ctx.Targets.TargetSpeed =
                    CategoryPerformance.BaseSpeed(ctx.Category);
            }
        }

        if (IsExtended)
        {
            // Level off at the glideslope intercept altitude so the
            // aircraft doesn't descend below a normal approach path
            // while waiting for the TB command.
            if (_pastAbeam && ctx.Aircraft.Altitude <= _altitudeFloor)
            {
                ctx.Targets.TargetAltitude = _altitudeFloor;
                ctx.Targets.DesiredVerticalRate = null;
            }

            return false;
        }

        double dist = FlightPhysics.DistanceNm(
            ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
            _targetLat, _targetLon);

        return dist < ArrivalNm;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.ClearedToLand => CommandAcceptance.Allowed,
            CanonicalCommandType.ClearedForOption => CommandAcceptance.Allowed,
            CanonicalCommandType.GoAround => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
