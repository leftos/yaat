using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A tug move that runs to completion records the taxiway it leaves the aircraft on: a push onto a taxiway sets
/// <see cref="AircraftGroundOps.CurrentTaxiway"/> to that taxiway, and a push ending on a spot clears it. A taxi
/// clearance issued afterwards then starts from the taxiway the push put the aircraft on. SFO gate F4's only lead-out
/// joins taxiway T8, while <c>PUSH T9</c> leaves the aircraft on T9: the following <c>TAXI T9 $9</c> must drive T9
/// from where the aircraft stands, not wander back through T8 and B5 to reach it (the UAL2183 report in the SFO ground
/// bundle).
///
/// <para>The engine is driven rather than a phase ticked directly — <see cref="FlightPhysics"/> is the only
/// integrator of ground speed. A missing SFO layout silently skips, the repo's convention for absent test data.</para>
/// </summary>
public class PushCompletionTaxiwayTests(ITestOutputHelper output)
{
    private const string Gate = "F4";
    private const string AircraftType = "A320";
    private const string PushTaxiway = "T9";
    private const string PushCommand = $"PUSH {PushTaxiway}";
    private const string TaxiCommand = $"TAXI {PushTaxiway} $9";
    private const int PushBudgetSeconds = 240;

    private const string SpotGate = "D15";
    private const string SpotType = "B738";
    private const string SpotCommand = "PUSH $6A";
    private const int SpotBudgetSeconds = 450;

    /// <summary>A taxiway name the aircraft is not on, preset so a push to a spot has something to clear.</summary>
    private const string StaleTaxiway = "A";

    /// <summary><c>PUSH T9</c> off F4, ticked to completion: the aircraft is recorded as on T9.</summary>
    [Fact]
    public void PushOntoTaxiway_Completed_SetsCurrentTaxiway()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = PushToCompletion(ground, "UAL2183", AircraftType, Gate, PushCommand, PushBudgetSeconds);

