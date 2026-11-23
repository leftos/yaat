using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using static Yaat.Sim.Tests.Helpers.FollowTestHelpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// YAAT-2782. Recording: S2-OAK-3 (ZOA), seed 2052219157, trimmed to t=0, 625 and 1580. N428KK flies DCT VPCOL; ERD 28R;
/// N152SP, on the crosswind, is told to FOLLOW it at t=1583 while 0.93 nm ahead of it along its track. The follower used to
/// start a spacing excursion on that negative gap ("S-turning for spacing") that steered it across the lead's track toward the
/// downwind, then join the lead's downwind ahead of it, turn base first and land number one. Expected: no excursion while
/// ahead, a pursuit back to the lead's trail, the lead turning base first with the follow on, the follower still following
/// when it turns base, and the follower reaching final after the lead.
/// </summary>
public class FollowAheadOfEntrantLeadTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak3-follow-recording.zip";
    private const string Follower = "N152SP";
    private const string Lead = "N428KK";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = RecordingGroundData.Evergreen("FOLLOW from ahead of a lead entering the pattern on today's OAK");
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("VfrFollowPhase", LogLevel.Debug)
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData, PushPlanStore.CreateShipped());
    }

    [Fact]
    public void CrosswindFollower_AheadOfDirectToEntrantLead_FallsInBehindAndTurnsBaseSecond()
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

            engine.Replay(recording, 0);
            TimedSnapshot? snapshot = archive.ReadSnapshotAt(1580);
            Assert.NotNull(snapshot);
            engine.RestoreFromSnapshot(snapshot.State);
            Assert.Equal(1580, (int)snapshot.ElapsedSeconds);

            var watch = new SequenceWatch();
            var fallInCallsAt = new SortedSet<int>();
            int now = 1580;
            engine.WarningEmitted += (callsign, warning) =>
            {
                if ((callsign == Follower) && warning.Contains(FallInCall, StringComparison.Ordinal))
                {
                    fallInCallsAt.Add(now);
                }
            };
            engine.PilotSpeechEmitted += (callsign, speech) =>
            {
                if ((callsign == Follower) && speech.Tts.Contains(FallInCall, StringComparison.Ordinal))
                {
                    fallInCallsAt.Add(now);
                }
            };
            for (int t = 1581; t <= 1900; t++)
            {
                now = t;
                if (t <= 1598)
                {
                    engine.ReplayOneSecond();
                }
                else
                {
                    engine.TickOneSecond();
                }

                AircraftState follower = Assert.IsType<AircraftState>(engine.FindAircraft(Follower));
                AircraftState lead = Assert.IsType<AircraftState>(engine.FindAircraft(Lead));
                watch.Observe(t, follower, lead, output);
            }

            Assert.True(watch.WidenWhileAhead.Count == 0, $"spacing excursion while ahead of the lead: {string.Join("; ", watch.WidenWhileAhead)}");
            // Measured: told to follow on the crosswind, tracking 022 while the lead tracks about 134, it turns 111° to hold the
            // lead's track (already beyond its offset line on the side it is on), so it advises the controller, once.
            output.WriteLine($"largest turn while ahead {watch.MaxTurnWhileAheadDeg:F1}°");
            Assert.True(watch.MaxTurnWhileAheadDeg > 90.0, $"largest turn while ahead {watch.MaxTurnWhileAheadDeg:F1}°");
            Assert.True(fallInCallsAt.Count == 1, $"fall-in calls at t={string.Join(", ", fallInCallsAt)}");
            int leadBaseAt = Assert.IsType<int>(watch.LeadBaseAt);
            Assert.True(watch.FollowOnAtLeadBase, $"{Follower} no longer followed {Lead} when the lead turned base at t={leadBaseAt}");
            int followerBaseAt = Assert.IsType<int>(watch.FollowerBaseAt);
            Assert.True(followerBaseAt > leadBaseAt, $"{Follower} turned base at t={followerBaseAt}, the lead at t={leadBaseAt}");
            int leadFinalAt = Assert.IsType<int>(watch.LeadFinalAt);
            int followerFinalAt = Assert.IsType<int>(watch.FollowerFinalAt);
            Assert.True(followerFinalAt > leadFinalAt, $"{Follower} reached final at t={followerFinalAt}, the lead at t={leadFinalAt}");
            Assert.True(watch.FollowOnAtFollowerBase, $"{Follower} no longer followed {Lead} when it turned base at t={watch.FollowerBaseAt}");
            Assert.False(watch.CrossFlippedWhileAhead, $"{Follower} crossed {Lead}'s track while ahead of it");
        }
    }

    [Fact]
    public void CrosswindFollower_RestoredMidFallIn_MakesNoSecondCallAndSteersAlike()
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

            engine.Replay(recording, 0);
            TimedSnapshot? snapshot = archive.ReadSnapshotAt(1580);
            Assert.NotNull(snapshot);
            engine.RestoreFromSnapshot(snapshot.State);
            for (int t = 1581; t <= 1600; t++)
            {
                if (t <= 1598)
                {
                    engine.ReplayOneSecond();
                }
                else
                {
                    engine.TickOneSecond();
                }
            }

            // Mid-encounter: the call was made at t=1584 and the side is latched.
            var mid = (VfrFollowPhaseDto)Assert.IsType<VfrFollowPhase>(FollowerIn(engine).Phases!.CurrentPhase).ToSnapshot();
            Assert.True(mid.FallInCalled);
            Assert.NotNull(mid.FallInSide);

            string json = JsonSerializer.Serialize(engine.CaptureSnapshot(), RecordingJsonOptions.Default);
            SimulationEngine restored = Assert.IsType<SimulationEngine>(BuildEngine());
            restored.Replay(recording, 0);
            restored.RestoreFromSnapshot(
                Assert.IsType<StateSnapshotDto>(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default))
            );
            int calls = 0;
            restored.WarningEmitted += (callsign, warning) =>
                calls += (callsign == Follower) && warning.Contains(FallInCall, StringComparison.Ordinal) ? 1 : 0;
            restored.PilotSpeechEmitted += (callsign, speech) =>
                calls += (callsign == Follower) && speech.Tts.Contains(FallInCall, StringComparison.Ordinal) ? 1 : 0;

            for (int t = 1601; t <= 1640; t++)
            {
                engine.TickOneSecond();
                restored.TickOneSecond();
                AircraftState continued = FollowerIn(engine);
                AircraftState resumed = FollowerIn(restored);
                Assert.True(
                    continued.Targets.TargetTrueHeading?.Degrees == resumed.Targets.TargetTrueHeading?.Degrees,
                    $"t={t}: steer {continued.Targets.TargetTrueHeading?.Degrees:F2} continued, "
                        + $"{resumed.Targets.TargetTrueHeading?.Degrees:F2} restored"
                );
                Assert.True(GeoMath.DistanceNm(continued.Position, resumed.Position) < 1e-9, $"t={t}: the restored follower flew elsewhere");
            }

            Assert.Equal(0, calls);
        }
    }

    private static AircraftState FollowerIn(SimulationEngine engine) => Assert.IsType<AircraftState>(engine.FindAircraft(Follower));

    // ─── The case table ───

    private const string RowLeader = "LEAD1";
    private const string RowFollower = "FOLL1";

    /// <summary>A slow follower's excursion limits: a 1 nm offset cap, turning up to 45° off the lead's track.</summary>
    private static FollowExcursionLimits PistonLimits => new(1.0, AirborneFollowHelper.TrailWideExcursionDeg);

    private static FreePursuitSpacing NoCircuitSpacing(FollowExtendedLeg? extendedLeg) => new(1.0, PistonLimits, null, extendedLeg, null);

    /// <summary>A northbound lead well clear of OAK, with 2 nm of straight path recorded behind it.</summary>
    private static (AircraftState Lead, LeadPathTrail Path) NorthboundLead()
    {
        TestVnasData.EnsureInitialized();
        var north = new TrueHeading(0);
        LatLon leadPosition = OffFinal(Oak("28R"), 12.0, 8.0);
        var path = new LeadPathTrail();
        for (int i = 8; i >= 1; i--)
        {
            path.Record(GeoMath.ProjectPoint(leadPosition, north.ToReciprocal(), i * 0.25));
        }
        path.Record(leadPosition);
        return (MakeVfr(RowLeader, leadPosition, north, 3000), path);
    }

    /// <summary>A northbound follower <paramref name="aheadNm"/> along the lead's track from it and 0.3 nm to its right.</summary>
    private static AircraftState FollowerAlongLead(AircraftState lead, double aheadNm)
    {
        LatLon along = GeoMath.ProjectPoint(lead.Position, aheadNm >= 0 ? lead.TrueTrack : lead.TrueTrack.ToReciprocal(), Math.Abs(aheadNm));
        return MakeVfr(RowFollower, GeoMath.ProjectPoint(along, lead.TrueTrack + 90.0, 0.3), lead.TrueTrack, 3000);
    }

    [Fact]
    public void Excursion_NegativeGapNoExtendedLeg_StartsNoExcursion()
    {
        (AircraftState lead, LeadPathTrail path) = NorthboundLead();
        AircraftState follower = FollowerAlongLead(lead, 0.93);
        var widen = new FollowWidenState();

        TrueHeading heading = AirborneFollowHelper.ComputeFreePursuitHeading(follower, lead, NoCircuitSpacing(null), path, widen);

        // No excursion: it turns out right, away from the lead's track it sits 0.3 nm right of (the owner's YAAT-2782 ruling),
        // where it used to steer for the point behind the lead the short way, a left turn across the lead's track.
        Assert.False(widen.Active);
        AssertTurnsOut(follower, lead, heading, 1);
    }

    [Fact]
    public void Excursion_ZeroGap_StartsNoExcursion()
    {
        (AircraftState lead, _) = NorthboundLead();
        AircraftState follower = MakeVfr(RowFollower, lead.Position, lead.TrueTrack, 3000);
        var widen = new FollowWidenState();

        AirborneFollowHelper.ComputeFreePursuitHeading(follower, lead, NoCircuitSpacing(null), new LeadPathTrail(), widen);

        Assert.False(widen.Active);
    }

    [Fact]
    public void Excursion_PositiveGapBelowDesired_StartsExcursion()
    {
        (AircraftState lead, LeadPathTrail path) = NorthboundLead();
        AircraftState follower = FollowerAlongLead(lead, -0.5);
        var widen = new FollowWidenState();

        TrueHeading heading = AirborneFollowHelper.ComputeFreePursuitHeading(follower, lead, NoCircuitSpacing(null), path, widen);

        Assert.True(widen.Active);
        Assert.True(heading.AbsAngleTo(lead.TrueTrack) > 1.0, $"excursion heading {heading.Degrees:F1}");
    }

    [Fact]
    public void Excursion_ExtendedLegNegativeGap_Unchanged()
    {
        (AircraftState lead, LeadPathTrail path) = NorthboundLead();
        AircraftState follower = FollowerAlongLead(lead, 0.93);
        var widen = new FollowWidenState();

        TrueHeading heading = AirborneFollowHelper.ComputeFreePursuitHeading(
            follower,
            lead,
            NoCircuitSpacing(new FollowExtendedLeg(lead.Position, lead.TrueTrack, -0.5)),
            path,
            widen
        );

        Assert.True(widen.Active);
        Assert.Equal(1, widen.Side);
        Assert.True(
            Math.Abs(lead.TrueTrack.SignedAngleTo(heading) - AirborneFollowHelper.TrailWideExcursionDeg) < 0.01,
            $"heading {heading.Degrees:F1}"
        );
    }

    // ─── Falling in behind a lead the follower is ahead of (YAAT-2782 owner ruling) ───

    private const string FallInCall = "turning out to fall in behind";

    /// <summary>
    /// Asserts the fall-in steer: perpendicular off the lead's track to <paramref name="side"/> (+1 right, -1 left), toward
    /// the offset line, turning that way.
    /// </summary>
    private static void AssertTurnsOut(AircraftState follower, AircraftState lead, TrueHeading heading, int side)
    {
        double offTrackDeg = lead.TrueTrack.SignedAngleTo(heading);
        Assert.True(Math.Abs(offTrackDeg - (90.0 * side)) < 1.0, $"heading {heading.Degrees:F1}, {offTrackDeg:F1}° off the lead's track");
        Assert.Equal(side > 0 ? TurnDirection.Right : TurnDirection.Left, follower.Targets.PreferredTurnDirection);
    }

    /// <summary>
    /// A follower <paramref name="aheadNm"/> along the northbound lead's track from it, <paramref name="crossNm"/> to its
    /// right, tracking <paramref name="offDeg"/> right of the lead's track.
    /// </summary>
    private static AircraftState FollowerAt(AircraftState lead, double aheadNm, double crossNm, double offDeg)
    {
        LatLon along = GeoMath.ProjectPoint(lead.Position, lead.TrueTrack, aheadNm);
        LatLon position =
            crossNm >= 0 ? GeoMath.ProjectPoint(along, lead.TrueTrack + 90.0, crossNm) : GeoMath.ProjectPoint(along, lead.TrueTrack - 90.0, -crossNm);
        return MakeVfr(RowFollower, position, lead.TrueTrack + offDeg, 3000);
    }

    /// <summary>What a fall-in row's engine run left: the follower, and whether its cross-track to the lead ever changed sign.</summary>
    private sealed record FallInRun(AircraftState Follower, bool CrossFlipped);

    /// <summary>
    /// The northbound free-flight lead and a C172 follower placed by <see cref="FollowerAt"/>, already in free pursuit of it,
    /// in an engine run for <paramref name="seconds"/>, watching the follower's side of the lead's track every sub-tick.
    /// </summary>
    private FallInRun FallInRow(double aheadNm, double crossNm, double offDeg, int seconds)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).EnableCategory("AirborneFollowHelper", LogLevel.Debug).InitializeSimLog();
        var engine = new SimulationEngine(
            RecordingGroundData.Evergreen("FOLLOW from ahead of a free-flight lead, no airport layout involved"),
            PushPlanStore.CreateShipped()
        );
        (AircraftState lead, _) = NorthboundLead();
        engine.World.AddAircraft(lead);
        AircraftState follower = FollowerAt(lead, aheadNm, crossNm, offDeg);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        CommandDispatcher.InstallVfrFollowPhase(follower, RowLeader, patternReturn: null, climbOutGate: null);
        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);

        var side = new CrossSideWatch();
        for (int i = 0; i < seconds * 4; i++)
        {
            engine.TickPhysics(0.25);
            side.Observe(GeoMath.SignedCrossTrackDistanceNm(follower.Position, lead.Position, lead.TrueTrack));
        }

        output.WriteLine(string.Join(Environment.NewLine, follower.PendingWarnings));
        return new FallInRun(follower, side.Flipped);
    }

    /// <summary>Whether a cross-track (nm) ever changes sign from the first one clear of the track.</summary>
    private sealed class CrossSideWatch
    {
        private const double OnTrackNm = 1e-3;
        private int _sign;

        public bool Flipped { get; private set; }

        public void Observe(double crossNm)
        {
            if (Math.Abs(crossNm) <= OnTrackNm)
            {
                return;
            }

            int sign = Math.Sign(crossNm);
            _sign = _sign == 0 ? sign : _sign;
            Flipped |= sign != _sign;
        }
    }

    private static int FallInCalls(AircraftState follower) => follower.PendingWarnings.Count(w => w.Contains(FallInCall, StringComparison.Ordinal));

    private static double FloorKts(AircraftState follower) => AircraftPerformance.ApproachSpeed(follower.AircraftType, AircraftCategory.Piston);

    [Fact]
    public void FallIn_AheadAndRightOfTrack_TurnsRightAway()
    {
        (AircraftState lead, LeadPathTrail path) = NorthboundLead();
        AircraftState follower = FollowerAt(lead, 0.93, 0.3, 0.0);
        var widen = new FollowWidenState();

        TrueHeading heading = AirborneFollowHelper.ComputeFreePursuitHeading(follower, lead, NoCircuitSpacing(null), path, widen);

        Assert.False(widen.Active);
        Assert.Equal(1, widen.FallInSide);
        AssertTurnsOut(follower, lead, heading, 1);

        // A plain turn-out perpendicular to the lead's track (90°) is silent: the call is for a turn of more than 90°.
        Assert.Equal(FloorKts(follower), FallInRow(0.93, 0.3, 0.0, 1).Follower.Targets.TargetSpeed);
        FallInRun run = FallInRow(0.93, 0.3, 0.0, 15);
        Assert.Equal(TurnDirection.Right, run.Follower.Targets.PreferredTurnDirection);
        Assert.Equal(0, FallInCalls(run.Follower));
        Assert.False(run.CrossFlipped);
    }

    [Fact]
    public void FallIn_AheadAndLeftOfTrack_TurnsLeftAway()
    {
        (AircraftState lead, LeadPathTrail path) = NorthboundLead();
        AircraftState follower = FollowerAt(lead, 0.93, -0.3, 0.0);
        var widen = new FollowWidenState();

        TrueHeading heading = AirborneFollowHelper.ComputeFreePursuitHeading(follower, lead, NoCircuitSpacing(null), path, widen);

        Assert.Equal(-1, widen.FallInSide);
        AssertTurnsOut(follower, lead, heading, -1);

        Assert.Equal(FloorKts(follower), FallInRow(0.93, -0.3, 0.0, 1).Follower.Targets.TargetSpeed);
        FallInRun run = FallInRow(0.93, -0.3, 0.0, 15);
        Assert.Equal(TurnDirection.Left, run.Follower.Targets.PreferredTurnDirection);
        Assert.Equal(0, FallInCalls(run.Follower));
        Assert.False(run.CrossFlipped);
    }

    [Fact]
    public void FallIn_DeadAheadOnTrack_NoCircuit_TurnsRight()
    {
        (AircraftState lead, LeadPathTrail path) = NorthboundLead();
        AircraftState follower = FollowerAt(lead, 0.93, 0.0, 0.0);
        var widen = new FollowWidenState();

        TrueHeading heading = AirborneFollowHelper.ComputeFreePursuitHeading(follower, lead, NoCircuitSpacing(null), path, widen);

        Assert.Equal(1, widen.FallInSide);
        AssertTurnsOut(follower, lead, heading, 1);
        Assert.False(FallInRow(0.93, 0.0, 0.0, 15).CrossFlipped);
    }

    [Fact]
    public void FallIn_SameHeadingPlusOneDegree_NoCall()
    {
        // Tracking 1° left of the lead's track: the turn out to the right is 91°, inside the call's tolerance.
        FallInRun run = FallInRow(0.93, 0.3, -1.0, 2);

        Assert.Equal(0, FallInCalls(run.Follower));
        Assert.False(run.CrossFlipped);
    }

    [Fact]
    public void FallIn_InLeftCircuit_TurnsToTheOutside()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak("28R");
        TrueHeading downwind = rwy.TrueHeading.ToReciprocal();
        // A left-traffic downwind 1 nm left of the 28R centerline; the follower 0.5 nm ahead of the lead and 0.3 nm left of
        // its track, toward the runway: the side it sits on is the inside, so only the circuit sends it right, to the outside.
        // Within the cap of the lead's track inside a circuit, it crosses the track by design.
        AircraftState lead = MakeVfr(RowLeader, OffFinal(rwy, 0.0, -1.0), downwind, 1000);
        LatLon along = GeoMath.ProjectPoint(lead.Position, downwind, 0.5);
        AircraftState follower = MakeVfr(RowFollower, GeoMath.ProjectPoint(along, downwind - 90.0, 0.3), downwind, 1000);
        var spacing = new FreePursuitSpacing(1.0, PistonLimits, new FollowCircuit(rwy, PatternDirection.Left), null, null);
        var widen = new FollowWidenState();

        TrueHeading heading = AirborneFollowHelper.ComputeFreePursuitHeading(follower, lead, spacing, new LeadPathTrail(), widen);

        Assert.Equal(1, widen.FallInSide);
        AssertTurnsOut(follower, lead, heading, 1);
    }

    [Fact]
    public void FallIn_InsideCircuitBeyondCap_TurnsAwayNotOutside()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak("28R");
        TrueHeading downwind = rwy.TrueHeading.ToReciprocal();
        // A left-traffic downwind 2 nm left of the 28R centerline; the follower 0.5 nm ahead of the lead and 1.34 nm left of
        // its track, on the inside, beyond the 1 nm offset cap, as N152SP was in YAAT-2782: away from the lead's track wins
        // over the circuit's outside, so it turns left, the side it is on.
        AircraftState lead = MakeVfr(RowLeader, OffFinal(rwy, 0.0, -2.0), downwind, 1000);
        LatLon along = GeoMath.ProjectPoint(lead.Position, downwind, 0.5);
        AircraftState follower = MakeVfr(RowFollower, GeoMath.ProjectPoint(along, downwind - 90.0, 1.34), downwind, 1000);
        var spacing = new FreePursuitSpacing(1.0, PistonLimits, new FollowCircuit(rwy, PatternDirection.Left), null, null);
        var widen = new FollowWidenState();

        TrueHeading heading = AirborneFollowHelper.ComputeFreePursuitHeading(follower, lead, spacing, new LeadPathTrail(), widen);

        Assert.Equal(-1, widen.FallInSide);
        // Already beyond its offset line on that side: it holds the lead's track, never steering back toward it.
        Assert.True(widen.FallInEstablished);
        Assert.True(heading.AbsAngleTo(lead.TrueTrack) < 0.5, $"heading {heading.Degrees:F1}");
    }

    [Fact]
    public void FallIn_HeadingBackAcrossLeadsTrack_Calls()
    {
        // 0.3 nm right of the lead's track, tracking 45° back across it (225° off the lead's track): the turn out to the
        // right offset line is the shorter left turn of 135°, which stays on the follower's side of the track, never a forced
        // right turn of 225° through the lead's track; more than 90°, so it advises the controller, once.
        (AircraftState lead, LeadPathTrail path) = NorthboundLead();
        AircraftState follower = FollowerAt(lead, 0.93, 0.3, 225.0);
        var widen = new FollowWidenState();

        TrueHeading heading = AirborneFollowHelper.ComputeFreePursuitHeading(follower, lead, NoCircuitSpacing(null), path, widen);

        Assert.Equal(1, widen.FallInSide);
        Assert.Null(follower.Targets.PreferredTurnDirection);
        double turnDeg = follower.TrueTrack.SignedAngleTo(heading);
        Assert.True(Math.Abs(turnDeg + 135.0) < 1.0, $"turn {turnDeg:F1}° (negative: left)");

        FallInRun run = FallInRow(0.93, 0.3, 225.0, 10);
        Assert.Null(run.Follower.Targets.PreferredTurnDirection);
        Assert.Equal(1, FallInCalls(run.Follower));
        Assert.False(run.CrossFlipped);
    }

    [Fact]
    public void FallIn_EstablishedSmallDrift_NoNewTurnOut()
    {
        (AircraftState lead, LeadPathTrail path) = NorthboundLead();
        var widen = new FollowWidenState();
        // Established at the 1 nm cap, holding the lead's track.
        AircraftState follower = FollowerAt(lead, 0.93, 1.0, 0.0);
        TrueHeading held = AirborneFollowHelper.ComputeFreePursuitHeading(follower, lead, NoCircuitSpacing(null), path, widen);
        Assert.True(widen.FallInEstablished);
        Assert.True(held.AbsAngleTo(lead.TrueTrack) < 0.5, $"heading {held.Degrees:F1}");

        // Drifts 0.05 nm below the 0.1 nm band: a shallow correction back to the line, no new turn-out.
        follower.Position = FollowerAt(lead, 0.9, 0.85, 0.0).Position;
        TrueHeading heading = AirborneFollowHelper.ComputeFreePursuitHeading(follower, lead, NoCircuitSpacing(null), path, widen);

        Assert.True(widen.FallInEstablished);
        Assert.Null(follower.Targets.PreferredTurnDirection);
        double turnDeg = follower.TrueTrack.SignedAngleTo(heading);
        Assert.True((turnDeg > 0.0) && (turnDeg <= 30.0), $"turn {turnDeg:F1}°");
    }

    [Fact]
    public void FallIn_SideTowardAParallelFinal_IsClosed()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak("28R");
        // A lead on the 28R final; the follower 0.5 nm ahead and 0.05 nm left of its track, toward the 28L final: that side
        // is closed, so it turns out right.
        AircraftState lead = MakeVfr(RowLeader, OffFinal(rwy, 5.0, 0.0), rwy.TrueHeading, 1500);
        LatLon along = GeoMath.ProjectPoint(lead.Position, rwy.TrueHeading, 0.5);
        AircraftState follower = MakeVfr(RowFollower, GeoMath.ProjectPoint(along, rwy.TrueHeading - 90.0, 0.05), rwy.TrueHeading, 1500);
        var spacing = new FreePursuitSpacing(1.0, PistonLimits, null, null, rwy);
        var widen = new FollowWidenState();

        TrueHeading heading = AirborneFollowHelper.ComputeFreePursuitHeading(follower, lead, spacing, new LeadPathTrail(), widen);

        Assert.Equal(1, widen.FallInSide);
        AssertTurnsOut(follower, lead, heading, 1);
    }

    [Fact]
    public void FollowCancelledDuringFallIn_ClearsPreferredTurn()
    {
        FallInRun run = FallInRow(0.93, 0.3, 0.0, 1);
        Assert.Equal(TurnDirection.Right, run.Follower.Targets.PreferredTurnDirection);
        VfrFollowPhase phase = Assert.IsType<VfrFollowPhase>(run.Follower.Phases!.CurrentPhase);

        phase.OnEnd(CommandDispatcher.BuildMinimalContext(run.Follower, ActiveRunways.Empty), PhaseStatus.Skipped);

        Assert.Null(run.Follower.Targets.PreferredTurnDirection);
        Assert.Equal(0, ((VfrFollowPhaseDto)phase.ToSnapshot()).FallInSide ?? 0);
    }

    [Fact]
    public void FallIn_LeadSlowerThanFloor_StallTurnOutFires()
    {
        (AircraftState lead, LeadPathTrail path) = NorthboundLead();
        var widen = new FollowWidenState();
        AircraftState follower = FollowerAt(lead, 0.5, 1.0, 0.0);
        AirborneFollowHelper.ComputeFreePursuitHeading(follower, lead, NoCircuitSpacing(null), path, widen);
        Assert.True(widen.FallInEstablished);
        LeadPathProjection projection = path.Project(follower.Position, lead.Position, lead.TrueTrack);

        // The established fall-in is a parallel hold the stall window judges, like an excursion at its cap.
        Assert.True(VfrFollowPhase.IsParallelHold(widen, NoCircuitSpacing(null), projection));

        // A lead slower than the follower's floor: over the window the gap only grows more negative, so the hold has stalled
        // and the stall turn-out is asked for.
        ParallelHoldWindow? window = null;
        bool stalled = false;
        for (int second = 0; (second <= (int)VfrFollowPhase.StallWindowSeconds) && !stalled; second++)
        {
            window = VfrFollowPhase.AdvanceParallelHoldWindow(window, true, projection.GapNm - (second * 0.005), 1.0, out stalled);
        }

        Assert.True(stalled);
    }

    [Fact]
    public void FallIn_StateRoundTripsThroughSnapshot()
    {
        FallInRun run = FallInRow(0.93, 0.3, 225.0, 1);
        VfrFollowPhase phase = Assert.IsType<VfrFollowPhase>(run.Follower.Phases!.CurrentPhase);
        var original = (VfrFollowPhaseDto)phase.ToSnapshot();
        Assert.Equal(1, original.FallInSide);
        Assert.True(original.FallInCalled);

        string json = JsonSerializer.Serialize<PhaseDto>(original, RecordingJsonOptions.Default);
        VfrFollowPhaseDto dto = Assert.IsType<VfrFollowPhaseDto>(JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default));
        var copy = (VfrFollowPhaseDto)VfrFollowPhase.FromSnapshot(dto).ToSnapshot();

        Assert.Equal(1, copy.FallInSide);
        Assert.True(copy.FallInCalled);
        Assert.Equal(original.FallInEstablished, copy.FallInEstablished);
        var established = new VfrFollowPhaseDto
        {
            Status = original.Status,
            ElapsedSeconds = 0,
            TargetCallsign = RowLeader,
            FallInSide = -1,
            FallInEstablished = true,
        };
        Assert.True(((VfrFollowPhaseDto)VfrFollowPhase.FromSnapshot(established).ToSnapshot()).FallInEstablished);
        var older = new VfrFollowPhaseDto
        {
            Status = original.Status,
            ElapsedSeconds = 0,
            TargetCallsign = RowLeader,
        };
        var restored = (VfrFollowPhaseDto)VfrFollowPhase.FromSnapshot(older).ToSnapshot();
        Assert.Null(restored.FallInSide);
        Assert.Null(restored.FallInCalled);
        Assert.Null(restored.FallInEstablished);
    }

    [Fact]
    public void FallIn_SmallHeadingChange_NoCall()
    {
        // 0.3 nm ahead, 0.3 nm right, already tracking 60° right of the lead's track: the turn out is 30°.
        FallInRun run = FallInRow(0.3, 0.3, 60.0, 1);

        Assert.Equal(FloorKts(run.Follower), run.Follower.Targets.TargetSpeed);
        Assert.Equal(0, FallInCalls(run.Follower));
        Assert.False(run.CrossFlipped);
    }

    [Fact]
    public void FallIn_GapTurnsPositive_ResumesPursuitBehind()
    {
        (AircraftState lead, LeadPathTrail path) = NorthboundLead();
        var widen = new FollowWidenState();
        AircraftState follower = FollowerAt(lead, 0.93, 0.3, 0.0);
        AirborneFollowHelper.ComputeFreePursuitHeading(follower, lead, NoCircuitSpacing(null), path, widen);
        Assert.Equal(1, widen.FallInSide);
        widen.FallInCalled = true;

        // The lead has passed: the follower is now 0.5 nm behind it, 1 nm right of its track.
        follower.Position = FollowerAt(lead, -0.5, 1.0, 0.0).Position;
        TrueHeading heading = AirborneFollowHelper.ComputeFreePursuitHeading(follower, lead, NoCircuitSpacing(null), path, widen);

        Assert.Equal(0, widen.FallInSide);
        Assert.False(widen.FallInCalled);
        Assert.Null(follower.Targets.PreferredTurnDirection);
        var fresh = new FollowWidenState();
        AircraftState control = FollowerAt(lead, -0.5, 1.0, 0.0);
        TrueHeading expected = AirborneFollowHelper.ComputeFreePursuitHeading(control, lead, NoCircuitSpacing(null), path, fresh);
        Assert.Equal(fresh.Active, widen.Active);
        Assert.True(heading.AbsAngleTo(expected) < 0.01, $"heading {heading.Degrees:F1}, the positive-gap pursuit steers {expected.Degrees:F1}");
    }

    /// <summary>OAK 28R's right-traffic piston pattern and a lead on its <paramref name="leg"/>.</summary>
    private static (PatternWaypoints Waypoints, AircraftState Lead) PatternLead(PatternEntryLeg leg)
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo rwy = Oak("28R");
        PatternWaypoints waypoints = PatternGeometry.Compute(
            new PatternGeometryRequest(rwy, AircraftCategory.Piston, "C172", 0, PatternDirection.Right)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = NavigationDatabase.Instance.GetRunways(rwy.AirportId),
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );
        AircraftState lead = MakeVfr(RowLeader, OffFinal(rwy, 3.0, 1.0), rwy.TrueHeading, 1000);
        PutOnCircuit(lead, rwy, leg);
        return (waypoints, lead);
    }

    /// <summary>A follower on the downwind line <paramref name="pastAbeamNm"/> past the abeam point (negative: before it).</summary>
    private static AircraftState FollowerOnDownwindLine(PatternWaypoints waypoints, double pastAbeamNm) =>
        FollowerOnDownwindLine(waypoints, pastAbeamNm, 0.0);

    /// <summary>The same follower, its track <paramref name="offDownwindDeg"/> right of the downwind heading.</summary>
    private static AircraftState FollowerOnDownwindLine(PatternWaypoints waypoints, double pastAbeamNm, double offDownwindDeg)
    {
        var abeam = new LatLon(waypoints.DownwindAbeamLat, waypoints.DownwindAbeamLon);
        TrueHeading toward = pastAbeamNm >= 0 ? waypoints.DownwindHeading : waypoints.DownwindHeading.ToReciprocal();
        LatLon position = GeoMath.ProjectPoint(abeam, toward, Math.Abs(pastAbeamNm));
        return MakeVfr(RowFollower, position, waypoints.DownwindHeading + offDownwindDeg, 1000);
    }

    [Fact]
    public void PatternJoin_NegativeProjectedGap_Refused()
    {
        (PatternWaypoints waypoints, AircraftState lead) = PatternLead(PatternEntryLeg.Downwind);
        Assert.IsType<DownwindPhase>(lead.Phases!.CurrentPhase);

        Assert.NotNull(VfrFollowPhase.PatternJoinRefusal(FollowerOnDownwindLine(waypoints, -1.0), lead, waypoints, -0.1));
    }

    [Fact]
    public void PatternJoin_ZeroProjectedGap_Accepted()
    {
        (PatternWaypoints waypoints, AircraftState lead) = PatternLead(PatternEntryLeg.Downwind);
        Assert.IsType<DownwindPhase>(lead.Phases!.CurrentPhase);

        Assert.Null(VfrFollowPhase.PatternJoinRefusal(FollowerOnDownwindLine(waypoints, -1.0), lead, waypoints, 0.0));
    }

    /// <summary>A follower <paramref name="distanceNm"/> straight behind the lead (south of it).</summary>
    private static AircraftState FollowerBehindLead(AircraftState lead, double distanceNm) =>
        MakeVfr(RowFollower, GeoMath.ProjectPoint(lead.Position, new TrueHeading(180), distanceNm), lead.TrueTrack, 1000);

    [Fact]
    public void PatternJoin_BehindButInsideHalfDesired_Refused()
    {
        (PatternWaypoints waypoints, AircraftState lead) = PatternLead(PatternEntryLeg.Downwind);
        // Tracking the runway heading, off the downwind: the join would build an entry, whose leg keeps the free-flight distance.
        double halfDesiredNm = AirborneFollowHelper.FreeFlightDistanceForLeader(AircraftCategory.Piston) * 0.5;

        Assert.NotNull(VfrFollowPhase.PatternJoinRefusal(FollowerBehindLead(lead, halfDesiredNm - 0.05), lead, waypoints, 0.5));
    }

    [Fact]
    public void PatternJoin_BehindAtHalfDesired_Accepted()
    {
        (PatternWaypoints waypoints, AircraftState lead) = PatternLead(PatternEntryLeg.Downwind);
        double halfDesiredNm = AirborneFollowHelper.FreeFlightDistanceForLeader(AircraftCategory.Piston) * 0.5;

        // Half the entry leg's free-flight spacing, plus a micro-mile so the projected point's round trip through DistanceNm
        // cannot fall short of it.
        Assert.Null(VfrFollowPhase.PatternJoinRefusal(FollowerBehindLead(lead, halfDesiredNm + 1e-6), lead, waypoints, 0.5));
    }

    [Fact]
    public void PatternJoin_EntryJoinInsideHalfFreeFlight_Refused()
    {
        (PatternWaypoints waypoints, AircraftState lead) = PatternLead(PatternEntryLeg.Downwind);
        // A C208 lead, as N428KK in YAAT-2782: a turboprop's free-flight distance is 2.0 nm, its pattern spacing 1.5 nm.
        lead.AircraftType = "C208";
        Assert.Equal(2.0, AirborneFollowHelper.FreeFlightDistanceForLeader(AircraftCategorization.Categorize(lead.AircraftType)));
        // 0.9 nm is outside half the pattern spacing, so only the entry leg's free-flight spacing refuses it.
        Assert.True(0.9 >= AirborneFollowHelper.PatternSpacingNm(lead) * 0.5);

        Assert.NotNull(VfrFollowPhase.PatternJoinRefusal(FollowerBehindLead(lead, 0.9), lead, waypoints, 0.5));
    }

    [Fact]
    public void DownwindJoin_LeadOnBaseFollowerPastAbeam_Refused()
    {
        (PatternWaypoints waypoints, AircraftState lead) = PatternLead(PatternEntryLeg.Base);
        Assert.IsType<BasePhase>(lead.Phases!.CurrentPhase);

        // 40° off the downwind, as N805FM was 30.8° off it (YAAT-2778): not established on it, so an entry would turn it back.
        Assert.NotNull(VfrFollowPhase.PatternJoinRefusal(FollowerOnDownwindLine(waypoints, 0.5, 40.0), lead, waypoints, 1.0));
    }

    [Fact]
    public void DownwindJoin_LeadOnDownwindFollowerPastAbeamOff40_Accepted()
    {
        (PatternWaypoints waypoints, AircraftState lead) = PatternLead(PatternEntryLeg.Downwind);
        Assert.IsType<DownwindPhase>(lead.Phases!.CurrentPhase);

        // The control row: the same follower as the based-lead refusal, but the lead is still on its downwind.
        Assert.Null(VfrFollowPhase.PatternJoinRefusal(FollowerOnDownwindLine(waypoints, 0.5, 40.0), lead, waypoints, 1.0));
    }

    [Fact]
    public void DownwindJoin_LeadOnBaseEntryFollowerPastAbeam_Refused()
    {
        (PatternWaypoints waypoints, AircraftState lead) = PatternLead(PatternEntryLeg.Base);
        RunwayInfo rwy = Oak("28R");
        var entry = new PatternEntryPhase
        {
            EntryLat = waypoints.BaseTurnLat,
            EntryLon = waypoints.BaseTurnLon,
            PatternAltitude = waypoints.PatternAltitude,
            Kind = PatternEntryKind.Base,
        };
        lead.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Right };
        lead.Phases.Add(entry);
        lead.Phases.Start(CommandDispatcher.BuildMinimalContext(lead, ActiveRunways.Empty));
        Assert.IsType<PatternEntryPhase>(lead.Phases.CurrentPhase);

        Assert.NotNull(VfrFollowPhase.PatternJoinRefusal(FollowerOnDownwindLine(waypoints, 0.5, 40.0), lead, waypoints, 1.0));
    }

    [Fact]
    public void DownwindJoin_LeadOnFinalFollowerAlreadyOnDownwind_Accepted()
    {
        (PatternWaypoints waypoints, AircraftState lead) = PatternLead(PatternEntryLeg.Final);
        Assert.IsType<FinalApproachPhase>(lead.Phases!.CurrentPhase);

        Assert.Null(VfrFollowPhase.PatternJoinRefusal(FollowerOnDownwindLine(waypoints, 0.5), lead, waypoints, 1.0));
    }

    [Fact]
    public void DownwindJoin_LeadOnBaseFollowerBeforeAbeam_Accepted()
    {
        (PatternWaypoints waypoints, AircraftState lead) = PatternLead(PatternEntryLeg.Base);
        Assert.IsType<BasePhase>(lead.Phases!.CurrentPhase);

        Assert.Null(VfrFollowPhase.PatternJoinRefusal(FollowerOnDownwindLine(waypoints, -1.0), lead, waypoints, 1.0));
    }

    /// <summary>What the replay saw: a spacing excursion flown while ahead of the lead, base turns and when each reached final.</summary>
    private sealed class SequenceWatch
    {
        private readonly CrossSideWatch _aheadSide = new();
        private bool _prevWiden;
        private double? _prevPathGapNm;

        public List<string> WidenWhileAhead { get; } = [];
        public bool CrossFlippedWhileAhead => _aheadSide.Flipped;
        public double MaxTurnWhileAheadDeg { get; private set; }

        /// <summary>The turn (deg) from the follower's track to its target heading, the way its preferred turn direction says.</summary>
        private static double FallInTurnDeg(AircraftState follower)
        {
            if (follower.Targets.TargetTrueHeading is not { } steer)
            {
                return 0.0;
            }

            return follower.Targets.PreferredTurnDirection is { } direction
                ? (((direction == TurnDirection.Right ? 1.0 : -1.0) * follower.TrueTrack.SignedAngleTo(steer)) + 360.0) % 360.0
                : follower.TrueTrack.AbsAngleTo(steer);
        }

        public int? LeadBaseAt { get; private set; }
        public int? FollowerBaseAt { get; private set; }
        public bool FollowOnAtLeadBase { get; private set; }
        public bool FollowOnAtFollowerBase { get; private set; }
        public int? LeadFinalAt { get; private set; }
        public int? FollowerFinalAt { get; private set; }

        public void Observe(int t, AircraftState follower, AircraftState lead, ITestOutputHelper output)
        {
            // Along the lead's track: positive while the lead is ahead of the follower.
            double gapNm = -GeoMath.AlongTrackDistanceNm(follower.Position, lead.Position, lead.TrueTrack);
            if (follower.Phases?.CurrentPhase is VfrFollowPhase follow)
            {
                var dto = (VfrFollowPhaseDto)follow.ToSnapshot();
                var trail = new LeadPathTrail();
                trail.RestoreSnapshot(dto.LeadPath);
                double pathGapNm = trail.Project(follower.Position, lead.Position, lead.TrueTrack).GapNm;
                // The phase decides before each sub-tick's motion: judge against the previous sample's gap.
                bool kept = _prevWiden && dto.WidenActive && (_prevPathGapNm <= 0.0);
                bool started = !_prevWiden && dto.WidenActive && (_prevPathGapNm <= 0.0) && (pathGapNm <= 0.0);
                if (kept || started)
                {
                    WidenWhileAhead.Add($"t={t} path gap {pathGapNm:F2} nm, {_prevPathGapNm:F2} nm a second earlier");
                }

                _prevWiden = dto.WidenActive;
                _prevPathGapNm = pathGapNm;
            }
            else
            {
                _prevWiden = false;
                _prevPathGapNm = null;
            }

            double turnDeg = FallInTurnDeg(follower);
            double crossNm = GeoMath.SignedCrossTrackDistanceNm(follower.Position, lead.Position, lead.TrueTrack);
            if ((gapNm < 0.0) && (follower.Phases?.CurrentPhase is VfrFollowPhase))
            {
                MaxTurnWhileAheadDeg = Math.Max(MaxTurnWhileAheadDeg, turnDeg);
                _aheadSide.Observe(crossNm);
            }

            if ((LeadBaseAt is null) && (lead.Phases?.CurrentPhase is BasePhase))
            {
                LeadBaseAt = t;
                FollowOnAtLeadBase = follower.Approach.FollowingCallsign == Lead;
            }
            if ((FollowerBaseAt is null) && (follower.Phases?.CurrentPhase is BasePhase))
            {
                FollowerBaseAt = t;
                FollowOnAtFollowerBase = follower.Approach.FollowingCallsign == Lead;
            }

            LeadFinalAt ??= lead.Phases?.CurrentPhase is FinalApproachPhase ? t : null;
            FollowerFinalAt ??= follower.Phases?.CurrentPhase is FinalApproachPhase ? t : null;
            if ((t <= 1640) || (t % 10 == 0))
            {
                output.WriteLine(
                    $"t={t}: {Follower} {follower.Phases?.CurrentPhase?.GetType().Name} hdg {follower.TrueHeading.Degrees:F0} "
                        + $"ias {follower.IndicatedAirspeed:F0} cross {crossNm:F2} "
                        + $"turn {follower.Targets.PreferredTurnDirection?.ToString() ?? "-"} {turnDeg:F0}° "
                        + $"following {follower.Approach.FollowingCallsign ?? "-"}; "
                        + $"{Lead} {lead.Phases?.CurrentPhase?.GetType().Name}; gap {gapNm:F2} nm"
                );
            }
        }
    }
}
