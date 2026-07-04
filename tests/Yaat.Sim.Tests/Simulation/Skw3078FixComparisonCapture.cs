using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Captures SKW3078 + the nearby aircraft that play into the
/// <c>FilletDiagnosticTests.SKW3078_TaxiAtoB10_AdvancesPastFormerStallSegment</c>
/// regression, recording one JSON per fix variant for visual comparison via
/// <c>tools/Yaat.LayoutInspector --html</c>.
///
/// "Before" disables the wingspan-based lateral-clearance skip in
/// <see cref="GroundConflictDetector"/> — the conservative cone-only check
/// that was active before the fix.
///
/// "After" runs with the fix enabled (default behavior).
///
/// The test produces:
///   .tmp/skw3078-before.json
///   .tmp/skw3078-after.json
///
/// Render with:
///   dotnet run --project tools/Yaat.LayoutInspector -- \
///     tests/Yaat.Sim.Tests/TestData/sfo.geojson \
///     --ticks .tmp/skw3078-before.json --html .tmp/skw3078-before.html
///   dotnet run --project tools/Yaat.LayoutInspector -- \
///     tests/Yaat.Sim.Tests/TestData/sfo.geojson \
///     --ticks .tmp/skw3078-after.json --html .tmp/skw3078-after.html
/// </summary>
[Collection("NavDbMutator")]
public class Skw3078FixComparisonCapture(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/sfo-s1-ground-control-28-01-recording.yaat-bug-report-bundle.zip";

    /// <summary>Aircraft that come into play around SKW3078's taxi to B10.</summary>
    private static readonly string[] CapturedCallsigns = ["SKW3078", "DAL2581", "SKW5707", "THY9WC"];

    // This is an artifact-capture tool, not an assertion test: it flips GroundConflictDetector's
    // process-global WingspanLateralCheck flags for a full replay, which races any ground-conflict test
    // running in parallel (e.g. Issue234Spot7AConflictTests) and made that test flaky in the full suite.
    // The flags have no other mutator, so skipping keeps them at their production defaults everywhere.
    // Remove the Skip (and run this class in isolation) to regenerate the .tmp/*.json for LayoutInspector.
    private const string CaptureSkip =
        "Diagnostic capture tool — mutates GroundConflictDetector global flags; run in isolation to regenerate artifacts.";

    [Fact(Skip = CaptureSkip)]
    public void Capture_Before()
    {
        Capture(checkEnabled: false, requireStationary: true, ".tmp/skw3078-before.json");
    }

    [Fact(Skip = CaptureSkip)]
    public void Capture_After()
    {
        Capture(checkEnabled: true, requireStationary: true, ".tmp/skw3078-after.json");
    }

    /// <summary>
    /// Variant: wingspan-based lateral clearance applies to any obstacle
    /// inside the closing cone, not just stationary ones. This was an
    /// earlier iteration of the fix that regressed
    /// FilletDiagnosticTests.SKW3078_TaxiAtoB10 by changing traffic-
    /// interaction timing. Captured here for direct visual comparison.
    /// </summary>
    [Fact(Skip = CaptureSkip)]
    public void Capture_AfterNoStationaryRestriction()
    {
        Capture(checkEnabled: true, requireStationary: false, ".tmp/skw3078-after-no-stationary.json");
    }

    private void Capture(bool checkEnabled, bool requireStationary, string outputRelPath)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        bool savedEnabled = GroundConflictDetector.WingspanLateralCheckEnabled;
        bool savedReqStat = GroundConflictDetector.WingspanLateralCheckRequireStationary;
        GroundConflictDetector.WingspanLateralCheckEnabled = checkEnabled;
        GroundConflictDetector.WingspanLateralCheckRequireStationary = requireStationary;
        try
        {
            var engine = new SimulationEngine(groundData);
            var recording = RecordingLoader.Load(RecordingPath);
            if (recording is null)
            {
                return;
            }

            string outputPath = Path.Combine(TickRecorder.FindRepoRoot(), outputRelPath);
            using var _ = TickRecorder.Attach(engine, outputPath, CapturedCallsigns);

            engine.Replay(recording, 816);
            for (int t = 1; t <= 240; t++)
            {
                engine.ReplayOneSecond();
            }

            output.WriteLine($"[capture] checkEnabled={checkEnabled} requireStationary={requireStationary} → {outputPath}");
        }
        finally
        {
            GroundConflictDetector.WingspanLateralCheckEnabled = savedEnabled;
            GroundConflictDetector.WingspanLateralCheckRequireStationary = savedReqStat;
        }
    }
}
