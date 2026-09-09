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
/// What the router hands the host when it applies a recorded action: the ASDE-X and SAID mutations apply to the engine
/// and hand over only a terminate, a weather change and a live-traffic removal reach their consumers, and a kind that is
/// never recorded — the session clock, bookmarks — is inert from a record: refused in the router before its arm runs,
/// so the Sim bodies behind both are never reached, and a legacy <c>PAUSE</c> or <c>BM</c> record can never pause a
/// rewind or re-add a bookmark to it.
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

    /// <summary>
    /// The ASDE-X and SAID mutations apply to the engine — the display state is the aircraft's — and the host hears
    /// only about a terminate, the one-shot delete the additive CRC topics need. <c>AsdexMutationStepTests</c> covers
    /// each kind; this pins that the router reaches the engine body rather than a slot.
    /// </summary>
    [Fact]
    public void RecordedAsdexAndSaidMutations_ApplyToTheEngine_AndNotifyTheHostOnTerminate()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var callsign = AiTestFixture.Callsign;
        engine.FindAircraft(callsign)!.Stars.AsdexAlertsInhibited = true;
        var asdex = new RecordedAsdexMutation(0, "EnableAllAlerts", null, null, null, null, null, null, null, null);
        var said = new RecordedSaidMutation(0, "Terminate", callsign, null, null, null, null, null, null, null);

        engine.Actions.ApplyRecorded(asdex, host);
        engine.Actions.ApplyRecorded(said, host);

        var stars = engine.FindAircraft(callsign)!.Stars;
        Assert.False(stars.AsdexAlertsInhibited);
        Assert.True(stars.SaidTerminated);
        Assert.Equal(callsign, Assert.Single(host.SaidTerminations));
        Assert.Empty(host.AsdexTerminations);
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
    public void NeverRecordedKinds_AreInertFromARecord_LeavingTheScenarioStateUntouched()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        engine.Scenario!.IsPaused = false;

        var pause = engine.Actions.Apply(Recorded("PAUSE"), host);
        var bookmark = engine.Actions.Apply(Recorded("BM ADD test"), host);

        Assert.False(pause.Result.Success);
        Assert.Equal(new ActionTrace(RecordedCommandKind.Transport, ActionScope.Global), pause.Trace);
        Assert.False(engine.Scenario!.IsPaused);
        Assert.Equal(0, host.SimStateChanges);
        Assert.False(bookmark.Result.Success);
        Assert.Equal(new ActionTrace(RecordedCommandKind.Bookmark, ActionScope.Global), bookmark.Trace);
        Assert.Empty(engine.Scenario!.Bookmarks);
        Assert.Equal(0, host.BookmarkChanges);
    }
}
