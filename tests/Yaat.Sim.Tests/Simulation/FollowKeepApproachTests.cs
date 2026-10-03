using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// FOLLOW from an instrument approach keeps the approach: a follower on a course intercept, an approach's fixes, a
/// procedure turn or the approach's hold-in-lieu of a procedure turn, told to follow a same-runway lead ahead or a
/// runwayless lead inside its cone, keeps its phase list, landing clearance and control state and only records the lead.
/// A VFR follower re-sequenced onto another runway outside the final approach fix loses its landing clearance and the
/// instructor is told so. An IFR follower that loses sight of its lead, and an approach follower whose lead goes around,
/// tell the instructor that visual separation ended. Real KOAK, KCCR and KACV navdata.
/// </summary>
public class FollowKeepApproachTests(ITestOutputHelper output)
{
    private const string Leader = "LEAD1";
    private const string Follower = "FOLL1";
    private const string RadarSeparationRequired = $"{Follower} visual separation terminated — radar separation required";
    private const string LeadWentAround = $"{Follower} follow of {Leader} ended — {Leader} went around";

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    private static RunwayInfo Runway(string airport, string designator) =>
        NavigationDatabase.Instance.GetRunway(airport, designator)
        ?? throw new InvalidOperationException($"{airport} {designator} missing from navdata");

    /// <summary>A point <paramref name="alongNm"/> out the final and <paramref name="rightNm"/> right of the landing direction.</summary>
    private static LatLon OffFinal(RunwayInfo rwy, double alongNm, double rightNm) =>
        GeoMath.ProjectPoint(
            GeoMath.ProjectPoint(new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading.ToReciprocal(), alongNm),
            rwy.TrueHeading + 90.0,
            rightNm
        );

