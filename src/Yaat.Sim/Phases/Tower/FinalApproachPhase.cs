using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Tracks 3° glideslope from current position to threshold.
/// Checks PhaseList-level landing clearance (CTL can be issued on
/// downwind/base, well before this phase activates).
/// Auto-triggers go-around if no clearance by 0.5nm from threshold.
/// Completes when crossing the threshold.
/// Checks for illegal approach intercept (7110.65 §5-9-1) on
/// first tick when aircraft is established on the localizer.
/// </summary>
public sealed class FinalApproachPhase : Phase
{
    private const double AutoGoAroundDistNm = 0.5;
    private const double InterceptCrossTrackThresholdNm = 0.1;
    private const double InterceptHeadingThresholdDeg = 15.0;

    private double _thresholdLat;
    private double _thresholdLon;
    private double _thresholdElevation;
    private double _runwayHeading;
    private bool _goAroundTriggered;
    private bool _interceptChecked;
    private bool _isPatternTraffic;

    public override string Name => "FinalApproach";

    public override void OnStart(PhaseContext ctx)
    {
        if (ctx.Runway is null)
        {
            return;
        }

        _thresholdLat = ctx.Runway.ThresholdLatitude;
        _thresholdLon = ctx.Runway.ThresholdLongitude;
        _thresholdElevation = ctx.Runway.ElevationFt;
        _runwayHeading = ctx.Runway.TrueHeading;
        _isPatternTraffic = ctx.Aircraft.Phases?.TrafficDirection is not null;

        // Set heading toward threshold
        ctx.Targets.TargetHeading = _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();

        // Set approach speed
        double approachSpeed = CategoryPerformance.ApproachSpeed(ctx.Category);
        ctx.Targets.TargetSpeed = approachSpeed;
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (_goAroundTriggered)
        {
            return false;
        }

        double distNm = FlightPhysics.DistanceNm(
            ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
            _thresholdLat, _thresholdLon);

        CheckInterceptDistance(ctx, distNm);

        // Target: threshold elevation (descend all the way to the runway)
        ctx.Targets.TargetAltitude = _thresholdElevation;

        // Compute descent rate proportionally: lose all remaining altitude
        // over the remaining distance. This naturally handles above/below
        // glideslope — steeper when high, shallower when low.
        double timeToThresholdSec = ctx.Aircraft.GroundSpeed > 1
            ? distNm / (ctx.Aircraft.GroundSpeed / 3600.0)
            : 1.0;
        double altToLose = ctx.Aircraft.Altitude - _thresholdElevation;
        double requiredFpm = timeToThresholdSec > 1
            ? (altToLose / timeToThresholdSec) * 60.0
            : altToLose * 60.0;

        // Clamp to reasonable range (don't dive or climb on approach)
        double clampedFpm = Math.Clamp(requiredFpm, 200, 1500);
        ctx.Targets.DesiredVerticalRate = -clampedFpm;

        // Check landing clearance from PhaseList (set earlier by CTL command)
        bool hasLandingClearance = HasLandingClearance(ctx);

        // Auto go-around if no landing clearance by 0.5nm
        if (distNm <= AutoGoAroundDistNm && !hasLandingClearance)
        {
            _goAroundTriggered = true;
            TriggerGoAround(ctx);
            return false;
        }

        // Phase complete at threshold
        double agl = ctx.Aircraft.Altitude - _thresholdElevation;
        return distNm < 0.05 || agl < 5;
    }

    private void CheckInterceptDistance(PhaseContext ctx, double distNm)
    {
        if (_interceptChecked || _isPatternTraffic || ctx.Runway is null)
        {
            return;
        }

        double crossTrack = Math.Abs(
            FlightPhysics.SignedCrossTrackDistanceNm(
                ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
                _thresholdLat, _thresholdLon, _runwayHeading));

        double headingDiff = Math.Abs(
            FlightPhysics.NormalizeAngle(
                ctx.Aircraft.Heading - _runwayHeading));

        if (crossTrack >= InterceptCrossTrackThresholdNm
            || headingDiff >= InterceptHeadingThresholdDeg)
        {
            return;
        }

        // Aircraft is established on the localizer — check distance
        _interceptChecked = true;

        double minIntercept = ApproachGateDatabase
            .GetMinInterceptDistanceNm(
                ctx.Runway.AirportId, ctx.Runway.RunwayId);

        if (distNm < minIntercept)
        {
            ctx.Aircraft.PendingWarnings.Add(
                $"Illegal intercept: turned on final {distNm:F1}nm "
                + $"from threshold (min {minIntercept:F1}nm) "
                + "[7110.65 §5-9-1]");
        }
    }

    private static bool HasLandingClearance(PhaseContext ctx)
    {
        var phases = ctx.Aircraft.Phases;
        if (phases is null)
        {
            return false;
        }

        return phases.LandingClearance is ClearanceType.ClearedToLand
            or ClearanceType.ClearedForOption
            or ClearanceType.ClearedTouchAndGo
            or ClearanceType.ClearedStopAndGo;
    }

    private void TriggerGoAround(PhaseContext ctx)
    {
        if (ctx.Aircraft.Phases is null)
        {
            return;
        }

        var goAround = new GoAroundPhase();
        ctx.Aircraft.Phases.InsertAfterCurrent(goAround);
        ctx.Aircraft.Phases.AdvanceToNext(ctx);
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
        // No per-phase requirements — clearance is tracked at PhaseList level
        return [];
    }
}
