using Xunit;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.LiveTraffic;

/// <summary>
/// What a replayed <see cref="RecordedLiveTrafficRemoval"/> does to the scenario's hidden set
/// (<see cref="SimScenarioState.SuppressedLiveTraffic"/>). The removal an instructor's <c>DEL</c> wrote hides the callsign again,
/// because the replayed <c>DEL</c> text cannot: the record takes the shadow out of the world first, so the command
/// refuses at the aircraft-exists guard and never reaches the arm that hides it. A removal the feed produced (stale, out
/// of scope, disabled) hides nothing — that shadow is meant to come back the moment the feed re-supplies it.
/// </summary>
public class LiveTrafficRemovalSuppressionTests
{
    private const string Callsign = "UAL123";

    public LiveTrafficRemovalSuppressionTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static SimulationEngine EngineWithShadow()
    {
        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
            },
        };
        engine.World.AddAircraft(
            LiveTrafficKinematics.CreateShadow(
                Callsign,
                "B738",
                new LiveTrafficSample(0, 37.9, -121.3, 11_000, 300, 90, 0, LiveTrafficSource.Stars, 4521),
                new AircraftFlightPlan { HasFlightPlan = true }
            )
        );
        return engine;
    }

    [Fact]
    public void ADeletedRemoval_HidesTheCallsignOnce()
    {
        SimulationEngine engine = EngineWithShadow();
        var host = new AttendanceActionHost();

        engine.Actions.ApplyRecorded(new RecordedLiveTrafficRemoval(0, Callsign, LiveTrafficRemovalReason.Deleted), host);

        Assert.Equal(Callsign, Assert.Single(engine.Scenario!.SuppressedLiveTraffic));
        Assert.Equal(Callsign, Assert.Single(host.DeletedCallsigns));
        Assert.Null(engine.World.FindAircraft(Callsign));
    }

    /// <summary>
    /// The hide does not depend on the shadow being there to remove: a reconstruction whose world never rebuilt it (the
    /// feed's samples before the <c>DEL</c> fell outside what it replays) still has to keep the feed from spawning it.
    /// </summary>
    [Fact]
    public void ADeletedRemoval_OfAShadowNoLongerInTheWorld_StillHidesTheCallsign()
    {
        SimulationEngine engine = EngineWithShadow();
        engine.World.RemoveAircraft(Callsign);
        var host = new AttendanceActionHost();

        engine.Actions.ApplyRecorded(new RecordedLiveTrafficRemoval(0, Callsign, LiveTrafficRemovalReason.Deleted), host);

        Assert.Equal(Callsign, Assert.Single(engine.Scenario!.SuppressedLiveTraffic));
    }

    [Fact]
    public void AFeedRemoval_TakesTheShadowOut_WithoutHidingTheCallsign()
    {
        SimulationEngine engine = EngineWithShadow();
        var host = new AttendanceActionHost();

        engine.Actions.ApplyRecorded(new RecordedLiveTrafficRemoval(0, Callsign, LiveTrafficRemovalReason.OutOfScope), host);

        Assert.Empty(engine.Scenario!.SuppressedLiveTraffic);
        Assert.Equal(Callsign, Assert.Single(host.DeletedCallsigns));
        Assert.Null(engine.World.FindAircraft(Callsign));
    }
}
