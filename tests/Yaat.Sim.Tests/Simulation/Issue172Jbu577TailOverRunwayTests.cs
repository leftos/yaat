using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #172 W2/W3 (tail-over-runway state + warnings). JBU577 is cleared <c>TAXI G B HS B</c> across
/// SFO RWY 01L/19R, where taxiway B sits closer than a fuselage length past the runway. The aircraft
/// cannot be both clear of the runway and short of B, so it holds at B's line with its tail over the
/// runway bars. This must: tag the B hold-short with the overhung runway hold-short node (W2 state),
/// register that runway node as occupied while it holds (W2 occupancy), warn the controller on the TAXI
/// echo (W3 issuance) and with a runtime terminal note (W3). Recording: issue172-sfo-taxiing (ZOA/SFO).
/// </summary>
public class Issue172Jbu577TailOverRunwayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue172-sfo-taxiing-recording.yaat-bug-report-bundle.zip";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void TaxiGBHsB_WarnsAtIssuanceAndTagsTailOverRunway()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 443);
        var jbu = engine.FindAircraft("JBU577");
        Assert.NotNull(jbu);

        var result = engine.SendCommand("JBU577", "TAXI G B HS B");
        output.WriteLine($"echo: {result.Message}");
        Assert.True(result.Success, result.Message);

        // W3 issuance: the TAXI echo warns the controller the aircraft can't clear the runway.
        Assert.Contains("unable to clear the runway", result.Message, StringComparison.OrdinalIgnoreCase);

        // W2 state: the B taxiway hold-short is tagged with the runway hold-short node it overhangs.
        var route = jbu.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        var bHold = route.HoldShortPoints.Single(h => h.Reason == HoldShortReason.ExplicitHoldShort && h.TargetName == "B");
        Assert.NotNull(bHold.TailOverRunwayNodeId);

        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);
        Assert.True(layout.Nodes.TryGetValue(bHold.TailOverRunwayNodeId!.Value, out var rwyNode));
        Assert.Equal(GroundNodeType.RunwayHoldShort, rwyNode.Type);

        // Negative: the runway-crossing hold-short itself is never tagged tail-over (only the taxiway HS).
        Assert.DoesNotContain(route.HoldShortPoints, h => h.Reason == HoldShortReason.RunwayCrossing && h.TailOverRunwayNodeId is not null);
    }

    [Fact]
    public void HoldingShortOfB_OccupiesRunwayAndEmitsUnableToClearNote()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var notes = new List<string>();
        engine.WarningEmitted += (callsign, warning) =>
        {
            if (callsign == "JBU577")
            {
                notes.Add(warning);
            }
        };

        engine.Replay(recording, 0);

        // Recorded commands replay only up to t=513 (TAXI B M1 Y @B5 at t=514 would extend the
        // route past B). Beyond the window, tick physics only (bounded) so the crossing completes
        // and the hold-short-of-B state can be observed — exit/taxi geometry changes legitimately
        // shift the crossing a few seconds past the recording's cutoff.
        const int WindowEnd = 513;
        const int PhysicsOnlyEnd = WindowEnd + 60;

        int? tailOverNode = null;
        bool occupiedWhileHolding = false;
        for (int t = 1; t <= PhysicsOnlyEnd; t++)
        {
            if (t <= WindowEnd)
            {
                engine.ReplayOneSecond();
            }
            else
            {
                engine.TickOneSecond();
            }

            var ac = engine.FindAircraft("JBU577");
            if (ac is null || t < 444)
            {
                continue;
            }

            bool holdingShort = ac.Phases?.CurrentPhase?.GetType().Name == "HoldingShortPhase";
            var bHold = ac.Ground.AssignedTaxiRoute?.HoldShortPoints.FirstOrDefault(h => h.TailOverRunwayNodeId is not null);
            if (holdingShort && bHold?.TailOverRunwayNodeId is { } node)
            {
                tailOverNode = node;
                if (engine.ComputeOccupiedHoldShortNodes().Contains(node))
                {
                    occupiedWhileHolding = true;
                }
            }

            if (occupiedWhileHolding && notes.Any(n => n.Contains("not clear of RWY", StringComparison.OrdinalIgnoreCase)))
            {
                output.WriteLine($"t={t}: tail-over state + occupancy + note all observed — ending physics-only extension");
                break;
            }
        }

        Assert.NotNull(tailOverNode);
        Assert.True(occupiedWhileHolding, "the runway hold-short node JBU577's tail overhangs should be marked occupied while it holds short of B");
        Assert.Contains(notes, n => n.Contains("not clear of RWY", StringComparison.OrdinalIgnoreCase));
    }
}
