using Microsoft.Extensions.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views;

namespace Yaat.Client.ViewModels;

/// <summary>
/// SignalR event handlers for aircraft updates, terminal entries, and membership changes.
/// </summary>
public partial class MainViewModel
{
    private void AddTerminalEntry(TerminalEntry entry)
    {
        _log.LogInformation("[Terminal] [{Kind}] {Initials} {Callsign}: {Message}", entry.Kind, entry.Initials, entry.Callsign, entry.Message);
        TerminalEntries.Add(entry);
        while (TerminalEntries.Count > 2000)
        {
            TerminalEntries.RemoveAt(0);
        }
    }

    public void AddSystemEntry(string message)
    {
        AddTerminalEntry(
            new TerminalEntry
            {
                Timestamp = DateTime.Now,
                Initials = "",
                Kind = TerminalEntryKind.System,
                Callsign = "",
                Message = message,
            }
        );
    }

    public void AddWarningEntry(string message)
    {
        AddTerminalEntry(
            new TerminalEntry
            {
                Timestamp = DateTime.Now,
                Initials = "",
                Kind = TerminalEntryKind.Warning,
                Callsign = "",
                Message = message,
            }
        );
    }

    private void OnTerminalEntry(TerminalBroadcastDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var kind = Enum.TryParse<TerminalEntryKind>(dto.Kind, out var k) ? k : TerminalEntryKind.System;
            AddTerminalEntry(
                new TerminalEntry
                {
                    Timestamp = dto.Timestamp.ToLocalTime(),
                    Initials = dto.Initials,
                    Kind = kind,
                    Callsign = dto.Callsign,
                    Message = dto.Message,
                }
            );

            if (kind == TerminalEntryKind.PilotSpeech && _preferences.RpoPilotSpeechAudibleAlert)
            {
                _pilotSpeechAlerts.PlayDing();
            }
        });
    }

    private void OnAircraftUpdated(AircraftDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var existing = FindAircraft(dto.Callsign);
            if (existing is not null)
            {
                var wasDelayed = existing.IsDelayed;
                existing.UpdateFromDto(dto, ComputeDistance);
                ApplyAutoClearedToLand(existing);
                if (existing.IsDelayed != wasDelayed)
                {
                    RefreshAircraftView();
                }
            }
            else
            {
                var model = AircraftModel.FromDto(dto, ComputeDistance);
                ApplyAutoClearedToLand(model);
                Aircraft.Add(model);
            }

            Radar.RefreshShownPaths();
            Ground.RefreshShownTaxiRoutes();
        });
    }

    private void OnAircraftDeleted(string callsign)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            FlightPlanEditorManager.Close();
            Radar.RemoveShownPath(callsign);
            Ground.RemoveShownTaxiRoute(callsign);
            var ac = FindAircraft(callsign);
            if (ac is not null)
            {
                Aircraft.Remove(ac);
            }
        });
    }

    private void OnAircraftSpawned(AircraftDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var existing = FindAircraft(dto.Callsign);
            if (existing is not null)
            {
                var wasDelayed = existing.IsDelayed;
                existing.UpdateFromDto(dto, ComputeDistance);
                ApplyAutoClearedToLand(existing);
                if (existing.IsDelayed != wasDelayed)
                {
                    RefreshAircraftView();
                }
            }
            else
            {
                var model = AircraftModel.FromDto(dto, ComputeDistance);
                ApplyAutoClearedToLand(model);
                Aircraft.Add(model);
            }
        });
    }

    private void OnSimulationStateChanged(bool paused, int rate, double elapsed, bool isPlayback, double tapeEnd)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ApplySimState(paused, rate, elapsed, isPlayback, tapeEnd);
        });
    }

    private AircraftModel? FindAircraft(string callsign)
    {
        foreach (var a in Aircraft)
        {
            if (a.Callsign == callsign)
            {
                return a;
            }
        }
        return null;
    }

    private void ApplyAutoClearedToLand(AircraftModel model)
    {
        model.IsAutoClearedToLand = _isAutoClearedToLand;
        model.ComputeSmartStatus();
    }
}
