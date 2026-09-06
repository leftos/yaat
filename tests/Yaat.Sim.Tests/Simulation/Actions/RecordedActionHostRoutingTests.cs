using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// What the router hands the host when it applies a recorded action: the records of host-owned state (ASDE-X and SAID
/// mutations) reach their slots, a weather change and a live-traffic removal reach their consumers, and a kind that is
/// never recorded — the room's clock, bookmarks — is inert from a record without the host being asked at all, so a
/// legacy <c>PAUSE</c> record can never pause a rewind.
/// </summary>
public class RecordedActionHostRoutingTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public RecordedActionHostRoutingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine() => _zoa is null ? null : AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);

    private static RecordedCommand Recorded(string command) => new(0, "", command, "XX", "conn-1");

    [Fact]
    public void RecordedAsdexAndSaidMutations_ReachTheHostSlots()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var asdex = new RecordedAsdexMutation(0, "EnableAllAlerts", null, null, null, null, null, null, null, null);
        var said = new RecordedSaidMutation(0, "Tag", null, AiTestFixture.Callsign, null, null, null, null, null, null);

        engine.Actions.ApplyRecorded(asdex, host);
        engine.Actions.ApplyRecorded(said, host);

        Assert.Same(asdex, Assert.Single(host.AsdexMutations));
        Assert.Same(said, Assert.Single(host.SaidMutations));
    }

    [Fact]
    public void RecordedWeatherChange_NotifiesTheHost()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        engine.Actions.ApplyRecorded(new RecordedWeatherChange(0, null, false), host);

        Assert.Equal(1, host.WeatherChanges);
        Assert.Null(engine.World.Weather);
        Assert.Null(engine.Scenario!.MetarIssuer);
    }

    [Fact]
    public void RecordedLiveTrafficRemoval_NotifiesTheHostOfTheDeletion()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        engine.Actions.ApplyRecorded(new RecordedLiveTrafficRemoval(0, "SHADOW1", LiveTrafficRemovalReason.Dropped), host);

        Assert.Equal("SHADOW1", Assert.Single(host.DeletedCallsigns));
    }

    [Fact]
    public void NeverRecordedKinds_AreInertFromARecord_WithoutAskingTheHost()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        var pause = engine.Actions.Apply(Recorded("PAUSE"), host);
        var bookmark = engine.Actions.Apply(Recorded("BM ADD test"), host);

        Assert.False(pause.Result.Success);
        Assert.Equal(new ActionTrace(RecordedCommandKind.Transport, ActionScope.Global, IsHostSlot: true), pause.Trace);
        Assert.False(bookmark.Result.Success);
        Assert.Equal(new ActionTrace(RecordedCommandKind.Bookmark, ActionScope.Global, IsHostSlot: true), bookmark.Trace);
        Assert.Equal(0, host.TransportApplies);
    }
}
