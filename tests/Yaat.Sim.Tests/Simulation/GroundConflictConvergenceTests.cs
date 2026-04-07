using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for ground conflict: convergence winner drives through yielder.
///
/// Recording: S1-SFO-2 Ground Control 28/01 — UAL194 (B772, parking G1)
/// and THY9WC (A359, parking G6) both push to T9 then taxi T9 toward spot 9.
/// Convergence designates THY9WC as yielder (stops it), but UAL194 (winner)
/// gets no speed limit and drives into THY9WC, overlapping at ~26ft.
///
/// Root cause: TryConvergence returns true and continues, skipping the
/// closing proximity check that would have stopped the winner.
/// </summary>
public class GroundConflictConvergenceTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/ground-conflict-bundle.yaat-bug-report-bundle.zip";

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
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundConflictDetector", Microsoft.Extensions.Logging.LogLevel.Trace).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// UAL194 and THY9WC must maintain at least 60ft separation throughout the
    /// recording. Before the fix, UAL194 (convergence winner) closes to ~26ft
    /// because the closing proximity check is skipped after convergence.
    /// </summary>
    [Fact]
    public void UAL194_And_THY9WC_MaintainSeparation()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // Both aircraft spawn by t=75 (UAL194 at 48, THY9WC at 75).
        // Replay to t=80 so both exist, then tick forward.
        engine.Replay(recording, 80);

        double minDistFt = double.MaxValue;
        int minDistTime = 0;

        // Tick through the remainder of the recording (288 seconds total)
        for (int t = 1; t <= 220; t++)
        {
            engine.ReplayOneSecond();

            var ual = engine.FindAircraft("UAL194");
            var thy = engine.FindAircraft("THY9WC");
            if (ual is null || thy is null)
            {
                continue;
            }

            double distNm = GeoMath.DistanceNm(ual.Latitude, ual.Longitude, thy.Latitude, thy.Longitude);
            double distFt = distNm * 6076.12;

            if (distFt < minDistFt)
            {
                minDistFt = distFt;
                minDistTime = 80 + t;
                output.WriteLine(
                    $"t={80 + t}: new min dist={distFt:F0}ft "
                        + $"UAL194 hdg={ual.TrueHeading.Degrees:F0} gs={ual.GroundSpeed:F1} gsl={ual.GroundSpeedLimit?.ToString("F0") ?? "null"} "
                        + $"THY9WC hdg={thy.TrueHeading.Degrees:F0} gs={thy.GroundSpeed:F1} gsl={thy.GroundSpeedLimit?.ToString("F0") ?? "null"}"
                );
            }
        }

        output.WriteLine($"Minimum separation: {minDistFt:F0}ft at t={minDistTime}");

        Assert.True(
            minDistFt >= 60,
            $"UAL194 and THY9WC overlapped: minimum separation was {minDistFt:F0}ft at t={minDistTime} "
                + "(expected >=60ft). Convergence winner must be stopped by closing proximity check."
        );
    }
}
