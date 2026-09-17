using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// The tug pose the ground view draws from: <see cref="AircraftModel.TowbarHeading"/> must follow
/// <c>AircraftDto.TowbarTrueHeadingDeg</c> on both the create path (<see cref="AircraftModel.FromDto"/>)
/// and the update path (<see cref="AircraftModel.UpdateFromDto"/>), and must go back to null when the
/// tug detaches.
/// </summary>
public class AircraftModelTowbarHeadingTests
{
    [Fact]
    public void FromDto_WithTowbarHeading_AppliesIt()
    {
        var model = AircraftModel.FromDto(Dto(towbarTrueHeadingDeg: 215.5));

        Assert.NotNull(model.TowbarHeading);
        Assert.Equal(215.5, model.TowbarHeading!.Value.Degrees, 6);
    }

    [Fact]
    public void FromDto_WithoutTowbarHeading_IsNull()
    {
        var model = AircraftModel.FromDto(Dto(towbarTrueHeadingDeg: null));

        Assert.Null(model.TowbarHeading);
    }

    [Fact]
    public void UpdateFromDto_WithTowbarHeading_AppliesIt()
    {
        var model = AircraftModel.FromDto(Dto(towbarTrueHeadingDeg: null));

        model.UpdateFromDto(Dto(towbarTrueHeadingDeg: 42.0));

        Assert.NotNull(model.TowbarHeading);
        Assert.Equal(42.0, model.TowbarHeading!.Value.Degrees, 6);
    }

    [Fact]
    public void UpdateFromDto_TugDetached_ClearsIt()
    {
        var model = AircraftModel.FromDto(Dto(towbarTrueHeadingDeg: 42.0));

        model.UpdateFromDto(Dto(towbarTrueHeadingDeg: null));

        Assert.Null(model.TowbarHeading);
    }

    private static AircraftDto Dto(double? towbarTrueHeadingDeg) =>
        new(
            Callsign: "UAL123",
            AircraftType: "B738",
            Latitude: 37.62,
            Longitude: -122.38,
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
            Departure: "KSFO",
            Destination: "KLAX",
            Route: "",
            FlightRules: "IFR",
            Status: "",
            TowbarTrueHeadingDeg: towbarTrueHeadingDeg
        );
}
