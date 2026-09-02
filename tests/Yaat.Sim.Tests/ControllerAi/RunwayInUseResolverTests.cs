using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.ControllerAi.Knowledge;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>
/// The generic runway-in-use rule over OAK's real runways: 28R/28L (279.5°/279.4° magnetic, 28L the longer), 12/30
/// (117°/297°, the longest pavement), 15/33 (152°/332°, the shortest).
/// </summary>
public class RunwayInUseResolverTests
{
    private static readonly DateTime ModelDate = MagneticDeclination.EvaluationDateUtc;

    public RunwayInUseResolverTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static IReadOnlyList<RunwayInfo> Oak => RunwayOccupancy.AirportRunways("OAK");

    private static WeatherProfile Wind(double directionMagnetic, double speedKt) =>
        new() { WindLayers = [new WindLayer { Direction = directionMagnetic, Speed = speedKt }] };

    [Theory]
    [InlineData(300, 12, "30")]
    [InlineData(280, 12, "28R")]
    [InlineData(100, 8, "10L")]
    [InlineData(320, 12, "33")]
    public void WindOfFiveKnotsOrMore_PicksTheEndMostNearlyAlignedWithTheMagneticWind(double direction, double speed, string expected)
    {
        var decision = RunwayInUseResolver.Resolve("OAK", null, Wind(direction, speed), Oak, ModelDate);

        Assert.NotNull(decision);
        Assert.Equal(expected, decision.PrimaryDepartureRunway);
        Assert.Equal([expected], decision.ArrivalRunways);
        Assert.Equal(RunwayUseSource.Generic, decision.Source);
        Assert.Contains("most nearly aligned", decision.Rationale);
    }

    [Fact]
    public void CalmWithNoWeather_PicksTheLongestRunway_EndByDesignator()
    {
        var decision = RunwayInUseResolver.Resolve("OAK", null, null, Oak, ModelDate);

        Assert.NotNull(decision);
        Assert.Equal("12", decision.PrimaryDepartureRunway);
        Assert.Contains("calm", decision.Rationale);
    }

    [Fact]
    public void LightWind_PicksTheLongestRunway_EndTowardTheWind()
    {
        var decision = RunwayInUseResolver.Resolve("OAK", null, Wind(300, 3), Oak, ModelDate);

        Assert.NotNull(decision);
        Assert.Equal("30", decision.PrimaryDepartureRunway);
        Assert.Contains("calm", decision.Rationale);
    }

    [Fact]
    public void VariableWind_IsCalm()
    {
        var weather = new WeatherProfile
        {
            WindLayers =
            [
                new WindLayer
                {
                    Direction = 300,
                    Speed = 3,
                    Variable = true,
                },
            ],
        };

        var decision = RunwayInUseResolver.Resolve("OAK", null, weather, Oak, ModelDate);

        Assert.NotNull(decision);
        Assert.Equal("12", decision.PrimaryDepartureRunway);
    }

    [Fact]
    public void SessionOverride_WinsOverTheWind_WhenItNamesARunwayEnd()
    {
        var decision = RunwayInUseResolver.Resolve("OAK", "30", Wind(100, 8), Oak, ModelDate);

        Assert.NotNull(decision);
        Assert.Equal("30", decision.PrimaryDepartureRunway);
        Assert.Equal(RunwayUseSource.Override, decision.Source);
    }

    [Fact]
    public void SessionOverride_NamingNoRunway_IsIgnored()
    {
        var decision = RunwayInUseResolver.Resolve("OAK", "99", Wind(100, 8), Oak, ModelDate);

        Assert.NotNull(decision);
        Assert.Equal("10L", decision.PrimaryDepartureRunway);
        Assert.Equal(RunwayUseSource.Generic, decision.Source);
    }

    [Fact]
    public void NoRunways_ResolvesToNothing()
    {
        Assert.Null(RunwayInUseResolver.Resolve("ZZZZ", "30", Wind(100, 8), [], ModelDate));
    }

    [Fact]
    public void State_MemoizesPerAirport_AppliesTheOverrideOnlyToThePrimaryAirport_AndRefreshesOnAWeatherChange()
    {
        var zoa = TestArtccConfig.LoadZoa();
        if (zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, zoa, 7, [ground]);
        var scenario = engine.Scenario!;
        scenario.ControllerAi = new ControllerAiConfig
        {
            Seed = 7,
            EnabledPositionIds = [ground.PositionId],
            RoleOverrides = AiTestHost.NoOverrides,
            RunwayInUse = "30",
            RunwayConfigurations = AiTestHost.NoRunwayConfigurations,
        };
        engine.World.Weather = Wind(100, 8);
        var aircraft = engine.FindAircraft(AiTestHost.Callsign)!;
        var state = new RunwayInUseState(FacilityOpsDatabase.For);
        var context = AiTestHost.Context(engine, [aircraft], [ground], 10, [], new RecordingAiCommandSink());

        var oak = state.For("OAK", context, ground.PositionId);
        Assert.NotNull(oak);
        Assert.Equal("30", oak.PrimaryDepartureRunway);
        Assert.Equal(RunwayUseSource.Override, oak.Source);
        Assert.Same(oak, state.For("KOAK", context, ground.PositionId));

        // The override is the primary airport's; another airport resolves from the wind.
        var sfo = state.For("SFO", context, ground.PositionId);
        Assert.NotNull(sfo);
        Assert.Equal(RunwayUseSource.Generic, sfo.Source);
        Assert.StartsWith("10", sfo.PrimaryDepartureRunway);

        // A new weather profile re-resolves; the same profile keeps the decision even when the config changed.
        scenario.ControllerAi = new ControllerAiConfig
        {
            Seed = 7,
            EnabledPositionIds = [ground.PositionId],
            RoleOverrides = AiTestHost.NoOverrides,
            RunwayInUse = null,
            RunwayConfigurations = AiTestHost.NoRunwayConfigurations,
        };
        Assert.Same(oak, state.For("OAK", AiTestHost.Context(engine, [aircraft], [ground], 11, [], new RecordingAiCommandSink()), ground.PositionId));
        engine.World.Weather = Wind(300, 12);
        var windy = state.For("OAK", AiTestHost.Context(engine, [aircraft], [ground], 12, [], new RecordingAiCommandSink()), ground.PositionId);
        Assert.NotNull(windy);
        // OAK has a knowledge file: 12 kt from 300 is the SOP's west configuration, not the generic single runway.
        Assert.Equal(RunwayUseSource.Knowledge, windy.Source);
        Assert.Equal("SFOW", windy.ConfigurationName);
        Assert.Equal(["28L", "28R", "30"], windy.DepartureRunways);

        state.Clear();
        Assert.NotSame(
            windy,
            state.For("OAK", AiTestHost.Context(engine, [aircraft], [ground], 13, [], new RecordingAiCommandSink()), ground.PositionId)
        );
    }
}
