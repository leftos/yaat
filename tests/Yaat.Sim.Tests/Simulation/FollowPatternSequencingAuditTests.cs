using Microsoft.Extensions.Logging;
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
/// FOLLOW pattern-sequencing audit (2026-07). Synthetic two-aircraft KOAK 28R harness
/// (real navdata, synthetic positions) that exercises the leader-instruction-propagation
/// behaviors directly through the real phase machinery: the follower must react to the
/// *leader's* pattern progression, not just its raw position.
///
/// Companion to <see cref="VfrPatternFollowSequencingTests"/> (static lead-on-final) and
/// <see cref="VfrFollowExtendedLeadBaseTurnTests"/> (recording-based extend hold). These
/// tests drive the dynamic <c>EXT → TB</c> transition and the upwind/crosswind extend-to-
/// sequence behavior.
/// </summary>
[Collection("NavDbMutator")]
public class FollowPatternSequencingAuditTests
{
    private const string LeadCallsign = "N100AA";
    private const string FollowerCallsign = "N200BB";

    private readonly ITestOutputHelper _output;

    public FollowPatternSequencingAuditTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("DownwindPhase", LogLevel.Debug)
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .InitializeSimLog();
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

    /// <summary>Signed along-track distance from the threshold along the downwind axis —
    /// larger = further out in the approach direction = further back in the landing
    /// sequence. Same coordinate the base-turn trigger uses.</summary>
    private static double SequenceCoord(AircraftState ac, PatternWaypoints wp) =>
        GeoMath.AlongTrackDistanceNm(ac.Position, new LatLon(wp.ThresholdLat, wp.ThresholdLon), wp.DownwindHeading);

