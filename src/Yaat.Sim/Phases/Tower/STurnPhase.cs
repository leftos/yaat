using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// S-turn phase: aircraft performs alternating left/right turns on final for spacing.
/// Each S-turn is a ~30° deviation from the final approach heading, alternating sides.
/// Used by controllers to absorb extra distance on final approach (7110.65 §3-8-1).
/// </summary>
public sealed class STurnPhase : Phase
{
    private const double TurnDeviationDeg = 30.0;
    private const double HeadingToleranceDeg = 5.0;

    private double _finalHeading;
    private int _turnsCompleted;
    private bool _turningToFinal;

    public required TurnDirection InitialDirection { get; init; }
    public int Count { get; init; } = 2;

    public override string Name => "S-Turns";

    public override void OnStart(PhaseContext ctx)
    {
        _finalHeading = ctx.Aircraft.Heading;
        _turnsCompleted = 0;
        _turningToFinal = false;

        // Start the first turn
        SetNextTurnTarget(ctx);

        ctx.Logger.LogDebug(
            "[STurn] {Callsign}: started, {Count} S-turns, initial {Dir}",
            ctx.Aircraft.Callsign,
            Count,
            InitialDirection == TurnDirection.Left ? "left" : "right"
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (ctx.Targets.TargetHeading is not { } targetHdg)
        {
            return true;
        }

        double hdgDiff = Math.Abs(FlightPhysics.NormalizeAngle(ctx.Aircraft.Heading - targetHdg));

        if (hdgDiff > HeadingToleranceDeg)
        {
            return false;
        }

        if (_turningToFinal)
        {
            _turnsCompleted++;
            _turningToFinal = false;

            if (_turnsCompleted >= Count)
            {
                // Restore final heading
                ctx.Targets.TargetHeading = _finalHeading;
                ctx.Targets.PreferredTurnDirection = null;
                ctx.Targets.TurnRateOverride = null;
                ctx.Logger.LogDebug("[STurn] {Callsign}: complete after {Count} S-turns", ctx.Aircraft.Callsign, _turnsCompleted);
                return true;
            }

            // Start next S-turn
            SetNextTurnTarget(ctx);
        }
        else
        {
            // Reached the deviation target, now turn back through final to the other side
            _turningToFinal = true;
            ctx.Targets.TargetHeading = _finalHeading;
            // Turn direction to return to final: opposite of current deviation
            var currentDir = GetCurrentTurnDirection();
            ctx.Targets.PreferredTurnDirection = currentDir == TurnDirection.Left ? TurnDirection.Right : TurnDirection.Left;
        }

        return false;
    }

    private void SetNextTurnTarget(PhaseContext ctx)
    {
        var dir = GetCurrentTurnDirection();
        double offset = dir == TurnDirection.Left ? -TurnDeviationDeg : TurnDeviationDeg;
        ctx.Targets.TargetHeading = FlightPhysics.NormalizeHeading(_finalHeading + offset);
        ctx.Targets.PreferredTurnDirection = dir;
        ctx.Targets.TurnRateOverride = CategoryPerformance.PatternTurnRate(ctx.Category);
    }

    private TurnDirection GetCurrentTurnDirection()
    {
        // Alternate: even turns go initial direction, odd turns go opposite
        bool isEven = _turnsCompleted % 2 == 0;
        return isEven ? InitialDirection : (InitialDirection == TurnDirection.Left ? TurnDirection.Right : TurnDirection.Left);
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
