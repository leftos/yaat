using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #315: LUAW is accepted but the aircraft never moves,
/// after a re-taxi that crosses one parallel runway to reach the other.
///
/// Recording: S2-SFO-4 | Shared Dep/Arr on Parallel Runways.
///
/// SKW5237 is set up for 28L, then re-taxied at t=536 with
/// <c>TAXI F C HS 10R RWY 28R</c> — an explicit hold-short at 10R/28L followed by a
/// DestinationRunway hold-short at 28R. On RES (t=588) the crossing of 28L exits at
/// the 28R hold-short node (SFO taxiway C has a single painted bar between the two
/// runways), which consumed the rest of the route and terminated the phase chain
/// with HoldingInPositionPhase instead of HoldingShortPhase(28R).
///
/// LUAW (t=624) therefore took DepartureClearanceHandler.LineUpFromPosition, which
/// built its PhaseContext without a ground layout — LineUpPhase faulted immediately
/// with "ctx.GroundLayout is null" while the command still reported
/// "OK — Line up and wait runway 28R". The aircraft sat on the hold line until the
/// controller deleted it.
///
/// SKW3473 is the control case: its 10R/28L bar was an auto-cleared RunwayCrossing,
/// so no CrossingRunwayPhase was built, it reached the 28R bar through the normal
/// TaxiingPhase path, got HoldingShortPhase, and departed normally.
/// </summary>
public class Issue315LuawAfterCrossingTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue315-luaw-after-crossing-recording.zip";

    /// <summary>Recording time at which SKW5237 is stopped on the 28R hold line, before the LUAW at t=624.</summary>
    private const int Skw5237HoldingTime = 620;

    /// <summary>Recording time just past the LUAW at t=624. Ticking beyond t=666 would replay the controller's panic re-taxi.</summary>
    private const int Skw5237PostLuawTime = 630;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Skw5237_HoldsShortOfDepartureRunwayAfterCrossing()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, Skw5237HoldingTime);

        var ac = engine.FindAircraft("SKW5237");
        Assert.NotNull(ac);

        var phase = ac.Phases?.CurrentPhase;
        output.WriteLine($"t={Skw5237HoldingTime}: SKW5237 phase={phase?.Name} ias={ac.IndicatedAirspeed:F1}");

        var holding = Assert.IsType<HoldingShortPhase>(phase);
        Assert.Equal(HoldShortReason.DestinationRunway, holding.HoldShort.Reason);
        Assert.Equal("28R", holding.HoldShort.TargetName);
    }

    [Fact]
    public void Skw5237_StopsShortOfTheHoldLine_NotInsideTheMarkings()
    {
        // The crossing appends a half-fuselage tail-clearance overshoot past its exit node. When that exit node
        // is itself an uncleared hold-short bar, the overshoot carries the aircraft inside runway 28R's holding
        // position markings while it reports holding short of them — a runway incursion (AIM 4-3-18.a.6).
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, Skw5237HoldingTime);

        var ac = engine.FindAircraft("SKW5237");
        Assert.NotNull(ac);

        var holdShort = ac.Ground.AssignedTaxiRoute?.HoldShortPoints.FirstOrDefault(hs => hs.Reason == HoldShortReason.DestinationRunway);
        Assert.NotNull(holdShort);
        Assert.NotNull(holdShort.Latitude);
        Assert.NotNull(holdShort.Longitude);

        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);
        Assert.True(layout.Nodes.TryGetValue(holdShort.NodeId, out var barNode), $"hold-short node {holdShort.NodeId} missing from the SFO layout");

        double toBarFt = GeoMath.DistanceNm(ac.Position, barNode.Position) * GeoMath.FeetPerNm;
        double toStopFt = GeoMath.DistanceNm(ac.Position, new LatLon(holdShort.Latitude.Value, holdShort.Longitude.Value)) * GeoMath.FeetPerNm;
        double barToStopFt =
            GeoMath.DistanceNm(barNode.Position, new LatLon(holdShort.Latitude.Value, holdShort.Longitude.Value)) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"t={Skw5237HoldingTime}: SKW5237 {toBarFt:F1}ft from the 28R bar, {toStopFt:F1}ft from its stop point (bar->stop {barToStopFt:F1}ft)"
        );

        // Stopping on the approach side means the aircraft is nearer its own stop point than the painted bar is
        // to that stop point — an overshoot past the bar puts it on the far side and inverts the comparison.
        Assert.True(toStopFt < barToStopFt, $"SKW5237 stopped {toBarFt:F1}ft past the 28R holding position markings instead of short of them");
    }

    [Fact]
    public void Skw5237_LuawFromDestinationHoldShort_LinesUpOnRunway()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay past the recorded LUAW, then tick physics only. The recording's later
        // actions (TAXI 28R C at t=666, DEL at t=686) are the controller working around
        // this very bug and would destroy the line-up under test.
        engine.Replay(recording, Skw5237PostLuawTime);

        var ac = engine.FindAircraft("SKW5237");
        Assert.NotNull(ac);
        output.WriteLine($"t={Skw5237PostLuawTime}: SKW5237 phase={ac.Phases?.CurrentPhase?.Name}");

        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("SKW5237");
            Assert.NotNull(ac);

            var phase = ac.Phases?.CurrentPhase;
            if (phase is LineUpPhase lineUp)
            {
                Assert.False(
                    lineUp.CurrentState == LineUpPhase.State.Faulted,
                    $"LineUpPhase faulted {t}s after LUAW — the aircraft accepted the clearance but cannot line up"
                );
            }

            if (phase is LinedUpAndWaitingPhase)
            {
                output.WriteLine($"t={Skw5237PostLuawTime + t}: SKW5237 lined up and waiting");
                return;
            }
        }

        Assert.Fail($"SKW5237 never reached LinedUpAndWaiting; phase={ac.Phases?.CurrentPhase?.Name} ias={ac.IndicatedAirspeed:F1}");
    }

    [Fact]
    public void Skw3473_LuawFromHoldShort_StillDeparts()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Control case: TAXI F C 28R (t=768), LUAW (t=885), CTO (t=948). Airborne by t=990.
        engine.Replay(recording, 990);

        var ac = engine.FindAircraft("SKW3473");
        Assert.NotNull(ac);
        output.WriteLine($"t=990: SKW3473 phase={ac.Phases?.CurrentPhase?.Name} alt={ac.Altitude:F0} onGround={ac.IsOnGround}");

        Assert.False(ac.IsOnGround, "SKW3473 should be airborne after CTO at t=948");
        Assert.Equal("28R", ac.Phases?.AssignedRunway?.Designator);
    }
}
