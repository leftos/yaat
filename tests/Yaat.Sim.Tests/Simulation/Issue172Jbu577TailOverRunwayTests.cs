using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Ground;
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

    /// <summary>Ground speed (kt) above which JBU577 is moving in its crossing.</summary>
    private const double MovingKts = 1.0;

    /// <summary>Ground speed (kt) below which JBU577 is at rest.</summary>
    private const double RestKts = 0.5;

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
        return new SimulationEngine(groundData, PushPlanStore.CreateShipped());
    }

    [Fact]
    public void TaxiGBHsB_WarnsAtIssuanceAndTagsTailOverRunway()
    {
        SessionRecording? recording = RecordingLoader.Load(RecordingPath);
        SimulationEngine? engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, Issue172Jbu577TaxiSpinTests.TaxiGBSeconds);
        AircraftState? jbu = engine.FindAircraft("JBU577");
        Assert.NotNull(jbu);

        CommandResult result = engine.SendCommand("JBU577", "TAXI G B HS B");
        output.WriteLine($"echo: {result.Message}");
        Assert.True(result.Success, result.Message);

        // W3 issuance: the TAXI echo warns the controller the aircraft can't clear the runway.
        Assert.Contains("unable to clear the runway", result.Message, StringComparison.OrdinalIgnoreCase);

        // W2 state: the B taxiway hold-short is tagged with the runway hold-short node it overhangs.
        TaxiRoute? route = jbu.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        HoldShortPoint bHold = route.HoldShortPoints.Single(h => h.Reason == HoldShortReason.ExplicitHoldShort && h.TargetName == "B");
        Assert.NotNull(bHold.TailOverRunwayNodeId);

        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);
        Assert.True(layout.Nodes.TryGetValue(bHold.TailOverRunwayNodeId!.Value, out GroundNode? rwyNode));
        Assert.Equal(GroundNodeType.RunwayHoldShort, rwyNode.Type);

        // Negative: the runway-crossing hold-short itself is never tagged tail-over (only the taxiway HS).
        Assert.DoesNotContain(route.HoldShortPoints, h => h.Reason == HoldShortReason.RunwayCrossing && h.TailOverRunwayNodeId is not null);
    }

    [Fact]
    public void HoldingShortOfB_OccupiesRunwayAndEmitsUnableToClearNote()
    {
        SessionRecording? recording = RecordingLoader.Load(RecordingPath);
        SimulationEngine? engine = BuildEngine();
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
        engine.ArmReplay(Issue172Jbu577TaxiSpinTests.ActionsWithTaxiGBMovedEarlier(recording));

        // Recorded commands replay only up to t=513 (TAXI B M1 Y @B5 at t=514 would extend the
        // route past B). Beyond the window, tick physics only (bounded) so the crossing completes
        // and the hold-short-of-B state can be observed — exit/taxi geometry changes legitimately
        // shift the crossing a few seconds past the recording's cutoff.
        const int WindowEnd = 513;
        const int PhysicsOnlyEnd = WindowEnd + 60;

        int? tailOverNode = null;
        bool occupiedWhileHolding = false;
        bool crossingMoved = false;
        int? restedInCrossingAt = null;
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

            AircraftState? ac = engine.FindAircraft("JBU577");
            if (ac is null || t < 444)
            {
                continue;
            }

            if (ac.Phases?.CurrentPhase is CrossingRunwayPhase)
            {
                crossingMoved |= ac.GroundSpeed > MovingKts;
                restedInCrossingAt ??= (crossingMoved && (ac.GroundSpeed < RestKts)) ? t : null;
            }

            bool holdingShort = ac.Phases?.CurrentPhase?.GetType().Name == "HoldingShortPhase";
            HoldShortPoint? bHold = ac.Ground.AssignedTaxiRoute?.HoldShortPoints.FirstOrDefault(h => h.TailOverRunwayNodeId is not null);
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

        // YAAT-1464: the crossing brakes onto the B stop and hands the route back before it comes to rest, so it never rests
        // inside the crossing and the crossing's "not clear of runway" call is never made; only the tail-over note above is.
        Assert.True(crossingMoved, "JBU577 never moved in its crossing");
        Assert.Null(restedInCrossingAt);
        Assert.DoesNotContain(notes, n => n.StartsWith("Holding position, not clear of runway", StringComparison.Ordinal));
    }
}
