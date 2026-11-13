using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A VFR follower on the same downwind as its C172 lead, KOAK 28R right traffic, both on the C172's circuit (the staging of
/// <see cref="FollowPairTrajectoryTests.Follow_SameLegDownwind_TrailsWithoutOvertake"/>): a follower not too close turns base
/// behind its lead; a faster follower that cannot slow to its lead breaks the follow off with "unable to maintain separation";
/// behind a lead the controller extended (EXT) the follower keeps the follow and advises once past
/// <see cref="AirborneFollowHelper.MaxFollowExtensionNm"/>, and once that lead is turned base its
/// <see cref="VfrFollowPhase.BaseExtensionLimitNm"/> give-up counts from where it then is; behind a lead holding its downwind
/// for its own follow it gives the follow up <see cref="VfrFollowPhase.BaseExtensionLimitNm"/> past its base point.
/// </summary>
[Collection("NavDbMutator")]
public class FollowSameDownwindExtensionTests
{
    private const string LeadCallsign = "N100AA";
    private const string FollowerCallsign = "N200BB";
    private const string LeadsLeadCallsign = "N300CC";
    private const string LeadType = "C172";
    private const string FastFollowerType = "PC12";
    private const PatternDirection Dir = PatternDirection.Right;
    private const AircraftCategory Cat = AircraftCategory.Piston;

    private static readonly string UnableToFollow = $"unable to follow {LeadCallsign}, extending downwind, request base turn.";
    private static readonly string ExtensionAdvisory = $"extending downwind behind {LeadCallsign}, unable to turn — request instructions.";

    private readonly ITestOutputHelper _output;

    public FollowSameDownwindExtensionTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("DownwindPhase", LogLevel.Debug)
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .InitializeSimLog();
    }

    /// <summary>
    /// A same-downwind follower not too close (at least half the desired distance behind at its floor) keeps the follow, never
    /// gives it up, and turns base after its lead.
    /// </summary>
    [Fact]
    public void NotTooCloseBehindSameDownwindLead_KeepsFollowAndTurnsBaseBehind()
    {
        if (Stage(leadShortOfBaseNm: 1.0, followerBehindLeadNm: 1.0, followerType: LeadType) is not { } s)
        {
            return;
        }

        double desiredNm = AirborneFollowHelper.PatternSpacingNm(s.Lead);
        Assert.True(GeoMath.DistanceNm(s.Follower.Position, s.Lead.Position) >= 0.5 * desiredNm);
        DispatchFollow(s.Follower, LeadCallsign, s);

        int leadBaseAt = -1;
        int followerBaseAt = -1;
        for (int t = 1; (t <= 700) && (followerBaseAt < 0); t++)
        {
            s.Tick();
            if (!s.Lead.IsOnGround)
            {
                // The lead landing ends the follow by design (the lead-lifecycle watchdog).
                Assert.Equal(LeadCallsign, s.Follower.Approach.FollowingCallsign);
            }
            if ((leadBaseAt < 0) && (s.Lead.Phases?.CurrentPhase is BasePhase))
            {
                leadBaseAt = t;
            }
            if (s.Follower.Phases?.CurrentPhase is BasePhase)
            {
                followerBaseAt = t;
            }
        }

        _output.WriteLine($"lead base t={leadBaseAt}; follower base t={followerBaseAt}");
        Assert.True(leadBaseAt > 0, "the lead never turned base");
        Assert.True(followerBaseAt > leadBaseAt, $"follower turned base at t={followerBaseAt}, lead at t={leadBaseAt}");
        Assert.DoesNotContain(s.Follower.PendingWarnings, w => w.Contains("unable to", StringComparison.Ordinal));
    }

    /// <summary>
    /// A follower whose approach speed outruns its C172 lead by more than <see cref="AirborneFollowHelper.StructuralOvertakeMarginKts"/>,
    /// too close behind it on the same downwind, cannot slow to it: it breaks the follow off with "unable to maintain
    /// separation" rather than overrunning silently, and never turns base ahead of the C172.
    /// </summary>
    [Fact]
    public void FasterFollowerTooCloseBehindSameDownwindLead_BreaksOffUnableToMaintainSeparation()
    {
        if (Stage(leadShortOfBaseNm: 3.0, followerBehindLeadNm: 0.8, followerType: FastFollowerType) is not { } s)
        {
            return;
        }

        double followerVref = AircraftPerformance.ApproachSpeed(FastFollowerType, AircraftCategorization.Categorize(FastFollowerType));
        double leadVref = AircraftPerformance.ApproachSpeed(LeadType, Cat);
        _output.WriteLine($"{FastFollowerType} approach {followerVref:F0} kt; {LeadType} approach {leadVref:F0} kt");
        Assert.True(followerVref > leadVref + AirborneFollowHelper.StructuralOvertakeMarginKts, "Setup: the follower must outrun the lead");
        DispatchFollow(s.Follower, LeadCallsign, s);

        int? brokeOffAt = null;
        for (int t = 1; t <= 600; t++)
        {
            s.Tick();
            if ((t % 10) == 0)
            {
                _output.WriteLine(
                    $"t={t} gap={GeoMath.DistanceNm(s.Follower.Position, s.Lead.Position):F2} fIas={s.Follower.IndicatedAirspeed:F0} "
                        + $"lIas={s.Lead.IndicatedAirspeed:F0} f={s.Follower.Phases?.CurrentPhase?.Name} l={s.Lead.Phases?.CurrentPhase?.Name}"
                );
            }
            Assert.False(
                (s.Follower.Phases?.CurrentPhase is BasePhase) && (s.Lead.Phases?.CurrentPhase is DownwindPhase),
                $"t={t}: the follower turned base ahead of its lead"
            );
            if ((brokeOffAt is null) && (s.Follower.Approach.FollowingCallsign is null))
            {
                brokeOffAt = t;
            }
        }

        Assert.NotNull(brokeOffAt);
        Assert.Contains(s.Follower.PendingWarnings, w => w.Contains($"unable to maintain separation from {LeadCallsign}", StringComparison.Ordinal));
    }

    /// <summary>
    /// Behind a lead the controller extended (EXT) on the same downwind the follower keeps the follow: no call between
    /// <see cref="VfrFollowPhase.BaseExtensionLimitNm"/> and <see cref="AirborneFollowHelper.MaxFollowExtensionNm"/> past its
    /// base point, then the "extending downwind … unable to turn" advisory once, never "unable to follow".
    /// </summary>
    [Fact]
    public void ExtendedLead_FollowerKeepsFollow_AdvisesOncePastTheExtensionCap()
    {
        if (Stage(leadShortOfBaseNm: 1.0, followerBehindLeadNm: 1.0, followerType: LeadType) is not { } s)
        {
            return;
        }

        DispatchFollow(s.Follower, LeadCallsign, s);
        ExtendDownwind(s.Lead);

        bool sampledBetween = false;
        double pastBaseNm = double.NegativeInfinity;
        for (int t = 1; (t <= 900) && (pastBaseNm < AirborneFollowHelper.MaxFollowExtensionNm + 0.5); t++)
        {
            s.Tick();
            Assert.Equal(LeadCallsign, s.Follower.Approach.FollowingCallsign);
            pastBaseNm = PastBaseNm(s.Follower);
            if ((pastBaseNm >= VfrFollowPhase.BaseExtensionLimitNm) && (pastBaseNm < AirborneFollowHelper.MaxFollowExtensionNm))
            {
                sampledBetween = true;
                Assert.DoesNotContain(s.Follower.PendingWarnings, w => w.Contains("unable to", StringComparison.Ordinal));
            }
        }

        _output.WriteLine($"follower {pastBaseNm:F2} nm past its base point");
        Assert.True(sampledBetween, "the follower was never sampled between the give-up limit and the extension cap");
        Assert.True(pastBaseNm >= AirborneFollowHelper.MaxFollowExtensionNm + 0.5, "the follower never passed the extension cap");
        Assert.Equal(LeadCallsign, s.Follower.Approach.FollowingCallsign);
        string advisory = Assert.Single(s.Follower.PendingWarnings, w => w.Contains("unable to", StringComparison.Ordinal));
        Assert.Equal(ExtensionAdvisory, advisory);
    }

    /// <summary>
    /// A lead the controller extended (EXT) and then turns base (TB) while the follower is 3 nm past its base point: no call on
    /// that tick, and with the lead flying its base the follower turns base behind it before the give-up. The restart point
    /// survives a snapshot.
    /// </summary>
    [Fact]
    public void ExtendedLeadTurnedBase_GiveUpCountsFromWhereTheFollowerWas()
    {
        if (Stage(leadShortOfBaseNm: 1.0, followerBehindLeadNm: 1.0, followerType: LeadType) is not { } s)
        {
            return;
        }

        DispatchFollow(s.Follower, LeadCallsign, s);
        ExtendDownwind(s.Lead);
        for (int t = 1; (t <= 900) && (PastBaseNm(s.Follower) < 3.0); t++)
        {
            s.Tick();
        }

        double markPastNm = PastBaseNm(s.Follower);
        Assert.True(markPastNm >= 3.0, "the follower never reached 3 nm past its base point");
        Assert.DoesNotContain(s.Follower.PendingWarnings, w => w.Contains("unable to", StringComparison.Ordinal));

        Dispatch(s.Lead, "TB", s);
        s.Tick();
        Assert.IsType<BasePhase>(s.Lead.Phases?.CurrentPhase);
        Assert.Equal(LeadCallsign, s.Follower.Approach.FollowingCallsign);
        Assert.DoesNotContain(s.Follower.PendingWarnings, w => w.Contains("unable to follow", StringComparison.Ordinal));

        var dto = (DownwindPhaseDto)Assert.IsType<DownwindPhase>(s.Follower.Phases?.CurrentPhase).ToSnapshot();
        Assert.NotNull(dto.LastExtendedLeadAlongTrackNm);
        var restored = (DownwindPhaseDto)DownwindPhase.FromSnapshot(dto).ToSnapshot();
        Assert.Equal(dto.LastExtendedLeadAlongTrackNm, restored.LastExtendedLeadAlongTrackNm);

        double? gaveUpPastNm = null;
        for (int t = 1; (t <= 900) && (s.Follower.Phases?.CurrentPhase is DownwindPhase) && (gaveUpPastNm is null); t++)
        {
            s.Tick();
            if (s.Follower.PendingWarnings.Any(w => w.Contains(UnableToFollow, StringComparison.Ordinal)))
            {
                gaveUpPastNm = PastBaseNm(s.Follower);
            }
        }

        _output.WriteLine($"lead turned base with the follower {markPastNm:F2} nm past; gave up at {gaveUpPastNm:F2}");
        Assert.Null(gaveUpPastNm);
        Assert.IsType<BasePhase>(s.Follower.Phases?.CurrentPhase);
    }

    /// <summary>
    /// Behind an extended (EXT) lead, the follower <paramref name="markPastNm"/> past its base point makes a right 360 and the
    /// lead is turned base (TB) during the turn: the resumed downwind keeps the give-up's restart mark, so no "unable to follow"
    /// before mark + <see cref="VfrFollowPhase.BaseExtensionLimitNm"/>, and keeps the advisory latch, so the "extending
    /// downwind … unable to turn" advisory is voiced at most once.
    /// </summary>
    [Theory]
    [InlineData(3.0)]
    [InlineData(AirborneFollowHelper.MaxFollowExtensionNm + 0.5)]
    public void ExtendedLeadTurnedBaseDuring360_GiveUpCountsFromTheMark(double markPastNm)
    {
        if (StageBehindExtendedLead(markPastNm) is not { } s)
        {
            return;
        }

        double mark = PastBaseNm(s.Follower);
        Dispatch(s.Follower, "R360", s);
        s.Tick();
        Assert.IsNotType<DownwindPhase>(s.Follower.Phases?.CurrentPhase);
        Dispatch(s.Lead, "TB", s);
        for (int t = 1; (t <= 300) && (s.Follower.Phases?.CurrentPhase is not DownwindPhase); t++)
        {
            s.Tick();
        }

        Assert.IsType<DownwindPhase>(s.Follower.Phases?.CurrentPhase);
        Assert.Equal(LeadCallsign, s.Follower.Approach.FollowingCallsign);
        AssertNoGiveUpBeforeMark(s, mark);
        Assert.True(AdvisoryCount(s.Follower) <= 1, $"the extension advisory was voiced {AdvisoryCount(s.Follower)} times");
    }

    /// <summary>
    /// Behind an extended (EXT) lead turned base (TB) with the follower 3 nm past its base point, an <c>MRT</c> that keeps the
    /// runway and the side rebuilds the downwind with the give-up's restart mark: no "unable to follow" before mark +
    /// <see cref="VfrFollowPhase.BaseExtensionLimitNm"/>.
    /// </summary>
    [Fact]
    public void ExtendedLeadTurnedBase_SameSidePatternRebuild_GiveUpCountsFromTheMark()
    {
        if (StageBehindExtendedLead(3.0) is not { } s)
        {
            return;
        }

        double mark = PastBaseNm(s.Follower);
        Dispatch(s.Lead, "TB", s);
        s.Tick();
        Assert.IsType<BasePhase>(s.Lead.Phases?.CurrentPhase);
        Dispatch(s.Follower, "MRT", s);
        Assert.IsType<DownwindPhase>(s.Follower.Phases?.CurrentPhase);
        Assert.Equal(LeadCallsign, s.Follower.Approach.FollowingCallsign);
        AssertNoGiveUpBeforeMark(s, mark);
    }

    /// <summary>
    /// A lead holding its downwind past its own base point for its own follow (behind an extended third aircraft), not
    /// extended itself: the follower gives the follow up <see cref="VfrFollowPhase.BaseExtensionLimitNm"/> past its base point.
    /// </summary>
    [Fact]
    public void LeadHoldingForItsOwnFollow_FollowerGivesUpPastTheLimit()
    {
        if (Stage(leadShortOfBaseNm: 1.0, followerBehindLeadNm: 1.0, followerType: LeadType) is not { } s)
        {
            return;
        }

        AircraftState leadsLead = s.Add(LeadsLeadCallsign, LeadType, shortOfBaseNm: -0.5);
        ExtendDownwind(leadsLead);
        DispatchFollow(s.Lead, LeadsLeadCallsign, s);
        DispatchFollow(s.Follower, LeadCallsign, s);

        double? gaveUpPastNm = null;
        for (int t = 1; (t <= 900) && (gaveUpPastNm is null) && (PastBaseNm(s.Follower) < 2.5); t++)
        {
            s.Tick();
            if (s.Follower.PendingWarnings.Any(w => w.Contains(UnableToFollow, StringComparison.Ordinal)))
            {
                gaveUpPastNm = PastBaseNm(s.Follower);
            }
        }

        DownwindPhase leadLeg = Assert.IsType<DownwindPhase>(s.Lead.Phases?.CurrentPhase);
        Assert.False(leadLeg.IsExtended, "Setup: the lead holds for its own follow, not an EXT");
        Assert.True(leadLeg.HasReachedBaseTurnPoint(s.Lead.Position), "Setup: the lead holds past its own base point");
        _output.WriteLine($"gave up at {gaveUpPastNm:F2} nm past the base point");
        Assert.NotNull(gaveUpPastNm);
        Assert.InRange(gaveUpPastNm.Value, VfrFollowPhase.BaseExtensionLimitNm, 2.5);
        Assert.Null(s.Follower.Approach.FollowingCallsign);
        Assert.True(Assert.IsType<DownwindPhase>(s.Follower.Phases?.CurrentPhase).IsExtended);
    }

    // ─── Harness (the FollowPairTrajectoryTests staging) ───

    /// <summary>The aircraft of one staging, ticked in the order they were added, and the lookup they share.</summary>
    private sealed class Traffic(RunwayInfo rwy, IReadOnlyList<RunwayInfo> allRunways)
    {
        private readonly List<AircraftState> _all = [];

        public RunwayInfo Rwy { get; } = rwy;
        public AircraftState Lead => _all[0];
        public AircraftState Follower => _all[1];

        public AircraftState? Lookup(string callsign) => _all.Find(ac => ac.Callsign == callsign);

        /// <summary>
        /// Adds an aircraft on the C172's downwind, <paramref name="shortOfBaseNm"/> short of its base point (negative: past
        /// it), with its circuit started and cleared to land.
        /// </summary>
        public AircraftState Add(string callsign, string type, double shortOfBaseNm)
        {
            PatternWaypoints wp = Waypoints(Rwy, Cat, allRunways);
            LatLon pos = GeoMath.ProjectPoint(new LatLon(wp.BaseTurnLat, wp.BaseTurnLon), wp.DownwindHeading.ToReciprocal(), shortOfBaseNm);
            AircraftState ac = MakeVfr(callsign, type, pos, wp);
            AttachCircuit(ac, Rwy, allRunways);
            _all.Add(ac);
            ac.Phases!.Start(Ctx(ac));
            ac.Phases.LandingClearance = ClearanceType.ClearedToLand;
            return ac;
        }

        public void Tick()
        {
            foreach (AircraftState ac in _all)
            {
                PhaseContext ctx = Ctx(ac);
                FlightPhysics.Update(ac, ctx.DeltaSeconds);
                PhaseRunner.Tick(ac, ctx);
            }
        }

        public PhaseContext Ctx(AircraftState ac) =>
            new()
            {
                ActiveRunways = ActiveRunways.Empty,
                Aircraft = ac,
                Targets = ac.Targets,
                Category = AircraftCategorization.Categorize(ac.AircraftType),
                DeltaSeconds = 1.0,
                Runway = Rwy,
                FieldElevation = Rwy.ElevationFt,
                AircraftLookup = Lookup,
                Logger = NullLogger.Instance,
            };
    }

    /// <summary>
    /// A C172 lead <paramref name="leadShortOfBaseNm"/> short of its base point and a <paramref name="followerType"/> follower
    /// <paramref name="followerBehindLeadNm"/> behind it, both on the C172's downwind; null when KOAK 28R is not in navdata.
    /// </summary>
    private Traffic? Stage(double leadShortOfBaseNm, double followerBehindLeadNm, string followerType)
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? rwy = navDb?.GetRunway("KOAK", "28R");
        if ((navDb is null) || (rwy is null))
        {
            _output.WriteLine("KOAK 28R not in navdata — skipping.");
            return null;
        }

        var traffic = new Traffic(rwy, navDb.GetRunways("KOAK"));
        traffic.Add(LeadCallsign, LeadType, leadShortOfBaseNm);
        AircraftState follower = traffic.Add(FollowerCallsign, followerType, leadShortOfBaseNm + followerBehindLeadNm);
        follower.Approach.HasReportedTrafficInSight = true;
        return traffic;
    }

    /// <summary>
    /// The default staging with the follower following its lead and the lead extended (EXT), ticked until the follower is
    /// <paramref name="pastBaseNm"/> past its base point with no "unable to" call yet unless past the extension cap.
    /// </summary>
    private Traffic? StageBehindExtendedLead(double pastBaseNm)
    {
        if (Stage(leadShortOfBaseNm: 1.0, followerBehindLeadNm: 1.0, followerType: LeadType) is not { } s)
        {
            return null;
        }

        DispatchFollow(s.Follower, LeadCallsign, s);
        ExtendDownwind(s.Lead);
        for (int t = 1; (t <= 900) && (PastBaseNm(s.Follower) < pastBaseNm); t++)
        {
            s.Tick();
        }

        Assert.True(PastBaseNm(s.Follower) >= pastBaseNm, $"the follower never reached {pastBaseNm:F1} nm past its base point");
        Assert.DoesNotContain(s.Follower.PendingWarnings, w => w.Contains("unable to follow", StringComparison.Ordinal));
        Assert.Equal(pastBaseNm >= AirborneFollowHelper.MaxFollowExtensionNm ? 1 : 0, AdvisoryCount(s.Follower));
        return s;
    }

    /// <summary>
    /// Ticks while the follower stays on its downwind, up to <paramref name="mark"/> + <see cref="VfrFollowPhase.BaseExtensionLimitNm"/>
    /// + 0.5 nm past its base point, and asserts it did not give the follow up before mark + the limit.
    /// </summary>
    private void AssertNoGiveUpBeforeMark(Traffic s, double mark)
    {
        double limitPastNm = mark + VfrFollowPhase.BaseExtensionLimitNm;
        double? gaveUpPastNm = null;
        string end = "";
        for (int t = 1; (t <= 900) && (gaveUpPastNm is null); t++)
        {
            s.Tick();
            if (s.Follower.Phases?.CurrentPhase is not DownwindPhase)
            {
                end = $"left the downwind for {s.Follower.Phases?.CurrentPhase?.Name} at t={t}";
                break;
            }
            if (s.Follower.PendingWarnings.Any(w => w.Contains(UnableToFollow, StringComparison.Ordinal)))
            {
                gaveUpPastNm = PastBaseNm(s.Follower);
            }
            else if (PastBaseNm(s.Follower) >= limitPastNm + 0.5)
            {
                end = $"still following {PastBaseNm(s.Follower):F2} nm past at t={t}";
                break;
            }
        }

        _output.WriteLine($"mark {mark:F2} nm past; gave up at {gaveUpPastNm:F2}; {end}");
        Assert.True((gaveUpPastNm is null) || (gaveUpPastNm >= limitPastNm), $"gave up {gaveUpPastNm:F2} nm past, mark {mark:F2}");
    }

    private static int AdvisoryCount(AircraftState ac) => ac.PendingWarnings.Count(w => w.Contains(ExtensionAdvisory, StringComparison.Ordinal));

    /// <summary>Along-track distance (nm) of <paramref name="ac"/> past the base point of the downwind it flies.</summary>
    private static double PastBaseNm(AircraftState ac)
    {
        PatternWaypoints wp = Assert.IsType<DownwindPhase>(ac.Phases?.CurrentPhase).Waypoints ?? throw new InvalidOperationException("no waypoints");
        var threshold = new LatLon(wp.ThresholdLat, wp.ThresholdLon);
        return GeoMath.AlongTrackDistanceNm(ac.Position, threshold, wp.DownwindHeading)
            - GeoMath.AlongTrackDistanceNm(new LatLon(wp.BaseTurnLat, wp.BaseTurnLon), threshold, wp.DownwindHeading);
    }

    private static void ExtendDownwind(AircraftState ac) => Assert.IsType<DownwindPhase>(ac.Phases?.CurrentPhase).IsExtended = true;

    private static PatternWaypoints Waypoints(RunwayInfo rwy, AircraftCategory cat, IReadOnlyList<RunwayInfo> allRunways) =>
        PatternGeometry.Compute(
            new PatternGeometryRequest(rwy, cat, "", 0, Dir)
            {
                SizeOverrideNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = allRunways,
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );

    private static AircraftState MakeVfr(string callsign, string type, LatLon pos, PatternWaypoints wp) =>
        new()
        {
            Callsign = callsign,
            AircraftType = type,
            Position = pos,
            TrueHeading = wp.DownwindHeading,
            TrueTrack = wp.DownwindHeading,
            Altitude = wp.PatternAltitude,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", FlightRules = "VFR" },
            Approach = new AircraftApproachState(),
        };

    private static void AttachCircuit(AircraftState ac, RunwayInfo rwy, IReadOnlyList<RunwayInfo> allRunways)
    {
        List<Phase> circuit = PatternBuilder.BuildCircuit(
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
        ac.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            TrafficDirection = Dir,
            PatternRunway = rwy,
        };
        foreach (Phase p in circuit)
        {
            ac.Phases.Add(p);
        }
    }

    private static void DispatchFollow(AircraftState follower, string target, Traffic traffic)
    {
        CommandDispatcher.Dispatch(
            new ReportTrafficInSightForcedCommand(target),
            follower,
            TestDispatch.Context(Random.Shared, findAircraft: traffic.Lookup)
        );
        CommandResult result = CommandDispatcher.Dispatch(
            new FollowCommand(target, false),
            follower,
            TestDispatch.Context(Random.Shared, findAircraft: traffic.Lookup)
        );
        Assert.True(result.Success, $"FOLLOW failed: {result.Message}");
        Assert.Equal(target, follower.Approach.FollowingCallsign);
    }

    private static void Dispatch(AircraftState ac, string command, Traffic traffic)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command, ac.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, $"Parse of '{command}' failed: {parsed.Reason}");
        Assert.NotNull(parsed.Value);
        CommandResult result = CommandDispatcher.DispatchCompound(
            parsed.Value,
            ac,
            TestDispatch.Context(Random.Shared, findAircraft: traffic.Lookup)
        );
        Assert.True(result.Success, $"{command} failed: {result.Message}");
    }
}
