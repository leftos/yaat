using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Client.Views.Scope.Dock;
using Yaat.Client.Views.Scope.Toolbar;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's SSA view: two lines (<see cref="SsaLines"/>) rebuilt every second from the session clock
/// (<see cref="MainViewModel.SessionNowUtc"/>, the wall clock with no scenario), the primary airport's weather, the sign-in
/// state and the Scope's range. The refresh timer runs only while the view is open.
/// </summary>
public sealed partial class SsaViewModel : ScopeDockViewModel
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _clock;

    [ObservableProperty]
    private string _line1 = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isStatusAlert;

    [ObservableProperty]
    private string _line2Tail = "";

    public SsaViewModel(ScopeViewModel scope)
        : base(scope, ScopeToolbarButtons.ViewSsaName, "SSA")
    {
        // Not the (interval, priority, handler) overload: that one starts the timer at once.
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickInterval };
        _clock.Tick += (_, _) => Refresh();
    }

    /// <summary>Whether the 1 s clock is running.</summary>
    internal bool IsClockRunning => _clock.IsEnabled;

    /// <summary>Rebuilds the lines from the session time (the wall clock with no scenario), weather, sign-in state and range.</summary>
    public void Refresh()
    {
        MainViewModel main = Scope.Main;
        string? airport = main.ActiveScenarioPrimaryAirportId;
        double? altimeter = airport is null ? null : main.GroundWeatherFor(airport)?.AltimeterInHg;
        bool signedIn = main.IsInRoom && !string.IsNullOrEmpty(main.ActiveTcp);
        DateTime now = main.SessionNowUtc ?? MainViewModel.WallClock.GetUtcNow().UtcDateTime;
        var lines = SsaLines.Build(now, altimeter, airport, signedIn, Scope.RangeNm);
        Line1 = lines.Line1;
        Status = lines.Status;
        IsStatusAlert = lines.IsStatusAlert;
        Line2Tail = lines.Line2Tail;
    }

    internal override void StartReading()
    {
        Refresh();
        _clock.Start();
    }

    internal override void StopReading() => _clock.Stop();
}
