using Microsoft.AspNetCore.SignalR.Client;

namespace Yaat.Client.Services;

public sealed class ServerConnection : IAsyncDisposable
{
    private HubConnection? _connection;

    public event Action<AircraftDto>? AircraftUpdated;

    public bool IsConnected =>
        _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(string serverUrl)
    {
        if (_connection is not null)
            await DisconnectAsync();

        var hubUrl = serverUrl.TrimEnd('/') + "/hubs/training";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _connection.On<AircraftDto>(
            "AircraftUpdated",
            dto => AircraftUpdated?.Invoke(dto));

        await _connection.StartAsync();
    }

    public async Task DisconnectAsync()
    {
        if (_connection is null)
            return;

        await _connection.DisposeAsync();
        _connection = null;
    }

    public async Task<List<AircraftDto>> GetAircraftListAsync()
    {
        if (_connection is null)
            throw new InvalidOperationException("Not connected.");

        return await _connection
            .InvokeAsync<List<AircraftDto>>("GetAircraftList");
    }

    public async Task SpawnAircraftAsync(SpawnAircraftDto dto)
    {
        if (_connection is null)
            throw new InvalidOperationException("Not connected.");

        await _connection.InvokeAsync("SpawnAircraft", dto);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}

public record AircraftDto(
    string Callsign,
    string AircraftType,
    double Latitude,
    double Longitude,
    double Heading,
    double Altitude,
    double GroundSpeed,
    uint BeaconCode);

public record SpawnAircraftDto(
    string Callsign,
    string AircraftType,
    double Latitude,
    double Longitude,
    double Heading,
    double Altitude,
    double GroundSpeed);
