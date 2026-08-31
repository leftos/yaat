using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Chain (`;`) coverage for handlers that previously had none: military-route (CMTR) and approach
/// (CAPP) commands as one arm of a compound. Pins the queue shape (follow-on blocks queue behind
/// the installed phase rather than being dropped or misapplied) and the abort-remainder contract
/// when the handler arm fails at fire time.
/// </summary>
public class HandlerChainTests
{
    private readonly ITestOutputHelper _output;
    private readonly List<string> _warnings = [];

    public HandlerChainTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? BuildEngine()
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
        engine.World.GroundLayout = layout;
        engine.World.ReactionDelayRng = new SerializableRandom(42);
        engine.WarningEmitted += (_, warning) => _warnings.Add(warning);
        return engine;
    }

    [Fact]
    public void CmtrChain_QueuesFollowOnBehindRoutePhase()
    {
        if (NavigationDatabase.Instance.GetMilitaryRoute("IR149") is not { } route)
        {
            _output.WriteLine("Skipped: IR149 not available");
            return;
        }

        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var entry = route.Points[0].Position;
        var next = route.Points[1].Position;
        var ac = new AircraftState
        {
            Callsign = "TREND21",
            AircraftType = "F16",
            Position = new LatLon(entry.Lat - 0.2, entry.Lon),
            TrueHeading = new TrueHeading(GeoMath.BearingTo(entry, next)),
            Altitude = 8000,
            IndicatedAirspeed = 300,
        };
        engine.World.AddAircraft(ac);

        var result = engine.SendCommand(ac.Callsign, "CMTR IR149; SQVFR");
        Assert.True(result.Success, result.Message);

        // The route is installed; the follow-on block queues behind the route phase (untriggered
        // blocks do not advance mid-phase) — it must be queued, not dropped or misapplied.
        Assert.Equal("IR149", ac.MilitaryRoute.Designator);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied && (b.Description ?? "").Contains("SQVFR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CmtrChain_UnknownRouteMidChain_AbortsRemainder()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = new AircraftState
        {
            Callsign = "TREND22",
            AircraftType = "F16",
            Position = new LatLon(37.65, -122.30),
            TrueHeading = new TrueHeading(40),
            Altitude = 8000,
            IndicatedAirspeed = 300,
            IsOnGround = false,
            Transponder = new AircraftTransponder
            {
                AssignedCode = 4611,
                Code = 7654,
                Mode = "C",
            },
        };
        engine.World.AddAircraft(ac);

        // CM 8000 completes immediately (already level); the unknown-route CMTR then fails at fire
        // time and must discard the trailing SQ.
        var result = engine.SendCommand(ac.Callsign, "CM 8000; CMTR IR999999; SQ");
        Assert.True(result.Success, result.Message);

        for (int t = 0; t < 10 && _warnings.Count == 0; t++)
        {
            engine.TickOneSecond();
        }

        _output.WriteLine($"warnings=[{string.Join(" | ", _warnings)}]");
        Assert.True(_warnings.Count > 0, "CMTR IR999999 never fired/failed");
        Assert.Equal(7654u, ac.Transponder.Code);
        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied && (b.Description ?? "").Contains("SQ", StringComparison.Ordinal));
    }

    [Fact]
    public void CappChain_TriggeredFollowOn_QueuesWithTrigger()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            Position = new LatLon(37.75, -122.35),
            TrueHeading = new TrueHeading(280),
            Altitude = 3000,
            IndicatedAirspeed = 210,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };
        engine.World.AddAircraft(ac);

        // The documented "speed until final" idiom: the clearance installs the approach phase and
        // the ATFN block queues with a DistanceFinal trigger so it can fire mid-phase (regime B).
        var result = engine.SendCommand(ac.Callsign, "CAPP I28R; ATFN 10 RNS");
        if (!result.Success)
        {
            _output.WriteLine($"Skipped: CAPP I28R unavailable — {result.Message}");
            return;
        }

        Assert.NotNull(ac.Phases?.CurrentPhase);
        var atfnBlock = ac.Queue.Blocks.Find(b => !b.IsApplied && b.Trigger is not null);
        Assert.NotNull(atfnBlock);
    }
}
