using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for the CLANDF-during-go-around bug (bundle "S2-OAK-P | S2 Rating Practical Exam",
/// ZOA). N500M is a VFR C182 flying closed traffic at OAK. Set up for a touch-and-go on 28L
/// (<c>MLT</c>), it flew the pattern to final and the sim automatically triggered a go-around,
/// leaving it in <see cref="GoAroundPhase"/> (climbing ~1000 fpm through ~354 ft AGL, aligned
/// with 28L) at ~t=1900.
///
/// Observed bug: <c>CLANDF</c> issued to N500M was rejected — "Cannot force landing — no
/// approach or pattern to land from" — because a go-around wipes every pending landing phase,
/// so <see cref="Yaat.Sim.Commands.PatternCommandHandler.TryForceLanding"/> had nothing to
/// commit a landing onto.
///
/// Expected (per instructor intent — "force an aircraft to land that appears to be going
/// around"): CLANDF cancels the go-around, re-establishes the aircraft on final for the
/// assigned runway, and drives it to a full-stop touchdown regardless of energy state.
///
/// Replay strategy: hybrid. Restore the recorded snapshot while N500M is mid-go-around,
/// issue CLANDF out-of-band (it was rejected in the original session, so it is not in the
/// recorded actions), then advance with <see cref="SimulationEngine.TickOneSecond"/> — physics
/// only, so the recorded <c>DEL</c> at t=1917 does not delete the aircraft mid-descent.
/// </summary>
public class ClandfGoAroundReversalE2ETests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/clandf-goaround-recording.zip";
    private const string Callsign = "N500M";
    private const int GoAroundTime = 1900;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("PatternCommandHandler", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Clandf_DuringGoAround_CancelsGoAroundAndLandsOn28L()
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

            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(GoAroundTime);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            // Sanity: N500M is mid-go-around with 28L still assigned — the bug state.
            var pre = engine.FindAircraft(Callsign);
            Assert.NotNull(pre);
            Assert.IsType<GoAroundPhase>(pre.Phases?.CurrentPhase);
            Assert.Equal("28L", pre.Phases?.AssignedRunway?.Designator);

            // Force landing: rejected before the fix ("no approach or pattern to land from").
            var result = engine.SendCommand(Callsign, "CLANDF");
            output.WriteLine($"CLANDF -> Success={result.Success} Message='{result.Message}'");
            Assert.True(result.Success, result.Message);

            var runway = NavigationDatabase.Instance.GetRunway("OAK", "28L");
            Assert.NotNull(runway);
            var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);

            bool touchedDown = false;
            double touchdownDistNm = double.MaxValue;
            double minAlt = double.MaxValue;
            for (int t = 1; t <= 180; t++)
            {
                engine.TickOneSecond();
                var cur = engine.FindAircraft(Callsign);
                if (cur is null)
                {
                    break;
                }

                double dist = GeoMath.DistanceNm(cur.Position, threshold);
                minAlt = Math.Min(minAlt, cur.Altitude);
                if (t % 5 == 0 || cur.IsOnGround)
                {
                    output.WriteLine(
                        $"t={GoAroundTime + t} alt={cur.Altitude:F0} dist={dist:F2}nm hdg={cur.TrueHeading.Degrees:F0} gs={cur.IndicatedAirspeed:F0} phase={cur.Phases?.CurrentPhase?.GetType().Name}"
                    );
                }

                if (cur.IsOnGround)
                {
                    touchedDown = true;
                    touchdownDistNm = dist;
                    break;
                }
            }

            Assert.True(
                touchedDown,
                $"N500M never touched down after CLANDF (min alt {minAlt:F0} ft) — the forced landing did not drive it to the runway."
            );
            Assert.True(touchdownDistNm <= 0.6, $"N500M touched down {touchdownDistNm:F2} nm from the 28L threshold — not on the assigned runway.");
            Assert.Equal("28L", engine.FindAircraft(Callsign)?.Phases?.AssignedRunway?.Designator);
        }
    }
}
