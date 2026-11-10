using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// <c>RTAXI &lt;path&gt;</c> re-routes an aircraft on its taxi route and keeps the route's destination (stand, spot or runway),
/// reading back exactly as the equivalent full TAXI would; it drops the old route's HS / CROSS / EXP, keeps a stored takeoff or
/// line-up clearance only when the new route reaches the same runway entry, and refuses with operator guidance when there is no
/// route, no destination to keep, the taxi is complete, or a different trailing destination is named.
/// </summary>
public class ReRouteTaxiTests(ITestOutputHelper output)
{
    /// <summary>UAL733's pose in the hold-short bundle: on T at t=1930, heading 208.98° true.</summary>
    private const double OnTLatitude = 37.62094280885291;

    private const double OnTLongitude = -122.38247885498703;
    private const double OnTHeadingTrueDeg = 208.98;

    private const string Callsign = "DAL2272";
    private const string AircraftType = "A320";

    [Fact]
    public void ParkingKept_NewRouteEndsAtTheStand_AndReadsBackAsTheFullTaxi()
    {
        if ((Build() is not { } reRouted) || (Build() is not { } typed))
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(reRouted);
        AssertSent(reRouted, "TAXI B K A @B9");
        (CommandResult result, string readback) = SendWithReadback(reRouted, aircraft, "RTAXI B T A");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Equal("B9", route.DestinationParking);

        AircraftState typedAircraft = SpawnOnT(typed);
        AssertSent(typed, "TAXI B K A @B9");
        (CommandResult typedResult, string typedReadback) = SendWithReadback(typed, typedAircraft, "TAXI B T A @B9");

        Assert.True(typedResult.Success, typedResult.Message);
        Assert.Contains("B9", typedReadback, StringComparison.Ordinal);
        Assert.Equal(typedReadback, readback);
        Assert.Equal(typedResult.Message, result.Message);
    }

    [Fact]
    public void ControllerAirtime_EqualsTheFullTaxis()
    {
        if ((Build() is not { } reRouted) || (Build() is not { } typed))
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(reRouted);
        AssertSent(reRouted, "TAXI B K A @B9");
        double reRouteAirtime = ControllerAirtimeOf(reRouted, aircraft, "RTAXI B T A");
        AircraftState typedAircraft = SpawnOnT(typed);
        AssertSent(typed, "TAXI B K A @B9");
        double taxiAirtime = ControllerAirtimeOf(typed, typedAircraft, "TAXI B T A @B9");

        Assert.True(taxiAirtime > 1.0, $"the full TAXI's airtime {taxiAirtime} s is the floor");
        Assert.Equal(taxiAirtime, reRouteAirtime);
    }

    /// <summary>
    /// The airtime the frequency meter records for the controller transmission <paramref name="command"/>, from the dispatch's own
    /// result (the harness has no scenario answering pilots, so the engine records none itself).
    /// </summary>
    private double ControllerAirtimeOf(SfoGround ground, AircraftState aircraft, string command)
    {
        CompoundCommand compound = CommandParser.ParseCompound(command).Value!;
        CommandResult result = ground.Engine.SendCommand(Callsign, command);
        Assert.True(result.Success, $"{command}: {result.Message}");
        double airtime = SimulationEngine.ControllerAirtimeSeconds(compound, result, aircraft, ActiveRunways.Empty);
        output.WriteLine($"{command}: controller airtime {airtime} s");
        return airtime;
    }

