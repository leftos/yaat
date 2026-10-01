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
/// The remaining path to the threshold of an aircraft still on a pattern entry (<see cref="AirborneFollowHelper.RemainingPatternPathNm"/>):
/// from its position along the entry route to the point where it joins the circuit, in straight lines, plus the circuit path of an
/// aircraft standing on the joined leg at that point. Real KOAK 28R right traffic.
/// </summary>
public class FollowEntryLeadPathTests(ITestOutputHelper output)
{
    private const double ToleranceNm = 0.01;

    private static RunwayInfo Rwy28R()
    {
        TestVnasData.EnsureInitialized();
        return NavigationDatabase.Instance.GetRunway("KOAK", "28R") ?? throw new InvalidOperationException("KOAK 28R missing from navdata");
    }

    private static AircraftState MakeAircraft(string callsign, string type, LatLon position) =>
        new()
        {
            Callsign = callsign,
            AircraftType = type,
            Position = position,
            TrueHeading = new TrueHeading(112),
            TrueTrack = new TrueHeading(112),
            Altitude = 1500,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KOAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(2000),
                CruiseSpeed = 110,
            },
        };

    private static List<Phase> Circuit(RunwayInfo rwy, string type, PatternEntryLeg leg) =>
        PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategorization.Categorize(type),
            type,
            windSpeedKt: 0,
            PatternDirection.Right,
            leg,
            touchAndGo: false,
            finalDistanceNm: null,
            patternSizeNm: null,
            altitudeOverrideFt: null,
            NavigationDatabase.Instance.GetRunways(rwy.AirportId),
            authoredRunway: null
        );

    /// <summary>The right-traffic 28R geometry a <paramref name="type"/> flies (its downwind's waypoints).</summary>
    private static PatternWaypoints Waypoints(RunwayInfo rwy, string type) =>
        AirborneFollowHelper.FirstPatternWaypoints(PhasesOf(rwy, Circuit(rwy, type, PatternEntryLeg.Downwind)))
        ?? throw new InvalidOperationException("downwind circuit carries no waypoints");

    private static PhaseList PhasesOf(RunwayInfo rwy, IEnumerable<Phase> phases)
    {
        var list = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Right };
        foreach (Phase phase in phases)
        {
            list.Add(phase);
        }

        return list;
    }

    /// <summary>
    /// <see cref="Leader"/> at <paramref name="position"/> whose phase list is <paramref name="first"/> then the circuit from
    /// <paramref name="leg"/>, started.
    /// </summary>
    private static AircraftState Fly(RunwayInfo rwy, string type, LatLon position, IEnumerable<Phase> first, PatternEntryLeg leg) =>
        FlyAs(Leader, new Placement(rwy, type, position), first, leg);

    /// <summary>Where and what an aircraft built by <see cref="FlyAs"/> is: its runway, type and position.</summary>
    private sealed record Placement(RunwayInfo Runway, string Type, LatLon Position);

    /// <summary>
    /// <paramref name="callsign"/> as <paramref name="at"/> places it, its phase list <paramref name="first"/> then the circuit
    /// from <paramref name="leg"/>, started.
    /// </summary>
    private static AircraftState FlyAs(string callsign, Placement at, IEnumerable<Phase> first, PatternEntryLeg leg)
    {
        AircraftState ac = MakeAircraft(callsign, at.Type, at.Position);
        ac.Phases = PhasesOf(at.Runway, first.Concat(Circuit(at.Runway, at.Type, leg)));
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        return ac;
    }

    /// <summary>The remaining path of a <paramref name="type"/> standing at <paramref name="at"/> on the circuit's <paramref name="leg"/>.</summary>
    private static double LegPathFrom(RunwayInfo rwy, string type, PatternEntryLeg leg, LatLon at, PatternWaypoints wp)
    {
        AircraftState probe = Fly(rwy, type, at, [], leg);
        return AirborneFollowHelper.RemainingPatternPathNm(probe, wp);
    }

    private static double Nm(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b);

    private static LatLon Threshold(RunwayInfo rwy) => new(rwy.ThresholdLatitude, rwy.ThresholdLongitude);

    /// <summary>A point on the downwind line <paramref name="shortNm"/> before the base-turn point.</summary>
    private static LatLon OnDownwind(PatternWaypoints wp, double shortNm) =>
        GeoMath.ProjectPoint(new LatLon(wp.BaseTurnLat, wp.BaseTurnLon), wp.DownwindHeading.ToReciprocal(), shortNm);

    /// <summary>
    /// A point <paramref name="backNm"/> behind <paramref name="join"/> along the downwind and as far out from it: the 45°
    /// entry line.
    /// </summary>
    private static LatLon OnFortyFive(PatternWaypoints wp, LatLon join, double backNm) =>
        GeoMath.ProjectPoint(GeoMath.ProjectPoint(join, wp.DownwindHeading.ToReciprocal(), backNm), wp.CrosswindHeading, backNm);

    private static PatternEntryPhase Entry(LatLon join, PatternEntryKind kind, LatLon? leadIn) =>
        new()
        {
            EntryLat = join.Lat,
            EntryLon = join.Lon,
            PatternAltitude = 1000,
            Kind = kind,
            LeadInLat = leadIn?.Lat,
            LeadInLon = leadIn?.Lon,
        };

    [Fact]
    public void FortyFiveEntryLead_BeforeLeadIn_PathRunsThroughLeadInEntryAndDownwind()
    {
        RunwayInfo rwy = Rwy28R();
        PatternWaypoints wp = Waypoints(rwy, "C172");
        LatLon join = OnDownwind(wp, 2.0);
        LatLon leadIn = OnFortyFive(wp, join, 0.7);
        LatLon position = GeoMath.ProjectPoint(OnFortyFive(wp, join, 2.0), wp.DownwindHeading, 0.5);
        AircraftState lead = Fly(rwy, "C172", position, [Entry(join, PatternEntryKind.FortyFive, leadIn)], PatternEntryLeg.Downwind);
        Assert.Equal(PatternEntryPhase.LeadInTargetName, lead.Targets.NavigationRoute[0].Name);

        double expected = Nm(position, leadIn) + Nm(leadIn, join) + LegPathFrom(rwy, "C172", PatternEntryLeg.Downwind, join, wp);
        double actual = AirborneFollowHelper.RemainingPatternPathNm(lead, wp);

        Assert.Equal(expected, actual, ToleranceNm);
        Assert.True(
            actual > Nm(position, Threshold(rwy)),
            $"path {actual:F2} nm is not longer than the straight line {Nm(position, Threshold(rwy)):F2} nm"
        );
    }

    [Fact]
    public void FortyFiveEntryLead_PastLeadIn_PathRunsThroughEntryAndDownwind()
    {
        RunwayInfo rwy = Rwy28R();
        PatternWaypoints wp = Waypoints(rwy, "C172");
        LatLon join = OnDownwind(wp, 2.0);
        LatLon leadIn = OnFortyFive(wp, join, 0.7);
        LatLon position = OnFortyFive(wp, join, 0.4);
        AircraftState lead = Fly(rwy, "C172", position, [Entry(join, PatternEntryKind.FortyFive, leadIn)], PatternEntryLeg.Downwind);
        Assert.Equal(PatternEntryPhase.LeadInTargetName, lead.Targets.NavigationRoute[0].Name);
        lead.Targets.NavigationRoute.RemoveAt(0);

        double expected = Nm(position, join) + LegPathFrom(rwy, "C172", PatternEntryLeg.Downwind, join, wp);

        Assert.Equal(expected, AirborneFollowHelper.RemainingPatternPathNm(lead, wp), ToleranceNm);
    }

    [Fact]
    public void BaseEntryLead_PathJoinsBaseLeg()
    {
        RunwayInfo rwy = Rwy28R();
        PatternWaypoints wp = Waypoints(rwy, "C172");
        LatLon join = GeoMath.ProjectPoint(new LatLon(wp.BaseTurnLat, wp.BaseTurnLon), wp.CrosswindHeading.ToReciprocal(), 0.3);
        LatLon position = GeoMath.ProjectPoint(GeoMath.ProjectPoint(join, wp.CrosswindHeading, 2.0), wp.DownwindHeading, 1.0);
        AircraftState lead = Fly(rwy, "C172", position, [Entry(join, PatternEntryKind.Base, null)], PatternEntryLeg.Base);

        double expected = Nm(position, join) + LegPathFrom(rwy, "C172", PatternEntryLeg.Base, join, wp);

        Assert.Equal(expected, AirborneFollowHelper.RemainingPatternPathNm(lead, wp), ToleranceNm);
    }

    [Fact]
    public void UpwindEntryLead_PathJoinsUpwindLeg()
    {
        RunwayInfo rwy = Rwy28R();
        PatternWaypoints wp = Waypoints(rwy, "C172");
        LatLon join = GeoMath.ProjectPoint(new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon), wp.UpwindHeading.ToReciprocal(), 1.0);
        LatLon position = GeoMath.ProjectPoint(join, wp.CrosswindHeading.ToReciprocal(), 1.5);
        AircraftState lead = Fly(rwy, "C172", position, [Entry(join, PatternEntryKind.Upwind, null)], PatternEntryLeg.Upwind);

        double expected = Nm(position, join) + LegPathFrom(rwy, "C172", PatternEntryLeg.Upwind, join, wp);

        Assert.Equal(expected, AirborneFollowHelper.RemainingPatternPathNm(lead, wp), ToleranceNm);
    }

    /// <summary>A point <paramref name="alongNm"/> out the 28R final and <paramref name="rightNm"/> right of the landing direction.</summary>
    private static LatLon OffFinal(RunwayInfo rwy, double alongNm, double rightNm) =>
        GeoMath.ProjectPoint(GeoMath.ProjectPoint(Threshold(rwy), rwy.TrueHeading.ToReciprocal(), alongNm), rwy.TrueHeading + 90.0, rightNm);

    /// <summary>
    /// A straight-in <paramref name="type"/> (<see cref="Leader"/>) entering the 28R final <paramref name="joinNm"/> out on the
    /// centerline from <paramref name="position"/>, heading for the join, with the circuit a Final entry hands over to (the
    /// final approach and the landing, no pattern legs).
    /// </summary>
    internal static AircraftState StraightInEntrant(RunwayInfo rwy, string type, double joinNm, LatLon position)
    {
        LatLon join = OffFinal(rwy, joinNm, 0.0);
        AircraftState lead = Fly(rwy, type, position, [Entry(join, PatternEntryKind.Final, null)], PatternEntryLeg.Final);
        lead.TrueHeading = new TrueHeading(GeoMath.BearingTo(position, join));
        lead.TrueTrack = lead.TrueHeading;
        return lead;
    }

    [Fact]
    public void StraightInFinalEntryLead_UsesFinalFormula()
    {
        RunwayInfo rwy = Rwy28R();
        LatLon position = OffFinal(rwy, 5.0, 2.0);
        AircraftState lead = StraightInEntrant(rwy, "C172", 3.0, position);
        Assert.IsType<PatternEntryPhase>(lead.Phases!.CurrentPhase);
        Assert.Null(AirborneFollowHelper.FirstPatternWaypoints(lead.Phases));
        LatLon join = OffFinal(rwy, 3.0, 0.0);
        double alongFinalNm = GeoMath.AlongTrackDistanceNm(join, Threshold(rwy), rwy.TrueHeading.ToReciprocal());

        double expected = Nm(position, join) + Math.Max(alongFinalNm, Nm(join, Threshold(rwy)));

        Assert.Equal(expected, AirborneFollowHelper.SequenceRemainingPathNm(lead, rwy), ToleranceNm);
    }

    /// <summary>
    /// An entry to the crosswind (ELC/ERC) joins at the crosswind turn but carries a downwind kind: the leg it hands over to
    /// decides the join, so its path flies the crosswind, one pattern width longer than a downwind join at the same point.
    /// </summary>
    [Fact]
    public void CrosswindEntryLead_PathJoinsCrosswindLeg()
    {
        RunwayInfo rwy = Rwy28R();
        PatternWaypoints wp = Waypoints(rwy, "C172");
        var join = new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon);
        LatLon position = GeoMath.ProjectPoint(join, wp.CrosswindHeading.ToReciprocal(), 1.5);
        AircraftState lead = Fly(rwy, "C172", position, [Entry(join, PatternEntryKind.Crosswind, null)], PatternEntryLeg.Crosswind);
        AircraftState downwindJoin = Fly(rwy, "C172", position, [Entry(join, PatternEntryKind.Crosswind, null)], PatternEntryLeg.Downwind);
        double widthNm = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(new LatLon(wp.DownwindStartLat, wp.DownwindStartLon), Threshold(rwy), wp.DownwindHeading)
        );

        double actual = AirborneFollowHelper.RemainingPatternPathNm(lead, wp);

        Assert.Equal(Nm(position, join) + LegPathFrom(rwy, "C172", PatternEntryLeg.Crosswind, join, wp), actual, ToleranceNm);
        Assert.Equal(AirborneFollowHelper.RemainingPatternPathNm(downwindJoin, wp) + widthNm, actual, ToleranceNm);
    }

    /// <summary>A B738 on the wrong (south) side of 28R, abeam the midfield point.</summary>
    private static LatLon WrongSide(PatternWaypoints wp) =>
        GeoMath.ProjectPoint(MidfieldCrossingPhase.MidfieldTarget(wp), wp.CrosswindHeading.ToReciprocal(), 4.0);

    /// <summary>The teardrop's route as <see cref="TeardropReentryPhase"/> builds it when it starts, for a B738 on <paramref name="wp"/>.</summary>
    private static List<LatLon> StartedTeardropRoute(RunwayInfo rwy, PatternWaypoints wp)
    {
        AircraftState probe = Fly(
            rwy,
            "B738",
            MidfieldCrossingPhase.MidfieldTarget(wp),
            [new TeardropReentryPhase { Waypoints = wp }],
            PatternEntryLeg.Downwind
        );
        return [.. probe.Targets.NavigationRoute.Select(t => t.Position)];
    }

    [Fact]
    public void MidfieldCrossingLead_WithQueuedTeardrop_PathRunsThroughTeardropFixes()
    {
        RunwayInfo rwy = Rwy28R();
        PatternWaypoints wp = Waypoints(rwy, "B738");
        LatLon position = WrongSide(wp);
        AircraftState lead = Fly(
            rwy,
            "B738",
            position,
            [new MidfieldCrossingPhase { Waypoints = wp }, new TeardropReentryPhase { Waypoints = wp }],
            PatternEntryLeg.Downwind
        );
        List<LatLon> teardrop = StartedTeardropRoute(rwy, wp);
        Assert.Equal(3, teardrop.Count);
        LatLon midfield = MidfieldCrossingPhase.MidfieldTarget(wp);
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);

        double expected =
            Nm(position, midfield)
            + Nm(midfield, teardrop[0])
            + Nm(teardrop[0], teardrop[1])
            + Nm(teardrop[1], abeam)
            + LegPathFrom(rwy, "B738", PatternEntryLeg.Downwind, abeam, wp);

        Assert.Equal(expected, AirborneFollowHelper.RemainingPatternPathNm(lead, wp), ToleranceNm);
    }

    [Fact]
    public void MidfieldCrossingLead_WithoutTeardrop_JoinsDownwindAtMidfield()
    {
        RunwayInfo rwy = Rwy28R();
        PatternWaypoints wp = Waypoints(rwy, "C172");
        LatLon position = WrongSide(wp);
        AircraftState lead = Fly(rwy, "C172", position, [new MidfieldCrossingPhase { Waypoints = wp }], PatternEntryLeg.Downwind);
        LatLon midfield = MidfieldCrossingPhase.MidfieldTarget(wp);

        double expected = Nm(position, midfield) + LegPathFrom(rwy, "C172", PatternEntryLeg.Downwind, midfield, wp);

        Assert.Equal(expected, AirborneFollowHelper.RemainingPatternPathNm(lead, wp), ToleranceNm);
    }

    [Fact]
    public void TeardropReentryLead_PathUsesRemainingTeardropFixes()
    {
        RunwayInfo rwy = Rwy28R();
        PatternWaypoints wp = Waypoints(rwy, "B738");
        List<LatLon> teardrop = StartedTeardropRoute(rwy, wp);
        LatLon position = GeoMath.ProjectPoint(teardrop[0], new TrueHeading(GeoMath.BearingTo(teardrop[0], teardrop[1])), 0.5);
        AircraftState lead = Fly(rwy, "B738", position, [new TeardropReentryPhase { Waypoints = wp }], PatternEntryLeg.Downwind);
        Assert.Equal(3, lead.Targets.NavigationRoute.Count);
        lead.Targets.NavigationRoute.RemoveAt(0);
        var abeam = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);

        double expected = Nm(position, teardrop[1]) + Nm(teardrop[1], abeam) + LegPathFrom(rwy, "B738", PatternEntryLeg.Downwind, abeam, wp);

        Assert.Equal(expected, AirborneFollowHelper.RemainingPatternPathNm(lead, wp), ToleranceNm);
    }

    // ─── Approach followers, through FOLLOW and the tick ───

    private const string Leader = "LEAD1";
    private const string Follower = "FOLL1";

    private static SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        return new SimulationEngine(new TestAirportGroundData());
    }

    /// <summary>
    /// <paramref name="callsign"/>, a C172 on a 45° entry to the 28R right downwind, <paramref name="backNm"/> back along the
    /// 45° line from its join point (2 nm short of the base turn), no lead-in, heading for the join. Returns it and its true
    /// remaining path.
    /// </summary>
    private static (AircraftState Lead, double TruePathNm) FortyFiveEntrant(string callsign, RunwayInfo rwy, double backNm)
    {
        PatternWaypoints wp = Waypoints(rwy, "C172");
        LatLon join = OnDownwind(wp, 2.0);
        LatLon position = OnFortyFive(wp, join, backNm);
        AircraftState lead = FlyAs(
            callsign,
            new Placement(rwy, "C172", position),
            [Entry(join, PatternEntryKind.FortyFive, null)],
            PatternEntryLeg.Downwind
        );
        lead.TrueHeading = new TrueHeading(GeoMath.BearingTo(position, join));
        lead.TrueTrack = lead.TrueHeading;
        return (lead, Nm(position, join) + LegPathFrom(rwy, "C172", PatternEntryLeg.Downwind, join, wp));
    }

    /// <summary>A <see cref="FortyFiveEntrant"/> as <see cref="Leader"/>, added to <paramref name="engine"/>.</summary>
    private static (AircraftState Lead, double TruePathNm) AddFortyFiveEntryLead(SimulationEngine engine, RunwayInfo rwy, double backNm)
    {
        (AircraftState lead, double truePathNm) = FortyFiveEntrant(Leader, rwy, backNm);
        engine.World.AddAircraft(lead);
        return (lead, truePathNm);
    }

    /// <summary>
    /// An approach follower whose own remaining path lies between a 45° entrant's straight line to the threshold and its true
    /// path through the entry and the circuit: the entrant is behind it in sequence, and FOLLOW is refused.
    /// </summary>
    [Fact]
    public void ApproachFollower_EntryLeadWithLongerTruePath_IsRefusedAsNotAhead()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Rwy28R();
        (AircraftState lead, double truePathNm) = AddFortyFiveEntryLead(engine, rwy, 2.0);
        double straightNm = Nm(lead.Position, Threshold(rwy));
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, (straightNm + truePathNm) / 2.0);
        double followerNm = AirborneFollowHelper.SequenceRemainingPathNm(follower, rwy);
        Assert.True((followerNm > straightNm) && (followerNm < truePathNm), $"follower {followerNm:F2}, lead {straightNm:F2}–{truePathNm:F2} nm");

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        Assert.False(result.Success, result.Message);
        Assert.Equal($"Unable, on approach for runway 28R, {Leader} is not ahead of us, request vectors", result.Message);
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    /// <summary>An approach follower 9 nm out behind a 45° entrant half a mile from its join: the entrant is ahead, FOLLOW is accepted.</summary>
    [Fact]
    public void ApproachFollower_EntryLeadWithShorterTruePath_IsAccepted()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Rwy28R();
        (AircraftState _, double truePathNm) = AddFortyFiveEntryLead(engine, rwy, 0.5);
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 9.0);
        Assert.True(truePathNm < AirborneFollowHelper.SequenceRemainingPathNm(follower, rwy), $"lead path {truePathNm:F2} nm");
        Phase? intercept = follower.Phases!.CurrentPhase;

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Same(intercept, follower.Phases!.CurrentPhase);
    }

    /// <summary>
    /// An approach follower 8 nm out behind a straight-in entrant joining the final 3 nm out: the entrant carries no pattern
    /// waypoints, is measured from the runway, is ahead, and FOLLOW is accepted.
    /// </summary>
    [Fact]
    public void ApproachFollower_StraightInEntrantAhead_IsAccepted()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Rwy28R();
        AircraftState lead = StraightInEntrant(rwy, "C172", 3.0, OffFinal(rwy, 4.0, 1.5));
        engine.World.AddAircraft(lead);
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 8.0);
        double leadNm = AirborneFollowHelper.SequenceRemainingPathNm(lead, rwy);
        Assert.True(leadNm < AirborneFollowHelper.SequenceRemainingPathNm(follower, rwy), $"lead path {leadNm:F2} nm");
        Phase? intercept = follower.Phases!.CurrentPhase;

        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Same(intercept, follower.Phases!.CurrentPhase);
    }

    /// <summary>
    /// An approach follower following a straight-in B738 entrant ahead of it by path keeps spacing on it once it captures the
    /// final: with the entrant inside the 3 nm desired distance, the follow speed comes out below the follower's unspaced
    /// final speed. The follow is set as a kept approach leaves it (only the lead recorded).
    /// </summary>
    [Fact]
    public void ApproachFollower_BehindStraightInEntrant_SlowsForSpacingAfterCapture()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Rwy28R();
        AircraftState lead = StraightInEntrant(rwy, "B738", 3.0, OffFinal(rwy, 3.0, 2.0));
        engine.World.AddAircraft(lead);
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 6.0);
        // A 30° intercept from 0.65 nm right of the final, so the capture comes before the entrant reaches its join even
        // though the follower slows for spacing on the entrant while it intercepts.
        follower.Position = OffFinal(rwy, 6.0, 0.65);
        double leadNm = AirborneFollowHelper.SequenceRemainingPathNm(lead, rwy);
        Assert.True(leadNm < AirborneFollowHelper.SequenceRemainingPathNm(follower, rwy), $"lead path {leadNm:F2} nm");
        follower.Approach.FollowingCallsign = Leader;
        follower.TrueHeading = rwy.TrueHeading - 30.0;
        follower.TrueTrack = follower.TrueHeading;

        TickUntilFinal(engine, follower, lead);

        Assert.IsType<FinalApproachPhase>(follower.Phases?.CurrentPhase);
        Assert.IsType<PatternEntryPhase>(lead.Phases?.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        output.WriteLine($"separation {Nm(follower.Position, lead.Position):F2} nm");
        Assert.True(Nm(follower.Position, lead.Position) < AirborneFollowHelper.DesiredDistanceForLeader(AircraftCategory.Jet));
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

    /// <summary>A C172 (<see cref="Follower"/>) on the 28R pattern final <paramref name="alongNm"/> out on the centerline.</summary>
    private static AircraftState FinalFollower(RunwayInfo rwy, double alongNm)
    {
        AircraftState follower = FlyAs(Follower, new Placement(rwy, "C172", OffFinal(rwy, alongNm, 0.0)), [], PatternEntryLeg.Final);
        follower.TrueHeading = rwy.TrueHeading;
        follower.TrueTrack = rwy.TrueHeading;
        Assert.IsType<FinalApproachPhase>(follower.Phases!.CurrentPhase);
        return follower;
    }

    [Fact]
    public void FinalFollower_EntryLeadAheadByPath_IsNotFlowBehind()
    {
        RunwayInfo rwy = Rwy28R();
        (AircraftState lead, double truePathNm) = FortyFiveEntrant(Leader, rwy, 0.5);
        AircraftState follower = FinalFollower(rwy, truePathNm + 1.0);

        Assert.False(AirborneFollowHelper.IsLeadPatternFlowBehind(follower, lead));
    }

    [Fact]
    public void FinalFollower_EntryLeadBehindByPath_IsFlowBehind()
    {
        RunwayInfo rwy = Rwy28R();
        (AircraftState lead, double truePathNm) = FortyFiveEntrant(Leader, rwy, 0.5);
        AircraftState follower = FinalFollower(rwy, truePathNm - 1.0);

        Assert.True(AirborneFollowHelper.IsLeadPatternFlowBehind(follower, lead));
    }

    /// <summary>
    /// Two entrants on one runway: a B738 starting its teardrop at midfield is nearer the threshold in a straight line than a
    /// 45° C172 entrant but has the longer path, so the order follows the path: the teardrop entrant is behind the 45°
    /// entrant, not the other way round.
    /// </summary>
    [Fact]
    public void FortyFiveEntrant_FollowingTeardropEntrantWithLongerPath_LeadIsBehind()
    {
        RunwayInfo rwy = Rwy28R();
        (AircraftState fortyFive, double _) = FortyFiveEntrant(Follower, rwy, 2.0);
        PatternWaypoints jetWp = Waypoints(rwy, "B738");
        AircraftState teardrop = FlyAs(
            Leader,
            new Placement(rwy, "B738", MidfieldCrossingPhase.MidfieldTarget(jetWp)),
            [new TeardropReentryPhase { Waypoints = jetWp }],
            PatternEntryLeg.Downwind
        );
        double fortyFiveNm = AirborneFollowHelper.SequenceRemainingPathNm(fortyFive, rwy);
        double teardropNm = AirborneFollowHelper.SequenceRemainingPathNm(teardrop, rwy);
        output.WriteLine($"45° entrant path {fortyFiveNm:F2} nm, teardrop entrant path {teardropNm:F2} nm");
        Assert.True(Nm(teardrop.Position, Threshold(rwy)) < Nm(fortyFive.Position, Threshold(rwy)));
        Assert.True(teardropNm > fortyFiveNm);

        Assert.True(AirborneFollowHelper.IsLeadPatternFlowBehind(fortyFive, teardrop));
        Assert.False(AirborneFollowHelper.IsLeadPatternFlowBehind(teardrop, fortyFive));
    }
}
