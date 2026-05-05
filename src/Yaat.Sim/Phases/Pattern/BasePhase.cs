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
    private static readonly ILogger Log = SimLog.CreateLogger("BasePhase");

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

    public override string Name => "Base";
    public override bool ManagesSpeed => true;

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
        if (!ctx.Targets.HasExplicitTurnRate)
        {
            ctx.Targets.TurnRateOverride = CategoryPerformance.PatternTurnRate(ctx.Category);
        }
        ctx.Targets.NavigationRoute.Clear();

        // Begin descent. Default rate; if SA shortened the final, compute a
        // steeper rate so the aircraft is on the 3° glideslope at base→final
        // rollout — not earlier. The 90° base→final turn translates the aircraft
        // one turn-radius further along the final, so the rollout point is
        // (finalDist + r) from the threshold, not finalDist.
        double descentRate = CategoryPerformance.PatternDescentRate(ctx.Category);

        // Approximate target altitude: halfway between pattern and threshold.
        // When SA shortens the final, replace the midpoint with the GS-intercept
        // altitude at the rollout point so the aircraft is stabilized on glide
        // path the moment it rolls out on final, not before.
        double thresholdElev = ctx.Runway?.ElevationFt ?? ctx.FieldElevation;
        double midAlt = thresholdElev + (Waypoints.PatternAltitude - thresholdElev) * 0.5;
        if (FinalDistanceNm is { } finalDist)
        {
            double gsAngle = GlideSlopeGeometry.AngleForCategory(ctx.Category);
            double turnRate = CategoryPerformance.PatternTurnRate(ctx.Category);
            double groundSpeedKt = Math.Max(ctx.Aircraft.GroundSpeed, 60);
            double turnRadiusNm = Math.Max(groundSpeedKt / (turnRate * 62.832), MinTurnRadiusNm);
            double rolloutDistNm = finalDist + turnRadiusNm;
            double gsAlt = thresholdElev + rolloutDistNm * GlideSlopeGeometry.FeetPerNm(gsAngle);
            if (gsAlt < midAlt)
            {
                midAlt = gsAlt;
                double deltaAlt = Math.Max(ctx.Aircraft.Altitude - midAlt, 0);
                double baseLen = CategoryPerformance.PatternSizeNm(ctx.Category);
                double timeMin = baseLen / (groundSpeedKt / 60.0);
                double computedRate = timeMin > 0 ? deltaAlt / timeMin : descentRate;
                descentRate = Math.Clamp(computedRate, descentRate, 1500);
            }
        }
        ctx.Targets.DesiredVerticalRate = -descentRate;
        ctx.Targets.TargetAltitude = midAlt;

        // Slow to base speed
        ctx.Targets.TargetSpeed = AircraftPerformance.BaseSpeed(ctx.AircraftType, ctx.Category);

        Log.LogDebug(
            "[Base] {Callsign}: started, hdg={Hdg:F0}, alt={Alt:F0}ft",
            ctx.Aircraft.Callsign,
            Waypoints.BaseHeading.Degrees,
            ctx.Aircraft.Altitude
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        // Follow speed adjustment — pass the phase baseline, never the previous
        // tick's adjusted target, so the +MaxSpeedAdjustKts clamp can't compound.
        if (ctx.Targets.TargetSpeed is not null)
        {
            double baseline = AircraftPerformance.BaseSpeed(ctx.AircraftType, ctx.Category);
            double minSpeed = AircraftPerformance.ApproachSpeed(ctx.AircraftType, ctx.Category);
            var adjusted = AirborneFollowHelper.GetAdjustedSpeed(ctx, baseline, minSpeed, AirborneFollowHelper.MaxSpeedAdjustKts);
            if (adjusted is not null)
            {
                ctx.Targets.TargetSpeed = adjusted.Value;
            }
        }

        double crossTrack = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(ctx.Aircraft.Position, new LatLon(_thresholdLat, _thresholdLon), _finalHeading)
        );

        // Turn initiation: begin turn when cross-track from extended centerline
        // equals the turn radius. This produces a geometrically correct 90° arc
        // that rolls out on centerline at the expected final approach distance.
        double turnRate = CategoryPerformance.PatternTurnRate(ctx.Category);
        double turnRadiusNm = Math.Max(ctx.Aircraft.GroundSpeed / (turnRate * 62.832), MinTurnRadiusNm);
        bool complete = crossTrack <= turnRadiusNm;
        if (complete)
        {
            Log.LogDebug(
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
            CanonicalCommandType.MakeShortApproach => CommandAcceptance.Allowed,
            CanonicalCommandType.MakeNormalApproach => CommandAcceptance.Allowed,
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
