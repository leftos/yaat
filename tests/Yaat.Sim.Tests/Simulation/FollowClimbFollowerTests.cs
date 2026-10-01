using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// FOLLOW to a follower still climbing: a go-around that re-enters the pattern, or a closed-traffic takeoff climb off its
/// pattern runway. Both count as the circuit's upwind (leg 1): the climb is kept and only the lead is set, a lead behind is
/// refused with the climb's own wording, an entry lead is accepted, and two climbs with no circuit geometry are ordered by
/// along-track distance from the threshold. A lead leaving the sequence (a go-around that does not re-enter, or its published
/// missed) is refused as going around. A follower on a go-around that does not re-enter is refused when IFR and re-sequenced
/// in place as a pattern climb-out when VFR. A runwayless lead is taken by the ±60° cone, plus the downwind box when the
/// traffic side is known. While climbing, the lead lifecycle ends only the follow. Real KOAK 28R right traffic.
/// </summary>
public class FollowClimbFollowerTests(ITestOutputHelper output)
{
    private const string Leader = "LEAD1";
    private const string Follower = "FOLL1";

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    private static RunwayInfo Oak28R() =>
        NavigationDatabase.Instance.GetRunway("KOAK", "28R") ?? throw new InvalidOperationException("KOAK 28R missing from navdata");

    /// <summary>
    /// Point <paramref name="alongNm"/> out the final (past the threshold when negative) and <paramref name="crossNm"/> to the
    /// right.
    /// </summary>
    private static LatLon OffFinal(RunwayInfo rwy, double alongNm, double crossNm)
    {
        LatLon onCenterline = GeoMath.ProjectPoint(
            new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            rwy.TrueHeading.ToReciprocal(),
            alongNm
        );
        if (Math.Abs(crossNm) < 1e-9)
        {
            return onCenterline;
        }

        TrueHeading perpendicular = crossNm > 0 ? rwy.TrueHeading + 90.0 : rwy.TrueHeading - 90.0;
        return GeoMath.ProjectPoint(onCenterline, perpendicular, Math.Abs(crossNm));
    }

