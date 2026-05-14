using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

public class BehindGroundTaxiTests
{
    private static readonly ILogger Logger = NullLoggerFactory.Instance.CreateLogger("test");

    private static AircraftState MakeGroundAircraft(string callsign, double lat, double lon, double headingDeg)
    {
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(headingDeg),
            Altitude = 9,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
        };
    }

    private static void StartPhase(AircraftState ac, Phase phase)
    {
        ac.Phases = new PhaseList();
        ac.Phases.Add(phase);
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            DeltaSeconds = 1.0,
            Logger = Logger,
            Category = AircraftCategory.Piston,
        };
        ac.Phases.Start(ctx);
    }

    // -------------------------------------------------------------------------
    // Fix #2: TryDeferGiveWay rejects when target callsign cannot be resolved
    // -------------------------------------------------------------------------

    [Fact]
    public void BehindTaxi_TargetMissing_RejectsAtDispatch()
    {
        var ac = MakeGroundAircraft("N569SX", 37.7272, -122.2097, 335.0);
        StartPhase(ac, new HoldingAfterPushbackPhase());

        Func<string, AircraftState?> findAircraft = _ => null;

        var compound = new CompoundCommand([new ParsedBlock(new GiveWayCondition("GHOST"), [new TaxiCommand(["A", "B"], [])])]);

        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42), findAircraft: findAircraft));

        Assert.False(result.Success, "Expected BEHIND with unknown target to be rejected");
        Assert.Contains("GHOST", result.Message ?? string.Empty);
        Assert.Empty(ac.DeferredDispatches);
        Assert.Null(ac.Ground.Hold);
    }

    [Fact]
    public void BehindTaxi_TargetExists_CreatesDeferredDispatch()
    {
        var ac = MakeGroundAircraft("N569SX", 37.7272, -122.2097, 335.0);
        var target = MakeGroundAircraft("N152SP", 37.7281, -122.2117, 112.0);
        StartPhase(ac, new HoldingAfterPushbackPhase());

        Func<string, AircraftState?> findAircraft = cs => cs == "N152SP" ? target : null;

        var compound = new CompoundCommand([new ParsedBlock(new GiveWayCondition("N152SP"), [new TaxiCommand(["C", "D"], [])])]);

        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(new SerializableRandom(42), findAircraft: findAircraft));

        Assert.True(result.Success, $"Expected success but got: {result.Message}");
        Assert.Single(ac.DeferredDispatches);
        Assert.Equal("N152SP", ac.DeferredDispatches[0].GiveWayTarget);
    }

    // -------------------------------------------------------------------------
    // Fix #1: IsGiveWayMet ground geometry — no 0.1 nm short-circuit
    // -------------------------------------------------------------------------

    [Fact]
    public void IsGiveWayMet_OppositeDirection_TargetAheadAt03Nm_ReturnsFalse()
    {
        // Held aircraft B heading east (90°), parked at (0, 0)
        var b = MakeGroundAircraft("B", 0.0, 0.0, 90.0);
        // Target A 0.3nm east of B, heading west (270°) — head-on, approaching
        var a = MakeGroundAircraft("A", 0.0, 0.005, 270.0);
        Assert.True(GeoMath.DistanceNm(b.Position, a.Position) > 0.2, "test geometry assumes >0.2 nm gap");

        var trigger = new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = "A" };
        bool met = FlightPhysics.IsGiveWayMet(b, trigger, _ => a);

        Assert.False(met, "Target is approaching head-on at 0.3 nm — held aircraft must wait");
    }

    [Fact]
    public void IsGiveWayMet_OppositeDirection_TargetBehindAndMovingAway_ReturnsTrue()
    {
        // Held aircraft B heading east (90°), parked at (0, 0)
        var b = MakeGroundAircraft("B", 0.0, 0.0, 90.0);
        // Target A 0.3nm WEST of B (behind), heading WEST — has already passed B
        // and is moving further away. Opposite-direction case, conflict resolved.
        var a = MakeGroundAircraft("A", 0.0, -0.005, 270.0);
        Assert.True(GeoMath.DistanceNm(b.Position, a.Position) > 0.2, "test geometry assumes >0.2 nm gap");

        var trigger = new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = "A" };
        bool met = FlightPhysics.IsGiveWayMet(b, trigger, _ => a);

        Assert.True(met, "Target is behind held aircraft moving away in opposite direction — condition should be met");
    }

    [Fact]
    public void IsGiveWayMet_OppositeDirection_TargetCloseAndApproaching_ReturnsFalse()
    {
        // Reproduces the bundle scenario at smaller scale: target ahead AND inside
        // the legacy 0.1 nm shortcut. Heading geometry must hold even there.
        var b = MakeGroundAircraft("B", 0.0, 0.0, 90.0);
        var a = MakeGroundAircraft("A", 0.0, 0.0008, 270.0); // ~0.05 nm east
        Assert.True(GeoMath.DistanceNm(b.Position, a.Position) < 0.1, "test geometry assumes <0.1 nm gap");

        var trigger = new BlockTrigger { Type = BlockTriggerType.GiveWay, TargetCallsign = "A" };
        bool met = FlightPhysics.IsGiveWayMet(b, trigger, _ => a);

        Assert.False(met, "Target is approaching head-on at <0.1 nm — held aircraft must wait");
    }
}
