using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// When a runway is named as a segment of a TAXI clearance, the aircraft taxis ALONG it and the
/// readback says "runway two eight right" (spoken) / "runway 28R" (terminal/echo) — never the taxiway
/// spelling "two eight romeo" nor the internal centerline edge name "RWY28R/10L". Regression for the
/// reported OAK bug "TAXI 28R G D @NEW1" → "Cannot find taxiway 28R in layout."
/// </summary>
public class TaxiAlongRunwayReadbackTests(ITestOutputHelper output)
{
    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Information).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    private static AircraftState SpawnOnTaxiwayB(AirportGroundLayout layout)
    {
        // A node on taxiway B just south of the 28R hold-short — a plausible spot to be cleared
        // "taxi onto and along 28R, then G, D".
        var start = layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesTaxiway("B")))
            .OrderBy(n => GeoMath.DistanceNm(37.723668, -122.205446, n.Position.Lat, n.Position.Lon))
            .First();

        var aircraft = new AircraftState
        {
            Callsign = "N436MS",
            AircraftType = "C172",
            Position = start.Position,
            TrueHeading = start.TrueHeading ?? new TrueHeading(0),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(3000),
            },
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    private void AddScenario(SimulationEngine engine, string id)
    {
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = id,
            ScenarioName = id,
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
        };
    }

    [Fact]
    public void Rsp_TaxiAlongRunway_EchoesRunwayDesignator_NotInternalName()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        engine.World.AddAircraft(SpawnOnTaxiwayB(layout));
        AddScenario(engine, "taxi-along-28R");

        var result = engine.SendCommand("N436MS", "TAXI 28R G D");
        output.WriteLine($"RSP: {result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.Contains("on 28R", result.Message); // "on (runway)" connector, single cleared end
        Assert.DoesNotContain("RWY28R", result.Message); // internal centerline name must not leak
        Assert.DoesNotContain("28R/10L", result.Message); // reciprocal pair must not leak into the echo
    }

    [Fact]
    public void FullReportedCommand_TaxiAlongRunwayToParking_Resolves()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        engine.World.AddAircraft(SpawnOnTaxiwayB(layout));
        AddScenario(engine, "taxi-along-28R-parking");

        // The exact reported command.
        var result = engine.SendCommand("N436MS", "TAXI 28R G D @NEW1");
        output.WriteLine($"RSP: {result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("Cannot find taxiway 28R", result.Message);
    }

    [Fact]
    public void Verbalize_TaxiAlongRunwayFirst_UsesOnRunway_NotViaOn()
    {
        TestVnasData.EnsureInitialized();
        var taxi = new TaxiCommand(["28R", "G", "D"], []);

        var spoken = PhraseologyVerbalizer.Verbalize(taxi);
        var terminal = PhraseologyVerbalizer.VerbalizeTerminal(taxi);
        output.WriteLine($"spoken:   {spoken}");
        output.WriteLine($"terminal: {terminal}");

        // 7110.65 §3-7-2.a: a runway is entered with "on (runway)", spoken with the word "runway"
        // and phonetic digits — never the taxiway spelling "two eight romeo", never "taxi via on".
        Assert.Contains("taxi on runway two eight right", spoken);
        Assert.Contains("golf", spoken);
        Assert.DoesNotContain("romeo", spoken);
        Assert.DoesNotContain("via on", spoken);

        Assert.Contains("taxi on 28R", terminal);
        Assert.Contains("G D", terminal);
    }

    [Fact]
    public void Verbalize_TaxiwayThenRunway_UsesViaForTaxiwayOnForRunway()
    {
        TestVnasData.EnsureInitialized();
        var taxi = new TaxiCommand(["B", "28R", "G"], []);

        var spoken = PhraseologyVerbalizer.Verbalize(taxi);
        output.WriteLine($"spoken: {spoken}");

        // "via" introduces the taxiway route; "on" introduces the runway segment.
        Assert.Contains("taxi via bravo", spoken);
        Assert.Contains("on runway two eight right", spoken);
        Assert.Contains("golf", spoken);
    }
}
