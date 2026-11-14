using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// A command-argument fix that only a CIFP terminal waypoint names (CFBMS at KBCT, absent from NavData) resolves for an
/// aircraft whose destination, then departure, is that airport, and is refused everywhere else with today's message.
/// NavData stays first: every name it holds resolves exactly where it does today.
/// </summary>
public class TerminalWaypointFixTests : IDisposable
{
    // CFBMS, KBCT terminal waypoint: N26 21 57.92 W080 11 56.18.
    private const double CfbmsLat = 26.3661;
    private const double CfbmsLon = -80.1989;

    private static readonly FixResolutionContext KbctArrival = new("KBCT", "KBWI");

    private readonly IDisposable _navDbScope;

    public TerminalWaypointFixTests()
    {
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(NavigationDatabase.Instance!);
    }

    public void Dispose() => _navDbScope.Dispose();

    [Fact]
    public void Cfix_Cfbms_Parses_ForKbctArrival()
    {
        ParseResult<CompoundCommand> result = CommandParser.ParseCompound("CFIX CFBMS 1400", ctx: KbctArrival);

        Assert.True(result.IsSuccess, result.Reason);
        CrossFixCommand cfix = Assert.IsType<CrossFixCommand>(Assert.Single(Assert.Single(result.Value!.Blocks).Commands));
        Assert.Equal("CFBMS", cfix.FixName);
        Assert.Equal(CfbmsLat, cfix.FixLat, 0.01);
        Assert.Equal(CfbmsLon, cfix.FixLon, 0.01);
        Assert.Equal(1400, cfix.Altitude);
    }

    [Fact]
    public void At_Cfbms_Parses_ForKbctArrival()
    {
        ParseResult<CompoundCommand> result = CommandParser.ParseCompound("AT CFBMS DM 003", ctx: KbctArrival);

        Assert.True(result.IsSuccess, result.Reason);
        ParsedBlock block = Assert.Single(result.Value!.Blocks);
        AtFixCondition at = Assert.IsType<AtFixCondition>(block.Condition);
        Assert.Equal("CFBMS", at.FixName);
        Assert.Equal(CfbmsLat, at.Lat, 0.01);
        Assert.Equal(CfbmsLon, at.Lon, 0.01);
        Assert.IsType<DescendMaintainCommand>(Assert.Single(block.Commands));
    }

    [Fact]
    public void Dct_Cfbms_Parses_ForKbctArrival()
    {
        ParseResult<CompoundCommand> result = CommandParser.ParseCompound("DCT CFBMS OMITE", ctx: KbctArrival);

        Assert.True(result.IsSuccess, result.Reason);
        DirectToCommand dct = Assert.IsType<DirectToCommand>(Assert.Single(Assert.Single(result.Value!.Blocks).Commands));
        Assert.Equal(["CFBMS", "OMITE"], dct.Fixes.Select(f => f.Name));
        Assert.Equal(CfbmsLat, dct.Fixes[0].Lat, 0.01);
        Assert.Equal(CfbmsLon, dct.Fixes[0].Lon, 0.01);
    }

    [Fact]
    public void AtScaleCfixCfbms_Parses_ForKbctArrival()
    {
        ParseResult<CompoundCommand> result = CommandParser.ParseCompound("AT SCALE CFIX CFBMS 1200", ctx: KbctArrival);

        Assert.True(result.IsSuccess, result.Reason);
        ParsedBlock block = Assert.Single(result.Value!.Blocks);
        Assert.Equal("SCALE", Assert.IsType<AtFixCondition>(block.Condition).FixName);
        CrossFixCommand cfix = Assert.IsType<CrossFixCommand>(Assert.Single(block.Commands));
        Assert.Equal("CFBMS", cfix.FixName);
        Assert.Equal(1200, cfix.Altitude);
    }

    [Fact]
    public void Cfbms_Refused_WhenNeitherAirportIsKbct()
    {
        ParseResult<CompoundCommand> oakland = CommandParser.ParseCompound("CFIX CFBMS 1400", ctx: new FixResolutionContext("KOAK", "KSFO"));
        ParseResult<CompoundCommand> noContext = CommandParser.ParseCompound("CFIX CFBMS 1400");

        Assert.False(oakland.IsSuccess);
        Assert.Contains("fix 'CFBMS' not found", oakland.Reason);
        Assert.False(noContext.IsSuccess);
        Assert.Contains("fix 'CFBMS' not found", noContext.Reason);
    }

    [Fact]
    public void DepartureAirportFallback_ResolvesTerminalWaypoint()
    {
        ParseResult<CompoundCommand> result = CommandParser.ParseCompound("CFIX CFBMS 1400", ctx: new FixResolutionContext("KOAK", "KBCT"));

        Assert.True(result.IsSuccess, result.Reason);
        CrossFixCommand cfix = Assert.IsType<CrossFixCommand>(Assert.Single(Assert.Single(result.Value!.Blocks).Commands));
        Assert.Equal(CfbmsLat, cfix.FixLat, 0.01);
        Assert.Equal(CfbmsLon, cfix.FixLon, 0.01);
    }

