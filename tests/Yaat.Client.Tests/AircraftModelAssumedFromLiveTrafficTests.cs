using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// The marker the release-to-the-feed menu item keys on: <see cref="AircraftModel.AssumedFromLiveTraffic"/> must
/// follow <c>AircraftDto.AssumedFromLiveTraffic</c> on both the create path (<see cref="AircraftModel.FromDto"/>)
/// and the update path (<see cref="AircraftModel.UpdateFromDto"/>), including back to false when an auto-assume
/// is rolled back and the aircraft becomes a shadow again.
/// </summary>
public class AircraftModelAssumedFromLiveTrafficTests
{
    [Fact]
    public void FromDto_WithTheMarker_AppliesIt()
    {
        var model = AircraftModel.FromDto(Dto(assumedFromLiveTraffic: true));

        Assert.True(model.AssumedFromLiveTraffic);
    }

    [Fact]
    public void FromDto_WithoutTheMarker_IsFalse()
    {
        var model = AircraftModel.FromDto(Dto(assumedFromLiveTraffic: false));

        Assert.False(model.AssumedFromLiveTraffic);
    }

    [Fact]
    public void UpdateFromDto_WithTheMarker_AppliesIt()
    {
        var model = AircraftModel.FromDto(Dto(assumedFromLiveTraffic: false));

        model.UpdateFromDto(Dto(assumedFromLiveTraffic: true));

        Assert.True(model.AssumedFromLiveTraffic);
    }

    [Fact]
    public void UpdateFromDto_AssumeRolledBack_ClearsIt()
    {
        var model = AircraftModel.FromDto(Dto(assumedFromLiveTraffic: true));

        model.UpdateFromDto(Dto(assumedFromLiveTraffic: false));

        Assert.False(model.AssumedFromLiveTraffic);
    }

    private static AircraftDto Dto(bool assumedFromLiveTraffic) =>
        new(
            Callsign: "UAL123",
            AircraftType: "B738",
            Latitude: 37.62,
            Longitude: -122.38,
            Heading: 90,
            Altitude: 5000,
            GroundSpeed: 250,
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
            AssumedFromLiveTraffic: assumedFromLiveTraffic
        );
}
