using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for runway exit overlap bug: SKW5899 exits 28R onto taxiway T
/// even though SKW3398 is already holding there after exiting. Instead of
/// choosing a different exit or stopping behind, SKW5899 snaps to SKW3398's
/// exact position, causing overlap.
///
/// Recording: S1-SFO-2 Ground Control 28/01
/// </summary>
public class ExitOverlapTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue-exit-overlap-recording.yaat-bug-report-bundle.zip";

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

    /// <summary>
    /// SKW5899 must not exit onto the same taxiway as SKW3398 when SKW3398
    /// is already holding at taxiway T's hold-short. The exit selection should
    /// skip the occupied exit and pick a different taxiway.
    /// </summary>
    [Fact]
    public void SKW5899_DoesNotExitOntoOccupiedTaxiwayT()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Replay to t=640 — SKW3398 is holding after exit on T,
        // SKW5899 is about to enter RunwayExitPhase
        engine.Replay(recording, 640);

        var skw3398 = engine.FindAircraft("SKW3398");
        Assert.NotNull(skw3398);
        Assert.Equal("Holding After Exit", skw3398.Phases?.CurrentPhase?.Name);
        Assert.Equal("T", skw3398.CurrentTaxiway);

        // Tick until SKW5899 enters RunwayExitPhase and picks an exit
        for (int t = 1; t <= 200; t++)
        {
            engine.ReplayOneSecond();

            var skw5899 = engine.FindAircraft("SKW5899");
            if (skw5899?.Phases?.CurrentPhase is RunwayExitPhase rep && rep.TargetHoldShortNodeId is not null)
            {
                output.WriteLine($"t={640 + t}: SKW5899 selected exit on taxiway {skw5899.CurrentTaxiway ?? "unknown"}");

                Assert.True(skw5899.CurrentTaxiway != "T", "SKW5899 should not exit onto T (occupied by SKW3398), but selected T");
                return;
            }
        }

        Assert.Fail("SKW5899 never selected an exit within 200 seconds");
    }

    /// <summary>
    /// Verify that SKW5899 on D(right) and WJA1508 on D(left) don't overlap.
    /// D has hold-shorts on both sides of 28R at SFO (nodes 833/834).
    /// </summary>
    [Fact]
    public void SKW5899_AndWJA1508_DoNotOverlapOnD()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 660);

        for (int t = 1; t <= 150; t++)
        {
            engine.ReplayOneSecond();

            var wja = engine.FindAircraft("WJA1508");
            var skw = engine.FindAircraft("SKW5899");
            if (wja is null || skw is null || !skw.IsOnGround)
            {
                continue;
            }

            double distNm = GeoMath.DistanceNm(wja.Latitude, wja.Longitude, skw.Latitude, skw.Longitude);
            double distFt = distNm * 6076.12;
            Assert.True(distFt > 60, $"SKW5899 and WJA1508 overlap at t={660 + t}: distance={distFt:F0}ft");
        }
    }

    /// <summary>
    /// After both aircraft have exited, they must never overlap — the minimum
    /// distance between them must exceed one aircraft length (~60ft).
    /// </summary>
    [Fact]
    public void ExitingAircraft_NeverOverlap()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 200);

        double minDistFt = double.MaxValue;

        for (int t = 1; t <= 600; t++)
        {
            engine.ReplayOneSecond();

            var skw3398 = engine.FindAircraft("SKW3398");
            var skw5899 = engine.FindAircraft("SKW5899");
            if (skw3398 is null || skw5899 is null)
            {
                continue;
            }

            // Only check once both are on the ground
            if (!skw3398.IsOnGround || !skw5899.IsOnGround)
            {
                continue;
            }

            double distNm = GeoMath.DistanceNm(skw3398.Latitude, skw3398.Longitude, skw5899.Latitude, skw5899.Longitude);
            double distFt = distNm * 6076.12;

            if (distFt < minDistFt)
            {
                minDistFt = distFt;
            }

            Assert.True(distFt > 60, $"Aircraft overlap at t={200 + t}: distance={distFt:F0}ft (must be >60ft)");
        }

        output.WriteLine($"Minimum distance between aircraft: {minDistFt:F0}ft");
    }
}
