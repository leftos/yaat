using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// An option clearance's pattern modifier (<c>COPT MLT 28L</c> issued on the 28R circuit) arms
/// <see cref="PhaseList.PatternRunway"/> ahead of <see cref="PhaseList.AssignedRunway"/> and waits for
/// the next cycle terminator to fly the transition. <c>FOLLOW</c> replaces the follower's phase list
/// outright — through the pattern join (<c>TryJoinLeadPattern</c>) and through the lead-landed final
/// sequence (<c>SequenceOntoFinal</c>) — and both rebuilds used to stamp the joined runway into both
/// fields, silently cancelling the armed clearance. The arming is carried over instead: the follow's
/// own circuit belongs to the lead's runway, the armed transition still applies after the terminator
/// that follows it. Joining the armed runway itself satisfies the arming, so both fields become it.
///
/// Real KOAK runways: 28R/28L are close parallels (no midfield crossing), 33 crosses them.
/// </summary>
[Collection("NavDbMutator")]
public class FollowArmedPatternRunwayTests
{
    private const string Lead = "LEAD";
    private const string Follower = "FOLL";

    public FollowArmedPatternRunwayTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // ---------------------------------------------------------------------------------------------
    // Pattern join — the lead is flying a circuit
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void PatternJoin_ArmedParallel_FollowingALeadOnTheFlownRunway_KeepsTheArmedPatternRunway()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? flown = navDb?.GetRunway("KOAK", "28R");
        RunwayInfo? armed = navDb?.GetRunway("KOAK", "28L");
        if (navDb is null || flown is null || armed is null)
        {
            return;
        }

        AircraftState joined = JoinLeadPattern(
            leadRunway: flown,
            flownRunway: flown,
            armedRunway: armed,
            direction: PatternDirection.Right,
            transitionAltitude: null
        );