    [Fact]
    public void Cfix_Cfbms_FliesToTheFix_ForKbctArrival()
    {
        AircraftState aircraft = MakeKbctArrival();
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "terminal-waypoint-cfix",
                ScenarioName = "CFIX at a KBCT terminal waypoint",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "BCT",
            },
        };
        engine.World.AddAircraft(aircraft);

        CommandResult result = engine.SendCommand(aircraft.Callsign, "CFIX CFBMS 1400");

        Assert.True(result.Success, result.Message);
        NavigationTarget cfbms = Assert.Single(aircraft.Targets.NavigationRoute, t => t.Name == "CFBMS");
        Assert.Equal(CfbmsLat, cfbms.Position.Lat, 0.01);
        Assert.Equal(CfbmsLon, cfbms.Position.Lon, 0.01);
        Assert.NotNull(cfbms.AltitudeRestriction);
        Assert.Equal(1400, cfbms.AltitudeRestriction.Altitude1Ft);
    }

    [Fact]
    public void DeferredCfbms_RestoresWithItsContext()
    {
        AircraftState aircraft = MakeKbctArrival();
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(
            "WAIT 120 AT CFBMS DM 003",
            aircraft.FlightPlan.Route,
            ctx: FixResolutionContext.From(aircraft.FlightPlan)
        );
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(new Random(1)));
        Assert.True(result.Success, result.Message);
        Assert.Single(aircraft.DeferredDispatches);

        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), null);

        DeferredDispatch deferral = Assert.Single(restored.DeferredDispatches);
        AtFixCondition at = Assert.IsType<AtFixCondition>(Assert.Single(deferral.Payload.Blocks).Condition);
        Assert.Equal("CFBMS", at.FixName);
        Assert.Equal(CfbmsLat, at.Lat, 0.01);
        Assert.Equal(CfbmsLon, at.Lon, 0.01);
    }

    [Fact]
    public void CfixCfbmsPreset_TargetsTheFix_ForKbctArrival()
    {
        const string scenarioJson = """
            { "id": "terminal-waypoint-preset", "name": "CFIX at a KBCT terminal waypoint as a preset", "aircraft": [{
                "id": "a1", "aircraftId": "RVJ100", "aircraftType": "C25B", "transponderMode": "C",
                "startingConditions": { "type": "FixOrFrd", "fix": "SCALE", "altitude": 4000, "speed": 200 },
                "flightplan": { "rules": "IFR", "departure": "KBWI", "destination": "KBCT",
                                "route": "", "cruiseAltitude": 15000, "cruiseSpeed": 400, "aircraftType": "C25B" },
                "presetCommands": [ { "id": "p1", "command": "CFIX CFBMS 1400", "timeOffset": 0 } ] }] }
            """;
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());

        engine.LoadScenario(scenarioJson, 1, MagneticDeclination.EvaluationDateUtc);

        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft("RVJ100"));
        NavigationTarget cfbms = Assert.Single(aircraft.Targets.NavigationRoute, t => t.Name == "CFBMS");
        Assert.Equal(CfbmsLat, cfbms.Position.Lat, 0.01);
        Assert.Equal(CfbmsLon, cfbms.Position.Lon, 0.01);
        Assert.NotNull(cfbms.AltitudeRestriction);
        Assert.Equal(1400, cfbms.AltitudeRestriction.Altitude1Ft);
    }

    private static AircraftState MakeKbctArrival() =>
        new()
        {
            Callsign = "RVJ100",
            AircraftType = "C25B",
            Position = new LatLon(26.70, -80.25),
            TrueHeading = new TrueHeading(180),
            Altitude = 4000,
            IndicatedAirspeed = 200,
            FlightPlan = new AircraftFlightPlan { Departure = "KBWI", Destination = "KBCT" },
        };

    [Fact]
    public void NavDataFirst_WinsOnCollision()
    {
        // Synthetic: the committed data has no NavData fix that disagrees with a CIFP terminal waypoint, so this
        // database holds a NavData CFBMS 240 nm from KBCT's terminal CFBMS and reads the real pinned CIFP.
        (double Lat, double Lon) navDataCfbms = (30.0, -84.0);
        var db = NavigationDatabase.ForTesting(
            fixes: new Dictionary<string, (double Lat, double Lon)> { ["CFBMS"] = navDataCfbms },
            cifpFilePath: TestVnasData.GetCifpPath()
        );
        Assert.True(db.TryGetTerminalWaypoint("KBCT", "CFBMS", out _));

        using IDisposable scope = NavigationDatabase.ScopedOverride(db);
        ParseResult<CompoundCommand> result = CommandParser.ParseCompound("CFIX CFBMS 1400", ctx: KbctArrival);

        Assert.True(result.IsSuccess, result.Reason);
        CrossFixCommand cfix = Assert.IsType<CrossFixCommand>(Assert.Single(Assert.Single(result.Value!.Blocks).Commands));
        Assert.Equal(navDataCfbms.Lat, cfix.FixLat);
        Assert.Equal(navDataCfbms.Lon, cfix.FixLon);
    }
}