    private static AircraftState MakeVfr(string callsign, LatLon position, TrueHeading heading, double altitude, string airport) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = airport,
                Destination = airport,
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(2000),
                CruiseSpeed = 110,
            },
        };

    /// <summary>Files <paramref name="flightRules"/> for <paramref name="ac"/> with a flight plan on file, so "IFR" counts as IFR.</summary>
    private static void FileFlightRules(AircraftState ac, string flightRules)
    {
        ac.FlightPlan.HasFlightPlan = true;
        ac.FlightPlan.FlightRules = flightRules;
    }

    /// <summary>
    /// <paramref name="callsign"/> at <paramref name="position"/> on the <paramref name="direction"/> circuit for
    /// <paramref name="rwy"/> from <paramref name="leg"/>, started.
    /// </summary>
    private static AircraftState AddOnCircuit(
        SimulationEngine engine,
        string callsign,
        RunwayInfo rwy,
        PatternDirection direction,
        PatternEntryLeg leg,
        LatLon position
    )
    {
        AircraftState ac = MakeVfr(callsign, position, rwy.TrueHeading, 1000, rwy.AirportId);
        engine.World.AddAircraft(ac);
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategorization.Categorize(ac.AircraftType),
            ac.AircraftType,
            windSpeedKt: 0,
            direction,
            leg,
            touchAndGo: false,
            finalDistanceNm: null,
            patternSizeNm: null,
            altitudeOverrideFt: null,
            NavigationDatabase.Instance.GetRunways(rwy.AirportId),
            authoredRunway: null
        );
        ac.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = direction };
        foreach (Phase phase in circuit)
        {
            ac.Phases.Add(phase);
        }

        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        return ac;
    }

    /// <summary>The lead on <paramref name="rwy"/>'s right-traffic final <paramref name="alongNm"/> out on the centerline.</summary>
    private static AircraftState AddFinalLead(SimulationEngine engine, RunwayInfo rwy, double alongNm)
    {
        AircraftState lead = AddOnCircuit(engine, Leader, rwy, PatternDirection.Right, PatternEntryLeg.Final, OffFinal(rwy, alongNm, 0.0));
        lead.Altitude = 300.0 + (alongNm * 300.0);
        Assert.IsType<FinalApproachPhase>(lead.Phases!.CurrentPhase);
        return lead;
    }

    /// <summary><paramref name="callsign"/> on <paramref name="rwy"/>'s downwind 1 nm short of the base turn, flying the downwind.</summary>
    private static AircraftState AddOnDownwind(SimulationEngine engine, string callsign, RunwayInfo rwy, PatternDirection direction)
    {
        AircraftState ac = AddOnCircuit(engine, callsign, rwy, direction, PatternEntryLeg.Downwind, OffFinal(rwy, 0.0, 1.0));
        PatternWaypoints wp = AirborneFollowHelper.PatternWaypointsOf(ac.Phases!.CurrentPhase) ?? throw new InvalidOperationException("no waypoints");
        ac.Position = GeoMath.ProjectPoint(new LatLon(wp.BaseTurnLat, wp.BaseTurnLon), wp.DownwindHeading.ToReciprocal(), 1.0);
        ac.TrueHeading = wp.DownwindHeading;
        ac.TrueTrack = wp.DownwindHeading;
        Assert.IsType<DownwindPhase>(ac.Phases.CurrentPhase);
        return ac;
    }

    /// <summary>
    /// <paramref name="callsign"/> cleared for <paramref name="approachId"/> at <paramref name="airport"/> direct
    /// <paramref name="dctFix"/> from <paramref name="position"/> (the real CAPP chain), its phase list advanced to the first
    /// <typeparamref name="T"/>, added to <paramref name="engine"/>.
    /// </summary>
    private static AircraftState AddClearedForApproach<T>(SimulationEngine engine, string callsign, ApproachSetup setup)
        where T : Phase
    {
        (double Lat, double Lon) fix =
            NavigationDatabase.Instance.GetFixPosition(setup.DctFix) ?? throw new InvalidOperationException($"{setup.DctFix} missing");
        AircraftState ac = MakeVfr(callsign, setup.Position, setup.Heading, setup.Altitude, setup.Airport);
        ac.IndicatedAirspeed = 110;
        ac.Declination = setup.Declination;
        ac.Procedure = new AircraftProcedure { DestinationRunway = null };
        ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = setup.DctFix, Position = new LatLon(fix.Lat, fix.Lon) });
        var cmd = new ClearedApproachCommand(
            setup.ApproachId,
            setup.Airport,
            Force: false,
            AtFix: null,
            AtFixLat: null,
            AtFixLon: null,
            DctFix: setup.DctFix,
            DctFixLat: fix.Lat,
            DctFixLon: fix.Lon,
            CrossFixAltitude: null,
            CrossFixAltType: null
        );
        CommandResult capp = ApproachCommandHandler.TryClearedApproach(cmd, ac);
        Assert.True(capp.Success, capp.Message);
        engine.World.AddAircraft(ac);

        PhaseList phases = ac.Phases!;
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(ac);
        if (phases.CurrentPhase is { Status: PhaseStatus.Pending })
        {
            phases.Start(ctx);
        }

        while ((phases.CurrentPhase is not null) && (phases.CurrentPhase is not T))
        {
            phases.AdvanceToNext(ctx);
        }

        Assert.IsType<T>(phases.CurrentPhase);
        Assert.NotNull(phases.AssignedRunway);
        return ac;
    }

    /// <summary>Where and how an aircraft built by <see cref="AddClearedForApproach{T}"/> is cleared.</summary>
    private sealed record ApproachSetup(
        string Airport,
        string ApproachId,
        string DctFix,
        LatLon Position,
        TrueHeading Heading,
        double Altitude,
        double Declination
    );

    /// <summary>KCCR VOR RWY 19R from south of CCR, direct CCR: the chain engages the procedure turn at CCR.</summary>
    private static readonly ApproachSetup KccrS19RFromSouth = new(
        "KCCR",
        "S19R",
        "CCR",
        new LatLon(37.88, -122.28),
        new TrueHeading(41.0),
        6000,
        13.0
    );

    /// <summary>A KACV RNAV (GPS) RWY 1 clearance direct SEGVE from south-west of it: the chain flies the hold-in-lieu at SEGVE.</summary>
    private static ApproachSetup KacvR01DctSegve()
    {
        (double Lat, double Lon) segve = NavigationDatabase.Instance.GetFixPosition("SEGVE") ?? throw new InvalidOperationException("SEGVE missing");
        return new ApproachSetup("KACV", "R01", "SEGVE", new LatLon(segve.Lat - 0.15, segve.Lon - 0.1), new TrueHeading(30.0), 4000, 15.0);
    }

    /// <summary>Clearance and traffic-in-sight a follower on an approach holds before FOLLOW.</summary>
    private static void ClearToLandWithTrafficInSight(AircraftState follower)
    {
        follower.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.ClearedRunwayId = follower.Phases.AssignedRunway!.Designator;
        follower.Approach.HasReportedTrafficInSight = true;
    }

    /// <summary>Control state a kept approach must not touch.</summary>
    private static void SetControlState(AircraftState follower)
    {
        follower.Targets.TurnRateOverride = 1.5;
        follower.Targets.HasExplicitTurnRate = true;
        follower.Targets.PreferredTurnDirection = TurnDirection.Left;
        follower.Targets.HasExplicitSpeedCommand = true;
        follower.Targets.TargetSpeed = 85;
        follower.Targets.TargetAltitude = 1500;
        follower.Approach.LastReportedTrafficCallsign = Leader;
    }

    private sealed record KeptState(
        PhaseList? Phases,
        Phase? Current,
        int PhaseCount,
        ClearanceType? Clearance,
        string? ClearedRunwayId,
        double? TurnRateOverride,
        bool HasExplicitTurnRate,
        TurnDirection? PreferredTurnDirection,
        double? TargetAltitude,
        double? TargetSpeed,
        bool HasExplicitSpeedCommand,
        bool TrafficInSight,
        string? LastReportedTraffic
    );

    private static KeptState Capture(AircraftState ac) =>
        new(
            ac.Phases,
            ac.Phases?.CurrentPhase,
            ac.Phases?.Phases.Count ?? 0,
            ac.Phases?.LandingClearance,
            ac.Phases?.ClearedRunwayId,
            ac.Targets.TurnRateOverride,
            ac.Targets.HasExplicitTurnRate,
            ac.Targets.PreferredTurnDirection,
            ac.Targets.TargetAltitude,
            ac.Targets.TargetSpeed,
            ac.Targets.HasExplicitSpeedCommand,
            ac.Approach.HasReportedTrafficInSight,
            ac.Approach.LastReportedTrafficCallsign
        );

    private CommandResult Send(SimulationEngine engine, string command)
    {
        CommandResult result = engine.SendCommand(Follower, command);
        output.WriteLine($"{command}: success={result.Success} — {result.Message}");
        return result;
    }

    private static void TickSeconds(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds * 4; i++)
        {
            engine.TickPhysics(0.25);
        }
    }

    private static void AssertKept(AircraftState follower, KeptState before, CommandResult result)
    {
        Assert.True(result.Success, result.Message);
        Assert.Equal($"Follow {Leader}", result.Message);
        Assert.Equal(before, Capture(follower));
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.True(follower.Approach.HasReportedTrafficInSight);
        Assert.DoesNotContain(follower.PendingWarnings, w => w.Contains("cancelled by FOLLOW", StringComparison.Ordinal));
    }

    /// <summary>Visibility <paramref name="visToken"/> at <paramref name="airport"/>, no wind and no cloud.</summary>
    private static void SetVisibility(SimulationEngine engine, string airport, string visToken) =>
        engine.World.Weather = new WeatherProfile { Metars = [$"{airport} 121853Z 00000KT {visToken} CLR 20/12 A2992"] };

    // ─── Keep the approach ───

    [Fact]
    public void FollowFromIntercept_SameRunwayLeadAhead_KeepsApproachAndClearance()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 6.0);
        AddFinalLead(engine, Runway("KOAK", "28R"), 2.0);
        SetControlState(follower);
        KeptState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertKept(follower, before, result);
        Assert.IsType<InterceptCoursePhase>(follower.Phases!.CurrentPhase);
    }

    [Fact]
    public void FollowFromApproachNavigation_SameRunwayLeadAhead_KeepsApproach()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowSequenceRefusalTests.AddApproachNavigationFollower(engine);
        AddFinalLead(engine, Runway("KOAK", "28R"), 1.0);
        SetControlState(follower);
        KeptState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertKept(follower, before, result);
        Assert.IsType<ApproachNavigationPhase>(follower.Phases!.CurrentPhase);
    }

    /// <summary>KCCR VOR RWY 19R from the south engages the procedure turn at CCR; the follower is flying it.</summary>
    [Fact]
    public void FollowFromProcedureTurn_SameRunwayLeadAhead_KeepsApproach()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClearedForApproach<ProcedureTurnPhase>(engine, Follower, KccrS19RFromSouth);
        ClearToLandWithTrafficInSight(follower);
        AddFinalLead(engine, follower.Phases!.AssignedRunway!, 1.5);
        SetControlState(follower);
        KeptState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertKept(follower, before, result);
        Assert.IsType<ProcedureTurnPhase>(follower.Phases!.CurrentPhase);
    }

    /// <summary>KACV RNAV (GPS) RWY 1 direct SEGVE flies the hold-in-lieu of a procedure turn at SEGVE.</summary>
    [Fact]
    public void FollowFromHoldInLieu_SameRunwayLeadAhead_KeepsApproach()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClearedForApproach<HoldingPatternPhase>(engine, Follower, KacvR01DctSegve());
        Assert.True(((HoldingPatternPhase)follower.Phases!.CurrentPhase!).IsHoldInLieu);
        ClearToLandWithTrafficInSight(follower);
        AddFinalLead(engine, follower.Phases.AssignedRunway!, 1.5);
        SetControlState(follower);
        KeptState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertKept(follower, before, result);
        Assert.IsType<HoldingPatternPhase>(follower.Phases!.CurrentPhase);
    }

    /// <summary>A plain hold (no circuit limit) is not part of an approach: FOLLOW clears it, as before.</summary>
    [Fact]
    public void FollowFromEnrouteHold_StillClearsTheHold()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Runway("KOAK", "28R");
        (double Lat, double Lon) fix = NavigationDatabase.Instance.GetFixPosition("OAK") ?? throw new InvalidOperationException("OAK missing");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 6.0, 0.8), rwy.TrueHeading, 2000, rwy.AirportId);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        var hold = new HoldingPatternPhase
        {
            FixName = "OAK",
            FixLat = fix.Lat,
            FixLon = fix.Lon,
            InboundCourse = 280,
            LegLength = 1.0,
            IsMinuteBased = true,
            Direction = TurnDirection.Right,
            MaxCircuits = null,
        };
        Assert.False(hold.IsHoldInLieu);
        follower.Phases = new PhaseList();
        follower.Phases.Add(hold);
        follower.Phases.Start(CommandDispatcher.BuildMinimalContext(follower));
        AddFinalLead(engine, rwy, 2.0);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.IsNotType<HoldingPatternPhase>(follower.Phases?.CurrentPhase);
        Assert.DoesNotContain(hold, follower.Phases?.Phases ?? []);
    }

    /// <summary>A lead flying a procedure turn has no leg in the landing sequence: an approach follower still refuses it.</summary>
    [Fact]
    public void FollowFromIntercept_LeadOnProcedureTurn_IsRefusedAsNotAhead()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState lead = AddClearedForApproach<ProcedureTurnPhase>(engine, Leader, KccrS19RFromSouth);
        RunwayInfo rwy = lead.Phases!.AssignedRunway!;
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 3.0, 0.8), rwy.TrueHeading, 1200, rwy.AirportId);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        follower.Phases = new PhaseList { AssignedRunway = rwy };
        follower.Phases.Add(
            new InterceptCoursePhase
            {
                FinalApproachCourse = rwy.TrueHeading,
                ThresholdLat = rwy.ThresholdLatitude,
                ThresholdLon = rwy.ThresholdLongitude,
                AssignedInterceptHeading = null,
            }
        );
        follower.Phases.Add(new FinalApproachPhase());
        follower.Phases.Add(new LandingPhase());
        follower.Phases.Start(CommandDispatcher.BuildMinimalContext(follower));
        KeptState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.False(result.Success, result.Message);
        Assert.Equal($"Unable, on approach for runway {rwy.Designator}, {Leader} is not ahead of us, request vectors", result.Message);
        Assert.Equal(before, Capture(follower));
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void FollowFromApproach_RunwaylessLeadInCone_KeepsApproach()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Runway("KOAK", "28R");
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 6.0);
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 4.5, 0.5), rwy.TrueHeading, 1500, rwy.AirportId);
        engine.World.AddAircraft(lead);
        Assert.Null(lead.Phases?.AssignedRunway);
        SetControlState(follower);
        KeptState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertKept(follower, before, result);
    }

    /// <summary>
    /// A repeat FOLLOW of the lead already followed is acknowledged and changes nothing, even once that lead has dropped
    /// behind the follower on the same runway, where a fresh FOLLOW is refused as not ahead.
    /// </summary>
    [Fact]
    public void FollowFromApproach_SameLeadTwice_ChangesNothing()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Runway("KOAK", "28R");
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 6.0);
        AircraftState lead = AddFinalLead(engine, rwy, 2.0);
        CommandResult first = Send(engine, $"FOLLOW {Leader}");
        Assert.True(first.Success, first.Message);
        lead.Position = OffFinal(rwy, 8.0, 0.0);
        lead.Altitude = 2700;
        SetControlState(follower);
        KeptState before = Capture(follower);

        CommandResult again = Send(engine, $"FOLLOW {Leader}");

        AssertKept(follower, before, again);
        follower.Approach.FollowingCallsign = null;
        CommandResult fresh = Send(engine, $"FOLLOW {Leader}");
        Assert.Equal($"Unable, on approach for runway 28R, {Leader} is not ahead of us, request vectors", fresh.Message);
    }

    /// <summary>
    /// A repeat FOLLOW of the lead already followed, once that lead lands another runway, runs the cross-runway rules: an
    /// IFR follower is refused and keeps its approach.
    /// </summary>
    [Fact]
    public void FollowFromApproach_SameLeadNowOnAnotherRunway_IfrRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 6.0);
        FileFlightRules(follower, "IFR");
        AircraftState lead = AddFinalLead(engine, Runway("KOAK", "28R"), 2.0);
        CommandResult first = Send(engine, $"FOLLOW {Leader}");
        Assert.True(first.Success, first.Message);
        RunwayInfo parallel = Runway("KOAK", "28L");
        lead.Phases!.AssignedRunway = parallel;
        lead.Position = OffFinal(parallel, 2.0, 0.0);
        KeptState before = Capture(follower);

        CommandResult again = Send(engine, $"FOLLOW {Leader}");

        Assert.False(again.Success, again.Message);
        Assert.Equal($"Unable, on approach for runway 28R, {Leader} is landing runway 28L, request vectors", again.Message);
        Assert.Equal(before, Capture(follower));
    }

    /// <summary>
    /// A follower outbound on the KCCR S19R procedure turn, 4 nm past CCR, is measured to the threshold through CCR and the
    /// approach's fixes, not in a straight line: a lead on the final farther out than the follower's straight line but no
    /// farther than its path through CCR is ahead, and FOLLOW keeps the approach.
    /// </summary>
    [Fact]
    public void FollowFromProcedureTurn_OutboundPastFix_LeadWithinPathThroughFix_IsAccepted()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClearedForApproach<ProcedureTurnPhase>(engine, Follower, KccrS19RFromSouth);
        ClearToLandWithTrafficInSight(follower);
        var turn = (ProcedureTurnPhase)follower.Phases!.CurrentPhase!;
        RunwayInfo rwy = follower.Phases.AssignedRunway!;
        var fix = new LatLon(turn.FixLat, turn.FixLon);
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        var outbound = new TrueHeading(turn.PtOutboundCourseDeg);
        follower.Position = GeoMath.ProjectPoint(fix, outbound, 4.0);
        follower.TrueHeading = outbound;
        follower.TrueTrack = outbound;
        double straightNm = GeoMath.DistanceNm(follower.Position, threshold);
        double viaFixNm = GeoMath.DistanceNm(follower.Position, fix) + GeoMath.DistanceNm(fix, threshold);
        double leadAlongNm = (straightNm + viaFixNm) / 2.0;
        output.WriteLine($"straight {straightNm:F2} nm, via {turn.FixName} {viaFixNm:F2} nm, lead {leadAlongNm:F2} nm out");
        AddFinalLead(engine, rwy, leadAlongNm);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Follow {Leader}", result.Message);
        Assert.Same(turn, follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    /// <summary>
    /// Through FOLLOW: an approach follower behind a straight-in B738 entrant ahead of it by path keeps its intercept, captures
    /// the final still following, and spaces on the entrant there (the follow speed comes out below its unspaced final speed).
    /// </summary>
    [Fact]
    public void FollowFromApproach_SameRunwayLeadAhead_ThenTick_FinalApproachSpacesBehindLead()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Runway("KOAK", "28R");
        AircraftState lead = FollowEntryLeadPathTests.StraightInEntrant(rwy, "B738", 3.0, OffFinal(rwy, 3.0, 2.0));
        engine.World.AddAircraft(lead);
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 6.0);
        // A 30° intercept from the right of the final, so the capture comes before the entrant reaches its join.
        follower.TrueHeading = rwy.TrueHeading - 30.0;
        follower.TrueTrack = follower.TrueHeading;
        Phase? intercept = follower.Phases!.CurrentPhase;

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.Same(intercept, follower.Phases!.CurrentPhase);
        TickUntilFinal(engine, follower, lead);
        Assert.IsType<FinalApproachPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases!.LandingClearance);
        double vref = AircraftPerformance.ApproachSpeed("C172", AircraftCategory.Piston);
        double unspacedKts = vref + 20.0;
        var ctx = new PhaseContext
        {
            Aircraft = follower,
            Targets = follower.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 0.25,
            Runway = rwy,
            Logger = NullLogger.Instance,
            AircraftLookup = callsign => callsign == Leader ? lead : null,
        };

        double? adjusted = AirborneFollowHelper.GetAdjustedSpeed(ctx, unspacedKts, vref, AirborneFollowHelper.MaxSpeedAdjustFinalKts);

        Assert.NotNull(adjusted);
        Assert.True(adjusted < unspacedKts, $"follow speed {adjusted:F0} kt, unspaced {unspacedKts:F0} kt");
    }

    /// <summary>Tick until <paramref name="follower"/> is on <see cref="FinalApproachPhase"/>, at most four minutes, logging phase changes.</summary>
    private void TickUntilFinal(SimulationEngine engine, AircraftState follower, AircraftState lead)
    {
        for (int i = 0; (i < 240 * 4) && (follower.Phases?.CurrentPhase is not FinalApproachPhase); i++)
        {
            string before = follower.Phases?.CurrentPhase?.Name ?? "none";
            engine.TickPhysics(0.25);
            string after = follower.Phases?.CurrentPhase?.Name ?? "none";
            if (after != before)
            {
                output.WriteLine($"t={i * 0.25:F2}s follower {before} -> {after}, lead {lead.Phases?.CurrentPhase?.Name}");
            }
        }
    }

    // ─── Loses sight ───

    /// <summary>An approach follower 6 nm out following a lead 2 nm out, IFR or VFR, after the visibility drops to 1 SM.</summary>
    private AircraftState FollowThenLoseSight(SimulationEngine engine, string flightRules)
    {
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 6.0);
        FileFlightRules(follower, flightRules);
        AddFinalLead(engine, Runway("KOAK", "28R"), 2.0);
        CommandResult result = Send(engine, $"FOLLOW {Leader}");
        Assert.True(result.Success, result.Message);
        SetVisibility(engine, "KOAK", "1SM");
        TickSeconds(engine, 1);
        output.WriteLine($"warnings: {string.Join(" | ", follower.PendingWarnings)}");
        return follower;
    }

    [Fact]
    public void FollowFromApproach_IfrFollowerLosesSight_WarnsAndContinuesApproach()
    {
        SimulationEngine engine = BuildEngine();

        AircraftState follower = FollowThenLoseSight(engine, "IFR");

        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Contains(RadarSeparationRequired, follower.PendingWarnings);
        Assert.IsType<InterceptCoursePhase>(follower.Phases?.CurrentPhase);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases!.LandingClearance);
    }

    [Fact]
    public void FollowFromApproach_VfrFollowerLosesSight_NoWarning()
    {
        SimulationEngine engine = BuildEngine();

        AircraftState follower = FollowThenLoseSight(engine, "VFR");

        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.DoesNotContain(follower.PendingWarnings, w => w.Contains("visual separation terminated", StringComparison.Ordinal));
        Assert.IsType<InterceptCoursePhase>(follower.Phases?.CurrentPhase);
    }

    [Fact]
    public void FollowOnDownwind_IfrFollowerLosesSight_Warns()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Runway("KOAK", "28R");
        AircraftState follower = AddOnDownwind(engine, Follower, rwy, PatternDirection.Right);
        FileFlightRules(follower, "IFR");
        follower.Approach.HasReportedTrafficInSight = true;
        AddFinalLead(engine, rwy, 1.5);
        CommandResult result = Send(engine, $"FOLLOW {Leader}");
        Assert.True(result.Success, result.Message);
        SetVisibility(engine, "KOAK", "1/4SM");

        TickSeconds(engine, 1);
        output.WriteLine($"warnings: {string.Join(" | ", follower.PendingWarnings)}");

        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Contains(RadarSeparationRequired, follower.PendingWarnings);
    }

    // ─── Lead goes around ───

    /// <summary>An approach follower 6 nm out following a lead 2 nm out that then goes around, ticked one second.</summary>
    private (AircraftState Follower, Phase? Approach, int PhaseCount) FollowThenLeadGoesAround(SimulationEngine engine, string flightRules)
    {
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 6.0);
        FileFlightRules(follower, flightRules);
        AircraftState lead = AddFinalLead(engine, Runway("KOAK", "28R"), 2.0);
        CommandResult result = Send(engine, $"FOLLOW {Leader}");
        Assert.True(result.Success, result.Message);
        Phase? approach = follower.Phases!.CurrentPhase;
        int phaseCount = follower.Phases.Phases.Count;
        follower.PendingWarnings.Clear();
        follower.PendingPilotTransmissions.Clear();

        CommandResult goAround = engine.SendCommand(Leader, "GA");
        Assert.True(goAround.Success, goAround.Message);
        Assert.IsType<GoAroundPhase>(lead.Phases?.CurrentPhase);
        TickSeconds(engine, 1);
        output.WriteLine($"warnings: {string.Join(" | ", follower.PendingWarnings)}");
        return (follower, approach, phaseCount);
    }

    private static void AssertApproachContinues(AircraftState follower, Phase? approach, int phaseCount)
    {
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Same(approach, follower.Phases?.CurrentPhase);
        Assert.Equal(phaseCount, follower.Phases!.Phases.Count);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
        Assert.True(follower.Approach.HasReportedTrafficInSight);
        Assert.Empty(follower.PendingPilotTransmissions);
    }

    [Fact]
    public void FollowFromApproach_LeadGoesAround_EndsFollowAndWarns()
    {
        SimulationEngine engine = BuildEngine();

        (AircraftState follower, Phase? approach, int phaseCount) = FollowThenLeadGoesAround(engine, "IFR");

        AssertApproachContinues(follower, approach, phaseCount);
        Assert.Equal([LeadWentAround, RadarSeparationRequired], follower.PendingWarnings);
    }

    [Fact]
    public void FollowFromApproach_LeadGoesAround_VfrFollower_EndsFollowWithoutRadarLine()
    {
        SimulationEngine engine = BuildEngine();

        (AircraftState follower, Phase? approach, int phaseCount) = FollowThenLeadGoesAround(engine, "VFR");

        AssertApproachContinues(follower, approach, phaseCount);
        Assert.Equal([LeadWentAround], follower.PendingWarnings);
    }

    // ─── Lead lands ───

    /// <summary>
    /// An approach follower 6 nm out following a lead 2 nm out that lands: the follow ends and the
    /// pilot says nothing. A lead landing is how a follow normally finishes, not a report to make.
    /// </summary>
    [Fact]
    public void FollowFromApproach_LeadLands_EndsFollowSilently()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 6.0);
        AircraftState lead = AddFinalLead(engine, Runway("KOAK", "28R"), 2.0);
        CommandResult result = Send(engine, $"FOLLOW {Leader}");
        Assert.True(result.Success, result.Message);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        follower.PendingWarnings.Clear();
        follower.PendingPilotTransmissions.Clear();

        lead.IsOnGround = true;
        TickSeconds(engine, 1);

        output.WriteLine($"warnings: {string.Join(" | ", follower.PendingWarnings)}");
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Empty(follower.PendingPilotTransmissions);
        Assert.DoesNotContain(follower.PendingWarnings, w => w.Contains("breaking off the follow", StringComparison.OrdinalIgnoreCase));
    }

    // ─── Course reversals: the lead lifecycle runs on the procedure turn and the hold-in-lieu ───

    /// <summary>An IFR follower on the KCCR S19R procedure turn following a lead that goes around: the follow ends there.</summary>
    [Fact]
    public void FollowFromProcedureTurn_LeadGoesAround_EndsFollowAndWarns()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClearedForApproach<ProcedureTurnPhase>(engine, Follower, KccrS19RFromSouth);
        FileFlightRules(follower, "IFR");
        ClearToLandWithTrafficInSight(follower);
        AddFinalLead(engine, follower.Phases!.AssignedRunway!, 1.5);
        CommandResult result = Send(engine, $"FOLLOW {Leader}");
        Assert.True(result.Success, result.Message);
        Phase? turn = follower.Phases.CurrentPhase;
        follower.PendingWarnings.Clear();

        CommandResult goAround = engine.SendCommand(Leader, "GA");
        Assert.True(goAround.Success, goAround.Message);
        TickSeconds(engine, 1);
        output.WriteLine($"warnings: {string.Join(" | ", follower.PendingWarnings)}");

        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Same(turn, follower.Phases?.CurrentPhase);
        Assert.Equal([LeadWentAround, RadarSeparationRequired], follower.PendingWarnings);
    }

    /// <summary>An IFR follower on the KACV R01 hold-in-lieu that loses sight of its lead: the follow ends there.</summary>
    [Fact]
    public void FollowFromHoldInLieu_IfrFollowerLosesSight_WarnsAndKeepsTheHold()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClearedForApproach<HoldingPatternPhase>(engine, Follower, KacvR01DctSegve());
        FileFlightRules(follower, "IFR");
        ClearToLandWithTrafficInSight(follower);
        AddFinalLead(engine, follower.Phases!.AssignedRunway!, 1.5);
        CommandResult result = Send(engine, $"FOLLOW {Leader}");
        Assert.True(result.Success, result.Message);
        Phase? hold = follower.Phases.CurrentPhase;

        SetVisibility(engine, "KACV", "1/4SM");
        TickSeconds(engine, 1);
        output.WriteLine($"warnings: {string.Join(" | ", follower.PendingWarnings)}");

        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Same(hold, follower.Phases?.CurrentPhase);
        Assert.Contains(RadarSeparationRequired, follower.PendingWarnings);
    }

    // ─── Missed approach ───

    /// <summary>
    /// <paramref name="callsign"/> on the published missed approach of KCCR VOR RWY 19R (the phases
    /// <see cref="ApproachCommandHandler.BuildMissedApproachPhases"/> builds), climbing out 0.5 nm past the threshold.
    /// </summary>
    private static AircraftState AddOnMissedApproach(SimulationEngine engine, string callsign)
    {
        AircraftState ac = AddClearedForApproach<ProcedureTurnPhase>(engine, callsign, KccrS19RFromSouth);
        PhaseList cleared = ac.Phases!;
        RunwayInfo rwy = cleared.AssignedRunway!;
        List<Phase> missed = ApproachCommandHandler.BuildMissedApproachPhases(ac);
        Assert.NotEmpty(missed);
        ac.Phases = new PhaseList { AssignedRunway = rwy, ActiveApproach = cleared.ActiveApproach };
        foreach (Phase phase in missed)
        {
            ac.Phases.Add(phase);
        }

        ac.Position = OffFinal(rwy, -0.5, 0.0);
        ac.TrueHeading = rwy.TrueHeading;
        ac.TrueTrack = rwy.TrueHeading;
        ac.Altitude = 800;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        Assert.IsType<ApproachNavigationPhase>(ac.Phases.CurrentPhase);
        return ac;
    }

    [Fact]
    public void FollowOnOwnMissedApproach_IfrFollower_IsRefusedUnchanged()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddOnMissedApproach(engine, Follower);
        FileFlightRules(follower, "IFR");
        follower.Approach.HasReportedTrafficInSight = true;
        AddFinalLead(engine, follower.Phases!.AssignedRunway!, 2.0);
        KeptState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.False(result.Success, result.Message);
        Assert.Equal("Unable, on the missed approach, request vectors", result.Message);
        Assert.Equal(before, Capture(follower));
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    /// <summary>A VFR follower on its missed is re-sequenced behind the lead and carries no landing clearance onward.</summary>
    [Fact]
    public void FollowOnOwnMissedApproach_VfrFollower_ResequencesWithoutLandingClearance()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddOnMissedApproach(engine, Follower);
        RunwayInfo rwy = follower.Phases!.AssignedRunway!;
        follower.Approach.HasReportedTrafficInSight = true;
        follower.Phases.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.ClearedRunwayId = rwy.Designator;
        Phase missed = follower.Phases.CurrentPhase!;
        AddFinalLead(engine, rwy, 2.0);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");
        output.WriteLine($"phases: {string.Join(" → ", follower.Phases?.Phases.Select(p => p.Name) ?? [])}");
        output.WriteLine($"warnings: {string.Join(" | ", follower.PendingWarnings)}");

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Follow {Leader}", result.Message);
        Assert.DoesNotContain(missed, follower.Phases!.Phases);
        Assert.Contains(follower.Phases.Phases, p => p is DownwindPhase);
        Assert.Null(follower.Phases.LandingClearance);
        Assert.Null(follower.Phases.ClearedRunwayId);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Contains(
            follower.PendingWarnings,
            w => w.StartsWith($"{Follower} ", StringComparison.Ordinal) && w.EndsWith(" cancelled by FOLLOW", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void FollowFromApproach_LeadOnMissedApproach_IsRefusedAsGoingAround()
    {
        SimulationEngine engine = BuildEngine();
        AddOnMissedApproach(engine, Leader);
        AircraftState follower = AddClearedForApproach<ProcedureTurnPhase>(engine, Follower, KccrS19RFromSouth);
        ClearToLandWithTrafficInSight(follower);
        KeptState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.False(result.Success, result.Message);
        Assert.Equal($"Unable, {Leader} is going around, request vectors", result.Message);
        Assert.Equal(before, Capture(follower));
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    /// <summary>A lead flying its missed approach stands for no leg in the landing sequence, so it is never ahead of a follower.</summary>
    [Fact]
    public void LeadOnMissedApproach_HasNoSequenceLeg()
    {
        SimulationEngine engine = BuildEngine();

        AircraftState lead = AddOnMissedApproach(engine, Leader);

        Assert.Null(AirborneFollowHelper.PatternLegIndex(lead));
        Assert.False(AirborneFollowHelper.IsOnApproachBeforeFinal(lead.Phases!.CurrentPhase));
    }

    // ─── VFR re-sequence to another runway ───

    /// <summary>A VFR approach follower 8 nm out on 28R (outside the FAF) told to follow a lead on the 28L left downwind.</summary>
    private CommandResult FollowLeadOnTheParallel(SimulationEngine engine)
    {
        AddOnDownwind(engine, Leader, Runway("KOAK", "28L"), PatternDirection.Left);
        return Send(engine, $"FOLLOW {Leader}");
    }

    private static void AssertResequencedTo28L(AircraftState follower, CommandResult result)
    {
        Assert.True(result.Success, result.Message);
        Assert.Equal($"Follow {Leader}", result.Message);
        Assert.Null(follower.Phases!.LandingClearance);
        Assert.Null(follower.Phases.ClearedRunwayId);
        Assert.Equal("28L", follower.Phases.AssignedRunway?.Designator);
        Assert.Contains(follower.Phases.Phases, p => p is DownwindPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Contains($"{Follower} approach to RWY 28R cancelled by FOLLOW, landing clearance RWY 28R cancelled", follower.PendingWarnings);
    }

    [Fact]
    public void FollowFromApproach_DifferentRunwayLeadOutsideFaf_VfrFollower_DropsClearanceAndWarns()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 8.0);

        CommandResult result = FollowLeadOnTheParallel(engine);

        AssertResequencedTo28L(follower, result);
    }

    /// <summary>A landing clearance with no cleared runway id counts as one for the follower's previous runway.</summary>
    [Fact]
    public void FollowFromApproach_DifferentRunwayLeadOutsideFaf_ClearanceWithoutRunwayId_NamesAssignedRunway()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 8.0);
        follower.Phases!.ClearedRunwayId = null;

        CommandResult result = FollowLeadOnTheParallel(engine);

        AssertResequencedTo28L(follower, result);
    }
}
