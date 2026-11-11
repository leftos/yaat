using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// <c>@GATE</c> is the stand the aircraft's flight-plan remarks name: <c>TAXI A @GATE</c> and <c>TAXIAUTO @GATE</c> clear it
/// to that stand with the readback a hand-typed <c>@stand</c> gives. A remark naming only a parking area clears it to a free
/// stand in the area that fits the type (any free stand in the area when none fits), picked the same way every time, and
/// reads back the area's name. Remarks that name no stand, an area that is full, and an area the airport does not know are
/// refused with the aircraft left as it was.
/// </summary>
public class TaxiToRemarkGateTests(ITestOutputHelper output)
{
    /// <summary>UAL733's pose in the hold-short bundle: on T at t=1930, heading 208.98° true, A320.</summary>
    private const double OnTLatitude = 37.62094280885291;

    private const double OnTLongitude = -122.38247885498703;
    private const double OnTHeadingTrueDeg = 208.98;

    /// <summary>
    /// SFO node 1546 on C, east of the T421 junction, heading west-north-west along C toward it: <c>TAXI C</c> reaches SIG1 and
    /// SIG6 from here (LayoutInspector <c>--pathfinder 1546 C</c>, no warnings).
    /// </summary>
    private const double OnCLatitude = 37.626213;

    private const double OnCLongitude = -122.383693;
    private const double OnCHeadingTrueDeg = 297.9;

    private const string Callsign = "UAL733";
    private const string AircraftType = "A320";
    private const string Stand = "E8";
    private const string StandRemark = "/V/ GATE E8, 28R";
    private const string NoStandRemark = "/v/";
    private const string AreaRemark = "/V/ PARKING SIGNATURE, 28R";
    private const string UnknownAreaRemark = "/V/ PARKING ATLANTIC, 28R";

    /// <summary>SFO's sidecar maps the Signature area to the stands named <c>SIG*</c>.</summary>
    private static readonly string[] SignatureStands = ["SIG1", "SIG2", "SIG3", "SIG4", "SIG5", "SIG6", "SIG7", "SIG8", "SIG9", "SIG10"];

    /// <summary>
    /// The Signature stands whose spacing takes a GLF4's 77.8 ft span: SIG1 (79.0 ft) and SIG6 (79.4 ft); SIG2, the next
    /// widest, takes 76.5 ft (StandFitTests' SIG estimates).
    /// </summary>
    private static readonly string[] MidSpanFitting = ["SIG1", "SIG6"];

    /// <summary>The stand UAL733 in a GLF4 draws from <see cref="MidSpanFitting"/> with nothing taken.</summary>
    private const string MidSpanPick = "SIG6";

    /// <summary>The stand UAL733 in an A320, which no Signature stand fits, draws from all of <see cref="SignatureStands"/>.</summary>
    private const string WholeAreaPick = "SIG9";

    /// <summary>A Gulfstream IV (77.8 ft span): it fits some of the Signature stands, not all of them.</summary>
    private const string MidSpanType = "GLF4";

    /// <summary>The type parked on the stands an area test fills.</summary>
    private const string ParkedType = "C172";

    private const string FullAreaRefusal = "UAL733's remarks name parking area SIGNATURE, and every stand there is taken; name the stand with @";

