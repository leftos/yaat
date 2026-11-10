using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// <c>@GATE</c> is the stand the aircraft's flight-plan remarks name: <c>TAXI A @GATE</c> and <c>TAXIAUTO @GATE</c> clear it
/// to that stand with the readback a hand-typed <c>@stand</c> gives, and remarks that name no stand are refused — naming the
/// parking area when the remark carries one — with the aircraft left as it was.
/// </summary>
public class TaxiToRemarkGateTests(ITestOutputHelper output)
{
    /// <summary>UAL733's pose in the hold-short bundle: on T at t=1930, heading 208.98° true, A320.</summary>
    private const double OnTLatitude = 37.62094280885291;

    private const double OnTLongitude = -122.38247885498703;
    private const double OnTHeadingTrueDeg = 208.98;

    private const string Callsign = "UAL733";
    private const string AircraftType = "A320";
    private const string Stand = "E8";
    private const string StandRemark = "/V/ GATE E8, 28R";
    private const string NoStandRemark = "/v/";
    private const string AreaRemark = "/V/ PARKING SIGNATURE, 28R";

    [Fact]
    public void TaxiAtGate_ResolvesTheRemarkStand()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground, StandRemark);
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

        AircraftState aircraft = SpawnOnT(ground, StandRemark);
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

        AircraftState aircraft = SpawnOnT(ground, NoStandRemark);
        Type? phaseBefore = aircraft.Phases?.CurrentPhase?.GetType();
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXI A @GATE");

        Assert.False(result.Success, result.Message);
        Assert.Equal("No gate in UAL733's flight plan remarks; name the stand with @", result.Message);
        Assert.True(result.IsOperatorGuidance, "the refusal must be operator guidance so no pilot unable is queued");
        Assert.Equal(phaseBefore, aircraft.Phases?.CurrentPhase?.GetType());
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    [Fact]
    public void TaxiAtGate_ParkingAreaRemark_IsRefusedNamingTheArea()
    {
        if (BuildSfo() is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnOnT(ground, AreaRemark);
        CommandResult result = ground.Engine.SendCommand(Callsign, "TAXI A @GATE");

        Assert.False(result.Success, result.Message);
        Assert.Equal("UAL733's remarks name parking area SIGNATURE, not a gate; name the stand with @", result.Message);
        Assert.True(result.IsOperatorGuidance, "the refusal must be operator guidance so no pilot unable is queued");
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
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

    private static AircraftState SpawnOnT(SfoGround ground, string remarks)
    {
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            Callsign,
            AircraftType,
            (VirtualNode.Create(OnTLatitude, OnTLongitude), new TrueHeading(OnTHeadingTrueDeg)),
            new HoldingInPositionPhase()
        );
        aircraft.FlightPlan.ReplaceRemarks(remarks);
        return aircraft;
    }

    /// <summary>Aircraft's own remark stand, cleared by <paramref name="command"/> on a freshly built SFO.</summary>
    private static CommandResult Clear(SfoGround ground, string command)
    {
        SpawnOnT(ground, StandRemark);
        return ground.Engine.SendCommand(Callsign, command);
    }
}
