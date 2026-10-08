using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// <c>ARWY</c> through the engine's action router with real navigation data: it replaces one airport's active-runway
/// list whole, answers the room with the list it left, and refuses — changing nothing — an end, an airport or a form
/// the navigation data or the grammar does not allow.
/// </summary>
public class ActiveRunwaysCommandTests
{
    public ActiveRunwaysCommandTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static SimulationEngine BuildEngine(string? primaryAirportId)
    {
        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = primaryAirportId,
            },
        };
        return engine;
    }

    private static CommandResult Arwy(SimulationEngine engine, string command) =>
        engine.Actions.Issue(new ActionInput("", command, "conn-1", "XX", Baked: null)).Result;

    private static string[] Tokens(SimulationEngine engine, string airport) =>
        [.. engine.Scenario!.ActiveRunways.For(airport).Select(runway => runway.ToToken())];

    [Fact]
    public void Arwy_SetsTheAirportsList()
    {
        SimulationEngine engine = BuildEngine("OAK");

        CommandResult result = Arwy(engine, "ARWY OAK 28L 28R");

        Assert.True(result.Success, result.Message);
        Assert.Equal(["28L", "28R"], Tokens(engine, "OAK"));
        Assert.All(engine.Scenario!.ActiveRunways.For("OAK"), runway => Assert.Equal(ActiveRunwayUse.Both, runway.Use));
        Assert.Equal(["OAK"], engine.Scenario.ActiveRunways.Airports);
    }

    [Fact]
    public void Arwy_OmittedAirport_UsesThePrimary()
    {
        SimulationEngine engine = BuildEngine("OAK");

        CommandResult result = Arwy(engine, "ARWY D28L A28R");

        Assert.True(result.Success, result.Message);
        Assert.Equal(["D28L", "A28R"], Tokens(engine, "OAK"));
        Assert.Equal("Active runways at OAK: D28L A28R", result.Message);
    }

    [Fact]
    public void Arwy_NoPrimaryAirport_Refused()
    {
        SimulationEngine engine = BuildEngine(null);

        CommandResult result = Arwy(engine, "ARWY 28L");

        Assert.False(result.Success);
        Assert.Equal("No primary airport; name one: ARWY {airport} {runways}", result.Message);
        Assert.Equal(ActiveRunways.Empty, engine.Scenario!.ActiveRunways);
    }

    [Fact]
    public void Arwy_UnknownEnd_RefusedAndUnchanged()
    {
        SimulationEngine engine = BuildEngine("OAK");
        Assert.True(Arwy(engine, "ARWY OAK 30").Success);
        ActiveRunways before = engine.Scenario!.ActiveRunways;

        // OAK's pavements are 12/30, 15/33 and the 10/28 pair: 01 is a runway's form, but no end at OAK.
        CommandResult unknown = Arwy(engine, "ARWY OAK 28L 01");

        Assert.False(unknown.Success);
        Assert.Equal("Unknown runway 01 at OAK", unknown.Message);
        Assert.Same(before, engine.Scenario.ActiveRunways);

        // 99 is past 36, so the list parser refuses it as no runway at all.
        CommandResult outOfRange = Arwy(engine, "ARWY OAK 99");

        Assert.False(outOfRange.Success);
        Assert.Equal("Not a runway: 99", outOfRange.Message);
        Assert.Same(before, engine.Scenario.ActiveRunways);
    }

    [Fact]
    public void Arwy_UnknownAirport_RefusedAndUnchanged()
    {
        SimulationEngine engine = BuildEngine("OAK");
        Assert.True(Arwy(engine, "ARWY OAK 30").Success);
        ActiveRunways before = engine.Scenario!.ActiveRunways;

        CommandResult result = Arwy(engine, "ARWY XYZQ 28L");

        Assert.False(result.Success);
        Assert.Equal("Unknown airport XYZQ", result.Message);
        Assert.Same(before, engine.Scenario.ActiveRunways);
    }

    [Fact]
    public void Arwy_None_ClearsTheList()
    {
        SimulationEngine engine = BuildEngine("OAK");
        Assert.True(Arwy(engine, "ARWY OAK 28L 28R").Success);
        Assert.True(Arwy(engine, "ARWY SFO 28R").Success);

        CommandResult named = Arwy(engine, "ARWY SFO NONE");

        Assert.True(named.Success, named.Message);
        Assert.Equal("No active runways at SFO", named.Message);
        Assert.Equal([], Tokens(engine, "SFO"));

        CommandResult primary = Arwy(engine, "ARWY NONE");

        Assert.True(primary.Success, primary.Message);
        Assert.Equal("No active runways at OAK", primary.Message);
        Assert.Equal(ActiveRunways.Empty, engine.Scenario!.ActiveRunways);
    }

    [Fact]
    public void Arwy_NoneWithOtherRunways_Refused()
    {
        SimulationEngine engine = BuildEngine("OAK");
        Assert.True(Arwy(engine, "ARWY OAK 30").Success);
        ActiveRunways before = engine.Scenario!.ActiveRunways;

        CommandResult result = Arwy(engine, "ARWY OAK NONE 28L");

        Assert.False(result.Success);
        Assert.Equal("NONE must be the only runway", result.Message);
        Assert.Same(before, engine.Scenario.ActiveRunways);
    }

    [Fact]
    public void Arwy_Show_AnswersTheListAndChangesNothing()
    {
        SimulationEngine engine = BuildEngine("OAK");
        Assert.True(Arwy(engine, "ARWY OAK D28L A28R 30").Success);
        ActiveRunways before = engine.Scenario!.ActiveRunways;

        CommandResult shown = Arwy(engine, "ARWY OAK");

        Assert.True(shown.Success, shown.Message);
        Assert.Equal("Active runways at OAK: D28L A28R 30", shown.Message);
        Assert.Same(before, engine.Scenario.ActiveRunways);

        CommandResult empty = Arwy(engine, "ARWY SFO");

        Assert.True(empty.Success, empty.Message);
        Assert.Equal("No active runways at SFO", empty.Message);
        Assert.Same(before, engine.Scenario.ActiveRunways);
    }

    [Fact]
    public void Arwy_Reply_ListsTokensInOrder_UpperCased_WithZeroPaddedDesignators()
    {
        SimulationEngine engine = BuildEngine("OAK");

        CommandResult result = Arwy(engine, "ARWY KSFO d1r,a28r, 28l");

        Assert.True(result.Success, result.Message);
        Assert.Equal("Active runways at SFO: D01R A28R 28L", result.Message);
        Assert.Equal(["D01R", "A28R", "28L"], Tokens(engine, "SFO"));
    }

    [Fact]
    public void Arwy_NewlineSeparatedList_SetsBoth()
    {
        SimulationEngine engine = BuildEngine("OAK");

        CommandResult result = Arwy(engine, "ARWY OAK 28L\n28R");

        Assert.True(result.Success, result.Message);
        Assert.Equal(["28L", "28R"], Tokens(engine, "OAK"));
    }

    [Theory]
    [InlineData("ARWY", "ARWY requires an airport or runways")]
    [InlineData("ARWY +33", "Not an airport: +33")]
    [InlineData("ARWY OAK +33", "Not a runway: +33")]
    [InlineData("ARWY OAK -28L", "Not a runway: -28L")]
    public void Arwy_Malformed_AnsweredByTheGlobalArm_AndUnchanged(string command, string expected)
    {
        SimulationEngine engine = BuildEngine("OAK");
        Assert.True(Arwy(engine, "ARWY OAK 30").Success);
        ActiveRunways before = engine.Scenario!.ActiveRunways;

        ActionOutcome outcome = engine.Actions.Issue(new ActionInput("", command, "conn-1", "XX", Baked: null));

        Assert.Equal(new ActionTrace(RecordedCommandKind.ActiveRunways, ActionScope.Global), outcome.Trace);
        Assert.False(outcome.Result.Success);
        Assert.Equal(expected, outcome.Result.Message);
        Assert.DoesNotContain("not found", outcome.Result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(before, engine.Scenario.ActiveRunways);
    }

    [Fact]
    public void Arwy_Set_CallsOnActiveRunwaysChanged_ShowDoesNot()
    {
        SimulationEngine engine = BuildEngine("OAK");
        var host = new AttendanceActionHost();
        CommandResult Issue(string command) => engine.Actions.Issue(new ActionInput("", command, "conn-1", "XX", Baked: null), host).Result;

        Assert.True(Issue("ARWY OAK 28L 28R").Success);
        Assert.Equal(1, host.ActiveRunwaysChanges);

        Assert.True(Issue("ARWY OAK").Success);
        Assert.Equal(1, host.ActiveRunwaysChanges);

        Assert.False(Issue("ARWY OAK 99").Success);
        Assert.Equal(1, host.ActiveRunwaysChanges);

        Assert.True(Issue("ARWY OAK NONE").Success);
        Assert.Equal(2, host.ActiveRunwaysChanges);
    }

    [Fact]
    public void Arwy_ReplacesNotAppends()
    {
        SimulationEngine engine = BuildEngine("OAK");
        Assert.True(Arwy(engine, "ARWY OAK 28L 28R").Success);

        CommandResult result = Arwy(engine, "ARWY OAK 30");

        Assert.True(result.Success, result.Message);
        Assert.Equal("Active runways at OAK: 30", result.Message);
        Assert.Equal(["30"], Tokens(engine, "OAK"));
    }

    [Fact]
    public void Arwy_OtherAirportsUntouched()
    {
        SimulationEngine engine = BuildEngine("OAK");
        Assert.True(Arwy(engine, "ARWY OAK 28L 28R").Success);

        Assert.True(Arwy(engine, "ARWY SFO 28R").Success);
        Assert.Equal(["28L", "28R"], Tokens(engine, "OAK"));
        Assert.Equal(["28R"], Tokens(engine, "SFO"));

        Assert.True(Arwy(engine, "ARWY SFO NONE").Success);
        Assert.Equal(["28L", "28R"], Tokens(engine, "OAK"));
        Assert.Equal(["OAK"], engine.Scenario!.ActiveRunways.Airports);
    }
}
