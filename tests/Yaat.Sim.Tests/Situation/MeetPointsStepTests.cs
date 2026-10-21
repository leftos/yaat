using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Simulation.GroundTaxi;

namespace Yaat.Sim.Tests.Situation;

/// <summary>
/// The predicted meeting points the <c>Situation</c> step stores (<see cref="Yaat.Sim.Situation.AircraftSituationState.MeetPoints"/>):
/// each on-ground aircraft with a driven route carries one row per other aircraft whose route shares a junction with its own
/// within the 1,500 ft look-ahead, at that junction; a parked aircraft carries none and is no one's partner; an aircraft at
/// another airport is no one's partner; the rows survive a snapshot round-trip.
///
/// <para>The KOAK H2 ground scene: N738SP taxis from GA16 via F, C, B to runway 28R while N52417, parked on K until
/// t=36, then taxis north on K across F, so the two routes meet at the F/K/L junction until N52417 has crossed it.</para>
/// </summary>
public class MeetPointsStepTests(ITestOutputHelper output)
{
    private const int LeadTaxiSecond = 36;
    private const int BudgetSeconds = 300;

    /// <summary>How far (ft) each aircraft is set back behind its route's start node in the far-apart test.</summary>
    private const double SetBackFt = 3000.0;

    private static readonly (int Second, string Callsign, string Command)[] Script =
    [
        (1, KoakFollowClip.Lead, "CAINH"),
        (1, KoakFollowClip.Follower, "CAINH"),
        (2, KoakFollowClip.Follower, "TAXI F C B CROSS 33 RWY 28R"),
        (LeadTaxiSecond, KoakFollowClip.Lead, "TAXI K D @GA7 CROSS 33"),
    ];

    [Fact]
    public void TaxiingPair_EachCarriesARowForTheOther_AtTheSharedJunction_OnlyWhileTheRoutesMeet()
    {
        if (KoakFollowClip.Load(output, KoakFollowClip.H2Scenario) is not { } engine)
        {
            return;
        }

        AircraftState follower = engine.FindAircraft(KoakFollowClip.Follower)!;
        AircraftState lead = engine.FindAircraft(KoakFollowClip.Lead)!;
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(follower.Ground.Layout);
        int parkedLeadSeconds = 0;
        int metAt = -1;
        int junctionId = -1;
        int endedAt = KoakFollowClip.RunScript(
            engine,
            output,
            Script,
            BudgetSeconds,
            second =>
            {
                GroundMeetPointDto? row = RowFor(follower, KoakFollowClip.Lead);
                output.WriteLine($"t={second} follower rows=[{Describe(follower)}] lead rows=[{Describe(lead)}]");
                if (FollowingPhase.DrivenRouteOf(lead) is null)
                {
                    Assert.Null(lead.Situation.MeetPoints);
                    Assert.Null(row);
                    parkedLeadSeconds += FollowingPhase.DrivenRouteOf(follower) is null ? 0 : 1;
                    return false;
                }

                if (row is not null)
                {
                    GroundNode junction = layout.Nodes[row.NodeId];
                    Assert.True(
                        junction.Edges.Any(e => e.TaxiwayName == "F") && junction.Edges.Any(e => e.TaxiwayName == "K"),
                        $"t={second}: the meet point is node {row.NodeId}, not the F/K junction"
                    );
                    Assert.Equal(junction.Position, row.Position);
                    GroundMeetPointDto mirror = Assert.IsType<GroundMeetPointDto>(RowFor(lead, KoakFollowClip.Follower));
                    Assert.Equal(row.NodeId, mirror.NodeId);
                    metAt = metAt < 0 ? second : metAt;
                    junctionId = row.NodeId;
                    return false;
                }

                // The routes no longer meet: after the junction, while both aircraft still drive a route.
                return (metAt > 0) && (FollowingPhase.DrivenRouteOf(follower) is not null);
            }
        );

        Assert.True(parkedLeadSeconds > 0, "N738SP never had a route while N52417 stood parked");
        Assert.True(metAt > 0, "N738SP never carried a meet point for N52417");
        Assert.True(endedAt > metAt, $"the meet point for N52417 never went away after t={metAt} with both routes still driven");
        // The meeting ends because one of the two has passed the junction: with no GIVEWAY the conflict detector picks who goes.
        bool followerPassed = Assert.IsType<TaxiRoute>(FollowingPhase.DrivenRouteOf(follower)).SegmentIndexAhead(junctionId) == int.MaxValue;
        bool leadPassed = Assert.IsType<TaxiRoute>(FollowingPhase.DrivenRouteOf(lead)).SegmentIndexAhead(junctionId) == int.MaxValue;
        output.WriteLine($"t={endedAt}: past the junction (node {junctionId}): N738SP={followerPassed} N52417={leadPassed}");
        Assert.True(followerPassed || leadPassed, $"t={endedAt}: the meeting ended with the F/K junction (node {junctionId}) ahead of both");
    }

