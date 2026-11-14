using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// The track-coordination identities (<see cref="AircraftDto.OwnerFacilityId"/>, the handoff peer's facility, the position
/// that redirected the handoff, and the pending pointout's recipient and sender codes) land on <see cref="AircraftModel"/> on
/// both the create and the update path, where the quick-command rules compare them with the client's acting position.
/// </summary>
public class AircraftModelTrackCoordinationTests
{
    [Fact]
    public void AircraftModel_LandsTheTrackCoordinationIdentities()
    {
        var model = AircraftModel.FromDto(Dto("NCT", "SCT", "NCT_3E_APP", "3E", "NCT", "4R", "2S"));
        AssertIdentities(model, "NCT", "SCT", "NCT_3E_APP", "3E", "NCT", "4R", "2S");

        model.UpdateFromDto(Dto("OAK", "NCT", "OAK_TWR", "3O", "OAK", "2S", "4R"));
        AssertIdentities(model, "OAK", "NCT", "OAK_TWR", "3O", "OAK", "2S", "4R");

        model.UpdateFromDto(Dto(null, null, null, null, null, null, null));
        AssertIdentities(model, null, null, null, null, null, null, null);
    }

    private static void AssertIdentities(AircraftModel model, params string?[] expected)
    {
        string?[] actual =
        [
            model.OwnerFacilityId,
            model.HandoffPeerFacilityId,
            model.HandoffRedirectedByCallsign,
            model.HandoffRedirectedBySectorCode,
            model.HandoffRedirectedByFacilityId,
            model.PointoutRecipientCode,
            model.PointoutSenderCode,
        ];
        Assert.Equal(expected, actual);
    }

    private static AircraftDto Dto(params string?[] identities) =>
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
            OwnerFacilityId: identities[0],
            HandoffPeerFacilityId: identities[1],
            HandoffRedirectedByCallsign: identities[2],
            HandoffRedirectedBySectorCode: identities[3],
            HandoffRedirectedByFacilityId: identities[4],
            PointoutRecipientCode: identities[5],
            PointoutSenderCode: identities[6]
        );
}