    /// <summary>
    /// Scenario #2 (dynamic): lead + follower both on Downwind, FOLLOW established. The lead
    /// is given <c>EXT</c> (extends downwind) and later <c>TB</c> (turn base). The follower
    /// must (a) hold its own base turn while the lead is still extending, and (b) keep
    /// extending after the lead turns base until it can roll out at least the category desired
    /// distance behind — never overtaking the traffic it was told to follow.
    /// </summary>
    [Fact]
    public void Follower_SequencesBehind_WhenLeadExtendsThenTurnsBase()
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

        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);
        double desired = AirborneFollowHelper.DesiredDistanceForLeader(Cat);

        // Lead A: on downwind, 0.3 nm short of its base-turn point, past abeam, at pattern
        // altitude. Follower B: on the same downwind 1.0 nm behind A (1.3 nm short of the
        // base turn), following A.
        var leadPos = GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 0.3);
        var lead = MakeVfr(LeadCallsign, leadPos, wp.DownwindHeading, altitude: wp.PatternAltitude, ias: 90);
        AttachCircuit(lead, rwy, Cat, Dir, PatternEntryLeg.Downwind, allRunways);

        var followerPos = GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 1.3);
        var follower = MakeVfr(FollowerCallsign, followerPos, wp.DownwindHeading, altitude: wp.PatternAltitude, ias: 90);
        follower.Approach.HasReportedTrafficInSight = true;
        AttachCircuit(follower, rwy, Cat, Dir, PatternEntryLeg.Downwind, allRunways);

        Func<string, AircraftState?> lookup = cs =>
            cs == LeadCallsign ? lead
            : cs == FollowerCallsign ? follower
            : null;

        lead.Phases!.Start(Ctx(lead, rwy, lookup));
        follower.Phases!.Start(Ctx(follower, rwy, lookup));
        lead.Phases.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.LandingClearance = ClearanceType.ClearedToLand;

        // Establish FOLLOW through the real command path (RTISF gate + FOLLOW).
        CommandDispatcher.Dispatch(
            new ReportTrafficInSightForcedCommand(LeadCallsign),
            follower,
            TestDispatch.Context(Random.Shared, findAircraft: lookup)
        );
        var followResult = CommandDispatcher.Dispatch(
            new FollowCommand(LeadCallsign, false),
            follower,
            TestDispatch.Context(Random.Shared, findAircraft: lookup)
        );
        Assert.True(followResult.Success, $"FOLLOW failed: {followResult.Message}");

        // Extend the lead's downwind immediately. EXT/TB are tower commands routed through
        // the compound-dispatch path (TryApplyTowerCommand), not the single-command arm.
        var extResult = DispatchTower("EXT", lead, lookup);
        Assert.True(extResult.Success, $"EXT failed: {extResult.Message}");
        Assert.True(lead.Phases.CurrentPhase is DownwindPhase { IsExtended: true }, "Lead should be on extended Downwind after EXT.");

        var recorder = new TickRecorder(lead, follower);
        bool followerReachedFinal = false;
        bool overtakeObserved = false;
        bool turnBaseIssued = false;
        int worstTick = -1;
        double worstGap = double.PositiveInfinity;
        int followerBaseTick = -1;
        double followerBaseGap = double.NaN;

        for (int t = 1; t <= 400; t++)
        {
            // Turn the lead base at t=20 — long after the follower would have hit its own
            // fixed base-turn point (~15 s at 90 kt over 1.3 nm minus the abeam offset).
            if (t == 20)
            {
                var tbResult = DispatchTower("TB", lead, lookup);
                Assert.True(tbResult.Success, $"TB failed: {tbResult.Message}");
                turnBaseIssued = true;
            }

            foreach (var ac in new[] { lead, follower })
            {
                var ctx = Ctx(ac, rwy, lookup);
                FlightPhysics.Update(ac, ctx.DeltaSeconds);
                PhaseRunner.Tick(ac, ctx);
            }
            recorder.Record(t);

            // While the lead is still extending its downwind, the follower must remain on
            // Downwind (holding to sequence behind it) and keep following.
            if (!turnBaseIssued && lead.Phases?.CurrentPhase is DownwindPhase { IsExtended: true })
            {
                Assert.True(
                    follower.Phases?.CurrentPhase is DownwindPhase,
                    $"t={t}: follower turned {follower.Phases?.CurrentPhase?.Name} while lead still extending downwind — should hold."
                );
                Assert.Equal(LeadCallsign, follower.Approach.FollowingCallsign);
            }

            // Record the moment the follower first commits to base.
            if (followerBaseTick < 0 && follower.Phases?.CurrentPhase is BasePhase)
            {
                followerBaseTick = t;
                followerBaseGap = SequenceCoord(follower, wp) - SequenceCoord(lead, wp);
            }

            if (follower.Phases?.CurrentPhase is FinalApproachPhase or LandingPhase)
            {
                followerReachedFinal = true;
            }

            // Overtake = follower committed to landing while the lead is still airborne and
            // committed, yet the follower is closer to the threshold than the lead.
            bool bCommitted = follower.Phases?.CurrentPhase is BasePhase or FinalApproachPhase or LandingPhase;
            if (bCommitted && !lead.IsOnGround && lead.Phases?.CurrentPhase is BasePhase or FinalApproachPhase or LandingPhase)
            {
                double gap = SequenceCoord(follower, wp) - SequenceCoord(lead, wp);
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
                break;
            }
        }

        string dump = Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "follow-audit-ext-tb.json");
        recorder.WriteJson(dump);
        _output.WriteLine(
            $"desired={desired:F1} nm; follower committed base at t={followerBaseTick} (gap={followerBaseGap:F2} nm); "
                + $"worst (aFollower-aLead)={worstGap:F2} nm at t={worstTick}; trace -> {dump}"
        );

        Assert.True(followerReachedFinal, "Follower never reached final within the window (possible infinite downwind hold).");
        Assert.False(
            overtakeObserved,
            $"Follower overtook the lead it was told to follow: aFollower-aLead dropped to {worstGap:F2} nm at t={worstTick}."
        );
        Assert.True(followerBaseTick > 0, "Follower never turned base within the window.");
        Assert.True(
            followerBaseGap >= desired - 0.35,
            $"Follower turned base only {followerBaseGap:F2} nm behind the lead (desired {desired:F1} nm) — turned base too early."
        );
    }

    /// <summary>
    /// The remaining-pattern-path metric must decrease monotonically as an aircraft progresses
    /// around the circuit toward the threshold (upwind → crosswind → downwind → base → final), and
    /// an extended leg must read as MORE remaining than the same leg at its normal turn point. This
    /// ordering is what makes the metric a valid sequence coordinate on every leg, including the
    /// reciprocal-heading upwind leg.
    /// </summary>
    [Fact]
    public void RemainingPatternPath_DecreasesMonotonicallyTowardThreshold()
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
        var der = new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon);
        var dwStart = new LatLon(wp.DownwindStartLat, wp.DownwindStartLon);
        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);
        double width = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(dwStart, threshold, wp.DownwindHeading));

        // Farthest (upwind extended) to nearest (short final).
        (string name, AircraftState ac)[] samples =
        [
            ("upwind+2 (extended)", OnLeg(GeoMath.ProjectPoint(der, wp.UpwindHeading, 2.0), new UpwindPhase { Waypoints = wp }, rwy)),
            ("upwind @DER", OnLeg(der, new UpwindPhase { Waypoints = wp }, rwy)),
            ("crosswind mid", OnLeg(GeoMath.ProjectPoint(der, wp.CrosswindHeading, width * 0.5), new CrosswindPhase { Waypoints = wp }, rwy)),
            ("downwind start", OnLeg(dwStart, new DownwindPhase { Waypoints = wp }, rwy)),
            (
                "downwind near base",
                OnLeg(GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 1.0), new DownwindPhase { Waypoints = wp }, rwy)
            ),
            ("base mid", OnLeg(GeoMath.ProjectPoint(baseTurn, wp.BaseHeading, width * 0.5), new BasePhase { Waypoints = wp }, rwy)),
            ("final 1nm", OnLeg(GeoMath.ProjectPoint(threshold, wp.FinalHeading.ToReciprocal(), 1.0), new FinalApproachPhase(), rwy)),
        ];

        double prev = double.PositiveInfinity;
        foreach (var (name, ac) in samples)
        {
            double r = AirborneFollowHelper.RemainingPatternPathNm(ac, wp);
            _output.WriteLine($"{name}: remaining={r:F2} nm");
            Assert.True(r < prev, $"'{name}' remaining {r:F2} nm is not less than the previous position's {prev:F2} nm — metric non-monotonic.");
            prev = r;
        }
    }

    /// <summary>
    /// Guard check: a follower must NOT hold (extend) its leg to sequence behind a lead that is
    /// actually behind it in pattern flow (here the lead is on the earlier upwind leg while the
    /// follower is on crosswind). Extending a pattern to fall in behind trailing traffic is
    /// aviationally absurd; the remaining-path hold shares the same <c>IsLeadPatternFlowBehind</c>
    /// guard as every other follow path.
    /// </summary>
    [Fact]
    public void Follower_DoesNotHoldLeg_WhenLeadIsBehindInPatternFlow()
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
        var dwStart = new LatLon(wp.DownwindStartLat, wp.DownwindStartLon);
        var der = new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon);

        // Follower on crosswind (leg 2), at the downwind-start (would turn downwind), following a
        // lead that is behind it on the earlier upwind leg (leg 1).
        var follower = MakeVfr(FollowerCallsign, dwStart, wp.CrosswindHeading, wp.PatternAltitude, ias: 80);
        follower.Approach.FollowingCallsign = LeadCallsign;
        follower.Phases = new PhaseList { AssignedRunway = rwy };
        follower.Phases.Add(new CrosswindPhase { Waypoints = wp });

        var lead = MakeVfr(LeadCallsign, der, wp.UpwindHeading, wp.PatternAltitude, ias: 80);
        lead.Phases = new PhaseList { AssignedRunway = rwy };
        lead.Phases.Add(new UpwindPhase { Waypoints = wp });

        Func<string, AircraftState?> lookup = cs =>
            cs == LeadCallsign ? lead
            : cs == FollowerCallsign ? follower
            : null;
        var ctx = Ctx(follower, rwy, lookup);

        Assert.False(
            AirborneFollowHelper.ShouldHoldLegForRemainingPathSequencing(ctx, wp),
            "Follower must not hold its leg to sequence behind a lead that is behind it in pattern flow."
        );
    }

    /// <summary>
    /// At the extension cap on upwind, a follower must NOT turn crosswind on its own — it keeps
    /// flying the upwind leg (OnTick returns false) and advises once ("extending upwind … unable to
    /// turn"). The turn is the controller's to give.
    /// </summary>
    [Fact]
    public void Follower_KeepsExtendingUpwindAndWarns_WhenExtensionCapReached()
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
        var der = new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon);

        // Follower extended past the cap on upwind, following a lead extended even further ahead on
        // the same upwind leg — so the follower still wants to hold.
        var follower = MakeVfr(
            FollowerCallsign,
            GeoMath.ProjectPoint(der, wp.UpwindHeading, AirborneFollowHelper.MaxFollowExtensionNm + 0.5),
            wp.UpwindHeading,
            altitude: wp.PatternAltitude,
            ias: 80
        );
        follower.Approach.FollowingCallsign = LeadCallsign;
        var upwind = new UpwindPhase { Waypoints = wp };
        follower.Phases = new PhaseList { AssignedRunway = rwy };
        follower.Phases.Add(upwind);

        var lead = MakeVfr(
            LeadCallsign,
            GeoMath.ProjectPoint(der, wp.UpwindHeading, AirborneFollowHelper.MaxFollowExtensionNm + 2.0),
            wp.UpwindHeading,
            altitude: wp.PatternAltitude,
            ias: 80
        );
        lead.Phases = new PhaseList { AssignedRunway = rwy };
        lead.Phases.Add(new UpwindPhase { Waypoints = wp });

        Func<string, AircraftState?> lookup = cs =>
            cs == LeadCallsign ? lead
            : cs == FollowerCallsign ? follower
            : null;

        lead.Phases.Start(Ctx(lead, rwy, lookup));
        var ctx = Ctx(follower, rwy, lookup);
        follower.Phases.Start(ctx);

        bool completed = upwind.OnTick(ctx);

        Assert.False(completed, "Follower must NOT turn crosswind on its own at the extension cap — it keeps flying the upwind.");
        Assert.Contains(
            follower.PendingWarnings,
            w =>
                w.Contains("extending upwind", StringComparison.OrdinalIgnoreCase) && w.Contains("unable to turn", StringComparison.OrdinalIgnoreCase)
        );
    }

    private static AircraftState OnLeg(LatLon pos, Phase leg, RunwayInfo rwy)
    {
        var ac = MakeVfr("TEST", pos, new TrueHeading(0), altitude: 1000, ias: 80);
        ac.Phases = new PhaseList { AssignedRunway = rwy };
        ac.Phases.Add(leg); // CurrentIndex defaults to 0, so CurrentPhase == leg without Start()
        return ac;
    }

    /// <summary>
    /// Upwind extend-to-sequence: lead + follower both on Upwind, FOLLOW established. The lead
    /// is given <c>EXT</c> (extends upwind). The follower must not turn crosswind while the lead
    /// is still extending the same upwind leg ahead of it — turning off would leapfrog the lead
    /// into the downwind sequence. Once the lead is told <c>TC</c> (turn crosswind), the follower
    /// completes its own upwind behind it and never overtakes into final.
    /// </summary>
    [Fact]
    public void Follower_HoldsUpwind_WhileLeadExtendsUpwind()
    {
        RunLegHoldScenario(
            entry: PatternEntryLeg.Upwind,
            legHeading: wp => wp.UpwindHeading,
            turnPoint: wp => new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon),
            isLeadExtendedOnLeg: lead => lead.Phases?.CurrentPhase is UpwindPhase { IsExtended: true },
            isFollowerOnLeg: foll => foll.Phases?.CurrentPhase is UpwindPhase,
            releaseCommand: "TC",
            dumpName: "follow-audit-upwind-hold.json"
        );
    }

    /// <summary>
    /// Crosswind extend-to-sequence: same as the upwind case one leg later. Lead + follower both
    /// on Crosswind, FOLLOW established, lead given <c>EXT</c>; the follower holds its downwind
    /// turn until the lead is told <c>TD</c> (turn downwind), then trails without overtaking.
    /// </summary>
    [Fact]
    public void Follower_HoldsCrosswind_WhileLeadExtendsCrosswind()
    {
        RunLegHoldScenario(
            entry: PatternEntryLeg.Crosswind,
            legHeading: wp => wp.CrosswindHeading,
            turnPoint: wp => new LatLon(wp.DownwindStartLat, wp.DownwindStartLon),
            isLeadExtendedOnLeg: lead => lead.Phases?.CurrentPhase is CrosswindPhase { IsExtended: true },
            isFollowerOnLeg: foll => foll.Phases?.CurrentPhase is CrosswindPhase,
            releaseCommand: "TD",
            dumpName: "follow-audit-crosswind-hold.json"
        );
    }

    private void RunLegHoldScenario(
        PatternEntryLeg entry,
        Func<PatternWaypoints, TrueHeading> legHeading,
        Func<PatternWaypoints, LatLon> turnPoint,
        Func<AircraftState, bool> isLeadExtendedOnLeg,
        Func<AircraftState, bool> isFollowerOnLeg,
        string releaseCommand,
        string dumpName
    )
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

        var hdg = legHeading(wp);
        var turn = turnPoint(wp);

        // Lead 0.4 nm past the leg's turn point (about to complete), follower 0.8 nm behind the
        // turn point, both at pattern altitude on the leg heading, follower following the lead.
        var lead = MakeVfr(LeadCallsign, GeoMath.ProjectPoint(turn, hdg, 0.4), hdg, altitude: wp.PatternAltitude, ias: 80);
        AttachCircuit(lead, rwy, Cat, Dir, entry, allRunways);

        var follower = MakeVfr(FollowerCallsign, GeoMath.ProjectPoint(turn, hdg.ToReciprocal(), 0.8), hdg, altitude: wp.PatternAltitude, ias: 80);
        follower.Approach.HasReportedTrafficInSight = true;
        AttachCircuit(follower, rwy, Cat, Dir, entry, allRunways);

        Func<string, AircraftState?> lookup = cs =>
            cs == LeadCallsign ? lead
            : cs == FollowerCallsign ? follower
            : null;

        lead.Phases!.Start(Ctx(lead, rwy, lookup));
        follower.Phases!.Start(Ctx(follower, rwy, lookup));
        lead.Phases.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.LandingClearance = ClearanceType.ClearedToLand;

        CommandDispatcher.Dispatch(
            new ReportTrafficInSightForcedCommand(LeadCallsign),
            follower,
            TestDispatch.Context(Random.Shared, findAircraft: lookup)
        );
        var followResult = CommandDispatcher.Dispatch(
            new FollowCommand(LeadCallsign, false),
            follower,
            TestDispatch.Context(Random.Shared, findAircraft: lookup)
        );
        Assert.True(followResult.Success, $"FOLLOW failed: {followResult.Message}");

        var extResult = DispatchTower("EXT", lead, lookup);
        Assert.True(extResult.Success, $"EXT failed: {extResult.Message}");
        Assert.True(isLeadExtendedOnLeg(lead), "Lead should be on its extended leg after EXT.");

        var recorder = new TickRecorder(lead, follower);
        bool leapfrogObserved = false;
        bool followerHeldPastTurnPoint = false;
        bool followerReachedFinal = false;
        bool overtakeObserved = false;
        bool released = false;
        int worstTick = -1;
        double worstGap = double.PositiveInfinity;

        // The follow survives the whole circuit (it is only cancelled when the lead lands),
        // so the follower spends the full downwind sequencing behind the lead before turning
        // base. That correctly-sequenced circuit needs more than the ~400 s a follower took
        // back when the runaway watchdog spuriously cancelled the follow mid-downwind.
        for (int t = 1; t <= 700; t++)
        {
            // Release the lead off its extended leg once the follower has demonstrably held past
            // its own normal turn point.
            if (!released && followerHeldPastTurnPoint)
            {
                var rel = DispatchTower(releaseCommand, lead, lookup);
                Assert.True(rel.Success, $"{releaseCommand} failed: {rel.Message}");
                released = true;
            }

            foreach (var ac in new[] { lead, follower })
            {
                var ctx = Ctx(ac, rwy, lookup);
                FlightPhysics.Update(ac, ctx.DeltaSeconds);
                PhaseRunner.Tick(ac, ctx);
            }
            recorder.Record(t);

            // While the lead is still extending its leg and has not been released, the follower
            // must remain on that same leg (holding) — it must NOT leapfrog onto the next leg.
            if (!released && isLeadExtendedOnLeg(lead))
            {
                if (!isFollowerOnLeg(follower))
                {
                    leapfrogObserved = true;
                }
                else
                {
                    double alongPastTurn = GeoMath.AlongTrackDistanceNm(follower.Position, turn, legHeading(wp));
                    if (alongPastTurn > 0.1)
                    {
                        followerHeldPastTurnPoint = true;
                    }
                }
            }

            if (follower.Phases?.CurrentPhase is FinalApproachPhase or LandingPhase)
            {
                followerReachedFinal = true;
            }

            bool bCommitted = follower.Phases?.CurrentPhase is BasePhase or FinalApproachPhase or LandingPhase;
            if (bCommitted && !lead.IsOnGround && lead.Phases?.CurrentPhase is BasePhase or FinalApproachPhase or LandingPhase)
            {
                double gap = SequenceCoord(follower, wp) - SequenceCoord(lead, wp);
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
                break;
            }
        }

        string dump = Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", dumpName);
        recorder.WriteJson(dump);
        _output.WriteLine(
            $"heldPastTurnPoint={followerHeldPastTurnPoint} reachedFinal={followerReachedFinal} worstGap={worstGap:F2} at t={worstTick}; trace -> {dump}"
        );

        Assert.False(
            leapfrogObserved,
            "Follower turned off its leg while the lead was still extending it ahead — leapfrogged the lead's pattern progression."
        );
        Assert.True(followerHeldPastTurnPoint, "Follower never flew past its own normal turn point while holding — the leg hold was not exercised.");
        Assert.True(followerReachedFinal, "Follower never reached final within the window.");
        Assert.False(overtakeObserved, $"Follower overtook the lead: aFollower-aLead dropped to {worstGap:F2} nm at t={worstTick}.");
    }

    /// <summary>
    /// Regression for the held-downwind-lead overtake (bundle S2-OAK-3 (2) "VFR Sequencing"):
    /// a lead still on the shared downwind, past its own base-turn point but WITHOUT the
    /// <c>IsExtended</c> flag (it was cleared when the lead was itself told to follow other
    /// traffic), must count as flow-ahead so a same-leg follower sequences behind it instead
    /// of turning base inside it. A lead merely progressing (short of its base turn) must NOT.
    /// </summary>
    [Fact]
    public void IsLeadPatternFlowAhead_TreatsHeldDownwindLead_AsFlowAhead()
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
        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);

        // Follower: on the downwind, 1.3 nm short of the base turn.
        var followerPos = GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 1.3);
        var follower = MakeVfr(FollowerCallsign, followerPos, wp.DownwindHeading, wp.PatternAltitude, 90);
        AttachCircuit(follower, rwy, Cat, Dir, PatternEntryLeg.Downwind, allRunways);
        Func<string, AircraftState?> lookup = cs => cs == FollowerCallsign ? follower : null;
        follower.Phases!.Start(Ctx(follower, rwy, lookup));

        // Held lead: on the SAME downwind, 0.3 nm PAST its base-turn point, IsExtended never
        // set (mirrors a lead holding out via its own follow-hold). This is exactly the state
        // the pre-fix IsExtended-only gate missed.
        var heldPos = GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading, 0.3);
        var heldLead = MakeVfr(LeadCallsign, heldPos, wp.DownwindHeading, wp.PatternAltitude, 90);
        AttachCircuit(heldLead, rwy, Cat, Dir, PatternEntryLeg.Downwind, allRunways);
        heldLead.Phases!.Start(Ctx(heldLead, rwy, lookup));
        Assert.True(heldLead.Phases.CurrentPhase is DownwindPhase { IsExtended: false }, "Held lead must be on a non-extended Downwind.");
        Assert.True(
            ((DownwindPhase)heldLead.Phases.CurrentPhase!).HasReachedBaseTurnPoint(heldLead.Position),
            "Held lead should be past its own base-turn point."
        );
        Assert.True(
            AirborneFollowHelper.IsLeadPatternFlowAhead(follower, heldLead),
            "A same-leg lead held past its base turn (no IsExtended) must count as flow-ahead."
        );

        // Progressing lead: on the same downwind, 0.6 nm SHORT of its base turn, not extended.
        // It will turn base at its own point ahead of the follower, so the follower keeps normal
        // spacing rather than extending behind it.
        var progressingPos = GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 0.6);
        var progressingLead = MakeVfr(LeadCallsign, progressingPos, wp.DownwindHeading, wp.PatternAltitude, 90);
        AttachCircuit(progressingLead, rwy, Cat, Dir, PatternEntryLeg.Downwind, allRunways);
        progressingLead.Phases!.Start(Ctx(progressingLead, rwy, lookup));
        Assert.False(
            ((DownwindPhase)progressingLead.Phases.CurrentPhase!).HasReachedBaseTurnPoint(progressingLead.Position),
            "Progressing lead should be short of its base-turn point."
        );
        Assert.False(
            AirborneFollowHelper.IsLeadPatternFlowAhead(follower, progressingLead),
            "A same-leg lead merely progressing (short of its base turn) must NOT count as flow-ahead."
        );
    }

    private static CommandResult DispatchTower(string command, AircraftState ac, Func<string, AircraftState?> lookup)
    {
        var parsed = CommandParser.ParseCompound(command, ac.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, $"Parse of '{command}' failed: {parsed.Reason}");
        return CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(Random.Shared, findAircraft: lookup));
    }

    private static void AttachCircuit(
        AircraftState ac,
        RunwayInfo rwy,
        AircraftCategory cat,
        PatternDirection dir,
        PatternEntryLeg entry,
        IReadOnlyList<RunwayInfo> allRunways
    )
    {
        var circuit = PatternBuilder.BuildCircuit(rwy, cat, dir, entry, false, null, null, null, allRunways);
        ac.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            TrafficDirection = dir,
            PatternRunway = rwy,
        };
        foreach (var p in circuit)
        {
            ac.Phases.Add(p);
        }
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
