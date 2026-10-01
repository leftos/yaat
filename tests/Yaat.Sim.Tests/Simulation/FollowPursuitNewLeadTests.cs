using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// FOLLOW of a new lead during a pursuit (<see cref="VfrFollowPhase"/>). A pursuit that left its circuit from base
/// (<see cref="FollowPatternReturn.FromBase"/>), or one turning out, compares the new lead on its circuit: the lead is ahead
/// when its remaining path (<see cref="AirborneFollowHelper.SequenceRemainingPathNm(AircraftState, RunwayInfo)"/>) is no longer
/// than the follower's shortest path to the threshold, and is then followed in place (pattern return kept, no downwind entry,
/// a turn-out ended). A lead behind or on a pattern entry is refused "Unable, {T} is not ahead of us, request vectors", a lead
/// landing another runway "Unable, {T} is landing runway {rwy2}, request vectors", and a runwayless lead takes the ±60° cone.
/// A pursuit that did not leave from base keeps the downwind entry behind a runway-bearing lead. Real KOAK navdata.
/// </summary>
public class FollowPursuitNewLeadTests(ITestOutputHelper output)
{
    private const string Leader = "LEAD1";
    private const string Other = "OTHER1";
    private const string Follower = "FOLL1";

    private const string LeaderNotAheadText = $"Unable, {Leader} is not ahead of us, request vectors";
    private const string OtherNotAheadText = $"Unable, {Other} is not ahead of us, request vectors";

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    private static RunwayInfo Oak(string designator) =>
        NavigationDatabase.Instance.GetRunway("KOAK", designator) ?? throw new InvalidOperationException($"KOAK {designator} missing from navdata");

    private static LatLon Threshold(RunwayInfo rwy) => new(rwy.ThresholdLatitude, rwy.ThresholdLongitude);

    private static double PatternAltitudeFt(RunwayInfo rwy) =>
        rwy.AirportElevationFt + CategoryPerformance.PatternAltitudeAgl(AircraftCategory.Piston);

    /// <summary>Point <paramref name="alongNm"/> out the final and <paramref name="crossNm"/> to the right of the landing direction.</summary>
    private static LatLon OffFinal(RunwayInfo rwy, double alongNm, double crossNm)
    {
        LatLon onCenterline = GeoMath.ProjectPoint(Threshold(rwy), rwy.TrueHeading.ToReciprocal(), alongNm);
        if (Math.Abs(crossNm) < 1e-9)
        {
            return onCenterline;
        }

        TrueHeading perpendicular = crossNm > 0 ? rwy.TrueHeading + 90.0 : rwy.TrueHeading - 90.0;
        return GeoMath.ProjectPoint(onCenterline, perpendicular, Math.Abs(crossNm));
    }

    private static AircraftState MakeVfr(string callsign, LatLon position, TrueHeading heading, double altitudeFt) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitudeFt,
            IndicatedAirspeed = AircraftPerformance.ApproachSpeed("C172", AircraftCategory.Piston),
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

