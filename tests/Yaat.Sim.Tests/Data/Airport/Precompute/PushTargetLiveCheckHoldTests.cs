using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Simulation;
using static Yaat.Sim.Tests.Data.Airport.Precompute.PushTargetLiveCheckFixtures;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// The live check and the simulation agree: a push off OAK gate 26 the check blocks on the B738 on gate 27 is held
/// short of it.
/// </summary>
public class PushTargetLiveCheckHoldTests(ITestOutputHelper output)
{
    /// <summary>
    /// Gate 26's group-III <c>PUSH TE</c> with B738s on gates 25 and 27: the live check blocks it on the B738 parked on
    /// 27, and the push is held short of that aircraft rather than completing
    /// (<see cref="Gate26PushHeldByTheB738OnGate27"/>).
    /// </summary>
    [Fact]
    public void Oak_Gate26_PushTe_B738OnGate27_HeldAsTheLiveCheckSays() => Gate26PushHeldByTheB738OnGate27("TE");

    /// <summary>
    /// Gate 26's group-III <c>PUSH TC</c> with B738s on gates 25 and 27: the live check blocks it on the B738 parked on
    /// 27, and the push is held short of that aircraft rather than completing
    /// (<see cref="Gate26PushHeldByTheB738OnGate27"/>).
    /// </summary>
    [Fact]
    public void Oak_Gate26_PushTc_B738OnGate27_HeldAsTheLiveCheckSays() => Gate26PushHeldByTheB738OnGate27("TC");

    /// <summary>
    /// With B738s on gates 25, 26 and 27, the live check blocks gate 26's group-III <c>PUSH <paramref name="taxilane"/></c>
    /// on the B738 parked on 27, and the simulation holds the same push short of that aircraft instead of completing it.
    /// The live check, which re-flies the stored plan, and the simulation, which plans the push live, must agree.
    /// </summary>
    /// <param name="taxilane">The taxilane the push is onto, <c>TE</c> or <c>TC</c>.</param>
    private void Gate26PushHeldByTheB738OnGate27(string taxilane)
    {
        SimulationEngine? engine = Issue222PushbackParkedNeighborTests.BuildEngine(output);
        Assert.NotNull(engine);
        AirportGroundLayout layout = PushTargetPlannerTests.Oak();
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-oak-gate26-push-hold",
            ScenarioName = "OAK gate 26 push hold",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
        };
        AircraftState subject = Issue222PushbackParkedNeighborTests.SpawnParked(engine, layout, Subject, Narrowbody, "26");
        _ = Issue222PushbackParkedNeighborTests.SpawnParked(engine, layout, Gate25Neighbour, Narrowbody, "25");
        _ = Issue222PushbackParkedNeighborTests.SpawnParked(engine, layout, Gate27Neighbour, Narrowbody, "27");

        PushTargetLiveVerdict verdict = PushTargetLiveCheck.Check(
            Target(PushTargetPlannerFixtures.OakEntries.Value, "26", GroupOf(Narrowbody), taxilane),
            Request(
                Parked(Subject, Narrowbody, StartOf(Stand(layout, "26")), "26"),
                AircraftFootprint.FromType(Narrowbody),
                [
                    Parked(Gate25Neighbour, Narrowbody, StartOf(Stand(layout, "25")), "25"),
                    Parked(Gate27Neighbour, Narrowbody, StartOf(Stand(layout, "27")), "27"),
                ]
            )
        );
        CommandResult push = engine.SendCommand(Subject, $"PUSH {taxilane}");
        Assert.True(push.Success, $"PUSH {taxilane} off gate 26 was refused: {push.Message}");
        for (int tick = 1; tick <= HoldSeconds; tick++)
        {
            engine.TickOneSecond();
        }

        output.WriteLine(
            $"{Subject} PUSH {taxilane}: live check {verdict}; after {HoldSeconds}s phase {subject.Phases?.CurrentPhase?.Name}, "
                + $"yielding to {subject.Ground.AutoYieldTarget ?? "-"}, speed {subject.GroundSpeed:F2} kt"
        );
        Assert.Equal(PushTargetLiveVerdict.Blocked(Gate27Neighbour), verdict);
        Assert.Equal(PushbackPhaseName, subject.Phases?.CurrentPhase?.Name);
        Assert.Equal(Gate27Neighbour, subject.Ground.AutoYieldTarget);
        Assert.True(
            subject.GroundSpeed < HeldSpeedKts,
            $"{Subject} is moving at {subject.GroundSpeed:F2} kt after {HoldSeconds}s held on {Gate27Neighbour}"
        );
    }
}
