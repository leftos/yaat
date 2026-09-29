using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Situation;

namespace Yaat.Client.Tests;

/// <summary>
/// The server-classified situation on <see cref="AircraftDto"/>: it crosses the hub's JSON as a number and lands on
/// <see cref="AircraftModel.Situation"/> on both the create and the update path.
/// </summary>
public class AircraftDtoSituationTests
{
    private static readonly JsonSerializerOptions WireOptions = HubPayloadOptions();

    /// <summary>The payload options the client's hub connection deserializes with: SignalR's defaults plus the hub contexts.</summary>
    private static JsonSerializerOptions HubPayloadOptions()
    {
        JsonSerializerOptions options = new JsonHubProtocolOptions().PayloadSerializerOptions;
        ServerConnection.UseHubJsonContexts(options);
        return options;
    }

    [Fact]
    public void Json_RoundTrip_CarriesSituationAsNumber()
    {
        string json = JsonSerializer.Serialize(Dto(AircraftSituation.Final), WireOptions);

        using var doc = JsonDocument.Parse(json);
        JsonElement situation = doc.RootElement.GetProperty("situation");
        Assert.Equal(JsonValueKind.Number, situation.ValueKind);
        Assert.Equal((int)AircraftSituation.Final, situation.GetInt32());

        AircraftDto? back = JsonSerializer.Deserialize<AircraftDto>(json, WireOptions);
        Assert.NotNull(back);
        Assert.Equal(AircraftSituation.Final, back.Situation);
    }

    [Fact]
    public void Json_MissingSituation_DeserializesAsUnknown()
    {
        string json = JsonSerializer.Serialize(Dto(AircraftSituation.Taxiing), WireOptions).Replace("\"situation\":4,", "", StringComparison.Ordinal);
        Assert.DoesNotContain("\"situation\"", json, StringComparison.Ordinal);

        AircraftDto? back = JsonSerializer.Deserialize<AircraftDto>(json, WireOptions);

        Assert.NotNull(back);
        Assert.Equal(AircraftSituation.Unknown, back.Situation);
    }

    [Fact]
    public void Model_CopiesSituation_OnCreateAndUpdate()
    {
        var model = AircraftModel.FromDto(Dto(AircraftSituation.HoldingShort));
        Assert.Equal(AircraftSituation.HoldingShort, model.Situation);

        model.UpdateFromDto(Dto(AircraftSituation.LinedUp));
        Assert.Equal(AircraftSituation.LinedUp, model.Situation);
    }

    private static AircraftDto Dto(AircraftSituation situation) =>
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
            Situation: situation
        );
}
