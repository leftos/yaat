using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Low approach: aircraft flies normal glideslope but does not land.
/// At category-specific AGL (Jet 100ft, Turboprop 75ft, Piston 50ft),
/// initiates go-around climb. Uses same climb profile as GoAroundPhase.
/// Completes at 1500ft AGL (self-clear).
/// </summary>
public sealed class LowApproachPhase : Phase
{
    private const double SelfClearAgl = 1500.0;

    private double _fieldElevation;
    private double _runwayHeading;
    private double _goAroundAgl;
    private double _thresholdLat;
    private double _thresholdLon;
    private bool _climbingOut;

    public override string Name => "LowApproach";

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.Heading;
        _goAroundAgl = CategoryPerformance.LowApproachAltitudeAgl(ctx.Category);

        if (ctx.Runway is not null)
        {
            _thresholdLat = ctx.Runway.ThresholdLatitude;
            _thresholdLon = ctx.Runway.ThresholdLongitude;
        }

        // Continue on glideslope toward threshold
        ctx.Targets.TargetHeading = _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();

        double approachSpeed = CategoryPerformance.ApproachSpeed(ctx.Category);
        ctx.Targets.TargetSpeed = approachSpeed;
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double agl = ctx.Aircraft.Altitude - _fieldElevation;

        if (!_climbingOut)
        {
            // Descend toward go-around altitude using proportional rate
            double distNm = FlightPhysics.DistanceNm(
                ctx.Aircraft.Latitude, ctx.Aircraft.Longitude,
                _thresholdLat, _thresholdLon);

            double goAroundAlt = _fieldElevation + _goAroundAgl;
            ctx.Targets.TargetAltitude = goAroundAlt;

            double timeToThresholdSec = ctx.Aircraft.GroundSpeed > 1
                ? distNm / (ctx.Aircraft.GroundSpeed / 3600.0)
                : 1.0;
            double altToLose = ctx.Aircraft.Altitude - goAroundAlt;
            if (altToLose > 0 && timeToThresholdSec > 1)
            {
                double requiredFpm = (altToLose / timeToThresholdSec) * 60.0;
                double clampedFpm = Math.Clamp(requiredFpm, 200, 1500);
                ctx.Targets.DesiredVerticalRate = -clampedFpm;
            }

            if (agl <= _goAroundAgl)
            {
                _climbingOut = true;

                double climbRate = CategoryPerformance.InitialClimbRate(ctx.Category);
                double climbSpeed = CategoryPerformance.InitialClimbSpeed(ctx.Category);
                double targetAlt = _fieldElevation + SelfClearAgl;

                ctx.Targets.TargetAltitude = targetAlt;
                ctx.Targets.DesiredVerticalRate = climbRate;
                ctx.Targets.TargetSpeed = climbSpeed;
                ctx.Targets.TargetHeading = _runwayHeading;
            }

            return false;
        }

        return agl >= SelfClearAgl;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return CommandAcceptance.ClearsPhase;
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
