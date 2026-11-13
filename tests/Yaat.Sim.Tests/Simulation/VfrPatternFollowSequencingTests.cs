using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Synthetic two-aircraft VFR-pattern FOLLOW sequencing test at KOAK 28R.
///
/// Reproduces the reported bug: a lead (A) flies a large pattern and is already
/// established ahead on a long final; a follower (B) on the downwind behind it is
/// given FOLLOW A. Expected: B extends its downwind and turns base behind A.
/// Observed (pre-fix): B turns base at its own static base-turn point and rolls
/// out on final ahead of A — overtaking the traffic it was told to follow.
///
/// The test ticks both aircraft through the real phase machinery
/// (<see cref="FlightPhysics"/> + <see cref="PhaseRunner"/>) with a shared
/// aircraft lookup so the follow helper resolves the lead, and asserts that B
/// never gets ahead of A in the landing sequence while both are airborne.
/// </summary>
[Collection("NavDbMutator")]
public class VfrPatternFollowSequencingTests
{
    private const string LeadCallsign = "N100AA";
    private const string FollowerCallsign = "N200BB";

    private readonly ITestOutputHelper _output;

    public VfrPatternFollowSequencingTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static PhaseContext Ctx(AircraftState ac, RunwayInfo rwy, Func<string, AircraftState?> lookup) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            AircraftLookup = lookup,
            Logger = NullLogger.Instance,
        };

    /// <summary>Signed along-track distance from the threshold along the downwind
    /// axis — larger = further out in the approach direction = further back in the
    /// landing sequence. This is the same coordinate the base-turn trigger uses.</summary>
    private static double SequenceCoord(AircraftState ac, PatternWaypoints wp) =>
        GeoMath.AlongTrackDistanceNm(ac.Position, new LatLon(wp.ThresholdLat, wp.ThresholdLon), wp.DownwindHeading);

    [Fact]
    public void Follower_DoesNotOvertake_LeadEstablishedAheadOnFinal()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        RunwayInfo? rwy = navDb.GetRunway("KOAK", "28R");
        if (rwy is null)
        {
            _output.WriteLine("KOAK 28R not in navdata — skipping.");
            return;
        }

        IReadOnlyList<RunwayInfo> allRunways = navDb.GetRunways("KOAK");
        const PatternDirection Dir = PatternDirection.Left;
        const AircraftCategory Cat = AircraftCategory.Piston;
        PatternWaypoints wp = PatternGeometry.Compute(
            new PatternGeometryRequest(rwy, Cat, "", 0, Dir)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = allRunways,
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );

        var threshold = new LatLon(wp.ThresholdLat, wp.ThresholdLon);
        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);
        double thresholdElev = rwy.ElevationFt;

        // Lead A: established on a long (2.3 nm) straight-in final, descending on a
        // 3° glideslope, cleared to land — the end state of a large/extended pattern.
        LatLon leadFinalPos = GeoMath.ProjectPoint(threshold, wp.DownwindHeading, 2.3);
        AircraftState lead = MakeVfr(LeadCallsign, leadFinalPos, wp.FinalHeading, altitude: thresholdElev + (2.3 * 318.0), ias: 75);
        List<Phase> leadCircuit = PatternBuilder.BuildCircuit(
            new PatternCircuitRequest
            {
                Runway = rwy,
                Category = Cat,
                AircraftType = "",
                WindSpeedKt = 0,
                Direction = Dir,
                EntryLeg = PatternEntryLeg.Final,
                TouchAndGo = false,
                FinalDistanceNm = null,
                PatternSizeNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = allRunways,
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );
        lead.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            TrafficDirection = Dir,
            PatternRunway = rwy,
        };
        foreach (Phase p in leadCircuit)
        {
            lead.Phases.Add(p);
        }

        // Follower B: on the downwind, 0.2 nm short of its (normal) base-turn point,
        // following A. Past abeam, at pattern altitude.
        LatLon followerPos = GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 0.2);
        AircraftState follower = MakeVfr(FollowerCallsign, followerPos, wp.DownwindHeading, altitude: wp.PatternAltitude, ias: 90);
        follower.Approach.HasReportedTrafficInSight = true;
        List<Phase> followerCircuit = PatternBuilder.BuildCircuit(
            new PatternCircuitRequest
            {
                Runway = rwy,
                Category = Cat,
                AircraftType = "",
                WindSpeedKt = 0,
                Direction = Dir,
                EntryLeg = PatternEntryLeg.Downwind,
                TouchAndGo = false,
                FinalDistanceNm = null,
                PatternSizeNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = allRunways,
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );
        follower.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            TrafficDirection = Dir,
            PatternRunway = rwy,
        };
        foreach (Phase p in followerCircuit)
        {
            follower.Phases.Add(p);
        }

        AircraftState? lookup(string cs) =>
            cs == LeadCallsign ? lead
            : cs == FollowerCallsign ? follower
            : null;

        lead.Phases.Start(Ctx(lead, rwy, lookup));
        follower.Phases.Start(Ctx(follower, rwy, lookup));
        lead.Phases.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.LandingClearance = ClearanceType.ClearedToLand;

        // Issue FOLLOW from B onto A through the real command path (RTISF gate +
        // FOLLOW), exactly as a controller would.
        CommandDispatcher.Dispatch(
            new ReportTrafficInSightForcedCommand(LeadCallsign),
            follower,
            TestDispatch.Context(Random.Shared, findAircraft: lookup)
        );
        CommandResult followResult = CommandDispatcher.Dispatch(
            new FollowCommand(LeadCallsign, false),
            follower,
            TestDispatch.Context(Random.Shared, findAircraft: lookup)
        );
        Assert.True(followResult.Success, $"FOLLOW failed: {followResult.Message}");
        Assert.Equal(LeadCallsign, follower.Approach.FollowingCallsign);

        var recorder = new TickRecorder(lead, follower);
        bool followerReachedFinal = false;
        bool overtakeObserved = false;
        int worstTick = -1;
        double worstGap = double.PositiveInfinity;

        for (int t = 1; t <= 400; t++)
        {
            foreach (AircraftState? ac in new[] { lead, follower })
            {
                PhaseContext ctx = Ctx(ac, rwy, lookup);
                FlightPhysics.Update(ac, ctx.DeltaSeconds);
                PhaseRunner.Tick(ac, ctx);
            }
            recorder.Record(t);

            string bPhase = follower.Phases?.CurrentPhase?.Name ?? "(none)";
            bool bCommitted = follower.Phases?.CurrentPhase is BasePhase or FinalApproachPhase or LandingPhase;
            if (follower.Phases?.CurrentPhase is FinalApproachPhase or LandingPhase)
            {
                followerReachedFinal = true;
            }

            // Overtake = follower committed to landing while the lead is still
            // airborne and committed, yet the follower is closer to the threshold
            // (smaller sequence coordinate) than the lead.
            if (bCommitted && !lead.IsOnGround && lead.Phases?.CurrentPhase is BasePhase or FinalApproachPhase or LandingPhase)
            {
                double aLead = SequenceCoord(lead, wp);
                double aFoll = SequenceCoord(follower, wp);
                double gap = aFoll - aLead;
                if (gap < worstGap)
                {
                    worstGap = gap;
                    worstTick = t;
                }
                if (gap < -0.1)
                {
                    overtakeObserved = true;
                }
            }

            if (follower.IsOnGround && lead.IsOnGround)
            {
                _output.WriteLine($"t={t}: both landed (B last phase {bPhase})");
                break;
            }
        }

        string dump = Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "vfr-pattern-follow.json");
        recorder.WriteJson(dump);
        _output.WriteLine($"worst (aFollower-aLead)={worstGap:F2} nm at t={worstTick}; trace -> {dump}");

        Assert.True(followerReachedFinal, "Follower never reached final within the window (possible infinite downwind hold).");
        Assert.False(
            overtakeObserved,
            $"Follower overtook the lead it was told to follow: aFollower-aLead dropped to {worstGap:F2} nm at t={worstTick} "
                + "(negative = follower closer to threshold than the lead while both airborne)."
        );
    }

    /// <summary>
    /// A downwind follower still held past its base point by more than <see cref="VfrFollowPhase.BaseExtensionLimitNm"/>
    /// behind a lead on final gives up the follow: "unable to follow the traffic, extending downwind, request base turn", the
    /// downwind held extended, and a <c>TB</c> turns it base.
    /// </summary>
    [Fact]
    public void Follower_GivesUpTheFollow_TwoMilesPastBaseBehindLeadOnFinal()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        RunwayInfo? rwy = navDb.GetRunway("KOAK", "28R");
        if (rwy is null)
        {
            return;
        }

        IReadOnlyList<RunwayInfo> allRunways = navDb.GetRunways("KOAK");
        const PatternDirection Dir = PatternDirection.Left;
        const AircraftCategory Cat = AircraftCategory.Piston;
        PatternWaypoints wp = PatternGeometry.Compute(
            new PatternGeometryRequest(rwy, Cat, "", 0, Dir)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = allRunways,
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );
        var threshold = new LatLon(wp.ThresholdLat, wp.ThresholdLon);
        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);
        double baseTurnAlong = GeoMath.AlongTrackDistanceNm(baseTurn, threshold, wp.DownwindHeading);

        // Follower B has already extended past the BaseExtensionLimitNm limit on the
        // downwind track; lead A is on final still only 0.5 nm ahead of B in the
        // sequence coordinate — so the follower still *wants* to keep holding. It does
        // NOT turn base on its own: it gives up the follow, keeps the downwind extended
        // and asks for a base turn.
        double pastBaseNm = VfrFollowPhase.BaseExtensionLimitNm + 0.5;
        double bAlong = baseTurnAlong + pastBaseNm;
        LatLon followerPos = GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading, pastBaseNm);
        AircraftState follower = MakeVfr(FollowerCallsign, followerPos, wp.DownwindHeading, altitude: wp.PatternAltitude, ias: 90);
        follower.Approach.HasReportedTrafficInSight = true;
        follower.Approach.FollowingCallsign = LeadCallsign;
        var downwind = new DownwindPhase { Waypoints = wp };
        follower.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            TrafficDirection = Dir,
            PatternRunway = rwy,
        };
        follower.Phases.Add(downwind);
        follower.Phases.Add(new BasePhase { Waypoints = wp });

        LatLon leadPos = GeoMath.ProjectPoint(threshold, wp.DownwindHeading, bAlong - 0.5);
        AircraftState lead = MakeVfr(LeadCallsign, leadPos, wp.FinalHeading, altitude: rwy.ElevationFt + 300, ias: 70);
        lead.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            TrafficDirection = Dir,
            PatternRunway = rwy,
        };
        lead.Phases.Add(new FinalApproachPhase());

        AircraftState? lookup(string cs) =>
            cs == LeadCallsign ? lead
            : cs == FollowerCallsign ? follower
            : null;

        lead.Phases.Start(Ctx(lead, rwy, lookup));
        PhaseContext ctx = Ctx(follower, rwy, lookup);
        follower.Phases.Start(ctx);

        bool completed = downwind.OnTick(ctx);

        Assert.False(completed, "Follower must NOT turn base on its own at the extension limit — it keeps flying the downwind.");
        // RPO-default warning is the pilot's compact terminal line (callsign in the SAY column).
        Assert.Contains(
            follower.PendingWarnings,
            w => w.Contains($"unable to follow {LeadCallsign}, extending downwind, request base turn", StringComparison.Ordinal)
        );
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.True(downwind.IsExtended);

        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound("TB", follower.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, $"Parse of 'TB' failed: {parsed.Reason}");
        CommandResult tb = CommandDispatcher.DispatchCompound(parsed.Value!, follower, TestDispatch.Context(Random.Shared, findAircraft: lookup));
        Assert.True(tb.Success, $"TB failed: {tb.Message}");
        for (int t = 0; (t < 5) && (follower.Phases.CurrentPhase is DownwindPhase); t++)
        {
            PhaseRunner.Tick(follower, Ctx(follower, rwy, lookup));
        }
        Assert.IsType<BasePhase>(follower.Phases.CurrentPhase);
    }

    /// <summary>
    /// A downwind follower held 2.5 nm past its base point behind a lead on a final with no runway assigned (a lead landing a
    /// known other runway ends the follow on its own) does not give up the follow at
    /// <see cref="VfrFollowPhase.BaseExtensionLimitNm"/>: the give-up is for a lead landing on the follower's own runway, so
    /// it keeps holding and following.
    /// </summary>
    [Fact]
    public void Follower_KeepsTheFollow_PastTheGiveUpLimitBehindRunwaylessLeadOnFinal()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? rwy = navDb?.GetRunway("KOAK", "28R");
        RunwayInfo? parallel = navDb?.GetRunway("KOAK", "28L");
        if ((navDb is null) || (rwy is null) || (parallel is null))
        {
            return;
        }

        const PatternDirection Dir = PatternDirection.Left;
        PatternWaypoints wp = PatternGeometry.Compute(
            new PatternGeometryRequest(rwy, AircraftCategory.Piston, "", 0, Dir)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = navDb.GetRunways("KOAK"),
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );
        var threshold = new LatLon(wp.ThresholdLat, wp.ThresholdLon);
        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);
        double pastBaseNm = VfrFollowPhase.BaseExtensionLimitNm + 0.5;
        double followerAlong = GeoMath.AlongTrackDistanceNm(baseTurn, threshold, wp.DownwindHeading) + pastBaseNm;

        AircraftState follower = MakeVfr(
            FollowerCallsign,
            GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading, pastBaseNm),
            wp.DownwindHeading,
            wp.PatternAltitude,
            90
        );
        follower.Approach.HasReportedTrafficInSight = true;
        follower.Approach.FollowingCallsign = LeadCallsign;
        var downwind = new DownwindPhase { Waypoints = wp };
        follower.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            TrafficDirection = Dir,
            PatternRunway = rwy,
        };
        follower.Phases.Add(downwind);
        follower.Phases.Add(new BasePhase { Waypoints = wp });

        // A jet on the parallel's final 1.55 nm nearer the field than the follower, no runway assigned: inside the proximity
        // hold's reach (0.6 x its 3 nm spacing) and outside the minimum-speed cancel's (0.5 x).
        var parallelThreshold = new LatLon(parallel.ThresholdLatitude, parallel.ThresholdLongitude);
        LatLon leadPos = GeoMath.ProjectPoint(parallelThreshold, wp.DownwindHeading, followerAlong - 1.55);
        AircraftState lead = MakeVfr(LeadCallsign, leadPos, parallel.TrueHeading, parallel.ElevationFt + 1200, 140);
        lead.AircraftType = "B738";
        lead.Phases = new PhaseList();
        lead.Phases.Add(new FinalApproachPhase());

        AircraftState? lookup(string cs) =>
            cs == LeadCallsign ? lead
            : cs == FollowerCallsign ? follower
            : null;

        lead.Phases.Start(Ctx(lead, parallel, lookup));
        PhaseContext ctx = Ctx(follower, rwy, lookup);
        follower.Phases.Start(ctx);
        double gapNm = GeoMath.DistanceNm(follower.Position, lead.Position);
        double spacingNm = AirborneFollowHelper.PatternSpacingNm(lead);
        _output.WriteLine($"gap {gapNm:F2} nm, spacing {spacingNm:F1} nm");
        Assert.InRange(gapNm, 0.5 * spacingNm, 0.6 * spacingNm);

        for (int t = 0; t < 5; t++)
        {
            Assert.False(downwind.OnTick(ctx), $"tick {t}: the follower must still be held on the downwind");
        }

        _output.WriteLine($"extended={downwind.IsExtended}; warnings: {string.Join(" | ", follower.PendingWarnings)}");

        Assert.Equal(LeadCallsign, follower.Approach.FollowingCallsign);
        Assert.False(downwind.IsExtended);
        Assert.DoesNotContain(follower.PendingWarnings, w => w.Contains("unable to follow", StringComparison.Ordinal));
    }

    private static AircraftState MakeVfr(string callsign, LatLon pos, TrueHeading heading, double altitude, double ias) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = pos,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", FlightRules = "VFR" },
            Approach = new AircraftApproachState(),
        };
}
