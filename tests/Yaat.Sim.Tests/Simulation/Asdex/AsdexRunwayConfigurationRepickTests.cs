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
/// An <c>ARWY</c> change re-picks the runway configuration of the ASDE-X facility at the airport it changed, from the
/// real ZOA config and navdata, unless a CRC push has chosen a different configuration for it. A push that keeps the id
/// (the closed-runway and inhibit toggles) chooses nothing. The choice is snapshotted, and the re-pick replays.
/// </summary>
public class AsdexRunwayConfigurationRepickTests
{
    private const string L28D01 = "01GVF4AGA8HX03NSN8QJS4WJS4";
    private const string L19D10 = "01GVM1TJ6YF6HE25H6E4RD9DA2";
    private const string L10D10 = "01GVM1VF16S824B4JHH1YR1FZR";

    public AsdexRunwayConfigurationRepickTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>A loaded engine whose ARTCC config is in place at the seed, as a room load and a replay have it.</summary>
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

    /// <summary>What CRC pushes: the whole configuration as the display holds it, which carries no chosen flag.</summary>
    private static AsdexSafetyLogicConfig AsPushed(AsdexSafetyLogicConfig config) => config with { RunwayConfigurationChosen = false };

    [Fact]
    public void Arwy_ChangingSfosRunways_RepicksItsConfiguration()
    {
        SimulationEngine engine = Engine();

        Arwy(engine, "ARWY SFO A28L A28R D1L D1R");
        Assert.Equal(L28D01, Sfo(engine).RunwayConfigurationId);

        Arwy(engine, "ARWY SFO 10L 10R");

        Assert.Equal(L10D10, Sfo(engine).RunwayConfigurationId);
        Assert.False(Sfo(engine).RunwayConfigurationChosen);
    }

    [Fact]
    public void Arwy_AfterAPushChoosingAnotherConfiguration_KeepsTheChoice()
    {
        SimulationEngine engine = Engine();

        Arwy(engine, "ARWY SFO A28L A28R D1L D1R");
        Push(engine, AsPushed(Sfo(engine)) with { RunwayConfigurationId = L19D10 });
        Assert.True(Sfo(engine).RunwayConfigurationChosen);

        Arwy(engine, "ARWY SFO 10L 10R");

        Assert.Equal(L19D10, Sfo(engine).RunwayConfigurationId);
        Assert.True(Sfo(engine).RunwayConfigurationChosen);
    }

    [Fact]
    public void Arwy_AfterAPushThatOnlyClosesARunway_StillRepicks()
    {
        SimulationEngine engine = Engine();

        Arwy(engine, "ARWY SFO A28L A28R D1L D1R");
        AsdexSafetyLogicConfig current = Sfo(engine);
        AsdexRunwayConfig closed = current.Runways[0] with { IsClosed = true };
        Push(engine, AsPushed(current) with { Runways = [closed, .. current.Runways.Skip(1)] });
        Assert.False(Sfo(engine).RunwayConfigurationChosen);
        Assert.True(Sfo(engine).Runways[0].IsClosed);

        Arwy(engine, "ARWY SFO 10L 10R");

        Assert.Equal(L10D10, Sfo(engine).RunwayConfigurationId);
        Assert.True(Sfo(engine).Runways[0].IsClosed);
    }

    [Fact]
    public void Arwy_ChangingOnlyAnotherAirport_LeavesSfoUntouched()
    {
        SimulationEngine engine = Engine();

        Arwy(engine, "ARWY SFO 10L 10R");
        AsdexSafetyLogicConfig before = Sfo(engine);

        Arwy(engine, "ARWY OAK 28R 30");

        Assert.Same(before, Sfo(engine));
        Assert.Equal(L10D10, Sfo(engine).RunwayConfigurationId);
    }

    [Fact]
    public void TheChosenFlag_SurvivesASnapshotRoundTrip()
    {
        SimulationEngine engine = Engine();

        Push(engine, AsPushed(Sfo(engine)) with { RunwayConfigurationId = L19D10 });
        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        StateSnapshotDto reread = JsonSerializer.Deserialize<StateSnapshotDto>(JsonSerializer.Serialize(snapshot))!;

        engine.Scenario!.AsdexSafetyLogicConfigs = AsdexSafetyLogicSeed.Empty;
        engine.RestoreFromSnapshot(reread);

        Assert.Equal(L19D10, Sfo(engine).RunwayConfigurationId);
        Assert.True(Sfo(engine).RunwayConfigurationChosen);
        Assert.False(engine.Scenario.AsdexSafetyLogicConfigs["OAK"].RunwayConfigurationChosen);
    }

    [Fact]
    public void ASnapshotWithoutTheChosenFlag_LoadsAsNotChosen()
    {
        const string json = """{"Runways":[],"RunwayConfigurationId":"01GVM1TJ6YF6HE25H6E4RD9DA2","InhibitedArrivalAlertPositionIds":[]}""";

        var config = AsdexSafetyLogicConfig.FromSnapshot(JsonSerializer.Deserialize<AsdexSafetyLogicConfigDto>(json)!, 0);

        Assert.Equal(L19D10, config.RunwayConfigurationId);
        Assert.False(config.RunwayConfigurationChosen);
    }

    [Fact]
    public void ARecordingWithAnArwyChange_ReplaysToTheSameConfiguration()
    {
        SimulationEngine engine = Engine();

        for (int i = 0; i < 3; i++)
        {
            engine.TickOneSecond();
        }

        Arwy(engine, "ARWY SFO 10L 10R");
        Assert.Equal(L10D10, Sfo(engine).RunwayConfigurationId);

        SimScenarioState scenario = engine.Scenario!;
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
        replayed.Replay(recording, scenario.ElapsedSeconds + 2);

        Assert.Equal(L10D10, Sfo(replayed).RunwayConfigurationId);
    }
}
