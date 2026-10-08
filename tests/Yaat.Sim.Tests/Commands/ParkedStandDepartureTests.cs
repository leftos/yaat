using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Data.Airport.Precompute;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// <see cref="ParkedStandDeparture.Of(AircraftState, AirportGroundLayout?, AirportSidecarCatalog)"/>: the departure of the
/// stand an aircraft is parked on, on the real KOAK and KSFO layouts with the shipped sidecars. KOAK GA20 is a taxi-out
/// stand, KOAK gate 26 and KSFO F8 are pushed back from.
/// </summary>
public class ParkedStandDepartureTests
{
    public ParkedStandDepartureTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Ga20_AtParking_IsTaxiOut() =>
        Assert.Equal(StandDeparture.TaxiOut, ParkedStandDeparture.Of(Parked("GA20", new AtParkingPhase()), Oak(), Sidecars));

    [Fact]
    public void Gate26_AtParking_IsPushBack() =>
        Assert.Equal(StandDeparture.PushBack, ParkedStandDeparture.Of(Parked("26", new AtParkingPhase()), Oak(), Sidecars));

    [Fact]
    public void SfoF8_AtParking_IsPushBack() =>
        Assert.Equal(StandDeparture.PushBack, ParkedStandDeparture.Of(Parked("F8", new AtParkingPhase()), PushTargetPlannerTests.Sfo(), Sidecars));

    /// <summary>The stand name matches as the layout's lookup does, whatever its case.</summary>
    [Fact]
    public void Ga20_NamedInLowerCase_IsTaxiOut() =>
        Assert.Equal(StandDeparture.TaxiOut, ParkedStandDeparture.Of(Parked("ga20", new AtParkingPhase()), Oak(), Sidecars));

    /// <summary>A sidecar override wins over the geometry both ways, as it does for the push-target cache.</summary>
    [Fact]
    public void SidecarOverride_FlipsAStandBothWays()
    {
        AirportGroundLayout layout = Oak();
        var flipped = new AirportSidecarCatalog([
            new AirportSidecar("KOAK")
            {
                StandDepartureOverrides = new Dictionary<string, StandDeparture>(StringComparer.OrdinalIgnoreCase)
                {
                    ["26"] = StandDeparture.TaxiOut,
                    ["ga20"] = StandDeparture.PushBack,
                },
            },
        ]);

        Assert.Equal(StandDeparture.TaxiOut, ParkedStandDeparture.Of(Parked("26", new AtParkingPhase()), layout, flipped));
        Assert.Equal(StandDeparture.PushBack, ParkedStandDeparture.Of(Parked("GA20", new AtParkingPhase()), layout, flipped));
    }

    /// <summary>No committed airport GeoJSON carries a helipad feature, so the helipad case is pinned on a hand-built pad.</summary>
    [Fact]
    public void Helipad_AtParking_IsTaxiOut()
    {
        var layout = new AirportGroundLayout { AirportId = "KOAK" };
        layout.Nodes[1] = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.72, -122.22),
            Type = GroundNodeType.Helipad,
            Name = "H1",
            TrueHeading = new TrueHeading(90),
        };

        Assert.Equal(StandDeparture.TaxiOut, ParkedStandDeparture.Of(Parked("H1", new AtParkingPhase()), layout, Sidecars));
    }

    /// <summary>An aircraft parked on a ramp spot is on no stand.</summary>
    [Fact]
    public void RampSpot_AtParking_IsNull()
    {
        AirportGroundLayout layout = Oak();
        GroundNode spot = layout.Nodes.Values.First(n =>
            (n.Type == GroundNodeType.Spot)
            && (n.Name is { Length: > 0 } name)
            && (layout.FindParkingByName(name) is null)
            && (layout.FindHelipadByName(name) is null)
        );

        Assert.Null(ParkedStandDeparture.Of(Parked(spot.Name!, new AtParkingPhase()), layout, Sidecars));
    }

    [Fact]
    public void UnknownStandName_IsNull() => Assert.Null(ParkedStandDeparture.Of(Parked("NOSUCHSTAND", new AtParkingPhase()), Oak(), Sidecars));

    [Fact]
    public void NoParkingSpot_IsNull() => Assert.Null(ParkedStandDeparture.Of(Parked("", new AtParkingPhase()), Oak(), Sidecars));

    [Fact]
    public void NoLayout_IsNull() => Assert.Null(ParkedStandDeparture.Of(Parked("GA20", new AtParkingPhase()), null, Sidecars));

    /// <summary>Pushed off the stand, the aircraft still carries the stand's name, but it is no longer at it.</summary>
    [Fact]
    public void HoldingAfterPushback_OffTheStand_IsNull() =>
        Assert.Null(ParkedStandDeparture.Of(Parked("GA20", new HoldingAfterPushbackPhase()), Oak(), Sidecars));

    [Fact]
    public void NoPhases_IsNull()
    {
        AircraftState aircraft = Parked("GA20", new AtParkingPhase());
        aircraft.Phases = null;

        Assert.Null(ParkedStandDeparture.Of(aircraft, Oak(), Sidecars));
    }

    private static AirportSidecarCatalog Sidecars => PushTargetPlannerTests.Sidecars.Value;

    private static AirportGroundLayout Oak() => PushTargetPlannerTests.Oak();

    private static AircraftState Parked(string parkingSpot, Phase phase)
    {
        var aircraft = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Position = new LatLon(37.72, -122.22),
            IsOnGround = true,
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(phase);
        aircraft.Ground.ParkingSpot = parkingSpot;
        return aircraft;
    }
}
