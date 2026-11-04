using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// YAAT-1781: an airborne FOLLOW given on an extended upwind turns crosswind at once and cuts in front of a lead already
/// on the crosswind.
///
/// Recording: S2-OAK-3 (2) "VFR Sequencing" (ZOA), KOAK 28R right traffic, trimmed to snapshots t=0/1040/1055/1060.
/// N152SP (C152) departed 28R, was told <c>EXT</c> on the upwind (t=919) and <c>TC</c> at t=1042, turning crosswind
/// 2.7 nm past the departure end. N172SP (C172) behind it on the same upwind, extended by <c>EXT UPWIND</c> (t=940),
/// reported the traffic in sight (t=1059) and was told <c>FOLLOW</c> (t=1065) 2.1 nm past the departure end.
///
/// Observed: the FOLLOW cleared N172SP's upwind <c>IsExtended</c>, and the remaining-path leg hold let it turn at once:
/// <see cref="AirborneFollowHelper.RemainingPatternPathNm"/> measures a crosswind as if it were flown at the departure end,
/// so the lead's 2.9 nm of upwind extension dropped out of its remaining path (4.0 nm read against 7.0 nm flown) and the
/// follower (6.5 nm) read as already a full spacing behind. It turned crosswind 0.6 nm inside the lead's turn, then held
/// that crosswind (its own extension dropped out the same way) straight through the lead's downwind, passing 0.08 nm from
/// it at the same altitude.
///
/// Expected: the follower flies the lead's flown path: it keeps the upwind at least to where the lead turned crosswind,
/// never closes inside half a mile of the lead, and joins the downwind behind it.
///
/// This pins this replay. "Follow" means keeping in-trail spacing behind the lead (AIM 4-4-14.a.2, 4-3-5), not turning
/// at the leader's turn point; the turn-point check holds here because this case meets both readings.
/// </summary>
public class FollowExtendedUpwindLeadOnCrosswindTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/yaat1781-oak-follow-extended-upwind-recording.yaat-bug-report-bundle.zip";
    private const string Follower = "N172SP";
    private const string Lead = "N152SP";
    private const int FollowAt = 1065;
    private const int RecordingEnd = 1112;

    /// <summary>
    /// The closest the follower may come to its lead after the FOLLOW, and the least it may join the downwind behind it:
    /// half the 1.0 nm piston in-trail spacing (<see cref="AirborneFollowHelper.DesiredDistanceForLeader"/>), so a follower
    /// that settles at the spacing passes with margin and the cut-in (0.08 nm) does not.
    /// </summary>
    private const double MinGapNm = 0.5;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = RecordingGroundData.Evergreen("airborne FOLLOW on an OAK 28R pattern");
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("UpwindPhase", LogLevel.Debug)
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData, PushPlanStore.CreateShipped());
    }

    [Fact]
    public void FollowOnExtendedUpwind_FliesLeadsPath_AndJoinsDownwindBehindIt()
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            SessionRecording recording = archive.ToBaseSessionRecording();
            SimulationEngine? engine = BuildEngine();
            if (engine is null)
            {
                return;
            }

            // Hybrid replay from t=1040: both aircraft on the 28R upwind, before the lead's TC (t=1042), so the lead's own
            // crosswind turn point is measured in this run rather than read back from the recording.
            engine.Replay(recording, 0);
            TimedSnapshot? snapshot = archive.ReadSnapshotAt(1040);
            Assert.NotNull(snapshot);
            engine.RestoreFromSnapshot(snapshot.State);
            int snapshotTime = (int)snapshot.ElapsedSeconds;

            AircraftState? preFollower = engine.FindAircraft(Follower);
            AircraftState? preLead = engine.FindAircraft(Lead);
            Assert.NotNull(preFollower);
            Assert.NotNull(preLead);
            UpwindPhase upwind = Assert.IsType<UpwindPhase>(preFollower.Phases?.CurrentPhase);
            Assert.IsType<UpwindPhase>(preLead.Phases?.CurrentPhase);
            Assert.True(upwind.IsExtended, "precondition: N172SP is on an extended upwind (EXT UPWIND at t=940)");
            PatternWaypoints wp = Assert.IsType<PatternWaypoints>(upwind.Waypoints);

            var der = new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon);
            var threshold = new LatLon(wp.ThresholdLat, wp.ThresholdLon);
            double PastDerNm(AircraftState ac) => GeoMath.AlongTrackDistanceNm(ac.Position, der, wp.UpwindHeading);
            double AlongDownwindNm(AircraftState ac) => GeoMath.AlongTrackDistanceNm(ac.Position, threshold, wp.DownwindHeading);

            double? leadTurnPastDerNm = null;
            double? followerTurnPastDerNm = null;
            int? followerTurnAt = null;
            double? gapAtFollowerDownwindNm = null;
            double closestNm = double.PositiveInfinity;
            int closestAt = 0;

            // Replay the recorded TC, RTIS and FOLLOW (t=1042/1059/1065), then physics only: the recording ends at t=1112
            // and holds no later command for either aircraft.
            for (int t = snapshotTime + 1; t <= snapshotTime + 300; t++)
            {
                if (t <= RecordingEnd)
                {
                    engine.ReplayOneSecond();
                }
                else
                {
                    engine.TickOneSecond();
                }

                AircraftState? follower = engine.FindAircraft(Follower);
                AircraftState? lead = engine.FindAircraft(Lead);
                Assert.NotNull(follower);
                Assert.NotNull(lead);

                if ((leadTurnPastDerNm is null) && lead.Phases?.CurrentPhase is not UpwindPhase)
                {
                    leadTurnPastDerNm = PastDerNm(lead);
                    output.WriteLine($"t={t}: {Lead} left the upwind {leadTurnPastDerNm:F2} nm past the DER");
                }

                if (t >= FollowAt)
                {
                    double gapNm = GeoMath.DistanceNm(follower.Position, lead.Position);
                    if (gapNm < closestNm)
                    {
                        closestNm = gapNm;
                        closestAt = t;
                    }
                }

                if ((followerTurnAt is null) && follower.Phases?.CurrentPhase is not UpwindPhase)
                {
                    followerTurnAt = t;
                    followerTurnPastDerNm = PastDerNm(follower);
                    output.WriteLine($"t={t}: {Follower} left the upwind {followerTurnPastDerNm:F2} nm past the DER");
                }

                if (follower.Phases?.CurrentPhase is DownwindPhase)
                {
                    gapAtFollowerDownwindNm = AlongDownwindNm(lead) - AlongDownwindNm(follower);
                    output.WriteLine(
                        $"t={t}: {Follower} on the downwind; {Lead} on {lead.Phases?.CurrentPhase?.Name} "
                            + $"{gapAtFollowerDownwindNm:F2} nm ahead along the downwind"
                    );
                    break;
                }
            }

            output.WriteLine($"closest approach after the FOLLOW: {closestNm:F2} nm at t={closestAt}");

            Assert.NotNull(leadTurnPastDerNm);
            Assert.NotNull(followerTurnAt);
            Assert.True(followerTurnAt > FollowAt, $"{Follower} left the upwind at t={followerTurnAt}, before the FOLLOW");

            // The lead's flown path: the follower keeps the upwind at least to where the lead turned crosswind.
            Assert.True(
                followerTurnPastDerNm >= leadTurnPastDerNm - 0.1,
                $"CUT-IN: {Follower} turned crosswind {followerTurnPastDerNm:F2} nm past the DER at t={followerTurnAt}, "
                    + $"inside {Lead}'s crosswind turn {leadTurnPastDerNm:F2} nm past it"
            );

            Assert.True(closestNm >= MinGapNm, $"{Follower} closed to {closestNm:F2} nm of {Lead} at t={closestAt}, inside {MinGapNm:F1} nm");

            Assert.True(gapAtFollowerDownwindNm is not null, $"{Follower} never reached the downwind within 300 s");
            Assert.True(
                gapAtFollowerDownwindNm >= MinGapNm,
                $"{Follower} joined the downwind {gapAtFollowerDownwindNm:F2} nm behind {Lead}, under {MinGapNm:F1} nm"
            );
        }
    }

    /// <summary>
    /// The remaining-path metric along one aircraft's path round a crosswind flown past the departure end (an extended
    /// upwind): it must fall as the aircraft flies, by the distance flown, across the upwind-to-crosswind and
    /// crosswind-to-downwind leg changes. A crosswind measured as if at the departure end reads the extension short, so the
    /// metric drops by the extension at the crosswind turn and climbs back by it at the downwind turn.
    /// </summary>
    [Fact]
    public void RemainingPatternPath_CrosswindPastTheDepartureEnd_CountsTheExtension()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        Assert.NotNull(navDb);
        RunwayInfo? rwy = navDb.GetRunway("KOAK", "28R");
        Assert.NotNull(rwy);
        PatternWaypoints wp = PatternGeometry.Compute(
            new PatternGeometryRequest(rwy, AircraftCategory.Piston, "", 0, PatternDirection.Right)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = navDb.GetRunways("KOAK"),
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );

        const double ExtensionNm = 2.0;
        const double CrosswindFlownNm = 0.3;
        var threshold = new LatLon(wp.ThresholdLat, wp.ThresholdLon);
        double width = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(new LatLon(wp.DownwindStartLat, wp.DownwindStartLon), threshold, wp.DownwindHeading)
        );
        LatLon turn = GeoMath.ProjectPoint(new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon), wp.UpwindHeading, ExtensionNm);
        LatLon onCrosswind = GeoMath.ProjectPoint(turn, wp.CrosswindHeading, CrosswindFlownNm);
        LatLon downwindTurn = GeoMath.ProjectPoint(turn, wp.CrosswindHeading, width);

        double upwindNm = AirborneFollowHelper.RemainingPatternPathNm(OnLeg(turn, new UpwindPhase { Waypoints = wp }, rwy), wp);
        double crosswindNm = AirborneFollowHelper.RemainingPatternPathNm(OnLeg(onCrosswind, new CrosswindPhase { Waypoints = wp }, rwy), wp);
        double downwindNm = AirborneFollowHelper.RemainingPatternPathNm(OnLeg(downwindTurn, new DownwindPhase { Waypoints = wp }, rwy), wp);
        output.WriteLine($"width={width:F2} upwind@turn={upwindNm:F2} crosswind+{CrosswindFlownNm}={crosswindNm:F2} downwind@turn={downwindNm:F2}");

        Assert.Equal(CrosswindFlownNm, upwindNm - crosswindNm, 0.05);
        Assert.Equal(width - CrosswindFlownNm, crosswindNm - downwindNm, 0.05);
    }

    private static AircraftState OnLeg(LatLon position, Phase leg, RunwayInfo rwy)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST",
            AircraftType = "C172",
            Position = position,
            TrueHeading = new TrueHeading(0),
            TrueTrack = new TrueHeading(0),
            Altitude = 1000,
            IndicatedAirspeed = 80,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", FlightRules = "VFR" },
            Approach = new AircraftApproachState(),
            Phases = new PhaseList { AssignedRunway = rwy },
        };
        ac.Phases.Add(leg); // CurrentIndex 0, so CurrentPhase is the leg without Start()
        return ac;
    }
}
