using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Two independently dispatched leading-WAIT commands must both fire: the first deferral's
/// dispatch (which runs through DispatchCompound with PreserveConditionals) must not cancel the
/// second, still-pending deferral. Pins the clears-on-supersede invariant of
/// SimulationEngine.ProcessDeferredDispatches (docs/command-pipeline.md).
/// </summary>
public class StackedWaitDispatchTests
{
    private readonly ITestOutputHelper _output;

    public StackedWaitDispatchTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void TwoStackedWaits_BothFire_InOrder()
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
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

        var ac = new AircraftState
        {
            Callsign = "SWD101",
            AircraftType = "B738",
            Position = new LatLon(37.65, -122.30),
            TrueHeading = new TrueHeading(40),
            Altitude = 5000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "MEM" },
        };
        engine.World.AddAircraft(ac);

        var r1 = engine.SendCommand(ac.Callsign, "WAIT 5 FH 090");
        Assert.True(r1.Success, r1.Message);
        var r2 = engine.SendCommand(ac.Callsign, "WAIT 12 CM 8000");
        Assert.True(r2.Success, r2.Message);
        Assert.Equal(2, ac.DeferredDispatches.Count);

        // After the first wait elapses (plus slack): heading fired, climb still pending.
        for (int t = 0; t < 8; t++)
        {
            engine.TickOneSecond();
        }

        var tgtHdg = ac.Targets.TargetTrueHeading?.Degrees.ToString("F0") ?? "null";
        var tgtAlt = ac.Targets.TargetAltitude?.ToString() ?? "null";
        _output.WriteLine($"t=8: tgtHdg={tgtHdg} tgtAlt={tgtAlt} deferred={ac.DeferredDispatches.Count}");
        Assert.NotNull(ac.Targets.TargetTrueHeading);
        Assert.Null(ac.Targets.TargetAltitude);
        Assert.Single(ac.DeferredDispatches);

        // After the second wait elapses: the climb fired too.
        for (int t = 0; t < 8; t++)
        {
            engine.TickOneSecond();
        }

        _output.WriteLine($"t=16: tgtAlt={ac.Targets.TargetAltitude?.ToString() ?? "null"} deferred={ac.DeferredDispatches.Count}");
        Assert.Equal(8000, ac.Targets.TargetAltitude);
        Assert.Empty(ac.DeferredDispatches);
    }
}
