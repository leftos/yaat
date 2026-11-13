using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The per-tick cross-runway check on a running follow: a lead reassigned to another runway ends the follow of a follower
/// that has a runway of its own — the follower keeps that runway, its approach or circuit and its landing clearance, and the
/// instructor is told once; an IFR follower is also told its visual separation ended. A runwayless pursuit keeps following onto
/// the lead's new runway, a pursuit's retarget onto such a lead is refused at command time, and a follow the dispatcher accepted
/// across runways survives the next ticks. Real KOAK navdata.
/// </summary>
public class FollowLeadRunwayChangeTests(ITestOutputHelper output)
{
    private const string Leader = "LEAD1";
    private const string SecondLead = "LEAD2";
    private const string Follower = "FOLL1";
    private const string RadarSeparationRequired = $"{Follower} visual separation terminated — radar separation required";
    private const string LeadNewRunway = $"{Follower} follow of {Leader} ended — {Leader} now landing runway 28L";

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("CommandDispatcher", LogLevel.Debug)
            .EnableCategory("AirborneFollowHelper", LogLevel.Debug)
            .EnableCategory("VfrFollow", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
    }

    private static RunwayInfo Runway(string designator) =>
        NavigationDatabase.Instance.GetRunway("KOAK", designator) ?? throw new InvalidOperationException($"KOAK {designator} missing from navdata");

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

