using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>One held departure shown in the rundown panel.</summary>
public sealed record HeldDepartureItem(string Callsign, string AircraftType, string Destination, string Status, bool IsGroundDeparture)
{
    public string Display => $"{Callsign}  {AircraftType} → {Destination}";
}

/// <summary>Held departures grouped under one armed airport in the rundown.</summary>
public sealed record RundownAirportGroup(string Airport, IReadOnlyList<HeldDepartureItem> Departures)
{
    public bool HasDepartures => Departures.Count > 0;
    public string Header => Departures.Count > 0 ? $"{Airport} ({Departures.Count})" : $"{Airport} (none held)";
}

/// <summary>
/// Client mirror of the hold-for-release rundown (<see cref="ServerConnection.HeldDeparturesChanged"/>
/// and the <c>RoomStateDto.Rundown</c> join seed). Server-authoritative display state — the client
/// never writes it back, so no echo guard is needed. Drives the rundown panel and its release actions.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Held departures grouped by armed airport (next-pending first within each group).</summary>
    public ObservableCollection<RundownAirportGroup> Rundown { get; } = [];

    /// <summary>True when at least one airport is armed for hold-for-release (drives the panel button's visibility).</summary>
    [ObservableProperty]
    private bool _holdForReleaseActive;

    private void OnHeldDeparturesChanged(HeldDeparturesChangedDto dto) => Dispatcher.UIThread.Post(() => ApplyRundown(dto.Rundown));

    public void ApplyRundown(RundownDto? rundown)
    {
        Rundown.Clear();
        if (rundown is null)
        {
            HoldForReleaseActive = false;
            return;
        }

        HoldForReleaseActive = rundown.ArmedAirports.Count > 0;

        var grouped = rundown
            .Held.GroupBy(h => h.Airport, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // One group per armed airport (in armed order), so the controller still sees a field that is
        // armed but currently has nothing held.
        foreach (var airport in rundown.ArmedAirports)
        {
            grouped.TryGetValue(airport, out var held);
            var items = (held ?? [])
                .Select(h => new HeldDepartureItem(h.Callsign, h.AircraftType, h.Destination, h.Status, h.IsGroundDeparture))
                .ToList();
            Rundown.Add(new RundownAirportGroup(airport, items));
        }
    }

    [RelayCommand]
    private async Task ReleaseDeparture(string? callsign)
    {
        if (!string.IsNullOrWhiteSpace(callsign))
        {
            await _connection.SendCommandAsync("", $"REL {callsign}", _preferences.UserInitials);
        }
    }

    [RelayCommand]
    private async Task ReleaseNextAtAirport(string? airport)
    {
        if (!string.IsNullOrWhiteSpace(airport))
        {
            await _connection.SendCommandAsync("", $"REL {airport}", _preferences.UserInitials);
        }
    }
}
