using Microsoft.Extensions.Logging;
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
/// FOLLOW refused at command time when the lead is not ahead in the landing sequence: a lead landing the follower's runway
/// that the running follow counts as behind (<see cref="AirborneFollowHelper.IsLeadPatternFlowBehind"/>), or with no leg in
/// the sequence. From upwind, crosswind or downwind the order is by leg (a lead on a later leg is ahead however far it is
/// extended) and by position on a shared leg, and a lead on a pattern entry is accepted; from base, final or an instrument
/// approach it is by remaining path to the threshold, and a lead on a pattern entry is refused from base or final and measured by
/// its path through the entry from an instrument approach. A departing lead is refused
/// from any follower, ahead of every other check. A refusal leaves the phase, the clearance and the follow untouched. Real
/// KOAK 28R right traffic.
/// </summary>
public class FollowSequenceRefusalTests(ITestOutputHelper output)
{
    private const string Leader = "LEAD1";
    private const string Follower = "FOLL1";

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    private static RunwayInfo Oak(string designator) =>
        NavigationDatabase.Instance.GetRunway("KOAK", designator) ?? throw new InvalidOperationException($"KOAK {designator} missing from navdata");

    /// <summary>Point <paramref name="alongNm"/> out the final and <paramref name="crossNm"/> to the right of the landing direction.</summary>
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

