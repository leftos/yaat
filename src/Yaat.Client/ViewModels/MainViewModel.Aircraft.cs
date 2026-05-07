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
            // SAY-class verbs (SayPosition, SaySpeed, SayMach, SayAltitude, SayHeading,
            // SayExpectedApproach) and the freeform "Say" all belong in the Say channel;
            // the dispatcher uses verb-specific Kind strings so future filters can split
            // them apart, but the visible categorization is uniform.
            TerminalEntryKind kind;
            if (dto.Kind.StartsWith("Say", StringComparison.Ordinal))
            {
                kind = TerminalEntryKind.Say;
            }
            else if (!Enum.TryParse(dto.Kind, out kind))
            {
                kind = TerminalEntryKind.System;
            }
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

    private void OnPilotTransmissionReceived(PilotTransmissionBroadcastDto dto)
    {
        if (
            !_preferences.PilotVoiceEnabled
            || !SessionSoloTrainingMode
            || !_pilotVoice.IsAvailable
            || !string.Equals(dto.ScenarioId, ActiveScenarioId, StringComparison.Ordinal)
        )
        {
            return;
        }

        _pilotVoice.Enqueue(dto, _preferences.PilotVoiceVolume, _preferences.PilotVoiceRadioFxEnabled);
    }

    private void OnAircraftUpdated(AircraftDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var existing = FindAircraft(dto.Callsign);
            if (existing is not null)
            {
                var wasDelayed = existing.IsDelayed;
                var wasUnsupported = existing.IsUnsupported;
                existing.UpdateFromDto(dto, ComputeDistance);
                ApplyAutoClearedToLand(existing);
                if (existing.IsDelayed != wasDelayed || existing.IsUnsupported != wasUnsupported)
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
                var wasUnsupported = existing.IsUnsupported;
                existing.UpdateFromDto(dto, ComputeDistance);
                ApplyAutoClearedToLand(existing);
                if (existing.IsDelayed != wasDelayed || existing.IsUnsupported != wasUnsupported)
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
