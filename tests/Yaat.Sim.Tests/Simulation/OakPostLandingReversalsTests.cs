using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression tests for the S2-OAK-3 post-landing taxi reversals. Two commands
/// from the recording produced <see cref="TaxiRoute.Segments"/> lists that
/// contained a U-turn — an (a,b) segment immediately followed by (b,a). The
/// walk overshot the ramp branch-off on the last explicit taxiway, and the
/// A* extension back to parking retraced the overshoot.
///
/// - N9225L at t=424: <c>TAXI D @NEW1</c> (102 segments, reversal at index 81).
/// - N436MS at t=455: <c>TAXI C @JSX1</c> (59 segments, reversal at index 46).
/// </summary>
public class OakPostLandingReversalsTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak3-follow-runaway-ias-recording.yaat-bug-report-bundle.zip";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    private static int CountReversals(IReadOnlyList<TaxiRouteSegment> segments)
    {
        int count = 0;
        for (int i = 0; i + 1 < segments.Count; i++)
        {
            var a = segments[i];
            var b = segments[i + 1];
            if (a.FromNodeId == b.ToNodeId && a.ToNodeId == b.FromNodeId)
            {
                count++;
            }
        }

        return count;
    }

    private static void TickUntilAtParking(SimulationEngine engine, string callsign, int maxTicks)
    {
        for (int i = 0; i < maxTicks; i++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft(callsign);
            if (ac?.Phases?.CurrentPhase?.Name == "At Parking")
            {
                return;
            }
        }
    }

    [Fact]
    public void N9225L_TaxiD_AtNEW1_HasNoReversals()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            using var _ = TickRecorder.Attach(engine, "N9225L", Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "oak-n9225l-taxi.csv"));

            // TAXI D @NEW1 is recorded at t=424.
            engine.Replay(recording, 430);

            var ac = engine.FindAircraft("N9225L");
            Assert.NotNull(ac);
            Assert.NotNull(ac.Ground.AssignedTaxiRoute);

            var segments = ac.Ground.AssignedTaxiRoute.Segments;
            int reversals = CountReversals(segments);
            output.WriteLine($"N9225L AssignedTaxiRoute: {segments.Count} segments, {reversals} reversal(s)");
            Assert.True(reversals == 0, $"N9225L TAXI D @NEW1 produced {reversals} reversal(s) in {segments.Count} segments");

            // Tick forward so the aircraft taxis to NEW1 — also produces the full trajectory
            // in the attached recorder's CSV for post-hoc visualization with LayoutInspector.
            TickUntilAtParking(engine, "N9225L", maxTicks: 600);
            ac = engine.FindAircraft("N9225L");
            Assert.NotNull(ac);
            Assert.Equal("At Parking", ac.Phases?.CurrentPhase?.Name);
        }
    }

    [Fact]
    public void N436MS_TaxiC_AtJSX1_HasNoReversals()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            using var _ = TickRecorder.Attach(engine, "N436MS", Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "oak-n436ms-taxi.csv"));

            // The recording's TAXI C @JSX1 at t=455 is rejected during replay because our
            // re-simulated physics has N436MS still in the Landing phase at t=455 (minor
            // drift from the original run). Replay to the end of the recording so the
            // aircraft settles into HoldingAfterExit, then re-issue the command.
            engine.Replay(recording, 614);

            var ac = engine.FindAircraft("N436MS");
            Assert.NotNull(ac);
            output.WriteLine(
                $"N436MS at t=614: phase={ac.Phases?.CurrentPhase?.Name} pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6}) gs={ac.GroundSpeed:F1}"
            );

            var taxi = engine.SendCommand("N436MS", "TAXI C @JSX1");
            Assert.True(taxi.Success, $"TAXI C @JSX1 failed after replay: {taxi.Message}");

            ac = engine.FindAircraft("N436MS");
            Assert.NotNull(ac);
            Assert.NotNull(ac.Ground.AssignedTaxiRoute);

            var segments = ac.Ground.AssignedTaxiRoute.Segments;
            int reversals = CountReversals(segments);
            output.WriteLine($"N436MS AssignedTaxiRoute: {segments.Count} segments, {reversals} reversal(s)");
            Assert.True(reversals == 0, $"N436MS TAXI C @JSX1 produced {reversals} reversal(s) in {segments.Count} segments");

            TickUntilAtParking(engine, "N436MS", maxTicks: 600);
            ac = engine.FindAircraft("N436MS");
            Assert.NotNull(ac);
            Assert.Equal("At Parking", ac.Phases?.CurrentPhase?.Name);
        }
    }
}