    [Fact]
    public void TaxiAtGate_ResolvesTheRemarkStand()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground, StandRemark, AircraftType);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXI A @GATE");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Equal(Stand, route.DestinationParking);
    }

    [Fact]
    public void TaxiAutoAtGate_ResolvesTheRemarkStand()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground, StandRemark, AircraftType);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @GATE");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Equal(Stand, route.DestinationParking);
    }

    [Fact]
    public void TaxiAtGate_NoParkingRemark_IsRefusedAndLeavesTheAircraftAlone()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground, NoStandRemark, AircraftType);
        Type? phaseBefore = aircraft.Phases?.CurrentPhase?.GetType();
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXI A @GATE");

        Assert.False(result.Success, result.Message);
        Assert.Equal("No gate in UAL733's flight plan remarks; name the stand with @", result.Message);
        Assert.True(result.IsOperatorGuidance, "the refusal must be operator guidance so no pilot unable is queued");
        Assert.Equal(phaseBefore, aircraft.Phases?.CurrentPhase?.GetType());
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>Case 1: an area remark with free stands that fit the type picks one of them, and the same one on a fresh sim.</summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_PicksTheSameFreeFittingStandInTheArea()
    {
        if ((BuildSfo() is not { } first) || (BuildSfo() is not { } second))
        {
            return;
        }

        string picked = ClearToArea(first, MidSpanType);
        string again = ClearToArea(second, MidSpanType);

        Assert.Equal(MidSpanPick, picked);
        Assert.Equal(MidSpanPick, again);
    }

    /// <summary>Case 2: the stand the pick would take is parked on, so a different free stand in the area is picked.</summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_WouldBePickTaken_PicksAnotherFreeStand()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        Park(ground, "PRK1", MidSpanPick);

        string picked = ClearToArea(ground, MidSpanType);

        Assert.Equal(Assert.Single(MidSpanFitting, s => s != MidSpanPick), picked);
    }

    /// <summary>Case 3: every stand that fits is taken while one that does not is free: the area is full for the type.</summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_EveryFittingStandTaken_IsRefusedAsFull()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        ParkOnEach(ground, MidSpanFitting);
        AircraftState aircraft = SpawnOnT(ground, AreaRemark, MidSpanType);

        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @GATE");

        AssertRefused(result, aircraft, FullAreaRefusal);
    }

    /// <summary>Case 4: no stand in the area fits the type, so the pick is made from the whole area.</summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_NoStandFits_PicksFromTheWholeArea()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        string picked = ClearToArea(ground, AircraftType);

        Assert.Equal(WholeAreaPick, picked);
    }

    /// <summary>Case 5: every stand in the area is taken.</summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_EveryStandTaken_IsRefusedAsFull()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        ParkOnEach(ground, SignatureStands);
        AircraftState aircraft = SpawnOnT(ground, AreaRemark, AircraftType);

        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @GATE");

        AssertRefused(result, aircraft, FullAreaRefusal);
    }

    /// <summary>
    /// An area the sidecar lists whose stand patterns cover no stand of the layout is refused as matching no stand; under the
    /// same sidecar an area covering one stand clears to it, so the test-local sidecar is the one read.
    /// </summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_AreaCoveringNoLayoutStand_IsRefusedAsMatchingNoStand()
    {
        if ((BuildSfo() is not { } ground) || (BuildSfo() is not { } control) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        var sidecars = new AirportSidecarCatalog([
            new AirportSidecar("KSFO")
            {
                ParkingAreas = [new ParkingAreaAlias("NOWHERE", ["ZZZ*"], null), new ParkingAreaAlias("HANGAR", ["SIG5"], null)],
            },
        ]);
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb.WithAirportSidecars(sidecars));

        AircraftState aircraft = SpawnOnT(ground, "/V/ PARKING NOWHERE, 28R", AircraftType);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @GATE");
        AircraftState controlAircraft = SpawnOnT(control, "/V/ PARKING HANGAR, 28R", AircraftType);
        CommandResult controlResult = control.Engine.SendCommand(Callsign, "TAXIAUTO @GATE");

        AssertRefused(result, aircraft, "UAL733's remarks name parking area NOWHERE, which matches no stand at this airport; name the stand with @");
        Assert.True(controlResult.Success, controlResult.Message);
        Assert.Equal("SIG5", Assert.IsType<TaxiRoute>(controlAircraft.Ground.AssignedTaxiRoute).DestinationParking);
    }

    /// <summary>Case 6: an area the airport's sidecar has no entry for matches no stand.</summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_AreaWithNoSidecarEntry_IsRefusedAsMatchingNoStand()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground, UnknownAreaRemark, AircraftType);

        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @GATE");

        AssertRefused(result, aircraft, "UAL733's remarks name parking area ATLANTIC, which matches no stand at this airport; name the stand with @");
    }

    /// <summary>Case 7: the readback names the area as the remark spells it, not the stand picked inside it.</summary>
    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_ReadsBackTheAreaName()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            Callsign,
            MidSpanType,
            (VirtualNode.Create(OnCLatitude, OnCLongitude), new TrueHeading(OnCHeadingTrueDeg)),
            new HoldingInPositionPhase()
        );
        aircraft.FlightPlan.ReplaceRemarks(AreaRemark);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXI C @GATE");

        Assert.True(result.Success, result.Message);
        HandlerReadback readback = Assert.IsType<HandlerReadback>(result.PilotReadback);
        output.WriteLine($"readback: {readback.Speech.Terminal} | {readback.Speech.Tts}");
        Assert.Contains("taxi to SIGNATURE", readback.Speech.Terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("parking SIG", readback.Speech.Terminal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TaxiAtGate_ReadsBackExactlyAsTheTypedStandDoes()
    {
        if ((BuildSfo() is not { } aliasGround) || (BuildSfo() is not { } typedGround))
        {
            return;
        }

        CommandResult alias = Clear(aliasGround, "TAXI A @GATE");
        CommandResult typed = Clear(typedGround, $"TAXI A @{Stand}");

        Assert.True(alias.Success, alias.Message);
        Assert.True(typed.Success, typed.Message);
        Assert.Equal(typed.Message, alias.Message);
    }

    private SfoGround? BuildSfo() => SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off);

    private static AircraftState SpawnOnT(SfoGround ground, string remarks, string type)
    {
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            Callsign,
            type,
            (VirtualNode.Create(OnTLatitude, OnTLongitude), new TrueHeading(OnTHeadingTrueDeg)),
            new HoldingInPositionPhase()
        );
        aircraft.FlightPlan.ReplaceRemarks(remarks);
        return aircraft;
    }

    /// <summary>Aircraft's own remark stand, cleared by <paramref name="command"/> on a freshly built SFO.</summary>
    private static CommandResult Clear(SfoGround ground, string command)
    {
        SpawnOnT(ground, StandRemark, AircraftType);
        return ground.Engine.SendCommand(Callsign, command);
    }

    /// <summary>Clears an aircraft of <paramref name="type"/> with the Signature remark to <c>@GATE</c>; the stand its route ends at.</summary>
    private static string ClearToArea(SfoGround ground, string type)
    {
        AircraftState aircraft = SpawnOnT(ground, AreaRemark, type);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXIAUTO @GATE");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        return Assert.IsType<string>(route.DestinationParking);
    }

    private static void AssertRefused(CommandResult result, AircraftState aircraft, string expected)
    {
        Assert.False(result.Success, result.Message);
        Assert.Equal(expected, result.Message);
        Assert.True(result.IsOperatorGuidance, "the refusal must be operator guidance so no pilot unable is queued");
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>Another aircraft parked on <paramref name="stand"/>, which is what makes it taken.</summary>
    private static void Park(SfoGround ground, string callsign, string stand)
    {
        AircraftState parked = SfoGroundHarness.SpawnParked(ground, callsign, ParkedType, stand);
        parked.Ground.ParkingSpot = stand;
    }

    private static void ParkOnEach(SfoGround ground, IReadOnlyList<string> stands)
    {
        for (int i = 0; i < stands.Count; i++)
        {
            Park(ground, $"PRK{i + 1}", stands[i]);
        }
    }
}
