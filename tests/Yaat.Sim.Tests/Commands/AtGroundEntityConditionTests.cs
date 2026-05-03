using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Tests for the AT ground-entity conditional. Covers parse-level disambiguation,
/// dispatcher-side resolution against a real airport layout, the
/// <see cref="FlightPhysics.NotifyGroundEntityReached"/> phase-bypass path, and
/// snapshot round-trip of the new <see cref="BlockTrigger"/> fields.
/// </summary>
public class AtGroundEntityConditionTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();
    private static readonly TestAirportGroundData GroundData = new();

    public AtGroundEntityConditionTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeGroundAircraft(LatLon position, AirportGroundLayout? layout = null)
    {
        var ac = new AircraftState
        {
            Callsign = "TST01",
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(0),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };
        if (layout is not null)
        {
            ac.Ground.Layout = layout;
        }
        return ac;
    }

    private static AircraftState MakeAirborneAircraft()
    {
        return new AircraftState
        {
            Callsign = "TST02",
            AircraftType = "B738",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(90),
            Altitude = 3000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
        };
    }

    // -------------------------------------------------------------------------
    // Parse: each sigil/form maps to the right BlockCondition
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_BareTaxiway_ProducesTaxiwayCondition()
    {
        var result = CommandParser.ParseCompound("AT A SPD 10");

        Assert.True(result.IsSuccess, result.Reason);
        var cond = Assert.IsType<AtGroundEntityCondition>(result.Value!.Blocks[0].Condition);
        Assert.Equal(GroundEntityKind.Taxiway, cond.Kind);
        Assert.Equal("A", cond.Token);
        Assert.Null(cond.SecondTaxiway);
    }

    [Fact]
    public void Parse_DollarSpot_ProducesSpotCondition()
    {
        var result = CommandParser.ParseCompound("AT $5 SPD 10");

        Assert.True(result.IsSuccess, result.Reason);
        var cond = Assert.IsType<AtGroundEntityCondition>(result.Value!.Blocks[0].Condition);
        Assert.Equal(GroundEntityKind.Spot, cond.Kind);
        Assert.Equal("5", cond.Token);
    }

    [Fact]
    public void Parse_AtSignParking_ProducesParkingCondition()
    {
        var result = CommandParser.ParseCompound("AT @TERM2 SPD 10");

        Assert.True(result.IsSuccess, result.Reason);
        var cond = Assert.IsType<AtGroundEntityCondition>(result.Value!.Blocks[0].Condition);
        Assert.Equal(GroundEntityKind.Parking, cond.Kind);
        Assert.Equal("TERM2", cond.Token);
    }

    [Fact]
    public void Parse_SlashIntersection_ProducesIntersectionCondition()
    {
        var result = CommandParser.ParseCompound("AT A/B SPD 10");

        Assert.True(result.IsSuccess, result.Reason);
        var cond = Assert.IsType<AtGroundEntityCondition>(result.Value!.Blocks[0].Condition);
        Assert.Equal(GroundEntityKind.Intersection, cond.Kind);
        Assert.Equal("A", cond.Token);
        Assert.Equal("B", cond.SecondTaxiway);
    }

    [Fact]
    public void Parse_NumericToken_StaysAltitude_NotSpot()
    {
        // SFO has spots "1".."35". Bare digits must always be altitude — numeric spot
        // names require the $ sigil. This keeps every existing AT <altitude> contract.
        var result = CommandParser.ParseCompound("AT 30 CM 040");

        Assert.True(result.IsSuccess, result.Reason);
        var cond = Assert.IsType<LevelCondition>(result.Value!.Blocks[0].Condition);
        Assert.Equal(3000, cond.Altitude);
    }

    [Fact]
    public void Parse_KnownAirborneFix_StaysAtFixCondition()
    {
        var result = CommandParser.ParseCompound("AT SUNOL FH 090");

        Assert.True(result.IsSuccess, result.Reason);
        Assert.IsType<AtFixCondition>(result.Value!.Blocks[0].Condition);
    }

    [Fact]
    public void Parse_EmptySigil_Fails()
    {
        var spotResult = CommandParser.ParseCompound("AT $ SPD 10");
        Assert.False(spotResult.IsSuccess);

        var parkResult = CommandParser.ParseCompound("AT @ SPD 10");
        Assert.False(parkResult.IsSuccess);
    }

    [Fact]
    public void Parse_SameTaxiwayIntersection_Fails()
    {
        var result = CommandParser.ParseCompound("AT A/A SPD 10");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Parse_MalformedIntersection_Fails()
    {
        // Only two parts allowed; A/B/C is rejected.
        var threePart = CommandParser.ParseCompound("AT A/B/C SPD 10");
        Assert.False(threePart.IsSuccess);

        var trailingSlash = CommandParser.ParseCompound("AT A/ SPD 10");
        Assert.False(trailingSlash.IsSuccess);
    }

    [Fact]
    public void Parse_BareTaxiway_NoFollowupAllowed()
    {
        // Mirrors AT BRIXX with no follow-up — bare condition is permitted.
        var result = CommandParser.ParseCompound("AT A");

        Assert.True(result.IsSuccess, result.Reason);
        var cond = Assert.IsType<AtGroundEntityCondition>(result.Value!.Blocks[0].Condition);
        Assert.Equal(GroundEntityKind.Taxiway, cond.Kind);
    }

    // -------------------------------------------------------------------------
    // Dispatcher: ConvertCondition resolves tokens against aircraft.Ground.Layout
    // -------------------------------------------------------------------------

    [Fact]
    public void Dispatch_TaxiwayResolvesToTrigger_OnOak()
    {
        var layout = GroundData.GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var ac = MakeGroundAircraft(new LatLon(37.728, -122.218), layout);
        var compound = CommandParser.ParseCompound("AT B SPD 10");
        Assert.True(compound.IsSuccess);

        CommandDispatcher.DispatchCompound(compound.Value!, ac, TestDispatch.Context(Random.Shared, groundLayout: layout));

        Assert.Single(ac.Queue.Blocks);
        var trigger = ac.Queue.Blocks[0].Trigger!;
        Assert.Equal(BlockTriggerType.AtGroundEntity, trigger.Type);
        Assert.Equal(GroundEntityKind.Taxiway, trigger.GroundKind);
        Assert.Equal("B", trigger.GroundTaxiwayName);
        Assert.Null(trigger.GroundNodeId);
    }

    [Fact]
    public void Dispatch_UnknownTaxiway_RejectsBlock()
    {
        var layout = GroundData.GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var ac = MakeGroundAircraft(new LatLon(37.728, -122.218), layout);
        var compound = CommandParser.ParseCompound("AT NOSUCH SPD 10");
        Assert.True(compound.IsSuccess);

        CommandDispatcher.DispatchCompound(compound.Value!, ac, TestDispatch.Context(Random.Shared, groundLayout: layout));

        // Block with unresolved ground entity is dropped; warning is recorded.
        Assert.Empty(ac.Queue.Blocks);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("AT ground entity not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dispatch_OakCollision_TaxiwayVsSpot_DistinguishedBySigil()
    {
        // OAK has both taxiway "C" and spot "C" — `AT C` must resolve to taxiway,
        // `AT $C` to spot.
        var layout = GroundData.GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var spotC = layout.FindSpotNodeByName("C");
        var taxiwayCnodes = layout.GetNodesOnTaxiway("C");
        if (spotC is null || taxiwayCnodes.Count == 0)
        {
            // Layout doesn't expose this collision in the current GeoJSON snapshot —
            // skip (the parse-side disambiguation is independently covered above).
            return;
        }

        var bareAc = MakeGroundAircraft(new LatLon(37.728, -122.218), layout);
        var bareCompound = CommandParser.ParseCompound("AT C SPD 10");
        Assert.True(bareCompound.IsSuccess);
        CommandDispatcher.DispatchCompound(bareCompound.Value!, bareAc, TestDispatch.Context(Random.Shared, groundLayout: layout));
        var bareTrigger = bareAc.Queue.Blocks[0].Trigger!;
        Assert.Equal(GroundEntityKind.Taxiway, bareTrigger.GroundKind);
        Assert.Equal("C", bareTrigger.GroundTaxiwayName);

        var spotAc = MakeGroundAircraft(new LatLon(37.728, -122.218), layout);
        var spotCompound = CommandParser.ParseCompound("AT $C SPD 10");
        Assert.True(spotCompound.IsSuccess);
        CommandDispatcher.DispatchCompound(spotCompound.Value!, spotAc, TestDispatch.Context(Random.Shared, groundLayout: layout));
        var spotTrigger = spotAc.Queue.Blocks[0].Trigger!;
        Assert.Equal(GroundEntityKind.Spot, spotTrigger.GroundKind);
        Assert.Equal(spotC.Id, spotTrigger.GroundNodeId);
    }

    [Fact]
    public void Dispatch_Intersection_ResolvesToSharedNode_OnOak()
    {
        // OAK B and C share intersection nodes (per AirportE2ETests connectivity notes).
        var layout = GroundData.GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var ac = MakeGroundAircraft(new LatLon(37.728, -122.218), layout);
        var compound = CommandParser.ParseCompound("AT B/C SPD 10");
        Assert.True(compound.IsSuccess);

        CommandDispatcher.DispatchCompound(compound.Value!, ac, TestDispatch.Context(Random.Shared, groundLayout: layout));

        Assert.Single(ac.Queue.Blocks);
        var trigger = ac.Queue.Blocks[0].Trigger!;
        Assert.Equal(BlockTriggerType.AtGroundEntity, trigger.Type);
        Assert.Equal(GroundEntityKind.Intersection, trigger.GroundKind);
        Assert.NotNull(trigger.GroundNodeId);

        // Verify the resolved node really is on both taxiways.
        var node = layout.Nodes[trigger.GroundNodeId!.Value];
        Assert.Contains(node.Edges, e => e.MatchesTaxiway("B"));
        Assert.Contains(node.Edges, e => e.MatchesTaxiway("C"));
    }

    // -------------------------------------------------------------------------
    // Runtime: NotifyGroundEntityReached fires queued blocks
    // -------------------------------------------------------------------------

    [Fact]
    public void Notify_TaxiwayMatch_FiresQueuedBlock()
    {
        var layout = GroundData.GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var ac = MakeGroundAircraft(new LatLon(37.728, -122.218), layout);
        var compound = CommandParser.ParseCompound("AT B SPD 5");
        Assert.True(compound.IsSuccess);
        CommandDispatcher.DispatchCompound(compound.Value!, ac, TestDispatch.Context(Random.Shared, groundLayout: layout));

        Assert.False(ac.Queue.Blocks[0].IsApplied);

        // Wrong taxiway — must not fire.
        FlightPhysics.NotifyGroundEntityReached(ac, arrivedNodeId: null, newTaxiwayName: "A");
        Assert.False(ac.Queue.Blocks[0].IsApplied);

        // Matching taxiway — must fire.
        FlightPhysics.NotifyGroundEntityReached(ac, arrivedNodeId: null, newTaxiwayName: "B");
        Assert.True(ac.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void Notify_NodeMatch_FiresQueuedBlock_ForSpot()
    {
        var layout = GroundData.GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var spot = layout.Nodes.Values.FirstOrDefault(n => n.Type == GroundNodeType.Spot);
        if (spot?.Name is null)
        {
            return;
        }

        var ac = MakeGroundAircraft(new LatLon(37.728, -122.218), layout);
        var compound = CommandParser.ParseCompound($"AT ${spot.Name} SPD 5");
        Assert.True(compound.IsSuccess);
        CommandDispatcher.DispatchCompound(compound.Value!, ac, TestDispatch.Context(Random.Shared, groundLayout: layout));

        Assert.Single(ac.Queue.Blocks);
        Assert.False(ac.Queue.Blocks[0].IsApplied);

        // Wrong node — must not fire.
        FlightPhysics.NotifyGroundEntityReached(ac, arrivedNodeId: -1, newTaxiwayName: null);
        Assert.False(ac.Queue.Blocks[0].IsApplied);

        // Matching node — must fire even though no phase tick happened
        // (this is the phase-bypass path that lets ground triggers work mid-taxi).
        FlightPhysics.NotifyGroundEntityReached(ac, arrivedNodeId: spot.Id, newTaxiwayName: null);
        Assert.True(ac.Queue.Blocks[0].IsApplied);
    }

    [Fact]
    public void Notify_AlreadyAppliedBlock_NotRefired()
    {
        var layout = GroundData.GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var ac = MakeGroundAircraft(new LatLon(37.728, -122.218), layout);
        var compound = CommandParser.ParseCompound("AT B SPD 5");
        Assert.True(compound.IsSuccess);
        CommandDispatcher.DispatchCompound(compound.Value!, ac, TestDispatch.Context(Random.Shared, groundLayout: layout));

        // First fire applies the block.
        FlightPhysics.NotifyGroundEntityReached(ac, arrivedNodeId: null, newTaxiwayName: "B");
        Assert.True(ac.Queue.Blocks[0].IsApplied);

        // Reset the side-effect (TargetSpeed) and notify again — must NOT re-apply.
        ac.Targets.TargetSpeed = 99;
        FlightPhysics.NotifyGroundEntityReached(ac, arrivedNodeId: null, newTaxiwayName: "B");
        Assert.Equal(99, ac.Targets.TargetSpeed);
    }

    // -------------------------------------------------------------------------
    // Snapshot round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void Snapshot_RoundTrip_PreservesGroundFields()
    {
        var trigger = new BlockTrigger
        {
            Type = BlockTriggerType.AtGroundEntity,
            GroundKind = GroundEntityKind.Spot,
            GroundNodeId = 42,
            FixLat = 37.728,
            FixLon = -122.218,
            GroundTaxiwayName = null,
            GroundEntityToken = "5",
        };

        var dto = trigger.ToSnapshot();
        var restored = BlockTrigger.FromSnapshot(dto);

        Assert.Equal(trigger.Type, restored.Type);
        Assert.Equal(trigger.GroundKind, restored.GroundKind);
        Assert.Equal(trigger.GroundNodeId, restored.GroundNodeId);
        Assert.Equal(trigger.FixLat, restored.FixLat);
        Assert.Equal(trigger.FixLon, restored.FixLon);
        Assert.Equal(trigger.GroundTaxiwayName, restored.GroundTaxiwayName);
        Assert.Equal(trigger.GroundEntityToken, restored.GroundEntityToken);
    }

    [Fact]
    public void Snapshot_RoundTrip_TaxiwayKind()
    {
        var trigger = new BlockTrigger
        {
            Type = BlockTriggerType.AtGroundEntity,
            GroundKind = GroundEntityKind.Taxiway,
            GroundTaxiwayName = "B",
            GroundEntityToken = "B",
        };

        var dto = trigger.ToSnapshot();
        var restored = BlockTrigger.FromSnapshot(dto);

        Assert.Equal(GroundEntityKind.Taxiway, restored.GroundKind);
        Assert.Equal("B", restored.GroundTaxiwayName);
        Assert.Null(restored.GroundNodeId);
    }
}