    /// <summary>
    /// The look-ahead counts from each route's current segment's from-node, which a fresh taxi route can place well ahead of the
    /// aircraft: set back <see cref="SetBackFt"/> behind those nodes, more than 3,000 ft apart, the two still meet.
    /// </summary>
    [Fact]
    public void PairFarApart_BehindTheirRoutesStartNodes_StillMeets()
    {
        if (StartMeeting() is not var (engine, follower, lead))
        {
            return;
        }

        follower.Position = SetBack(follower);
        lead.Position = SetBack(lead);
        double apartFt = GeoMath.DistanceNm(follower.Position, lead.Position) * GeoMath.FeetPerNm;
        Assert.True(apartFt > 3000.0, $"the two aircraft are only {apartFt:F0} ft apart");

        engine.TickSituation();

        Assert.NotNull(RowFor(follower, KoakFollowClip.Lead));
        Assert.NotNull(RowFor(lead, KoakFollowClip.Follower));
    }

    /// <summary>
    /// Node ids are per layout: with N52417's layout another airport's (KSFO's), its route's node ids still coinciding with
    /// N738SP's, neither carries a row for the other.
    /// </summary>
    [Fact]
    public void PairOnDifferentAirportsLayouts_DoesNotMeet()
    {
        if (StartMeeting() is not var (engine, follower, lead))
        {
            return;
        }

        lead.Ground.Layout = new TestAirportGroundData().GetLayout("SFO") ?? throw new InvalidOperationException("KSFO layout missing from TestData");

        engine.TickSituation();

        Assert.Null(RowFor(follower, KoakFollowClip.Lead));
        Assert.Null(RowFor(lead, KoakFollowClip.Follower));
    }

    [Fact]
    public void Snapshot_RoundTripsTheMeetPoints()
    {
        var ac = new AircraftState
        {
            Callsign = "N738SP",
            AircraftType = "C172",
            IsOnGround = true,
        };
        GroundMeetPointDto[] rows = [new("N52417", 352, new LatLon(37.7306, -122.2161)), new("N12345", 518, new LatLon(37.7290, -122.2190))];
        ac.Situation.MeetPoints = rows;

        string json = JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default);
        AircraftSnapshotDto dto = JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default)!;
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Equal(rows, Assert.IsAssignableFrom<IReadOnlyList<GroundMeetPointDto>>(restored.Situation.MeetPoints));
    }

    [Fact]
    public void Snapshot_WithoutMeetPoints_WritesNoFieldAndRestoresNull()
    {
        var ac = new AircraftState
        {
            Callsign = "N738SP",
            AircraftType = "C172",
            IsOnGround = true,
        };

        string json = JsonSerializer.Serialize(ac.ToSnapshot(), RecordingJsonOptions.Default);
        AircraftSnapshotDto dto = JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default)!;

        Assert.DoesNotContain("MeetPoints", json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(AircraftState.FromSnapshot(dto, null).Situation.MeetPoints);
    }

    /// <summary>The H2 clip run to the first second N738SP carries a row for N52417; null when the test data is unavailable.</summary>
    private (SimulationEngine Engine, AircraftState Follower, AircraftState Lead)? StartMeeting()
    {
        if (KoakFollowClip.Load(output, KoakFollowClip.H2Scenario) is not { } engine)
        {
            return null;
        }

        AircraftState follower = engine.FindAircraft(KoakFollowClip.Follower)!;
        AircraftState lead = engine.FindAircraft(KoakFollowClip.Lead)!;
        int metAt = KoakFollowClip.RunScript(engine, output, Script, BudgetSeconds, _ => RowFor(follower, KoakFollowClip.Lead) is not null);
        Assert.True(metAt > 0, "N738SP never carried a meet point for N52417");
        return (engine, follower, lead);
    }

    /// <summary>
    /// <see cref="SetBackFt"/> behind the from-node of the aircraft's current route segment, along the segment's line: mid-edge
    /// behind the node its route's look-ahead counts from.
    /// </summary>
    private static LatLon SetBack(AircraftState ac)
    {
        TaxiRouteSegment segment = Assert.IsType<TaxiRouteSegment>(FollowingPhase.DrivenRouteOf(ac)?.CurrentSegment);
        LatLon from = segment.Edge.FromNode.Position;
        var back = new TrueHeading(GeoMath.BearingTo(segment.Edge.ToNode.Position, from));
        return GeoMath.ProjectPoint(from, back, SetBackFt / GeoMath.FeetPerNm);
    }

    private static GroundMeetPointDto? RowFor(AircraftState ac, string callsign) =>
        ac.Situation.MeetPoints?.SingleOrDefault(row => row.Callsign == callsign);

    private static string Describe(AircraftState ac) =>
        string.Join(", ", (ac.Situation.MeetPoints ?? []).Select(row => $"{row.Callsign}@{row.NodeId}"));
}
