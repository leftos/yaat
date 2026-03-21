using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Helicopter vertical liftoff: climb from ground to 400ft AGL
/// at initial climb rate with no ground roll. Accelerates to
/// InitialClimbSpeed once airborne. Transitions to InitialClimbPhase.
/// </summary>
public sealed class HelicopterTakeoffPhase : Phase
{
    private const double CompletionAgl = 400.0;

    private double _fieldElevation;
    private TrueHeading _runwayHeading;
    private DepartureInstruction? _departure;

    public override string Name => "Takeoff-H";

    public override PhaseDto ToSnapshot() =>
        new HelicopterTakeoffPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            FieldElevation = _fieldElevation,
            RunwayHeadingDeg = _runwayHeading.Degrees,
            Departure = _departure?.ToSnapshot(),
        };

    public static HelicopterTakeoffPhase FromSnapshot(HelicopterTakeoffPhaseDto dto)
    {
        DepartureInstruction? departure = dto.Departure is not null ? DepartureInstruction.FromSnapshot(dto.Departure) : null;
        var phase = new HelicopterTakeoffPhase();
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        phase._fieldElevation = dto.FieldElevation;
        phase._runwayHeading = new TrueHeading(dto.RunwayHeadingDeg);
        phase._departure = departure;
        phase.Departure = departure;
        return phase;
    }

    /// <summary>Departure instruction from CTO command.</summary>
    public DepartureInstruction? Departure { get; private set; }

    public void SetAssignedDeparture(DepartureInstruction? departure)
    {
        Departure = departure;
    }

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _runwayHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.TrueHeading;
        _departure = Departure;

        // Immediate liftoff — no ground roll
        ctx.Aircraft.IsOnGround = false;

        double climbRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);
        double climbSpeed = AircraftPerformance.InitialClimbSpeed(ctx.AircraftType, ctx.Category);

        ctx.Targets.TargetAltitude = _fieldElevation + CompletionAgl;
        ctx.Targets.DesiredVerticalRate = climbRate;
        ctx.Targets.TargetSpeed = climbSpeed;
        ctx.Targets.TargetTrueHeading = _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;

        ApplyDepartureHeading(ctx);

        ctx.Logger.LogDebug("[Takeoff-H] {Callsign}: vertical liftoff, targetAlt={Alt:F0}ft", ctx.Aircraft.Callsign, _fieldElevation + CompletionAgl);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double agl = ctx.Aircraft.Altitude - _fieldElevation;
        bool complete = agl >= CompletionAgl;
        if (complete)
        {
            ctx.Logger.LogDebug("[Takeoff-H] {Callsign}: complete at {Agl:F0}ft AGL", ctx.Aircraft.Callsign, agl);
        }

        return complete;
    }

    private void ApplyDepartureHeading(PhaseContext ctx)
    {
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
        }
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // Once airborne (immediately), most commands clear the phase
        return cmd switch
        {
            CanonicalCommandType.GoAround => CommandAcceptance.Rejected,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.ClearsPhase,
        };
    }
}
