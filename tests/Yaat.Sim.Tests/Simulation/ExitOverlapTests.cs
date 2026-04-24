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

            double distNm = GeoMath.DistanceNm(wja.Position.Lat, wja.Position.Lon, skw.Position.Lat, skw.Position.Lon);
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

            double distNm = GeoMath.DistanceNm(skw3398.Position.Lat, skw3398.Position.Lon, skw5899.Position.Lat, skw5899.Position.Lon);
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
