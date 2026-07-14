using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the <c>P270</c> ("plan 270 at next turn") wrong-way bug.
///
/// Recording: S2-OAK-3 "VFR Sequencing". N123AB (C172, VFR, RIGHT traffic to OAK 28R) is on the
/// Base leg (base heading ~202°, final ~292°) when the controller issues <c>P270</c> to make it
/// fly the long way round from base to final and open spacing behind traffic ahead. Instead the
/// aircraft began turning RIGHT — the short ~90° way straight toward final — and was deleted.
///
/// Root cause: <see cref="Yaat.Sim.Commands.PatternCommandHandler"/>.TryPlan270 planned the 270 in
/// the SAME rotational sense as the traffic pattern. A 270 in the pattern's own direction rolls out
/// on the reciprocal of final; only a 270 OPPOSITE the pattern (LEFT, for right traffic) returns to
/// the final course the long way (base 202° + 90° = 292°). After the fix the aircraft turns LEFT,
/// away from the runway toward the downwind side, never right toward final.
/// </summary>
public class Plan270LongWayTurnTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/plan270-long-way-turn-recording.yaat-bug-report-bundle.zip";

    /// <summary>Snapshot just before the user typed <c>P270</c> at t=639; N123AB is on Base here.</summary>
    private const int SnapshotSeconds = 636;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("PatternCommandHandler", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void N123AB_Plan270FromRightBase_TurnsLeftTheLongWay()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            engine.Replay(archive.ToBaseSessionRecording(), 0);
            var snapshot = archive.ReadSnapshotAt(SnapshotSeconds);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            var aircraft = engine.FindAircraft("N123AB");
            Assert.NotNull(aircraft);

            // Preconditions: established on a RIGHT-traffic Base leg.
            Assert.IsType<BasePhase>(aircraft.Phases!.CurrentPhase);
            Assert.Equal(PatternDirection.Right, aircraft.Phases.TrafficDirection);
            double baseHeading = aircraft.TrueHeading.Degrees;
            output.WriteLine($"pre: phase={aircraft.Phases.CurrentPhase?.Name} hdg={baseHeading:F0} traffic={aircraft.Phases.TrafficDirection}");

            var result = engine.SendCommand("N123AB", "P270");
            output.WriteLine($"P270 -> Success={result.Success} Message='{result.Message}'");

            // For a RIGHT-traffic pattern the long way round to final is a LEFT 270.
            Assert.True(result.Success, result.Message);
            Assert.Contains("left", result.Message!, StringComparison.OrdinalIgnoreCase);

            var planned = aircraft.Phases.Phases[aircraft.Phases.CurrentIndex + 1] as MakeTurnPhase;
            Assert.NotNull(planned);
            Assert.Equal(TurnDirection.Left, planned!.Direction);

            // Advance physics only (never replay — the user deleted the aircraft at t=666). The Base
            // leg completes, the MakeTurn starts, and the aircraft must turn LEFT (away from the
            // runway, toward the downwind side) the long way around, then roll out established on the
            // 292° final — not turn the short way right toward it.
            double finalHeading = baseHeading + 90; // right-traffic base→final is +90°
            double minSignedDelta = 0; // most-negative (leftward) swing seen
            bool rolledOutOnFinal = false;
            for (int t = 1; t <= 120 && !rolledOutOnFinal; t++)
            {
                engine.TickOneSecond();
                var ac = engine.FindAircraft("N123AB");
                if (ac is null)
                {
                    break;
                }

                double signed = NormalizeAngleDiff(ac.TrueHeading.Degrees - baseHeading);
                minSignedDelta = Math.Min(minSignedDelta, signed);

                // Only counts as rolling out on final AFTER the long left loop (so the short-way bug,
                // which reaches ~292° early by turning right, can't satisfy it).
                if (minSignedDelta < -120 && Math.Abs(NormalizeAngleDiff(ac.TrueHeading.Degrees - finalHeading)) < 15)
                {
                    rolledOutOnFinal = true;
                }

                if (t % 10 == 0)
                {
                    output.WriteLine(
                        $"t=+{t}s phase={ac.Phases?.CurrentPhase?.Name} hdg={ac.TrueHeading.Degrees:F0} "
                            + $"signedDelta={signed:F0} minSigned={minSignedDelta:F0} rolledOut={rolledOutOnFinal}"
                    );
                }
            }

            Assert.True(minSignedDelta < -60, $"P270 must turn the aircraft LEFT (the long way); most-negative swing was {minSignedDelta:F0}°");
            Assert.True(rolledOutOnFinal, "P270 must roll the aircraft out on the 292° final the long way around, not turn the short way toward it");
        }
    }

    private static double NormalizeAngleDiff(double deg)
    {
        double d = deg % 360;
        if (d > 180)
        {
            d -= 360;
        }

        if (d < -180)
        {
            d += 360;
        }

        return d;
    }
}
