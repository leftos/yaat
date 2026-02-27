using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

public sealed class ServerConnection : IAsyncDisposable
{
    private readonly ILogger _log =
        AppLog.CreateLogger<ServerConnection>();

    private HubConnection? _connection;

    public event Action<AircraftDto>? AircraftUpdated;
    public event Action<string>? AircraftDeleted;
    public event Action<AircraftDto>? AircraftSpawned;
    public event Action<bool, int>? SimulationStateChanged;

    public bool IsConnected =>
        _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(string serverUrl)
    {
        if (_connection is not null)
        {
            await DisconnectAsync();
        }

        var hubUrl = serverUrl.TrimEnd('/') + "/hubs/training";
        _log.LogInformation("Connecting to {Url}", hubUrl);

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _connection.On<AircraftDto>(
            "AircraftUpdated",
            dto => AircraftUpdated?.Invoke(dto));

        _connection.On<string>(
            "AircraftDeleted",
            cs => AircraftDeleted?.Invoke(cs));

        _connection.On<AircraftDto>(
            "AircraftSpawned",
            dto => AircraftSpawned?.Invoke(dto));

        _connection.On<bool, int>(
            "SimulationStateChanged",
            (paused, rate) =>
                SimulationStateChanged?.Invoke(
                    paused, rate));

        await _connection.StartAsync();
        _log.LogInformation("Connected");
    }

    public async Task DisconnectAsync()
    {
        if (_connection is null)
        {
            return;
        }

        await _connection.DisposeAsync();
        _connection = null;
        _log.LogInformation("Disconnected");
    }

    public async Task<List<AircraftDto>> GetAircraftListAsync()
    {
        if (_connection is null)
        {
            throw new InvalidOperationException(
                "Not connected.");
        }

        return await _connection
            .InvokeAsync<List<AircraftDto>>(
                "GetAircraftList");
    }

    public async Task SpawnAircraftAsync(SpawnAircraftDto dto)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException(
                "Not connected.");
        }

        await _connection.InvokeAsync("SpawnAircraft", dto);
    }

    public async Task<LoadScenarioResultDto>
        LoadScenarioAsync(string scenarioJson)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException(
                "Not connected.");
        }

        return await _connection
            .InvokeAsync<LoadScenarioResultDto>(
                "LoadScenario", scenarioJson);
    }

    public async Task<CommandResultDto>
        SendCommandAsync(string callsign, string command)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException(
                "Not connected.");
        }

        return await _connection
            .InvokeAsync<CommandResultDto>(
                "SendCommand", callsign, command);
    }

    public async Task DeleteAircraftAsync(string callsign)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException(
                "Not connected.");
        }

        await _connection.InvokeAsync(
            "DeleteAircraft", callsign);
    }

    public async Task PauseSimulationAsync()
    {
        if (_connection is null)
        {
            throw new InvalidOperationException(
                "Not connected.");
        }

        await _connection.InvokeAsync("PauseSimulation");
    }

    public async Task ResumeSimulationAsync()
    {
        if (_connection is null)
        {
            throw new InvalidOperationException(
                "Not connected.");
        }

        await _connection.InvokeAsync("ResumeSimulation");
    }

    public async Task SetSimRateAsync(int rate)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException(
                "Not connected.");
        }

        await _connection.InvokeAsync("SetSimRate", rate);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
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
    uint BeaconCode,
    string TransponderMode,
    double VerticalSpeed,
    double? AssignedHeading,
    double? AssignedAltitude,
    double? AssignedSpeed,
    string Departure,
    string Destination,
    string Route,
    string FlightRules,
    string Status);

public record SpawnAircraftDto(
    string Callsign,
    string AircraftType,
    double Latitude,
    double Longitude,
    double Heading,
    double Altitude,
    double GroundSpeed);

public record LoadScenarioResultDto(
    bool Success,
    string Name,
    int AircraftCount,
    int DelayedCount,
    List<string> Warnings,
    List<AircraftDto> AllAircraft);

public record CommandResultDto(
    bool Success,
    string? Message);
