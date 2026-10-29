using System.Text.Json;
using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Asdex;

/// <summary>
/// A tower position's arrival-alert inhibit lapses 180 sim seconds after it was set, on every run kind, and a re-push that
/// keeps it does not restart its timer. An <c>ARWY</c> re-pick clears the inhibits and remembers the configuration it
/// moved away from for 10 sim seconds: a push carrying that id in the window is a stale display's, not a CRC user's
/// choice, so the stored configuration stays and only the push's closures apply.
/// </summary>
public class AsdexInhibitLapseAndStalePushTests
{
    private const string L28D01 = "01GVF4AGA8HX03NSN8QJS4WJS4";
    private const string L10D10 = "01GVM1VF16S824B4JHH1YR1FZR";
    private const string Lc = "01HBGNH9A19W2WK9Y3GJ0QM811";
    private const string Gc = "01HBGNHVY3DTSV477ET4MPSYS0";

    public AsdexInhibitLapseAndStalePushTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static SimulationEngine Engine()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(
            AiTestFixture.ParkedAtOak,
            7,
            MagneticDeclination.EvaluationDateUtc,
            scenario => scenario.ArtccConfig = ZoaArtccConfig.Full
        );
        return engine;
    }

    private static void Arwy(SimulationEngine engine, string command)
    {
        CommandResult result = engine.Actions.Issue(new ActionInput("", command, "conn-1", "XX", Baked: null)).Result;
        Assert.True(result.Success, result.Message);
    }

    private static AsdexSafetyLogicConfig Sfo(SimulationEngine engine) => engine.Scenario!.AsdexSafetyLogicConfigs["SFO"];

    private static void Push(SimulationEngine engine, AsdexSafetyLogicConfig config) =>
        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(engine.Scenario!.ElapsedSeconds, "SFO", config));

    /// <summary>What CRC pushes: the whole configuration as the display holds it, which carries none of the derived state.</summary>
    private static AsdexSafetyLogicConfig AsPushed(AsdexSafetyLogicConfig config) =>
        config with
        {
            RunwayConfigurationChosen = false,
            InhibitStartSeconds = AsdexSafetyLogicConfig.NoSeconds,
            SupersededConfigurationIds = AsdexSafetyLogicConfig.NoSeconds,
        };

    private static AsdexSafetyLogicConfig WithFirstRunwayClosed(AsdexSafetyLogicConfig config) =>
        config with
        {
            Runways = [config.Runways[0] with { IsClosed = true }, .. config.Runways.Skip(1)],
        };

    private static void Tick(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }

    /// <summary>SFO re-picked from L28D01 to L10D10 by an <c>ARWY</c> change; returns the second of the re-pick.</summary>
    private static double RepickedToL10D10(SimulationEngine engine)
    {
        Arwy(engine, "ARWY SFO A28L A28R D1L D1R");
        Assert.Equal(L28D01, Sfo(engine).RunwayConfigurationId);
        double repickedAt = engine.Scenario!.ElapsedSeconds;
        Arwy(engine, "ARWY SFO 10L 10R");
        Assert.Equal(L10D10, Sfo(engine).RunwayConfigurationId);
        return repickedAt;
    }

    [Fact]
    public void InhibitLapse_After180SimSeconds_ClearsThePosition()
    {
        SimulationEngine engine = Engine();
        var host = new SpineCapturingHost(engine);
        Push(engine, AsPushed(Sfo(engine)) with { InhibitedArrivalAlertPositionIds = [Lc] });

        for (int i = 0; i < 179; i++)
        {
            engine.RunSecond(host);
        }

        Assert.Equal([Lc], Sfo(engine).InhibitedArrivalAlertPositionIds);
        host.AsdexSafetyLogicConfigChanges.Clear();

        engine.RunSecond(host);

        Assert.Empty(Sfo(engine).InhibitedArrivalAlertPositionIds);
        Assert.Empty(Sfo(engine).InhibitStartSeconds);
        Assert.Equal(["SFO"], Assert.Single(host.AsdexSafetyLogicConfigChanges));
    }

    [Fact]
    public void InhibitLapse_RePushKeepingThePosition_DoesNotRestartItsTimer()
    {
        SimulationEngine engine = Engine();
        Push(engine, AsPushed(Sfo(engine)) with { InhibitedArrivalAlertPositionIds = [Lc] });
        Tick(engine, 100);

        Push(engine, AsPushed(Sfo(engine)));
        Tick(engine, 79);
        Assert.Equal([Lc], Sfo(engine).InhibitedArrivalAlertPositionIds);

        Tick(engine, 1);

        Assert.Empty(Sfo(engine).InhibitedArrivalAlertPositionIds);
    }

    [Fact]
    public void InhibitLapse_ReplayMatchesLive()
    {
        SimulationEngine engine = Engine();
        Tick(engine, 3);
        SimScenarioState scenario = engine.Scenario!;
        double pushedAt = scenario.ElapsedSeconds;
        AsdexSafetyLogicConfig pushed = AsPushed(Sfo(engine)) with { InhibitedArrivalAlertPositionIds = [Lc] };
        CommandResult result = engine.Actions.IssueDerived(new RecordedAsdexSafetyLogicChange(pushedAt, "SFO", pushed));
        Assert.True(result.Success, result.Message);
        Tick(engine, 200);
        Assert.Empty(Sfo(engine).InhibitedArrivalAlertPositionIds);

        var recording = new SessionRecording
        {
            ScenarioJson = AiTestFixture.ParkedAtOak,
            RngSeed = 7,
            Actions = [.. scenario.ActionLog],
            TotalElapsedSeconds = scenario.ElapsedSeconds,
            ArtccConfigJson = JsonSerializer.Serialize(ZoaArtccConfig.Full, RecordingJsonOptions.Default),
            SessionStartUtc = MagneticDeclination.EvaluationDateUtc,
            StudentPositionState = new ReplayStudentPosition(scenario.StudentPosition, scenario.StudentTcp, scenario.StudentPositionType, false),
        };

        var midway = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        midway.Replay(recording, pushedAt + 100);
        Assert.Equal([Lc], Sfo(midway).InhibitedArrivalAlertPositionIds);
        Assert.Equal(pushedAt, Sfo(midway).InhibitStartSeconds[Lc]);

        var replayed = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        replayed.Replay(recording, scenario.ElapsedSeconds);

        AsdexSafetyLogicConfig live = Sfo(engine);
        AsdexSafetyLogicConfig replay = Sfo(replayed);
        Assert.Equal(live.RunwayConfigurationId, replay.RunwayConfigurationId);
        Assert.Equal(live.InhibitedArrivalAlertPositionIds, replay.InhibitedArrivalAlertPositionIds);
        Assert.Equal(live.InhibitStartSeconds, replay.InhibitStartSeconds);
        Assert.Equal(live.RunwayConfigurationChosen, replay.RunwayConfigurationChosen);
    }

    [Fact]
    public void Repick_ClearsInhibits_AndSupersedesTheOldId()
    {
        SimulationEngine engine = Engine();
        Arwy(engine, "ARWY SFO A28L A28R D1L D1R");
        Push(engine, AsPushed(Sfo(engine)) with { InhibitedArrivalAlertPositionIds = [Lc] });
        Tick(engine, 5);
        double repickedAt = engine.Scenario!.ElapsedSeconds;

        Arwy(engine, "ARWY SFO 10L 10R");

        AsdexSafetyLogicConfig sfo = Sfo(engine);
        Assert.Equal(L10D10, sfo.RunwayConfigurationId);
        Assert.Empty(sfo.InhibitedArrivalAlertPositionIds);
        Assert.Empty(sfo.InhibitStartSeconds);
        Assert.Equal(repickedAt, sfo.SupersededConfigurationIds[L28D01]);
    }

    [Fact]
    public void StalePush_WithSupersededId_WithinWindow_KeepsTheConfiguration_AppliesItsClosure_NotChosen()
    {
        SimulationEngine engine = Engine();
        Arwy(engine, "ARWY SFO A28L A28R D1L D1R");
        AsdexSafetyLogicConfig displayView = AsPushed(Sfo(engine));
        RepickedToL10D10(engine);
        Tick(engine, 3);

        Push(engine, WithFirstRunwayClosed(displayView));

        AsdexSafetyLogicConfig sfo = Sfo(engine);
        Assert.Equal(L10D10, sfo.RunwayConfigurationId);
        Assert.True(sfo.Runways[0].IsClosed);
        Assert.False(sfo.RunwayConfigurationChosen);
    }

    [Fact]
    public void StalePush_AfterWindow_IsAChoice()
    {
        SimulationEngine engine = Engine();
        Arwy(engine, "ARWY SFO A28L A28R D1L D1R");
        AsdexSafetyLogicConfig displayView = AsPushed(Sfo(engine));
        RepickedToL10D10(engine);
        Tick(engine, 11);

        Push(engine, WithFirstRunwayClosed(displayView) with { InhibitedArrivalAlertPositionIds = [Lc] });

        AsdexSafetyLogicConfig sfo = Sfo(engine);
        Assert.Equal(L28D01, sfo.RunwayConfigurationId);
        Assert.True(sfo.RunwayConfigurationChosen);
        Assert.Empty(sfo.InhibitedArrivalAlertPositionIds);
        Assert.Empty(sfo.InhibitStartSeconds);
    }

    [Fact]
    public void StalePush_WithClosureChange_KeepsStoredInhibits()
    {
        SimulationEngine engine = Engine();
        AsdexSafetyLogicConfig displayView = AsPushed(Sfo(engine)) with { InhibitedArrivalAlertPositionIds = [Lc] };
        Push(engine, displayView);
        Tick(engine, 180);
        Assert.Empty(Sfo(engine).InhibitedArrivalAlertPositionIds);

        Push(engine, WithFirstRunwayClosed(displayView));

        AsdexSafetyLogicConfig sfo = Sfo(engine);
        Assert.True(sfo.Runways[0].IsClosed);
        Assert.Empty(sfo.InhibitedArrivalAlertPositionIds);
        Assert.Empty(sfo.InhibitStartSeconds);
    }

    [Fact]
    public void StalePush_InhibitToggle_WithinWindow_KeepsTheStoredInhibits()
    {
        SimulationEngine engine = Engine();
        Arwy(engine, "ARWY SFO A28L A28R D1L D1R");
        Push(engine, AsPushed(Sfo(engine)) with { InhibitedArrivalAlertPositionIds = [Lc] });
        AsdexSafetyLogicConfig displayView = AsPushed(Sfo(engine));
        Arwy(engine, "ARWY SFO 10L 10R");
        Assert.Empty(Sfo(engine).InhibitedArrivalAlertPositionIds);
        Tick(engine, 1);

        Push(engine, displayView with { InhibitedArrivalAlertPositionIds = [Lc, Gc] });

        AsdexSafetyLogicConfig sfo = Sfo(engine);
        Assert.Equal(L10D10, sfo.RunwayConfigurationId);
        Assert.Empty(sfo.InhibitedArrivalAlertPositionIds);
        Assert.Empty(sfo.InhibitStartSeconds);
        Assert.False(sfo.RunwayConfigurationChosen);
    }

    [Fact]
    public void InhibitToggle_WithReorderedRunwaysAndAnExtraOpenRunway_AppliesTheInhibit()
    {
        SimulationEngine engine = Engine();
        AsdexSafetyLogicConfig stored = AsPushed(Sfo(engine));
        double pushedAt = engine.Scenario!.ElapsedSeconds;

        Push(
            engine,
            stored with
            {
                Runways = [.. stored.Runways.Reverse(), new AsdexRunwayConfig("4-22", stored.Runways[0].AreaPoints, IsClosed: false)],
                InhibitedArrivalAlertPositionIds = [Lc],
            }
        );

        AsdexSafetyLogicConfig sfo = Sfo(engine);
        Assert.Equal([Lc], sfo.InhibitedArrivalAlertPositionIds);
        Assert.Equal(new Dictionary<string, double> { [Lc] = pushedAt }, sfo.InhibitStartSeconds);
    }

    [Fact]
    public void InhibitLapse_PartialLapse()
    {
        SimulationEngine engine = Engine();
        double lcAt = engine.Scenario!.ElapsedSeconds;
        Push(engine, AsPushed(Sfo(engine)) with { InhibitedArrivalAlertPositionIds = [Lc] });
        Tick(engine, 60);
        double gcAt = engine.Scenario.ElapsedSeconds;
        Assert.Equal(lcAt + 60, gcAt);
        Push(engine, AsPushed(Sfo(engine)) with { InhibitedArrivalAlertPositionIds = [Lc, Gc] });

        Tick(engine, 119);
        Assert.Equal([Lc, Gc], Sfo(engine).InhibitedArrivalAlertPositionIds);

        Tick(engine, 1);
        Assert.Equal([Gc], Sfo(engine).InhibitedArrivalAlertPositionIds);
        Assert.Equal(new Dictionary<string, double> { [Gc] = gcAt }, Sfo(engine).InhibitStartSeconds);

        Tick(engine, 59);
        Assert.Equal([Gc], Sfo(engine).InhibitedArrivalAlertPositionIds);

        Tick(engine, 1);
        Assert.Empty(Sfo(engine).InhibitedArrivalAlertPositionIds);
        Assert.Empty(Sfo(engine).InhibitStartSeconds);
    }

    [Fact]
    public void StalePush_AtExactlyTheWindowEdge_IsAChoice()
    {
        SimulationEngine engine = Engine();
        Arwy(engine, "ARWY SFO A28L A28R D1L D1R");
        AsdexSafetyLogicConfig displayView = AsPushed(Sfo(engine));
        double repickedAt = RepickedToL10D10(engine);
        Tick(engine, (int)AsdexSafetyLogicConfig.SupersededIdWindowSeconds);
        Assert.Equal(repickedAt + AsdexSafetyLogicConfig.SupersededIdWindowSeconds, engine.Scenario!.ElapsedSeconds);

        Push(engine, WithFirstRunwayClosed(displayView));

        AsdexSafetyLogicConfig sfo = Sfo(engine);
        Assert.Equal(L28D01, sfo.RunwayConfigurationId);
        Assert.True(sfo.RunwayConfigurationChosen);
    }

    [Fact]
    public void StaleWindow_ReplayMatchesLive()
    {
        SimulationEngine engine = Engine();
        Arwy(engine, "ARWY SFO A28L A28R D1L D1R");
        AsdexSafetyLogicConfig displayView = AsPushed(Sfo(engine));
        Tick(engine, 2);
        Arwy(engine, "ARWY SFO 10L 10R");
        Tick(engine, 3);
        SimScenarioState scenario = engine.Scenario!;
        AsdexSafetyLogicConfig stale = WithFirstRunwayClosed(displayView) with { InhibitedArrivalAlertPositionIds = [Lc] };
        CommandResult result = engine.Actions.IssueDerived(new RecordedAsdexSafetyLogicChange(scenario.ElapsedSeconds, "SFO", stale));
        Assert.True(result.Success, result.Message);
        Tick(engine, 2);
        Assert.Equal(L10D10, Sfo(engine).RunwayConfigurationId);
        Assert.NotEmpty(Sfo(engine).SupersededConfigurationIds);

        var recording = new SessionRecording
        {
            ScenarioJson = AiTestFixture.ParkedAtOak,
            RngSeed = 7,
            Actions = [.. scenario.ActionLog],
            TotalElapsedSeconds = scenario.ElapsedSeconds,
            ArtccConfigJson = JsonSerializer.Serialize(ZoaArtccConfig.Full, RecordingJsonOptions.Default),
            SessionStartUtc = MagneticDeclination.EvaluationDateUtc,
            StudentPositionState = new ReplayStudentPosition(scenario.StudentPosition, scenario.StudentTcp, scenario.StudentPositionType, false),
        };
        var replayed = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        replayed.Replay(recording, scenario.ElapsedSeconds);

        AsdexSafetyLogicConfig live = Sfo(engine);
        AsdexSafetyLogicConfig replay = Sfo(replayed);
        Assert.Equal(live.RunwayConfigurationId, replay.RunwayConfigurationId);
        Assert.Equal(live.Runways.Select(r => (r.Id, r.IsClosed)), replay.Runways.Select(r => (r.Id, r.IsClosed)));
        Assert.Equal(live.InhibitedArrivalAlertPositionIds, replay.InhibitedArrivalAlertPositionIds);
        Assert.Equal(live.InhibitStartSeconds, replay.InhibitStartSeconds);
        Assert.Equal(live.SupersededConfigurationIds, replay.SupersededConfigurationIds);
        Assert.Equal(live.RunwayConfigurationChosen, replay.RunwayConfigurationChosen);
    }

    [Fact]
    public void Repick_BackToASupersededId_DropsItFromTheSupersededSet()
    {
        SimulationEngine engine = Engine();
        double repickedAt = RepickedToL10D10(engine);
        Tick(engine, 2);
        double backAt = engine.Scenario!.ElapsedSeconds;

        Arwy(engine, "ARWY SFO A28L A28R D1L D1R");

        AsdexSafetyLogicConfig sfo = Sfo(engine);
        Assert.Equal(L28D01, sfo.RunwayConfigurationId);
        Assert.Equal(new Dictionary<string, double> { [L10D10] = backAt }, sfo.SupersededConfigurationIds);
        Assert.True(backAt < repickedAt + AsdexSafetyLogicConfig.SupersededIdWindowSeconds);
    }

    [Fact]
    public void Repick_DropsSupersededEntriesPastTheWindow()
    {
        SimulationEngine engine = Engine();
        RepickedToL10D10(engine);
        Tick(engine, (int)AsdexSafetyLogicConfig.SupersededIdWindowSeconds + 1);
        double nextAt = engine.Scenario!.ElapsedSeconds;

        Arwy(engine, "ARWY SFO 28L 28R");

        AsdexSafetyLogicConfig sfo = Sfo(engine);
        Assert.NotEqual(L10D10, sfo.RunwayConfigurationId);
        Assert.Equal(new Dictionary<string, double> { [L10D10] = nextAt }, sfo.SupersededConfigurationIds);
    }

    [Fact]
    public void Restore_DropsPendingConfigChangeMarks()
    {
        SimulationEngine engine = Engine();
        engine.DrainAsdexSafetyLogicConfigChanges();
        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        Push(engine, AsPushed(Sfo(engine)) with { InhibitedArrivalAlertPositionIds = [Lc] });

        engine.RestoreFromSnapshot(snapshot);

        Assert.Empty(engine.DrainAsdexSafetyLogicConfigChanges());
    }

    [Fact]
    public void Snapshot_RoundTripsStartsAndSupersededIds()
    {
        SimulationEngine engine = Engine();
        double repickedAt = RepickedToL10D10(engine);
        Tick(engine, 2);
        double inhibitedAt = engine.Scenario!.ElapsedSeconds;
        Push(engine, AsPushed(Sfo(engine)) with { InhibitedArrivalAlertPositionIds = [Lc] });

        StateSnapshotDto reread = JsonSerializer.Deserialize<StateSnapshotDto>(JsonSerializer.Serialize(engine.CaptureSnapshot()))!;
        engine.Scenario.AsdexSafetyLogicConfigs = AsdexSafetyLogicSeed.Empty;
        engine.RestoreFromSnapshot(reread);

        AsdexSafetyLogicConfig sfo = Sfo(engine);
        Assert.Equal([Lc], sfo.InhibitedArrivalAlertPositionIds);
        Assert.Equal(new Dictionary<string, double> { [Lc] = inhibitedAt }, sfo.InhibitStartSeconds);
        Assert.Equal(new Dictionary<string, double> { [L28D01] = repickedAt }, sfo.SupersededConfigurationIds);
    }

    [Fact]
    public void Snapshot_WithoutStarts_RestoresStartAtSnapshotSecond()
    {
        const string json = $$"""{"Runways":[],"RunwayConfigurationId":"{{L28D01}}","InhibitedArrivalAlertPositionIds":["{{Lc}}"]}""";

        var config = AsdexSafetyLogicConfig.FromSnapshot(JsonSerializer.Deserialize<AsdexSafetyLogicConfigDto>(json)!, 42.0);

        Assert.Equal([Lc], config.InhibitedArrivalAlertPositionIds);
        Assert.Equal(new Dictionary<string, double> { [Lc] = 42.0 }, config.InhibitStartSeconds);
        Assert.Empty(config.SupersededConfigurationIds);
    }
}
