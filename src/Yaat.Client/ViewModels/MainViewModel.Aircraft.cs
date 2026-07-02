using Microsoft.Extensions.Logging;
using Yaat.Client.Core.Services;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views;
using Yaat.Sim.Commands;

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

    /// <summary>
    /// Adds an amber Warning terminal entry attributed to a specific aircraft and (when the user has
    /// opted in) an anchored speech bubble on it. Unlike <see cref="AddWarningEntry"/> the callsign is
    /// set, so <see cref="MaybeAttachSpeechBubble"/> can anchor the bubble.
    /// </summary>
    public void AddAircraftWarning(string callsign, string message)
    {
        AddTerminalEntry(
            new TerminalEntry
            {
                Timestamp = DateTime.Now,
                Initials = "",
                Kind = TerminalEntryKind.Warning,
                Callsign = callsign,
                Message = message,
            }
        );
        MaybeAttachSpeechBubble(TerminalEntryKind.Warning, callsign, message);
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
    /// When the user has opted in to speech bubbles (Settings → Display → Overlays), attach a
    /// transient bubble to the matching aircraft for SAY-family and RPO pilot-speech entries
    /// (suppressed in solo-training mode, where TTS already covers pilot transmissions) and —
    /// when the WARN opt-in is on — amber WARN-channel entries (shown in solo mode too, since
    /// warnings are controller-facing and not TTS'd). The radar and ground renderers pick it up
    /// on the next frame. A second message for the same callsign replaces the existing bubble
    /// (single slot, no queue). Unknown / empty callsigns are dropped silently. Gating is
    /// delegated to <see cref="AircraftSpeechBubble.TryBuild"/> so the predicate can be
    /// unit-tested without fabricating a full view-model.
    /// </summary>
    private void MaybeAttachSpeechBubble(TerminalEntryKind kind, string callsign, string message)
    {
        if (string.IsNullOrEmpty(callsign))
        {
            return;
        }
        var bubble = AircraftSpeechBubble.TryBuild(
            _preferences.ShowSpeechBubbles,
            _preferences.ShowWarningSpeechBubbles,
            SessionSoloTrainingMode,
            kind,
            message,
            _preferences.SpeechBubbleDurationMultiplier,
            _preferences.SpeechBubblesStayUntilClicked,
            DateTime.UtcNow
        );
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

    internal void OnAircraftUpdated(AircraftDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var existing = FindAircraft(dto.Callsign);
            if (existing is not null)
            {
                var wasDelayed = existing.IsDelayed;
                var wasUnsupported = existing.IsUnsupported;
                var wasGhostOverlay = existing.IsGhostOverlay;
                var wasOnGround = existing.IsOnGround;
                existing.UpdateFromDto(dto, ComputeDistance);
                ApplyAutoClearedToLand(existing);
                ApplyDelayedSpawnTransition(wasDelayed, existing.IsDelayed);
                EvaluateCfrAlerts(existing, wasOnGround);
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
                EvaluateCfrAlerts(model, wasOnGround: model.IsOnGround);
                if (model.IsDelayed)
                {
                    PendingDelayedSpawnCount++;
                }
                // A full refresh re-filters and re-sorts the view. The DataGridCollectionView's
                // incremental sorted-insert on Add mis-places a new row when the active filter has
                // shrunk the view (it appends at the bottom instead of sorting in), so rebuild.
                RefreshAircraftView();
            }

            Radar.RefreshShownPaths();
            Ground.RefreshShownTaxiRoutes();
        });
    }

    private readonly CfrAlertMonitor _cfrMonitor = new();

    /// <summary>
    /// Per-second sweep that raises the "release window expired while still on the ground" alert. A held
    /// departure stops broadcasting once stationary, so this can't ride the <see cref="OnAircraftUpdated"/>
    /// stream. Wired to a 1 s <c>DispatcherTimer</c> in the constructor.
    /// </summary>
    internal void SweepCfrExpiry()
    {
        foreach (var ac in Aircraft)
        {
            if (ac.CfrWindowStartUtc is not null)
            {
                EvaluateCfrAlerts(ac, wasOnGround: ac.IsOnGround);
            }
        }
    }

    /// <summary>
    /// Evaluates one aircraft's Call-For-Release window against real UTC and surfaces any newly-tripped
    /// violation as an instructor warning. <paramref name="wasOnGround"/> is the ground state before the
    /// current update (equal to the current state for the periodic sweep, so only expiry-while-grounded
    /// can fire there). Alert-only — never affects the simulation (GitHub issue #230).
    /// </summary>
    private void EvaluateCfrAlerts(AircraftModel ac, bool wasOnGround)
    {
        var kind = _cfrMonitor.Evaluate(ac.Callsign, ac.CfrWindowStartUtc, ac.CfrWindowEndUtc, ac.IsOnGround, wasOnGround, DateTime.UtcNow);
        if (kind is { } fired)
        {
            AddAircraftWarning(ac.Callsign, FormatCfrAlert(fired, ac));
        }
    }

    private static string FormatCfrAlert(CfrAlertKind kind, AircraftModel ac)
    {
        var window = $"{ac.CfrWindowStartUtc:HHmm}–{ac.CfrWindowEndUtc:HHmm}Z";
        return kind switch
        {
            CfrAlertKind.EarlyTakeoff => $"{ac.Callsign} departed before its release window opened ({window})",
            CfrAlertKind.LateTakeoff => $"{ac.Callsign} departed after its release window expired ({window})",
            CfrAlertKind.ExpiredGrounded => $"{ac.Callsign} release window expired ({window}) — still holding for release",
            _ => $"{ac.Callsign} release window alert ({window})",
        };
    }

    private void OnAircraftDeleted(string callsign)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _cfrMonitor.Remove(callsign);
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
                // See OnAircraftUpdated: rebuild so a new row sorts into place under the active filter.
                RefreshAircraftView();
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
        // Drives the radar / tower-cab "NoLndgClnc" datablock suppression. The Info-column status
        // is computed server-side (AircraftStatusDescriber) and already accounts for the session
        // auto-clear setting, so no client-side status recompute is needed here.
        model.IsAutoClearedToLand = _isAutoClearedToLand;
    }
}
