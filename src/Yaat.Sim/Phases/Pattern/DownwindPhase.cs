using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Downwind leg: fly opposite runway heading at pattern altitude.
/// Maintains downwind speed, level flight.
/// Completes when reaching the base turn waypoint.
/// </summary>
public sealed class DownwindPhase : Phase
{
    private const double AlongTrackToleranceNm = 0.3;

    private double _baseTurnAlongTrack;
    private double _abeamAlongTrack;
    private double _thresholdLat;
    private double _thresholdLon;
    private double _downwindHeading;
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

        _thresholdLat = Waypoints.ThresholdLat;
        _thresholdLon = Waypoints.ThresholdLon;
        _downwindHeading = Waypoints.DownwindHeading;
        _pastAbeam = false;

        _abeamAlongTrack = GeoMath.AlongTrackDistanceNm(
            Waypoints.DownwindAbeamLat,
            Waypoints.DownwindAbeamLon,
            _thresholdLat,
            _thresholdLon,
            _downwindHeading
        );

        _baseTurnAlongTrack = GeoMath.AlongTrackDistanceNm(
            Waypoints.BaseTurnLat,
            Waypoints.BaseTurnLon,
            _thresholdLat,
            _thresholdLon,
            _downwindHeading
        );

        var turnDir = Waypoints.Direction == PatternDirection.Left ? TurnDirection.Left : TurnDirection.Right;

        ctx.Targets.TargetHeading = Waypoints.DownwindHeading;
        ctx.Targets.PreferredTurnDirection = turnDir;
        ctx.Targets.TurnRateOverride = CategoryPerformance.PatternTurnRate(ctx.Category);
        ctx.Targets.NavigationRoute.Clear();

        // Level off at pattern altitude
        ctx.Targets.TargetAltitude = Waypoints.PatternAltitude;
        ctx.Targets.DesiredVerticalRate = null;

        // Downwind speed (per-type if available)
        ctx.Targets.TargetSpeed = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);

        ctx.Logger.LogDebug(
            "[Downwind] {Callsign}: started, hdg={Hdg:F0}, patternAlt={Alt:F0}ft, extended={Ext}",
            ctx.Aircraft.Callsign,
            Waypoints.DownwindHeading,
            Waypoints.PatternAltitude,
            IsExtended
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double aircraftAlongTrack = GeoMath.AlongTrackDistanceNm(
            ctx.Aircraft.Latitude,
            ctx.Aircraft.Longitude,
            _thresholdLat,
            _thresholdLon,
            _downwindHeading
        );

        // Begin descent when abeam the approach end of the runway
        if (!_pastAbeam && Waypoints is not null)
        {
            if (aircraftAlongTrack >= _abeamAlongTrack - AlongTrackToleranceNm)
            {
                _pastAbeam = true;
                ctx.Logger.LogDebug("[Downwind] {Callsign}: abeam threshold, beginning descent", ctx.Aircraft.Callsign);
                double descentRate = CategoryPerformance.PatternDescentRate(ctx.Category);
                ctx.Targets.DesiredVerticalRate = -descentRate;

                // Target: 60% of the way from threshold to pattern altitude
                double thresholdElev = ctx.Runway?.ElevationFt ?? ctx.FieldElevation;
                double midAlt = thresholdElev + (Waypoints.PatternAltitude - thresholdElev) * 0.6;
                ctx.Targets.TargetAltitude = midAlt;

                // Compute altitude floor for extended downwind: the altitude
                // at which the aircraft would intercept a 3° glideslope from
                // the approximate final approach distance to the threshold.
                double patternSize = CategoryPerformance.PatternSizeNm(ctx.Category);
                double baseExt = CategoryPerformance.BaseExtensionNm(ctx.Category);
                double finalApproachDist = Math.Sqrt(patternSize * patternSize + baseExt * baseExt);
                double gsAngle = GlideSlopeGeometry.AngleForCategory(ctx.Category);
                _altitudeFloor = thresholdElev + finalApproachDist * GlideSlopeGeometry.FeetPerNm(gsAngle);

                // Begin decelerating toward base speed
                ctx.Targets.TargetSpeed = AircraftPerformance.BaseSpeed(ctx.AircraftType, ctx.Category);
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

        bool complete = aircraftAlongTrack >= _baseTurnAlongTrack - AlongTrackToleranceNm;
        if (complete)
        {
            ctx.Logger.LogDebug("[Downwind] {Callsign}: base turn point reached, alt={Alt:F0}ft", ctx.Aircraft.Callsign, ctx.Aircraft.Altitude);
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

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
