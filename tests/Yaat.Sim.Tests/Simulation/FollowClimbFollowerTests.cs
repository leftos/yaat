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
        AddRunwaylessLead(engine, OffFinal(rwy, -3.0, 0.3), rwy.TrueHeading);
        Phase goAround = follower.Phases!.CurrentPhase!;

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.NotSame(goAround, follower.Phases?.CurrentPhase);
        Assert.IsType<VfrFollowPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Contains(follower.PendingWarnings, w => w.Contains("cancelled by FOLLOW", StringComparison.Ordinal));
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
}
