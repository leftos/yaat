using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Simulation;

// The scripted route: a scenario preset or a WAIT payload running a flight-plan or global verb through its arm body.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// The verbs a preset never runs: the session clock (<c>PAUSE</c>, <c>UNPAUSE</c>, <c>SIMRATE</c>), a spawn and a bare
    /// <c>AS</c>.
    /// </summary>
    internal static bool IsRefusedInPreset(ParsedCommand command) =>
        command is PauseCommand or UnpauseCommand or SimRateCommand or SpawnNowCommand or SetActivePositionCommand;

    private static bool IsScriptedOrRefused(ParsedCommand command) => CompoundPolicy.IsScriptedArmCommand(command) || IsRefusedInPreset(command);

    /// <summary>
    /// The scripted route for a preset acting as <paramref name="identity"/>: a compound that is one unconditional block of
    /// scripted or refused verbs runs now (<see cref="TryApplyScriptedArms"/>), and one led by a bare <c>WAIT</c> or
    /// <c>WAITD</c> whose payload is such a block is deferred, to run through the same route when its gate fires (a refused
    /// verb in it refuses at once). Null for any other compound, which belongs to the dispatcher.
    /// </summary>
    internal CommandResult? TryApplyScriptedPreset(CompoundCommand compound, AircraftState aircraft, TrackOwner? identity) =>
        TryApplyScriptedArms(compound, aircraft, identity) ?? TryDeferScriptedArms(compound, aircraft, identity);

    /// <summary>
    /// Runs every command of a compound that is one unconditional block of scripted or refused verbs, in order, through
    /// <see cref="ApplyScriptedArm"/>; a block holding a refused verb is refused whole, naming it, before any arm runs, and the
    /// first refusal of an arm stops the rest. Null when the compound is not such a block.
    /// </summary>
    internal CommandResult? TryApplyScriptedArms(CompoundCommand compound, AircraftState aircraft, TrackOwner? identity)
    {
        if (SoleUnconditionalBlockOf(compound, IsScriptedOrRefused) is not { } commands)
        {
            return null;
        }

        if (commands.Find(IsRefusedInPreset) is { } refused)
        {
            return RefusedInPreset(refused);
        }

        CommandResult result = new(true, "");
        foreach (ParsedCommand command in commands)
        {
            result = ApplyScriptedArm(command, aircraft, identity);
            if (!result.Success)
            {
                return result;
            }
        }

        return result;
    }

    /// <summary>
    /// Runs one command of a preset or a deferred payload through its <see cref="ArmTable"/> row, addressed to
    /// <paramref name="aircraft"/>'s callsign and acting as <paramref name="identity"/>, on the bare host with
    /// <see cref="ArmContext.IsScripted"/> set: what the body changes reaches a live room through the tick's state-change step.
    /// A command the route does not run answers "<c>{verb} cannot run from a preset</c>".
    /// <para>
    /// Records nothing: the preset fires again on replay and applies the command there, so a record of it would apply it a
    /// second time. The arms leave their records out on this route (the flight-plan arm's <see cref="RecordedAmendFlightPlan"/>);
    /// a record one appends anyway is logged as an error and dropped, so a later arm cannot break replay silently.
    /// </para>
    /// </summary>
    internal CommandResult ApplyScriptedArm(ParsedCommand command, AircraftState aircraft, TrackOwner? identity)
    {
        if (!CompoundPolicy.IsScriptedArmCommand(command))
        {
            return RefusedInPreset(command);
        }

        string text = CommandDescriber.DescribeCommand(command);
        ActionArm arm = ArmTable.For(RecordedCommandClassifier.ClassifyParsed(command).Kind);
        var ctx = new ArmContext
        {
            Engine = this,
            Host = BareHost,
            Input = new ActionInput(aircraft.Callsign, text, ConnectionId: "", Initials: "", Baked: null),
            Remainder = text,
            AsOverrideTcp = null,
            Parsed = command,
            Aircraft = aircraft,
            Identity = identity,
            IsScripted = true,
        };

        int recorded = Scenario?.ActionLog.Count ?? 0;
        CommandResult result = arm.Run(ctx);
        DropScriptedRecords(recorded, text);
        return result;
    }

    private static CommandResult RefusedInPreset(ParsedCommand command) =>
        new(false, $"{CommandDescriber.DescribeCommand(command)} cannot run from a preset");

    /// <summary>
    /// Defers a preset led by a bare <c>WAIT</c> or <c>WAITD</c> whose payload is one unconditional block of scripted verbs,
    /// scripted and acting as <paramref name="identity"/>, built as a snapshot restore rebuilds it
    /// (<see cref="DeferredDispatch.FromSnapshot"/>); its payload runs through <see cref="TryApplyScriptedArms"/> when the gate
    /// fires. A payload holding a refused verb refuses now. It supersedes none of the aircraft's other deferrals. Null for any
    /// other compound.
    /// </summary>
    private static CommandResult? TryDeferScriptedArms(CompoundCommand compound, AircraftState aircraft, TrackOwner? identity)
    {
        if ((compound.Blocks is not [{ Condition: null } first, ..]) || (first.Commands.Find(IsDeferralGate) is not { } gate))
        {
            return null;
        }

        if (
            (CommandDispatcher.StripDeferralGateBlocks(compound) is not { } blocks)
            || (SoleUnconditionalBlockOf(new CompoundCommand(blocks), IsScriptedOrRefused) is not { } commands)
        )
        {
            return null;
        }

        if (commands.Find(IsRefusedInPreset) is { } refused)
        {
            return RefusedInPreset(refused);
        }

        var dto = new DeferredDispatchDto
        {
            RemainingSeconds = (gate as WaitCommand)?.Seconds ?? 0,
            RemainingDistanceNm = (gate as WaitDistanceCommand)?.DistanceNm ?? 0,
            IsDistanceBased = gate is WaitDistanceCommand,
            SourceText = compound.SourceText,
            IsScenarioScripted = true,
            ActingAs = identity?.ToSnapshot(),
        };
        if (DeferredDispatch.FromSnapshot(dto, aircraft.FlightPlan.Route, FixResolutionContext.From(aircraft.FlightPlan)) is not { } deferred)
        {
            return null;
        }

        aircraft.DeferredDispatches.Add(deferred);
        string timer = deferred.IsDistanceBased ? $"{deferred.RemainingDistanceNm}nm" : $"{deferred.RemainingSeconds}s";
        return new CommandResult(true, $"Will execute in {timer}: {DescribeDeferredPayload(deferred)}");
    }

    private static bool IsDeferralGate(ParsedCommand command) => command is WaitCommand or WaitDistanceCommand;

    /// <summary>
    /// Drops, with an error, every record an arm appended to the action log on the scripted route after its first
    /// <paramref name="count"/>: none should, and one kept would apply the command a second time on replay.
    /// </summary>
    private void DropScriptedRecords(int count, string command)
    {
        if ((Scenario is not { } scenario) || (scenario.ActionLog.Count <= count))
        {
            return;
        }

        int appended = scenario.ActionLog.Count - count;
        _logger.LogError("Scripted {Command} appended {Count} record(s) to the action log; dropped so replay applies it once", command, appended);
        scenario.ActionLog.RemoveRange(count, appended);
    }
}