    /// <summary>
    /// A follower pursuing <paramref name="target"/> on the 28R right base, heading for the final, 2.5 nm out and 1 nm right
    /// of the centerline; its pursuit returns to the 28R right circuit, from base when <paramref name="fromBase"/>.
    /// </summary>
    private static AircraftState AddBasePursuitFollower(SimulationEngine engine, string target, bool fromBase)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 2.5, 1.0), rwy.TrueHeading - 90.0, 1000);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        CommandDispatcher.InstallVfrFollowPhase(
            follower,
            target,
            new FollowPatternReturn(rwy, PatternDirection.Right, PatternAltitudeFt(rwy), fromBase)
        );
        Assert.Equal(target, follower.Approach.FollowingCallsign);
        return follower;
    }

    /// <summary>A follower pursuing <paramref name="target"/> just off the 28R final, as the turn-out tests start it.</summary>
    private static AircraftState AddTurnOutPursuitFollower(SimulationEngine engine, string target)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 1.2, 1.2), rwy.TrueHeading, 1000);
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        CommandDispatcher.InstallVfrFollowPhase(
            follower,
            target,
            new FollowPatternReturn(rwy, PatternDirection.Right, PatternAltitudeFt(rwy), false)
        );
        return follower;
    }

    /// <summary>
    /// <paramref name="callsign"/> on the final of KOAK <paramref name="designator"/>, <paramref name="alongNm"/> out, on its glidepath.
    /// </summary>
    private static AircraftState AddFinalLead(SimulationEngine engine, string callsign, string designator, double alongNm)
    {
        RunwayInfo rwy = Oak(designator);
        AircraftState lead = MakeVfr(callsign, OffFinal(rwy, alongNm, 0.0), rwy.TrueHeading, rwy.ElevationFt + (318.0 * alongNm));
        engine.World.AddAircraft(lead);
        PutOnCircuit(lead, rwy, PatternEntryLeg.Final);
        Assert.IsType<FinalApproachPhase>(lead.Phases!.CurrentPhase);
        return lead;
    }

    /// <summary>The lead on the 28R right downwind, 1 nm before its base-turn point.</summary>
    private static AircraftState AddDownwindLead(SimulationEngine engine)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 1.0, 1.0), rwy.TrueHeading, 1000);
        engine.World.AddAircraft(lead);
        PutOnCircuit(lead, rwy, PatternEntryLeg.Downwind);
        PatternWaypoints wp =
            AirborneFollowHelper.PatternWaypointsOf(lead.Phases!.CurrentPhase)
            ?? throw new InvalidOperationException("downwind carries no waypoints");
        lead.Position = GeoMath.ProjectPoint(new LatLon(wp.BaseTurnLat, wp.BaseTurnLon), wp.DownwindHeading.ToReciprocal(), 1.0);
        lead.TrueHeading = wp.DownwindHeading;
        lead.TrueTrack = wp.DownwindHeading;
        return lead;
    }

    /// <summary>The lead on the 28R right base, 1.5 nm out and 0.5 nm right of the centerline, heading for the final.</summary>
    private static AircraftState AddBaseLead(SimulationEngine engine)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 1.5, 0.5), rwy.TrueHeading - 90.0, 800);
        engine.World.AddAircraft(lead);
        PutOnCircuit(lead, rwy, PatternEntryLeg.Base);
        Assert.IsType<BasePhase>(lead.Phases!.CurrentPhase);
        return lead;
    }

    /// <summary>
    /// The lead (<see cref="FollowEntryLeadPathTests.StraightInEntrant"/>) on a straight-in entry joining the 28R final
    /// <paramref name="joinNm"/> out, from <paramref name="alongNm"/> out and <paramref name="crossNm"/> right of the final.
    /// </summary>
    private static AircraftState AddStraightInLead(SimulationEngine engine, double joinNm, double alongNm, double crossNm)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = FollowEntryLeadPathTests.StraightInEntrant(rwy, "C172", joinNm, OffFinal(rwy, alongNm, crossNm));
        Assert.Equal(Leader, lead.Callsign);
        engine.World.AddAircraft(lead);
        Assert.IsType<PatternEntryPhase>(lead.Phases?.CurrentPhase);
        Assert.True(AirborneFollowHelper.IsStraightInEntry(lead), "the lead must be on a straight-in entry");
        return lead;
    }

    /// <summary>
    /// The lead on a base entry to the 28R right circuit (<see cref="PatternEntryKind.Base"/>), joining the base 0.3 nm past
    /// the base-turn point from 0.7 nm outside it on the base line, heading for the join.
    /// </summary>
    private static AircraftState AddBaseEntryLead(SimulationEngine engine)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, Threshold(rwy), rwy.TrueHeading, 1000);
        PutOnCircuit(lead, rwy, PatternEntryLeg.Base, []);
        PatternWaypoints wp =
            AirborneFollowHelper.PatternWaypointsOf(lead.Phases!.CurrentPhase) ?? throw new InvalidOperationException("base carries no waypoints");
        TrueHeading baseHeading = wp.CrosswindHeading.ToReciprocal();
        LatLon join = GeoMath.ProjectPoint(new LatLon(wp.BaseTurnLat, wp.BaseTurnLon), baseHeading, 0.3);
        lead.Position = GeoMath.ProjectPoint(join, wp.CrosswindHeading, 1.0);
        lead.TrueHeading = baseHeading;
        lead.TrueTrack = baseHeading;
        var entry = new PatternEntryPhase
        {
            EntryLat = join.Lat,
            EntryLon = join.Lon,
            PatternAltitude = 1000,
            Kind = PatternEntryKind.Base,
        };
        PutOnCircuit(lead, rwy, PatternEntryLeg.Base, [entry]);
        engine.World.AddAircraft(lead);
        Assert.IsType<PatternEntryPhase>(lead.Phases?.CurrentPhase);
        Assert.False(AirborneFollowHelper.IsStraightInEntry(lead), "the lead must be on an entry that does not join the final");
        return lead;
    }

    private static void PutOnCircuit(AircraftState ac, RunwayInfo rwy, PatternEntryLeg leg) => PutOnCircuit(ac, rwy, leg, []);

    /// <summary>Gives <paramref name="ac"/> the phases <paramref name="first"/>, then the 28R right circuit from <paramref name="leg"/>, started.</summary>
    private static void PutOnCircuit(AircraftState ac, RunwayInfo rwy, PatternEntryLeg leg, Phase[] first)
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
        foreach (Phase phase in first.Concat(circuit))
        {
            ac.Phases.Add(phase);
        }

        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
    }

    private static void TickSeconds(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds * 4; i++)
        {
            engine.TickPhysics(0.25);
        }
    }

    /// <summary>The follower's shortest path to the 28R threshold on the right circuit, from its position and heading.</summary>
    private static double FollowerPathNm(AircraftState follower)
    {
        RunwayInfo rwy = Oak("28R");
        var circuit = new FollowPatternReturn(rwy, PatternDirection.Right, PatternAltitudeFt(rwy), false);
        return VfrFollowPhase.FollowerPathToThresholdNm(follower, circuit);
    }

    /// <summary>Asserts the pursuit still returns to the circuit it was given: same runway, direction and from-base flag.</summary>
    private static void AssertPatternReturnKept(FollowPatternReturn expected, FollowPatternReturn? actual)
    {
        Assert.NotNull(actual);
        Assert.True(AirborneFollowHelper.IsSameRunway(expected.Runway, actual.Runway), $"return runway {actual.Runway.Designator}");
        Assert.Equal(expected.Direction, actual.Direction);
        Assert.Equal(expected.FromBase, actual.FromBase);
    }

    /// <summary>Asserts the follower, ticked on behind its new lead, installed no pattern entry or downwind.</summary>
    private static void AssertNoEntryOrDownwind(AircraftState follower)
    {
        Assert.DoesNotContain(follower.Phases!.Phases, phase => phase is PatternEntryPhase);
        Assert.DoesNotContain(follower.Phases.Phases, phase => phase is DownwindPhase);
    }

    /// <summary>Asserts the lead's remaining path to 28R against the follower's path, and logs both.</summary>
    private void AssertLeadPath(AircraftState follower, AircraftState lead, bool expectAhead)
    {
        double followerNm = FollowerPathNm(follower);
        double leadNm = AirborneFollowHelper.SequenceRemainingPathNm(lead, Oak("28R"));
        output.WriteLine($"follower path {followerNm:F2} nm, {lead.Callsign} remaining path {leadNm:F2} nm");
        Assert.True(expectAhead ? (leadNm < followerNm) : (leadNm > followerNm), $"follower path {followerNm:F2} nm, lead path {leadNm:F2} nm");
    }

    /// <summary>The follower's state before FOLLOW, to prove a refusal leaves it untouched.</summary>
    private sealed record FollowerState(
        PhaseList? Phases,
        Phase? Current,
        int PhaseCount,
        string? Followed,
        string? Target,
        FollowPatternReturn? PatternReturn,
        bool TurningOut,
        bool InSight
    );

    private static FollowerState Capture(AircraftState ac)
    {
        var pursuit = ac.Phases?.CurrentPhase as VfrFollowPhase;
        return new FollowerState(
            ac.Phases,
            ac.Phases?.CurrentPhase,
            ac.Phases?.Phases.Count ?? 0,
            ac.Approach.FollowingCallsign,
            pursuit?.TargetCallsign,
            pursuit?.PatternReturn,
            pursuit?.TurningOut ?? false,
            ac.Approach.HasReportedTrafficInSight
        );
    }

    private CommandResult Send(SimulationEngine engine, string command)
    {
        CommandResult result = engine.SendCommand(Follower, command);
        output.WriteLine($"{command}: success={result.Success} — {result.Message}");
        return result;
    }

    private static void AssertRefusedUnchanged(AircraftState follower, FollowerState before, CommandResult result, string expectedMessage)
    {
        Assert.False(result.Success, result.Message);
        Assert.Equal(expectedMessage, result.Message);
        Assert.Equal(before, Capture(follower));
    }

    // ─── A pursuit that left its circuit from base ───

    /// <summary>
    /// Regression pin: this also passed before the pursuit compared new leads, since a follower that can join the lead's final
    /// directly (<c>CanJoinLeadFinalDirectly</c>) was already retargeted in place. The base lead below proves the comparison.
    /// </summary>
    [Fact]
    public void FromBasePursuit_NewSameRunwayLeadAhead_RetargetsInPlace()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddBasePursuitFollower(engine, Other, fromBase: true);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        FollowPatternReturn patternReturn = Assert.IsType<FollowPatternReturn>(pursuit.PatternReturn);
        AircraftState lead = AddFinalLead(engine, Leader, "28R", 1.0);
        AssertLeadPath(follower, lead, expectAhead: true);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRetargetedInPlace(follower, pursuit, patternReturn, result);
    }

    /// <summary>Asserts FOLLOW was accepted in place: the same pursuit, now on the new lead, its pattern return kept.</summary>
    private static void AssertRetargetedInPlace(
        AircraftState follower,
        VfrFollowPhase pursuit,
        FollowPatternReturn patternReturn,
        CommandResult result
    )
    {
        Assert.True(result.Success, result.Message);
        Assert.Equal($"Follow {Leader}", result.Message);
        Assert.Same(pursuit, follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, pursuit.TargetCallsign);
        AssertPatternReturnKept(patternReturn, pursuit.PatternReturn);
        Assert.True(patternReturn.FromBase);
        Assert.DoesNotContain(follower.Phases.Phases, phase => phase is PatternEntryPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    /// <summary>
    /// A lead ahead on base, not on final: the follower cannot join a final the lead is not on, so without the comparison
    /// the follow would rebuild into a downwind entry.
    /// </summary>
    [Fact]
    public void FromBasePursuit_NewSameRunwayLeadAheadOnBase_RetargetsInPlace()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddBasePursuitFollower(engine, Other, fromBase: true);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        FollowPatternReturn patternReturn = Assert.IsType<FollowPatternReturn>(pursuit.PatternReturn);
        AircraftState lead = AddBaseLead(engine);
        AssertLeadPath(follower, lead, expectAhead: true);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRetargetedInPlace(follower, pursuit, patternReturn, result);
        TickSeconds(engine, 5);
        AssertNoEntryOrDownwind(follower);
    }

    /// <summary>A straight-in entrant converges on the same final, so it is measured by path: here it is ahead.</summary>
    [Fact]
    public void FromBasePursuit_NewStraightInEntrantAhead_RetargetsInPlace()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddBasePursuitFollower(engine, Other, fromBase: true);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        FollowPatternReturn patternReturn = Assert.IsType<FollowPatternReturn>(pursuit.PatternReturn);
        AircraftState lead = AddStraightInLead(engine, 1.5, 1.8, 0.4);
        AssertLeadPath(follower, lead, expectAhead: true);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRetargetedInPlace(follower, pursuit, patternReturn, result);
    }

    [Fact]
    public void FromBasePursuit_NewStraightInEntrantBehind_RefusedUnchanged()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddBasePursuitFollower(engine, Other, fromBase: true);
        FollowerState before = Capture(follower);
        AircraftState lead = AddStraightInLead(engine, 3.0, 6.0, 2.0);
        AssertLeadPath(follower, lead, expectAhead: false);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, LeaderNotAheadText);
    }

    /// <summary>A runwayless lead inside the ±60° cone is pursued: the same pursuit, now on the new lead.</summary>
    [Fact]
    public void FromBasePursuit_NewRunwaylessLeadInsideCone_RetargetsInPlace()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddBasePursuitFollower(engine, Other, fromBase: true);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        LatLon ahead = GeoMath.ProjectPoint(follower.Position, follower.TrueTrack, 2.0);
        AircraftState lead = MakeVfr(Leader, ahead, follower.TrueTrack, 1000);
        lead.Phases = new PhaseList();
        engine.World.AddAircraft(lead);
        Assert.True(AirborneFollowHelper.IsLeadAheadOfTrack(follower, lead), "the lead must be inside the follower's ±60° cone");

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Follow {Leader}", result.Message);
        Assert.Same(pursuit, follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, pursuit.TargetCallsign);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    /// <summary>
    /// A lead with a 28R runway but no leg in the landing sequence (itself pursuing, off the final) has an infinite remaining
    /// path, so it is not ahead however close it is.
    /// </summary>
    [Fact]
    public void FromBasePursuit_NewRunwayLeadWithNoSequenceLeg_RefusedUnchanged()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddBasePursuitFollower(engine, Other, fromBase: true);
        FollowerState before = Capture(follower);
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 1.5, 0.5), rwy.TrueHeading - 90.0, 800);
        lead.Phases = new PhaseList { AssignedRunway = rwy };
        lead.Phases.Add(new VfrFollowPhase(Other, null)); // CurrentIndex defaults to 0, so CurrentPhase is the pursuit without Start()
        engine.World.AddAircraft(lead);
        Assert.Null(AirborneFollowHelper.PatternLegIndex(lead));
        Assert.True(double.IsPositiveInfinity(AirborneFollowHelper.SequenceRemainingPathNm(lead, rwy)), "the lead must have no sequence leg");

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, LeaderNotAheadText);
    }

    [Fact]
    public void FromBasePursuit_NewSameRunwayLeadBehind_RefusedUnchanged()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddBasePursuitFollower(engine, Other, fromBase: true);
        FollowerState before = Capture(follower);
        AircraftState lead = AddFinalLead(engine, Leader, "28R", 6.0);
        AssertLeadPath(follower, lead, expectAhead: false);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, LeaderNotAheadText);
    }

    /// <summary>
    /// A close-in base entrant ahead by path is still refused: getting behind an entrant from base would need a 360, so only
    /// the entry rule can refuse it here.
    /// </summary>
    [Fact]
    public void FromBasePursuit_NewEntryLead_RefusedUnchanged()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddBasePursuitFollower(engine, Other, fromBase: true);
        FollowerState before = Capture(follower);
        AircraftState lead = AddBaseEntryLead(engine);
        AssertLeadPath(follower, lead, expectAhead: true);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, LeaderNotAheadText);
    }

    [Fact]
    public void FromBasePursuit_NewLeadOnOtherRunway_RefusedUnchanged()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddBasePursuitFollower(engine, Other, fromBase: true);
        FollowerState before = Capture(follower);
        AddFinalLead(engine, Leader, "28L", 1.0);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, $"Unable, {Leader} is landing runway 28L, request vectors");
    }

    [Fact]
    public void FromBasePursuit_NewRunwaylessLeadOutsideCone_RefusedByCone()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddBasePursuitFollower(engine, Other, fromBase: true);
        FollowerState before = Capture(follower);
        LatLon astern = GeoMath.ProjectPoint(follower.Position, follower.TrueTrack.ToReciprocal(), 3.0);
        AircraftState lead = MakeVfr(Leader, astern, follower.TrueTrack.ToReciprocal(), 1000);
        lead.Phases = new PhaseList();
        engine.World.AddAircraft(lead);
        Assert.False(AirborneFollowHelper.IsLeadAheadOfTrack(follower, lead), "the lead must be outside the follower's ±60° cone");

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, LeaderNotAheadText);
    }

    // ─── A pursuit turning out ───

    [Fact]
    public void TurnOut_NewLeadBehindOnTurnOutCircuit_RefusedUnchanged()
    {
        SimulationEngine engine = BuildEngine();
        AddFinalLead(engine, Leader, "28R", 3.0);
        AircraftState follower = AddTurnOutPursuitFollower(engine, Leader);
        TickSeconds(engine, 1);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.True(pursuit.TurningOut, "the pursuit should have turned out alongside the lead on final");
        FollowerState before = Capture(follower);
        AircraftState other = AddFinalLead(engine, Other, "28R", 8.0);
        AssertLeadPath(follower, other, expectAhead: false);

        CommandResult result = Send(engine, $"FOLLOW {Other}");

        AssertRefusedUnchanged(follower, before, result, OtherNotAheadText);
    }

    [Fact]
    public void TurnOut_NewLeadAhead_RetargetsAndEndsTurnOut()
    {
        SimulationEngine engine = BuildEngine();
        AddFinalLead(engine, Leader, "28R", 3.0);
        AircraftState follower = AddTurnOutPursuitFollower(engine, Leader);
        TickSeconds(engine, 1);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.True(pursuit.TurningOut, "the pursuit should have turned out alongside the lead on final");
        FollowPatternReturn? patternReturn = pursuit.PatternReturn;
        AircraftState other = AddFinalLead(engine, Other, "28R", 0.5);
        AssertLeadPath(follower, other, expectAhead: true);

        CommandResult result = Send(engine, $"FOLLOW {Other}");

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Follow {Other}", result.Message);
        Assert.Same(pursuit, follower.Phases!.CurrentPhase);
        Assert.Equal(Other, pursuit.TargetCallsign);
        Assert.False(pursuit.TurningOut, "following a new lead ends the turn-out");
        AssertPatternReturnKept(Assert.IsType<FollowPatternReturn>(patternReturn), pursuit.PatternReturn);
        Assert.Equal(Other, follower.Approach.FollowingCallsign);

        TickSeconds(engine, 5);
        Assert.Same(pursuit, follower.Phases!.CurrentPhase);
        Assert.False(pursuit.TurningOut, "the pursuit must not turn out again behind a new lead that is ahead");
    }

    // ─── A pursuit that did not leave from base (table H) ───

    [Fact]
    public void NotFromBasePursuit_NewRunwayLead_KeepsDownwindEntry()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddBasePursuitFollower(engine, Other, fromBase: false);
        AddDownwindLead(engine);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.IsNotType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Contains(follower.Phases.Phases, phase => phase is DownwindPhase);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }
}
