using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Live-traffic sessions: opening one at a position/airport (no authored scenario), and the LIVE / PAUSED / PLAYBACK
/// badge with the "Go Live" action that rejoins real time from a pause or a scrub. The server owns the state; this
/// partial only mirrors <c>IsLiveSession</c> from the three scenario-activation DTOs and derives the display from it.
/// </summary>
public partial class MainViewModel
{
    public const string LiveBadgeColor = "#2F6F3E";
    public const string PausedBadgeColor = "#8A6D1F";
    public const string PlaybackBadgeColor = "#553A8C";
    public const string FeedLostBadgeColor = "#8C2F2F";

    /// <summary>The active scenario was synthesized for a live-traffic session (server-computed; never set locally).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveSessionBadgeText))]
    [NotifyPropertyChangedFor(nameof(LiveSessionBadgeBrush))]
    [NotifyPropertyChangedFor(nameof(ShowGoLive))]
    [NotifyPropertyChangedFor(nameof(ShowPlaybackBadge))]
    [NotifyPropertyChangedFor(nameof(ShowTakeControl))]
    private bool _isLiveSession;

    /// <summary>Mentor/instructor in a room, on a server whose feed is configured.</summary>
    public bool CanStartLiveSession => CanLoadScenario && LiveTrafficAvailable;

    public string StartLiveSessionToolTip =>
        LiveTrafficAvailable ? "Open a room at a position with real traffic from the SWIM feed." : "Live traffic is not enabled on this server.";

    /// <summary>LIVE while running on the feed; PAUSED / PLAYBACK while the room is off real time; feed loss is called out.</summary>
    public string LiveSessionBadgeText => DescribeLiveSession(IsLiveSession, IsPaused, IsPlaybackMode, LiveTrafficStatus?.Connected == true);

    public string LiveSessionBadgeBrush =>
        IsPlaybackMode ? PlaybackBadgeColor
        : IsPaused ? PausedBadgeColor
        : LiveTrafficStatus?.Connected == true ? LiveBadgeColor
        : FeedLostBadgeColor;

    /// <summary>The generic PLAYBACK badge belongs to scenario rooms; a live session shows its own badge instead.</summary>
    public bool ShowPlaybackBadge => IsPlaybackMode && !IsLiveSession;

    public bool ShowTakeControl => IsPlaybackMode && !IsLiveSession;

    /// <summary>"Go Live" is offered whenever a live session is off real time — in playback or paused.</summary>
    public bool ShowGoLive => IsLiveSession && (IsPlaybackMode || IsPaused);

    public static string DescribeLiveSession(bool isLiveSession, bool isPaused, bool isPlaybackMode, bool feedConnected)
    {
        if (!isLiveSession)
        {
            return "";
        }

        if (isPlaybackMode)
        {
            return "PLAYBACK";
        }

        if (isPaused)
        {
            return "PAUSED";
        }

        return feedConnected ? "LIVE" : "LIVE · feed lost";
    }

    /// <summary>
    /// Opens a live session at the picked position/airport. On success the returned load result is applied like any
    /// scenario load, the choice is remembered for the next picker, and live weather is fetched so the room's METARs
    /// and winds match the real traffic on the scope.
    /// </summary>
    public async Task StartLiveSessionAsync(LiveSessionChoice choice)
    {
        try
        {
            StatusText = $"Starting live session at {choice.PositionLabel} / {choice.AirportId}…";
            var result = await _connection.StartLiveSessionAsync(new LiveSessionRequestDto(choice.PositionId, choice.AirportId, choice.CeilingFt));
            if (!result.Success)
            {
                var reason = result.Warnings.FirstOrDefault() ?? "Live session refused";
                _log.LogWarning("Live session refused: {Reason}", reason);
                StatusText = reason;
                AddSystemEntry($"Live session refused: {reason}");
                return;
            }

            ApplyScenarioResult(result);
            _preferences.SetLastLiveSession(choice);
            _log.LogInformation("Live session started: '{Name}' ({Id})", result.Name, result.ScenarioId);
            StatusText = $"Live session: {result.Name}";
            AddSystemEntry($"Live session started: {result.Name}");

            if (LoadLiveWeatherCommand.CanExecute(null))
            {
                await LoadLiveWeatherCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Start live session error");
            ReportScenarioActionFailure("Start live session", ex.Message);
        }
    }

    /// <summary>Leaves playback / pause and rejoins real time. No confirmation: the tape's future is only feed samples.</summary>
    [RelayCommand]
    private async Task GoLive()
    {
        try
        {
            var result = await _connection.GoLiveAsync();
            if (!result.Success)
            {
                StatusText = result.Message ?? "Go live refused";
                return;
            }

            IsPlaybackMode = false;
            PlaybackTapeEnd = 0;
            IsPaused = false;
            OnPropertyChanged(nameof(TapeEndDisplay));
            OnPropertyChanged(nameof(TimelineMaximum));
            OnPropertyChanged(nameof(PlayPauseIcon));
            StatusText = "Live";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GoLive failed");
            StatusText = $"Go live error: {ex.Message}";
        }
    }
}
