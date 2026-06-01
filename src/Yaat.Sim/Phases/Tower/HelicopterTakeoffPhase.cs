using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Helicopter vertical liftoff: climb straight up from the ground to the completion
/// altitude (default 400 ft AGL, or the hover altitude for a present-position hold) at
/// initial climb rate, with zero forward speed and no ground roll — the aircraft does not
/// drift laterally off the spot. Any departure turn and forward acceleration belong to the
/// next phase (InitialClimbPhase for a departure, VfrHoldPhase for a present-position hover).
/// </summary>
public sealed class HelicopterTakeoffPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("HelicopterTakeoffPhase");

    private const double DefaultCompletionAgl = 400.0;

    private double _fieldElevation;
    private TrueHeading _runwayHeading;
    private DepartureInstruction? _departure;
    private double _completionAgl = DefaultCompletionAgl;

    public override string Name => "Takeoff-H";

    /// <summary>
    /// AGL (ft) at which the vertical liftoff completes. Set to the hover altitude for a
    /// present-position hold; defaults to 400 ft for a normal departure liftoff.
    /// </summary>
    public double CompletionAgl
    {
        get => _completionAgl;
        set => _completionAgl = value;
    }

    public override PhaseDto ToSnapshot() =>
        new HelicopterTakeoffPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            FieldElevation = _fieldElevation,
            RunwayHeadingDeg = _runwayHeading.Degrees,
            Departure = _departure?.ToSnapshot(),
            CompletionAgl = _completionAgl,
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
        phase._completionAgl = dto.CompletionAgl ?? DefaultCompletionAgl;
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

        // Pure vertical climb: zero forward speed and hold present heading so the helicopter
        // rises straight up without drifting off the spot. The departure turn / forward
        // acceleration is applied by the following phase once this completes.
        ctx.Targets.TargetAltitude = _fieldElevation + _completionAgl;
        ctx.Targets.DesiredVerticalRate = climbRate;
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.TargetTrueHeading = _runwayHeading;
        ctx.Targets.PreferredTurnDirection = null;

        Log.LogDebug("[Takeoff-H] {Callsign}: vertical liftoff, targetAlt={Alt:F0}ft AGL", ctx.Aircraft.Callsign, _completionAgl);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double agl = ctx.Aircraft.Altitude - _fieldElevation;
        bool complete = agl >= _completionAgl;
        if (complete)
        {
            Log.LogDebug("[Takeoff-H] {Callsign}: complete at {Agl:F0}ft AGL", ctx.Aircraft.Callsign, agl);
        }

        return complete;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // Once airborne (immediately), most commands clear the phase
        return cmd switch
        {
            CanonicalCommandType.GoAround => CommandAcceptance.Rejected("helicopter is taking off; GA is not applicable for helicopter departure"),
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.ClearsPhase,
        };
    }
}
