using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ServerConnection _connection = new();
    private readonly UserPreferences _preferences = new();

    public UserPreferences Preferences => _preferences;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SpawnCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadScenarioCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommandCommand))]
    [NotifyCanExecuteChangedFor(nameof(TogglePauseCommand))]
    private bool _isConnected;

    [ObservableProperty]
    private string _serverUrl = "http://localhost:5000";

    [ObservableProperty]
    private string _statusText = "Disconnected";

    [ObservableProperty]
    private string _scenarioFilePath = "";

    [ObservableProperty]
    private string _commandText = "";

    [ObservableProperty]
    private AircraftModel? _selectedAircraft;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private int _simRate = 1;

    [ObservableProperty]
    private string _commandSchemeName = "ATCTrainer";

    public ObservableCollection<AircraftModel> Aircraft
        { get; } = [];

    public ObservableCollection<string> CommandHistory
        { get; } = [];

    public MainViewModel()
    {
        _connection.AircraftUpdated += OnAircraftUpdated;
        _connection.AircraftDeleted += OnAircraftDeleted;
        _connection.AircraftSpawned += OnAircraftSpawned;
        _connection.SimulationStateChanged +=
            OnSimulationStateChanged;

        RefreshCommandScheme();
    }

    [RelayCommand(CanExecute = nameof(CanToggleConnect))]
    private async Task ConnectAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
            return;
        }

        try
        {
            StatusText = "Connecting...";
            await _connection.ConnectAsync(ServerUrl);
            IsConnected = true;
            StatusText = "Connected";

            var list = await _connection
                .GetAircraftListAsync();
            Aircraft.Clear();
            foreach (var dto in list)
                Aircraft.Add(DtoToModel(dto));
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            IsConnected = false;
        }
    }

    private bool CanToggleConnect() => true;

    private async Task DisconnectAsync()
    {
        await _connection.DisconnectAsync();
        IsConnected = false;
        StatusText = "Disconnected";
        Aircraft.Clear();
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task SpawnAsync()
    {
        try
        {
            var dto = new SpawnAircraftDto(
                Callsign: $"TST{Random.Shared.Next(100, 999)}",
                AircraftType: "B738",
                Latitude: 37.72
                    + Random.Shared.NextDouble() * 0.1,
                Longitude: -122.22
                    - Random.Shared.NextDouble() * 0.1,
                Heading: Random.Shared.Next(0, 360),
                Altitude: Random.Shared.Next(3000, 15000),
                GroundSpeed: Random.Shared.Next(180, 350));

            await _connection.SpawnAircraftAsync(dto);
        }
        catch (Exception ex)
        {
            StatusText = $"Spawn error: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task LoadScenarioAsync()
    {
        if (string.IsNullOrWhiteSpace(ScenarioFilePath))
        {
            StatusText = "No scenario file selected";
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(
                ScenarioFilePath);
            var result = await _connection
                .LoadScenarioAsync(json);

            if (result.Success)
            {
                StatusText = $"Loaded '{result.Name}': " +
                    $"{result.AircraftCount} aircraft" +
                    (result.DelayedCount > 0
                        ? $" ({result.DelayedCount} delayed)"
                        : "");
            }
            else
            {
                StatusText = "Scenario load failed";
            }

            foreach (var w in result.Warnings)
            {
                AddHistory($"[WARN] {w}");
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Load error: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task SendCommandAsync()
    {
        var text = CommandText.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        var scheme = _preferences.CommandScheme;
        var parsed = CommandSchemeParser.Parse(text, scheme);

        if (parsed is null)
        {
            StatusText = $"Unknown command: {text}";
            return;
        }

        // Global commands don't need aircraft selection
        if (parsed.Type == CanonicalCommandType.Pause)
        {
            await _connection.PauseSimulationAsync();
            AddHistory("PAUSE");
            CommandText = "";
            return;
        }
        if (parsed.Type == CanonicalCommandType.Unpause)
        {
            await _connection.ResumeSimulationAsync();
            AddHistory("UNPAUSE");
            CommandText = "";
            return;
        }
        if (parsed.Type == CanonicalCommandType.SimRate)
        {
            if (int.TryParse(parsed.Argument, out var rate))
            {
                await _connection.SetSimRateAsync(rate);
                AddHistory($"SIMRATE {rate}");
            }
            CommandText = "";
            return;
        }

        // Aircraft-targeted commands
        if (SelectedAircraft is null)
        {
            StatusText = "Select an aircraft first";
            return;
        }

        var canonical = CommandSchemeParser.ToCanonical(
            parsed.Type, parsed.Argument);

        try
        {
            var result = await _connection
                .SendCommandAsync(
                    SelectedAircraft.Callsign, canonical);

            var entry = $"{SelectedAircraft.Callsign} {text}";
            if (result.Success)
            {
                AddHistory(entry);
            }
            else
            {
                AddHistory($"{entry} — {result.Message}");
            }

            CommandText = "";
        }
        catch (Exception ex)
        {
            StatusText = $"Command error: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task TogglePauseAsync()
    {
        try
        {
            if (IsPaused)
                await _connection.ResumeSimulationAsync();
            else
                await _connection.PauseSimulationAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Pause error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SetSimRateAsync(string rateStr)
    {
        if (!int.TryParse(rateStr, out var rate))
            return;

        try
        {
            await _connection.SetSimRateAsync(rate);
        }
        catch (Exception ex)
        {
            StatusText = $"SimRate error: {ex.Message}";
        }
    }

    public void RefreshCommandScheme()
    {
        CommandSchemeName =
            CommandScheme.DetectPresetName(
                _preferences.CommandScheme) ?? "Custom";
    }

    private void OnAircraftUpdated(AircraftDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var existing = FindAircraft(dto.Callsign);
            if (existing is not null)
            {
                UpdateModel(existing, dto);
            }
            else
            {
                Aircraft.Add(DtoToModel(dto));
            }
        });
    }

    private void OnAircraftDeleted(string callsign)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var ac = FindAircraft(callsign);
            if (ac is not null)
                Aircraft.Remove(ac);
        });
    }

    private void OnAircraftSpawned(AircraftDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var existing = FindAircraft(dto.Callsign);
            if (existing is null)
                Aircraft.Add(DtoToModel(dto));
        });
    }

    private void OnSimulationStateChanged(
        bool paused, int rate)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsPaused = paused;
            SimRate = rate;
        });
    }

    private void AddHistory(string entry)
    {
        CommandHistory.Insert(0, entry);
        while (CommandHistory.Count > 50)
            CommandHistory.RemoveAt(
                CommandHistory.Count - 1);
    }

    private AircraftModel? FindAircraft(string callsign)
    {
        foreach (var a in Aircraft)
        {
            if (a.Callsign == callsign)
                return a;
        }
        return null;
    }

    private static void UpdateModel(
        AircraftModel model, AircraftDto dto)
    {
        model.Latitude = dto.Latitude;
        model.Longitude = dto.Longitude;
        model.Heading = dto.Heading;
        model.Altitude = dto.Altitude;
        model.GroundSpeed = dto.GroundSpeed;
        model.BeaconCode = dto.BeaconCode;
        model.TransponderMode = dto.TransponderMode;
        model.VerticalSpeed = dto.VerticalSpeed;
        model.AssignedHeading = dto.AssignedHeading;
        model.AssignedAltitude = dto.AssignedAltitude;
        model.AssignedSpeed = dto.AssignedSpeed;
        model.Departure = dto.Departure;
        model.Destination = dto.Destination;
        model.Route = dto.Route;
        model.FlightRules = dto.FlightRules;
    }

    private static AircraftModel DtoToModel(AircraftDto dto)
    {
        return new AircraftModel
        {
            Callsign = dto.Callsign,
            AircraftType = dto.AircraftType,
            Latitude = dto.Latitude,
            Longitude = dto.Longitude,
            Heading = dto.Heading,
            Altitude = dto.Altitude,
            GroundSpeed = dto.GroundSpeed,
            BeaconCode = dto.BeaconCode,
            TransponderMode = dto.TransponderMode,
            VerticalSpeed = dto.VerticalSpeed,
            AssignedHeading = dto.AssignedHeading,
            AssignedAltitude = dto.AssignedAltitude,
            AssignedSpeed = dto.AssignedSpeed,
            Departure = dto.Departure,
            Destination = dto.Destination,
            Route = dto.Route,
            FlightRules = dto.FlightRules,
        };
    }
}
