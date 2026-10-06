using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests;

/// <summary>
/// The turn-about shape the server sends by name (<see cref="AircraftDto.TaxiTurnAboutShape"/>) and its target node land on
/// <see cref="AircraftModel.TaxiTurnAboutShape"/> and <see cref="AircraftModel.TaxiTurnAboutTargetNodeId"/> on both the
/// create and the update path; a name this client does not know reads as no turn about.
/// </summary>
public class AircraftModelTaxiTurnAboutShapeTests
{
    [Fact]
    public void KnownShape_ReachesTheModel_OnCreateAndUpdate()
    {
        var model = AircraftModel.FromDto(Dto("FromFarEnd", 42));
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, model.TaxiTurnAboutShape);
        Assert.Equal(42, model.TaxiTurnAboutTargetNodeId);

        model.UpdateFromDto(Dto("InPlace", 43));
        Assert.Equal(TaxiTurnAboutShape.InPlace, model.TaxiTurnAboutShape);
        Assert.Equal(43, model.TaxiTurnAboutTargetNodeId);

        model.UpdateFromDto(Dto(null, null));
        Assert.Equal(TaxiTurnAboutShape.None, model.TaxiTurnAboutShape);
        Assert.Null(model.TaxiTurnAboutTargetNodeId);
    }

    [Theory]
    [InlineData("Sideways")]
    [InlineData("fromfarend")]
    [InlineData("7")]
    public void UnknownShape_ReadsAsNone_OnCreateAndUpdate(string wire)
    {
        var model = AircraftModel.FromDto(Dto(wire, 42));
        Assert.Equal(TaxiTurnAboutShape.None, model.TaxiTurnAboutShape);

        model.UpdateFromDto(Dto("FromFarEnd", 42));
        model.UpdateFromDto(Dto(wire, 42));
        Assert.Equal(TaxiTurnAboutShape.None, model.TaxiTurnAboutShape);

        model.UpdateFromDto(Dto(wire, 42));
        Assert.Equal(TaxiTurnAboutShape.None, model.TaxiTurnAboutShape);
    }

    private static AircraftDto Dto(string? shape, int? targetNodeId) =>
        new(
            Callsign: "UAL123",
            AircraftType: "C172",
            Latitude: 37.72,
            Longitude: -122.22,
            Heading: 90,
            Altitude: 0,
            GroundSpeed: 0,
            BeaconCode: 1200,
            TransponderMode: "C",
            IsIdenting: false,
            VerticalSpeed: 0,
            AssignedHeading: null,
            AssignedAltitude: null,
            AssignedSpeed: null,
            Departure: "KOAK",
            Destination: "KSFO",
            Route: "",
            FlightRules: "VFR",
            Status: "",
            TaxiTurnAboutShape: shape,
            TaxiTurnAboutTargetNodeId: targetNodeId
        );
}