    [Fact]
    public void SpotKept_NewRouteEndsAtTheSpot()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A $2");
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B T A");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Equal("2", route.DestinationSpot);
    }

    [Fact]
    public void RunwayKept_NewRouteEndsHeldShortOfTheSameRunway()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A 1L");
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B T A");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        HoldShortPoint destination = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.DestinationRunway);
        Assert.True(HoldShortAnnotator.TargetMatches(destination.TargetName, "1L"), destination.TargetName);
    }

    [Fact]
    public void Dropped_OldHoldShortCrossAndExpedite_AreNotCarried()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A 1L HS K EXP");
        Assert.True(aircraft.Ground.IsExpeditingTaxi);
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B T A");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.DoesNotContain(route.HoldShortPoints, h => h.Reason == HoldShortReason.ExplicitHoldShort);
        Assert.False(aircraft.Ground.IsExpeditingTaxi);
    }

    [Fact]
    public void Dropped_OldCrossing_IsNotCarried_UnlessRestated()
    {
        if ((Build() is not { } dropped) || (Build() is not { } restated))
        {
            return;
        }

        AircraftState droppedAircraft = SpawnOnT(dropped);
        AssertSent(dropped, "TAXI B K A F 28L CROSS 1L");
        Assert.Contains(CrossingsOf(droppedAircraft), h => h.IsCleared);
        CommandResult droppedResult = dropped.Engine.SendCommand(Callsign, "RTAXI B K A F");

        Assert.True(droppedResult.Success, droppedResult.Message);
        Assert.NotEmpty(CrossingsOf(droppedAircraft));
        Assert.DoesNotContain(CrossingsOf(droppedAircraft), h => h.IsCleared);

        AircraftState restatedAircraft = SpawnOnT(restated);
        AssertSent(restated, "TAXI B K A F 28L CROSS 1L");
        CommandResult restatedResult = restated.Engine.SendCommand(Callsign, "RTAXI B K A F CROSS 1L");

        Assert.True(restatedResult.Success, restatedResult.Message);
        Assert.Contains(CrossingsOf(restatedAircraft), h => h.IsCleared);
    }

    [Fact]
    public void Restated_HoldShortAndExpedite_AreCarried()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A 1L");
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B T A HS A EXP");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Contains(route.HoldShortPoints, h => h.Reason == HoldShortReason.ExplicitHoldShort);
        Assert.True(aircraft.Ground.IsExpeditingTaxi);
    }

    [Fact]
    public void NotCleared_ParkedWithNoRoute_IsRefused()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, Callsign, AircraftType, "B9");
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B T A");

        AssertRefused(result, "RTAXI rejected: not cleared to taxi; use TAXI");
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    [Fact]
    public void NoDestination_IsRefused()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        SpawnOnT(ground);
        AssertSent(ground, "TAXI B K");
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B T");

        AssertRefused(result, "RTAXI rejected: route has no destination to keep; give one with TAXI <path> <runway/@gate/$spot>");
    }

    [Fact]
    public void Complete_AtTheSpot_IsRefused()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A $2");
        int arrivedAt = SfoGroundHarness.TickUntil(ground.Engine, () => aircraft.Ground.AssignedTaxiRoute?.IsComplete == true, 1200, null);
        Assert.True(arrivedAt > 0, "the aircraft never reached spot 2");
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B T A");

        AssertRefused(result, "RTAXI rejected: taxi to $2 complete; use TAXI");
    }

    [Fact]
    public void DifferentRunway_IsRefused()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A 1L");
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B T A 1R");

        AssertRefused(result, "RTAXI keeps runway 1L; use TAXI B T A 1R to change the runway");
    }

    [Fact]
    public void EqualRunway_IsAcceptedAsRedundant()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A 1L");
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B T A 1L");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Contains(route.HoldShortPoints, h => h.Reason == HoldShortReason.DestinationRunway);
    }

    [Fact]
    public void DifferentParking_IsRefused()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A @B9");
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B T A @B10");

        AssertRefused(result, "RTAXI keeps @B9; use TAXI B T A @B10 to change the destination");
    }

    [Fact]
    public void StoredTakeoffClearance_SameEntry_IsKept()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A 1L");
        AssertSent(ground, "CTO");
        Assert.NotNull(aircraft.Phases?.DepartureClearance);
        int priorEntry = DestinationHoldNode(aircraft);
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B K A");

        Assert.True(result.Success, result.Message);
        Assert.Equal(priorEntry, DestinationHoldNode(aircraft));
        Assert.Equal(ClearanceType.ClearedForTakeoff, aircraft.Phases?.DepartureClearance?.Type);
    }

    [Fact]
    public void StoredTakeoffClearance_DifferentEntry_IsVoidedWithTheWarning()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A 1L");
        AssertSent(ground, "CTO");
        aircraft.PendingWarnings.Clear();
        int priorEntry = DestinationHoldNode(aircraft);
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B K A F");
        output.WriteLine($"RTAXI B K A F → {result.Success}: {result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.NotEqual(priorEntry, DestinationHoldNode(aircraft));
        Assert.Null(aircraft.Phases?.DepartureClearance);
        Assert.Contains(
            "RTAXI reaches runway 1L at a different entry: the takeoff clearance issued for 1L is void and must be re-issued",
            aircraft.PendingWarnings
        );
    }

    [Fact]
    public void StoredLineUpAndWait_SameEntry_IsKept()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A 1L");
        AssertSent(ground, "LUAW");
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B K A");

        Assert.True(result.Success, result.Message);
        Assert.Equal(ClearanceType.LineUpAndWait, aircraft.Phases?.DepartureClearance?.Type);
    }

    [Fact]
    public void StoredLineUpAndWait_DifferentEntry_IsVoidedWithTheWarning()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A 1L");
        AssertSent(ground, "LUAW");
        aircraft.PendingWarnings.Clear();
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B K A F");

        Assert.True(result.Success, result.Message);
        Assert.Null(aircraft.Phases?.DepartureClearance);
        Assert.Contains(
            "RTAXI reaches runway 1L at a different entry: the line up and wait clearance issued for 1L is void and must be re-issued",
            aircraft.PendingWarnings
        );
    }

    [Fact]
    public void HeldAtACrossingBarTheNewRouteCrosses_StaysHeldUntilCross()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = HeldShortOfTheFCrossing(ground, storeTakeoffClearance: false);
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI F");
        output.WriteLine($"RTAXI F → {result.Success}: {result.Message}");

        Assert.True(result.Success, result.Message);
        HoldShortPoint crossing = CrossingsOf(aircraft)[0];
        Assert.False(crossing.IsCleared, $"nobody cleared {crossing.TargetName}: the RTAXI must not carry the hold's crossing");
        SfoGroundHarness.TickUntil(ground.Engine, () => false, 15, null);
        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
    }

    [Fact]
    public void StoredTakeoffClearance_RtaxiAfterCrossInOneBlock_IsKept()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = HeldShortOfTheFCrossing(ground, storeTakeoffClearance: true);
        CommandResult result = ground.Engine.SendCommand(Callsign, "CROSS 1L, RTAXI F");
        output.WriteLine($"CROSS 1L, RTAXI F → {result.Success}: {result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.Equal(ClearanceType.ClearedForTakeoff, aircraft.Phases?.DepartureClearance?.Type);
    }

    [Fact]
    public void RtaxiAfterCrossInOneBlock_ReadsBackAsTheFullTaxi()
    {
        if ((Build() is not { } reRouted) || (Build() is not { } typed))
        {
            return;
        }

        AircraftState aircraft = HeldShortOfTheFCrossing(reRouted, storeTakeoffClearance: false);
        (CommandResult result, string readback) = SendWithReadback(reRouted, aircraft, "CROSS 1L, RTAXI F");
        AircraftState typedAircraft = HeldShortOfTheFCrossing(typed, storeTakeoffClearance: false);
        (CommandResult typedResult, string typedReadback) = SendWithReadback(typed, typedAircraft, "CROSS 1L, TAXI F 28L");

        Assert.Equal(typedResult.Success, result.Success);
        Assert.NotEqual("(none)", typedReadback);
        Assert.Equal(typedReadback, readback);
    }

    [Fact]
    public void StoredTakeoffClearance_QueuedRtaxi_IsKept()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = HeldShortOfTheFCrossing(ground, storeTakeoffClearance: true);
        TaxiRoute before = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        AssertSent(ground, "CROSS 1L; RTAXI F");
        int firedAt = SfoGroundHarness.TickUntil(ground.Engine, () => !ReferenceEquals(before, aircraft.Ground.AssignedTaxiRoute), 300, null);

        Assert.True(firedAt > 0, "the queued RTAXI never fired");
        Assert.Equal(ClearanceType.ClearedForTakeoff, aircraft.Phases?.DepartureClearance?.Type);
    }

    /// <summary>
    /// A CROSS in the same block as the TAXI reads back as the full TAXI's: one naming the first runway the route crosses, one naming
    /// the second. Both are on the route — a CROSS of a runway the route carries no hold for is refused ("No hold-short for 28R in taxi
    /// route"), so an accepted off-route CROSS cannot be expressed on this route.
    /// </summary>
    [Theory]
    [InlineData("CROSS 1L")]
    [InlineData("CROSS 1R")]
    public void CrossInTheSameBlock_ReadsBackAsTheFullTaxi(string cross)
    {
        if ((Build() is not { } reRouted) || (Build() is not { } typed))
        {
            return;
        }

        // From the T pose, not from a hold: a hold's crossing is the one place an RTAXI deliberately differs from TAXI.
        AircraftState aircraft = SpawnOnT(reRouted);
        AssertSent(reRouted, "TAXI B K A F 28L");
        (CommandResult result, string readback) = SendWithReadback(reRouted, aircraft, $"RTAXI B K A F, {cross}");
        AircraftState typedAircraft = SpawnOnT(typed);
        AssertSent(typed, "TAXI B K A F 28L");
        (CommandResult typedResult, string typedReadback) = SendWithReadback(typed, typedAircraft, $"TAXI B K A F 28L, {cross}");

        Assert.Equal(typedResult.Success, result.Success);
        Assert.NotEqual("(none)", typedReadback);
        Assert.Equal(typedReadback, readback);
    }

    [Fact]
    public void DroppedTaxiway_ReadsBackUnableAsTheFullTaxi()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground);
        var issued = new TaxiCommand(["M4", "M1", "A"], [], Expedite: false, DestinationRunway: "1R");
        var effective = new TaxiCommand(["M1", "A"], [], Expedite: false, DestinationRunway: "1R");
        var reRoute = new ReRouteTaxiCommand(new TaxiCommand(["M4", "M1", "A"], [], Expedite: false));

        PilotSpeechText? typed = PilotResponder.BuildReadbackAsApplied(
            new CompoundCommand([new ParsedBlock(null, [issued])]),
            new CommandResult(true, EffectiveCommand: effective),
            aircraft,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );
        PilotSpeechText? reRouted = PilotResponder.BuildReadbackAsApplied(
            new CompoundCommand([new ParsedBlock(null, [reRoute])]),
            new CommandResult(true, EffectiveCommand: effective) { ReRouteAppliedAs = issued },
            aircraft,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );

        Assert.NotNull(typed);
        Assert.StartsWith("unable M4, ", typed.Terminal, StringComparison.Ordinal);
        Assert.Equal(typed, reRouted);
    }

    /// <summary>
    /// The real engine drops <c>T</c> from <c>TAXI B K A T @B9</c> as a contradictory via (no route via T reaches @B9), and the
    /// RTAXI that keeps @B9 reads the route back with the same "unable T" the full TAXI does.
    /// </summary>
    [Fact]
    public void Dropped_RealRtaxiReadsBackUnableAsTheFullTaxi()
    {
        if ((Build() is not { } reRouted) || (Build() is not { } typed))
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(reRouted);
        AssertSent(reRouted, "TAXI B K A @B9");
        (CommandResult result, string readback) = SendWithReadback(reRouted, aircraft, "RTAXI B K A T");
        AircraftState typedAircraft = SpawnOnT(typed);
        (CommandResult typedResult, string typedReadback) = SendWithReadback(typed, typedAircraft, "TAXI B K A T @B9");

        Assert.True(result.Success, result.Message);
        Assert.True(typedResult.Success, typedResult.Message);
        Assert.StartsWith("unable", typedReadback, StringComparison.Ordinal);
        Assert.StartsWith("unable", readback, StringComparison.Ordinal);
        Assert.Equal(typedReadback, readback);
    }

    [Fact]
    public void Complete_HeldShortOfTheDestinationRunway_IsRefused()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A 1L");
        int heldAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.Reason: HoldShortReason.DestinationRunway },
            1200,
            null
        );
        Assert.True(heldAt > 0, "the aircraft never reached its 1L hold short");
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B T A");

        AssertRefused(result, "RTAXI rejected: taxi to 1L complete; use TAXI");
    }

    [Fact]
    public void NoDelete_IsCarried()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A 1L NODEL");
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B T A");

        Assert.True(result.Success, result.Message);
        Assert.True(aircraft.Ground.NoDeleteRequested);
        Assert.True(aircraft.Ground.AutoDeleteExempt);
    }

    [Fact]
    public void DifferentSpot_IsRefused()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A $2");
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B T A $1");

        AssertRefused(result, "RTAXI keeps $2; use TAXI B T A $1 to change the destination");
    }

    [Fact]
    public void RunwayWhenTheKeptDestinationIsAStand_IsRefused()
    {
        if (Build() is not { } ground)
        {
            return;
        }

        SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A @B9");
        CommandResult result = ground.Engine.SendCommand(Callsign, "RTAXI B T A 1L");

        AssertRefused(result, "RTAXI keeps @B9; use TAXI B T A 1L to change the destination");
    }

    /// <summary>
    /// The aircraft taxied <c>TAXI B K A F 28L</c> to HoldingShortPhase at F's 1L/19R crossing bar, with a takeoff clearance stored
    /// on the way when <paramref name="storeTakeoffClearance"/>.
    /// </summary>
    private AircraftState HeldShortOfTheFCrossing(SfoGround ground, bool storeTakeoffClearance)
    {
        AircraftState aircraft = SpawnOnT(ground);
        AssertSent(ground, "TAXI B K A F 28L");
        if (storeTakeoffClearance)
        {
            AssertSent(ground, "CTO");
        }

        int heldAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.Reason: HoldShortReason.RunwayCrossing },
            1200,
            null
        );
        Assert.True(heldAt > 0, "the aircraft never held short of the F crossing");
        output.WriteLine($"held short of {((HoldingShortPhase)aircraft.Phases!.CurrentPhase!).HoldShort.TargetName} at t+{heldAt}");
        return aircraft;
    }

    /// <summary>The runway-crossing holds of 1L/19R on the aircraft's taxi route.</summary>
    private static List<HoldShortPoint> CrossingsOf(AircraftState aircraft) =>
        [
            .. Assert
                .IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute)
                .HoldShortPoints.Where(h => (h.Reason == HoldShortReason.RunwayCrossing) && HoldShortAnnotator.TargetMatches(h.TargetName, "1L")),
        ];

    /// <summary>The node of the runway entry the aircraft's taxi route ends held short at.</summary>
    private static int DestinationHoldNode(AircraftState aircraft) =>
        Assert
            .Single(Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute).HoldShortPoints, h => h.Reason == HoldShortReason.DestinationRunway)
            .NodeId;

    private SfoGround? Build() => SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off);

    private static AircraftState SpawnOnT(SfoGround ground) =>
        SfoGroundHarness.SpawnAt(
            ground,
            Callsign,
            AircraftType,
            (VirtualNode.Create(OnTLatitude, OnTLongitude), new TrueHeading(OnTHeadingTrueDeg)),
            new HoldingInPositionPhase()
        );

    private void AssertSent(SfoGround ground, string command)
    {
        CommandResult result = ground.Engine.SendCommand(Callsign, command);
        output.WriteLine($"{command} → {result.Success}: {result.Message}");
        Assert.True(result.Success, $"{command}: {result.Message}");
    }

    private (CommandResult Result, string Readback) SendWithReadback(SfoGround ground, AircraftState aircraft, string command)
    {
        CompoundCommand compound = CommandParser.ParseCompound(command).Value!;
        CommandResult result = ground.Engine.SendCommand(Callsign, command);
        string readback =
            PilotResponder.BuildReadbackAsApplied(compound, result, aircraft, PilotSpeechContext.Canonical, ActiveRunways.Empty)?.Terminal
            ?? "(none)";
        output.WriteLine($"{command} → {result.Success}: {result.Message} | readback: {readback}");
        return (result, readback);
    }

    private void AssertRefused(CommandResult result, string expected)
    {
        output.WriteLine($"refused: {result.Message}");
        Assert.False(result.Success, result.Message);
        Assert.Equal(expected, result.Message);
        Assert.True(result.IsOperatorGuidance, "an RTAXI refusal is operator guidance, so no pilot unable is queued");
    }
}
