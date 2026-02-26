using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Models;
using Yaat.Services;

namespace Yaat.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ServerConnection _connection = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SpawnCommand))]
    private bool _isConnected;

    [ObservableProperty]
    private string _serverUrl = "http://localhost:5000";

    [ObservableProperty]
    private string _statusText = "Disconnected";

    public ObservableCollection<AircraftModel> Aircraft { get; } = [];

    public MainViewModel()
    {
        _connection.AircraftUpdated += OnAircraftUpdated;
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

            var list = await _connection.GetAircraftListAsync();
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
                Latitude: 37.72 + Random.Shared.NextDouble() * 0.1,
                Longitude: -122.22 - Random.Shared.NextDouble() * 0.1,
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

    private void OnAircraftUpdated(AircraftDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var existing = FindAircraft(dto.Callsign);
            if (existing is not null)
            {
                existing.Latitude = dto.Latitude;
                existing.Longitude = dto.Longitude;
                existing.Heading = dto.Heading;
                existing.Altitude = dto.Altitude;
                existing.GroundSpeed = dto.GroundSpeed;
                existing.BeaconCode = dto.BeaconCode;
            }
            else
            {
                Aircraft.Add(DtoToModel(dto));
            }
        });
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
        };
    }
}
