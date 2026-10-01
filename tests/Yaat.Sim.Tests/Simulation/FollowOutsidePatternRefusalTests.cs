using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// FOLLOW to a follower with no pattern leg — an aircraft outside the pattern, an approach follower or a pursuit
/// (<see cref="VfrFollowPhase"/>) — when the lead is on the ground or bound for another airport. Both are refused:
/// "Unable, {T} is on the ground" (a lead rolling out on the runway a pursuit returns to is accepted — the lead lifecycle
/// ends the follow next tick; from an approach or outside the pattern every ground lead is refused) and
/// "Unable, {T} is inbound to {APT}, request vectors" (only
/// when the follower's own airport is known and differs). A re-FOLLOW of the lead already being pursued is
/// acknowledged and changes nothing: same phase instance, same path, same pattern return, turn-out under way kept.
/// Real KOAK 28R navdata.
/// </summary>
public class FollowOutsidePatternRefusalTests(ITestOutputHelper output)
{
    private const string Leader = "LEAD1";
    private const string Other = "OTHER1";
    private const string Follower = "FOLL1";

    private const string OnTheGroundText = $"Unable, {Leader} is on the ground";
    private const string InboundToHwdText = $"Unable, {Leader} is inbound to HWD, request vectors";

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

    private static AircraftState MakeVfr(string callsign, LatLon position, TrueHeading heading, double altitudeFt, string destination) =>
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
                Destination = destination,
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(2000),
                CruiseSpeed = 110,
            },
        };

    /// <summary>An airborne VFR follower with an empty phase list — outside the pattern, bound for <paramref name="destination"/>.</summary>
    private static AircraftState AddOutsidePatternFollower(SimulationEngine engine, string destination)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, 4.0, 1.0), rwy.TrueHeading, 1000, destination);
        follower.Phases = new PhaseList();
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        return follower;
    }

    /// <summary>A follower pursuing <paramref name="target"/>, returning to the 28R right-traffic circuit.</summary>
    private static AircraftState AddPursuitFollower(SimulationEngine engine, string target, double alongNm, double crossNm)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState follower = MakeVfr(Follower, OffFinal(rwy, alongNm, crossNm), rwy.TrueHeading, 1000, "KOAK");
        follower.Approach.HasReportedTrafficInSight = true;
        engine.World.AddAircraft(follower);
        CommandDispatcher.InstallVfrFollowPhase(
            follower,
            target,
            new FollowPatternReturn(rwy, PatternDirection.Right, PatternAltitudeFt(rwy), false),
            climbOutGate: null
        );
        return follower;
    }

    /// <summary>The lead on the ground at the 28R threshold, with no phase of its own.</summary>
    private static AircraftState AddGroundLead(SimulationEngine engine)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, Threshold(rwy), rwy.TrueHeading, rwy.AirportElevationFt, "KOAK");
        lead.IsOnGround = true;
        lead.IndicatedAirspeed = 0;
        lead.Phases = new PhaseList();
        engine.World.AddAircraft(lead);
        return lead;
    }

    /// <summary>The lead rolling out on 28R just past its threshold.</summary>
    private static AircraftState AddRollingOutLead(SimulationEngine engine)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 0.2, 0.0), rwy.TrueHeading, rwy.AirportElevationFt, "KOAK");
        lead.IsOnGround = true;
        lead.Phases = new PhaseList { AssignedRunway = rwy };
        lead.Phases.Add(new LandingPhase());
        engine.World.AddAircraft(lead);
        return lead;
    }

    /// <summary>The lead airborne over the 28R final, filed to Hayward: bound elsewhere.</summary>
    private static AircraftState AddElsewhereLead(SimulationEngine engine)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 2.0, 0.0), rwy.TrueHeading, 1200, "KHWD");
        lead.Phases = new PhaseList();
        engine.World.AddAircraft(lead);
        return lead;
    }

    /// <summary>The lead on the 28R right downwind, 1 nm before its base-turn point.</summary>
    private static AircraftState AddDownwindLead(SimulationEngine engine)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, 1.0, 1.0), rwy.TrueHeading, 1000, "KOAK");
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

    /// <summary>The lead on the 28R final <paramref name="alongNm"/> out, on its glidepath, in the right-traffic circuit.</summary>
    private static AircraftState AddFinalLead(SimulationEngine engine, double alongNm)
    {
        RunwayInfo rwy = Oak("28R");
        AircraftState lead = MakeVfr(Leader, OffFinal(rwy, alongNm, 0.0), rwy.TrueHeading, rwy.ElevationFt + (318.0 * alongNm), "KOAK");
        engine.World.AddAircraft(lead);
        PutOnCircuit(lead, rwy, PatternEntryLeg.Final);
        Assert.IsType<FinalApproachPhase>(lead.Phases!.CurrentPhase);
        return lead;
    }

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

    private static void TickSeconds(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds * 4; i++)
        {
            engine.TickPhysics(0.25);
        }
    }

    /// <summary>The follower's state before FOLLOW, to prove a refusal leaves it untouched.</summary>
    private sealed record FollowerState(PhaseList? Phases, Phase? Current, int PhaseCount, ClearanceType? Clearance, string? Followed);

    private static FollowerState Capture(AircraftState ac) =>
        new(ac.Phases, ac.Phases?.CurrentPhase, ac.Phases?.Phases.Count ?? 0, ac.Phases?.LandingClearance, ac.Approach.FollowingCallsign);

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

    // ─── Outside the pattern ───

    [Fact]
    public void OutsidePattern_LeadOnGround_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddOutsidePatternFollower(engine, "KOAK");
        FollowerState before = Capture(follower);
        AddGroundLead(engine);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, OnTheGroundText);
    }

    [Fact]
    public void OutsidePattern_LeadRollingOut_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddOutsidePatternFollower(engine, "KOAK");
        FollowerState before = Capture(follower);
        AddRollingOutLead(engine);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, OnTheGroundText);
    }

    [Fact]
    public void OutsidePattern_LeadBoundElsewhere_FollowerBoundHere_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddOutsidePatternFollower(engine, "KOAK");
        FollowerState before = Capture(follower);
        AddElsewhereLead(engine);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, InboundToHwdText);
    }

    [Fact]
    public void OutsidePattern_LeadBoundElsewhere_FollowerNoDestination_IsAccepted()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddOutsidePatternFollower(engine, "");
        AddElsewhereLead(engine);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, pursuit.TargetCallsign);
        Assert.Null(pursuit.PatternReturn);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    /// <summary>
    /// An outside-the-pattern follower that still carries an assigned runway: an airspace-boundary hold keeps its
    /// phase list (and so its runway) while listing every command as allowed. It is still outside the pattern, so a
    /// rolling-out lead earns no exception there.
    /// </summary>
    [Fact]
    public void OutsidePattern_WithRunway_LeadRollingOut_Refused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddOutsidePatternFollower(engine, "KOAK");
        follower.Phases = new PhaseList { AssignedRunway = Oak("28R") };
        follower.Phases.Add(
            new AirspaceBoundaryHoldPhase
            {
                AirspaceClass = AirspaceClass.Charlie,
                Ident = "OAK",
                ReferencePosition = new LatLon(37.7213, -122.2208),
                OrbitDirection = TurnDirection.Right,
            }
        );
        FollowerState before = Capture(follower);
        AddRollingOutLead(engine);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, OnTheGroundText);
    }

    // ─── Approach followers ───

    [Fact]
    public void ApproachFollower_LeadOnGround_Refused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 2.5);
        FollowerState before = Capture(follower);
        AddGroundLead(engine);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, OnTheGroundText);
    }

    /// <summary>
    /// Every ground lead is refused from an approach, one rolling out on the follower's own runway included: accepting it
    /// would tear the approach down for a follow the lead lifecycle ends on the next tick, leaving no phase. The approach
    /// is kept and keeps flying.
    /// </summary>
    [Fact]
    public void ApproachFollower_LeadRollingOutOnItsRunway_Refused_KeepsApproach()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 2.5);
        FollowerState before = Capture(follower);
        AddRollingOutLead(engine);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, OnTheGroundText);
        TickSeconds(engine, 1);
        Assert.True(
            follower.Phases!.CurrentPhase is InterceptCoursePhase or FinalApproachPhase,
            $"the approach should continue, got {follower.Phases.CurrentPhase?.GetType().Name ?? "no phase"}"
        );
        Assert.Equal(ClearanceType.ClearedToLand, follower.Phases.LandingClearance);
        Assert.Null(follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void ApproachFollower_LeadBoundElsewhere_Refused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = FollowSequenceRefusalTests.AddApproachFollower(engine, 2.5);
        FollowerState before = Capture(follower);
        AddElsewhereLead(engine);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        AssertRefusedUnchanged(follower, before, result, InboundToHwdText);
    }

    // ─── Pursuing followers ───
    [Fact]
    public void Pursuit_LeadOnGround_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddPursuitFollower(engine, Other, 4.0, 1.0);
        Phase? before = follower.Phases!.CurrentPhase;
        AddGroundLead(engine);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.False(result.Success, result.Message);
        Assert.Equal(OnTheGroundText, result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Equal(Other, follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void Pursuit_LeadRollingOutOnItsRunway_IsAccepted()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddPursuitFollower(engine, Other, 4.0, 1.0);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        AddRollingOutLead(engine);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.Same(pursuit, follower.Phases!.CurrentPhase);
        Assert.Equal(Leader, pursuit.TargetCallsign);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void Pursuit_LeadBoundElsewhere_IsRefused()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddPursuitFollower(engine, Other, 4.0, 1.0);
        Phase? before = follower.Phases!.CurrentPhase;
        AddElsewhereLead(engine);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.False(result.Success, result.Message);
        Assert.Equal(InboundToHwdText, result.Message);
        Assert.Same(before, follower.Phases!.CurrentPhase);
        Assert.Equal(Other, follower.Approach.FollowingCallsign);
    }

    // ─── Re-FOLLOW of the lead already being pursued ───

    [Fact]
    public void Pursuit_ReFollowSameEstablishedLead_KeepsPursuitUnchanged()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddPursuitFollower(engine, Leader, 4.0, 1.0);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        FollowPatternReturn patternReturn = Assert.IsType<FollowPatternReturn>(pursuit.PatternReturn);
        AddDownwindLead(engine);

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        VfrFollowPhase after = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.Same(pursuit, after);
        Assert.Same(patternReturn, after.PatternReturn);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    /// <summary>
    /// A re-FOLLOW of the runwayless lead already being pursued is acknowledged even once the lead is outside the ±60° cone
    /// (it overtook, or the follower S-turned): the pilot is already doing what it was told.
    /// </summary>
    [Fact]
    public void Pursuit_ReFollowSameRunwaylessLeadOutsideCone_KeepsPursuit()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddPursuitFollower(engine, Leader, 4.0, 1.0);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        FollowPatternReturn patternReturn = Assert.IsType<FollowPatternReturn>(pursuit.PatternReturn);
        LatLon astern = GeoMath.ProjectPoint(follower.Position, follower.TrueTrack.ToReciprocal(), 3.0);
        AircraftState lead = MakeVfr(Leader, astern, follower.TrueTrack.ToReciprocal(), 1000, "KOAK");
        lead.Phases = new PhaseList();
        engine.World.AddAircraft(lead);
        Assert.False(AirborneFollowHelper.IsLeadAheadOfTrack(follower, lead), "the lead must be outside the follower's ±60° cone");

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Follow {Leader}", result.Message);
        Assert.Same(pursuit, follower.Phases!.CurrentPhase);
        Assert.Same(patternReturn, pursuit.PatternReturn);
        Assert.Equal(Leader, pursuit.TargetCallsign);
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }

    [Fact]
    public void TurnOut_ReFollowSameLead_KeepsTurnOut()
    {
        SimulationEngine engine = BuildEngine();
        AddFinalLead(engine, 3.0);
        AircraftState follower = AddPursuitFollower(engine, Leader, 1.2, 1.2);

        TickSeconds(engine, 1);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases!.CurrentPhase);
        Assert.True(pursuit.TurningOut, "the pursuit should have turned out alongside the lead on final");

        CommandResult result = Send(engine, $"FOLLOW {Leader}");

        Assert.True(result.Success, result.Message);
        Assert.Same(pursuit, follower.Phases!.CurrentPhase);
        Assert.True(pursuit.TurningOut, "a re-FOLLOW of the same lead must keep the turn-out under way");
        Assert.Equal(Leader, follower.Approach.FollowingCallsign);
    }
}
