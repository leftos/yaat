using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>One active countdown timer shown in the timers panel.</summary>
public sealed record TimerItem(int Id, string? Callsign, string? Message, int RemainingSeconds, int TotalSeconds)
{
    /// <summary>Remaining time as mm:ss.</summary>
    public string Remaining => $"{RemainingSeconds / 60}:{RemainingSeconds % 60:D2}";

    /// <summary>The expiry text, prefixed with the callsign for a per-aircraft timer.</summary>
    public string Label
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(Message) ? "timer expired" : Message;
            return string.IsNullOrEmpty(Callsign) ? text : $"{Callsign}: {text}";
        }
    }
}

/// <summary>
/// Client mirror of the active TIMER countdowns (<see cref="ServerConnection.TimersChanged"/> and the
/// <c>RoomStateDto.Timers</c> join seed). Server-authoritative display state — the client never writes
/// it back, so no echo guard is needed. Drives the timers panel and its cancel action.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Active timers, soonest-to-fire first.</summary>
    public ObservableCollection<TimerItem> ActiveTimers { get; } = [];

    /// <summary>True when at least one timer is running (drives the timers panel's visibility).</summary>
    [ObservableProperty]
    private bool _timersActive;

    /// <summary>Always-visible countdown label: the soonest timer's remaining time, plus a "+N" badge
    /// when more are running. Updates every second from the server broadcast.</summary>
    [ObservableProperty]
    private string _timersSummary = "";

    private void OnTimersChanged(TimersChangedDto dto) => Dispatcher.UIThread.Post(() => ApplyTimers(dto.Timers));

    public void ApplyTimers(List<TimerDto>? timers)
    {
        ActiveTimers.Clear();
        if (timers is null)
        {
            TimersActive = false;
            return;
        }

        foreach (var t in timers)
        {
            ActiveTimers.Add(new TimerItem(t.Id, t.Callsign, t.Message, t.RemainingSeconds, t.TotalSeconds));
        }

        TimersActive = ActiveTimers.Count > 0;

        // Server orders timers soonest-to-fire first, so the first entry drives the summary label.
        if (ActiveTimers.Count == 0)
        {
            TimersSummary = "";
        }
        else
        {
            var soonest = ActiveTimers[0];
            TimersSummary = ActiveTimers.Count == 1 ? $"⏱ {soonest.Remaining}" : $"⏱ {soonest.Remaining}  +{ActiveTimers.Count - 1}";
        }
    }

    [RelayCommand]
    private async Task CancelTimer(int id)
    {
        await _connection.SendCommandAsync("", $"TIMER CANCEL {id}", _preferences.UserInitials);
    }
}
