using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// A <c>&gt;</c>/<c>&lt;</c> turn-direction hint on a TAXI taxiway (e.g. <c>TAXI &gt;J</c>) is echoed
/// as "right on J" / "left on J" in BOTH the controller response (RSP) and the pilot's spoken readback
/// (TTS). The TTS already renders it via the verbalizer; the RSP previously dropped it because it was
/// built from the resolved route summary, which carries no turn hint.
/// </summary>
public class TaxiTurnHintEchoTests(ITestOutputHelper output)
{
    private const int JApproachNode = 379; // OAK: a node on taxiway J approaching the 28R crossing

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

    private static AircraftState SpawnOnJ(AirportGroundLayout layout)
    {
        var start = layout.Nodes[JApproachNode];
        var aircraft = new AircraftState
        {
            Callsign = "N70CS",
            AircraftType = "C25C",
            Position = start.Position,
            TrueHeading = start.TrueHeading ?? new TrueHeading(0),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "IFR",
                CruiseAltitude = 3000,
            },
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    [Fact]
    public void Rsp_TaxiWithRightTurnHint_EchoesRightOnTaxiway()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);

        var aircraft = SpawnOnJ(layout);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "taxi-turn-hint-rsp",
            ScenarioName = "TAXI >J turn-hint echo",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
        };

        var result = engine.SendCommand("N70CS", "TAXI >J HS 28R");
        Assert.True(result.Success, result.Message);
        output.WriteLine($"RSP: {result.Message}");

        Assert.Contains("right on J", result.Message);
    }

    [Fact]
    public void Rsp_TaxiWithLeftTurnHint_EchoesLeftOnTaxiway()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);

        var aircraft = SpawnOnJ(layout);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "taxi-turn-hint-rsp-left",
            ScenarioName = "TAXI <J turn-hint echo",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
        };

        var result = engine.SendCommand("N70CS", "TAXI <J HS 28R");
        Assert.True(result.Success, result.Message);
        output.WriteLine($"RSP: {result.Message}");

        Assert.Contains("left on J", result.Message);
    }

    [Fact]
    public void Rsp_TaxiWithoutTurnHint_NoTurnWording()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);

        var aircraft = SpawnOnJ(layout);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "taxi-no-turn-hint",
            ScenarioName = "TAXI J no hint",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
        };

        var result = engine.SendCommand("N70CS", "TAXI J HS 28R");
        Assert.True(result.Success, result.Message);

        Assert.DoesNotContain(" on J", result.Message);
        Assert.Contains("Taxi via J", result.Message);
    }

    [Fact]
    public void Tts_TaxiWithRightTurnHint_SpeaksRightOnTaxiway()
    {
        TestVnasData.EnsureInitialized();

        // Full dispatch-parse round trip: the canonical "TAXI >J HS 28R" must parse the ">J" glyph into
        // PathTurnHints so the verbalizer-driven readback voices the turn.
        var parsed = CommandParser.ParseCompound("TAXI >J HS 28R", "");
        Assert.True(parsed.IsSuccess, parsed.Reason);

        var ac = new AircraftState { Callsign = "N70CS", AircraftType = "C25C" };
        var tts = PilotResponder.BuildReadback(parsed.Value!, ac)?.Tts;
        output.WriteLine($"TTS: {tts}");

        Assert.NotNull(tts);
        Assert.Contains("right on juliet", tts);
    }
}
