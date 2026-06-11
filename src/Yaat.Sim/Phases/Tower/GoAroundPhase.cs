using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Go-around: full power, climb on runway heading, accelerate.
/// Completes at 2000ft AGL (self-clear) or assigned altitude.
/// RPO commands clear the phase, allowing immediate re-vectoring.
/// </summary>
public sealed class GoAroundPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("GoAroundPhase");

    private const double NoTurnAgl = 400.0;
    private const double SelfClearAgl = 2000.0;

    private double _fieldElevation;
    private TrueHeading _runwayTrueHeading;
    private bool _headingAssigned;

    public override string Name => "GoAround";

    public override PhaseDto ToSnapshot() =>
        new GoAroundPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            AssignedMagneticHeadingDeg = AssignedMagneticHeading?.Degrees,
            TargetAltitude = TargetAltitude,
            ReenterPattern = ReenterPattern,
            FieldElevation = _fieldElevation,
            RunwayTrueHeadingDeg = _runwayTrueHeading.Degrees,
            HeadingAssigned = _headingAssigned,
            NextLandingFullStop = NextLandingFullStop,
        };

    public static GoAroundPhase FromSnapshot(GoAroundPhaseDto dto)
    {
        MagneticHeading? assignedHeading = dto.AssignedMagneticHeadingDeg.HasValue ? new MagneticHeading(dto.AssignedMagneticHeadingDeg.Value) : null;
        var phase = new GoAroundPhase
        {
            AssignedMagneticHeading = assignedHeading,
            TargetAltitude = dto.TargetAltitude,
            ReenterPattern = dto.ReenterPattern,
            NextLandingFullStop = dto.NextLandingFullStop,
        };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        phase._fieldElevation = dto.FieldElevation;
        phase._runwayTrueHeading = new TrueHeading(dto.RunwayTrueHeadingDeg);
        phase._headingAssigned = dto.HeadingAssigned;
        return phase;
    }

    /// <summary>Heading to fly in magnetic (null = runway heading).</summary>
    public MagneticHeading? AssignedMagneticHeading { get; init; }

    /// <summary>Altitude to climb to (null = self-clear at 2000 AGL).</summary>
    public int? TargetAltitude { get; init; }

    /// <summary>
    /// When true, the aircraft re-enters the traffic pattern after the go-around climb.
    /// Set for pattern traffic and visual approaches; false for instrument approaches.
    /// </summary>
    public bool ReenterPattern { get; init; }

    /// <summary>
    /// True when the aircraft was on track for a full-stop landing before the go-around
    /// (pre-GA terminating phase was <see cref="LandingPhase"/> or <see cref="HelicopterLandingPhase"/>).
    /// When ReenterPattern is also true, the next auto-cycled circuit ends in a landing phase
    /// instead of a touch-and-go phase, preserving the aircraft's pre-go-around landing intent.
    /// </summary>
    public bool NextLandingFullStop { get; init; }

    public override void OnStart(PhaseContext ctx)
    {
        _fieldElevation = ctx.FieldElevation;
        _runwayTrueHeading = ctx.Runway?.TrueHeading ?? ctx.Aircraft.TrueHeading;

        ctx.Aircraft.IsOnGround = false;

        // TOGA power climb
        double climbRate = AircraftPerformance.InitialClimbRate(ctx.AircraftType, ctx.Category);
        double climbSpeed = AircraftPerformance.InitialClimbSpeed(ctx.AircraftType, ctx.Category);
        double targetAlt = TargetAltitude ?? (_fieldElevation + SelfClearAgl);

        ctx.Targets.TargetAltitude = targetAlt;
        ctx.Targets.DesiredVerticalRate = climbRate;
        ctx.Targets.TargetSpeed = climbSpeed;
        // Drop any speed floor/ceiling from the approach — including the ceiling
        // AutoCancelSpeedAtFinal leaves at the 5nm gate — so the missed-approach climb
        // is not capped at the approach speed.
        ctx.Targets.SpeedFloor = null;
        ctx.Targets.SpeedCeiling = null;
        ctx.Targets.TargetTrueHeading = _runwayTrueHeading;
        ctx.Targets.PreferredTurnDirection = null;
        ctx.Targets.NavigationRoute.Clear();

        if (ctx.Aircraft.Phases?.TrafficDirection is not null)
        {
            if (!ctx.Targets.HasExplicitTurnRate)
            {
                ctx.Targets.TurnRateOverride = CategoryPerformance.PatternTurnRate(ctx.Category);
            }
        }

        Log.LogDebug(
            "[GoAround] {Callsign}: started, rwyHdg={Hdg:F0}, targetAlt={Alt:F0}ft, assignedHdg={AssHdg}",
            ctx.Aircraft.Callsign,
            _runwayTrueHeading.Degrees,
            targetAlt,
            AssignedMagneticHeading?.ToString() ?? "none"
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        double agl = ctx.Aircraft.Altitude - _fieldElevation;

        if (!_headingAssigned && AssignedMagneticHeading is not null && agl >= NoTurnAgl)
        {
            _headingAssigned = true;
            ctx.Targets.TargetTrueHeading = AssignedMagneticHeading.Value.ToTrue(ctx.Aircraft.Declination);
            Log.LogDebug(
                "[GoAround] {Callsign}: turning to assigned heading {Hdg} at {Agl:F0}ft AGL",
                ctx.Aircraft.Callsign,
                AssignedMagneticHeading.Value.Degrees,
                agl
            );
        }

        double targetAgl = TargetAltitude.HasValue ? TargetAltitude.Value - _fieldElevation : SelfClearAgl;

        bool complete = agl >= targetAgl;
        if (complete)
        {
            Log.LogDebug(
                "[GoAround] {Callsign}: complete at {Agl:F0}ft AGL, IAS={Ias:F0}kts",
                ctx.Aircraft.Callsign,
                agl,
                ctx.Aircraft.IndicatedAirspeed
            );
        }

        return complete;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // Tower commands that set state for the next approach are accepted
        // without interrupting the go-around climb. Altitude/speed adjustments
        // are additive — controllers routinely amend the missed-approach
        // altitude ("climb maintain 3000") after issuing GA.
        if (IsAdditiveAirborneAdjustment(cmd))
        {
            return CommandAcceptance.Allowed;
        }

        return cmd switch
        {
            CanonicalCommandType.ClearedToLand
            or CanonicalCommandType.ForceLanding
            or CanonicalCommandType.CancelLandingClearance
            or CanonicalCommandType.ClearedForOption
            or CanonicalCommandType.TouchAndGo
            or CanonicalCommandType.StopAndGo
            or CanonicalCommandType.LowApproach
            or CanonicalCommandType.MakeLeftTraffic
            or CanonicalCommandType.MakeRightTraffic
            or CanonicalCommandType.ExitLeft
            or CanonicalCommandType.ExitRight
            or CanonicalCommandType.ExitTaxiway => CommandAcceptance.Allowed,
            _ => CommandAcceptance.ClearsPhase,
        };
    }
}
