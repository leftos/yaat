using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// SWA4587 (B737/L, IFR) was an arrival established on the OAK ILS RWY 30
/// (FinalApproach → Landing, glideslope captured). Inside the 5 nm final gate the
/// controller issued <c>RFAS</c> (Reduce to Final Approach Speed — a pure speed
/// instruction). The whole approach was torn down: FinalApproachPhase.CanAcceptCommand
/// returned <c>ClearsPhase</c> for the speed family inside 5 nm, and (unlike a plain
/// SPD, which a separate FlightCommandHandler gate rejects) RFAS passed dry-run
/// validation so the phase-clear actually fired. The client logged the nonsensical
/// "pattern to RWY 30 cancelled by RFAS" and the aircraft leveled off.
///
/// Expected: inside the gate the pilot replies "unable" and the approach is preserved —
/// a speed instruction never tears down a stabilized final.
///
/// Recording: S2-OAK-5 (2) | Practical Exam Preparation/Advanced Concepts (ZOA), trimmed
/// to its scenario + actions. Full replay from t=0 — the fix changes only command
/// acceptance at the RFAS moment, so SWA4587's pre-RFAS trajectory is identical.
/// </summary>
public class Swa4587RfasRejectsInsideGateTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/swa4587-rfas-clears-final-recording.zip";
    private const string Callsign = "SWA4587";

    // OAK RWY 30 landing threshold (from the scenario runway geometry).
    private const double Rwy30ThresholdLat = 37.701493194444446;
    private const double Rwy30ThresholdLon = -122.21425797222223;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Replay to t=2086 (SWA4587 ~4.9 nm on final, inside the 5 nm gate) and issue RFAS.
    /// The command must be rejected ("unable, inside 5nm final") and FinalApproachPhase
    /// must survive. Before the fix RFAS cleared the phase (Phases == null).
    /// </summary>
    [Fact]
    public void RfasInsideGate_RejectsAndPreservesApproach()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        engine.Replay(recording, 2086);

        var before = engine.FindAircraft(Callsign);
        Assert.NotNull(before);
        Assert.IsType<FinalApproachPhase>(before.Phases?.CurrentPhase);

        double distNm = GeoMath.DistanceNm(before.Position, new LatLon(Rwy30ThresholdLat, Rwy30ThresholdLon));
        output.WriteLine($"Pre-RFAS: phase={before.Phases?.CurrentPhase?.GetType().Name} distToThr={distNm:F2}nm alt={before.Altitude:F0}");
        Assert.True(distNm < 5.0, $"Aircraft should be inside the 5 nm gate, was {distNm:F2}nm");

        var result = engine.SendCommand(Callsign, "RFAS");
        output.WriteLine($"RFAS result: success={result.Success} message={result.Message}");

        // Rejected with a pilot "unable", NOT a phase clear.
        Assert.False(result.Success);
        Assert.Contains("5nm final", result.Message);

        var after = engine.FindAircraft(Callsign);
        Assert.NotNull(after);
        Assert.NotNull(after.Phases);
        Assert.IsType<FinalApproachPhase>(after.Phases.CurrentPhase);
    }
}
