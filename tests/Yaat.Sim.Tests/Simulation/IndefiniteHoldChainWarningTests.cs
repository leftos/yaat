using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Untriggered blocks chained behind a command that installs a never-self-completing phase
/// (HP holds, HPP*/HFIX* VFR holds, FOLLOW) stall until the hold/follow is cancelled — the
/// dispatch must say so up front. The chain still queues ("after the hold, do X" is preserved);
/// this is feedback, not a rejection. Triggered tails still fire mid-phase and get no warning.
/// </summary>
public class IndefiniteHoldChainWarningTests
{
    private readonly ITestOutputHelper _output;

    public IndefiniteHoldChainWarningTests(ITestOutputHelper output)
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
        return engine;
    }

    private static AircraftState AddAirborne(SimulationEngine engine, string callsign, double lat, double lon)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(40),
            Altitude = 5000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "MEM" },
        };
        engine.World.AddAircraft(ac);
        return ac;
    }

    [Fact]
    public void HpChain_UntriggeredTail_WarnsAtDispatch_AndStillQueues()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = AddAirborne(engine, "IHW101", 37.65, -122.30);
        var result = engine.SendCommand(ac.Callsign, "HOLDP OAK 180 1M R; FH 090");
        Assert.True(result.Success, result.Message);
        _output.WriteLine($"warnings=[{string.Join(" | ", ac.PendingWarnings)}]");

        Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("will not execute", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied && (b.Description ?? "").Contains("FH", StringComparison.Ordinal));
    }

    [Fact]
    public void HpChain_TriggeredTail_NoWarning()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = AddAirborne(engine, "IHW102", 37.65, -122.30);
        var result = engine.SendCommand(ac.Callsign, "HOLDP OAK 180 1M R; LV 40 FH 090");
        Assert.True(result.Success, result.Message);
        _output.WriteLine($"warnings=[{string.Join(" | ", ac.PendingWarnings)}]");

        Assert.DoesNotContain(ac.PendingWarnings, w => w.Contains("will not execute", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FollowChain_UntriggeredTail_WarnsAtDispatch()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var lead = AddAirborne(engine, "IHW103", 37.66, -122.29);
        var ac = AddAirborne(engine, "IHW104", 37.64, -122.31);
        var result = engine.SendCommand(ac.Callsign, $"FOLLOWF {lead.Callsign}; CM 3000");
        _output.WriteLine($"dispatch: success={result.Success} msg={result.Message}");
        _output.WriteLine($"warnings=[{string.Join(" | ", ac.PendingWarnings)}]");
        Assert.True(result.Success, result.Message);

        Assert.Contains(ac.PendingWarnings, w => w.Contains("will not execute", StringComparison.OrdinalIgnoreCase));
    }
}
