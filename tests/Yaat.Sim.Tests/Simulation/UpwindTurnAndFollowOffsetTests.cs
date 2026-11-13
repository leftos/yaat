using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The upwind's crosswind turn and the pursuit's departure-leg hold share one rule (AIM 4-3-3, FIG 4-3-2 key 5): past the
/// departure end, at or above pattern altitude less the turn margin or, when the controller cleared the aircraft lower
/// (AIM 4-3-2.b), at that cleared altitude. A pursuit started on an upwind flying an OFL/OFR offset keeps flying that offset
/// while it holds the departure leg. Real KOAK 28R right traffic, C172.
/// </summary>
public class UpwindTurnAndFollowOffsetTests(ITestOutputHelper output)
{
    private const string Follower = "FOLL1";

    /// <summary>Ticks (4 a second) an upwind test waits for the crosswind turn before failing as hung: 150 s of sim time.</summary>
    private const int MaxTicks = 600;

    /// <summary>Feet above the turn altitude the aircraft may be at on the tick it turns, one climbing tick's worth and more.</summary>
    private const double TurnAltitudeToleranceFt = 30.0;

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).EnableCategory("CommandDispatcher", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
    }

    private static RunwayInfo Oak28R() =>
        NavigationDatabase.Instance.GetRunway("KOAK", "28R") ?? throw new InvalidOperationException("KOAK 28R missing from navdata");

    private static AircraftState MakeAircraft(string callsign, LatLon position, TrueHeading heading, double altitude) =>
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
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(2000),
                CruiseSpeed = 110,
            },
        };

    private static List<Phase> RightCircuitFromUpwind(RunwayInfo rwy) =>
        PatternBuilder.BuildCircuit(
            new PatternCircuitRequest
            {
                Runway = rwy,
                Category = AircraftCategorization.Categorize("C172"),
                AircraftType = "C172",
                WindSpeedKt = 0,
                Direction = PatternDirection.Right,
                EntryLeg = PatternEntryLeg.Upwind,
                TouchAndGo = true,
                FinalDistanceNm = null,
                PatternSizeNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = NavigationDatabase.Instance.GetRunways(rwy.AirportId),
                AuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );

    /// <summary>
    /// A C172 on the 28R right circuit's upwind, climbing through 600 ft above the field, <paramref name="shortNm"/> short of
    /// the crosswind-turn point along the upwind heading (past it when negative).
    /// </summary>
    private static AircraftState AddOnUpwind(SimulationEngine engine, double shortNm)
    {
        RunwayInfo rwy = Oak28R();
        AircraftState ac = MakeAircraft(
            Follower,
            new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            rwy.TrueHeading,
            rwy.AirportElevationFt + 600
        );
        engine.World.AddAircraft(ac);
        ac.Phases = new PhaseList { AssignedRunway = rwy, TrafficDirection = PatternDirection.Right };
        foreach (Phase phase in RightCircuitFromUpwind(rwy))
        {
            ac.Phases.Add(phase);
        }

        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        PatternWaypoints wp = Waypoints(ac);
        ac.Position = GeoMath.ProjectPoint(CrosswindTurnPoint(wp), wp.UpwindHeading.ToReciprocal(), shortNm);
        ac.TrueHeading = wp.UpwindHeading;
        ac.TrueTrack = wp.UpwindHeading;
        return ac;
    }

    private static PatternWaypoints Waypoints(AircraftState ac) =>
        Assert.IsType<UpwindPhase>(ac.Phases?.CurrentPhase).Waypoints ?? throw new InvalidOperationException("the upwind carries no waypoints");

    private static LatLon CrosswindTurnPoint(PatternWaypoints wp) => new(wp.CrosswindTurnLat, wp.CrosswindTurnLon);

    private static double MinTurnAltitude(PatternWaypoints wp) => wp.PatternAltitude - UpwindPhase.PatternHandoffMarginFt;

    private CommandResult Send(SimulationEngine engine, string callsign, string command)
    {
        CommandResult result = engine.SendCommand(callsign, command);
        output.WriteLine($"{callsign} {command}: success={result.Success} — {result.Message}");
        return result;
    }

    /// <summary>What the upwind left for, where and at what altitude.</summary>
    private sealed record UpwindEnd(Phase? Next, bool PastDepartureEnd, double Altitude);

    /// <summary>Ticks until the aircraft leaves <see cref="UpwindPhase"/>, failing as hung after <see cref="MaxTicks"/>.</summary>
    private UpwindEnd TickUntilUpwindEnds(SimulationEngine engine, AircraftState ac, PatternWaypoints wp)
    {
        for (int tick = 1; tick <= MaxTicks; tick++)
        {
            engine.TickPhysics(0.25);
            if (ac.Phases?.CurrentPhase is not UpwindPhase)
            {
                bool past = UpwindPhase.PastDepartureEnd(ac.Position, CrosswindTurnPoint(wp), wp.UpwindHeading);
                output.WriteLine($"left the upwind after {tick} ticks for {ac.Phases?.CurrentPhase?.Name} at {ac.Altitude:F0} ft, past={past}");
                return new UpwindEnd(ac.Phases?.CurrentPhase, past, ac.Altitude);
            }
        }

        throw new InvalidOperationException($"hung on {ac.Phases?.CurrentPhase?.Name} at {ac.Altitude:F0} ft after {MaxTicks} ticks");
    }

    // ─── The upwind's crosswind turn ───

    /// <summary>
    /// A DM below pattern altitude less the margin is the aircraft's clearance: past the departure end at that altitude the upwind
    /// turns crosswind, rather than holding the leg for an altitude it was told not to climb to.
    /// </summary>
    [Fact]
    public void Upwind_DescendedBelowTurnAltitude_TurnsCrosswindPastDepartureEndAtTheAssignedAltitude()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = AddOnUpwind(engine, shortNm: 1.0);
        PatternWaypoints wp = Waypoints(ac);
        const double assigned = 500.0;
        Assert.True(assigned < MinTurnAltitude(wp), $"the assigned altitude must sit below the turn altitude {MinTurnAltitude(wp):F0}");

        Assert.True(Send(engine, Follower, "DM 5").Success);
        UpwindEnd end = TickUntilUpwindEnds(engine, ac, wp);

        Assert.IsType<CrosswindPhase>(end.Next);
        Assert.True(end.PastDepartureEnd, "the crosswind turn must come past the departure end");
        Assert.InRange(end.Altitude, assigned - FlightPhysics.AltitudeSnapFt, assigned + FlightPhysics.AltitudeSnapFt);
    }

    [Fact]
    public void Upwind_NoAltitudeCommand_TurnsAtPatternAltitudeLessMargin()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = AddOnUpwind(engine, shortNm: 0.1);
        PatternWaypoints wp = Waypoints(ac);

        UpwindEnd end = TickUntilUpwindEnds(engine, ac, wp);

        Assert.IsType<CrosswindPhase>(end.Next);
        Assert.True(end.PastDepartureEnd, "the crosswind turn must come past the departure end");
        Assert.InRange(end.Altitude, MinTurnAltitude(wp), MinTurnAltitude(wp) + TurnAltitudeToleranceFt);
    }

    [Fact]
    public void Upwind_ClimbedAboveTurnAltitude_KeepsTheStandardTurn()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState ac = AddOnUpwind(engine, shortNm: 0.1);
        PatternWaypoints wp = Waypoints(ac);

        Assert.True(Send(engine, Follower, "CM 2500").Success);
        UpwindEnd end = TickUntilUpwindEnds(engine, ac, wp);

        Assert.IsType<CrosswindPhase>(end.Next);
        Assert.True(end.PastDepartureEnd, "the crosswind turn must come past the departure end");
        Assert.InRange(end.Altitude, MinTurnAltitude(wp), MinTurnAltitude(wp) + TurnAltitudeToleranceFt);
    }

    /// <summary>
    /// An upwind past the departure end at <paramref name="altitude"/>, cleared to 500 ft MSL (below pattern altitude less the
    /// margin) by <paramref name="command"/>.
    /// </summary>
    private (AircraftState Ac, UpwindPhase Upwind) PastDepartureEndCleared(SimulationEngine engine, double altitude, string command)
    {
        AircraftState ac = AddOnUpwind(engine, shortNm: -0.2);
        PatternWaypoints wp = Waypoints(ac);
        UpwindPhase upwind = Assert.IsType<UpwindPhase>(ac.Phases!.CurrentPhase);
        ac.Altitude = altitude;
        Assert.True(Send(engine, Follower, command).Success);
        Assert.Equal(500.0, ac.Targets.TargetAltitude);
        Assert.True(ac.Altitude < MinTurnAltitude(wp), "the aircraft must be below the turn altitude");
        Assert.True(UpwindPhase.PastDepartureEnd(ac.Position, CrosswindTurnPoint(wp), wp.UpwindHeading));
        return (ac, upwind);
    }

    private static bool TickOnce(AircraftState ac, UpwindPhase upwind) =>
        upwind.OnTick(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));

    /// <summary>
    /// Past the departure end, still climbing to a lower assigned altitude and not yet within capture of it: no turn; once
    /// within <see cref="FlightPhysics.AltitudeSnapFt"/> of it, the turn.
    /// </summary>
    [Fact]
    public void Upwind_AssignedLowerButStillClimbingToIt_DoesNotTurnEarly()
    {
        SimulationEngine engine = BuildEngine();
        (AircraftState ac, UpwindPhase upwind) = PastDepartureEndCleared(engine, 450.0, "CM 5");

        Assert.False(TickOnce(ac, upwind), "the upwind must hold 50 ft below the assigned 500 ft");

        ac.Altitude = 500.0 - (FlightPhysics.AltitudeSnapFt / 2.0);
        Assert.True(TickOnce(ac, upwind), $"the upwind must turn within capture of the assigned 500 ft, at {ac.Altitude:F0} ft");
    }

    /// <summary>Above a lower assigned altitude it is already higher than its clearance, so past the departure end it turns.</summary>
    [Fact]
    public void Upwind_AboveTheLowerAssignedTarget_TurnsPastTheDepartureEnd()
    {
        SimulationEngine engine = BuildEngine();
        (AircraftState ac, UpwindPhase upwind) = PastDepartureEndCleared(engine, 600.0, "DM 5");

        Assert.True(TickOnce(ac, upwind), "the upwind must turn above the assigned 500 ft");
    }

    // ─── The pursuit's departure-leg hold keeps the upwind's lateral offset ───

    private const string Leader = "LEAD1";

    /// <summary>Distance (nm) the follower may stray from its offset track while the departure-leg hold applies.</summary>
    private const double OffsetToleranceNm = 0.1;

    /// <summary>
    /// A runwayless lead on the follower's circuit's downwind, a mile short of the base turn: FOLLOW from the upwind installs a
    /// pursuit that holds the departure leg.
    /// </summary>
    private static AircraftState AddLeadOnDownwind(SimulationEngine engine, PatternWaypoints wp)
    {
        LatLon position = GeoMath.ProjectPoint(new LatLon(wp.BaseTurnLat, wp.BaseTurnLon), wp.DownwindHeading.ToReciprocal(), 1.0);
        AircraftState lead = MakeAircraft(Leader, position, wp.DownwindHeading, 1500);
        engine.World.AddAircraft(lead);
        return lead;
    }

    /// <summary>FOLLOW the lead and return the pursuit, asserting it holds the departure leg.</summary>
    private VfrFollowPhase FollowLead(SimulationEngine engine, AircraftState follower)
    {
        follower.Approach.HasReportedTrafficInSight = true;
        Assert.True(Send(engine, Follower, $"FOLLOW {Leader}").Success);
        VfrFollowPhase pursuit = Assert.IsType<VfrFollowPhase>(follower.Phases?.CurrentPhase);
        Assert.NotNull(pursuit.ClimbOutGate);
        return pursuit;
    }

    /// <summary>Signed cross-track (nm, positive right) of <paramref name="ac"/> from the upwind centreline through the departure end.</summary>
    private static double CrossTrackNm(AircraftState ac, PatternWaypoints wp) =>
        GeoMath.SignedCrossTrackDistanceNm(ac.Position.Lat, ac.Position.Lon, wp.DepartureEndLat, wp.DepartureEndLon, wp.UpwindHeading);

    /// <summary>
    /// An upwind with an acquired OFR 0.5, followed from there: until the gate clears the pursuit stays on the offset track, and
    /// while it strays from it the heading target is never the bare upwind heading. A crosswind from the right pushes the
    /// follower back toward the centreline, so only a hold that keeps flying the offset stays on it.
    /// </summary>
    [Fact]
    public void FollowOnUpwindWithOffset_HoldsTheOffsetTrackUntilTheTurn()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddOnUpwind(engine, shortNm: 2.5);
        PatternWaypoints wp = Waypoints(follower);
        UpwindPhase upwind = Assert.IsType<UpwindPhase>(follower.Phases!.CurrentPhase);
        engine.World.Weather = new WeatherProfile
        {
            WindLayers = [new WindLayer { Direction = (wp.UpwindHeading.Degrees + 90.0) % 360.0, Speed = 12 }],
        };
        Assert.True(Send(engine, Follower, "OFR 0.5").Success);

        int acquireTicks = 0;
        while ((upwind.LateralOffset is not { Acquired: true }) && (acquireTicks < MaxTicks))
        {
            engine.TickPhysics(0.25);
            acquireTicks++;
        }

        Assert.Same(upwind, follower.Phases.CurrentPhase);
        Assert.True(upwind.LateralOffset is { Acquired: true }, $"the offset must be acquired on the upwind, at {CrossTrackNm(follower, wp):F2} nm");

        AddLeadOnDownwind(engine, wp);
        VfrFollowPhase pursuit = FollowLead(engine, follower);
        UpwindLateralOffset offset = pursuit.ClimbOutGate?.LateralOffset ?? throw new InvalidOperationException("the gate must carry the offset");
        Assert.NotSame(upwind.LateralOffset, offset.State);

        HoldOffsetTrackUntilGateClears(engine, follower, wp, pursuit, offset.State);
    }

    /// <summary>
    /// FOLLOW issued right after OFR, before the offset is acquired: the hold carries the unacquired offset, acquires it itself,
    /// and from then on holds the offset track until the gate clears.
    /// </summary>
    [Fact]
    public void FollowRightAfterOfr_HoldAcquiresTheOffsetItself()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddOnUpwind(engine, shortNm: 2.5);
        PatternWaypoints wp = Waypoints(follower);
        Assert.True(Send(engine, Follower, "OFR 0.5").Success);

        AddLeadOnDownwind(engine, wp);
        VfrFollowPhase pursuit = FollowLead(engine, follower);
        UpwindLateralOffset offset = pursuit.ClimbOutGate?.LateralOffset ?? throw new InvalidOperationException("the gate must carry the offset");
        Assert.False(offset.State.Acquired, "the offset must not be acquired yet when FOLLOW is issued");

        HoldOffsetTrackUntilGateClears(engine, follower, wp, pursuit, offset.State);

        Assert.True(offset.State.Acquired, "the hold must acquire the offset before the gate clears");
    }

    /// <summary>
    /// Ticks the pursuit through its departure-leg hold: while it strays from the 0.5 nm right offset track the heading target is
    /// never the bare upwind heading, and once <paramref name="state"/> is acquired the track stays within
    /// <see cref="OffsetToleranceNm"/> of it until the gate clears.
    /// </summary>
    private void HoldOffsetTrackUntilGateClears(
        SimulationEngine engine,
        AircraftState follower,
        PatternWaypoints wp,
        VfrFollowPhase pursuit,
        PatternLateralOffsetState state
    )
    {
        int holdTicks = 0;
        while ((pursuit.ClimbOutGate is not null) && (holdTicks < MaxTicks))
        {
            engine.TickPhysics(0.25);
            holdTicks++;
            if (pursuit.ClimbOutGate is null)
            {
                break;
            }

            double offTrackNm = Math.Abs(CrossTrackNm(follower, wp) - 0.5);
            Assert.False(
                (offTrackNm > OffsetToleranceNm) && (follower.Targets.TargetTrueHeading == wp.UpwindHeading),
                $"the hold flies the bare upwind heading {offTrackNm:F2} nm off the offset track after {holdTicks} ticks"
            );
            Assert.True(
                !state.Acquired || (offTrackNm <= OffsetToleranceNm),
                $"the hold strayed {offTrackNm:F2} nm off the offset track after {holdTicks} ticks"
            );
        }

        output.WriteLine($"held for {holdTicks} ticks, cross-track {CrossTrackNm(follower, wp):F2} nm");
        Assert.True(holdTicks > 0, "the hold must last at least one tick");
        Assert.Null(pursuit.ClimbOutGate);
    }

    /// <summary>
    /// A crossing-runway transition's upwind (off 28R, right traffic for 33) flying OFR: the gate its pursuit owes
    /// (<see cref="VfrFollowPhase.ClimbOutGateFor"/>, through the crossing branch) carries the offset too.
    /// </summary>
    [Fact]
    public void FollowOnCrossingTransitionUpwindWithOffset_CarriesTheOffset()
    {
        SimulationEngine engine = BuildEngine();
        RunwayInfo flown = Oak28R();
        RunwayInfo rwy33 = NavigationDatabase.Instance.GetRunway("KOAK", "33") ?? throw new InvalidOperationException("KOAK 33 missing");
        AircraftState ac = MakeAircraft(Follower, new LatLon(flown.ThresholdLatitude, flown.ThresholdLongitude), flown.TrueHeading, 600);
        engine.World.AddAircraft(ac);
        ac.Phases = new PhaseList
        {
            AssignedRunway = rwy33,
            PatternRunway = rwy33,
            DepartureRunway = flown,
            TrafficDirection = PatternDirection.Right,
        };
        foreach (Phase phase in CrossingTransitionCircuit(flown, rwy33))
        {
            ac.Phases.Add(phase);
        }

        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        Assert.IsType<UpwindPhase>(ac.Phases.CurrentPhase);
        Assert.True(AirborneFollowHelper.IsCrossingTransitionClimb(ac), "the upwind must be a crossing transition's");
        Assert.True(Send(engine, Follower, "OFR 0.5").Success);

        FollowClimbOutGate gate = VfrFollowPhase.ClimbOutGateFor(ac, groundLayout: null) ?? throw new InvalidOperationException("no gate");

        UpwindLateralOffset offset = gate.LateralOffset ?? throw new InvalidOperationException("the crossing gate must carry the offset");
        Assert.Equal(0.5, offset.State.TargetNm);
        Assert.Equal(TurnDirection.Right, offset.State.Direction);
    }

    private static List<Phase> CrossingTransitionCircuit(RunwayInfo flown, RunwayInfo patternRunway) =>
        PatternBuilder.BuildRunwayTransitionCircuit(
            new RunwayTransitionCircuitRequest
            {
                FlownRunway = flown,
                PatternRunway = patternRunway,
                Category = AircraftCategorization.Categorize("C172"),
                AircraftType = "C172",
                WindSpeedKt = 0,
                Direction = PatternDirection.Right,
                TouchAndGo = true,
                PatternSizeNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = NavigationDatabase.Instance.GetRunways("KOAK"),
                FlownAuthoredRunway = null,
                PatternAuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );

    [Fact]
    public void FollowOnUpwindWithoutOffset_HoldsTheBareUpwindHeading()
    {
        SimulationEngine engine = BuildEngine();
        AircraftState follower = AddOnUpwind(engine, shortNm: 1.0);
        PatternWaypoints wp = Waypoints(follower);
        AddLeadOnDownwind(engine, wp);
        VfrFollowPhase pursuit = FollowLead(engine, follower);
        Assert.Null(pursuit.ClimbOutGate?.LateralOffset);

        int holdTicks = 0;
        while ((pursuit.ClimbOutGate is not null) && (holdTicks < MaxTicks))
        {
            engine.TickPhysics(0.25);
            holdTicks++;
            if (pursuit.ClimbOutGate is not null)
            {
                Assert.Equal(wp.UpwindHeading, follower.Targets.TargetTrueHeading);
            }
        }

        Assert.True(holdTicks > 0, "the hold must last at least one tick");
        Assert.Null(pursuit.ClimbOutGate);
    }

    [Fact]
    public void FollowClimbOutGate_WithOffset_RoundTripsThroughTheSnapshot()
    {
        TestVnasData.EnsureInitialized();
        var reference = new LatLon(37.72, -122.22);
        var offset = new UpwindLateralOffset(
            reference,
            new PatternLateralOffsetState
            {
                TargetNm = 0.7,
                Direction = TurnDirection.Left,
                Acquired = true,
            }
        );
        var gate = new FollowClimbOutGate(new LatLon(37.73, -122.23), new TrueHeading(292.0), 709.0, null, offset);

        VfrFollowPhase restored = RoundTrip(new VfrFollowPhase(Leader, null) { ClimbOutGate = gate }, out _);

        UpwindLateralOffset restoredOffset = restored.ClimbOutGate?.LateralOffset ?? throw new InvalidOperationException("the offset must survive");
        Assert.Equal(reference.Lat, restoredOffset.Reference.Lat, 9);
        Assert.Equal(reference.Lon, restoredOffset.Reference.Lon, 9);
        Assert.Equal(0.7, restoredOffset.State.TargetNm);
        Assert.Equal(TurnDirection.Left, restoredOffset.State.Direction);
        Assert.True(restoredOffset.State.Acquired);

        VfrFollowPhase bare = RoundTrip(new VfrFollowPhase(Leader, null) { ClimbOutGate = gate with { LateralOffset = null } }, out string bareJson);

        Assert.NotNull(bare.ClimbOutGate);
        Assert.Null(bare.ClimbOutGate.LateralOffset);
        Assert.DoesNotContain("LateralOffsetTargetNm", bareJson, StringComparison.Ordinal);
    }

    /// <summary>A snapshot that carries the offset's distance but not its reference point restores no offset.</summary>
    [Fact]
    public void FollowClimbOutGate_OffsetWithMissingReference_RestoresNoOffset()
    {
        TestVnasData.EnsureInitialized();
        var offset = new UpwindLateralOffset(
            new LatLon(37.72, -122.22),
            new PatternLateralOffsetState
            {
                TargetNm = 0.5,
                Direction = TurnDirection.Right,
                Acquired = true,
            }
        );
        var phase = new VfrFollowPhase(Leader, null)
        {
            ClimbOutGate = new FollowClimbOutGate(new LatLon(37.73, -122.23), new TrueHeading(292.0), 709.0, null, offset),
        };
        JsonNode json = JsonSerializer.SerializeToNode(Assert.IsType<VfrFollowPhaseDto>(phase.ToSnapshot()), RecordingJsonOptions.Default)!;
        JsonObject gateJson = json[nameof(VfrFollowPhaseDto.ClimbOutGate)]!.AsObject();
        Assert.True(gateJson.Remove(nameof(FollowClimbOutGateDto.LateralOffsetReferenceLat)), "the gate snapshot must carry the reference");
        VfrFollowPhaseDto dto = json.Deserialize<VfrFollowPhaseDto>(RecordingJsonOptions.Default) ?? throw new InvalidOperationException("no dto");

        var restored = VfrFollowPhase.FromSnapshot(dto);

        Assert.NotNull(restored.ClimbOutGate);
        Assert.Null(restored.ClimbOutGate.LateralOffset);
    }

    private static VfrFollowPhase RoundTrip(VfrFollowPhase phase, out string json)
    {
        VfrFollowPhaseDto dto = Assert.IsType<VfrFollowPhaseDto>(phase.ToSnapshot());
        json = JsonSerializer.Serialize(dto, RecordingJsonOptions.Default);
        VfrFollowPhaseDto read =
            JsonSerializer.Deserialize<VfrFollowPhaseDto>(json, RecordingJsonOptions.Default)
            ?? throw new InvalidOperationException("the snapshot must deserialize");
        return VfrFollowPhase.FromSnapshot(read);
    }
}
