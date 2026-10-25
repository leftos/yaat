using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// The open pilot request (<see cref="AircraftDto.PendingPilotRequest"/>) and the initial-contact flag
/// (<see cref="AircraftDto.HasMadeInitialContact"/>) land on <see cref="AircraftModel"/> on both the create and the update path.
/// </summary>
public class AircraftModelPendingPilotRequestTests
{
    private static readonly PendingPilotRequestDto Request = new("Takeoff", "Standby", "ready for departure runway 28R.", 120, 150);

    [Fact]
    public void RequestAndInitialContact_ReachTheModel_OnCreateAndUpdate()
    {
        var model = AircraftModel.FromDto(Dto(Request, hasMadeInitialContact: true));
        Assert.Equal(Request, model.PendingPilotRequest);
        Assert.True(model.HasMadeInitialContact);

        model.UpdateFromDto(Dto(null, hasMadeInitialContact: false));
        Assert.Null(model.PendingPilotRequest);
        Assert.False(model.HasMadeInitialContact);

        model.UpdateFromDto(Dto(Request, hasMadeInitialContact: true));
        Assert.Equal(Request, model.PendingPilotRequest);
        Assert.True(model.HasMadeInitialContact);
    }

    private static AircraftDto Dto(PendingPilotRequestDto? request, bool hasMadeInitialContact) =>
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
            PendingPilotRequest: request,
            HasMadeInitialContact: hasMadeInitialContact
        );
}
