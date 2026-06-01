using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
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
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy = navDb.GetRunway("KOAK", "28R");
        if (rwy is null)
        {
            _output.WriteLine("KOAK 28R not in navdata — skipping.");
            return;
        }

        var allRunways = navDb.GetRunways("KOAK");
        const PatternDirection Dir = PatternDirection.Left;
        const AircraftCategory Cat = AircraftCategory.Piston;
        var wp = PatternGeometry.Compute(rwy, Cat, Dir, null, null, allRunways);

        var threshold = new LatLon(wp.ThresholdLat, wp.ThresholdLon);
        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);
        double thresholdElev = rwy.ElevationFt;

        // Lead A: established on a long (2.3 nm) straight-in final, descending on a
        // 3° glideslope, cleared to land — the end state of a large/extended pattern.
        var leadFinalPos = GeoMath.ProjectPoint(threshold, wp.DownwindHeading, 2.3);
        var lead = MakeVfr(LeadCallsign, leadFinalPos, wp.FinalHeading, altitude: thresholdElev + (2.3 * 318.0), ias: 75);
        var leadCircuit = PatternBuilder.BuildCircuit(rwy, Cat, Dir, PatternEntryLeg.Final, false, null, null, null, allRunways);
        lead.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            TrafficDirection = Dir,
            PatternRunway = rwy,
        };
        foreach (var p in leadCircuit)
        {
            lead.Phases.Add(p);
        }

        // Follower B: on the downwind, 0.2 nm short of its (normal) base-turn point,
        // following A. Past abeam, at pattern altitude.
        var followerPos = GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 0.2);
        var follower = MakeVfr(FollowerCallsign, followerPos, wp.DownwindHeading, altitude: wp.PatternAltitude, ias: 90);
        follower.Approach.HasReportedTrafficInSight = true;
        var followerCircuit = PatternBuilder.BuildCircuit(rwy, Cat, Dir, PatternEntryLeg.Downwind, false, null, null, null, allRunways);
        follower.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            TrafficDirection = Dir,
            PatternRunway = rwy,
        };
        foreach (var p in followerCircuit)
        {
            follower.Phases.Add(p);
        }

        Func<string, AircraftState?> lookup = cs =>
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
        var followResult = CommandDispatcher.Dispatch(
            new FollowCommand(LeadCallsign),
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
            foreach (var ac in new[] { lead, follower })
            {
                var ctx = Ctx(ac, rwy, lookup);
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

    [Fact]
    public void Follower_TurnsBaseAndWarns_WhenExtensionCapReached()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }

        var rwy = navDb.GetRunway("KOAK", "28R");
        if (rwy is null)
        {
            return;
        }

        var allRunways = navDb.GetRunways("KOAK");
        const PatternDirection Dir = PatternDirection.Left;
        const AircraftCategory Cat = AircraftCategory.Piston;
        var wp = PatternGeometry.Compute(rwy, Cat, Dir, null, null, allRunways);
        var threshold = new LatLon(wp.ThresholdLat, wp.ThresholdLon);
        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);
        double baseTurnAlong = GeoMath.AlongTrackDistanceNm(baseTurn, threshold, wp.DownwindHeading);

        // Follower B has already extended past the MaxFollowExtensionNm cap on the
        // downwind track; lead A is on final still only 0.5 nm ahead of B in the
        // sequence coordinate — so the follower still *wants* to keep holding, but
        // the cap forces the base turn and a one-shot warning.
        double bAlong = baseTurnAlong + AirborneFollowHelper.MaxFollowExtensionNm + 0.5;
        var followerPos = GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading, AirborneFollowHelper.MaxFollowExtensionNm + 0.5);
        var follower = MakeVfr(FollowerCallsign, followerPos, wp.DownwindHeading, altitude: wp.PatternAltitude, ias: 90);
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

        var leadPos = GeoMath.ProjectPoint(threshold, wp.DownwindHeading, bAlong - 0.5);
        var lead = MakeVfr(LeadCallsign, leadPos, wp.FinalHeading, altitude: rwy.ElevationFt + 300, ias: 70);
        lead.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            TrafficDirection = Dir,
            PatternRunway = rwy,
        };
        lead.Phases.Add(new FinalApproachPhase());

        Func<string, AircraftState?> lookup = cs =>
            cs == LeadCallsign ? lead
            : cs == FollowerCallsign ? follower
            : null;

        lead.Phases.Start(Ctx(lead, rwy, lookup));
        var ctx = Ctx(follower, rwy, lookup);
        follower.Phases.Start(ctx);

        bool completed = downwind.OnTick(ctx);

        Assert.True(completed, "Follower should turn base (phase completes) once it reaches the extension cap.");
        Assert.Contains(follower.PendingWarnings, w => w.Contains("max downwind extension", StringComparison.OrdinalIgnoreCase));
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