        Assert.Equal(PushTaxiway, ac.Ground.CurrentTaxiway);
    }

    /// <summary><c>PUSH $6A</c> off D15, ticked to completion: a spot is not a taxiway, so the recorded taxiway is cleared.</summary>
    [Fact]
    public void PushToSpot_Completed_ClearsCurrentTaxiway()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "PSH9", SpotType, SpotGate);
        ac.Ground.CurrentTaxiway = StaleTaxiway;
        Tick(ground, ac, SpotCommand, SpotBudgetSeconds);

        Assert.Null(ac.Ground.CurrentTaxiway);
    }

    /// <summary>
    /// The tow's last move carries the taxiway through a snapshot: <see cref="PushbackPhase.IsLastMove"/> and
    /// <see cref="PushbackPhase.EndTaxiway"/> survive <see cref="PushbackPhase.ToSnapshot"/> and
    /// <see cref="PushbackPhase.FromSnapshot"/>, and only the last move is marked.
    /// </summary>
    [Fact]
    public void PushOntoTaxiway_LastMove_RoundTripsThroughSnapshot()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL2183", AircraftType, Gate);
        CommandResult push = ground.Engine.SendCommand(ac.Callsign, PushCommand);
        Assert.True(push.Success, $"'{PushCommand}' off {Gate} was refused: {push.Message}");

        List<PushbackPhase> moves = [.. ac.Phases!.Phases.OfType<PushbackPhase>()];
        Assert.NotEmpty(moves);
        Assert.All(moves[..^1], m => Assert.False(m.IsLastMove));
        PushbackPhase last = moves[^1];
        Assert.True(last.IsLastMove);
        Assert.Equal(PushTaxiway, last.EndTaxiway);

        var restored = PushbackPhase.FromSnapshot(Assert.IsType<PushbackPhaseDto>(last.ToSnapshot()));
        Assert.True(restored.IsLastMove);
        Assert.Equal(PushTaxiway, restored.EndTaxiway);
    }

    /// <summary>
    /// <c>PUSH T9</c> off F4, then <c>TAXI T9 $9</c>: the resolved route's first named taxiway is T9, and it never
    /// touches T8 (F4's lead-out) or B5 (the link from T8 back to T9).
    /// </summary>
    [Fact]
    public void PushOntoTaxiway_ThenTaxi_StartsFromThatTaxiway()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = PushToCompletion(ground, "UAL2183", AircraftType, Gate, PushCommand, PushBudgetSeconds);

        CommandResult taxi = ground.Engine.SendCommand(ac.Callsign, TaxiCommand);
        output.WriteLine($"'{TaxiCommand}' → success={taxi.Success} \"{taxi.Message}\"");
        Assert.True(taxi.Success, $"'{TaxiCommand}' after the push was refused: {taxi.Message}");

        TaxiRoute? route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        SfoGroundHarness.DumpRoute(output, route);
        List<string> legs = [.. route.Segments.Select(s => s.TaxiwayName).Where(IsNamedTaxiway)];
        string sequence = string.Join(" ", legs);

        Assert.True(legs.Count > 0, "the route has no named-taxiway segments");
        Assert.DoesNotContain("T8", legs);
        Assert.DoesNotContain("B5", legs);
        Assert.True(legs[0] == PushTaxiway, $"the taxi started on {legs[0]}, not on {PushTaxiway} where the push left it (route: {sequence})");
    }

    /// <summary>
    /// <c>PUSH T9</c> off F4, interrupted by <c>TAXI T9 $9</c> while the tug is still moving: the dropped tow is not a
    /// completed one, so it records nothing — the aircraft's taxiway is still the one it had before the push, neither the
    /// push's end taxiway nor a null written by the tow letting go.
    /// </summary>
    [Fact]
    public void TaxiMidPush_DoesNotTouchCurrentTaxiwayFromTheTow()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL2183", AircraftType, Gate);
        ac.Ground.CurrentTaxiway = StaleTaxiway;
        CommandResult push = ground.Engine.SendCommand(ac.Callsign, PushCommand);
        Assert.True(push.Success, $"'{PushCommand}' off {Gate} was refused: {push.Message}");

        int lastMove = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => ac.Phases?.CurrentPhase is PushbackPhase { IsLastMove: true },
            PushBudgetSeconds,
            null
        );
        Assert.True(lastMove > 0, $"'{PushCommand}' never reached its last tug move (phase={ac.Phases?.CurrentPhase?.Name ?? "null"})");
        ground.Engine.TickOneSecond();
        Assert.True(
            ac.Phases?.CurrentPhase is PushbackPhase { IsLastMove: true },
            $"the last tug move ended before the TAXI could interrupt it (phase={ac.Phases?.CurrentPhase?.Name ?? "null"})"
        );

        CommandResult taxi = ground.Engine.SendCommand(ac.Callsign, TaxiCommand);
        output.WriteLine($"'{TaxiCommand}' mid-push → success={taxi.Success} \"{taxi.Message}\"");
        Assert.True(taxi.Success, $"'{TaxiCommand}' mid-push was refused: {taxi.Message}");

        Assert.Equal(StaleTaxiway, ac.Ground.CurrentTaxiway);
    }

    /// <summary>
    /// F3 <c>PUSH A F1</c>, amended a second into the push-off by <c>PUSH FACE N</c>, ticked to completion: the amended
    /// tow still ends on A, so the aircraft is recorded as on A whether the re-plan kept the running push-off as its
    /// only move or queued more behind it.
    /// </summary>
    [Fact]
    public void PushOntoTaxiway_AmendedMidPush_Completed_SetsCurrentTaxiway()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState ac = SfoGroundHarness.SpawnParked(ground, "UAL462", "B738", "F3");
        ac.Ground.CurrentTaxiway = StaleTaxiway + "1";
        CommandResult push = ground.Engine.SendCommand(ac.Callsign, "PUSH A F1");
        Assert.True(push.Success, $"'PUSH A F1' off F3 was refused: {push.Message}");
        ground.Engine.TickOneSecond();
        CommandResult amended = ground.Engine.SendCommand(ac.Callsign, "PUSH FACE N");
        output.WriteLine($"'PUSH FACE N' → success={amended.Success} \"{amended.Message}\"");
        Assert.True(amended.Success, $"'PUSH FACE N' mid-push was refused: {amended.Message}");
        output.WriteLine($"moves after the amendment: {ac.Phases!.Phases.OfType<PushbackPhase>().Count()}");

        int completed = SfoGroundHarness.TickUntil(ground.Engine, () => ac.Phases?.CurrentPhase is not PushbackPhase, SpotBudgetSeconds, null);
        Assert.True(completed > 0, $"the amended push never completed within {SpotBudgetSeconds}s");

        Assert.Equal("A", ac.Ground.CurrentTaxiway);
    }

    /// <summary>
    /// The re-mark a mid-push amendment applies to the running push-off (<c>GroundCommandHandler.MarkRunningPushOff</c>):
    /// a re-plan of one move leaves the push-off as the tow's last, so its completion records the amended goal's
    /// taxiway; a re-plan of more queues another move as the last, so the push-off records nothing. The amendment's
    /// re-plan always carries at least a push-off and one goal move, so the one-move branch is exercised directly
    /// rather than through a pose: the running push-off must be re-marked even when the re-plan adds nothing behind it.
    /// </summary>
    [Fact]
    public void MarkRunningPushOff_OneMovePlan_MarksTheRunningPushOffLast()
    {
        PushbackPhase pushOff = RunningPushOff();

        GroundCommandHandler.MarkRunningPushOff(pushOff, PlanOfMoves(1), PushTaxiway);

        Assert.True(pushOff.IsLastMove);
        Assert.Equal(PushTaxiway, pushOff.EndTaxiway);
    }

    /// <summary>A re-plan that queues a move behind the running push-off clears the mark the push-off carried before.</summary>
    [Fact]
    public void MarkRunningPushOff_TwoMovePlan_ClearsTheRunningPushOff()
    {
        PushbackPhase pushOff = RunningPushOff();
        pushOff.IsLastMove = true;
        pushOff.EndTaxiway = PushTaxiway;

        GroundCommandHandler.MarkRunningPushOff(pushOff, PlanOfMoves(2), PushTaxiway);

        Assert.False(pushOff.IsLastMove);
        Assert.Null(pushOff.EndTaxiway);
    }

    private static PushbackPhase RunningPushOff() =>
        new()
        {
            Move = TugMove.Straight(PushbackLegKind.Push, 60.0),
            PlannedEnd = new LatLon(37.62, -122.38),
            StartsAtStand = true,
            ContinuesIntoNextMove = false,
            ContinuesStandPushOff = false,
        };

    private static TugPlan PlanOfMoves(int moveCount)
    {
        var start = new TugPose(new LatLon(37.62, -122.38), 0.0);
        List<TugMove> moves = [.. Enumerable.Range(0, moveCount).Select(_ => TugMove.Straight(PushbackLegKind.Push, 80.0))];
        TugSimulation simulation = TugKinematics.Simulate(start, moves, AircraftType, 1.0);
        return new TugPlan([.. simulation.Moves], simulation.End, [], null, null);
    }

    private AircraftState PushToCompletion(SfoGround ground, string callsign, string type, string gate, string command, int budgetSeconds)
    {
        AircraftState ac = SfoGroundHarness.SpawnParked(ground, callsign, type, gate);
        Tick(ground, ac, command, budgetSeconds);
        return ac;
    }

    private void Tick(SfoGround ground, AircraftState ac, string command, int budgetSeconds)
    {
        CommandResult push = ground.Engine.SendCommand(ac.Callsign, command);
        output.WriteLine($"'{command}' → success={push.Success} \"{push.Message}\"");
        Assert.True(push.Success, $"'{command}' was refused: {push.Message}");

        bool everPushed = false;
        int completed = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => everPushed && (ac.Phases?.CurrentPhase is not PushbackPhase),
            budgetSeconds,
            _ => everPushed |= ac.Phases?.CurrentPhase is PushbackPhase
        );
        Assert.True(completed > 0, $"'{command}' never completed within {budgetSeconds}s (phase={ac.Phases?.CurrentPhase?.Name ?? "null"})");
        output.WriteLine($"'{command}' completed t={completed}s: currentTaxiway={ac.Ground.CurrentTaxiway ?? "null"}");
    }

    /// <summary>
    /// True for a leg of one named taxiway — not the ramp lead-out and not a junction arc, whose name joins the two
    /// taxiways it transitions between (<c>"T9 - B5"</c>).
    /// </summary>
    private static bool IsNamedTaxiway(string taxiwayName) =>
        !string.Equals(taxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase) && !taxiwayName.Contains(" - ", StringComparison.Ordinal);
}
