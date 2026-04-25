using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the "OAK 28R picks J instead of G/H" bug.
///
/// Recording: S2-OAK-3 (1) | VFR Sequencing — N9225L (C172) lands on 28R
/// with no explicit exit instruction. Before the fix the aircraft coasts
/// past G and H and only exits at J (a high-speed 39° exit with 18 kt turnoff).
///
/// Root cause (verified by diagnostic): two stacked issues:
///  1. RolloutDecelRate(Piston)=1.5 kts/s → ComfortableBrakingMultiplier×Default=2.25 kts/s
///     comfort ceiling is below routine real-world C172 braking (~3 kts/s ≈ 0.15 g).
///  2. LandingPhase.TickRollout clamps targetSpeed at RolloutCoastSpeed (25 kt for piston),
///     so even when a 12-kt standard exit is comfortably reachable, the phase won't plan
///     for it — and the missed-exit check at distToBranch≤0 fires for every standard exit
///     regardless of speed, so G and H are structurally unreachable for a piston at coast.
///
/// Aviation-sim-expert (AIM 4-3-21.1) confirms C172-class aircraft are expected to exit
/// at the first available taxiway, not coast past to a high-speed exit.
/// </summary>
public class OakRunwayExitTooFarTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak3-vfr-sequencing-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N9225L";
    private const int TouchdownSecond = 400;
    private const int RolloutEndSecond = 485;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine(bool enableExitLogs)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var builder = SimLogBuilder.CreateForTest(output);
        if (enableExitLogs)
        {
            builder = builder.EnableCategory("AirportGroundLayout", LogLevel.Debug).EnableCategory("LandingPhase", LogLevel.Debug);
        }

        builder.InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Diagnostic: replay the bundle through rollout, log per-tick groundspeed,
    /// current taxiway, and nearest nodes. Enable AirportGroundLayout + LandingPhase
    /// debug logs so every [ExitCL] and [ExitBFS] score line is visible in xunit output.
    /// Writes a TickRecorder CSV for LayoutInspector overlay.
    /// </summary>
    [Fact]
    public void Diagnostic_LogRolloutScoreBreakdown()
    {
        var recording = LoadRecording();
        var engine = BuildEngine(enableExitLogs: true);
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, TouchdownSecond);

        var aircraft = engine.FindAircraft(Callsign);
        if (aircraft is null)
        {
            output.WriteLine($"Aircraft {Callsign} not found at t={TouchdownSecond}; skipping diagnostic.");
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        var recorder = new TickRecorder(aircraft);

        for (int t = TouchdownSecond + 1; t <= RolloutEndSecond; t++)
        {
            engine.ReplayOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            if (aircraft is null)
            {
                break;
            }

            recorder.Record(t);

            output.WriteLine(
                $"t={t} gs={aircraft.GroundSpeed:F1} ias={aircraft.IndicatedAirspeed:F1} "
                    + $"twy={aircraft.Ground.CurrentTaxiway ?? "(runway)"} "
                    + $"pos=({aircraft.Position.Lat:F6},{aircraft.Position.Lon:F6}) hdg={aircraft.TrueHeading.Degrees:F0}"
            );
            if (layout is not null)
            {
                NearestNodeHelper.Log(output, $"  t={t}:", aircraft, layout);
            }
        }

        string repoRoot = TickRecorder.FindRepoRoot();
        string jsonPath = Path.Combine(repoRoot, ".tmp", "oak-28r-n9225l-rollout.json");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        recorder.WriteJson(jsonPath);
        output.WriteLine($"Wrote tick CSV to {jsonPath}");
    }

    /// <summary>
    /// Assertion: after rollout, N9225L should be on taxiway G or H, not J.
    /// Fails against current code (aircraft ends on J). Passes after the piston
    /// decel + brake-below-coast fixes.
    /// </summary>
    [Fact]
    public void N9225L_ExitsAtGorH_NotJ()
    {
        var recording = LoadRecording();
        var engine = BuildEngine(enableExitLogs: false);
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, TouchdownSecond);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);

        // Replay through rollout — exit is resolved and aircraft turns off during
        // this window (t=405–460 typical, leaving margin for the fix to pick an
        // earlier exit like G or H).
        string? lastTaxiway = null;
        for (int t = TouchdownSecond + 1; t <= RolloutEndSecond; t++)
        {
            engine.ReplayOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);

            // Capture the first non-null, non-runway CurrentTaxiway — that's the
            // exit taxiway the phase committed to. We check it hasn't progressed
            // on to a different taxiway (aircraft might be taxiing onward); first
            // assignment is the exit choice.
            if (aircraft.Ground.CurrentTaxiway is not null && lastTaxiway is null)
            {
                lastTaxiway = aircraft.Ground.CurrentTaxiway;
                output.WriteLine($"t={t}: aircraft committed to taxiway {lastTaxiway} at gs={aircraft.GroundSpeed:F1}");
            }

            if (aircraft.GroundSpeed <= 1.0 && lastTaxiway is not null)
            {
                break;
            }
        }

        Assert.NotNull(lastTaxiway);
        Assert.True(
            lastTaxiway is "G" or "H",
            $"Expected exit at G or H for C172 landing on OAK 28R with no exit instruction, "
                + $"got {lastTaxiway}. AIM 4-3-21.1: exit at first available taxiway."
        );
    }
}