    private static AircraftState MakeVfr(string callsign, LatLon position, TrueHeading heading, double altitude) =>
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
                Departure = "KOAK",
                Destination = "KOAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(2000),
                CruiseSpeed = 110,
            },
        };

    /// <summary>Put <paramref name="ac"/> on the right-traffic circuit for <paramref name="rwy"/> starting at <paramref name="leg"/>.</summary>
    private static void PutOnCircuit(AircraftState ac, RunwayInfo rwy, PatternEntryLeg leg)
    {
        List<Phase> circuit = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategorization.Categorize(ac.AircraftType),
            ac.AircraftType,
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
        ac.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Right };
        foreach (Phase phase in circuit)
        {
            ac.Phases.Add(phase);
        }

        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
    }

    /// <summary>
    /// An aircraft on the 28R right circuit from <paramref name="leg"/>, then placed where <paramref name="place"/> puts it on
    /// that circuit's own geometry (the dispatcher reads position only, so no tick runs between).
    /// </summary>
    private static AircraftState AddOnCircuit(
        SimulationEngine engine,
        string callsign,
        PatternEntryLeg leg,
        Func<PatternWaypoints, (LatLon, TrueHeading)> place
    )
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState ac = MakeVfr(callsign, OffFinal(rwy, 1.0, 1.0), rwy.TrueHeading, 1000);
        engine.World.AddAircraft(ac);
        PutOnCircuit(ac, rwy, leg);
        PatternWaypoints wp =
            AirborneFollowHelper.PatternWaypointsOf(ac.Phases!.CurrentPhase) ?? throw new InvalidOperationException($"{leg} carries no waypoints");
        (LatLon position, TrueHeading heading) = place(wp);
        ac.Position = position;
        ac.TrueHeading = heading;
        ac.TrueTrack = heading;
        return ac;
    }

    private static LatLon BaseTurn(PatternWaypoints wp) => new(wp.BaseTurnLat, wp.BaseTurnLon);

    /// <summary>On the downwind, <paramref name="shortNm"/> before the base-turn point (past it when negative).</summary>
    private static (LatLon, TrueHeading) DownwindShortOfBaseTurn(PatternWaypoints wp, double shortNm) =>
        (GeoMath.ProjectPoint(BaseTurn(wp), wp.DownwindHeading.ToReciprocal(), shortNm), wp.DownwindHeading);

    /// <summary>On the base, <paramref name="intoNm"/> past the base-turn point.</summary>
    private static (LatLon, TrueHeading) IntoBase(PatternWaypoints wp, double intoNm) =>
        (GeoMath.ProjectPoint(BaseTurn(wp), wp.CrosswindHeading.ToReciprocal(), intoNm), wp.CrosswindHeading.ToReciprocal());

    /// <summary>On the upwind, <paramref name="shortNm"/> before the crosswind-turn point.</summary>
    private static (LatLon, TrueHeading) UpwindShortOfCrosswindTurn(PatternWaypoints wp, double shortNm) =>
        (GeoMath.ProjectPoint(new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon), wp.UpwindHeading.ToReciprocal(), shortNm), wp.UpwindHeading);

    /// <summary>On the crosswind, <paramref name="outNm"/> out from the crosswind-turn point.</summary>
    private static (LatLon, TrueHeading) OutOnCrosswind(PatternWaypoints wp, double outNm) =>
        (GeoMath.ProjectPoint(new LatLon(wp.CrosswindTurnLat, wp.CrosswindTurnLon), wp.CrosswindHeading, outNm), wp.CrosswindHeading);

    private static AircraftState AddFollower(SimulationEngine engine, PatternEntryLeg leg, Func<PatternWaypoints, (LatLon, TrueHeading)> place)
    {
        AircraftState follower = AddOnCircuit(engine, Follower, leg, place);
        follower.Approach.HasReportedTrafficInSight = true;
        follower.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.ClearedRunwayId = "28R";
        return follower;
    }

    /// <summary>The follower's state before FOLLOW, to prove a refusal leaves it untouched.</summary>
    private sealed record FollowerState(PhaseList? Phases, Phase? Current, int PhaseCount, ClearanceType? Clearance, string? ClearedRunwayId);

    private static FollowerState Capture(AircraftState ac) =>
        new(ac.Phases, ac.Phases?.CurrentPhase, ac.Phases?.Phases.Count ?? 0, ac.Phases?.LandingClearance, ac.Phases?.ClearedRunwayId);

    private static void TickSeconds(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds * 4; i++)
        {
            engine.TickPhysics(0.25);
        }
    }

    private CommandResult Send(SimulationEngine engine, string command)
    {
        CommandResult result = engine.SendCommand(Follower, command);
        output.WriteLine($"{command}: success={result.Success} — {result.Message}");
        return result;
    }

    private void LogPaths(AircraftState follower, AircraftState lead, PatternWaypoints wp) =>
        output.WriteLine(
            $"remaining path: follower {AirborneFollowHelper.RemainingPatternPathNm(follower, wp):F2} nm, "
                + $"lead {AirborneFollowHelper.RemainingPatternPathNm(lead, wp):F2} nm"
        );

    private static void AssertRefusedUnchanged(AircraftState follower, FollowerState before, CommandResult result, string expectedMessage)
    {
        Assert.False(result.Success, result.Message);
        Assert.Equal(expectedMessage, result.Message);
        Assert.Equal(before, Capture(follower));
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    private static string NotAhead(string position) => $"Unable, on {position} for runway 28R, {Leader} is not ahead of us, request vectors";

    private const string DepartingText = $"Unable, {Leader} is departing, request vectors";

    /// <summary>Follower on the 28R final <paramref name="alongNm"/> out on the centerline, cleared to land.</summary>
    private static AircraftState AddFinalFollower(SimulationEngine engine, double alongNm, double altitude)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, alongNm, 0), rwy.TrueHeading, altitude);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        PutOnCircuit(follower, rwy, PatternEntryLeg.Final);
        Assert.IsType<FinalApproachPhase>(follower.Phases!.CurrentPhase);
        follower.Phases.LandingClearance = ClearanceType.ClearedToLand;
        follower.Phases.ClearedRunwayId = "28R";
        return follower;
    }

    /// <summary>Lead on the 28R final <paramref name="alongNm"/> out on the centerline.</summary>
    private static AircraftState AddFinalLead(SimulationEngine engine, double alongNm, double altitude)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, alongNm, 0), rwy.TrueHeading, altitude);
        engine.World.AddAircraft(lead);
        PutOnCircuit(lead, rwy, PatternEntryLeg.Final);
        Assert.IsType<FinalApproachPhase>(lead.Phases!.CurrentPhase);
        return lead;
    }

    /// <summary>Lead north of the field entering the 28R right downwind (ERD 28R), still on its pattern entry.</summary>
    private static AircraftState AddEntryLead(SimulationEngine engine)
    {
        AircraftState lead = MakeVfr(Leader, OffFinal(Oak("28R"), -2.0, 4.0), new TrueHeading(180), 1500);
        engine.World.AddAircraft(lead);
        CommandResult setup = engine.SendCommand(Leader, "ERD 28R");
        Assert.True(setup.Success, setup.Message);
        Assert.IsType<PatternEntryPhase>(lead.Phases?.CurrentPhase);
        return lead;
    }

    /// <summary>Lead airborne 1.5 nm past the departure end of KOAK <paramref name="designator"/>, in its initial climb.</summary>
    private static AircraftState AddDepartingLead(SimulationEngine engine, string designator)
    {
        RunwayInfo rwy = Oak(designator);
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, -1.5, 0), rwy.TrueHeading, 600);
        lead.Phases = new PhaseList { AssignedRunway = rwy };
        lead.Phases.Add(new InitialClimbPhase()); // CurrentIndex defaults to 0, so CurrentPhase is the climb without Start()
        engine.World.AddAircraft(lead);
        return lead;
    }

    /// <summary>Lead flying a missed approach off 28R (no pattern re-entry): no leg in the landing sequence.</summary>
    private static AircraftState AddMissedApproachLead(SimulationEngine engine)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, -0.5, 0), rwy.TrueHeading, 600);
        lead.Phases = new PhaseList { AssignedRunway = rwy };
        lead.Phases.Add(new GoAroundPhase { ReenterPattern = false }); // CurrentIndex defaults to 0, so no Start() needed
        engine.World.AddAircraft(lead);
        return lead;
    }

    private static void AssertRetargetedInPlace(AircraftState follower, FollowerState before, CommandResult result)
    {
        Assert.True(result.Success, result.Message);
        Assert.Equal(before, Capture(follower));
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    private static PatternWaypoints WaypointsOf(AircraftState ac) =>
        AirborneFollowHelper.PatternWaypointsOf(ac.Phases!.CurrentPhase) ?? throw new InvalidOperationException("no waypoints");

    // ─── Pattern-leg followers ───

    [Fact]
    public void FollowFromBase_SameRunwayLeadOnDownwind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Base, wp => IntoBase(wp, 0.3));
        AircraftState lead = AddOnCircuit(engine, Leader, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 1.5));
        LogPaths(follower, lead, WaypointsOf(follower));
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, $"Unable, on base for runway 28R, {Leader} is not ahead of us, request vectors");
    }

    /// <summary>A follower 0.5 nm out on final has less path to the threshold than a lead on base: the lead is behind.</summary>
    [Fact]
    public void FollowFromFinal_SameRunwayLeadOnBase_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFinalFollower(engine, 0.5, 300);
        AddOnCircuit(engine, Leader, PatternEntryLeg.Base, wp => IntoBase(wp, 0.3));
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, NotAhead("final"));
    }

    /// <summary>
    /// A 6 nm straight-in behind a Cessna on a close base: the lead's path through its base and final is far shorter, so it
    /// is ahead by path and FOLLOW is accepted, though base is an earlier leg than final.
    /// </summary>
    [Fact]
    public void FollowFromFinal_SixMileStraightIn_LeadOnCloseBase_IsAccepted()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFinalFollower(engine, 6.0, 1900);
        AddOnCircuit(engine, Leader, PatternEntryLeg.Base, wp => IntoBase(wp, 0.3));
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRetargetedInPlace(follower, before, result);
    }

    /// <summary>A lead 10 nm out on final has more path to the threshold than a follower on a close base: behind, refused.</summary>
    [Fact]
    public void FollowFromBase_LeadOnTenMileFinal_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Base, wp => IntoBase(wp, 0.3));
        AddFinalLead(engine, 10.0, 3000);
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, NotAhead("base"));
    }

    /// <summary>From base, getting behind a lead still on its pattern entry would take a 360: refused.</summary>
    [Fact]
    public void FollowFromBase_LeadOnPatternEntry_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Base, wp => IntoBase(wp, 0.3));
        AddEntryLead(engine);
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, NotAhead("base"));
    }

    /// <summary>From final, getting behind a lead still on its pattern entry would take a 360: refused.</summary>
    [Fact]
    public void FollowFromFinal_LeadOnPatternEntry_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFinalFollower(engine, 2.0, 700);
        AddEntryLead(engine);
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, NotAhead("final"));
    }

    [Fact]
    public void FollowFromDownwind_SameRunwayLeadOnUpwind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 1.0));
        AddOnCircuit(engine, Leader, PatternEntryLeg.Upwind, wp => UpwindShortOfCrosswindTurn(wp, 0.3));
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, $"Unable, on downwind for runway 28R, {Leader} is not ahead of us, request vectors");
    }

    [Fact]
    public void FollowFromDownwind_LeadOnBase_IsAccepted()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 1.0));
        AddOnCircuit(engine, Leader, PatternEntryLeg.Base, wp => IntoBase(wp, 0.3));
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRetargetedInPlace(follower, before, result);
    }

    /// <summary>A lead still on its pattern entry is accepted from a downwind: falling in behind it is the follow's job.</summary>
    [Fact]
    public void FollowFromDownwind_LeadOnPatternEntry_IsAccepted()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 1.0));
        AddEntryLead(engine);
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRetargetedInPlace(follower, before, result);
    }

    /// <summary>A same-runway lead flying a missed approach is leaving the landing sequence: refused as going around.</summary>
    [Fact]
    public void FollowFromDownwind_SameRunwayLeadOnMissedApproach_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 1.0));
        AddMissedApproachLead(engine);
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, $"Unable, {Leader} is going around, request vectors");
    }

    /// <summary>On a shared downwind, a lead 1 nm farther from its base-turn point than the follower is behind it.</summary>
    [Fact]
    public void FollowFromDownwind_LeadBehindOnSameDownwind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 1.0));
        AddOnCircuit(engine, Leader, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 2.0));
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, NotAhead("downwind"));
    }

    /// <summary>On a shared crosswind, a lead nearer the runway than the follower is behind it.</summary>
    [Fact]
    public void FollowFromCrosswind_LeadBehindOnSameCrosswind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Crosswind, wp => OutOnCrosswind(wp, 0.4));
        AddOnCircuit(engine, Leader, PatternEntryLeg.Crosswind, wp => OutOnCrosswind(wp, 0.1));
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, NotAhead("crosswind"));
    }

    [Fact]
    public void FollowFromCrosswind_LeadOnUpwind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Crosswind, wp => OutOnCrosswind(wp, 0.2));
        AddOnCircuit(engine, Leader, PatternEntryLeg.Upwind, wp => UpwindShortOfCrosswindTurn(wp, 0.3));
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, NotAhead("crosswind"));
    }

    /// <summary>A refused FOLLOW from a follower already following someone else keeps that follow and its extended downwind.</summary>
    [Fact]
    public void FollowFromDownwind_AlreadyFollowing_RefusalKeepsTheFollowAndTheExtension()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 1.0));
        follower.Approach.FollowingCallsign = "OTHER1";
        DownwindPhase downwind = Assert.IsType<DownwindPhase>(follower.Phases!.CurrentPhase);
        downwind.IsExtended = true;
        AddOnCircuit(engine, Leader, PatternEntryLeg.Upwind, wp => UpwindShortOfCrosswindTurn(wp, 0.3));
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.False(result.Success, result.Message);
        Assert.Equal(NotAhead("downwind"), result.Message);
        Assert.Equal(before, Capture(follower));
        Assert.Equal("OTHER1", follower.Approach.FollowingCallsign);
        Assert.True(downwind.IsExtended);
    }

    /// <summary>
    /// A lead held 3.5 nm past its base-turn point on the downwind has more path left to the threshold than a follower on the
    /// crosswind, but it is on a later leg: ahead in sequence, so FOLLOW is accepted and the follower sequences behind it.
    /// </summary>
    [Fact]
    public void FollowFromCrosswind_LeadExtendedOnDownwind_IsAccepted()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Crosswind, wp => OutOnCrosswind(wp, 0.2));
        AircraftState lead = AddOnCircuit(engine, Leader, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, -3.5));
        LogPaths(follower, lead, WaypointsOf(follower));
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRetargetedInPlace(follower, before, result);
    }

    // ─── Approach follower ───

    /// <summary>Follower intercepting the 28R final <paramref name="alongNm"/> out and 0.8 nm right of it, cleared to land.</summary>
    internal static AircraftState AddApproachFollower(SimulationEngine engine, double alongNm)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, alongNm, 0.8), rwy.TrueHeading, 900);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        follower.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            LandingClearance = ClearanceType.ClearedToLand,
            ClearedRunwayId = "28R",
        };
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
        Assert.IsType<InterceptCoursePhase>(follower.Phases.CurrentPhase);
        return follower;
    }

    /// <summary>
    /// Follower 2.5 nm out on the intercept, lead extended 3.5 nm past its base-turn point on the downwind: the lead's circuit
    /// to the threshold is far longer than the follower's distance, so it is behind and FOLLOW is refused, keeping the approach.
    /// </summary>
    [Fact]
    public void FollowFromApproach_SameRunwayLeadBehind_IsRefusedAndKeepsTheApproach()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddApproachFollower(engine, 2.5);
        AircraftState lead = AddOnCircuit(engine, Leader, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, -3.5));
        output.WriteLine($"lead remaining path {AirborneFollowHelper.RemainingPatternPathNm(lead, WaypointsOf(lead)):F2} nm");
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, $"Unable, on approach for runway 28R, {Leader} is not ahead of us, request vectors");
        Assert.DoesNotContain(follower.PendingWarnings, w => w.Contains("cancelled by FOLLOW", StringComparison.Ordinal));
    }

    /// <summary>
    /// Follower 6 nm out on the intercept, lead on its base: the lead's path to the threshold is shorter, so it is ahead by
    /// distance and FOLLOW is accepted even though an approach follower stands for the final, a later leg than base.
    /// </summary>
    [Fact]
    public void FollowFromApproach_PatternLeadCloserToTheField_IsAccepted()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddApproachFollower(engine, 6.0);
        AddOnCircuit(engine, Leader, PatternEntryLeg.Base, wp => IntoBase(wp, 0.3));
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRetargetedInPlace(follower, before, result);
        Assert.IsType<InterceptCoursePhase>(follower.Phases!.CurrentPhase);
    }

    /// <summary>
    /// Follower on an instrument approach's fix sequence 2.5 nm out and 0.8 nm right of the 28R final, its one fix on the
    /// centerline 1.5 nm out, cleared to land.
    /// </summary>
    internal static AircraftState AddApproachNavigationFollower(SimulationEngine engine)
    {
        RunwayInfo rwy = Oak("28R");
        LatLon fix = OffFinal(rwy, 1.5, 0);
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 2.5, 0.8), new TrueHeading(GeoMath.BearingTo(OffFinal(rwy, 2.5, 0.8), fix)), 900);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        follower.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            LandingClearance = ClearanceType.ClearedToLand,
            ClearedRunwayId = "28R",
        };
        follower.Phases.Add(new ApproachNavigationPhase { Fixes = [new ApproachFix("FIX", fix.Lat, fix.Lon)] });
        follower.Phases.Add(new FinalApproachPhase());
        follower.Phases.Add(new LandingPhase()); // CurrentIndex defaults to 0, so CurrentPhase is the fix sequence without Start()
        Assert.IsType<ApproachNavigationPhase>(follower.Phases.CurrentPhase);
        return follower;
    }

    /// <summary>
    /// A follower on an approach's fixes is measured by the path it still has to fly (about 2.5 nm): a lead extended 3.5 nm
    /// on the downwind is behind, refused, and the approach is kept.
    /// </summary>
    [Fact]
    public void FollowFromApproachNavigation_SameRunwayLeadBehind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddApproachNavigationFollower(engine);
        AddOnCircuit(engine, Leader, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, -3.5));
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, NotAhead("approach"));
    }

    [Fact]
    public void FollowFromApproach_LeadOnMissedApproach_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddApproachFollower(engine, 2.5);
        AddMissedApproachLead(engine);
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, $"Unable, {Leader} is going around, request vectors");
    }

    /// <summary>
    /// Follower 2.5 nm out on the intercept, lead on its entry to the right downwind from north of the field: its path through
    /// the entry and the circuit is far longer than the follower's, so it is behind and FOLLOW is refused.
    /// </summary>
    [Fact]
    public void FollowFromApproach_LeadOnPatternEntryWithLongerPath_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddApproachFollower(engine, 2.5);
        AddEntryLead(engine);
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, NotAhead("approach"));
    }

    // ─── Departing leads ───

    [Fact]
    public void Follow_DepartingLead_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 1.0));
        AddDepartingLead(engine, "28R");
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, DepartingText);
    }

    /// <summary>A follower on an instrument approach gets the departing refusal too, and keeps its approach.</summary>
    [Fact]
    public void FollowFromApproach_DepartingLead_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddApproachFollower(engine, 2.5);
        AddDepartingLead(engine, "28R");
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, DepartingText);
    }

    /// <summary>
    /// A lead departing another KOAK runway, followed from base: the departing refusal comes first, ahead of the cross-runway
    /// refusal from base.
    /// </summary>
    [Fact]
    public void FollowFromBase_LeadDepartingAnotherRunway_IsRefusedAsDeparting()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Base, wp => IntoBase(wp, 0.3));
        AddDepartingLead(engine, "28L");
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, DepartingText);
    }

    /// <summary>
    /// A closed-traffic takeoff climb is the circuit's upwind, not a departure: 0.3 nm past the crosswind-turn point it is
    /// ahead of an upwind follower 0.5 nm short of it, so FOLLOW retargets in place.
    /// </summary>
    [Fact]
    public void Follow_ClosedTrafficClimbLead_IsNotRefusedAsDeparting()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Upwind, wp => UpwindShortOfCrosswindTurn(wp, 0.5));
        PatternWaypoints wp = WaypointsOf(follower);

        var takeoff = new TakeoffPhase();
        takeoff.SetAssignedDeparture(new ClosedTrafficDeparture(PatternDirection.Right, null, null));
        (LatLon leadPosition, TrueHeading leadHeading) = UpwindShortOfCrosswindTurn(wp, -0.3);
        AircraftState lead = MakeVfr(Leader, leadPosition, leadHeading, 700);
        lead.Phases = new PhaseList { AssignedRunway = rwy };
        lead.Phases.Add(takeoff); // CurrentIndex defaults to 0, so CurrentPhase is the takeoff without Start()
        engine.World.AddAircraft(lead);
        Assert.True(AirborneFollowHelper.IsClosedTrafficClimb(lead));
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRetargetedInPlace(follower, before, result);
    }

    // ─── FOLLOWF goes through the same gate ───

    [Fact]
    public void Followf_SameRunwayLeadBehind_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Base, wp => IntoBase(wp, 0.3));
        follower.Approach.HasReportedTrafficInSight = false;
        AddOnCircuit(engine, Leader, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 1.5));
        FollowerState before = Capture(follower);

        CommandResult result = Send(engine, $"FOLLOWF {Leader}");

        AssertRefusedUnchanged(follower, before, result, $"Unable, on base for runway 28R, {Leader} is not ahead of us, request vectors");
    }

    /// <summary>
    /// A refused FOLLOWF must not leave the traffic marked in sight: the traffic-in-sight report is what an accepted
    /// FOLLOWF folds in, and the refusal is the pilot declining the follow entirely. Issued as a condition-led block,
    /// which is queued and applied to the follower once its trigger is met: only an unconditional compound's first
    /// block is dry-run on a clone, so this is the path on which a refusal reaches the follower.
    /// </summary>
    [Fact]
    public void FollowForce_Refused_LeavesTrafficNotInSight()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddFollower(engine, PatternEntryLeg.Base, wp => IntoBase(wp, 0.3));
        follower.Altitude = 2000;
        follower.Approach.HasReportedTrafficInSight = false;
        AddOnCircuit(engine, Leader, PatternEntryLeg.Downwind, wp => DownwindShortOfBaseTurn(wp, 1.5));

        CommandResult issued = engine.SendCommand(Follower, $"AT 2000 FOLLOWF {Leader}");
        output.WriteLine($"AT 2000 FOLLOWF: success={issued.Success} — {issued.Message}");
        Assert.True(issued.Success, issued.Message);

        TickSeconds(engine, 2);
        output.WriteLine($"warnings: {string.Join(" | ", follower.PendingWarnings)}");

        // The refusal reaching the follower proves the triggered FOLLOWF fired and was refused, not that it never ran.
        Assert.Contains(follower.PendingWarnings, w => w.Contains(NotAhead("base"), StringComparison.Ordinal));
        Assert.False(follower.Approach.HasReportedTrafficInSight, "a refused FOLLOWF must leave the traffic-in-sight state as it was");
        Assert.Null(follower.Approach.FollowingCallsign);
    }
}
