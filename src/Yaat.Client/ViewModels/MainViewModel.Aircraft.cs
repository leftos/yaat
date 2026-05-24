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

            MaybeAttachSpeechBubble(kind, dto.Callsign, dto.Message);
        });
    }

    /// <summary>
    /// When the user has opted in to speech bubbles (Settings → Display → Overlays) and the
    /// session is not in solo-training mode (where TTS already covers pilot transmissions),
    /// attach a transient bubble to the matching aircraft for SAY-family and RPO pilot-speech
    /// entries. The radar and ground renderers pick it up on the next frame. A second message
    /// for the same callsign replaces the existing bubble (single slot, no queue). Unknown
    /// callsigns are dropped silently. Gating is delegated to <see cref="AircraftSpeechBubble.TryBuild"/>
    /// so the predicate can be unit-tested without fabricating a full view-model.
    /// </summary>
    private void MaybeAttachSpeechBubble(TerminalEntryKind kind, string callsign, string message)
    {
        if (string.IsNullOrEmpty(callsign))
        {
            return;
        }
        var bubble = AircraftSpeechBubble.TryBuild(_preferences.ShowSpeechBubbles, SessionSoloTrainingMode, kind, message, DateTime.UtcNow);
        if (bubble is null)
        {
            return;
        }
        var aircraft = FindAircraft(callsign);
        if (aircraft is null)
        {
            return;
        }
        aircraft.SpeechBubble = bubble;
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
                var wasGhostOverlay = existing.IsGhostOverlay;
                existing.UpdateFromDto(dto, ComputeDistance);
                ApplyAutoClearedToLand(existing);
                ApplyDelayedSpawnTransition(wasDelayed, existing.IsDelayed);
                if (existing.IsDelayed != wasDelayed || existing.IsUnsupported != wasUnsupported || existing.IsGhostOverlay != wasGhostOverlay)
                {
                    RefreshAircraftView();
                }
            }
            else
            {
                var model = AircraftModel.FromDto(dto, ComputeDistance);
                ApplyAutoClearedToLand(model);
                Aircraft.Add(model);
                if (model.IsDelayed)
                {
                    PendingDelayedSpawnCount++;
                }
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
                if (ac.IsDelayed && PendingDelayedSpawnCount > 0)
                {
                    PendingDelayedSpawnCount--;
                }
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
                var wasGhostOverlay = existing.IsGhostOverlay;
                existing.UpdateFromDto(dto, ComputeDistance);
                ApplyAutoClearedToLand(existing);
                ApplyDelayedSpawnTransition(wasDelayed, existing.IsDelayed);
                if (existing.IsDelayed != wasDelayed || existing.IsUnsupported != wasUnsupported || existing.IsGhostOverlay != wasGhostOverlay)
                {
                    RefreshAircraftView();
                }
            }
            else
            {
                var model = AircraftModel.FromDto(dto, ComputeDistance);
                ApplyAutoClearedToLand(model);
                Aircraft.Add(model);
                if (model.IsDelayed)
                {
                    PendingDelayedSpawnCount++;
                }
            }
        });
    }

    private void ApplyDelayedSpawnTransition(bool wasDelayed, bool isDelayed)
    {
        if (wasDelayed == isDelayed)
        {
            return;
        }
        if (wasDelayed && PendingDelayedSpawnCount > 0)
        {
            PendingDelayedSpawnCount--;
        }
        else if (isDelayed)
        {
            PendingDelayedSpawnCount++;
        }
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