    private static AircraftState MakeAircraft(string callsign, LatLon position, TrueHeading heading, double altitude, string flightRules) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = 80,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                Departure = "KOAK",
                Destination = "KOAK",
                FlightRules = flightRules,
                Altitude = flightRules == "VFR" ? PlannedAltitude.Vfr(2000) : PlannedAltitude.Ifr(5000),
                CruiseSpeed = 110,
            },
        };

    private static List<Phase> RightCircuit(RunwayInfo rwy, PatternEntryLeg leg) =>
        PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategorization.Categorize("C172"),
            "C172",
            windSpeedKt: 0,
            PatternDirection.Right,
            leg,
            touchAndGo: true,
            finalDistanceNm: null,
            patternSizeNm: null,
            altitudeOverrideFt: null,
            NavigationDatabase.Instance.GetRunways(rwy.AirportId),
            authoredRunway: null
        );

    /// <summary>
    /// An aircraft on the 28R right circuit from <paramref name="leg"/>, placed where <paramref name="place"/> puts it on that
    /// circuit.
    /// </summary>
    private static AircraftState AddOnCircuit(
        SimulationEngine engine,
        string callsign,
        PatternEntryLeg leg,
        Func<PatternWaypoints, (LatLon, TrueHeading)> place
    )
    {
        RunwayInfo rwy = Oak28R();
        AircraftState ac = MakeAircraft(callsign, OffFinal(rwy, 1.0, 1.0), rwy.TrueHeading, 1000, "VFR");
        engine.World.AddAircraft(ac);
        ac.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Right };
        foreach (Phase phase in RightCircuit(rwy, leg))
        {
            ac.Phases.Add(phase);
        }

        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        PatternWaypoints wp = AirborneFollowHelper.PatternWaypointsOf(ac.Phases.CurrentPhase) ?? throw new InvalidOperationException("no waypoints");
        (LatLon position, TrueHeading heading) = place(wp);
        ac.Position = position;
        ac.TrueHeading = heading;
        ac.TrueTrack = heading;
        return ac;
    }

    private static (LatLon, TrueHeading) OutOnCrosswind(PatternWaypoints wp, double outNm) =>
        (GeoMath.ProjectPoint(new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon), wp.CrosswindHeading, outNm), wp.CrosswindHeading);

    private static (LatLon, TrueHeading) UpwindShortOfCrosswindTurn(PatternWaypoints wp, double shortNm) =>
        (GeoMath.ProjectPoint(new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon), wp.UpwindHeading.ToReciprocal(), shortNm), wp.UpwindHeading);

    private static (LatLon, TrueHeading) DownwindShortOfBaseTurn(PatternWaypoints wp, double shortNm) =>
        (GeoMath.ProjectPoint(new LatLon(wp.BaseTurnLat, wp.BaseTurnLon), wp.DownwindHeading.ToReciprocal(), shortNm), wp.DownwindHeading);

    /// <summary>
    /// An aircraft in a go-around off 28R <paramref name="pastThresholdNm"/> past the threshold on the centerline, climbing
    /// through 600 ft; re-entering the right pattern when <paramref name="reenter"/>, else flying the missed approach.
    /// </summary>
    private static AircraftState AddGoAround(SimulationEngine engine, string callsign, double pastThresholdNm, bool reenter, string flightRules)
    {
        RunwayInfo rwy = Oak28R();
        AircraftState ac = MakeAircraft(callsign, OffFinal(rwy, -pastThresholdNm, 0), rwy.TrueHeading, rwy.AirportElevationFt + 600, flightRules);
        engine.World.AddAircraft(ac);
        ac.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = reenter ? PatternDirection.Right : null };
        ac.Phases.Add(
            new GoAroundPhase
            {
                ReenterPattern = reenter,
                TargetAltitude = reenter ? (int)(rwy.AirportElevationFt + 700) : null,
                NextLandingFullStop = true,
            }
        );
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        return ac;
    }

    private static AircraftState AddGoAroundFollower(SimulationEngine engine, double pastThresholdNm, bool reenter, string flightRules)
    {
        AircraftState follower = AddGoAround(engine, Follower, pastThresholdNm, reenter, flightRules);
        follower.Approach.HasReportedTrafficInSight = true;
        return follower;
    }

    /// <summary>
    /// A follower airborne in its closed-traffic takeoff climb off 28R (right traffic, same runway), <paramref name="pastThresholdNm"/>
    /// past the threshold at 300 ft, with its right circuit queued from the upwind.
    /// </summary>
    private static AircraftState AddClosedClimbFollower(SimulationEngine engine, double pastThresholdNm)
    {
        RunwayInfo rwy = Oak28R();
        AircraftState follower = MakeAircraft(Follower, OffFinal(rwy, -pastThresholdNm, 0), rwy.TrueHeading, rwy.AirportElevationFt + 300, "VFR");
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        var takeoff = TakeoffPhase.FromSnapshot(
            new TakeoffPhaseDto
            {
                Status = (int)PhaseStatus.Active,
                ElapsedSeconds = 30,
                Airborne = true,
                FieldElevation = rwy.AirportElevationFt,
                RunwayHeadingDeg = rwy.TrueHeading.Degrees,
                ThresholdLat = rwy.ThresholdLatitude,
                ThresholdLon = rwy.ThresholdLongitude,
                Departure = new ClosedTrafficDepartureDto { Direction = (int)PatternDirection.Right },
            }
        );
        follower.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Right };
        follower.Phases.Add(takeoff); // CurrentIndex defaults to 0: the active takeoff is the current phase
        foreach (Phase phase in RightCircuit(rwy, PatternEntryLeg.Upwind))
        {
            follower.Phases.Add(phase);
        }

        Assert.True(AirborneFollowHelper.IsClosedTrafficClimb(follower));
        return follower;
    }

    /// <summary>The waypoints of the follower's queued circuit, or of a right circuit on 28R when it carries none.</summary>
    private static PatternWaypoints CircuitWaypoints(AircraftState ac) =>
        AirborneFollowHelper.FirstPatternWaypoints(ac.Phases)
        ?? PatternGeometry.Compute(
            Oak28R(),
            AircraftCategorization.Categorize("C172"),
            "C172",
            0,
            PatternDirection.Right,
            null,
            null,
            NavigationDatabase.Instance.GetRunways("KOAK"),
            null
        );

    private static AircraftState AddCrosswindLead(SimulationEngine engine) =>
        AddOnCircuit(engine, Leader, PatternEntryLeg.Crosswind, wp => OutOnCrosswind(wp, 0.5));

    /// <summary>Lead north of the field entering the 28R right downwind (ERD 28R), still on its pattern entry.</summary>
    private static AircraftState AddEntryLead(SimulationEngine engine)
    {
        AircraftState lead = MakeAircraft(Leader, OffFinal(Oak28R(), -2.0, 4.0), new TrueHeading(180), 1500, "VFR");
        engine.World.AddAircraft(lead);
        CommandResult setup = engine.SendCommand(Leader, "ERD 28R");
        Assert.True(setup.Success, setup.Message);
        Assert.IsType<PatternEntryPhase>(lead.Phases?.CurrentPhase);
        return lead;
    }

    /// <summary>A lead with no assigned runway, inbound to KOAK, at <paramref name="position"/> tracking <paramref name="heading"/>.</summary>
    private static AircraftState AddRunwaylessLead(SimulationEngine engine, LatLon position, TrueHeading heading)
    {
        AircraftState lead = MakeAircraft(Leader, position, heading, 1500, "VFR");
        engine.World.AddAircraft(lead);
        return lead;
    }

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

    /// <summary>The climb's state FOLLOW must keep: the phase list and instance, its count, the climb target and the clearance.</summary>
    private sealed record ClimbState(
        PhaseList? Phases,
        Phase? Current,
        int PhaseCount,
        double? TargetAltitude,
        ClearanceType? Clearance,
        PatternDirection? Side
    );

    private static ClimbState Capture(AircraftState ac) =>
        new(
            ac.Phases,
            ac.Phases?.CurrentPhase,
            ac.Phases?.Phases.Count ?? 0,
            ac.Targets.TargetAltitude,
            ac.Phases?.LandingClearance,
            ac.Phases?.TrafficDirection
        );

    private static void AssertKeptClimbFollowingLead(AircraftState follower, ClimbState before, CommandResult result)
    {
        Assert.True(result.Success, result.Message);
        Assert.Equal($"Follow {Leader}", result.Message);
        Assert.Equal(before, Capture(follower));
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    private static void AssertRefusedUnchanged(AircraftState follower, ClimbState before, CommandResult result, string expectedMessage)
    {
        Assert.False(result.Success, result.Message);
        Assert.Equal(expectedMessage, result.Message);
        Assert.Equal(before, Capture(follower));
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    private static string GoAroundNotAhead => $"Unable, on the go-around for runway 28R, {Leader} is not ahead of us, request vectors";

    private static string UpwindNotAhead => $"Unable, on upwind for runway 28R, {Leader} is not ahead of us, request vectors";

    private const string GoingAroundText = $"Unable, {Leader} is going around, request vectors";

    // ─── Rule 1: follower position, the climb kept ───

    [Fact]
    public void GoAroundFollower_LeadAhead_KeepsClimbAndSetsLead()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddGoAroundFollower(engine, 0.5, reenter: true, "VFR");
        AddCrosswindLead(engine);
        var goAround = (GoAroundPhase)follower.Phases!.CurrentPhase!;
        goAround.AssignedMagneticHeading = new MagneticHeading(310);
        int? targetAltitude = goAround.TargetAltitude;
        TrueHeading? headingTarget = follower.Targets.TargetTrueHeading;
        ClimbState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertKeptClimbFollowingLead(follower, before, result);
        Assert.Same(goAround, follower.Phases.CurrentPhase);
        Assert.Equal(targetAltitude, goAround.TargetAltitude);
        Assert.Equal(new MagneticHeading(310), goAround.AssignedMagneticHeading);
        Assert.Equal(headingTarget, follower.Targets.TargetTrueHeading);
        Assert.True(goAround.NextLandingFullStop);
        Assert.True(goAround.ReenterPattern);
    }

    [Fact]
    public void GoAroundFollower_LeadBehind_RefusedWithGoAroundText()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState lead = AddOnCircuit(engine, Leader, PatternEntryLeg.Upwind, wp => UpwindShortOfCrosswindTurn(wp, 1.0));
        AircraftState follower = AddGoAroundFollower(engine, 0.5, reenter: true, "VFR");
        (LatLon ahead, TrueHeading heading) = UpwindShortOfCrosswindTurn(CircuitWaypoints(lead), 0.2);
        follower.Position = ahead;
        follower.TrueTrack = heading;
        ClimbState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, GoAroundNotAhead);
    }

    [Fact]
    public void ClosedTrafficClimb_LeadBehind_RefusedWithUpwindText()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClosedClimbFollower(engine, 1.2);
        AddGoAround(engine, Leader, 0.2, reenter: true, "VFR");
        ClimbState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, UpwindNotAhead);
    }

    [Fact]
    public void ClosedTrafficClimb_LeadAhead_KeepsClimb()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClosedClimbFollower(engine, 1.0);
        AddCrosswindLead(engine);
        Phase takeoff = follower.Phases!.CurrentPhase!;
        ClimbState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertKeptClimbFollowingLead(follower, before, result);
        Assert.Same(takeoff, follower.Phases.CurrentPhase);
    }

    /// <summary>An entry lead is accepted from the climb, and the running follow keeps it flow-behind until it joins.</summary>
    [Fact]
    public void ClimbFollower_EntryLead_Accepted()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddGoAroundFollower(engine, 0.5, reenter: true, "VFR");
        AircraftState lead = AddEntryLead(engine);
        ClimbState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertKeptClimbFollowingLead(follower, before, result);
        Assert.True(AirborneFollowHelper.IsLeadPatternFlowBehind(follower, lead));
    }

    // ─── Rule 4: two climbs with no circuit geometry ───

    [Fact]
    public void TwoGoArounds_OrderedByAlongTrackFromThreshold()
    {
        SimulationEngine aheadEngine = BuildEngine();
        AircraftState follower = AddGoAroundFollower(aheadEngine, 0.5, reenter: true, "VFR");
        AircraftState leadAhead = AddGoAround(aheadEngine, Leader, 1.5, reenter: true, "VFR");
        ClimbState before = Capture(follower);

        CommandResult accepted = Send(aheadEngine, $"FOLLOW {Leader}");

        AssertKeptClimbFollowingLead(follower, before, accepted);
        Assert.False(AirborneFollowHelper.IsLeadPatternFlowBehind(follower, leadAhead), "the running follow must keep the lead ahead");

        SimulationEngine behindEngine = BuildEngine();
        AircraftState aheadFollower = AddGoAroundFollower(behindEngine, 1.5, reenter: true, "VFR");
        AircraftState leadBehind = AddGoAround(behindEngine, Leader, 0.5, reenter: true, "VFR");
        ClimbState beforeRefusal = Capture(aheadFollower);

        CommandResult refused = Send(behindEngine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(aheadFollower, beforeRefusal, refused, GoAroundNotAhead);
        Assert.True(AirborneFollowHelper.IsLeadPatternFlowBehind(aheadFollower, leadBehind), "the running follow must keep the lead behind");
    }

    // ─── Rule 2: a lead leaving the sequence ───

    [Theory]
    [InlineData("pattern")]
    [InlineData("go-around")]
    [InlineData("closed-climb")]
    public void NonReenteringGoAroundLead_RefusedGoingAround(string followerKind)
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = followerKind switch
        {
            "pattern" => AddOnCircuit(engine, Follower, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 1.0)),
            "go-around" => AddGoAroundFollower(engine, 0.5, reenter: true, "VFR"),
            _ => AddClosedClimbFollower(engine, 1.0),
        };
        follower.Approach.HasReportedTrafficInSight = true;
        AddGoAround(engine, Leader, 1.5, reenter: false, "IFR");
        ClimbState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, GoingAroundText);
    }

    // ─── Rule 3: a follower on a go-around that does not re-enter ───

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissedApproachGoAround_IfrFollower_RefusedOnTheMissed(bool atcHeading)
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddGoAroundFollower(engine, 0.5, reenter: false, "IFR");
        if (atcHeading)
        {
            ((GoAroundPhase)follower.Phases!.CurrentPhase!).AssignedMagneticHeading = new MagneticHeading(310);
        }

        AddCrosswindLead(engine);
        ClimbState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, "Unable, on the missed approach, request vectors");
        Assert.False(((GoAroundPhase)follower.Phases!.CurrentPhase!).ReenterPattern);
    }

    /// <summary>An IFR follower on its own missed approach is refused as such whoever the lead is, one going around included.</summary>
    [Fact]
    public void MissedApproachGoAround_IfrFollower_LeadGoingAround_RefusedOnTheMissed()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddGoAroundFollower(engine, 0.5, reenter: false, "IFR");
        AddGoAround(engine, Leader, 1.5, reenter: false, "IFR");
        ClimbState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, "Unable, on the missed approach, request vectors");
    }

    /// <summary>
    /// A VFR follower on its published missed approach, told to follow a lead whose go-around leaves the sequence: refused as
    /// going around, with the missed kept, rather than torn down for a pursuit.
    /// </summary>
    [Fact]
    public void PublishedMissedApproach_VfrFollower_LeadGoingAround_RefusedGoingAround()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        AircraftState follower = MakeAircraft(Follower, OffFinal(rwy, -2.0, 0), rwy.TrueHeading, 1500, "VFR");
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        follower.Phases = new PhaseList { AssignedRunway = rwy };
        follower.Phases.Add(new ApproachNavigationPhase { Fixes = [], IsMissedApproach = true }); // CurrentIndex 0: the current phase
        AddGoAround(engine, Leader, 1.0, reenter: false, "IFR");
        ClimbState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, GoingAroundText);
    }

    [Fact]
    public void MissedApproachGoAround_VfrFollower_ResequencedAsPatternClimbOut()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddGoAroundFollower(engine, 0.5, reenter: false, "VFR");
        follower.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.ClearedRunwayId = "28R";
        var goAround = (GoAroundPhase)follower.Phases.CurrentPhase!;
        AddCrosswindLead(engine);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Follow {Leader}", result.Message);
        Assert.Same(goAround, follower.Phases!.CurrentPhase);
        Assert.True(goAround.ReenterPattern);
        Assert.NotNull(goAround.TargetAltitude);
        Assert.Equal(PatternDirection.Right, follower.Phases.TrafficDirection);
        Assert.Null(follower.Phases.LandingClearance);
        Assert.Null(follower.Phases.ClearedRunwayId);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Equal(1, AirborneFollowHelper.PatternLegIndex(follower));
    }

    // ─── Rule 5: a runwayless lead ───

    [Fact]
    public void ClimbFollower_RunwaylessLead_ConeAccepts()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        AircraftState follower = AddGoAroundFollower(engine, 0.5, reenter: true, "VFR");
        AddRunwaylessLead(engine, OffFinal(rwy, -3.0, 0.3), rwy.TrueHeading);
        ClimbState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertKeptClimbFollowingLead(follower, before, result);
    }

    /// <summary>The traffic side is known, so a runwayless lead on the right downwind line, behind the climb's track, is accepted.</summary>
    [Fact]
    public void ClimbFollower_RunwaylessLeadInDownwindBox_Accepted()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddGoAroundFollower(engine, 0.5, reenter: true, "VFR");
        (LatLon position, TrueHeading heading) = DownwindShortOfBaseTurn(CircuitWaypoints(follower), 1.0);
        AircraftState lead = AddRunwaylessLead(engine, position, heading);
        Assert.False(AirborneFollowHelper.IsLeadAheadOfTrack(follower, lead), "the lead must sit outside the cone for the box to decide");
        ClimbState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertKeptClimbFollowingLead(follower, before, result);
    }

    [Fact]
    public void ClimbFollower_RunwaylessLead_OutsideCone_Refused()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        AircraftState follower = AddClosedClimbFollower(engine, 1.0);
        AddRunwaylessLead(engine, OffFinal(rwy, 3.0, -2.0), rwy.TrueHeading);
        ClimbState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, UpwindNotAhead);
    }

    /// <summary>
    /// A runwayless lead queued to enter the 28L right downwind re-sequences a climb follower onto 28L, as it re-sequences an
    /// upwind follower (the leg the climb hands over to): the same answer before and after the hand-over.
    /// </summary>
    [Theory]
    [InlineData("go-around")]
    [InlineData("closed-climb")]
    [InlineData("upwind")]
    public void ClimbFollower_LeadQueuedForAnotherRunway_ResequencedLikeUpwind(string followerKind)
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = followerKind switch
        {
            "go-around" => AddGoAroundFollower(engine, 0.5, reenter: true, "VFR"),
            "closed-climb" => AddClosedClimbFollower(engine, 1.0),
            _ => AddOnCircuit(engine, Follower, PatternEntryLeg.Upwind, wp => UpwindShortOfCrosswindTurn(wp, 1.0)),
        };
        follower.Approach.HasReportedTrafficInSight = true;
        RunwayInfo rwy = Oak28R();
        AircraftState lead = AddRunwaylessLead(engine, OffFinal(rwy, 3.0, 2.0), rwy.TrueHeading);
        CommandResult setup = engine.SendCommand(Leader, "DCT VPCBT; ERD 28L");
        Assert.True(setup.Success, setup.Message);
        Assert.Equal(("28L", PatternDirection.Right), PatternCommandHandler.QueuedPatternEntry(lead));

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.Equal("28L", follower.Phases!.AssignedRunway?.Designator);
        Assert.Equal(PatternDirection.Right, follower.Phases.TrafficDirection);
        Assert.True(
            follower.Phases.CurrentPhase is PatternEntryPhase or MidfieldCrossingPhase or DownwindPhase,
            $"expected a downwind entry to 28L, got {follower.Phases.CurrentPhase?.GetType().Name}"
        );
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    // ─── The climbs FOLLOW does not keep: the install clears the chain ───

    /// <summary>
    /// A re-entering go-around told to follow a lead on the 28L left downwind: in-trail has no meaning on its own runway, so
    /// the go-around is cleared and the follower enters the lead's pattern, as before.
    /// </summary>
    [Fact]
    public void GoAroundFollower_LeadOnAnotherRunway_ChainClearedAndEntersLeadPattern()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddGoAroundFollower(engine, 0.5, reenter: true, "VFR");
        RunwayInfo rwy28L = NavigationDatabase.Instance.GetRunway("KOAK", "28L") ?? throw new InvalidOperationException("KOAK 28L missing");
        AircraftState lead = MakeAircraft(Leader, OffFinal(rwy28L, -1.0, -1.0), rwy28L.TrueHeading.ToReciprocal(), 1000, "VFR");
        engine.World.AddAircraft(lead);
        lead.Phases = new PhaseList { AssignedRunway = rwy28L, TrafficDirection = PatternDirection.Left };
        foreach (
            Phase phase in PatternBuilder.BuildCircuit(
                rwy28L,
                AircraftCategorization.Categorize("C172"),
                "C172",
                windSpeedKt: 0,
                PatternDirection.Left,
                PatternEntryLeg.Downwind,
                touchAndGo: true,
                finalDistanceNm: null,
                patternSizeNm: null,
                altitudeOverrideFt: null,
                NavigationDatabase.Instance.GetRunways("KOAK"),
                authoredRunway: null
            )
        )
        {
            lead.Phases.Add(phase);
        }

        lead.Phases.Start(CommandDispatcher.BuildMinimalContext(lead));
        Phase goAround = follower.Phases!.CurrentPhase!;

        CommandResult result = Send(engine, $"FOLLOW {Leader}");
        output.WriteLine($"warnings: {string.Join(" | ", follower.PendingWarnings)}");

        Assert.True(result.Success, result.Message);
        Assert.NotSame(goAround, follower.Phases?.CurrentPhase);
        Assert.Equal("28L", follower.Phases?.AssignedRunway?.Designator);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Contains(follower.PendingWarnings, w => w.Contains("cancelled by FOLLOW", StringComparison.Ordinal));
    }

    /// <summary>
    /// Rule 3's fallback: a VFR follower on a go-around that does not re-enter, told to follow a lead flying no circuit (no
    /// runway), is not re-sequenced in place; the go-around is cleared and the follower pursues the lead, as before.
    /// </summary>
    [Fact]
    public void MissedApproachGoAround_VfrFollower_LeadFliesNoCircuit_ChainClearedToPursuit()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        AircraftState follower = AddGoAroundFollower(engine, 0.5, reenter: false, "VFR");
        AircraftState lead = AddLeadAheadRight(engine, follower);
        HoldGeometry hold = HoldFrom(CircuitWaypoints(follower), clearedAltitude: null);
        Phase goAround = follower.Phases!.CurrentPhase!;

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.NotSame(goAround, follower.Phases?.CurrentPhase);
        Assert.IsType<VfrFollowPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Contains(follower.PendingWarnings, w => w.Contains("cancelled by FOLLOW", StringComparison.Ordinal));

        // The chain-clearing install owes the departure leg too: the runway's own end, no circuit of its own to read.
        TickThroughDepartureLegHold(engine, follower, lead, rwy, hold);
    }

    // ─── Rule 7: the lead lifecycle while climbing ───

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClimbFollower_LeadLands_FollowEndsClimbKept(bool closedClimb)
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = closedClimb ? AddClosedClimbFollower(engine, 1.0) : AddGoAroundFollower(engine, 0.5, reenter: true, "VFR");
        AircraftState lead = AddCrosswindLead(engine);
        Phase climb = follower.Phases!.CurrentPhase!;
        Assert.True(Send(engine, $"FOLLOW {Leader}").Success);

        lead.Phases = null;
        lead.IsOnGround = true;
        lead.IndicatedAirspeed = 0;
        TickSeconds(engine, 1);

        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Same(climb, follower.Phases!.CurrentPhase);
    }

    [Fact]
    public void ClimbFollower_IfrLeadLost_VisualSeparationTerminated()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddGoAroundFollower(engine, 0.5, reenter: true, "IFR");
        AddCrosswindLead(engine);
        Phase climb = follower.Phases!.CurrentPhase!;
        Assert.True(Send(engine, $"FOLLOW {Leader}").Success);

        engine.RemoveFromWorld(Leader);
        TickSeconds(engine, 1);
        output.WriteLine($"warnings: {string.Join(" | ", follower.PendingWarnings)}");

        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Same(climb, follower.Phases!.CurrentPhase);
        Assert.Contains($"{Follower} visual separation terminated — radar separation required", follower.PendingWarnings);
    }

    // ─── A refused FOLLOW leaves the chain untouched ───

    /// <summary>
    /// A VFR follower on a go-around that does not re-enter, farther out along the upwind than its lead: the re-sequence would
    /// make it the upwind, where that lead is behind it, so FOLLOW is refused, and neither the dry-run nor the real dispatch
    /// converts the go-around.
    /// </summary>
    [Fact]
    public void ClimbFollower_RefusedFollow_DryRunLeavesChainUntouched()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState lead = AddOnCircuit(engine, Leader, PatternEntryLeg.Upwind, wp => UpwindShortOfCrosswindTurn(wp, 1.0));
        AircraftState follower = AddGoAroundFollower(engine, 0.5, reenter: false, "VFR");
        (LatLon ahead, TrueHeading heading) = UpwindShortOfCrosswindTurn(CircuitWaypoints(lead), 0.2);
        follower.Position = ahead;
        follower.TrueTrack = heading;
        var goAround = (GoAroundPhase)follower.Phases!.CurrentPhase!;
        ClimbState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, GoAroundNotAhead);
        Assert.Same(goAround, follower.Phases!.CurrentPhase);
        Assert.False(goAround.ReenterPattern);
        Assert.Null(goAround.TargetAltitude);
        Assert.Null(follower.Pattern.TrafficDirection);
    }

    // ─── The pending pursuit a runwayless lead leaves on the climb ───

    private const string OtherLeader = "LEAD2";

    /// <summary>The pursuit a climb is holding off, as it is armed on the climbing phase itself.</summary>
    private static bool PendingPursuitOf(AircraftState ac) =>
        ac.Phases?.CurrentPhase is IPendingPursuitClimb { PursuesRunwaylessLeadAfterClimb: true };

    /// <summary>Ticks a departure-leg hold test will wait through — 4 sub-ticks a second, so this is 150 s of sim time.</summary>
    private const int MaxHoldTicks = 600;

    /// <summary>Degrees the follower may drift off the upwind heading while the departure-leg hold applies.</summary>
    private const double HoldingHeadingToleranceDeg = 5.0;

    /// <summary>
    /// Degrees off the runway heading that count as the pursuit having begun its turn toward its lead. Its direction is asserted
    /// separately, against the side the lead is on: a slow climb-out banks gently, so the angle alone would prove little.
    /// </summary>
    private const double TurnedHeadingDeg = 10.0;

    /// <summary>Ticks (4 sub-ticks a second) within which the pursuit must start its turn once the gate clears: 20 s (AIM §4-3-2).</summary>
    private const int MaxTurnTicks = 80;

    /// <summary>Degrees off the runway heading <paramref name="ac"/> is flying.</summary>
    private static double HeadingOffRunway(AircraftState ac, RunwayInfo rwy) => Math.Abs(rwy.TrueHeading.SignedAngleTo(ac.TrueHeading));

    /// <summary>
    /// The departure leg a hold test waits through: the point to fly past, the heading held until then, the gate's own minimum
    /// turn altitude and the altitude the controller has cleared the aircraft to, when that is lower (a CM or DM below TPA − 300).
    /// </summary>
    private sealed record HoldGeometry(LatLon DepartureEnd, TrueHeading UpwindHeading, double MinTurnAltitude, double? ClearedAltitude);

    /// <summary>The hold a circuit's waypoints put on a pursuit: its crosswind-turn point, upwind heading and pattern altitude less 300 ft.</summary>
    private static HoldGeometry HoldFrom(PatternWaypoints circuit, double? clearedAltitude) =>
        new(new LatLon(circuit.CrosswindTurnLat, circuit.CrosswindTurnLon), circuit.UpwindHeading, circuit.PatternAltitude - 300.0, clearedAltitude);

    /// <summary>What a hold test observed, for the callers that assert on it.</summary>
    private sealed record HoldOutcome(int HoldTicks, bool HeldPastDepartureEndBelowTurnAltitude);

    /// <summary>
    /// True when <paramref name="ac"/> has flown over the departure end: the bearing to the point more than 90° off the upwind
    /// heading (AIM §4-3-2). Written out here rather than read back through the production helper, so a wrong extraction of that
    /// condition fails this test.
    /// </summary>
    private static bool PastDepartureEnd(AircraftState ac, HoldGeometry geometry) =>
        Math.Abs(GeoMath.SignedBearingDifference(GeoMath.BearingTo(ac.Position, geometry.DepartureEnd), geometry.UpwindHeading.Degrees)) > 90.0;

    /// <summary>
    /// The altitude at or above which the hold ends: pattern altitude less 300 ft, or a lower clearance reached within the
    /// physics' capture tolerance (<see cref="FlightPhysics.AltitudeSnapFt"/>, read here as "reached").
    /// </summary>
    private static double LegalTurnAltitude(HoldGeometry geometry) =>
        (geometry.ClearedAltitude is { } cleared) && (cleared < geometry.MinTurnAltitude)
            ? cleared - FlightPhysics.AltitudeSnapFt
            : geometry.MinTurnAltitude;

    /// <summary>True while the departure-leg hold must still be holding: short of the departure end, or below a legal turn altitude.</summary>
    private static bool HoldsDepartureLeg(AircraftState ac, HoldGeometry geometry) =>
        !PastDepartureEnd(ac, geometry) || (ac.Altitude < LegalTurnAltitude(geometry));

    /// <summary>
    /// Ticks the pursuit through its departure-leg hold: while the hold applies the follower must stay on the upwind heading;
    /// then the gate must clear, the pursuit must still be following its lead and must start its turn within 20 s, on the same
    /// side of the upwind heading the lead is. The gate and its geometry are checked before the first tick, so the wiring is
    /// covered too.
    /// </summary>
    private static HoldOutcome TickThroughDepartureLegHold(
        SimulationEngine engine,
        AircraftState follower,
        AircraftState lead,
        RunwayInfo rwy,
        HoldGeometry geometry
    )
    {
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        FollowClimbOutGate gate = pursuit.ClimbOutGate ?? throw new InvalidOperationException("the pursuit must hold the departure leg");
        Assert.Equal(geometry.DepartureEnd.Lat, gate.DepartureEnd.Lat, 6);
        Assert.Equal(geometry.DepartureEnd.Lon, gate.DepartureEnd.Lon, 6);
        Assert.Equal(geometry.UpwindHeading.Degrees, gate.UpwindHeading.Degrees, 3);
        Assert.Equal(geometry.MinTurnAltitude, gate.MinTurnAltitude, 1);

        int holdTicks = 0;
        bool heldPastDepartureEndBelowTurnAltitude = false;
        while (HoldsDepartureLeg(follower, geometry) && (holdTicks < MaxHoldTicks))
        {
            engine.TickPhysics(0.25);
            if (HoldsDepartureLeg(follower, geometry))
            {
                holdTicks++;
                Assert.True(
                    HeadingOffRunway(follower, rwy) <= HoldingHeadingToleranceDeg,
                    $"the departure-leg hold must keep the upwind heading; off by {HeadingOffRunway(follower, rwy):F0}° after {holdTicks} ticks"
                );
                heldPastDepartureEndBelowTurnAltitude |= PastDepartureEnd(follower, geometry) && (follower.Altitude < geometry.MinTurnAltitude);
            }
        }

        Assert.True(holdTicks > 0, "the departure-leg hold must last at least one tick, or nothing was held");
        Assert.True(PastDepartureEnd(follower, geometry), "the follower must fly past the departure end");

        int turnTicks = 0;
        while ((turnTicks < MaxTurnTicks) && (HeadingOffRunway(follower, rwy) <= TurnedHeadingDeg))
        {
            engine.TickPhysics(0.25);
            turnTicks++;
        }

        Assert.True(
            (turnTicks < MaxTurnTicks) && (HeadingOffRunway(follower, rwy) > TurnedHeadingDeg),
            $"the pursuit must start its turn toward the lead within 20 s; still {HeadingOffRunway(follower, rwy):F0}° off the runway heading"
        );

        double sideToLead = rwy.TrueHeading.SignedAngleTo(new TrueHeading(GeoMath.BearingTo(follower.Position, lead.Position)));
        double sideTurned = rwy.TrueHeading.SignedAngleTo(follower.TrueHeading);
        Assert.Equal(Math.Sign(sideToLead), Math.Sign(sideTurned));
        Assert.Equal(lead.Callsign, follower.Approach.FollowingCallsign);
        Assert.Null(pursuit.ClimbOutGate);
        return new HoldOutcome(holdTicks, heldPastDepartureEndBelowTurnAltitude);
    }

    /// <summary>
    /// Place the closed-traffic climb just past its 400 ft completion so it hands over on its next tick; the climb's own
    /// integration is not what these tests are about.
    /// </summary>
    private static void ReadyClosedClimbToComplete(AircraftState follower) => follower.Altitude = Oak28R().AirportElevationFt + 410.0;

    /// <summary>The same for the go-around climb, just past its target altitude (pattern altitude less the AIM 4-3-2 margin).</summary>
    private static void ReadyGoAroundToComplete(AircraftState follower) => follower.Altitude = Oak28R().AirportElevationFt + 720.0;

    /// <summary>A runwayless lead dead ahead of a climb on the 28R centerline, inside its ±60° cone.</summary>
    private static AircraftState AddLeadAheadOfClimb(SimulationEngine engine)
    {
        RunwayInfo rwy = Oak28R();
        return AddRunwaylessLead(engine, OffFinal(rwy, -3.0, 0.0), rwy.TrueHeading);
    }

    /// <summary>
    /// Land the lead on 28R: it is put on the runway just past the threshold and marked down, so the landing the follow ends
    /// on happens on a runway rather than out over the bay.
    /// </summary>
    private static void LandOn28R(AircraftState lead)
    {
        RunwayInfo rwy = Oak28R();
        lead.Position = OffFinal(rwy, -0.05, 0.0);
        lead.Phases = null;
        lead.IsOnGround = true;
        lead.IndicatedAirspeed = 0;
    }

    /// <summary>
    /// FOLLOW of a runwayless lead keeps the climb and leaves the pursuit pending on it: nothing is installed from a few
    /// hundred feet, and the phase instance is untouched.
    /// </summary>
    [Theory]
    [InlineData("closed-climb")]
    [InlineData("go-around")]
    public void ClimbWithRunwaylessLead_AcceptKeepsClimbAndArmsPending(string followerKind)
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower =
            followerKind == "go-around" ? AddGoAroundFollower(engine, 0.5, reenter: true, "VFR") : AddClosedClimbFollower(engine, 1.0);
        AddLeadAheadOfClimb(engine);
        Phase climb = follower.Phases!.CurrentPhase!;
        ClimbState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertKeptClimbFollowingLead(follower, before, result);
        Assert.Same(climb, follower.Phases!.CurrentPhase);
        Assert.True(PendingPursuitOf(follower), "the pursuit must be pending on the climb, not started");
    }

    /// <summary>
    /// The pending pursuit starts when the closed-traffic climb hands over to its circuit's upwind: a free pursuit with the
    /// circuit's pattern return, carrying the runway, direction and altitude the climb was making, and the landing clearance.
    /// </summary>
    [Fact]
    public void PendingPursuit_StartsAtHandOverToUpwind_FromTakeoff()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        AircraftState follower = AddClosedClimbFollower(engine, 1.0);
        follower.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.ClearedRunwayId = "28R";
        AddLeadAheadOfClimb(engine);
        Assert.True(Send(engine, $"FOLLOW {Leader}").Success);
        PatternWaypoints circuit = CircuitWaypoints(follower);
        ReadyClosedClimbToComplete(follower);

        TickSeconds(engine, 1);

        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, pursuit.TargetCallsign);
        FollowPatternReturn patternReturn = pursuit.PatternReturn ?? throw new InvalidOperationException("the pursuit must carry a pattern return");
        Assert.Equal("28R", patternReturn.Runway.Designator);
        Assert.Equal(PatternDirection.Right, patternReturn.Direction);
        Assert.Equal(circuit.PatternAltitude, patternReturn.PatternAltitudeFt, 1);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
        Assert.Equal("28R", follower.Phases.ClearedRunwayId);

        // A few hundred feet over the runway, the pursuit owes the departure leg: no steering at the lead yet.
        Assert.NotNull(pursuit.ClimbOutGate);
        Assert.Equal(rwy.TrueHeading, follower.Targets.TargetTrueHeading);
    }

    /// <summary>The same through a re-entering go-around, which hands over to the upwind like any other re-entry.</summary>
    [Fact]
    public void PendingPursuit_StartsAtHandOverToUpwind_FromGoAround()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddGoAroundFollower(engine, 0.5, reenter: true, "VFR");
        AddLeadAheadOfClimb(engine);
        Assert.True(Send(engine, $"FOLLOW {Leader}").Success);
        PatternWaypoints circuit = CircuitWaypoints(follower);
        ReadyGoAroundToComplete(follower);

        TickSeconds(engine, 1);

        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, pursuit.TargetCallsign);
        FollowPatternReturn patternReturn = pursuit.PatternReturn ?? throw new InvalidOperationException("the pursuit must carry a pattern return");
        Assert.Equal("28R", patternReturn.Runway.Designator);
        Assert.Equal(PatternDirection.Right, patternReturn.Direction);
        Assert.Equal(circuit.PatternAltitude, patternReturn.PatternAltitudeFt, 1);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.NotNull(pursuit.ClimbOutGate);

        // The control: the same go-around with no FOLLOW hands over to its circuit's upwind as well, so the pursuit above is
        // the follow's doing and not the hand-over's.
        SimulationEngine controlEngine = BuildEngine();
        AircraftState control = AddGoAround(controlEngine, Follower, 0.5, reenter: true, "VFR");
        ReadyGoAroundToComplete(control);
        TickSeconds(controlEngine, 1);
        Assert.IsType<UpwindPhase>(control.Phases!.CurrentPhase);
    }

    /// <summary>
    /// The lead lands on 28R while the climb is still flying: the follow ends, the pending pursuit is disarmed and the climb
    /// hands over to its own circuit instead of pursuing. The disarm is read on the climb the cancel left in place, so a
    /// missing disarm fails here rather than being masked by the hand-over.
    /// </summary>
    [Fact]
    public void PendingPursuit_CancelledWhenLeadLands()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClosedClimbFollower(engine, 1.0);
        AircraftState lead = AddLeadAheadOfClimb(engine);
        Assert.True(Send(engine, $"FOLLOW {Leader}").Success);
        Assert.True(PendingPursuitOf(follower));

        LandOn28R(lead);
        TickSeconds(engine, 1);

        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.IsType<TakeoffPhase>(follower.Phases!.CurrentPhase);
        Assert.False(PendingPursuitOf(follower), "the cancel must disarm the climb, not just drop the follow");

        ReadyClosedClimbToComplete(follower);
        TickSeconds(engine, 1);

        Assert.IsNotType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.IsType<UpwindPhase>(follower.Phases.CurrentPhase);
    }

    /// <summary>The lead despawns while the climb is still flying: the same disarm and the same untouched hand-over.</summary>
    [Fact]
    public void PendingPursuit_CancelledWhenLeadLost()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClosedClimbFollower(engine, 1.0);
        AddLeadAheadOfClimb(engine);
        Assert.True(Send(engine, $"FOLLOW {Leader}").Success);
        Assert.True(PendingPursuitOf(follower));

        engine.RemoveFromWorld(Leader);
        TickSeconds(engine, 1);

        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.IsType<TakeoffPhase>(follower.Phases!.CurrentPhase);
        Assert.False(PendingPursuitOf(follower), "the cancel must disarm the climb, not just drop the follow");

        ReadyClosedClimbToComplete(follower);
        TickSeconds(engine, 1);

        Assert.IsNotType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.IsType<UpwindPhase>(follower.Phases.CurrentPhase);
    }

    /// <summary>
    /// A same-runway re-FOLLOW while climbing keeps the climb for the new lead, which flies the follower's own runway: the
    /// pending pursuit of the runwayless lead is disarmed, and the hand-over sequences in trail instead.
    /// </summary>
    [Fact]
    public void PendingPursuit_CancelledBySameRunwayReFollow()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClosedClimbFollower(engine, 1.0);
        AddLeadAheadOfClimb(engine);
        Assert.True(Send(engine, $"FOLLOW {Leader}").Success);
        Assert.True(PendingPursuitOf(follower));

        AddOnCircuit(engine, OtherLeader, PatternEntryLeg.Crosswind, wp => OutOnCrosswind(wp, 0.5));
        CommandResult refollow = Send(engine, $"FOLLOW {OtherLeader}");

        Assert.True(refollow.Success, refollow.Message);
        Assert.Equal(OtherLeader, follower.Approach.FollowingCallsign);
        Assert.False(PendingPursuitOf(follower), "the new lead flies a runway, so nothing is pending after the hand-over");
        ReadyClosedClimbToComplete(follower);

        TickSeconds(engine, 1);

        Assert.IsNotType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.IsType<UpwindPhase>(follower.Phases.CurrentPhase);
        Assert.Equal(OtherLeader, follower.Approach.FollowingCallsign);
    }

    /// <summary>The armed flag survives a mid-climb snapshot round trip, and the hand-over still starts the pursuit.</summary>
    [Fact]
    public void PendingPursuit_SurvivesSnapshotRoundTrip()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClosedClimbFollower(engine, 1.0);
        AddLeadAheadOfClimb(engine);
        Assert.True(Send(engine, $"FOLLOW {Leader}").Success);

        follower.Phases = PhaseList.FromSnapshot(follower.Phases!.ToSnapshot(), groundLayout: null);
        Assert.True(PendingPursuitOf(follower), "the flag must survive the snapshot");
        ReadyClosedClimbToComplete(follower);

        TickSeconds(engine, 1);

        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, pursuit.TargetCallsign);
        Assert.NotNull(pursuit.PatternReturn);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    // ─── The departure leg a pursuit started on the upwind owes (AIM 4-3-2.c.1, FIG 4-3-2 keys 4–5) ───

    /// <summary>A runwayless lead out in the follower's downwind box, off its track: the box, not the cone, decides it, and
    /// steering at it takes a turn well past the upwind heading.</summary>
    private static AircraftState AddLeadInDownwindBox(SimulationEngine engine, AircraftState follower) =>
        AddRunwaylessLeadNamed(engine, follower, Leader);

    /// <summary><see cref="AddLeadInDownwindBox"/> under its own callsign, for a test that follows two leads in turn.</summary>
    private static AircraftState AddRunwaylessLeadNamed(SimulationEngine engine, AircraftState follower, string callsign)
    {
        (LatLon position, TrueHeading heading) = DownwindShortOfBaseTurn(CircuitWaypoints(follower), 1.0);
        AircraftState lead = MakeAircraft(callsign, position, heading, 1500, "VFR");
        engine.World.AddAircraft(lead);
        Assert.False(AirborneFollowHelper.IsLeadAheadOfTrack(follower, lead), "the lead must sit outside the cone for the box to decide");
        return lead;
    }

    /// <summary>
    /// A runwayless lead two miles ahead of the follower and one to the right of its track: inside the ±60° cone, and far enough
    /// off the track that steering at it is unambiguously a right turn, which a lead in the downwind box need not be — the
    /// pursuit's steering point is then a spacing excursion off the lead's own track rather than the lead itself.
    /// </summary>
    private static AircraftState AddLeadAheadRight(SimulationEngine engine, AircraftState follower)
    {
        LatLon ahead = GeoMath.ProjectPoint(follower.Position, follower.TrueHeading, 2.0);
        LatLon position = GeoMath.ProjectPoint(ahead, follower.TrueHeading + 90.0, 1.0);
        AircraftState lead = MakeAircraft(Leader, position, follower.TrueHeading, 1500, "VFR");
        engine.World.AddAircraft(lead);
        Assert.True(AirborneFollowHelper.IsLeadAheadOfTrack(follower, lead), "the lead must sit inside the cone");
        return lead;
    }

    /// <summary>
    /// A pursuit that starts at the closed-traffic climb's hand-over holds the departure leg: with the lead out in the
    /// downwind, the follower keeps the upwind heading and climbs to pattern altitude less 300 ft before it steers at all.
    /// </summary>
    [Fact]
    public void PendingPursuit_FromClosedClimb_HoldsUpwindUntilPastDepartureEndAndTpaMinus300()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        // Placed further along the runway than the other fixtures: the departure end arrives well before pattern altitude less
        // 300 ft, so the altitude half is shown holding the pursuit on its own.
        AircraftState follower = AddClosedClimbFollower(engine, 1.4);
        AircraftState lead = AddLeadInDownwindBox(engine, follower);
        Assert.True(Send(engine, $"FOLLOW {Leader}").Success);
        HoldGeometry hold = HoldFrom(CircuitWaypoints(follower), clearedAltitude: null);
        ReadyClosedClimbToComplete(follower);

        TickSeconds(engine, 1);

        Assert.True(HoldsDepartureLeg(follower, hold), "the hand-over is short of the departure end and below TPA - 300");
        HoldOutcome outcome = TickThroughDepartureLegHold(engine, follower, lead, rwy, hold);

        Assert.True(follower.Altitude >= LegalTurnAltitude(hold), "the follower must reach the legal turn altitude");
        Assert.True(
            outcome.HeldPastDepartureEndBelowTurnAltitude,
            "past the departure end and below TPA - 300 the altitude half must still be holding the pursuit"
        );
    }

    /// <summary>
    /// The same through a re-entering go-around, which reaches pattern altitude less 300 ft while still over 28R: only the
    /// departure end still holds it.
    /// </summary>
    [Fact]
    public void PendingPursuit_FromGoAround_HoldsUpwindUntilPastDepartureEnd()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        AircraftState follower = AddGoAroundFollower(engine, 0.5, reenter: true, "VFR");
        AircraftState lead = AddLeadInDownwindBox(engine, follower);
        Assert.True(Send(engine, $"FOLLOW {Leader}").Success);
        HoldGeometry hold = HoldFrom(CircuitWaypoints(follower), clearedAltitude: null);
        ReadyGoAroundToComplete(follower);

        TickSeconds(engine, 1);

        Assert.True(follower.Altitude >= hold.MinTurnAltitude, "the go-around must reach TPA - 300 over the runway");
        Assert.True(HoldsDepartureLeg(follower, hold), "the follower must still be short of the departure end");
        TickThroughDepartureLegHold(engine, follower, lead, rwy, hold);
    }

    /// <summary>
    /// The same hold on the existing upwind path: a runwayless lead followed from the upwind is pursued from the crosswind
    /// point, not from where the FOLLOW was issued.
    /// </summary>
    [Fact]
    public void Follow_RunwaylessLeadFromUpwind_HoldsUpwindUntilCrosswindPoint()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        AircraftState follower = AddOnCircuit(engine, Follower, PatternEntryLeg.Upwind, wp => UpwindShortOfCrosswindTurn(wp, 1.0));
        follower.Approach.HasReportedTrafficInSight = true;
        AircraftState lead = AddLeadInDownwindBox(engine, follower);
        Assert.True(Send(engine, $"FOLLOW {Leader}").Success);
        HoldGeometry hold = HoldFrom(CircuitWaypoints(follower), clearedAltitude: null);

        Assert.True(HoldsDepartureLeg(follower, hold), "the upwind follower is short of the crosswind point");
        TickThroughDepartureLegHold(engine, follower, lead, rwy, hold);
    }

    /// <summary>The departure-leg hold survives a snapshot round trip, and the restored pursuit still flies it.</summary>
    [Fact]
    public void PendingPursuit_GateSurvivesSnapshotRoundTrip()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        AircraftState follower = AddClosedClimbFollower(engine, 1.0);
        AddLeadInDownwindBox(engine, follower);
        Assert.True(Send(engine, $"FOLLOW {Leader}").Success);
        ReadyClosedClimbToComplete(follower);
        TickSeconds(engine, 1);

        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        FollowClimbOutGate gate = pursuit.ClimbOutGate ?? throw new InvalidOperationException("the pursuit must hold the departure leg");

        follower.Phases = PhaseList.FromSnapshot(follower.Phases.ToSnapshot(), groundLayout: null);

        VfrFollowPhase restored = Assert.IsType<VfrFollowPhase>(follower.Phases.CurrentPhase);
        FollowClimbOutGate restoredGate = restored.ClimbOutGate ?? throw new InvalidOperationException("the hold must survive the snapshot");
        Assert.Equal(gate.DepartureEnd.Lat, restoredGate.DepartureEnd.Lat, 6);
        Assert.Equal(gate.DepartureEnd.Lon, restoredGate.DepartureEnd.Lon, 6);
        Assert.Equal(gate.UpwindHeading.Degrees, restoredGate.UpwindHeading.Degrees, 3);
        Assert.Equal(gate.MinTurnAltitude, restoredGate.MinTurnAltitude, 3);

        engine.TickPhysics(0.25);

        Assert.NotNull(restored.ClimbOutGate);
        Assert.True(HeadingOffRunway(follower, rwy) <= HoldingHeadingToleranceDeg, "the restored pursuit must still hold the upwind");
    }

    /// <summary>
    /// The VFR follower re-sequenced in place out of a non-reentering go-around keeps that climb for a lead flying the
    /// follower's own runway: a runway-bearing lead is sequenced in trail, so nothing is left pending for the hand-over.
    /// </summary>
    [Fact]
    public void RuleThreeConversion_ThenRunwaylessLead_ArmsPendingPursuitAndHoldsDepartureLeg()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        AircraftState follower = AddGoAroundFollower(engine, 0.5, reenter: false, "VFR");
        AddCrosswindLead(engine);
        var goAround = (GoAroundPhase)follower.Phases!.CurrentPhase!;

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.Same(goAround, follower.Phases!.CurrentPhase);
        Assert.True(goAround.ReenterPattern, "the conversion re-enters the pattern");
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.False(PendingPursuitOf(follower), "a lead flying the follower's runway is sequenced in trail, not pursued");

        // The converted climb is now the circuit's upwind, so a runwayless lead is the climb case: the follow keeps it and
        // leaves the pursuit pending.
        AircraftState runwayless = AddRunwaylessLeadNamed(engine, follower, OtherLeader);
        CommandResult second = Send(engine, $"FOLLOW {OtherLeader}");

        Assert.True(second.Success, second.Message);
        Assert.Same(goAround, follower.Phases!.CurrentPhase);
        Assert.True(PendingPursuitOf(follower), "the runwayless lead leaves the pursuit pending on the converted climb");
        HoldGeometry hold = HoldFrom(CircuitWaypoints(follower), clearedAltitude: null);

        ReadyGoAroundToComplete(follower);
        TickSeconds(engine, 1);

        Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        TickThroughDepartureLegHold(engine, follower, runwayless, rwy, hold);
    }

    // ─── The altitude and speed the hold leaves to the controller and to the leg ───

    /// <summary>
    /// A CM issued while the pursuit holds the departure leg stands once the gate clears: the hold owns no altitude, so the
    /// controller's clearance is still the target (a per-tick pattern-altitude write would have overwritten it).
    /// </summary>
    [Fact]
    public void PendingPursuit_CmDuringHold_StandsAfterTheGateClears()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        AircraftState follower = AddClosedClimbFollower(engine, 1.0);
        AircraftState lead = AddLeadInDownwindBox(engine, follower);
        Assert.True(Send(engine, $"FOLLOW {Leader}").Success);
        HoldGeometry hold = HoldFrom(CircuitWaypoints(follower), clearedAltitude: null);
        ReadyClosedClimbToComplete(follower);
        TickSeconds(engine, 1);

        Assert.True(Send(engine, "CM 2500").Success);
        Assert.True(HoldsDepartureLeg(follower, hold), "the CM must not end the hold");

        TickThroughDepartureLegHold(engine, follower, lead, rwy, hold);

        Assert.Equal(2500.0, follower.Targets.TargetAltitude);
    }

    /// <summary>
    /// A DM below pattern altitude less 300 ft is the aircraft's clearance, and so the gate's ceiling too: past the departure end
    /// at that altitude the pursuit turns, instead of holding the upwind for an altitude it was told not to climb to.
    /// </summary>
    [Fact]
    public void PendingPursuit_DmBelowTurnAltitude_ClearsTheGateAtTheDmAltitude()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        AircraftState follower = AddClosedClimbFollower(engine, 1.0);
        AircraftState lead = AddLeadInDownwindBox(engine, follower);
        Assert.True(Send(engine, $"FOLLOW {Leader}").Success);
        // 600 ft above the field: below pattern altitude less the 300 ft margin, and above the climb's present altitude.
        double dmAltitude = Oak28R().AirportElevationFt + 600.0;
        HoldGeometry hold = HoldFrom(CircuitWaypoints(follower), clearedAltitude: dmAltitude);
        ReadyClosedClimbToComplete(follower);
        TickSeconds(engine, 1);

        Assert.True(Send(engine, "DM KOAK+006").Success);
        Assert.True(HoldsDepartureLeg(follower, hold), "the DM must not end the hold before the aircraft is there");

        TickThroughDepartureLegHold(engine, follower, lead, rwy, hold);

        Assert.True(follower.Altitude < hold.MinTurnAltitude, $"the pursuit must turn below TPA - 300, was at {follower.Altitude:F0} ft");
        Assert.Equal(dmAltitude, follower.Altitude, 0);
    }

    /// <summary>
    /// While it holds the departure leg the pursuit flies the upwind's speed schedule — the downwind baseline, or the spaced
    /// figure when the lead is close ahead — rather than the climb's.
    /// </summary>
    [Fact]
    public void PendingPursuit_GatedHold_FliesTheUpwindSpeedBaseline()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddClosedClimbFollower(engine, 1.0);
        // A slow climb-out, so the leg's speed schedule has room to climb to and the target is not captured on the first tick.
        follower.IndicatedAirspeed = 60.0;
        AddLeadInDownwindBox(engine, follower);
        Assert.True(Send(engine, $"FOLLOW {Leader}").Success);
        ReadyClosedClimbToComplete(follower);
        TickSeconds(engine, 1);

        AircraftCategory category = AircraftCategorization.Categorize(follower.AircraftType);
        double baseline = AircraftPerformance.DownwindSpeed(follower.AircraftType, category);
        double minSpeed = AircraftPerformance.ApproachSpeed(follower.AircraftType, category);

        double speed = follower.Targets.TargetSpeed ?? double.NaN;
        Assert.False(double.IsNaN(speed), "the gated hold must schedule the upwind's leg speed");
        Assert.InRange(speed, minSpeed, baseline);
        Assert.Equal(baseline, speed, 3.0);
    }

    /// <summary>
    /// An upwind follower told to follow a lead on another runway that it cannot sequence onto (a lead flying no circuit of its
    /// own) installs a free pursuit, and owes the departure leg like any other pursuit started on the upwind.
    /// </summary>
    [Fact]
    public void Follow_LeadOnAnotherRunwayFromUpwind_HoldsDepartureLeg()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak28R();
        AircraftState follower = AddOnCircuit(engine, Follower, PatternEntryLeg.Upwind, wp => UpwindShortOfCrosswindTurn(wp, 1.0));
        follower.Approach.HasReportedTrafficInSight = true;
        AircraftState lead = AddLeadOn28L(engine, follower, 1.5);
        Assert.True(Send(engine, $"FOLLOW {Leader}").Success);
        HoldGeometry hold = HoldFrom(CircuitWaypoints(follower), clearedAltitude: null);

        Assert.True(HoldsDepartureLeg(follower, hold), "the upwind follower is short of the crosswind point");
        TickThroughDepartureLegHold(engine, follower, lead, rwy, hold);
    }

    /// <summary>
    /// A lead assigned another runway of the field (28L) but flying no pattern phase, so FOLLOW installs a free pursuit rather
    /// than sequencing onto its circuit. Placed abeam of the follower's track, which takes a turn well past the upwind heading.
    /// </summary>
    private static AircraftState AddLeadOn28L(SimulationEngine engine, AircraftState follower, double sideNm)
    {
        RunwayInfo rwy28L =
            NavigationDatabase.Instance.GetRunway("KOAK", "28L") ?? throw new InvalidOperationException("KOAK 28L missing from navdata");
        LatLon position = GeoMath.ProjectPoint(follower.Position, follower.TrueHeading + 90.0, sideNm);
        AircraftState lead = MakeAircraft(Leader, position, follower.TrueHeading, 1500, "VFR");
        engine.World.AddAircraft(lead);
        lead.Phases = new PhaseList { AssignedRunway = rwy28L };
        return lead;
    }
}
