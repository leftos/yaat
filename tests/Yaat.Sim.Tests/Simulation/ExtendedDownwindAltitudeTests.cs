using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The downwind altitude profile at KOAK 28R, right traffic, C172 (AIM FIG 4-3-2 key 2; AC 90-66B §11.5 and Appendix A
/// key 2): pattern altitude is held to abeam the approach end, a held or extended downwind keeps its altitude until the
/// base turn, and a normal downwind splits the descent to the base-to-final rollout with the base leg along one constant
/// ground gradient. Every assertion measures the flown altitude, not the phase's own target.
/// </summary>
[Collection("NavDbMutator")]
public class ExtendedDownwindAltitudeTests
{
    private const string FollowerCallsign = "N342T";
    private const string LeadCallsign = "N52417";
    private const PatternDirection Dir = PatternDirection.Right;
    private const AircraftCategory Cat = AircraftCategory.Piston;

    private readonly ITestOutputHelper _output;

    public ExtendedDownwindAltitudeTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private sealed record Koak28R(RunwayInfo Runway, IReadOnlyList<RunwayInfo> AllRunways, PatternWaypoints Waypoints)
    {
        public LatLon Threshold => new(Waypoints.ThresholdLat, Waypoints.ThresholdLon);

        public LatLon Abeam => new(Waypoints.DownwindAbeamLat, Waypoints.DownwindAbeamLon);

        public double AlongTrack(LatLon pos) => GeoMath.AlongTrackDistanceNm(pos, Threshold, Waypoints.DownwindHeading);

        public double AbeamAlongTrack => AlongTrack(Abeam);

        public double BaseTriggerAlongTrack =>
            AlongTrack(new LatLon(Waypoints.BaseTurnLat, Waypoints.BaseTurnLon)) - DownwindPhase.AlongTrackToleranceNm;
    }

    private static Koak28R ResolveKoak28R()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        Assert.NotNull(navDb);
        RunwayInfo? rwy = navDb.GetRunway("KOAK", "28R");
        Assert.NotNull(rwy);
        IReadOnlyList<RunwayInfo> all = navDb.GetRunways("KOAK");
        PatternWaypoints wp = PatternGeometry.Compute(rwy, Cat, "", 0, Dir, null, null, all, authoredRunway: null);
        return new Koak28R(rwy, all, wp);
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

    private static AircraftState MakeC172(string callsign, LatLon pos, TrueHeading heading, double altitude, double ias) =>
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

    private static void AttachCircuit(AircraftState ac, Koak28R koak, PatternEntryLeg entry)
    {
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            koak.Runway,
            Cat,
            "",
            0,
            Dir,
            entry,
            false,
            null,
            null,
            null,
            koak.AllRunways,
            authoredRunway: null
        );
        ac.Phases = new PhaseList
        {
            AssignedRunway = koak.Runway,
            TrafficDirection = Dir,
            PatternRunway = koak.Runway,
        };
        foreach (Phase p in circuit)
        {
            ac.Phases.Add(p);
        }
    }

    /// <summary>A C172 on the right downwind for 28R, <paramref name="nmBeforeAbeam"/> short of abeam, level at TPA,
    /// its circuit started and cleared to land.</summary>
    private static AircraftState MakeDownwindFollower(Koak28R koak, double nmBeforeAbeam, Func<string, AircraftState?> lookup)
    {
        LatLon pos = GeoMath.ProjectPoint(koak.Abeam, koak.Waypoints.DownwindHeading.ToReciprocal(), nmBeforeAbeam);
        AircraftState ac = MakeC172(FollowerCallsign, pos, koak.Waypoints.DownwindHeading, koak.Waypoints.PatternAltitude, ias: 90);
        AttachCircuit(ac, koak, PatternEntryLeg.Downwind);
        ac.Phases!.Start(Ctx(ac, koak.Runway, lookup));
        ac.Phases.LandingClearance = ClearanceType.ClearedToLand;
        return ac;
    }

    private static AircraftState MakeLeadOnStraightInFinal(Koak28R koak, double finalNm, double ias)
    {
        LatLon pos = GeoMath.ProjectPoint(koak.Threshold, koak.Waypoints.FinalHeading.ToReciprocal(), finalNm);
        AircraftState lead = MakeC172(LeadCallsign, pos, koak.Waypoints.FinalHeading, koak.Runway.ElevationFt + (finalNm * 318.0), ias);
        AttachCircuit(lead, koak, PatternEntryLeg.Final);
        return lead;
    }

    private static CommandResult DispatchCommand(string command, AircraftState ac, Func<string, AircraftState?> lookup)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command, ac.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, $"Parse of '{command}' failed: {parsed.Reason}");
        return CommandDispatcher.DispatchCompound(parsed.Value!, ac, TestDispatch.Context(Random.Shared, findAircraft: lookup));
    }

    private static void Tick(AircraftState ac, RunwayInfo rwy, Func<string, AircraftState?> lookup)
    {
        PhaseContext ctx = Ctx(ac, rwy, lookup);
        FlightPhysics.Update(ac, ctx.DeltaSeconds);
        PhaseRunner.Tick(ac, ctx);
    }

    /// <summary>Lowest flown altitude while on the downwind, and how far past abeam the leg was held.</summary>
    private sealed class DownwindTrace(Koak28R koak)
    {
        public double MinAltitude { get; private set; } = double.PositiveInfinity;
        public int MinTick { get; private set; } = -1;
        public double MinAlongFromAbeam { get; private set; }
        public double? MinTarget { get; private set; }
        public double MaxAlongTrack { get; private set; } = double.NegativeInfinity;

        public void Record(int t, AircraftState ac)
        {
            if (ac.Phases?.CurrentPhase is not DownwindPhase)
            {
                return;
            }
            double along = koak.AlongTrack(ac.Position);
            MaxAlongTrack = Math.Max(MaxAlongTrack, along);
            if (ac.Altitude < MinAltitude)
            {
                MinAltitude = ac.Altitude;
                MinTick = t;
                MinAlongFromAbeam = along - koak.AbeamAlongTrack;
                MinTarget = ac.Targets.TargetAltitude;
            }
        }

        public void AssertHeldAtPatternAltitude()
        {
            double tpa = koak.Waypoints.PatternAltitude;
            Assert.True(
                MaxAlongTrack > koak.BaseTriggerAlongTrack + 0.2,
                $"Setup: the downwind was not held past the base trigger (max along-track {MaxAlongTrack:F2} nm, "
                    + $"trigger {koak.BaseTriggerAlongTrack:F2} nm)."
            );
            Assert.True(
                MinAltitude >= tpa - 100,
                $"Held downwind sank to {MinAltitude:F0} ft MSL at t={MinTick} (along-track {MinAlongFromAbeam:F2} nm from abeam, "
                    + $"target {MinTarget:F0}); TPA {tpa:F0}."
            );
        }
    }

    [Fact]
    public void ExtendedDownwind_HoldsPatternAltitude_UntilBaseTurn()
    {
        Koak28R koak = ResolveKoak28R();
        AircraftState? self = null;
        AircraftState? lookup(string cs) => (cs == FollowerCallsign) ? self : null;
        AircraftState ac = MakeDownwindFollower(koak, nmBeforeAbeam: 0.5, lookup);
        self = ac;

        CommandResult ext = DispatchCommand("EXT", ac, lookup);
        Assert.True(ext.Success, $"EXT failed: {ext.Message}");

        var trace = new DownwindTrace(koak);
        for (int t = 1; t <= 120; t++)
        {
            Tick(ac, koak.Runway, lookup);
            trace.Record(t, ac);
        }

        trace.AssertHeldAtPatternAltitude();
    }

    [Fact]
    public void Follow_HeldDownwindBehindStraightInLead_HoldsPatternAltitude()
    {
        Koak28R koak = ResolveKoak28R();
        AircraftState? follower = null;
        AircraftState lead = MakeLeadOnStraightInFinal(koak, finalNm: 2.0, ias: 62);
        AircraftState? lookup(string cs) =>
            (cs == LeadCallsign) ? lead
            : (cs == FollowerCallsign) ? follower
            : null;
        lead.Phases!.Start(Ctx(lead, koak.Runway, lookup));
        lead.Phases.LandingClearance = ClearanceType.ClearedToLand;
        follower = MakeDownwindFollower(koak, nmBeforeAbeam: 0.2, lookup);

        CommandDispatcher.Dispatch(
            new ReportTrafficInSightForcedCommand(LeadCallsign),
            follower,
            TestDispatch.Context(Random.Shared, findAircraft: lookup)
        );
        CommandResult follow = CommandDispatcher.Dispatch(
            new FollowCommand(LeadCallsign, false),
            follower,
            TestDispatch.Context(Random.Shared, findAircraft: lookup)
        );
        Assert.True(follow.Success, $"FOLLOW failed: {follow.Message}");
        Assert.Equal(LeadCallsign, follower.Approach.FollowingCallsign);

        var trace = new DownwindTrace(koak);
        for (int t = 1; t <= 300; t++)
        {
            Tick(lead, koak.Runway, lookup);
            Tick(follower, koak.Runway, lookup);
            trace.Record(t, follower);
        }

        trace.AssertHeldAtPatternAltitude();
    }

    [Fact]
    public void NormalDownwind_HoldsTpaUntilAbeam()
    {
        Koak28R koak = ResolveKoak28R();
        AircraftState? self = null;
        AircraftState? lookup(string cs) => (cs == FollowerCallsign) ? self : null;
        AircraftState ac = MakeDownwindFollower(koak, nmBeforeAbeam: 0.5, lookup);
        self = ac;
        double tpa = koak.Waypoints.PatternAltitude;

        bool reachedAbeam = false;
        for (int t = 1; t <= 120; t++)
        {
            Tick(ac, koak.Runway, lookup);
            double fromAbeam = koak.AlongTrack(ac.Position) - koak.AbeamAlongTrack;
            if (fromAbeam >= 0)
            {
                reachedAbeam = true;
                break;
            }
            Assert.True(
                ac.Altitude >= tpa - 10,
                $"t={t}: descended to {ac.Altitude:F0} ft MSL {-fromAbeam:F2} nm before abeam (TPA {tpa:F0}, target "
                    + $"{ac.Targets.TargetAltitude:F0}) — pattern altitude is held to abeam the approach end (AC 90-66B §11.5)."
            );
        }

        Assert.True(reachedAbeam, "Setup: the aircraft never reached abeam the approach end.");
    }

    [Fact]
    public void NormalDownwind_SplitsDescentWithBase()
    {
        Koak28R koak = ResolveKoak28R();
        AircraftState? self = null;
        AircraftState? lookup(string cs) => (cs == FollowerCallsign) ? self : null;
        AircraftState ac = MakeDownwindFollower(koak, nmBeforeAbeam: 0.5, lookup);
        self = ac;
        PatternWaypoints wp = koak.Waypoints;
        double tpa = wp.PatternAltitude;

        // The rollout point BasePhase plans its descent to when it starts at the base trigger: the trigger's distance
        // out (floored at one turn radius) plus one turn radius.
        double turnRadiusNm = BasePhase.TurnRadiusNm(BasePhase.PlannedSpeedKt(ac, Cat), Cat);
        double rolloutDistNm = Math.Max(koak.BaseTriggerAlongTrack, turnRadiusNm) + turnRadiusNm;
        double rolloutAlt = GlideSlopeGeometry.AltitudeAtDistance(rolloutDistNm, koak.Runway.ElevationFt, Cat);
        // The distance BasePhase descends over: the downwind-to-base arc, then the straight base to the start of the
        // turn to final one turn radius from the centerline.
        double baseXtrackNm = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(new LatLon(wp.BaseTurnLat, wp.BaseTurnLon), koak.Threshold, wp.FinalHeading)
        );
        double baseLen = (Math.PI / 2.0 * turnRadiusNm) + Math.Max(baseXtrackNm - (2.0 * turnRadiusNm), 0);
        double dwDescentLen = koak.BaseTriggerAlongTrack - koak.AbeamAlongTrack;
        double drop = tpa - rolloutAlt;
        double expected = rolloutAlt + (drop * baseLen / (dwDescentLen + baseLen));

        (double Alt, double Along)? abeam = null;
        (double Alt, double Along)? baseStart = null;
        double? baseEndAlt = null;
        double baseDistNm = 0;
        bool baseDescending = false;
        LatLon prev = ac.Position;
        bool wentAround = false;
        int landedTick = -1;
        for (int t = 1; t <= 600; t++)
        {
            Tick(ac, koak.Runway, lookup);
            Phase? current = ac.Phases?.CurrentPhase;
            double along = koak.AlongTrack(ac.Position);
            if ((abeam is null) && (along >= koak.AbeamAlongTrack))
            {
                abeam = (ac.Altitude, along);
            }
            if ((current is BasePhase) && (baseStart is null))
            {
                baseStart = (ac.Altitude, along);
                _output.WriteLine(
                    $"t={t}: base started at {ac.Altitude:F0} ft MSL (expected {expected:F0}, rollout {rolloutAlt:F0}, TPA {tpa:F0}, "
                        + $"baseLen {baseLen:F2} nm)"
                );
            }
            else if ((baseStart is not null) && (baseEndAlt is null))
            {
                // The base descends until it levels, or until the turn to final starts.
                baseDistNm += GeoMath.DistanceNm(prev, ac.Position);
                baseDescending |= ac.VerticalSpeed <= -50;
                if ((current is not BasePhase) || (baseDescending && (Math.Abs(ac.VerticalSpeed) < 50)))
                {
                    baseEndAlt = ac.Altitude;
                }
            }
            prev = ac.Position;
            wentAround |= current is GoAroundPhase;
            if (ac.IsOnGround)
            {
                landedTick = t;
                break;
            }
        }

        Assert.NotNull(abeam);
        Assert.NotNull(baseStart);
        Assert.NotNull(baseEndAlt);
        double baseStartAlt = baseStart.Value.Alt;
        Assert.InRange(baseStartAlt, expected - 30, expected + 30);
        Assert.InRange(baseStartAlt, rolloutAlt + (0.3 * drop), rolloutAlt + (0.7 * drop));

        // One gradient: each leg measured over the distance it descends — the downwind from abeam to the base trigger,
        // the base from the trigger to where it levels — loses its share at about the same ft/nm. The tolerance is 20%
        // because BasePhase floors its rate at the category pattern descent rate (700 fpm piston), which the aviation ruling
        // keeps, so the base can run up to ~17% steeper than the downwind line.
        double dwGradient = (abeam.Value.Alt - baseStartAlt) / (baseStart.Value.Along - abeam.Value.Along);
        double baseGradient = (baseStartAlt - baseEndAlt.Value) / baseDistNm;
        _output.WriteLine($"downwind {dwGradient:F0} ft/nm, base {baseGradient:F0} ft/nm over {baseDistNm:F2} nm, base ended at {baseEndAlt:F0}");
        Assert.True(
            Math.Abs(baseGradient - dwGradient) <= 0.20 * dwGradient,
            $"Base gradient {baseGradient:F0} ft/nm over {baseDistNm:F2} nm is not within 20% of the downwind's {dwGradient:F0} ft/nm."
        );
        Assert.False(wentAround, "The pattern went around instead of landing.");
        Assert.True(landedTick > 0, "The aircraft never landed.");
    }

    /// <summary>Ticks an unheld aircraft until it is <paramref name="nmPastAbeam"/> past abeam; returns the tick reached.</summary>
    private static int FlyToPastAbeam(AircraftState ac, Koak28R koak, Func<string, AircraftState?> lookup, double nmPastAbeam)
    {
        for (int t = 1; t <= 200; t++)
        {
            Tick(ac, koak.Runway, lookup);
            if (koak.AlongTrack(ac.Position) - koak.AbeamAlongTrack >= nmPastAbeam)
            {
                return t;
            }
        }

        Assert.Fail($"Setup: the aircraft never reached {nmPastAbeam:F1} nm past abeam.");
        return -1;
    }

    [Fact]
    public void HeldMidDescent_LevelsOff_NeverClimbs()
    {
        Koak28R koak = ResolveKoak28R();
        AircraftState? self = null;
        AircraftState? lookup(string cs) => (cs == FollowerCallsign) ? self : null;
        AircraftState ac = MakeDownwindFollower(koak, nmBeforeAbeam: 0.5, lookup);
        self = ac;

        int extTick = FlyToPastAbeam(ac, koak, lookup, 0.2);
        double extAlt = ac.Altitude;
        CommandResult ext = DispatchCommand("EXT", ac, lookup);
        Assert.True(ext.Success, $"EXT failed: {ext.Message}");

        double maxAfter = double.NegativeInfinity;
        double minAfter = double.PositiveInfinity;
        int maxTick = -1;
        int minTick = -1;
        for (int t = extTick + 1; t <= extTick + 90; t++)
        {
            Tick(ac, koak.Runway, lookup);
            Assert.IsType<DownwindPhase>(ac.Phases?.CurrentPhase);
            if (ac.Altitude > maxAfter)
            {
                maxAfter = ac.Altitude;
                maxTick = t;
            }
            if (ac.Altitude < minAfter)
            {
                minAfter = ac.Altitude;
                minTick = t;
            }
        }

        Assert.True(
            koak.AlongTrack(ac.Position) > koak.BaseTriggerAlongTrack,
            "Setup: the extended downwind did not carry the aircraft past the base trigger."
        );
        Assert.True(maxAfter <= extAlt + 10, $"Held downwind climbed to {maxAfter:F0} ft MSL at t={maxTick} after EXT at {extAlt:F0} (t={extTick}).");
        Assert.True(
            minAfter >= extAlt - 30,
            $"Held downwind kept descending to {minAfter:F0} ft MSL at t={minTick} after EXT at {extAlt:F0} (t={extTick})."
        );

        CommandResult tb = DispatchCommand("TB", ac, lookup);
        Assert.True(tb.Success, $"TB failed: {tb.Message}");
        double tbAlt = ac.Altitude;
        for (int t = 1; t <= 5; t++)
        {
            Tick(ac, koak.Runway, lookup);
            Assert.True(ac.Altitude <= tbAlt + 10, $"{t} s after TB the aircraft climbed to {ac.Altitude:F0} ft MSL from {tbAlt:F0}.");
        }
        Assert.IsType<BasePhase>(ac.Phases?.CurrentPhase);
    }

    [Fact]
    public void ExtendedDownwind_ControllerClimb_HoldsAssignedAltitude()
    {
        Koak28R koak = ResolveKoak28R();
        AircraftState? self = null;
        AircraftState? lookup(string cs) => (cs == FollowerCallsign) ? self : null;
        AircraftState ac = MakeDownwindFollower(koak, nmBeforeAbeam: 0.5, lookup);
        self = ac;

        int t0 = FlyToPastAbeam(ac, koak, lookup, 0.2);
        CommandResult ext = DispatchCommand("EXT", ac, lookup);
        Assert.True(ext.Success, $"EXT failed: {ext.Message}");
        for (int i = 0; i < 5; i++)
        {
            Tick(ac, koak.Runway, lookup);
        }
        CommandResult cm = DispatchCommand("CM 1500", ac, lookup);
        Assert.True(cm.Success, $"CM failed: {cm.Message}");

        AssertReachesAndHolds(ac, koak, lookup, 1500, maxTicks: 150, $"EXT at t={t0}, then CM 1500");
        CommandResult tb = DispatchCommand("TB", ac, lookup);
        Assert.True(tb.Success, $"TB failed: {tb.Message}");
    }

    [Fact]
    public void UnheldDescent_ControllerAltitude_StandsUntilBaseTurn()
    {
        Koak28R koak = ResolveKoak28R();
        AircraftState? self = null;
        AircraftState? lookup(string cs) => (cs == FollowerCallsign) ? self : null;
        AircraftState ac = MakeDownwindFollower(koak, nmBeforeAbeam: 0.5, lookup);
        self = ac;

        int t0 = FlyToPastAbeam(ac, koak, lookup, 0.2);
        Assert.True(ac.Altitude < koak.Waypoints.PatternAltitude - 30, $"Setup: not descending 0.2 nm past abeam ({ac.Altitude:F0} ft MSL).");
        CommandResult cm = DispatchCommand("CM 1000", ac, lookup);
        Assert.True(cm.Success, $"CM failed: {cm.Message}");

        bool reached = false;
        for (int t = t0 + 1; t <= t0 + 120; t++)
        {
            Tick(ac, koak.Runway, lookup);
            if (ac.Phases?.CurrentPhase is not DownwindPhase)
            {
                Assert.True(reached, $"t={t}: the base turn came before the aircraft reached 1,000 ft MSL ({ac.Altitude:F0}).");
                Assert.InRange(ac.Altitude, 970, 1030);
                return;
            }
            if (Math.Abs(ac.Altitude - 1000) <= 30)
            {
                reached = true;
            }
            else
            {
                Assert.False(reached, $"t={t}: left the controller's 1,000 ft MSL for {ac.Altitude:F0} on the downwind.");
            }
        }

        Assert.Fail("Setup: the downwind never reached its base turn.");
    }

    [Fact]
    public void HeldLevel_SurvivesSnapshotRestore()
    {
        Koak28R koak = ResolveKoak28R();
        AircraftState? self = null;
        AircraftState? lookup(string cs) => (cs == FollowerCallsign) ? self : null;
        AircraftState ac = MakeDownwindFollower(koak, nmBeforeAbeam: 0.5, lookup);
        self = ac;

        FlyToPastAbeam(ac, koak, lookup, 0.2);
        CommandResult ext = DispatchCommand("EXT", ac, lookup);
        Assert.True(ext.Success, $"EXT failed: {ext.Message}");
        Tick(ac, koak.Runway, lookup);
        Tick(ac, koak.Runway, lookup);
        var dto = (DownwindPhaseDto)Assert.IsType<DownwindPhase>(ac.Phases?.CurrentPhase).ToSnapshot();
        Assert.NotNull(dto.HoldLevelFt);
        double latched = dto.HoldLevelFt!.Value;

        SimulationEngine engine = Engine();
        engine.World.AddAircraft(ac);
        string json = JsonSerializer.Serialize(engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        StateSnapshotDto snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;
        SimulationEngine restoredEngine = Engine();
        restoredEngine.RestoreFromSnapshot(snapshot);
        AircraftState restored = restoredEngine.World.FindAircraft(FollowerCallsign)!;
        Assert.NotNull(restored);
        self = restored;

        // Displace the restored aircraft above the latch: a phase that lost it would re-latch here instead.
        restored.Altitude = latched + 100;
        for (int t = 1; t <= 40; t++)
        {
            Tick(restored, koak.Runway, lookup);
        }

        var restoredDto = (DownwindPhaseDto)Assert.IsType<DownwindPhase>(restored.Phases?.CurrentPhase).ToSnapshot();
        Assert.Equal(latched, restoredDto.HoldLevelFt);
        Assert.InRange(restored.Altitude, latched - 15, latched + 15);
    }

    [Fact]
    public void ControllerAltitudeBeforeAbeam_StandsThroughAbeam()
    {
        Koak28R koak = ResolveKoak28R();
        AircraftState? self = null;
        AircraftState? lookup(string cs) => (cs == FollowerCallsign) ? self : null;
        AircraftState ac = MakeDownwindFollower(koak, nmBeforeAbeam: 0.5, lookup);
        self = ac;
        Tick(ac, koak.Runway, lookup);
        CommandResult cm = DispatchCommand("CM 1200", ac, lookup);
        Assert.True(cm.Success, $"CM failed: {cm.Message}");

        bool reached = false;
        bool passedAbeam = false;
        for (int t = 2; t <= 150; t++)
        {
            Tick(ac, koak.Runway, lookup);
            passedAbeam |= koak.AlongTrack(ac.Position) >= koak.AbeamAlongTrack;
            if (ac.Phases?.CurrentPhase is not DownwindPhase)
            {
                Assert.True(passedAbeam && reached, $"t={t}: the base turn came before the aircraft held 1,200 ft MSL past abeam.");
                Assert.InRange(ac.Altitude, 1170, 1230);
                return;
            }
            if (Math.Abs(ac.Altitude - 1200) <= 30)
            {
                reached = true;
            }
            else
            {
                Assert.False(reached, $"t={t}: left the controller's 1,200 ft MSL for {ac.Altitude:F0} (past abeam: {passedAbeam}).");
            }
        }

        Assert.Fail("Setup: the downwind never reached its base turn.");
    }

    /// <summary>
    /// A recording written before the leg recorded its assigned-altitude baseline: the assignment in force when it is
    /// restored (an inbound 3,500 that predates the pattern) is not a controller altitude issued on this leg, so the
    /// restored downwind still holds pattern altitude to abeam and descends after it.
    /// </summary>
    [Fact]
    public void RestoredWithoutAssignedAltitudeBaseline_DoesNotAdoptStaleAssignment()
    {
        Koak28R koak = ResolveKoak28R();
        AircraftState? self = null;
        AircraftState? lookup(string cs) => (cs == FollowerCallsign) ? self : null;
        AircraftState ac = MakeDownwindFollower(koak, nmBeforeAbeam: 0.3, lookup);
        self = ac;
        double tpa = koak.Waypoints.PatternAltitude;

        string json = JsonSerializer.Serialize<PhaseDto>(
            Assert.IsType<DownwindPhase>(ac.Phases!.CurrentPhase).ToSnapshot(),
            RecordingJsonOptions.Default
        );
        JsonObject node = JsonNode.Parse(json)!.AsObject();
        Assert.True(node.Remove(nameof(DownwindPhaseDto.SeenAssignedAltitudeFt)), "Setup: the DTO JSON carries no assigned-altitude baseline.");
        node.Remove(nameof(DownwindPhaseDto.AssignedAltitudeBaselineRecorded));
        var oldDto = (DownwindPhaseDto)JsonSerializer.Deserialize<PhaseDto>(node.ToJsonString(), RecordingJsonOptions.Default)!;
        var restored = DownwindPhase.FromSnapshot(oldDto);

        ac.Phases = null;
        ac.Targets.AssignedAltitude = 3500;
        double maxAlt = double.NegativeInfinity;
        for (int t = 1; t <= 120; t++)
        {
            PhaseContext ctx = Ctx(ac, koak.Runway, lookup);
            FlightPhysics.Update(ac, ctx.DeltaSeconds);
            maxAlt = Math.Max(maxAlt, ac.Altitude);
            if (restored.OnTick(ctx))
            {
                Assert.True(maxAlt <= tpa + 10, $"The restored downwind climbed to {maxAlt:F0} ft MSL toward the stale 3,500 (TPA {tpa:F0}).");
                Assert.True(ac.Altitude < tpa - 100, $"The restored downwind never descended after abeam: {ac.Altitude:F0} ft MSL at the base turn.");
                Assert.Null(((DownwindPhaseDto)restored.ToSnapshot()).ControllerAltitudeFt);
                return;
            }
        }

        Assert.Fail("Setup: the restored downwind never reached its base turn.");
    }

    [Fact]
    public void HeldDownwind_ShortApproachThenNormalApproach_StaysLevelUntilReleased()
    {
        Koak28R koak = ResolveKoak28R();
        AircraftState? self = null;
        AircraftState? lookup(string cs) => (cs == FollowerCallsign) ? self : null;
        AircraftState ac = MakeDownwindFollower(koak, nmBeforeAbeam: 0.5, lookup);
        self = ac;

        FlyToPastAbeam(ac, koak, lookup, 0.1);
        CommandResult ext = DispatchCommand("EXT", ac, lookup);
        Assert.True(ext.Success, $"EXT failed: {ext.Message}");
        Tick(ac, koak.Runway, lookup);
        DownwindPhase downwind = Assert.IsType<DownwindPhase>(ac.Phases?.CurrentPhase);
        double heldFt = Assert.NotNull(((DownwindPhaseDto)downwind.ToSnapshot()).HoldLevelFt);

        CommandResult sa = DispatchCommand("SA", ac, lookup);
        Assert.True(sa.Success, $"SA failed: {sa.Message}");
        CommandResult mna = DispatchCommand("MNA", ac, lookup);
        Assert.True(mna.Success, $"MNA failed: {mna.Message}");
        Assert.Same(downwind, ac.Phases?.CurrentPhase);
        Assert.True(downwind.IsExtended, "Setup: SA/MNA ended the extension.");

        // MNA planned nothing: the held level is still the target, with no line descent and no commanded rate.
        Assert.Null(((DownwindPhaseDto)downwind.ToSnapshot()).DescentTargetFt);
        Assert.Equal(heldFt, ac.Targets.TargetAltitude);
        Assert.Null(ac.Targets.DesiredVerticalRate);

        for (int t = 1; t <= 15; t++)
        {
            Tick(ac, koak.Runway, lookup);
            Assert.Null(((DownwindPhaseDto)downwind.ToSnapshot()).DescentTargetFt);
            Assert.InRange(ac.Altitude, heldFt - 10, heldFt + 10);
        }

        // Released before the base trigger: the line descent starts now.
        Assert.True(koak.AlongTrack(ac.Position) < koak.BaseTriggerAlongTrack, "Setup: the hold carried the aircraft past the base trigger.");
        downwind.IsExtended = false;
        Tick(ac, koak.Runway, lookup);
        Assert.NotNull(((DownwindPhaseDto)downwind.ToSnapshot()).DescentTargetFt);
    }

    private static SimulationEngine Engine() =>
        new(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
            },
        };

    /// <summary>Ticks the held downwind until it reaches <paramref name="levelFt"/> ± 30, then asserts it stays there.</summary>
    private static void AssertReachesAndHolds(
        AircraftState ac,
        Koak28R koak,
        Func<string, AircraftState?> lookup,
        double levelFt,
        int maxTicks,
        string context
    )
    {
        int reachedTick = -1;
        for (int t = 1; t <= maxTicks; t++)
        {
            Tick(ac, koak.Runway, lookup);
            Assert.IsType<DownwindPhase>(ac.Phases?.CurrentPhase);
            bool within = Math.Abs(ac.Altitude - levelFt) <= 30;
            if ((reachedTick < 0) && within)
            {
                reachedTick = t;
            }
            Assert.True(
                (reachedTick < 0) || within,
                $"{context}: left {levelFt:F0} ft MSL for {ac.Altitude:F0} at t={t} (reached at t={reachedTick})."
            );
        }

        Assert.True(reachedTick > 0, $"{context}: never reached {levelFt:F0} ft MSL (at {ac.Altitude:F0}, target {ac.Targets.TargetAltitude:F0}).");
    }
}
