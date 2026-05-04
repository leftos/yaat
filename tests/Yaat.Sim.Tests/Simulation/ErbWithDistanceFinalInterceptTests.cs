using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test: N10194 was given <c>ERB 28R 3</c> at OAK at t=466 in the recording
/// "S2-OAK-5 (1) | Practical Exam Preparation/Advanced Concepts" but rolled
/// onto final at ~4.78nm from threshold instead of the requested 3nm.
///
/// Root cause: <see cref="Yaat.Sim.Commands.PatternCommandHandler.GetEntryPoint"/>
/// for ERB+distance offset the entry point diagonally from the displaced
/// threshold using the canonical pattern's BaseTurn (which carries an along-
/// track component). The entry should be a perpendicular offset by the
/// pattern width so the base leg is perpendicular and rolls out on centerline
/// at exactly the requested distance.
/// </summary>
public class ErbWithDistanceFinalInterceptTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/erb-distance-final-intercept-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "N10194";
    private const int ErbDispatchTime = 466;
    private const double RequestedFinalDistanceNm = 3.0;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

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

    /// <summary>
    /// Replay through the recorded ERB dispatch, tick until FinalApproach
    /// engages, and assert the rollout is at or before the requested 3nm.
    /// Without the fix, the aircraft rolls onto final at ~4.78nm.
    /// </summary>
    [Fact]
    public void N10194_ErbWithDistance_RollsOntoFinalAtSpecifiedDistance()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        // Replay through the ERB dispatch so the aircraft has the pattern
        // phases armed. ERB lands at action_index 92 (t=466); replaying to
        // 467 picks it up via action filtering.
        engine.Replay(recording, ErbDispatchTime + 1);

        var ac = engine.FindAircraft(Callsign);
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases?.AssignedRunway);
        var runway = ac.Phases.AssignedRunway;
        Assert.Equal("28R", runway.Designator);

        output.WriteLine(
            $"t={ErbDispatchTime + 1}: {Callsign} pos=({ac.Position.Lat:F5},{ac.Position.Lon:F5}) hdg={ac.TrueHeading.Degrees:F1} alt={ac.Altitude:F0}"
        );
        output.WriteLine(
            $"  AssignedRunway={runway.Designator} threshold=({runway.ThresholdLatitude:F5},{runway.ThresholdLongitude:F5}) hdg={runway.TrueHeading.Degrees:F1}"
        );
        output.WriteLine($"  Phases: {string.Join(" → ", ac.Phases.Phases.Select(p => p.GetType().Name))}");

        // Tick forward until FinalApproachPhase becomes active. Cap at
        // 600 ticks (10 min) which comfortably covers the recorded 144s.
        FinalApproachPhase? finalPhase = null;
        int tickedSeconds = 0;
        for (int t = 1; t <= 600; t++)
        {
            engine.ReplayOneSecond();
            tickedSeconds = t;
            ac = engine.FindAircraft(Callsign);
            if (ac is null)
            {
                output.WriteLine($"  t+{t}: aircraft deleted");
                break;
            }

            if (ac.Phases?.CurrentPhase is FinalApproachPhase fa && fa.Status == PhaseStatus.Active)
            {
                finalPhase = fa;
                break;
            }

            if (t % 30 == 0)
            {
                var phaseName = ac.Phases?.CurrentPhase?.GetType().Name ?? "(none)";
                output.WriteLine(
                    $"  t+{t, 3}: phase={phaseName} pos=({ac.Position.Lat:F5},{ac.Position.Lon:F5}) hdg={ac.TrueHeading.Degrees:F1} alt={ac.Altitude:F0}"
                );
            }
        }

        Assert.NotNull(finalPhase);
        Assert.NotNull(ac);

        // Compute along-track distance from threshold along reciprocal of
        // runway heading (positive = outbound from threshold).
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        TrueHeading reciprocal = runway.TrueHeading.ToReciprocal();
        double alongTrackOutbound = GeoMath.AlongTrackDistanceNm(ac.Position, threshold, reciprocal);
        double crossTrack = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position, threshold, runway.TrueHeading));

        output.WriteLine(
            $"  FinalApproach engaged at t+{tickedSeconds}: pos=({ac.Position.Lat:F5},{ac.Position.Lon:F5}) along={alongTrackOutbound:F2}nm cross={crossTrack:F2}nm alt={ac.Altitude:F0}"
        );

        // Tolerance: the aircraft starts the turn at cross-track ≈ turn
        // radius, so the rollout point lands at along ≈ FinalDistanceNm
        // ± turn-radius wobble. 0.5 nm is comfortable for a C172 at base
        // speed (~70kt → turn radius ~0.2 nm). Without the fix the value
        // is ~4.78 nm, well outside the tolerance.
        Assert.True(
            alongTrackOutbound <= RequestedFinalDistanceNm + 0.5,
            $"Aircraft should roll onto final at ≤ {RequestedFinalDistanceNm + 0.5}nm from threshold (requested {RequestedFinalDistanceNm}nm + 0.5nm tolerance), but rolled out at {alongTrackOutbound:F2}nm"
        );
    }
}
