using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

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
    private TrueHeading _runwayHeading;
    private double _thresholdLat;
    private double _thresholdLon;
    private DepartureInstruction? _departure;

    public override string Name => "Takeoff";

    public override PhaseDto ToSnapshot() =>
        new TakeoffPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            Airborne = _airborne,
            FieldElevation = _fieldElevation,
            RunwayHeadingDeg = _runwayHeading.Degrees,
            ThresholdLat = _thresholdLat,
            ThresholdLon = _thresholdLon,
            Departure = _departure?.ToSnapshot(),
        };

    public static TakeoffPhase FromSnapshot(TakeoffPhaseDto dto)
    {
        DepartureInstruction? departure = dto.Departure is not null ? DepartureInstruction.FromSnapshot(dto.Departure) : null;
        var phase = new TakeoffPhase();
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        phase._airborne = dto.Airborne;
        phase._fieldElevation = dto.FieldElevation;
        phase._runwayHeading = new TrueHeading(dto.RunwayHeadingDeg);
        phase._thresholdLat = dto.ThresholdLat;
        phase._thresholdLon = dto.ThresholdLon;
        phase._departure = departure;
        phase.Departure = departure;
        return phase;
    }

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
        _runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.TrueHeading;
        _thresholdLat = ctx.Runway?.ThresholdLatitude ?? ctx.Aircraft.Latitude;
        _thresholdLon = ctx.Runway?.ThresholdLongitude ?? ctx.Aircraft.Longitude;
        _departure = Departure;

        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.TargetTrueHeading = _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;

        ctx.Logger.LogDebug(
            "[Takeoff] {Callsign}: started, rwy hdg={Hdg:F0}, fieldElev={Elev:F0}ft",
            ctx.Aircraft.Callsign,
            _runwayHeading.Degrees,
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
        ctx.Targets.TargetTrueHeading = new TrueHeading(_runwayHeading.Degrees - correction);

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
        // AIM 4-3-2: VFR departures must maintain runway heading until past
        // the DER and within 300ft of pattern altitude. Defer heading changes
        // to InitialClimbPhase which checks both conditions.
        if (ctx.Aircraft.IsVfr)
        {
            return;
        }

        switch (_departure)
        {
            case RelativeTurnDeparture rel:
                ctx.Targets.TargetTrueHeading =
                    rel.Direction == TurnDirection.Right
                        ? new TrueHeading(_runwayHeading.Degrees + rel.Degrees)
                        : new TrueHeading(_runwayHeading.Degrees - rel.Degrees);
                ctx.Targets.PreferredTurnDirection = rel.Direction;
                break;

            case FlyHeadingDeparture fh:
                ctx.Targets.TargetTrueHeading = fh.MagneticHeading.ToTrue(ctx.Aircraft.Declination);
                ctx.Targets.PreferredTurnDirection = fh.Direction;
                break;

            case DirectFixDeparture { Direction: not null } dfd:
                ctx.Targets.PreferredTurnDirection = dfd.Direction;
                break;

            // DefaultDeparture, RunwayHeadingDeparture, OnCourseDeparture,
            // DirectFixDeparture (no direction), ClosedTrafficDeparture: keep runway heading.
            // Navigation is set up by InitialClimbPhase.
        }
    }

    private static bool TickAirborneClimb(double agl)
    {
        return agl >= CompletionAgl;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // CM/DM set an interim altitude ceiling without interrupting the takeoff phase.
        // The altitude is stored in Targets.AssignedAltitude and picked up by InitialClimbPhase.
        if ((cmd is CanonicalCommandType.ClimbMaintain) || (cmd is CanonicalCommandType.DescendMaintain))
        {
            return CommandAcceptance.Allowed;
        }

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