        Assert.Equal("28R", joined.Phases?.AssignedRunway?.Designator);
        Assert.Equal("28L", joined.Phases?.PatternRunway?.Designator);
        // 28R/28L are close parallels — nothing crosses the field — but the follower still leaves the
        // sequence it was just put into one terminator later, so the arming itself is announced.
        Assert.Contains(joined.PendingWarnings, w => w.Contains("28L stays armed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(joined.PendingWarnings, w => w.Contains("will leave the 28R sequence", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(joined.PendingWarnings, w => w.Contains("cross midfield", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PatternJoin_ArmedRunwayIsTheLeadRunway_SatisfiesTheArmingOnBothFields()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? flown = navDb?.GetRunway("KOAK", "28R");
        RunwayInfo? armed = navDb?.GetRunway("KOAK", "28L");
        if (navDb is null || flown is null || armed is null)
        {
            return;
        }

        AircraftState joined = JoinLeadPattern(
            leadRunway: armed,
            flownRunway: flown,
            armedRunway: armed,
            direction: PatternDirection.Left,
            transitionAltitude: null
        );

        Assert.Equal("28L", joined.Phases?.AssignedRunway?.Designator);
        Assert.Equal("28L", joined.Phases?.PatternRunway?.Designator);
        Assert.DoesNotContain(joined.PendingWarnings, w => w.Contains("stays armed", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A pattern runway armed at another airport is not this circuit's business: the follower is joining
    /// traffic here, and carrying it would arm a transition to a field it is not at.
    /// </summary>
    [Fact]
    public void PatternJoin_ArmedRunwayAtAnotherAirport_IsNotCarriedOver()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? flown = navDb?.GetRunway("KOAK", "28R");
        RunwayInfo? armedElsewhere = navDb?.GetRunway("KSFO", "28L");
        if (navDb is null || flown is null || armedElsewhere is null)
        {
            return;
        }

        AircraftState joined = JoinLeadPattern(
            leadRunway: flown,
            flownRunway: flown,
            armedRunway: armedElsewhere,
            direction: PatternDirection.Right,
            transitionAltitude: null
        );

        Assert.Equal("28R", joined.Phases?.AssignedRunway?.Designator);
        Assert.Equal("28R", joined.Phases?.PatternRunway?.Designator);
        Assert.DoesNotContain(joined.PendingWarnings, w => w.Contains("stays armed", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A carried-over transition to a runway that is not a close parallel joins through a
    /// <see cref="MidfieldCrossingPhase"/>, which the RPO last heard about on a circuit the FOLLOW has
    /// now replaced — so it is announced (AIM 4-3-5, an unexpected maneuver in the pattern).
    /// </summary>
    [Fact]
    public void PatternJoin_ArmedCrossingRunway_WarnsThatTheCarriedTransitionCrossesTheField()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? flown = navDb?.GetRunway("KOAK", "28R");
        RunwayInfo? armed = navDb?.GetRunway("KOAK", "33");
        if (navDb is null || flown is null || armed is null)
        {
            return;
        }

        AircraftState joined = JoinLeadPattern(
            leadRunway: flown,
            flownRunway: flown,
            armedRunway: armed,
            direction: PatternDirection.Right,
            transitionAltitude: null
        );

        Assert.Equal("28R", joined.Phases?.AssignedRunway?.Designator);
        Assert.Equal("33", joined.Phases?.PatternRunway?.Designator);
        Assert.Contains(joined.PendingWarnings, w => w.Contains("33 stays armed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(joined.PendingWarnings, w => w.Contains("cross midfield", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------------------------------
    // Final sequence — the lead has landed and the follower is sequenced onto its runway
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void FinalSequence_ArmedParallel_AfterTheLeadLands_KeepsTheArmedPatternRunway()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? flown = navDb?.GetRunway("KOAK", "28R");
        RunwayInfo? armed = navDb?.GetRunway("KOAK", "28L");
        if (navDb is null || flown is null || armed is null)
        {
            return;
        }

        // Free (north) side of 28R but tracking away from the final course: the landed-lead sequence
        // refuses the 180° intercept, the follow ends and the follower re-enters the 28R circuit.
        AircraftState lead = MakeVfr(Lead, OffFinal(flown, 0.8, 0), flown.TrueHeading, altitude: 300);
        lead.Phases = new PhaseList { AssignedRunway = flown, PatternRunway = flown };
        lead.Phases.Add(new FinalApproachPhase());

        AircraftState follower = MakeVfr(Follower, OffFinal(flown, 3.5, 0.6), new TrueHeading(112), altitude: 1200);
        follower.Approach.FollowingCallsign = Lead;
        var phase = new VfrFollowPhase(Lead, patternReturn: null);
        follower.Phases = new PhaseList
        {
            AssignedRunway = flown,
            PatternRunway = armed,
            TrafficDirection = PatternDirection.Right,
        };
        follower.Phases.Add(phase);

        PhaseContext ctx = Ctx(follower, flown, cs => cs == Lead ? lead : null);
        follower.Phases.Start(ctx);

        // First tick captures the lead's landing runway while it is still airborne on final.
        phase.OnTick(ctx);
        Assert.IsType<VfrFollowPhase>(follower.Phases.CurrentPhase);

        lead.IsOnGround = true;
        phase.OnTick(ctx);

        PatternEntryPhase entry = Assert.IsType<PatternEntryPhase>(follower.Phases.CurrentPhase);
        Assert.NotEqual(PatternEntryKind.Final, entry.Kind);
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Contains(
            follower.PendingWarnings,
            w => w.Contains("follow ended, re-entering right traffic runway 28R", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Equal("28R", follower.Phases.AssignedRunway?.Designator);
        Assert.Equal("28L", follower.Phases.PatternRunway?.Designator);
        Assert.Contains(follower.PendingWarnings, w => w.Contains("28L stays armed", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The lead lands on the armed runway (28L, with 2,000 ft deferred to it) and the follower, tracking the final course,
    /// is sequenced onto that final: the sequence satisfies the arming, so 2,000 becomes the standing pattern altitude for
    /// the circuit after the landing and nothing stays deferred.
    /// </summary>
    [Fact]
    public void FinalSequence_ArmedRunwayIsTheLeadRunwayWithDeferredAltitude_ClaimsIt()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? flown = navDb?.GetRunway("KOAK", "28R");
        RunwayInfo? armed = navDb?.GetRunway("KOAK", "28L");
        if (navDb is null || flown is null || armed is null)
        {
            return;
        }

        AircraftState lead = MakeVfr(Lead, OffFinal(armed, 0.8, 0), armed.TrueHeading, altitude: 300);
        lead.Phases = new PhaseList { AssignedRunway = armed, PatternRunway = armed };
        lead.Phases.Add(new FinalApproachPhase());

        AircraftState follower = MakeVfr(Follower, OffFinal(armed, 3.5, -0.6), new TrueHeading(112), altitude: 1200);
        follower.Approach.FollowingCallsign = Lead;
        var phase = new VfrFollowPhase(Lead, patternReturn: null);
        follower.Phases = new PhaseList
        {
            AssignedRunway = flown,
            PatternRunway = armed,
            TransitionPatternAltitude = new DeferredPatternAltitude(2000),
            TrafficDirection = PatternDirection.Left,
        };
        follower.Phases.Add(phase);

        PhaseContext ctx = Ctx(follower, armed, cs => cs == Lead ? lead : null);
        follower.Phases.Start(ctx);

        // First tick captures the lead's landing runway while it is still airborne on final.
        phase.OnTick(ctx);
        Assert.IsType<VfrFollowPhase>(follower.Phases.CurrentPhase);

        // Turned onto the final course by the time the lead lands, so the landed-lead sequence takes the follower.
        follower.TrueHeading = armed.TrueHeading;
        follower.TrueTrack = armed.TrueHeading;
        lead.IsOnGround = true;
        phase.OnTick(ctx);

        PatternEntryPhase entry = Assert.IsType<PatternEntryPhase>(follower.Phases.CurrentPhase);
        Assert.Equal(PatternEntryKind.Final, entry.Kind);
        Assert.Equal("28L", follower.Phases.AssignedRunway?.Designator);
        Assert.Equal("28L", follower.Phases.PatternRunway?.Designator);
        Assert.Equal(2000.0, follower.Pattern.AltitudeOverrideFt);
        Assert.Null(follower.Phases.TransitionPatternAltitude);
    }

    /// <summary>
    /// A follow that ends on its own (here the lead is lost) re-enters the circuit it left through
    /// <c>TryEnterPattern</c>, which builds a fresh list; the armed pattern runway survives the re-entry.
    /// </summary>
    [Fact]
    public void ReturnToPattern_KeepsAnArmedPatternRunway()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? flown = navDb?.GetRunway("KOAK", "28R");
        RunwayInfo? armed = navDb?.GetRunway("KOAK", "28L");
        if (navDb is null || flown is null || armed is null)
        {
            return;
        }

        AircraftState follower = MakeVfr(Follower, OffFinal(flown, 3.5, 1.0), flown.TrueHeading, altitude: 1200);
        follower.Approach.FollowingCallsign = Lead;
        var patternReturn = new FollowPatternReturn(flown, PatternDirection.Right, flown.AirportElevationFt + 1000, FromBase: false);
        var phase = new VfrFollowPhase(Lead, patternReturn);
        follower.Phases = new PhaseList
        {
            AssignedRunway = flown,
            PatternRunway = armed,
            TrafficDirection = PatternDirection.Right,
        };
        follower.Phases.Add(phase);

        PhaseContext ctx = Ctx(follower, flown, _ => null);
        follower.Phases.Start(ctx);

        Assert.True(phase.OnTick(ctx));

        Assert.IsNotType<VfrFollowPhase>(follower.Phases.CurrentPhase);
        Assert.Null(follower.Approach.FollowingCallsign);
        Assert.Contains(
            follower.PendingWarnings,
            w => w.Contains("follow ended, re-entering right traffic runway 28R", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Equal("28R", follower.Phases.AssignedRunway?.Designator);
        Assert.Equal("28L", follower.Phases.PatternRunway?.Designator);
        Assert.Contains(follower.PendingWarnings, w => w.Contains("28L stays armed", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------------------------------
    // The altitude an option modifier deferred to the armed runway travels with the arming
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>COPT MLT 28L 20</c> on the 28R circuit, then FOLLOW into a 28R lead's pattern: the join carries the armed 28L
    /// and the 2,000 ft it deferred, so the 28L transition circuit after the next terminator is built at 2,000.
    /// </summary>
    [Fact]
    public void PatternJoin_ArmedParallelWithDeferredAltitude_BuildsTheTransitionAtIt()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? flown = navDb?.GetRunway("KOAK", "28R");
        RunwayInfo? armed = navDb?.GetRunway("KOAK", "28L");
        if (navDb is null || flown is null || armed is null)
        {
            return;
        }

        AircraftState joined = JoinLeadPattern(
            leadRunway: flown,
            flownRunway: flown,
            armedRunway: armed,
            direction: PatternDirection.Right,
            transitionAltitude: new DeferredPatternAltitude(2000)
        );

        Assert.Equal("28L", joined.Phases?.PatternRunway?.Designator);
        Assert.Equal(new DeferredPatternAltitude(2000), joined.Phases?.TransitionPatternAltitude);

        DownwindPhase transitionDownwind = FlyLowApproachIntoNextCircuit(joined);

        Assert.Equal("28L", joined.Phases?.AssignedRunway?.Designator);
        Assert.Equal(2000.0, transitionDownwind.Waypoints!.PatternAltitude);
    }

    /// <summary>
    /// A follow that ends on its own re-enters the 28R circuit through <c>TryEnterPattern</c>'s fresh list; the armed 28L
    /// and the 2,000 ft it deferred both survive, and the 28L transition circuit is built at 2,000.
    /// </summary>
    [Fact]
    public void ReturnToPattern_CarriesTheDeferredAltitudeToTheTransition()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? flown = navDb?.GetRunway("KOAK", "28R");
        RunwayInfo? armed = navDb?.GetRunway("KOAK", "28L");
        if (navDb is null || flown is null || armed is null)
        {
            return;
        }

        AircraftState follower = ReturnToPatternWithDeferral(flown, armed, returnRunway: flown, PatternDirection.Right);

        Assert.Equal("28R", follower.Phases?.AssignedRunway?.Designator);
        Assert.Equal("28L", follower.Phases?.PatternRunway?.Designator);
        Assert.Equal(new DeferredPatternAltitude(2000), follower.Phases?.TransitionPatternAltitude);

        DownwindPhase transitionDownwind = FlyLowApproachIntoNextCircuit(follower);

        Assert.Equal("28L", follower.Phases?.AssignedRunway?.Designator);
        Assert.Equal(2000.0, transitionDownwind.Waypoints!.PatternAltitude);
    }

    /// <summary>
    /// A follow that ends with a return to the armed runway (28L) itself satisfies the arming: that circuit is built at
    /// the 2,000 ft the modifier deferred, which becomes the standing override, and nothing stays armed.
    /// </summary>
    [Fact]
    public void ReturnToPattern_OnTheArmedRunway_BuildsThatCircuitAtTheDeferredAltitude()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? flown = navDb?.GetRunway("KOAK", "28R");
        RunwayInfo? armed = navDb?.GetRunway("KOAK", "28L");
        if (navDb is null || flown is null || armed is null)
        {
            return;
        }

        AircraftState follower = ReturnToPatternWithDeferral(flown, armed, returnRunway: armed, PatternDirection.Left);

        Assert.Equal("28L", follower.Phases?.AssignedRunway?.Designator);
        Assert.Equal(2000.0, follower.Pattern.AltitudeOverrideFt);
        Assert.Equal(2000.0, follower.Phases!.Phases.OfType<DownwindPhase>().First().Waypoints!.PatternAltitude);
        Assert.Null(follower.Phases.TransitionPatternAltitude);
    }

    /// <summary>
    /// <c>COPT MLT 28L 20</c> on the 28R circuit, then FOLLOW into a 28L lead's pattern: the join onto the armed runway
    /// satisfies the arming, so the circuit it builds is flown at the 2,000 ft the modifier deferred — the downwind join's
    /// lead-in and the circuit's legs alike — and 2,000 becomes the standing override, as a pattern entry onto the armed
    /// runway does. Nothing stays deferred.
    /// </summary>
    [Fact]
    public void PatternJoin_ArmedRunwayIsTheLeadRunwayWithDeferredAltitude_FliesThatCircuitAtIt()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? flown = navDb?.GetRunway("KOAK", "28R");
        RunwayInfo? armed = navDb?.GetRunway("KOAK", "28L");
        if (navDb is null || flown is null || armed is null)
        {
            return;
        }

        LeadPatternJoin arranged = ArrangeLeadPattern(armed, flown, armed, PatternDirection.Left, new DeferredPatternAltitude(2000));
        // Converging on the downwind at 45° rather than established on it, so the join leads in through a downwind entry.
        TrueHeading convergingHeading = arranged.LeadWaypoints.DownwindHeading + 45.0;
        arranged.Follower.TrueHeading = convergingHeading;
        arranged.Follower.TrueTrack = convergingHeading;

        AircraftState joined = Join(arranged);

        Assert.Equal("28L", joined.Phases?.AssignedRunway?.Designator);
        Assert.Equal("28L", joined.Phases?.PatternRunway?.Designator);
        Assert.Equal(2000.0, joined.Pattern.AltitudeOverrideFt);
        Assert.Null(joined.Phases!.TransitionPatternAltitude);
        PatternEntryPhase leadIn = Assert.IsType<PatternEntryPhase>(joined.Phases.Phases[0]);
        Assert.Equal(2000.0, leadIn.PatternAltitude);
        List<DownwindPhase> downwinds = [.. joined.Phases.Phases.OfType<DownwindPhase>()];
        Assert.NotEmpty(downwinds);
        Assert.All(downwinds, downwind => Assert.Equal(2000.0, downwind.Waypoints!.PatternAltitude));
    }

    /// <summary>
    /// A deferral that is the runway change's clear (<c>COPT MLT 28L</c> with no altitude): the join onto 28L clears the
    /// standing 28R override rather than flying 28R's altitude on 28L, and the circuit keeps the lead's pattern altitude.
    /// </summary>
    [Fact]
    public void PatternJoin_ArmedRunwayIsTheLeadRunwayWithDeferredClear_ClearsTheStandingOverride()
    {
        NavigationDatabase? navDb = TestVnasData.NavigationDb;
        RunwayInfo? flown = navDb?.GetRunway("KOAK", "28R");
        RunwayInfo? armed = navDb?.GetRunway("KOAK", "28L");
        if (navDb is null || flown is null || armed is null)
        {
            return;
        }

        LeadPatternJoin arranged = ArrangeLeadPattern(armed, flown, armed, PatternDirection.Left, new DeferredPatternAltitude(null));
        arranged.Follower.Pattern.SetAltitudeOverride(1500, null, AircraftCategory.Piston);

        AircraftState joined = Join(arranged);

        Assert.Equal("28L", joined.Phases?.AssignedRunway?.Designator);
        Assert.Null(joined.Pattern.AltitudeOverrideFt);
        Assert.Null(joined.Phases!.TransitionPatternAltitude);
        Assert.Equal(arranged.LeadWaypoints.PatternAltitude, joined.Phases.Phases.OfType<DownwindPhase>().First().Waypoints!.PatternAltitude);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixture
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A follower flying <paramref name="flownRunway"/> with <paramref name="armedRunway"/> armed and 2,000 ft deferred to it,
    /// whose follow ends (the lead is lost) and returns to <paramref name="returnRunway"/>'s circuit. Returns the follower
    /// after the return tick.
    /// </summary>
    private static AircraftState ReturnToPatternWithDeferral(
        RunwayInfo flownRunway,
        RunwayInfo armedRunway,
        RunwayInfo returnRunway,
        PatternDirection direction
    )
    {
        AircraftState follower = MakeVfr(Follower, OffFinal(returnRunway, 3.5, 1.0), returnRunway.TrueHeading, altitude: 1200);
        follower.Approach.FollowingCallsign = Lead;
        var patternReturn = new FollowPatternReturn(returnRunway, direction, returnRunway.AirportElevationFt + 1000, FromBase: false);
        var phase = new VfrFollowPhase(Lead, patternReturn);
        follower.Phases = new PhaseList
        {
            AssignedRunway = flownRunway,
            PatternRunway = armedRunway,
            TransitionPatternAltitude = new DeferredPatternAltitude(2000),
            TrafficDirection = direction,
        };
        follower.Phases.Add(phase);

        PhaseContext ctx = Ctx(follower, returnRunway, _ => null);
        follower.Phases.Start(ctx);

        Assert.True(phase.OnTick(ctx));
        Assert.IsNotType<VfrFollowPhase>(follower.Phases.CurrentPhase);
        return follower;
    }

    /// <summary>
    /// Ends the follower's circuit with a low approach and ticks it to completion, so the auto-cycle builds the next
    /// circuit. Returns that circuit's downwind.
    /// </summary>
    private static DownwindPhase FlyLowApproachIntoNextCircuit(AircraftState aircraft)
    {
        aircraft.Approach.FollowingCallsign = null;
        PhaseList phases = aircraft.Phases!;
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty);
        phases.Clear(ctx);
        var lowApproach = new LowApproachPhase();
        phases.Add(lowApproach);

        // At the low-pass floor, then climbed clear of the field: the low approach completes on the second tick.
        aircraft.Altitude = phases.AssignedRunway!.ElevationFt;
        PhaseRunner.Tick(aircraft, ctx);
        aircraft.Altitude = 3000;
        PhaseRunner.Tick(aircraft, ctx);

        Assert.Equal(PhaseStatus.Completed, lowApproach.Status);
        return phases.Phases.OfType<DownwindPhase>().Last();
    }

    /// <summary>
    /// Places the follower on <paramref name="leadRunway"/>'s downwind abeam point (inside every join
    /// gate) behind a lead flying that runway's downwind, with the follower's outgoing phase list
    /// flying <paramref name="flownRunway"/> and <paramref name="armedRunway"/> armed, with
    /// <paramref name="transitionAltitude"/> deferred to it. Returns the follower after the join tick.
    /// </summary>
    private static AircraftState JoinLeadPattern(
        RunwayInfo leadRunway,
        RunwayInfo flownRunway,
        RunwayInfo armedRunway,
        PatternDirection direction,
        DeferredPatternAltitude? transitionAltitude
    ) => Join(ArrangeLeadPattern(leadRunway, flownRunway, armedRunway, direction, transitionAltitude));

    /// <summary>A follower set up behind a lead's downwind by <see cref="ArrangeLeadPattern"/>, not yet ticked.</summary>
    private sealed record LeadPatternJoin(AircraftState Follower, VfrFollowPhase Phase, PhaseContext Context, PatternWaypoints LeadWaypoints);

    /// <summary>Starts <paramref name="join"/>'s follow and ticks it once; the follower must have joined the lead's pattern.</summary>
    private static AircraftState Join(LeadPatternJoin join)
    {
        join.Follower.Phases!.Start(join.Context);
        join.Phase.OnTick(join.Context);
        Assert.False(
            join.Follower.Phases.CurrentPhase is VfrFollowPhase,
            "the follower never joined the lead's pattern — the join gates were not satisfied"
        );
        return join.Follower;
    }

    /// <summary>The set-up of <see cref="JoinLeadPattern"/>, returned before the join tick so a test can adjust the follower.</summary>
    private static LeadPatternJoin ArrangeLeadPattern(
        RunwayInfo leadRunway,
        RunwayInfo flownRunway,
        RunwayInfo armedRunway,
        PatternDirection direction,
        DeferredPatternAltitude? transitionAltitude
    )
    {
        IReadOnlyList<RunwayInfo> airportRunways = NavigationDatabase.Instance.GetRunways(leadRunway.AirportId);
        PatternWaypoints waypoints = PatternGeometry.Compute(
            leadRunway,
            AircraftCategory.Piston,
            "C172",
            windSpeedKt: 0,
            direction,
            sizeOverrideNm: null,
            altitudeOverrideFt: null,
            airportRunways,
            authoredRunway: null,
            activeRunways: ActiveRunways.Empty,
            commanded: PatternCommanded.None
        );

        var abeam = new LatLon(waypoints.DownwindAbeamLat, waypoints.DownwindAbeamLon);
        AircraftState lead = MakeVfr(
            Lead,
            GeoMath.ProjectPoint(abeam, waypoints.DownwindHeading, 0.5),
            waypoints.DownwindHeading,
            waypoints.PatternAltitude
        );
        lead.Phases = new PhaseList
        {
            AssignedRunway = leadRunway,
            PatternRunway = leadRunway,
            TrafficDirection = direction,
        };
        lead.Phases.Add(new DownwindPhase { Waypoints = waypoints });

        AircraftState follower = MakeVfr(Follower, abeam, waypoints.DownwindHeading, waypoints.PatternAltitude);
        follower.Approach.FollowingCallsign = Lead;
        follower.Pattern.TrafficDirection = direction;
        var phase = new VfrFollowPhase(Lead, patternReturn: null);
        follower.Phases = new PhaseList
        {
            AssignedRunway = flownRunway,
            PatternRunway = armedRunway,
            TransitionPatternAltitude = transitionAltitude,
            TrafficDirection = direction,
        };
        follower.Phases.Add(phase);

        PhaseContext ctx = Ctx(follower, leadRunway, cs => cs == Lead ? lead : null);
        return new LeadPatternJoin(follower, phase, ctx, waypoints);
    }

    private static AircraftState MakeVfr(string callsign, LatLon pos, TrueHeading heading, double altitude) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = pos,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = altitude,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK", FlightRules = "VFR" },
            Approach = new AircraftApproachState { HasReportedTrafficInSight = true },
        };

    private static PhaseContext Ctx(AircraftState ac, RunwayInfo rwy, Func<string, AircraftState?> lookup) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            AircraftLookup = lookup,
            Logger = NullLogger.Instance,
        };

    /// <summary>Point <paramref name="alongNm"/> out the final approach course, <paramref name="crossNm"/> laterally
    /// (positive toward the runway heading's right-hand side).</summary>
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

        TrueHeading perp = crossNm > 0 ? rwy.TrueHeading + 90.0 : rwy.TrueHeading - 90.0;
        return GeoMath.ProjectPoint(onCenterline, perp, Math.Abs(crossNm));
    }
}
