using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #239: N104NT lining up on OAK RWY 33 from taxiway C cut the corner.
/// The hold-short bar on C sits set back from the runway and C meets 33
/// obliquely, so <see cref="LineUpPhase"/>'s graph-blind pivot drove a straight
/// diagonal from the hold-short pose across open ground to the runway centerline
/// — skipping the taxiway.
///
/// The fix makes the maneuver graph-aware: the aircraft taxis along taxiway C to
/// the runway edge and curves onto the centerline via the baked junction fillet
/// arc (ArcFollow), ending tangent to the runway heading.
///
/// Path-realism assertion: ArcFollow routes THROUGH the taxiway-C junction node
/// the fillet arc departs from (~0-10 ft), whereas the recorded corner-cut
/// diagonal never gets within ~70 ft (21 m) of it. Plus the Design D end state
/// (on centerline, aligned).
///
/// Hybrid replay: restore the recorded hold-short snapshot just before the
/// <c>CTO MLT</c> at t=1955, replay the clearance, then tick the maneuver live.
/// </summary>
public class Issue239Lineup33CornerCutTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue239-oak-33-lineup-corner-cut-recording.zip";
    private const string Callsign = "N104NT";

    /// <summary>Recorded CTO MLT (cleared for takeoff) second for N104NT.</summary>
    private const int CtoSecond = 1955;

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

    [Fact]
    public void N104NT_LineUp33FromC_FollowsTaxiwayArc_DoesNotCutCorner()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null || BuildEngine() is null)
        {
            output.WriteLine("Skipped: recording or NavData not available");
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine()!;

            // Restore the hold-short snapshot just before CTO, replay through the
            // clearance, then leave the maneuver to run live.
            engine.Replay(recording, 0);
            var snap = archive.ReadSnapshotAt(1950);
            Assert.NotNull(snap);
            engine.RestoreFromSnapshot(snap!.State);
            int t = (int)snap.ElapsedSeconds;
            engine.FastForwardTo(t + 1, recording.Actions);
            t += 1;
            while (t < CtoSecond + 1)
            {
                engine.ReplayOneSecond();
                t++;
            }

            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);

            var runway = TestVnasData.NavigationDb!.GetRunway("KOAK", "33");
            Assert.NotNull(runway);
            var rwyHdg = runway!.TrueHeading;
            double threshLat = runway.ThresholdLatitude;
            double threshLon = runway.ThresholdLongitude;

            var layout = engine.ResolveGroundLayout(ac!);
            Assert.NotNull(layout);

            // The taxiway-C junction node the fillet arc onto the RWY 33 centerline
            // departs from. ArcFollow passes through it; the corner-cut skips it.
            var junctionNode = FindRunwayJunctionNode(layout!, ac!, runway, rwyHdg);
            Assert.NotNull(junctionNode);
            output.WriteLine($"junction node = {junctionNode!.Id} @ {junctionNode.Position.Lat:F6},{junctionNode.Position.Lon:F6}");

            double minDistToJunctionFt = double.MaxValue;
            double finalCrossFt = double.NaN;
            double finalHdgDiffDeg = double.NaN;
            bool sawLineUp = false;
            bool exited = false;

            for (int s = 0; s < 90; s++)
            {
                engine.TickOneSecond();
                ac = engine.FindAircraft(Callsign);
                if (ac is null)
                {
                    break;
                }

                var phase = ac.Phases?.CurrentPhase;
                if (phase is LineUpPhase)
                {
                    sawLineUp = true;
                    double dFt = GeoMath.DistanceNm(ac.Position, junctionNode.Position) * GeoMath.FeetPerNm;
                    minDistToJunctionFt = Math.Min(minDistToJunctionFt, dFt);
                }
                else if (sawLineUp)
                {
                    exited = true;
                    finalCrossFt =
                        Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position.Lat, ac.Position.Lon, threshLat, threshLon, rwyHdg))
                        * GeoMath.FeetPerNm;
                    finalHdgDiffDeg = Math.Abs(rwyHdg.SignedAngleTo(ac.TrueHeading));
                    break;
                }
            }

            Assert.True(sawLineUp, "N104NT never entered LineUpPhase after CTO");
            output.WriteLine($"min dist to junction node = {minDistToJunctionFt:F1}ft; exit cross={finalCrossFt:F2}ft hdgDiff={finalHdgDiffDeg:F2}°");

            // Path realism: ArcFollow passes through the taxiway-C junction node
            // (~0-10 ft); the corner-cut diagonal stays ~70 ft (21 m) away.
            Assert.True(
                minDistToJunctionFt < 40.0,
                $"N104NT cut the corner: closest approach to the taxiway-C junction node was {minDistToJunctionFt:F1}ft (> 40ft) — "
                    + "it drove a diagonal to the centerline instead of following taxiway C and the junction fillet arc"
            );

            // Design D end state: on centerline, aligned with the runway. Cross-track
            // is measured against the nav-database threshold, which sits ~5 ft off the
            // graph centerline node the aircraft lines up on, so allow that margin.
            Assert.True(exited, "N104NT never exited LineUpPhase within the observation window");
            Assert.True(finalCrossFt < 10.0, $"N104NT off centerline at lineup exit: {finalCrossFt:F2}ft (> 10ft)");
            Assert.True(finalHdgDiffDeg < 3.0, $"N104NT not aligned with RWY 33 at lineup exit: {finalHdgDiffDeg:F2}° (> 3°)");
        }
    }

    /// <summary>
    /// Walk taxiway edges from the node nearest the aircraft toward the runway
    /// (decreasing cross-track from the centerline) until reaching the node that
    /// carries the runway fillet arc onto the centerline — the junction ArcFollow
    /// curves through. Located by graph walk (not node id) so it survives layout
    /// regeneration.
    /// </summary>
    private static GroundNode? FindRunwayJunctionNode(AirportGroundLayout layout, AircraftState ac, RunwayInfo runway, TrueHeading rwyHdg)
    {
        double Cross(GroundNode n) =>
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(n.Position.Lat, n.Position.Lon, runway.ThresholdLatitude, runway.ThresholdLongitude, rwyHdg))
            * GeoMath.FeetPerNm;

        var start = layout.Nodes.Values.OrderBy(n => GeoMath.DistanceNm(ac.Position, n.Position)).First();
        var visited = new HashSet<int> { start.Id };
        var cur = start;

        for (int hop = 0; hop < 8; hop++)
        {
            bool hasRunwayFilletArc = cur.Edges.Any(e =>
                e is GroundArc && !e.IsRunwayCenterline && e.TaxiwayName.Contains("RWY", StringComparison.OrdinalIgnoreCase)
            );
            if (hasRunwayFilletArc)
            {
                return cur;
            }

            GroundNode? next = null;
            foreach (var e in cur.Edges)
            {
                if (e.IsRunwayCenterline)
                {
                    continue;
                }

                var o = e.OtherNode(cur);
                if (!visited.Contains(o.Id) && Cross(o) < Cross(cur) && (next is null || Cross(o) < Cross(next)))
                {
                    next = o;
                }
            }

            if (next is null)
            {
                return null;
            }

            visited.Add(next.Id);
            cur = next;
        }

        return null;
    }
}
