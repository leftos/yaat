using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

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
    private TrueHeading _downwindHeading;
    private bool _pastAbeam;
    private double _altitudeFloor;
    private bool _midfieldBroadcastIssued;

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
        _midfieldBroadcastIssued = false;

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

        ctx.Targets.TargetTrueHeading = Waypoints.DownwindHeading;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.TurnRateOverride = CategoryPerformance.PatternTurnRate(ctx.Category);
        ctx.Targets.NavigationRoute.Clear();

        // Target pattern altitude. If still above TPA (e.g., from a high pattern entry),
        // continue descending at the pattern rate instead of using the slower default.
        ctx.Targets.TargetAltitude = Waypoints.PatternAltitude;
        ctx.Targets.DesiredVerticalRate =
            (ctx.Aircraft.Altitude > Waypoints.PatternAltitude + 100) ? -CategoryPerformance.PatternDescentRate(ctx.Category) : null;

        // Downwind speed (per-type if available)
        ctx.Targets.TargetSpeed = AircraftPerformance.DownwindSpeed(ctx.AircraftType, ctx.Category);

        ctx.Logger.LogDebug(
            "[Downwind] {Callsign}: started, hdg={Hdg:F0}, patternAlt={Alt:F0}ft, extended={Ext}",
            ctx.Aircraft.Callsign,
            Waypoints.DownwindHeading.Degrees,
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

        // Midfield downwind broadcast: remind controller if no landing clearance
        if (!_midfieldBroadcastIssued && !ctx.AutoClearedToLand)
        {
            double midfieldAlongTrack = _abeamAlongTrack / 2.0;
            if (aircraftAlongTrack >= midfieldAlongTrack - AlongTrackToleranceNm)
            {
                _midfieldBroadcastIssued = true;
                if (!HasLandingClearance(ctx))
                {
                    string runwayId = ctx.Runway?.Designator ?? "unknown";
                    ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign} midfield downwind runway {runwayId}");
                }
            }
        }

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

        // Follow speed adjustment: modulate speed based on distance to leader
        if (ctx.Targets.TargetSpeed is { } currentSpeed)
        {
            double minSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            var adjusted = AirborneFollowHelper.GetAdjustedSpeed(ctx, currentSpeed, minSpeed);
            if (adjusted is not null)
            {
                ctx.Targets.TargetSpeed = adjusted.Value;
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

        // Extend downwind if following traffic and too close
        if (AirborneFollowHelper.ShouldExtendDownwind(ctx))
        {
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
            CanonicalCommandType.Follow => CommandAcceptance.Allowed,
            CanonicalCommandType.ClimbMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.DescendMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new DownwindPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            Waypoints = Waypoints?.ToSnapshot(),
            IsExtended = IsExtended,
            BaseTurnAlongTrack = _baseTurnAlongTrack,
            AbeamAlongTrack = _abeamAlongTrack,
            ThresholdLat = _thresholdLat,
            ThresholdLon = _thresholdLon,
            DownwindHeadingDeg = _downwindHeading.Degrees,
            PastAbeam = _pastAbeam,
            AltitudeFloor = _altitudeFloor,
            MidfieldBroadcastIssued = _midfieldBroadcastIssued,
        };

    public static DownwindPhase FromSnapshot(DownwindPhaseDto dto)
    {
        var phase = new DownwindPhase
        {
            Waypoints = dto.Waypoints is not null ? PatternWaypoints.FromSnapshot(dto.Waypoints) : null,
            IsExtended = dto.IsExtended,
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase._baseTurnAlongTrack = dto.BaseTurnAlongTrack;
        phase._abeamAlongTrack = dto.AbeamAlongTrack;
        phase._thresholdLat = dto.ThresholdLat;
        phase._thresholdLon = dto.ThresholdLon;
        phase._downwindHeading = new TrueHeading(dto.DownwindHeadingDeg);
        phase._pastAbeam = dto.PastAbeam;
        phase._altitudeFloor = dto.AltitudeFloor;
        phase._midfieldBroadcastIssued = dto.MidfieldBroadcastIssued;
        return phase;
    }

    private static bool HasLandingClearance(PhaseContext ctx)
    {
        var phases = ctx.Aircraft.Phases;
        if (phases is null)
        {
            return false;
        }

        return phases.LandingClearance
            is ClearanceType.ClearedToLand
                or ClearanceType.ClearedForOption
                or ClearanceType.ClearedTouchAndGo
                or ClearanceType.ClearedStopAndGo
                or ClearanceType.ClearedLowApproach;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
