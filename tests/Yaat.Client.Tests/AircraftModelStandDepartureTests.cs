using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Tests;

/// <summary>
/// The stand departure the server sends by name (<see cref="AircraftDto.StandDeparture"/>) crosses the hub's JSON as a
/// string and lands on <see cref="AircraftModel.StandDeparture"/> on both the create and the update path; a name this
/// client does not know reads as no stand.
/// </summary>
public class AircraftModelStandDepartureTests
{
    [Fact]
    public void Json_StandDeparture_CrossesAsItsName()
    {
        JsonSerializerOptions options = new JsonHubProtocolOptions().PayloadSerializerOptions;
        ServerConnection.UseHubJsonContexts(options);

        string json = JsonSerializer.Serialize(Dto("TaxiOut"), options);

        Assert.Contains("\"standDeparture\":\"TaxiOut\"", json, StringComparison.Ordinal);
        Assert.Equal("TaxiOut", JsonSerializer.Deserialize<AircraftDto>(json, options)!.StandDeparture);
    }

    [Fact]
    public void KnownDeparture_ReachesTheModel_OnCreateAndUpdate()
    {
        var model = AircraftModel.FromDto(Dto("TaxiOut"));
        Assert.Equal(StandDeparture.TaxiOut, model.StandDeparture);

        model.UpdateFromDto(Dto("PushBack"));
        Assert.Equal(StandDeparture.PushBack, model.StandDeparture);

        model.UpdateFromDto(Dto(null));
        Assert.Null(model.StandDeparture);
    }

    [Fact]
    public void Parse_Either_ReachesTheModel_WithNoWarning()
    {
        var model = AircraftModel.FromDto(Dto("Either"));
        var log = new LevelCapturingLogger();

        Assert.Equal(StandDeparture.Either, model.StandDeparture);
        Assert.Equal(StandDeparture.Either, model.ParseStandDeparture("Either", log));
        Assert.Empty(log.Levels);
    }

    [Theory]
    [InlineData("Sideways")]
    [InlineData("taxiout")]
    [InlineData("7")]
    public void UnknownDeparture_ReadsAsNoStand_OnCreateAndUpdate(string wire)
    {
        var model = AircraftModel.FromDto(Dto(wire));
        Assert.Null(model.StandDeparture);

        model.UpdateFromDto(Dto("TaxiOut"));
        model.UpdateFromDto(Dto(wire));
        Assert.Null(model.StandDeparture);
    }

    [Fact]
    public void UnknownDeparture_WarnsOnce_ThenLogsAtDebug()
    {
        var model = AircraftModel.FromDto(Dto(null));
        var log = new LevelCapturingLogger();

        Assert.Null(model.ParseStandDeparture("Sideways", log));
        Assert.Null(model.ParseStandDeparture("Sideways", log));
        Assert.Null(model.ParseStandDeparture("Sideways", log));

        Assert.Equal([LogLevel.Warning, LogLevel.Debug, LogLevel.Debug], log.Levels);
    }

    private sealed class LevelCapturingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Levels.Add(logLevel);
    }

    private static AircraftDto Dto(string? standDeparture) =>
        new(
            Callsign: "N123AB",
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
            CurrentPhase: "At Parking",
            ParkingSpot: "GA20",
            StandDeparture: standDeparture
        );
}
