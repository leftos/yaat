using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-1527, replayed from the OAK S1-OAK-7 bundle: N9671E follows N33069 west on C. The lead held short of 15/33 at C on its
/// own route (<c>TAXI E C F @GA13 HS 15</c>) and was released with RES at t=1888. The follower's trail runs on along C across
/// 15/33, so it holds at the 15/33 bar on C, never at the 28R/10L bar on J beside that junction, which is off its trail; and the
/// context menu's Cross, which names the held runway, keeps it on the trail.
/// </summary>
public class Oak1527FollowerBarOnTrailTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/yaat1527-oak-followg-cross-recording.yaat-bug-report-bundle.zip";
    private const string Follower = "N9671E";
    private const string Lead = "N33069";

    /// <summary>A snapshot second with the lead held short of 15/33 at C and the follower stopped behind it.</summary>
    private const double RestoreSecond = 1880;

    /// <summary>The last second before the recorded CROSS 28R (t=1914).</summary>
    private const int HoldBudgetSecond = 1913;

    /// <summary>How long (s) the follower is ticked after its crossing clearance.</summary>
    private const int CrossingSeconds = 40;

    /// <summary>How far (ft) the follower may stand from its trail's centreline and still be on it.</summary>
    private const double OnTrailFt = 60;

    /// <summary>How far (ft) along its trail past the 15/33 bar the follower must be by the end, so a follower that never moved fails.</summary>
    private const double MinPastBarFt = 300;

    [Fact]
    public void FollowerBehindLeadReleasedAcross15_HoldsShortOf15AtC()
    {
        if (Start() is not { } run)
        {
            return;
        }

        HoldingShortPhase hold = ReplayUntilFollowerHolds(run.Engine);
        GroundNode bar15AtC = GroundNodeLookup.HoldShortBar(run.Layout, "15", "C", nearestTo: GroundNodeLookup.Junction(run.Layout, "C", "J"));
        output.WriteLine($"held at node {hold.HoldShort.NodeId} target={hold.HoldShort.TargetName}; the 15/33 bar on C is {bar15AtC.Id}");

        Assert.Equal(bar15AtC.Id, hold.HoldShort.NodeId);
        var held = RunwayIdentifier.Parse(Assert.IsType<string>(hold.HoldShort.TargetName));
        Assert.True((held.Contains("15")) && (held.Contains("33")), $"{Follower} held short of {held}, not 15/33");
    }

    [Fact]
    public void FollowerAtThe15BarCrossing15_StaysOnTheLeadsTrail()
    {
        if (Start() is not { } run)
        {
            return;
        }

        AircraftState follower = Assert.IsType<AircraftState>(run.Engine.FindAircraft(Follower));
        TaxiRoute trail = Assert.IsType<TaxiRoute>(Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase).FollowRoute);
        List<LatLon> trailPoints =
        [
            run.Layout.Nodes[trail.Segments[0].FromNodeId].Position,
            .. trail.Segments.Select(s => run.Layout.Nodes[s.ToNodeId].Position),
        ];

        HoldingShortPhase hold = ReplayUntilFollowerHolds(run.Engine);

        // What the context menu sends at a hold: CROSS with the held runway's first end.
        string menuRunway = RunwayIdentifier.Parse(Assert.IsType<string>(hold.HoldShort.TargetName)).End1;
        CommandResult result = run.Engine.SendCommand(Follower, $"CROSS {menuRunway}");
        output.WriteLine($"CROSS {menuRunway}: success={result.Success} '{result.Message}'");
        Assert.True(result.Success);

        GroundNode bar15AtC = GroundNodeLookup.HoldShortBar(run.Layout, "15", "C", nearestTo: GroundNodeLookup.Junction(run.Layout, "C", "J"));
        double barAlongFt = OnPolyline(bar15AtC.Position, trailPoints).AlongFt;
        double worstOffTrailFt = 0;
        double pastBarFt = 0;
        for (int s = 1; s <= CrossingSeconds; s++)
        {
            run.Engine.TickOneSecond();
            follower = Assert.IsType<AircraftState>(run.Engine.FindAircraft(Follower));
            (double offTrailFt, double alongFt) = OnPolyline(follower.Position, trailPoints);
            worstOffTrailFt = Math.Max(worstOffTrailFt, offTrailFt);
            pastBarFt = alongFt - barAlongFt;
            output.WriteLine(
                $"+{s}s {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} offTrail={offTrailFt:F1} ft pastBar={pastBarFt:F1} ft"
            );
        }

        Assert.True(worstOffTrailFt <= OnTrailFt, $"{Follower} left its lead's trail by {worstOffTrailFt:F1} ft after CROSS {menuRunway}");
        Assert.IsType<FollowingPhase>(follower.Phases?.CurrentPhase);
        Assert.True(pastBarFt > MinPastBarFt, $"{Follower} is {pastBarFt:F1} ft along its trail past the 15/33 bar {CrossingSeconds}s after CROSS");
    }

    /// <summary>
    /// Loads the recording, restores its snapshot at <see cref="RestoreSecond"/> and arms the recorded actions after it: the engine
    /// and the OAK layout. Null on the repo's silent-skip path.
    /// </summary>
    private (SimulationEngine Engine, AirportGroundLayout Layout)? Start()
    {
        TestVnasData.EnsureInitialized();
        RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        var groundData = RecordingGroundData.Evergreen("FOLLOWG behind a lead released across 15/33 on C at the C/J junction");
        if ((archive is null) || (TestVnasData.NavigationDb is null) || (groundData.GetLayout("OAK") is not { } layout))
        {
            archive?.Dispose();
            return null;
        }

        using (archive)
        {
            SessionRecording recording = archive.ToBaseSessionRecording();
            var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
            engine.Replay(recording, 0);
            TimedSnapshot snapshot = Assert.IsType<TimedSnapshot>(archive.ReadSnapshotAt(RestoreSecond));
            engine.RestoreFromSnapshot(snapshot.State);
            engine.ArmReplay(recording.Actions);
            output.WriteLine($"restored t={snapshot.ElapsedSeconds}");
            return (engine, layout);
        }
    }

    /// <summary>
    /// Replays a second at a time from <see cref="RestoreSecond"/> until the follower holds short, to <see cref="HoldBudgetSecond"/>.
    /// </summary>
    private HoldingShortPhase ReplayUntilFollowerHolds(SimulationEngine engine)
    {
        for (int t = (int)RestoreSecond + 1; t <= HoldBudgetSecond; t++)
        {
            engine.ReplayOneSecond();
            AircraftState follower = Assert.IsType<AircraftState>(engine.FindAircraft(Follower));
            output.WriteLine(
                $"t={t} {Follower} {follower.Phases?.CurrentPhase?.Name} gs={follower.GroundSpeed:F1} "
                    + $"| {Lead} {engine.FindAircraft(Lead)?.Phases?.CurrentPhase?.Name}"
            );
            if (follower.Phases?.CurrentPhase is HoldingShortPhase hold)
            {
                return hold;
            }
        }

        Assert.Fail($"{Follower} never held short by t={HoldBudgetSecond}");
        return null!;
    }

    /// <summary>
    /// <paramref name="p"/>'s flat-earth distance (ft) from the polyline through <paramref name="points"/>, and how far (ft) along
    /// the polyline from its start its nearest point lies.
    /// </summary>
    private static (double OffFt, double AlongFt) OnPolyline(LatLon p, List<LatLon> points)
    {
        double bestOffFt = double.MaxValue;
        double bestAlongFt = 0;
        double startFt = 0;
        for (int i = 1; i < points.Count; i++)
        {
            (double offFt, double intoFt, double lengthFt) = OnSegment(p, points[i - 1], points[i]);
            if (offFt < bestOffFt)
            {
                bestOffFt = offFt;
                bestAlongFt = startFt + intoFt;
            }

            startFt += lengthFt;
        }

        return (bestOffFt, bestAlongFt);
    }

    /// <summary>
    /// <paramref name="p"/>'s flat-earth distance (ft) from the segment <paramref name="a"/>–<paramref name="b"/>, how far (ft) from
    /// <paramref name="a"/> its nearest point lies, and the segment's length (ft).
    /// </summary>
    private static (double OffFt, double IntoFt, double LengthFt) OnSegment(LatLon p, LatLon a, LatLon b)
    {
        double ftPerDegLat = 60.0 * GeoMath.FeetPerNm;
        double ftPerDegLon = ftPerDegLat * Math.Cos(a.Lat * Math.PI / 180.0);
        double bx = (b.Lon - a.Lon) * ftPerDegLon;
        double by = (b.Lat - a.Lat) * ftPerDegLat;
        double px = (p.Lon - a.Lon) * ftPerDegLon;
        double py = (p.Lat - a.Lat) * ftPerDegLat;
        double lengthSq = (bx * bx) + (by * by);
        double along = lengthSq > 0 ? Math.Clamp(((px * bx) + (py * by)) / lengthSq, 0.0, 1.0) : 0.0;
        double dx = px - (along * bx);
        double dy = py - (along * by);
        double lengthFt = Math.Sqrt(lengthSq);
        return (Math.Sqrt((dx * dx) + (dy * dy)), along * lengthFt, lengthFt);
    }
}
