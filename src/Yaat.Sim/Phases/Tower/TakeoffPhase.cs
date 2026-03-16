using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Ground roll (accelerate to Vr) then liftoff and climb.
/// Completes at 400ft AGL.
/// </summary>
public sealed class TakeoffPhase : Phase
{
    private const double CompletionAgl = 400.0;
    private const double CenterlineGainDegPerNm = 150.0;
    private const double MaxCenterlineCorrectionDeg = 10.0;

    private bool _airborne;
    private double _fieldElevation;
    private double _runwayHeading;
    private double _thresholdLat;
    private double _thresholdLon;
    private DepartureInstruction? _departure;

    public override string Name => "Takeoff";

    /// <summary>Departure instruction from CTO command.</summary>
    public DepartureInstruction? Departure { get; private set; }

    /// <summary>
    /// Called by the dispatcher when CTO is issued.
    /// </summary>
    public void SetAssignedDeparture(DepartureInstruction? departure)
    {
        Departure = departure;
    }

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.Heading;
        _thresholdLat = ctx.Runway?.ThresholdLatitude ?? ctx.Aircraft.Latitude;
        _thresholdLon = ctx.Runway?.ThresholdLongitude ?? ctx.Aircraft.Longitude;
        _departure = Departure;

        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.TargetHeading = _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;

        ctx.Logger.LogDebug(
            "[Takeoff] {Callsign}: started, rwy hdg={Hdg:F0}, fieldElev={Elev:F0}ft",
            ctx.Aircraft.Callsign,
            _runwayHeading,
            _fieldElevation
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double agl = ctx.Aircraft.Altitude - _fieldElevation;

        if (!_airborne)
        {
            return TickGroundRoll(ctx);
        }

        return TickAirborneClimb(agl);
    }

    private bool TickGroundRoll(PhaseContext ctx)
    {
        // Steer toward runway centerline
        double signedXte = GeoMath.SignedCrossTrackDistanceNm(
            ctx.Aircraft.Latitude,
            ctx.Aircraft.Longitude,
            _thresholdLat,
            _thresholdLon,
            _runwayHeading
        );
        double correction = Math.Clamp(signedXte * CenterlineGainDegPerNm, -MaxCenterlineCorrectionDeg, MaxCenterlineCorrectionDeg);
        ctx.Targets.TargetHeading = FlightPhysics.NormalizeHeading(_runwayHeading - correction);

        double vr = AircraftPerformance.RotationSpeed(ctx.AircraftType, ctx.Category);
        double accelRate = AircraftPerformance.GroundAccelRate(ctx.AircraftType, ctx.Category);

        // Accelerate toward Vr using ground acceleration rate
        double targetSpeed = ctx.Aircraft.IndicatedAirspeed + accelRate * ctx.DeltaSeconds;
        if (targetSpeed >= vr)
        {
            targetSpeed = vr;
        }
        ctx.Aircraft.IndicatedAirspeed = targetSpeed;
        ctx.Targets.TargetSpeed = null;

        // Liftoff at Vr
        if (ctx.Aircraft.IndicatedAirspeed >= vr)
        {
            _airborne = true;
            ctx.Aircraft.IsOnGround = false;
            ctx.Logger.LogDebug("[Takeoff] {Callsign}: airborne at Vr={Vr:F0}kts", ctx.Aircraft.Callsign, vr);

            // Set climb targets
            double climbRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);
            double climbSpeed = AircraftPerformance.InitialClimbSpeed(ctx.AircraftType, ctx.Category);
            ctx.Targets.TargetAltitude = _fieldElevation + CompletionAgl;
            ctx.Targets.DesiredVerticalRate = climbRate;
            ctx.Targets.TargetSpeed = climbSpeed;

            ApplyDepartureHeading(ctx);
        }

        return false;
    }

    private void ApplyDepartureHeading(PhaseContext ctx)
    {
        switch (_departure)
        {
            case RelativeTurnDeparture rel:
                int relHdg =
                    rel.Direction == TurnDirection.Right
                        ? FlightPhysics.NormalizeHeadingInt(_runwayHeading + rel.Degrees)
                        : FlightPhysics.NormalizeHeadingInt(_runwayHeading - rel.Degrees);
                ctx.Targets.TargetHeading = relHdg;
                ctx.Targets.PreferredTurnDirection = rel.Direction;
                break;

            case FlyHeadingDeparture fh:
                ctx.Targets.TargetHeading = fh.Heading;
                ctx.Targets.PreferredTurnDirection = fh.Direction;
                break;

            // DefaultDeparture, RunwayHeadingDeparture, OnCourseDeparture,
            // DirectFixDeparture, ClosedTrafficDeparture: keep runway heading.
            // Navigation is set up by InitialClimbPhase.
        }
    }

    private static bool TickAirborneClimb(double agl)
    {
        return agl >= CompletionAgl;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        if (!_airborne)
        {
            // During ground roll, reject most commands
            return cmd switch
            {
                CanonicalCommandType.CancelTakeoffClearance => CommandAcceptance.Allowed,
                CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
                _ => CommandAcceptance.Rejected,
            };
        }

        // Once airborne, most commands clear the phase
        return cmd switch
        {
            CanonicalCommandType.GoAround => CommandAcceptance.Rejected,
            _ => CommandAcceptance.ClearsPhase,
        };
    }
}