    /// <summary>
    /// <paramref name="callsign"/> on <paramref name="rwy"/>'s <paramref name="direction"/> circuit from <paramref name="leg"/>,
    /// started, then placed where <paramref name="place"/> puts it on that circuit's own geometry.
    /// </summary>
    private static AircraftState AddOnCircuit(
        SimulationEngine engine,
        string callsign,
        RunwayInfo rwy,
        PatternDirection direction,
        PatternEntryLeg leg,
        Func<PatternWaypoints, (LatLon Position, TrueHeading Heading)> place
    )
    {
        AircraftState ac = MakeVfr(callsign, OffFinal(rwy, 1.0, 1.0), rwy.TrueHeading, 1000, rwy.AirportId);
        engine.World.AddAircraft(ac);
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            new PatternCircuitRequest
            {
                Runway = rwy,
                Category = AircraftCategorization.Categorize(ac.AircraftType),
                AircraftType = ac.AircraftType,
                WindSpeedKt = 0,
                Direction = direction,
                EntryLeg = leg,
                TouchAndGo = false,
                FinalDistanceNm = null,
                PatternSizeNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = NavigationDatabase.Instance.GetRunways(rwy.AirportId),
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );
        ac.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = direction };
        foreach (Phase phase in circuit)
        {
            ac.Phases.Add(phase);
        }

        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        PatternWaypoints waypoints =
            AirborneFollowHelper.PatternWaypointsOf(ac.Phases.CurrentPhase) ?? throw new InvalidOperationException($"{leg} carries no waypoints");
        (LatLon position, TrueHeading heading) = place(waypoints);
        ac.Position = position;
        ac.TrueHeading = heading;
        ac.TrueTrack = heading;
        return ac;
    }

    /// <summary>On the downwind, <paramref name="shortNm"/> before the base-turn point.</summary>
    private static (LatLon Position, TrueHeading Heading) DownwindShortOfBaseTurn(PatternWaypoints waypoints, double shortNm) =>
        (
            GeoMath.ProjectPoint(new LatLon(waypoints.BaseTurnLat, waypoints.BaseTurnLon), waypoints.DownwindHeading.ToReciprocal(), shortNm),
            waypoints.DownwindHeading
        );

    /// <summary>
    /// <paramref name="callsign"/> flying <paramref name="rwy"/>'s <paramref name="direction"/> downwind, <paramref name="shortNm"/>
    /// before the base turn.
    /// </summary>
    private static AircraftState AddOnDownwind(SimulationEngine engine, string callsign, RunwayInfo rwy, PatternDirection direction, double shortNm)
    {
        AircraftState ac = AddOnCircuit(
            engine,
            callsign,
            rwy,
            direction,
            PatternEntryLeg.Downwind,
            waypoints => DownwindShortOfBaseTurn(waypoints, shortNm)
        );
        Assert.IsType<DownwindPhase>(ac.Phases!.CurrentPhase);
        return ac;
    }

    /// <summary>A runwayless aircraft ahead of <paramref name="follower"/> on its downwind track, <paramref name="aheadNm"/> away.</summary>
    private static AircraftState AddRunwaylessLeadAhead(SimulationEngine engine, AircraftState follower, double aheadNm)
    {
        PatternWaypoints waypoints =
            AirborneFollowHelper.PatternWaypointsOf(follower.Phases!.CurrentPhase)
            ?? throw new InvalidOperationException("the follower has no pattern waypoints");
        AircraftState lead = MakeVfr(
            Leader,
            GeoMath.ProjectPoint(follower.Position, waypoints.DownwindHeading, aheadNm),
            waypoints.DownwindHeading,
            1000,
            follower.FlightPlan.Destination!
        );
        engine.World.AddAircraft(lead);
        Assert.Null(lead.Phases?.AssignedRunway);
        return lead;
    }

    /// <summary>A lead on the 28L final <paramref name="alongNm"/> out, on the course intercept that precedes its final.</summary>
    private static AircraftState AddApproachLead(SimulationEngine engine, RunwayInfo rwy, double alongNm)
    {
        AircraftState lead = MakeVfr(SecondLead, OffFinal(rwy, alongNm, 0.0), rwy.TrueHeading, 1500, rwy.AirportId);
        engine.World.AddAircraft(lead);
        lead.Phases = new PhaseList { AssignedRunway = rwy };
        lead.Phases.Add(
            new InterceptCoursePhase
            {
                FinalApproachCourse = rwy.TrueHeading,
                ThresholdLat = rwy.ThresholdLatitude,
                ThresholdLon = rwy.ThresholdLongitude,
                AssignedInterceptHeading = null,
            }
        );
        lead.Phases.Add(new FinalApproachPhase());
        lead.Phases.Add(new LandingPhase());
        lead.Phases.Start(CommandDispatcher.BuildMinimalContext(lead, ActiveRunways.Empty));
        Assert.IsType<InterceptCoursePhase>(lead.Phases.CurrentPhase);
        return lead;
    }

    /// <summary>A free pursuit of <see cref="Leader"/> by <paramref name="follower"/>, returning to <paramref name="rwy"/>'s circuit.</summary>
    private static VfrFollowPhase InstallPursuit(SimulationEngine engine, AircraftState follower, RunwayInfo rwy, PatternDirection direction)
    {
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        VfrFollowPhase pursuit = CommandDispatcher.InstallVfrFollowPhase(
            follower,
            Leader,
            new FollowPatternReturn(rwy, direction, rwy.ElevationFt + 1000, FromBase: false),
            climbOutGate: null
        );
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        return pursuit;
    }

    /// <summary>The circuit phases a follow's return can rebuild.</summary>
    private static void AssertOnRunwayCircuit(AircraftState follower, RunwayInfo rwy)
    {
        Assert.Equal(rwy.Designator, follower.Phases!.AssignedRunway?.Designator);
        Assert.True(
            follower.Phases.CurrentPhase
                is PatternEntryPhase
                    or MidfieldCrossingPhase
                    or UpwindPhase
                    or CrosswindPhase
                    or DownwindPhase
                    or BasePhase
                    or FinalApproachPhase,
            $"expected a circuit phase, got {follower.Phases.CurrentPhase?.Name}"
        );
    }

    /// <summary>The aircraft that follows <see cref="Leader"/>: cleared to land its own runway, traffic in sight.</summary>
    private static void ClearToLandInSight(AircraftState follower)
    {
        follower.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.ClearedRunwayId = follower.Phases.AssignedRunway!.Designator;
        follower.Approach.HasReportedTrafficInSight = true;
    }

    private CommandResult Follow(SimulationEngine engine, string target = Leader)
    {
        CommandResult result = engine.SendCommand(Follower, $"FOLLOW {target}");
        output.WriteLine($"FOLLOW {target}: success={result.Success} — {result.Message}");
        return result;
    }

    private CommandResult LeadCommand(SimulationEngine engine, string command, string lead = Leader)
    {
        CommandResult result = engine.SendCommand(lead, command);
        output.WriteLine($"{lead} {command}: success={result.Success} — {result.Message}");
        return result;
    }

    private static void TickSeconds(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds * 4; i++)
        {
            engine.TickPhysics(0.25);
        }
    }

    private void LogWarnings(AircraftState follower) => output.WriteLine($"warnings: {string.Join(" | ", follower.PendingWarnings)}");

    // ─── A lead reassigned to another runway ───

    [Fact]
    public void DownwindFollower_LeadReassignedToTheParallel_EndsTheFollowKeepingItsRunway()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Runway("28R");
        AircraftState lead = AddOnDownwind(engine, Leader, rwy, PatternDirection.Right, 0.5);
        AircraftState follower = AddOnDownwind(engine, Follower, rwy, PatternDirection.Right, 1.8);
        ClearToLandInSight(follower);

        CommandResult follow = Follow(engine);
        Assert.True(follow.Success, follow.Message);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        follower.PendingWarnings.Clear();
        follower.PendingPilotTransmissions.Clear();

        CommandResult change = LeadCommand(engine, "EF 28L");
        Assert.True(change.Success, change.Message);
        Assert.Equal("28L", lead.Phases!.AssignedRunway?.Designator);

        TickSeconds(engine, 1);

        LogWarnings(follower);
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Equal("28R", follower.Phases!.AssignedRunway?.Designator);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
        Assert.Equal(LeadNewRunway, Assert.Single(follower.PendingWarnings));
        Assert.Empty(follower.PendingPilotTransmissions);
    }

    [Fact]
    public void IfrKeptApproachFollower_LeadReassigned_AlsoWarnsVisualSeparationTerminated()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 6.0);
        follower.FlightPlan.HasFlightPlan = true;
        follower.FlightPlan.FlightRules = "IFR";
        AircraftState lead = FollowKeepApproachTests.AddFinalLead(engine, Runway("28R"), 2.0);

        CommandResult follow = Follow(engine);
        Assert.True(follow.Success, follow.Message);
        Phase? approach = follower.Phases!.CurrentPhase;
        follower.PendingWarnings.Clear();
        follower.PendingPilotTransmissions.Clear();

        CommandResult change = LeadCommand(engine, "EF 28L");
        Assert.True(change.Success, change.Message);
        Assert.Equal("28L", lead.Phases!.AssignedRunway?.Designator);

        TickSeconds(engine, 1);

        LogWarnings(follower);
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Same(approach, follower.Phases?.CurrentPhase);
        Assert.Equal("28R", follower.Phases!.AssignedRunway?.Designator);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
        Assert.Equal([LeadNewRunway, RadarSeparationRequired], follower.PendingWarnings);
        Assert.Empty(follower.PendingPilotTransmissions);
    }

    [Fact]
    public void RunwaylessPursuit_LeadReassigned_KeepsFollowing()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Runway("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 4.5, 0.5), rwy.TrueHeading, 1500, rwy.AirportId);
        engine.World.AddAircraft(lead);
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 8.0, 0.8), rwy.TrueHeading, 2000, rwy.AirportId);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);

        CommandResult follow = Follow(engine);
        Assert.True(follow.Success, follow.Message);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases?.CurrentPhase);
        Assert.Null(pursuit.PatternReturn);

        CommandResult change = LeadCommand(engine, "EF 28L");
        Assert.True(change.Success, change.Message);
        Assert.Equal("28L", lead.Phases!.AssignedRunway?.Designator);

        TickSeconds(engine, 10);

        LogWarnings(follower);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    /// <summary>A downwind follower's pursuit keeps its circuit: the lead reassigned ends it on the instructor's one line and the
    /// follower re-enters its own 28R circuit with its clearance, saying nothing.</summary>
    [Fact]
    public void PursuitWithCircuit_LeadReassigned_EndsOnOneLineAndReturnsToItsCircuit()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Runway("28R");
        AircraftState follower = AddOnDownwind(engine, Follower, rwy, PatternDirection.Right, 1.8);
        ClearToLandInSight(follower);
        AircraftState lead = AddRunwaylessLeadAhead(engine, follower, 2.0);

        CommandResult follow = Follow(engine);
        Assert.True(follow.Success, follow.Message);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases?.CurrentPhase);
        Assert.Equal("28R", pursuit.PatternReturn?.Runway.Designator);
        follower.PendingWarnings.Clear();
        follower.PendingPilotTransmissions.Clear();

        CommandResult change = LeadCommand(engine, "EF 28L");
        Assert.True(change.Success, change.Message);
        Assert.Equal("28L", lead.Phases!.AssignedRunway?.Designator);

        TickSeconds(engine, 1);

        LogWarnings(follower);
        Assert.Null(follower.Approach.FollowingCallsign);
        AssertOnRunwayCircuit(follower, rwy);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases!.LandingClearance);
        Assert.Equal(LeadNewRunway, Assert.Single(follower.PendingWarnings));
        Assert.Empty(follower.PendingPilotTransmissions);
    }

    /// <summary>A lead reassigned during a turn-out ends the follow with the same instructor line, not silently.</summary>
    [Fact]
    public void TurnOutInProgress_LeadReassigned_WarnsAndReturnsToItsCircuit()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Runway("28R");
        AircraftState lead = FollowKeepApproachTests.AddFinalLead(engine, rwy, 3.0);
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 1.2, 1.2), rwy.TrueHeading, 1000, rwy.AirportId);
        follower.Phases = new PhaseList { LandingClearance = ClearanceType.ClearedToLand, ClearedRunwayId = rwy.Designator };
        VfrFollowPhase pursuit = InstallPursuit(engine, follower, rwy, PatternDirection.Right);
        pursuit.RequestTurnOut();

        TickSeconds(engine, 1);
        Assert.True(pursuit.TurningOut, "the pursuit should have turned out alongside the lead on final");
        follower.PendingWarnings.Clear();
        follower.PendingPilotTransmissions.Clear();

        CommandResult change = LeadCommand(engine, "EF 28L");
        Assert.True(change.Success, change.Message);
        Assert.Equal("28L", lead.Phases!.AssignedRunway?.Designator);

        TickSeconds(engine, 1);

        LogWarnings(follower);
        Assert.Null(follower.Approach.FollowingCallsign);
        AssertOnRunwayCircuit(follower, rwy);
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases!.LandingClearance);
        Assert.Equal(LeadNewRunway, Assert.Single(follower.PendingWarnings));
        Assert.Empty(follower.PendingPilotTransmissions);
    }

    // ─── A pursuit with a circuit cannot be retargeted onto a cross-runway lead ───

    /// <summary>
    /// A pursuit that keeps a circuit refuses a retarget onto a lead landing another runway, by the per-tick check's own rule,
    /// so the follow it already has survives.
    /// </summary>
    [Fact]
    public void PursuitWithCircuit_FollowOfACrossRunwayLead_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Runway("28R");
        AircraftState follower = AddOnDownwind(engine, Follower, rwy, PatternDirection.Right, 1.8);
        follower.Approach.HasReportedTrafficInSight = true;
        AddRunwaylessLeadAhead(engine, follower, 2.0);
        CommandResult follow = Follow(engine);
        Assert.True(follow.Success, follow.Message);
        Assert.Equal("28R", Assert.IsType<VfrFollowPhase>(follower.Phases?.CurrentPhase).PatternReturn?.Runway.Designator);
        AddApproachLead(engine, Runway("28L"), 2.0);

        CommandResult retarget = Follow(engine, SecondLead);

        Assert.False(retarget.Success, retarget.Message);
        Assert.Equal($"Unable, {SecondLead} is landing runway 28L, request vectors", retarget.Message);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        TickSeconds(engine, 1);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        TickSeconds(engine, 10);

        LogWarnings(follower);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    // ─── A follow accepted across runways survives the next ticks ───

    /// <summary>The dispatcher's cross-runway retarget (<c>IsLeadOnDifferentRunway</c> in <c>RouteFollow</c>) falls through to
    /// the pattern install: the follower is re-sequenced onto the lead's runway, so its own runway matches the lead's.</summary>
    [Fact]
    public void CrossRunwayPatternFollow_DownwindFollowerOntoTheParallel_SurvivesTheNextTicks()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddOnDownwind(engine, Follower, Runway("28R"), PatternDirection.Right, 1.8);
        AddOnDownwind(engine, Leader, Runway("28L"), PatternDirection.Left, 1.0);
        follower.Approach.HasReportedTrafficInSight = true;

        CommandResult follow = Follow(engine);
        Assert.True(follow.Success, follow.Message);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Equal("28L", follower.Phases!.AssignedRunway?.Designator);
        output.WriteLine($"follower runway {follower.Phases.AssignedRunway?.Designator}, phase {follower.Phases.CurrentPhase?.Name}");

        TickSeconds(engine, 1);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        TickSeconds(engine, 10);

        LogWarnings(follower);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    /// <summary>A VFR follower on an instrument approach outside the final approach fix is re-sequenced onto the lead's runway
    /// (<c>ClearApproachForFollow</c> drops the clearance): the new follow survives the next ticks.</summary>
    [Fact]
    public void CrossRunwayApproachFollow_OutsideFaf_ResequencedOntoTheParallel_SurvivesTheNextTicks()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 8.0);
        AddOnDownwind(engine, Leader, Runway("28L"), PatternDirection.Left, 1.0);

        CommandResult follow = Follow(engine);
        Assert.True(follow.Success, follow.Message);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        Assert.Equal("28L", follower.Phases!.AssignedRunway?.Designator);
        output.WriteLine($"follower runway {follower.Phases.AssignedRunway?.Designator}, phase {follower.Phases.CurrentPhase?.Name}");

        TickSeconds(engine, 1);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
        TickSeconds(engine, 10);

        LogWarnings(follower);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }
}
