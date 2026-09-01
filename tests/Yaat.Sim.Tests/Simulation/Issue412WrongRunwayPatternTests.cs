using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E test for GitHub issue #412: aircraft instructed to enter the pattern for one runway
/// tried to land on the parallel (bundle "OAK TWR | North Field Day 13", ZOA). N115SA is a
/// PAY3 Cheyenne commanded <c>ELD 28L</c> at OAK. The OAK layout authors 28L with
/// PatternSizeNm=0.5 / TPA 600 AGL (sized for light GA); the sim applied that width to the
/// turboprop, which flies the pattern at ~121 KIAS with a ~0.48 nm turn radius. The 180° of
/// turning from downwind to final needs r(downwind)+r(base) ≈ 0.94 nm of lateral room, so the
/// aircraft was geometrically forced through the 28L extended centerline, rolling out ~1,370 ft
/// right of it — on the 28R final (the parallels are ~1,000 ft apart) — before the
/// no-landing-clearance go-around fired.
///
/// Expected: the pattern width is floored at the turn-radius-derived minimum (AIM 4-3-3.b;
/// AIM FIG 4-3-3 key 7 — do not overshoot final onto a parallel runway's final), so the
/// aircraft rolls out on the 28L centerline.
///
/// Replay strategy: hybrid. A fixed pattern width changes every pattern aircraft's geometry in
/// this 65-ops scenario, so a full replay diverges from the recorded commands long before the
/// bug. Restore the snapshot at t=2095 (just before the second <c>ELD 28L</c> at t=2097 — later
/// snapshots pin the recorded 0.5 nm waypoints), replay through the ELD so it dispatches with
/// current code, then tick physics forward and watch cross-track against the 28L centerline.
/// </summary>
public class Issue412WrongRunwayPatternTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue412-wrong-runway-pattern-recording.zip";
    private const string Callsign = "N115SA";
    private const int SnapshotTime = 2095;

    /// <summary>Half the 28L→28R centerline spacing (~1,000 ft): beyond this the aircraft reads as lined up on 28R.</summary>
    private const double MaxOvershootNm = 0.08;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("DownwindPhase", LogLevel.Debug)
            .EnableCategory("BasePhase", LogLevel.Debug)
            .EnableCategory("PatternGeometry", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Eld28L_Turboprop_RollsOutOn28LFinal_NotThe28RParallel()
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

            var snapshot = archive.ReadSnapshotAt(SnapshotTime);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);

            // Apply the recorded ELD 28L at t=2097 with current code, then leave the
            // recording behind (physics-only ticks) so the recorded DEL at t=2270
            // doesn't delete the aircraft mid-pattern.
            engine.ReplayRange(SnapshotTime, SnapshotTime + 5, recording.Actions);

            var aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);

            // Clear it to land so the 200-ft no-clearance go-around doesn't cut the final short.
            var cland = engine.SendCommand(Callsign, "CLAND");
            Assert.True(cland.Success, $"CLAND rejected: {cland.Message}");

            var runway = aircraft.Phases?.AssignedRunway;
            Assert.NotNull(runway);
            Assert.Equal("28L", runway.Designator);
            var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);

            double maxOvershootNm = double.MinValue;
            bool establishedOnFinal = false;

            for (int t = 0; t < 480; t++)
            {
                engine.TickOneSecond();
                aircraft = engine.FindAircraft(Callsign);
                Assert.NotNull(aircraft);

                if (aircraft.IsOnGround)
                {
                    break;
                }

                // Signed cross-track along the landing course: positive = right of the 28L
                // centerline as flown (toward 28R), negative = the left-pattern side.
                double xteNm = GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, threshold, runway.TrueHeading);
                double alongNm = GeoMath.AlongTrackDistanceNm(aircraft.Position, threshold, runway.TrueHeading.ToReciprocal());
                double offCourseDeg = aircraft.TrueHeading.AbsAngleTo(runway.TrueHeading);
                maxOvershootNm = Math.Max(maxOvershootNm, xteNm);

                if (t % 20 == 0)
                {
                    output.WriteLine(
                        $"t=+{t} alt={aircraft.Altitude:F0} hdg={aircraft.TrueHeading.Degrees:F0} xte={xteNm * 6076:F0}ft final={alongNm:F2}nm"
                    );
                }

                if ((offCourseDeg < 15) && (Math.Abs(xteNm) < 0.05) && (alongNm > 0.2) && (alongNm < 2.5))
                {
                    establishedOnFinal = true;
                }
            }

            Assert.True(
                maxOvershootNm < MaxOvershootNm,
                $"Aircraft overshot the 28L final by {maxOvershootNm * 6076:F0} ft — past halfway to the parallel 28R "
                    + $"(28L/28R centerlines are ~1,000 ft apart). Max allowed: {MaxOvershootNm * 6076:F0} ft."
            );

            Assert.True(establishedOnFinal, "Aircraft never established on the 28L final approach course");
            Assert.NotNull(aircraft);
            Assert.True(aircraft.IsOnGround, "Aircraft never touched down on 28L");

            double finalXteNm = GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, threshold, runway.TrueHeading);
            Assert.True(Math.Abs(finalXteNm) < 0.03, $"Touchdown {finalXteNm * 6076:F0} ft off the 28L centerline — landed on the wrong surface");
        }
    }
}
