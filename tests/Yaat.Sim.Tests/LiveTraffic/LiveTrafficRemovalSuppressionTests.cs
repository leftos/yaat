using Xunit;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.LiveTraffic;

/// <summary>
/// What a replayed <see cref="RecordedLiveTrafficRemoval"/> tells the host. The removal an instructor's <c>DEL</c>
/// wrote re-raises the host's feed suppression, because the replayed <c>DEL</c> text cannot: the record takes the
/// shadow out of the world first, so the command refuses at the aircraft-exists guard and never reaches the arm that
/// hides it. A removal the feed produced (stale, out of scope, disabled) suppresses nothing — that shadow is meant to
/// come back the moment the feed re-supplies it.
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
    public void ADeletedRemoval_RaisesTheHostsFeedSuppressionOnce()
    {
        var engine = EngineWithShadow();
        var host = new AttendanceActionHost();

        engine.Actions.ApplyRecorded(new RecordedLiveTrafficRemoval(0, Callsign, LiveTrafficRemovalReason.Deleted), host);

        Assert.Equal(Callsign, Assert.Single(host.HiddenLiveTraffic));
        Assert.Equal(Callsign, Assert.Single(host.DeletedCallsigns));
        Assert.Null(engine.World.FindAircraft(Callsign));
    }

    /// <summary>
    /// Ordering, not just the call: the consumer fires <b>after</b> the shadow has left the world, so the host's own
    /// teardown behind it (the room's <c>ShadowTrafficSync.Remove</c>) finds nothing to remove and writes no second
    /// removal. Hoisting the notification above <c>World.RemoveAircraft</c> turns this red.
    /// </summary>
    [Fact]
    public void TheSuppressionConsumer_FiresAfterTheShadowHasLeftTheWorld()
    {
        var engine = EngineWithShadow();
        var host = new AttendanceActionHost();
        bool? stillInWorldWhenTold = null;
        host.WhenLiveTrafficHidden = callsign => stillInWorldWhenTold = engine.World.FindAircraft(callsign) is not null;

        engine.Actions.ApplyRecorded(new RecordedLiveTrafficRemoval(0, Callsign, LiveTrafficRemovalReason.Deleted), host);

        Assert.Equal(Callsign, Assert.Single(host.HiddenLiveTraffic));
        Assert.False(stillInWorldWhenTold, "the suppression consumer must be told after the world removal, not before");
    }

    [Fact]
    public void AFeedRemoval_TakesTheShadowOut_WithoutSuppressingTheCallsign()
    {
        var engine = EngineWithShadow();
        var host = new AttendanceActionHost();

        engine.Actions.ApplyRecorded(new RecordedLiveTrafficRemoval(0, Callsign, LiveTrafficRemovalReason.OutOfScope), host);

        Assert.Empty(host.HiddenLiveTraffic);
        Assert.Equal(Callsign, Assert.Single(host.DeletedCallsigns));
        Assert.Null(engine.World.FindAircraft(Callsign));
    }
}
