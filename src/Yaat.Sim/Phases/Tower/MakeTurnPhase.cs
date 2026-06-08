using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Self-completing turn phase: aircraft turns a specified number of degrees
/// in the given direction, then resumes the previous heading.
/// Used for L360/R360/L270/R270 commands.
/// </summary>
public sealed class MakeTurnPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("MakeTurnPhase");

    private const double CompletionToleranceDeg = 5.0;
    private const double ExitAlignmentDeg = 10.0;

    /// <summary>
    /// Always-ahead heading lead applied while the turn is in progress.
    /// Far above <c>FlightPhysics.HeadingSnapDeg</c> (0.5°) so the snap that
    /// clears <c>PreferredTurnDirection</c> never fires until the exit
    /// window opens at <see cref="ExitAlignmentDeg"/> from the goal.
    /// </summary>
    private const double SustainedTurnLeadDeg = 90.0;

    private TrueHeading _startHeading;
    private double _cumulativeTurn;
    private TrueHeading _lastHeading;
    private bool _exiting;
    private readonly ManeuverSpeedController _speed = new();

    public required TurnDirection Direction { get; init; }
    public required double TargetDegrees { get; init; }

    public override string Name => $"Turn{(Direction == TurnDirection.Left ? "L" : "R")}{TargetDegrees:F0}";

    public override bool ManagesSpeed => true;

    public override PhaseDto ToSnapshot() =>
        new MakeTurnPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = Requirements.Count > 0 ? Requirements.Select(r => r.ToSnapshot()).ToList() : null,
            Direction = (int)Direction,
            TargetDegrees = TargetDegrees,
            StartHeadingDeg = _startHeading.Degrees,
            CumulativeTurn = _cumulativeTurn,
            LastHeadingDeg = _lastHeading.Degrees,
            Exiting = _exiting,
            PriorTargetSpeed = _speed.PriorTargetSpeed,
            PriorHasExplicitSpeed = _speed.PriorHasExplicitSpeed,
            SpeedReduced = _speed.SpeedReduced,
        };

    public static MakeTurnPhase FromSnapshot(MakeTurnPhaseDto dto)
    {
        var phase = new MakeTurnPhase { Direction = (TurnDirection)dto.Direction, TargetDegrees = dto.TargetDegrees };
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        phase._startHeading = new TrueHeading(dto.StartHeadingDeg);
        phase._cumulativeTurn = dto.CumulativeTurn;
        phase._lastHeading = new TrueHeading(dto.LastHeadingDeg);
        phase._exiting = dto.Exiting;
        phase._speed.PriorTargetSpeed = dto.PriorTargetSpeed;
        phase._speed.PriorHasExplicitSpeed = dto.PriorHasExplicitSpeed;
        phase._speed.SpeedReduced = dto.SpeedReduced;
        return phase;
    }

    public override void OnStart(PhaseContext ctx)
    {
        _startHeading = ctx.Aircraft.TrueHeading;
        _lastHeading = ctx.Aircraft.TrueHeading;

        SetSustainedTurnTarget(ctx);
        ctx.Targets.NavigationRoute.Clear();

        _speed.Capture(ctx);
        _speed.Reduce(ctx);

        Log.LogDebug(
            "[MakeTurn] {Callsign}: started {Dir}{Deg}° from hdg {Hdg:F0}",
            ctx.Aircraft.Callsign,
            Direction == TurnDirection.Left ? "L" : "R",
            TargetDegrees,
            _startHeading.Degrees
        );
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        _speed.Resume(ctx);

        // The orbit drives PreferredTurnDirection every tick so the turn keeps going past
        // the heading-snap band. When the phase ends — whether it completes the loop or a
        // command (e.g. DCT) clears it mid-turn — the bias must not leak into the next
        // lateral target, or LNAV/UpdateHeading would turn the long way around toward the
        // new heading. Mirrors InitialClimbPhase.OnEnd.
        ctx.Targets.PreferredTurnDirection = null;
    }

    public override bool OnTick(PhaseContext ctx)
    {
        TrueHeading current = ctx.Aircraft.TrueHeading;
        double delta = _lastHeading.SignedAngleTo(current);
        _cumulativeTurn += Math.Abs(delta);
        _lastHeading = current;

        if (!_exiting && (_cumulativeTurn >= TargetDegrees - ExitAlignmentDeg))
        {
            _exiting = true;
            ctx.Targets.TargetTrueHeading = ComputeExitHeading();
            ctx.Targets.PreferredTurnDirection = Direction;
        }
        else if (!_exiting)
        {
            // Re-issue the target every tick. FlightPhysics.UpdateHeading clears
            // PreferredTurnDirection when it snaps to the goal (|diff| < 0.5°),
            // so a static target = start ± 1° stalls after the very first tick.
            // A 90° lead keeps |diff| well above the snap threshold and keeps
            // PreferredTurnDirection set, so the aircraft turns continuously
            // until the exit window opens above.
            SetSustainedTurnTarget(ctx);
        }

        bool complete = _cumulativeTurn >= TargetDegrees - CompletionToleranceDeg;
        if (complete)
        {
            Log.LogDebug("[MakeTurn] {Callsign}: complete, turned {Turn:F0}°", ctx.Aircraft.Callsign, _cumulativeTurn);
        }

        return complete;
    }

    private void SetSustainedTurnTarget(PhaseContext ctx)
    {
        double offset = Direction == TurnDirection.Left ? -SustainedTurnLeadDeg : SustainedTurnLeadDeg;
        ctx.Targets.TargetTrueHeading = ctx.Aircraft.TrueHeading + offset;
        ctx.Targets.PreferredTurnDirection = Direction;
    }

    private TrueHeading ComputeExitHeading()
    {
        if (Math.Abs(TargetDegrees - 360) < 1)
        {
            return _startHeading;
        }

        // 270° turn: exit heading is 90° opposite to turn direction
        double offset = Direction == TurnDirection.Left ? 90 : -90;
        return _startHeading + offset;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // MakeTurn manages only heading (lateral dimension). Altitude and speed
        // adjustments are additive — let them pass through to normal dispatch
        // without cancelling the orbit.
        if (IsAdditiveAirborneAdjustment(cmd))
        {
            return CommandAcceptance.Allowed;
        }

        return CommandAcceptance.ClearsPhase;
    }

    public override void OnCommandAccepted(CanonicalCommandType cmd, PhaseContext ctx)
    {
        if (IsSpeedFamilyCommand(cmd))
        {
            _speed.CancelAutoResume();
        }
    }

    protected override List<ClearanceRequirement> CreateRequirements()
    {
        return [];
    }
}
