using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for the FOLLOW-behind-a-straight-in-IFR-lead bug.
///
/// Recording: S2-OAK-5 (2) "Practical Exam Preparation/Advanced Concepts" (ZOA),
/// OAK 28R. N713UP (VFR piston, pattern alt ~1009 ft) was set up to land 28R via
/// <c>EF 28R</c>, then given <c>FOLLOW N8307E</c> at t=742. N8307E is an IFR
/// aircraft on a straight-in final to 28R (spawned directly into
/// <see cref="FinalApproachPhase"/> — it never flew a VFR pattern, so it has no
/// pattern-leg waypoints).
///
/// Observed (the original bug): FOLLOW installed <see cref="VfrFollowPhase"/>, which
/// wiped N713UP's runway + landing chain and only steered laterally toward the lead
/// while holding 1009 ft; when N8307E landed the follow cancelled and N713UP free-flew
/// level over the 28R threshold at 1009 ft.
///
/// Expected: N713UP sits in the 28R approach corridor at FOLLOW time, so the install
/// keeps the direct in-trail join (the issue-#352 pattern-aware install applies only to
/// followers positioned off the corridor, e.g. abeam the field on a downwind): it is
/// sequenced onto 28R final behind N8307E, descends, and holds for a separate landing
/// clearance (CLAND) — going around if never cleared.
///
/// Replay strategy: hybrid (snapshot restore just before FOLLOW, then forward) —
/// mirrors <see cref="Issue148FollowOnFinalTests"/> which uses the same scenario.
/// </summary>
public class VfrFollowSequencesToFinalTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/vfr-follow-no-land-recording.yaat-bug-report-bundle.zip";
    private const string Follower = "N713UP";
    private const string Leader = "N8307E";

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
            .EnableCategory("VfrFollowPhase", LogLevel.Debug)
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .EnableCategory("FinalApproachPhase", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// Core bug assertion: by the time the lead is on a short final, the follower must
    /// have been sequenced onto the lead's runway final and be descending — not stuck
    /// in <see cref="VfrFollowPhase"/> / free-flying level over the field at 1009 ft.
    /// </summary>
    [Fact]
    public void Follower_SequencesOntoRunwayFinal_DescendingBehindLead()
    {
        var navDb = TestVnasData.NavigationDb;
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (navDb is null || archive is null)
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
            var snapshot = archive.ReadSnapshotAt(740);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            engine.ReplayRange((int)snapshot.ElapsedSeconds, 860, recording.Actions);

            var follower = engine.FindAircraft(Follower);
            Assert.NotNull(follower);

            var phase = follower.Phases?.CurrentPhase;
            Assert.True(
                phase is FinalApproachPhase or LandingPhase,
                $"Follower should be on the runway final, got {phase?.GetType().Name ?? "(null)"} at alt {follower.Altitude:F0}ft"
            );
            Assert.Equal("28R", follower.Phases?.AssignedRunway?.Designator);

            // Pattern altitude was ~1009 ft; the bug left it level there. After the fix it
            // is well into the descent.
            Assert.True(follower.Altitude < 850, $"Follower should have descended below 850 ft, was {follower.Altitude:F0}ft");

            // Stayed genuinely behind the landing traffic (never cut in front).
            var lead = engine.FindAircraft(Leader);
            if (lead is not null && !lead.IsOnGround)
            {
                var threshold = new LatLon(navDb.GetRunway("KOAK", "28R")!.ThresholdLatitude, navDb.GetRunway("KOAK", "28R")!.ThresholdLongitude);
                double followerDist = GeoMath.DistanceNm(follower.Position, threshold);
                double leadDist = GeoMath.DistanceNm(lead.Position, threshold);
                Assert.True(followerDist > leadDist, $"Follower ({followerDist:F2}nm) must stay behind the lead ({leadDist:F2}nm)");
            }
        }
    }

    /// <summary>
    /// The follower holds for a landing clearance: once the controller issues CLAND
    /// while it is on final, it lands. (The recording never cleared N713UP, so the
    /// clearance is injected here; physics-only ticks avoid the recorded DEL at t=929.)
    /// </summary>
    [Fact]
    public void Follower_LandsAfterClearedToLand()
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
            var snapshot = archive.ReadSnapshotAt(740);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            engine.ReplayRange((int)snapshot.ElapsedSeconds, 830, recording.Actions);

            var follower = engine.FindAircraft(Follower);
            Assert.NotNull(follower);
            Assert.True(
                follower.Phases?.CurrentPhase is FinalApproachPhase,
                $"Precondition: follower should be on final at t=830, got {follower.Phases?.CurrentPhase?.GetType().Name ?? "(null)"}"
            );
            Assert.Equal("28R", follower.Phases?.AssignedRunway?.Designator);

            var clandResult = engine.SendCommand(Follower, "CLAND");
            Assert.True(clandResult.Success, $"CLAND should succeed once the follower is on final: {clandResult.Message}");

            // Physics-only ticks: do not re-apply the recording (its DEL at t=929 would
            // despawn the follower before touchdown).
            bool landed = false;
            for (int t = 0; t < 300; t++)
            {
                engine.TickOneSecond();
                follower = engine.FindAircraft(Follower);
                if (follower is null)
                {
                    break;
                }
                if (follower.IsOnGround)
                {
                    landed = true;
                    break;
                }
            }

            Assert.NotNull(follower);
            Assert.True(
                landed,
                $"Follower should have landed after CLAND; phase {follower.Phases?.CurrentPhase?.GetType().Name ?? "(null)"}, alt {follower.Altitude:F0}ft"
            );
        }
    }

    /// <summary>
    /// "Follow that traffic, cleared to land runway 28R." The controller clears the
    /// follower to land while it is still pursuing its lead and has no runway of its
    /// own. <c>CLAND 28R</c> arms the clearance; when <see cref="VfrFollowPhase"/>
    /// sequences the follower onto 28R final, the clearance carries over and the
    /// follower lands without a second CLAND. (This recording's follower sits in the
    /// 28R approach corridor at FOLLOW time, so the install keeps the direct in-trail
    /// join rather than the issue-#352 pattern join.)
    /// </summary>
    [Fact]
    public void Follower_LandsOnArmedClearance_ClandRunwayWhileFollowing()
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
            var snapshot = archive.ReadSnapshotAt(740);
            if (snapshot is null)
            {
                return;
            }
            engine.RestoreFromSnapshot(snapshot.State);
            int start = (int)snapshot.ElapsedSeconds;

            // Advance a few seconds so FOLLOW (t=742) installs VfrFollowPhase.
            engine.ReplayRange(start, 760, recording.Actions);
            var follower = engine.FindAircraft(Follower);
            Assert.NotNull(follower);
            Assert.True(
                follower.Phases?.CurrentPhase is VfrFollowPhase,
                $"Precondition: follower should still be following at t=760, got {follower.Phases?.CurrentPhase?.GetType().Name ?? "(null)"}"
            );
            Assert.Null(follower.Phases?.AssignedRunway);

            // Clear it to land 28R while it has no runway of its own — arms the clearance.
            var cland = engine.SendCommand(Follower, "CLAND 28R");
            Assert.True(cland.Success, $"CLAND 28R should arm while following: {cland.Message}");
            Assert.Equal(ClearanceType.ClearedToLand, follower.Phases?.LandingClearance);

            // Sequence onto 28R final — the armed clearance carries onto the rebuilt chain.
            engine.ReplayRange(760, 830, recording.Actions);
            follower = engine.FindAircraft(Follower);
            Assert.NotNull(follower);
            Assert.True(
                follower.Phases?.CurrentPhase is FinalApproachPhase or LandingPhase,
                $"Follower should be on 28R final by t=830, got {follower.Phases?.CurrentPhase?.GetType().Name ?? "(null)"}"
            );
            Assert.Equal("28R", follower.Phases?.AssignedRunway?.Designator);
            Assert.Equal(ClearanceType.ClearedToLand, follower.Phases?.LandingClearance);

            // No second CLAND: it lands on the armed clearance. Physics-only ticks avoid
            // the recorded DEL at t=929.
            bool landed = false;
            for (int t = 0; t < 300; t++)
            {
                engine.TickOneSecond();
                follower = engine.FindAircraft(Follower);
                if (follower is null)
                {
                    break;
                }
                if (follower.IsOnGround)
                {
                    landed = true;
                    break;
                }
            }

            Assert.NotNull(follower);
            Assert.True(
                landed,
                $"Follower should land on the armed clearance without a second CLAND; phase {follower.Phases?.CurrentPhase?.GetType().Name ?? "(null)"}, alt {follower.Altitude:F0}ft"
            );
        }
    }

    /// <summary>
    /// Visual separation — and therefore FOLLOW — is not authorized behind a super
    /// (7110.65 §7-2-1; AIM §5-5-11.2.5). FOLLOW behind an A388 must be rejected, while
    /// FOLLOW behind ordinary traffic still succeeds.
    /// </summary>
    [Fact]
    public void Follow_RejectedBehindSuper_AllowedBehindOrdinaryTraffic()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var follower = MakeAirborneVfr("N456CD", "C172");
        follower.Approach.HasReportedTrafficInSight = true;

        var superLead = MakeAirborneVfr("BAW286", "A388");
        var jetLead = MakeAirborneVfr("UAL77", "B738");

        Func<string, AircraftState?> lookup = cs =>
            cs == "N456CD" ? follower
            : cs == "BAW286" ? superLead
            : cs == "UAL77" ? jetLead
            : null;

        var superResult = CommandDispatcher.Dispatch(
            new FollowCommand("BAW286", false),
            follower,
            TestDispatch.Context(System.Random.Shared, findAircraft: lookup)
        );
        Assert.False(superResult.Success, "FOLLOW behind a super should be rejected");
        Assert.Contains("super", superResult.Message, System.StringComparison.OrdinalIgnoreCase);

        var jetResult = CommandDispatcher.Dispatch(
            new FollowCommand("UAL77", false),
            follower,
            TestDispatch.Context(System.Random.Shared, findAircraft: lookup)
        );
        Assert.True(jetResult.Success, $"FOLLOW behind ordinary traffic should succeed: {jetResult.Message}");
        Assert.Equal("UAL77", follower.Approach.FollowingCallsign);
    }

    private static AircraftState MakeAirborneVfr(string callsign, string type) =>
        new()
        {
            Callsign = callsign,
            AircraftType = type,
            Position = new LatLon(37.80, -122.00),
            TrueHeading = new TrueHeading(290),
            TrueTrack = new TrueHeading(290),
            Altitude = 2000,
            IndicatedAirspeed = 110,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", FlightRules = "VFR" },
            Approach = new AircraftApproachState(),
        };
}
