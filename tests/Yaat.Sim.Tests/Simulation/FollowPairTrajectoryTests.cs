using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Paired-trajectory FOLLOW matrix at KOAK (issue #352). Each test places a lead (A) and a
/// follower (B) in a distinct geometry, issues FOLLOW through the real command path, ticks
/// both aircraft through the real phase machinery, and asserts trajectory-level invariants:
///
///   - No about-face: the follower never settles onto a runway-heading track while laterally
///     out on the pattern side of the runway on a pre-base leg — the exact signature of #352
///     (a downwind aircraft turning ~180° onto a parallel-to-final offset track).
///   - No overtake: while both aircraft are committed to landing, the follower never gets
///     closer to the threshold than the lead it was told to follow.
///   - Landing order: the lead touches down first; the follower still lands within the window.
///
/// Companion to <see cref="FollowPatternSequencingAuditTests"/> (leg-hold audit) and
/// <see cref="VfrPatternFollowSequencingTests"/> (static lead-on-final). This matrix adds the
/// entry-phase, no-phase, wrong-side, and cross-runway FOLLOW geometries.
/// </summary>
[Collection("NavDbMutator")]
public class FollowPairTrajectoryTests
{
    private const string LeadCallsign = "N100AA";
    private const string FollowerCallsign = "N200BB";

    private readonly ITestOutputHelper _output;

    public FollowPairTrajectoryTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("DownwindPhase", LogLevel.Debug)
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .EnableCategory("PatternEntryPhase", LogLevel.Debug)
            .EnableCategory("PatternCommandHandler", LogLevel.Debug)
            .EnableCategory("VfrFollowPhase", LogLevel.Debug)
            .InitializeSimLog();
    }

    // ─── Harness ───

    private sealed record Sample(int T, LatLon Position, double TrackDeg, string Phase, bool OnGround, double AltitudeFt);

    private sealed record PairRun(List<Sample> Lead, List<Sample> Follower, int LeadLandedTick, int FollowerLandedTick);

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
    /// larger = further back in the landing sequence (same coordinate as the base-turn trigger).</summary>
    private static double SequenceCoord(LatLon pos, PatternWaypoints wp) =>
        GeoMath.AlongTrackDistanceNm(pos, new LatLon(wp.ThresholdLat, wp.ThresholdLon), wp.DownwindHeading);

    private static AircraftState MakeVfr(string callsign, string type, LatLon pos, TrueHeading heading, double altitude, double ias) =>
        new()
        {
            Callsign = callsign,
            AircraftType = type,
            Position = pos,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", FlightRules = "VFR" },
            Approach = new AircraftApproachState(),
        };

    private static void AttachCircuit(
        AircraftState ac,
        RunwayInfo rwy,
        AircraftCategory cat,
        PatternDirection dir,
        PatternEntryLeg entry,
        IReadOnlyList<RunwayInfo> allRunways
    )
    {
        var circuit = PatternBuilder.BuildCircuit(rwy, cat, "", 0, dir, entry, false, null, null, null, allRunways, authoredRunway: null);
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

    private static Func<string, AircraftState?> Lookup(AircraftState lead, AircraftState follower) =>
        cs =>
            cs == lead.Callsign ? lead
            : cs == follower.Callsign ? follower
            : null;

    private static void DispatchFollow(AircraftState follower, string leadCallsign, Func<string, AircraftState?> lookup)
    {
        CommandDispatcher.Dispatch(
            new ReportTrafficInSightForcedCommand(leadCallsign),
            follower,
            TestDispatch.Context(Random.Shared, findAircraft: lookup)
        );
        var result = CommandDispatcher.Dispatch(
            new FollowCommand(leadCallsign, false),
            follower,
            TestDispatch.Context(Random.Shared, findAircraft: lookup)
        );
        Assert.True(result.Success, $"FOLLOW failed: {result.Message}");
        Assert.Equal(leadCallsign, follower.Approach.FollowingCallsign);
    }

    private static CommandResult DispatchCommand(string command, AircraftState ac, Func<string, AircraftState?> lookup)
    {
        var parsed = CommandParser.ParseCompound(command, ac.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, $"Parse of '{command}' failed: {parsed.Reason}");
        return CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(Random.Shared, findAircraft: lookup));
    }

    /// <summary>Tick both aircraft for up to <paramref name="maxTicks"/> seconds, recording per-tick
    /// samples, and stop once both are on the ground. <paramref name="onTick"/> runs before each tick.</summary>
    private PairRun RunPair(
        AircraftState lead,
        AircraftState follower,
        RunwayInfo rwy,
        Func<string, AircraftState?> lookup,
        int maxTicks,
        string dumpName,
        Action<int>? onTick
    )
    {
        var recorder = new TickRecorder(lead, follower);
        var leadSamples = new List<Sample>();
        var followerSamples = new List<Sample>();
        int leadLanded = -1;
        int followerLanded = -1;

        for (int t = 1; t <= maxTicks; t++)
        {
            onTick?.Invoke(t);
            foreach (var ac in new[] { lead, follower })
            {
                var ctx = Ctx(ac, rwy, lookup);
                FlightPhysics.Update(ac, ctx.DeltaSeconds);
                PhaseRunner.Tick(ac, ctx);
            }
            recorder.Record(t);
            leadSamples.Add(SampleOf(t, lead));
            followerSamples.Add(SampleOf(t, follower));

            if (leadLanded < 0 && lead.IsOnGround)
            {
                leadLanded = t;
            }
            if (followerLanded < 0 && follower.IsOnGround)
            {
                followerLanded = t;
            }
            if (leadLanded > 0 && followerLanded > 0)
            {
                break;
            }
        }

        string dump = Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", dumpName);
        recorder.WriteJson(dump);
        _output.WriteLine($"leadLanded={leadLanded} followerLanded={followerLanded}; trace -> {dump}");
        return new PairRun(leadSamples, followerSamples, leadLanded, followerLanded);
    }

    private static Sample SampleOf(int t, AircraftState ac) =>
        new(t, ac.Position, ac.TrueTrack.Degrees, ac.Phases?.CurrentPhase?.GetType().Name ?? "none", ac.IsOnGround, ac.Altitude);

    /// <summary>Phases on which a runway-heading track is legitimate (final turn, rollout, upwind).</summary>
    private static readonly string[] RunwayAlignedPhases =
    [
        nameof(BasePhase),
        nameof(FinalApproachPhase),
        nameof(LandingPhase),
        nameof(TouchAndGoPhase),
        nameof(UpwindPhase),
    ];

    /// <summary>
    /// The #352 signature: the follower settles onto a runway-heading track (within 35°)
    /// while laterally out on the pattern side (>0.35 nm) on a pre-base leg. A legitimate
    /// circuit never does this — downwind is reciprocal, crosswind/base are perpendicular,
    /// and upwind/final are runway-aligned but on the centerline (and excluded by phase).
    /// Fails when the condition persists for ≥6 consecutive ticks.
    /// </summary>
    private void AssertNoAboutFace(List<Sample> follower, RunwayInfo rwy, PatternDirection dir)
    {
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        double patternSign = dir == PatternDirection.Right ? 1.0 : -1.0;
        int consecutive = 0;
        foreach (var s in follower)
        {
            bool phaseExcluded = s.OnGround || RunwayAlignedPhases.Contains(s.Phase);
            double sideDistNm = GeoMath.SignedCrossTrackDistanceNm(s.Position, threshold, rwy.TrueHeading) * patternSign;
            double offRunwayHeadingDeg = GeoMath.AbsBearingDifference(s.TrackDeg, rwy.TrueHeading.Degrees);
            bool wrongWay = !phaseExcluded && (sideDistNm > 0.35) && (offRunwayHeadingDeg < 35.0);
            consecutive = wrongWay ? consecutive + 1 : 0;
            Assert.True(
                consecutive < 6,
                $"t={s.T}: follower about-face — tracking {s.TrackDeg:F0}° (runway {rwy.TrueHeading.Degrees:F0}°) at "
                    + $"{sideDistNm:F2} nm on the pattern side while on {s.Phase} — the #352 parallel-offset-track signature."
            );
        }
    }

    /// <summary>While both aircraft are committed (base/final/landing) and airborne, the follower's
    /// sequence coordinate must never drop more than 0.1 nm below the lead's. When the committed
    /// windows never overlap, the assertion is only satisfied vacuously if the lead was already on
    /// the ground before the follower ever committed — otherwise the test setup lost its oracle.</summary>
    private void AssertNoOvertake(PairRun run, PatternWaypoints wp)
    {
        string[] committed = [nameof(BasePhase), nameof(FinalApproachPhase), nameof(LandingPhase)];
        double worstGap = double.PositiveInfinity;
        int worstTick = -1;
        int compared = 0;
        for (int i = 0; i < run.Follower.Count; i++)
        {
            var f = run.Follower[i];
            var l = run.Lead[i];
            if (f.OnGround || l.OnGround || !committed.Contains(f.Phase) || !committed.Contains(l.Phase))
            {
                continue;
            }
            compared++;
            double gap = SequenceCoord(f.Position, wp) - SequenceCoord(l.Position, wp);
            if (gap < worstGap)
            {
                worstGap = gap;
                worstTick = f.T;
            }
        }
        if (compared == 0)
        {
            int firstCommitTick = run.Follower.FirstOrDefault(s => committed.Contains(s.Phase))?.T ?? int.MaxValue;
            _output.WriteLine($"no committed-phase overlap; lead landed t={run.LeadLandedTick}, follower first committed t={firstCommitTick}");
            Assert.True(
                (run.LeadLandedTick > 0) && (run.LeadLandedTick <= firstCommitTick),
                $"No committed-phase overlap AND the lead (landed t={run.LeadLandedTick}) was still airborne when the follower "
                    + $"committed at t={firstCommitTick} — the overtake oracle never engaged."
            );
            return;
        }
        _output.WriteLine($"worst (aFollower-aLead)={worstGap:F2} nm at t={worstTick} over {compared} ticks");
        Assert.True(
            worstGap >= -0.1,
            $"Follower overtook the lead it was told to follow: aFollower-aLead dropped to {worstGap:F2} nm at t={worstTick}."
        );
    }

    /// <summary>AIM 4-3-3 FIG 4-3-3 note 7: while descending on base/final below pattern altitude,
    /// the follower must never be on the far side of a parallel runway's extended centerline —
    /// being there means its capture path penetrates the parallel's final approach course.</summary>
    private static void AssertStaysClearOfParallelFinal(List<Sample> follower, RunwayInfo target, RunwayInfo parallel, double tpaFt)
    {
        var targetThreshold = new LatLon(target.ThresholdLatitude, target.ThresholdLongitude);
        double parallelCrossNm = GeoMath.SignedCrossTrackDistanceNm(
            new LatLon(parallel.ThresholdLatitude, parallel.ThresholdLongitude),
            targetThreshold,
            target.TrueHeading
        );
        string[] lowPhases = [nameof(BasePhase), nameof(FinalApproachPhase)];
        foreach (var s in follower)
        {
            if (s.OnGround || !lowPhases.Contains(s.Phase) || (s.AltitudeFt >= tpaFt))
            {
                continue;
            }
            double crossNm = GeoMath.SignedCrossTrackDistanceNm(s.Position, targetThreshold, target.TrueHeading);
            bool beyondParallel = (Math.Sign(crossNm) == Math.Sign(parallelCrossNm)) && (Math.Abs(crossNm) > Math.Abs(parallelCrossNm));
            Assert.False(
                beyondParallel,
                $"t={s.T}: follower on {s.Phase} at {s.AltitudeFt:F0} ft is beyond {parallel.Designator}'s final "
                    + $"(cross={crossNm:F2} nm vs parallel centerline at {parallelCrossNm:F2} nm) — its capture of "
                    + $"{target.Designator} penetrates the parallel's final approach course (AIM 4-3-3 FIG 4-3-3 note 7)."
            );
        }
    }

    private static void AssertLandingOrder(PairRun run)
    {
        Assert.True(run.LeadLandedTick > 0, "Lead never landed within the window.");
        Assert.True(run.FollowerLandedTick > 0, "Follower never landed within the window (stuck hold or lost sequencing).");
        Assert.True(
            run.FollowerLandedTick > run.LeadLandedTick,
            $"Follower landed at t={run.FollowerLandedTick}, before/with the lead at t={run.LeadLandedTick}."
        );
    }

    private (RunwayInfo rwy, IReadOnlyList<RunwayInfo> all)? ResolveRunway(string designator)
    {
        var navDb = TestVnasData.NavigationDb;
        var rwy = navDb?.GetRunway("KOAK", designator);
        if (navDb is null || rwy is null)
        {
            _output.WriteLine($"KOAK {designator} not in navdata — skipping.");
            return null;
        }
        return (rwy, navDb.GetRunways("KOAK"));
    }

    /// <summary>Lead established on a straight-in final at <paramref name="finalNm"/> from the threshold,
    /// cleared to land, built via a Final-entry circuit (FinalApproach → Landing, no pattern waypoints).</summary>
    private static AircraftState MakeLeadOnStraightInFinal(
        RunwayInfo rwy,
        PatternWaypoints wp,
        IReadOnlyList<RunwayInfo> allRunways,
        double finalNm,
        double ias
    )
    {
        var threshold = new LatLon(wp.ThresholdLat, wp.ThresholdLon);
        var pos = GeoMath.ProjectPoint(threshold, wp.FinalHeading.ToReciprocal(), finalNm);
        var lead = MakeVfr(LeadCallsign, "C172", pos, wp.FinalHeading, altitude: rwy.ElevationFt + (finalNm * 318.0), ias: ias);
        var circuit = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategory.Piston,
            "",
            0,
            PatternDirection.Right,
            PatternEntryLeg.Final,
            false,
            null,
            null,
            null,
            allRunways,
            authoredRunway: null
        );
        lead.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            TrafficDirection = PatternDirection.Right,
            PatternRunway = rwy,
        };
        foreach (var p in circuit)
        {
            lead.Phases.Add(p);
        }
        return lead;
    }

    // ─── 1. #352 repro (D1): ERD at/past the midfield abeam point must not U-turn ───

    /// <summary>
    /// The incident geometry from issue #352: an aircraft approaching along the right downwind
    /// for 28R, already alongside the midfield abeam point (laterally outside the downwind
    /// track, so the fixed entry point is &gt;1 nm away and both lead-in candidates project
    /// BEHIND it), is told ERD 28R and then FOLLOW on a lead rolling out on a 2 nm final.
    /// The entry must join the downwind from present position — not command a U-turn back to
    /// a lead-in point, which produces the reported "about-face onto a parallel offset track".
    /// </summary>
    [Fact]
    public void Erd_AtMidfieldAbeam_JoinsDownwind_ThenFollowsLeadOnFinal()
    {
        if (ResolveRunway("28R") is not var (rwy, allRunways))
        {
            return;
        }

        const PatternDirection Dir = PatternDirection.Right;
        const AircraftCategory Cat = AircraftCategory.Piston;
        var wp = PatternGeometry.Compute(rwy, Cat, "", 0, Dir, null, null, allRunways, authoredRunway: null);
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);

        // Follower: established on the downwind track (0.15 nm outside it), 1.1 nm PAST the
        // fixed abeam entry point, tracking the downwind heading — "midfield downwind" as the
        // pilot reports it. distToEntry to the fixed entry point is > 1.0 nm, which (pre-fix)
        // installs a PatternEntryPhase, and BOTH lead-in candidates project behind the
        // aircraft — the commanded U-turn of issue #352.
        var alongPast = GeoMath.ProjectPoint(abeam, wp.DownwindHeading, 1.1);
        var patternOutward = Dir == PatternDirection.Right ? rwy.TrueHeading + 90.0 : rwy.TrueHeading - 90.0;
        var followerPos = GeoMath.ProjectPoint(alongPast, patternOutward, 0.15);
        double distToEntry = GeoMath.DistanceNm(followerPos, abeam);
        Assert.True(distToEntry > 1.0, $"Test setup: expected the fixed entry point > 1.0 nm away, got {distToEntry:F2} nm.");

        var follower = MakeVfr(FollowerCallsign, "C172", followerPos, wp.DownwindHeading, wp.PatternAltitude, ias: 95);
        var lead = MakeLeadOnStraightInFinal(rwy, wp, allRunways, finalNm: 2.0, ias: 75);
        var lookup = Lookup(lead, follower);
        lead.Phases!.Start(Ctx(lead, rwy, lookup));
        lead.Phases.LandingClearance = ClearanceType.ClearedToLand;

        var erd = DispatchCommand("ERD 28R", follower, lookup);
        Assert.True(erd.Success, $"ERD failed: {erd.Message}");
        follower.Approach.HasReportedTrafficInSight = true;
        DispatchFollow(follower, LeadCallsign, lookup);
        follower.Phases!.LandingClearance = ClearanceType.ClearedToLand;

        var run = RunPair(lead, follower, rwy, lookup, maxTicks: 700, dumpName: "follow-pair-352-repro.json", onTick: null);

        AssertNoAboutFace(run.Follower, rwy, Dir);
        AssertNoOvertake(run, wp);
        AssertLandingOrder(run);
    }

    // ─── 2. Established downwind, lead on a straight-in final ───

    [Fact]
    public void Follow_FromDownwind_LeadOnStraightInFinal_SequencesBehind()
    {
        if (ResolveRunway("28R") is not var (rwy, allRunways))
        {
            return;
        }

        const PatternDirection Dir = PatternDirection.Right;
        const AircraftCategory Cat = AircraftCategory.Piston;
        var wp = PatternGeometry.Compute(rwy, Cat, "", 0, Dir, null, null, allRunways, authoredRunway: null);
        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);

        var follower = MakeVfr(
            FollowerCallsign,
            "C172",
            GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 0.2),
            wp.DownwindHeading,
            wp.PatternAltitude,
            ias: 90
        );
        follower.Approach.HasReportedTrafficInSight = true;
        AttachCircuit(follower, rwy, Cat, Dir, PatternEntryLeg.Downwind, allRunways);

        var lead = MakeLeadOnStraightInFinal(rwy, wp, allRunways, finalNm: 2.0, ias: 75);
        var lookup = Lookup(lead, follower);
        lead.Phases!.Start(Ctx(lead, rwy, lookup));
        follower.Phases!.Start(Ctx(follower, rwy, lookup));
        lead.Phases.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.LandingClearance = ClearanceType.ClearedToLand;

        DispatchFollow(follower, LeadCallsign, lookup);

        var run = RunPair(lead, follower, rwy, lookup, maxTicks: 700, dumpName: "follow-pair-downwind-straightin.json", onTick: null);

        AssertNoAboutFace(run.Follower, rwy, Dir);
        AssertNoOvertake(run, wp);
        AssertLandingOrder(run);
    }

    // ─── 3. Established downwind, lead progressing downwind → base → final ahead ───

    [Fact]
    public void Follow_FromDownwind_LeadProgressesThroughBaseAndFinal_SequencesBehind()
    {
        if (ResolveRunway("28R") is not var (rwy, allRunways))
        {
            return;
        }

        const PatternDirection Dir = PatternDirection.Right;
        const AircraftCategory Cat = AircraftCategory.Piston;
        var wp = PatternGeometry.Compute(rwy, Cat, "", 0, Dir, null, null, allRunways, authoredRunway: null);
        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);

        // Lead 0.2 nm short of its base turn; follower 1.5 nm behind it on the same downwind.
        var lead = MakeVfr(
            LeadCallsign,
            "C172",
            GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 0.2),
            wp.DownwindHeading,
            wp.PatternAltitude,
            ias: 90
        );
        AttachCircuit(lead, rwy, Cat, Dir, PatternEntryLeg.Downwind, allRunways);

        var follower = MakeVfr(
            FollowerCallsign,
            "C172",
            GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 1.7),
            wp.DownwindHeading,
            wp.PatternAltitude,
            ias: 90
        );
        follower.Approach.HasReportedTrafficInSight = true;
        AttachCircuit(follower, rwy, Cat, Dir, PatternEntryLeg.Downwind, allRunways);

        var lookup = Lookup(lead, follower);
        lead.Phases!.Start(Ctx(lead, rwy, lookup));
        follower.Phases!.Start(Ctx(follower, rwy, lookup));
        lead.Phases.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.LandingClearance = ClearanceType.ClearedToLand;

        DispatchFollow(follower, LeadCallsign, lookup);

        var run = RunPair(lead, follower, rwy, lookup, maxTicks: 700, dumpName: "follow-pair-lead-progresses.json", onTick: null);

        AssertNoAboutFace(run.Follower, rwy, Dir);
        AssertNoOvertake(run, wp);
        AssertLandingOrder(run);
    }

    // ─── 4. (D2) No phase at all, downwind-shaped track, lead on final ───

    /// <summary>
    /// A follower with NO phase (hand-flown heading/DCT — exactly N178M's state before the ERD
    /// in #352) abeam midfield on the pattern side, told to FOLLOW a lead on a 2 nm final.
    /// This dispatches through the VfrFollowPhase-install branch. It must not about-face onto
    /// a parallel-to-final track; it must sequence pattern-shaped behind the lead and land.
    /// </summary>
    [Fact]
    public void Follow_NoPhase_OnDownwindTrack_LeadOnFinal_NoAboutFace()
    {
        if (ResolveRunway("28R") is not var (rwy, allRunways))
        {
            return;
        }

        const PatternDirection Dir = PatternDirection.Right;
        const AircraftCategory Cat = AircraftCategory.Piston;
        var wp = PatternGeometry.Compute(rwy, Cat, "", 0, Dir, null, null, allRunways, authoredRunway: null);
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);

        // On the downwind line at the abeam point, tracking the downwind heading, no phases.
        var follower = MakeVfr(FollowerCallsign, "C172", abeam, wp.DownwindHeading, wp.PatternAltitude, ias: 95);
        follower.Approach.HasReportedTrafficInSight = true;
        Assert.Null(follower.Phases);

        var lead = MakeLeadOnStraightInFinal(rwy, wp, allRunways, finalNm: 2.0, ias: 75);
        var lookup = Lookup(lead, follower);
        lead.Phases!.Start(Ctx(lead, rwy, lookup));
        lead.Phases.LandingClearance = ClearanceType.ClearedToLand;

        DispatchFollow(follower, LeadCallsign, lookup);
        follower.Phases!.LandingClearance = ClearanceType.ClearedToLand;

        var run = RunPair(lead, follower, rwy, lookup, maxTicks: 700, dumpName: "follow-pair-nophase.json", onTick: null);

        AssertNoAboutFace(run.Follower, rwy, Dir);
        AssertNoOvertake(run, wp);
        AssertLandingOrder(run);
    }

    // ─── 5. FOLLOW mid pattern-entry (normal far entry, lead-in ahead) ───

    [Fact]
    public void Follow_DuringPatternEntry_KeepsEntry_AndLands()
    {
        if (ResolveRunway("28R") is not var (rwy, allRunways))
        {
            return;
        }

        const PatternDirection Dir = PatternDirection.Right;
        const AircraftCategory Cat = AircraftCategory.Piston;
        var wp = PatternGeometry.Compute(rwy, Cat, "", 0, Dir, null, null, allRunways, authoredRunway: null);
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);

        // Follower 3.5 nm out on the pattern side, positioned on the reciprocal of the standard
        // 45° entry course so its inbound track IS the 45° entry (AIM 4-3-3) — a normal far
        // entry whose lead-in lies ahead of the aircraft.
        var entry45 = Dir == PatternDirection.Right ? wp.DownwindHeading + 45.0 : wp.DownwindHeading - 45.0;
        var followerPos = GeoMath.ProjectPoint(abeam, entry45.ToReciprocal(), 3.5);
        var follower = MakeVfr(FollowerCallsign, "C172", followerPos, entry45, wp.PatternAltitude + 300, ias: 100);

        var lead = MakeLeadOnStraightInFinal(rwy, wp, allRunways, finalNm: 2.0, ias: 75);
        var lookup = Lookup(lead, follower);
        lead.Phases!.Start(Ctx(lead, rwy, lookup));
        lead.Phases.LandingClearance = ClearanceType.ClearedToLand;

        var erd = DispatchCommand("ERD 28R", follower, lookup);
        Assert.True(erd.Success, $"ERD failed: {erd.Message}");
        Assert.IsType<PatternEntryPhase>(follower.Phases!.CurrentPhase);

        follower.Approach.HasReportedTrafficInSight = true;
        DispatchFollow(follower, LeadCallsign, lookup);
        Assert.True(
            follower.Phases.CurrentPhase is PatternEntryPhase,
            $"Same-runway FOLLOW mid-entry must keep the entry, but the phase is now {follower.Phases.CurrentPhase?.Name}."
        );
        follower.Phases.LandingClearance = ClearanceType.ClearedToLand;

        var run = RunPair(lead, follower, rwy, lookup, maxTicks: 900, dumpName: "follow-pair-mid-entry.json", onTick: null);

        AssertNoAboutFace(run.Follower, rwy, Dir);
        AssertNoOvertake(run, wp);
        AssertLandingOrder(run);
    }

    // ─── 6. (D3) FOLLOW during a wrong-side entry (midfield crossing), same runway ───

    [Fact]
    public void Follow_DuringWrongSideEntry_SameRunway_KeepsCrossing()
    {
        if (ResolveRunway("28R") is not var (rwy, allRunways))
        {
            return;
        }

        const PatternDirection Dir = PatternDirection.Right;
        const AircraftCategory Cat = AircraftCategory.Piston;
        var wp = PatternGeometry.Compute(rwy, Cat, "", 0, Dir, null, null, allRunways, authoredRunway: null);

        // Follower on the WRONG side for right traffic (south of the runway), abeam midfield.
        var midpoint = new LatLon((rwy.ThresholdLatitude + rwy.EndLatitude) / 2.0, (rwy.ThresholdLongitude + rwy.EndLongitude) / 2.0);
        var wrongSide = Dir == PatternDirection.Right ? rwy.TrueHeading - 90.0 : rwy.TrueHeading + 90.0;
        var followerPos = GeoMath.ProjectPoint(midpoint, wrongSide, 1.5);
        var follower = MakeVfr(FollowerCallsign, "C172", followerPos, rwy.TrueHeading.ToReciprocal(), wp.PatternAltitude, ias: 95);

        var lead = MakeLeadOnStraightInFinal(rwy, wp, allRunways, finalNm: 2.5, ias: 75);
        var lookup = Lookup(lead, follower);
        lead.Phases!.Start(Ctx(lead, rwy, lookup));
        lead.Phases.LandingClearance = ClearanceType.ClearedToLand;

        var erd = DispatchCommand("ERD 28R", follower, lookup);
        Assert.True(erd.Success, $"ERD failed: {erd.Message}");
        Assert.IsType<MidfieldCrossingPhase>(follower.Phases!.CurrentPhase);

        follower.Approach.HasReportedTrafficInSight = true;
        DispatchFollow(follower, LeadCallsign, lookup);
        Assert.True(
            follower.Phases.CurrentPhase is MidfieldCrossingPhase,
            $"Same-runway FOLLOW during a wrong-side entry must keep the crossing, but the phase is now {follower.Phases.CurrentPhase?.Name}."
        );
        follower.Phases.LandingClearance = ClearanceType.ClearedToLand;

        var run = RunPair(lead, follower, rwy, lookup, maxTicks: 900, dumpName: "follow-pair-wrongside.json", onTick: null);

        AssertNoOvertake(run, wp);
        AssertLandingOrder(run);
    }

    // ─── 7. Same-leg trail: follower 1 nm behind the lead on the same downwind ───

    [Fact]
    public void Follow_SameLegDownwind_TrailsWithoutOvertake()
    {
        if (ResolveRunway("28R") is not var (rwy, allRunways))
        {
            return;
        }

        const PatternDirection Dir = PatternDirection.Right;
        const AircraftCategory Cat = AircraftCategory.Piston;
        var wp = PatternGeometry.Compute(rwy, Cat, "", 0, Dir, null, null, allRunways, authoredRunway: null);
        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);

        var lead = MakeVfr(
            LeadCallsign,
            "C172",
            GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 1.0),
            wp.DownwindHeading,
            wp.PatternAltitude,
            ias: 90
        );
        AttachCircuit(lead, rwy, Cat, Dir, PatternEntryLeg.Downwind, allRunways);

        var follower = MakeVfr(
            FollowerCallsign,
            "C172",
            GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 2.0),
            wp.DownwindHeading,
            wp.PatternAltitude,
            ias: 90
        );
        follower.Approach.HasReportedTrafficInSight = true;
        AttachCircuit(follower, rwy, Cat, Dir, PatternEntryLeg.Downwind, allRunways);

        var lookup = Lookup(lead, follower);
        lead.Phases!.Start(Ctx(lead, rwy, lookup));
        follower.Phases!.Start(Ctx(follower, rwy, lookup));
        lead.Phases.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.LandingClearance = ClearanceType.ClearedToLand;

        DispatchFollow(follower, LeadCallsign, lookup);

        var run = RunPair(lead, follower, rwy, lookup, maxTicks: 700, dumpName: "follow-pair-sameleg.json", onTick: null);

        AssertNoAboutFace(run.Follower, rwy, Dir);
        AssertNoOvertake(run, wp);
        AssertLandingOrder(run);
    }

    // ─── 8. Follower downwind, lead already on base ───

    [Fact]
    public void Follow_FromDownwind_LeadOnBase_SequencesBehind()
    {
        if (ResolveRunway("28R") is not var (rwy, allRunways))
        {
            return;
        }

        const PatternDirection Dir = PatternDirection.Right;
        const AircraftCategory Cat = AircraftCategory.Piston;
        var wp = PatternGeometry.Compute(rwy, Cat, "", 0, Dir, null, null, allRunways, authoredRunway: null);
        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);

        // Lead mid-base (0.3 nm past the base turn along the base heading), slightly below TPA.
        var lead = MakeVfr(
            LeadCallsign,
            "C172",
            GeoMath.ProjectPoint(baseTurn, wp.BaseHeading, 0.3),
            wp.BaseHeading,
            wp.PatternAltitude - 200,
            ias: 80
        );
        AttachCircuit(lead, rwy, Cat, Dir, PatternEntryLeg.Base, allRunways);

        var follower = MakeVfr(
            FollowerCallsign,
            "C172",
            GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 0.5),
            wp.DownwindHeading,
            wp.PatternAltitude,
            ias: 90
        );
        follower.Approach.HasReportedTrafficInSight = true;
        AttachCircuit(follower, rwy, Cat, Dir, PatternEntryLeg.Downwind, allRunways);

        var lookup = Lookup(lead, follower);
        lead.Phases!.Start(Ctx(lead, rwy, lookup));
        follower.Phases!.Start(Ctx(follower, rwy, lookup));
        lead.Phases.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.LandingClearance = ClearanceType.ClearedToLand;

        DispatchFollow(follower, LeadCallsign, lookup);

        var run = RunPair(lead, follower, rwy, lookup, maxTicks: 700, dumpName: "follow-pair-lead-on-base.json", onTick: null);

        AssertNoAboutFace(run.Follower, rwy, Dir);
        AssertNoOvertake(run, wp);
        AssertLandingOrder(run);
    }

    // ─── 9. Cross-runway: follower in the 28L pattern, lead on final 28R ───

    [Fact]
    public void Follow_CrossRunway_FromDownwind_ResequencesOntoLeadRunway_NoAboutFace()
    {
        if (ResolveRunway("28R") is not var (rwy28R, allRunways))
        {
            return;
        }
        if (ResolveRunway("28L") is not var (rwy28L, _))
        {
            return;
        }

        const AircraftCategory Cat = AircraftCategory.Piston;
        // 28L flies LEFT traffic (south side), 28R flies RIGHT traffic (north side) — the
        // standard split for close parallels so the patterns don't overlap.
        var wpL = PatternGeometry.Compute(rwy28L, Cat, "", 0, PatternDirection.Left, null, null, allRunways, authoredRunway: null);
        var wpR = PatternGeometry.Compute(rwy28R, Cat, "", 0, PatternDirection.Right, null, null, allRunways, authoredRunway: null);
        var baseTurnL = new LatLon(wpL.BaseTurnLat, wpL.BaseTurnLon);

        var follower = MakeVfr(
            FollowerCallsign,
            "C172",
            GeoMath.ProjectPoint(baseTurnL, wpL.DownwindHeading.ToReciprocal(), 1.0),
            wpL.DownwindHeading,
            wpL.PatternAltitude,
            ias: 90
        );
        follower.Approach.HasReportedTrafficInSight = true;
        AttachCircuit(follower, rwy28L, Cat, PatternDirection.Left, PatternEntryLeg.Downwind, allRunways);

        var lead = MakeLeadOnStraightInFinal(rwy28R, wpR, allRunways, finalNm: 3.0, ias: 75);
        var lookup = Lookup(lead, follower);
        lead.Phases!.Start(Ctx(lead, rwy28R, lookup));
        follower.Phases!.Start(Ctx(follower, rwy28L, lookup));
        lead.Phases.LandingClearance = ClearanceType.ClearedToLand;

        DispatchFollow(follower, LeadCallsign, lookup);
        follower.Phases!.LandingClearance = ClearanceType.ClearedToLand;

        var run = RunPair(lead, follower, rwy28R, lookup, maxTicks: 900, dumpName: "follow-pair-crossrunway.json", onTick: null);

        AssertNoOvertake(run, wpR);
        AssertLandingOrder(run);
        // The join must fly 28R's established right circuit (crossing midfield at TPA if
        // needed), never a left circuit whose base leg descends across 28L's final.
        AssertStaysClearOfParallelFinal(run.Follower, rwy28R, rwy28L, wpR.PatternAltitude);
        // The follower must end up on the LEAD's runway: its touchdown point must be within
        // 0.25 nm of the 28R centerline (28L's centerline is ~0.09 nm away from 28R's, so
        // assert against both: closer to 28R than to 28L).
        var touchdown = run.Follower.First(s => s.OnGround).Position;
        double dist28R = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(touchdown, new LatLon(rwy28R.ThresholdLatitude, rwy28R.ThresholdLongitude), rwy28R.TrueHeading)
        );
        double dist28L = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(touchdown, new LatLon(rwy28L.ThresholdLatitude, rwy28L.ThresholdLongitude), rwy28L.TrueHeading)
        );
        Assert.True(dist28R < dist28L, $"Follower touched down closer to 28L ({dist28L:F3} nm) than to 28R ({dist28R:F3} nm) — did not re-sequence.");
    }

    // ─── 10. Fast follower behind a slow lead on final: extends, never cuts in ───

    [Fact]
    public void Follow_FastFollowerSlowLead_NeverCutsInFront()
    {
        if (ResolveRunway("28R") is not var (rwy, allRunways))
        {
            return;
        }

        const PatternDirection Dir = PatternDirection.Right;
        const AircraftCategory FollowerCat = AircraftCategory.Jet;
        var wp = PatternGeometry.Compute(rwy, FollowerCat, "", 0, Dir, null, null, allRunways, authoredRunway: null);
        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);

        // Fast follower (Citation-class jet) on ITS downwind, 0.5 nm before its base turn.
        var follower = MakeVfr(
            FollowerCallsign,
            "C56X",
            GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 0.5),
            wp.DownwindHeading,
            wp.PatternAltitude,
            ias: 140
        );
        follower.Approach.HasReportedTrafficInSight = true;
        AttachCircuit(follower, rwy, FollowerCat, Dir, PatternEntryLeg.Downwind, allRunways);

        // Slow C172 lead on a 3 nm straight-in final.
        var lead = MakeLeadOnStraightInFinal(rwy, wp, allRunways, finalNm: 3.0, ias: 70);
        var lookup = Lookup(lead, follower);
        lead.Phases!.Start(Ctx(lead, rwy, lookup));
        follower.Phases!.Start(Ctx(follower, rwy, lookup));
        lead.Phases.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.LandingClearance = ClearanceType.ClearedToLand;

        DispatchFollow(follower, LeadCallsign, lookup);

        var run = RunPair(lead, follower, rwy, lookup, maxTicks: 900, dumpName: "follow-pair-fast-follower.json", onTick: null);

        AssertNoAboutFace(run.Follower, rwy, Dir);
        AssertNoOvertake(run, wp);
        AssertLandingOrder(run);
        // 7110.65 §3-10-3.a: a Cat III succeeding aircraft (the C56X) gets no reduced
        // same-runway separation — the lead must be on the ground (and clear) before it
        // lands. The harness has no ground graph, so runway-exit can't be modeled; assert
        // the lead is down before the follower's landing phase even begins.
        int followerLandingPhaseTick = run.Follower.First(s => s.Phase == nameof(LandingPhase)).T;
        Assert.True(
            (run.LeadLandedTick > 0) && (run.LeadLandedTick < followerLandingPhaseTick),
            $"Fast follower began its landing phase (t={followerLandingPhaseTick}) before the lead landed (t={run.LeadLandedTick})."
        );
    }

    // ─── 11. Lead lands mid-follow: follower continues its own approach ───

    [Fact]
    public void Follow_LeadLands_FollowerContinuesOwnApproach()
    {
        if (ResolveRunway("28R") is not var (rwy, allRunways))
        {
            return;
        }

        const PatternDirection Dir = PatternDirection.Right;
        const AircraftCategory Cat = AircraftCategory.Piston;
        var wp = PatternGeometry.Compute(rwy, Cat, "", 0, Dir, null, null, allRunways, authoredRunway: null);
        var baseTurn = new LatLon(wp.BaseTurnLat, wp.BaseTurnLon);

        var follower = MakeVfr(
            FollowerCallsign,
            "C172",
            GeoMath.ProjectPoint(baseTurn, wp.DownwindHeading.ToReciprocal(), 0.8),
            wp.DownwindHeading,
            wp.PatternAltitude,
            ias: 90
        );
        follower.Approach.HasReportedTrafficInSight = true;
        AttachCircuit(follower, rwy, Cat, Dir, PatternEntryLeg.Downwind, allRunways);

        // Lead on a short 0.8 nm final — lands within ~40 s of the FOLLOW.
        var lead = MakeLeadOnStraightInFinal(rwy, wp, allRunways, finalNm: 0.8, ias: 65);
        var lookup = Lookup(lead, follower);
        lead.Phases!.Start(Ctx(lead, rwy, lookup));
        follower.Phases!.Start(Ctx(follower, rwy, lookup));
        lead.Phases.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.LandingClearance = ClearanceType.ClearedToLand;

        DispatchFollow(follower, LeadCallsign, lookup);

        var run = RunPair(lead, follower, rwy, lookup, maxTicks: 700, dumpName: "follow-pair-lead-lands.json", onTick: null);

        AssertNoAboutFace(run.Follower, rwy, Dir);
        AssertLandingOrder(run);
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Contains(follower.PendingWarnings, w => w.Contains("on the ground", StringComparison.OrdinalIgnoreCase));
    }
}
