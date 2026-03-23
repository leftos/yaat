using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Pattern;

/// <summary>
/// Base leg: turn from downwind onto base heading, begin descent.
/// Decelerates to base speed, descends toward approach altitude.
/// Completes when reaching the final turn waypoint.
/// </summary>
public sealed class BasePhase : Phase
{
    private const double MinTurnRadiusNm = 0.15;

    private double _thresholdLat;
    private double _thresholdLon;
    private TrueHeading _finalHeading;

    public PatternWaypoints? Waypoints { get; set; }

    /// <summary>
    /// When set, overrides the default final turn target to a point on the
    /// extended centerline at this distance from the threshold.
    /// </summary>
    public double? FinalDistanceNm { get; set; }

    /// <summary>
    /// When true, the aircraft continues on the current heading past the turn point
    /// until a turn-final command or another EXT-clearing command is issued.
    /// </summary>
    public bool IsExtended { get; set; }

    public override string Name => "Base";

    public override void OnStart(PhaseContext ctx)
    {
        if (Waypoints is null)
        {
            return;
        }

        _finalHeading = Waypoints.FinalHeading;

        if (FinalDistanceNm is not null)
        {
            TrueHeading reciprocal = Waypoints.FinalHeading.ToReciprocal();
            var target = GeoMath.ProjectPoint(Waypoints.ThresholdLat, Waypoints.ThresholdLon, reciprocal, FinalDistanceNm.Value);
            _thresholdLat = target.Lat;
            _thresholdLon = target.Lon;
        }
        else
        {
            _thresholdLat = Waypoints.ThresholdLat;
            _thresholdLon = Waypoints.ThresholdLon;
        }

        ctx.Targets.TargetTrueHeading = Waypoints.BaseHeading;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.TurnRateOverride = CategoryPerformance.PatternTurnRate(ctx.Category);
        ctx.Targets.NavigationRoute.Clear();

        // Begin descent
        double descentRate = CategoryPerformance.PatternDescentRate(ctx.Category);
        ctx.Targets.DesiredVerticalRate = -descentRate;

        // Approximate target altitude: halfway between pattern and threshold
        double thresholdElev = ctx.Runway?.ElevationFt ?? ctx.FieldElevation;
        double midAlt = thresholdElev + (Waypoints.PatternAltitude - thresholdElev) * 0.5;
        ctx.Targets.TargetAltitude = midAlt;

        // Slow to base speed
        ctx.Targets.TargetSpeed = AircraftPerformance.BaseSpeed(ctx.AircraftType, ctx.Category);

        ctx.Logger.LogDebug(
            "[Base] {Callsign}: started, hdg={Hdg:F0}, alt={Alt:F0}ft, extended={Ext}",
            ctx.Aircraft.Callsign,
            Waypoints.BaseHeading.Degrees,
            ctx.Aircraft.Altitude,
            IsExtended
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Follow speed adjustment
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
            return false;
        }

        double crossTrack = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, _thresholdLat, _thresholdLon, _finalHeading)
        );

        // Turn initiation: begin turn when cross-track from extended centerline
        // equals the turn radius. This produces a geometrically correct 90° arc
        // that rolls out on centerline at the expected final approach distance.
        double turnRate = CategoryPerformance.PatternTurnRate(ctx.Category);
        double turnRadiusNm = Math.Max(ctx.Aircraft.GroundSpeed / (turnRate * 62.832), MinTurnRadiusNm);
        bool complete = crossTrack <= turnRadiusNm;
        if (complete)
        {
            ctx.Logger.LogDebug(
                "[Base] {Callsign}: final turn point reached, alt={Alt:F0}ft, xtrack={XT:F2}nm, turnR={R:F2}nm",
                ctx.Aircraft.Callsign,
                ctx.Aircraft.Altitude,
                crossTrack,
                turnRadiusNm
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
            CanonicalCommandType.Follow => CommandAcceptance.Allowed,
            CanonicalCommandType.ClimbMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.DescendMaintain => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.ClearsPhase,
        };
    }

    public override PhaseDto ToSnapshot() =>
        new BasePhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            Waypoints = Waypoints?.ToSnapshot(),
            FinalDistanceNm = FinalDistanceNm,
            IsExtended = IsExtended,
            ThresholdLat = _thresholdLat,
            ThresholdLon = _thresholdLon,
            FinalHeadingDeg = _finalHeading.Degrees,
        };

    public static BasePhase FromSnapshot(BasePhaseDto dto)
    {
        var phase = new BasePhase
        {
            Waypoints = dto.Waypoints is not null ? PatternWaypoints.FromSnapshot(dto.Waypoints) : null,
            FinalDistanceNm = dto.FinalDistanceNm,
            IsExtended = dto.IsExtended,
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase._thresholdLat = dto.ThresholdLat;
        phase._thresholdLon = dto.ThresholdLon;
        phase._finalHeading = new TrueHeading(dto.FinalHeadingDeg);
        return phase;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
