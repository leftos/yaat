using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Radar;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The radar side of a menu row's scope feedback: the host's highlight reaches every radar view and the radar canvas
/// repaints when it changes; the host's radar cue reaches every radar view, whose canvas places its ends against the
/// aircraft it draws; a row's pointer and focus drive the cue, which clears when the row loses both and when the flyout
/// closes.
/// </summary>
public class RadarMenuCueTests
{
    [AvaloniaFact]
    public void HighlightAircraft_ReachesRadarViews()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var host = new ClientMenuHost(main, null, new Border());
        main.Radar.DataBlockState.ToggleHighlight("KEEP1");

        host.HighlightAircraft("SWA1");
        Assert.All(main.AllRadarViews, radar => Assert.Equal(["KEEP1", "SWA1"], radar.DataBlockState.HighlightedCallsigns.Order()));

        host.HighlightAircraft(null);
        Assert.All(main.AllRadarViews, radar => Assert.Equal(["KEEP1"], radar.DataBlockState.HighlightedCallsigns.Order()));
    }

    [AvaloniaFact]
    public void RadarCanvas_RepaintsOnHighlightsChanged()
    {
        var state = new RadarDataBlockViewState();
        var canvas = new RadarCanvas { DataBlockState = state };
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = canvas,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        int before = canvas.DataBlockStateRepaints;

        state.SetMenuHighlight("SWA1");

        Assert.Equal(before + 1, canvas.DataBlockStateRepaints);
        window.Close();
    }

    [AvaloniaFact]
    public void SetRadarCue_ResolvesBothEndsOnTheUiThread()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var host = new ClientMenuHost(main, null, new Border());
        (RadarCanvas canvas, Window window) = ShowRadarView(main);
        AircraftModel from = Airborne(From, new LatLon(37.70, -122.20));
        AircraftModel target = Airborne(Target, new LatLon(37.75, -122.20));
        canvas.Aircraft = [from, target];
        var cue = new RadarCue(From, Target, null, ShowBearingLabel: true);

        host.SetRadarCue(cue);
        Dispatcher.UIThread.RunJobs();

        Assert.All(main.AllRadarViews, radar => Assert.Same(cue, radar.MenuCue));
        Assert.Same(cue, canvas.MenuCue);
        ResolvedRadarCue? resolved = canvas.CaptureMenuCue();
        Assert.NotNull(resolved);
        Assert.Equal((target.Position, (LatLon?)null, from.Position), (resolved.Target, resolved.ResolvedOther, resolved.From));

        host.SetRadarCue(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(canvas.MenuCue);
        Assert.Null(canvas.CaptureMenuCue());
        window.Close();
    }

    [AvaloniaFact]
    public void SetRadarCue_UnknownCallsign_DrawsNothing()
    {
        AircraftModel from = Airborne(From, new LatLon(37.70, -122.20));
        AircraftModel target = Airborne(Target, new LatLon(37.75, -122.20));
        // A target the view does not draw: no ring and no line.
        var canvas = new RadarCanvas { Aircraft = [from, target], MenuCue = new RadarCue(From, "NOPE1", null, ShowBearingLabel: true) };
        Assert.Null(canvas.CaptureMenuCue());

        // A commanded aircraft the view does not draw: the ring stays, the line and its label go.
        canvas.MenuCue = new RadarCue("NOPE1", Target, null, ShowBearingLabel: true);
        ResolvedRadarCue? noFrom = canvas.CaptureMenuCue();
        Assert.NotNull(noFrom);
        Assert.Equal((target.Position, (LatLon?)null, (string?)null), (noFrom.Target, noFrom.From, noFrom.BearingLabel));

        // A resolved aircraft the view does not draw: no dashed ring.
        canvas.MenuCue = new RadarCue(From, Target, "NOPE1", ShowBearingLabel: true);
        Assert.Null(canvas.CaptureMenuCue()?.ResolvedOther);

        // A target the view does not draw beside a resolved aircraft it does: the dashed ring alone.
        canvas.MenuCue = new RadarCue(From, "NOPE1", Target, ShowBearingLabel: true);
        Assert.Equal(new ResolvedRadarCue(null, target.Position, null, null), canvas.CaptureMenuCue());
    }

    [AvaloniaFact]
    public void Cue_ClearsWhenRowLosesPointerAndFocus()
    {
        var host = new RecordingMenuHost("");
        var tracker = new RadarCueTracker(host, key => new RadarCue(From, key, null, ShowBearingLabel: true));
        var row = new Button();
        tracker.Track(row, Target);

        RaisePointer(row, InputElement.PointerEnteredEvent);
        Assert.Equal((Target, Target), (host.RadarCues[^1]?.TargetCallsign, host.Highlights[^1]));

        // Focus on the row keeps its cue once the pointer leaves.
        row.RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent));
        RaisePointer(row, InputElement.PointerExitedEvent);
        Assert.Equal((Target, Target), (host.RadarCues[^1]?.TargetCallsign, host.Highlights[^1]));

        row.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
        Assert.Equal(((RadarCue?)null, (string?)null), (host.RadarCues[^1], host.Highlights[^1]));
    }

    [AvaloniaFact]
    public void Cue_ClearsWhenFlyoutCloses()
    {
        var host = new RecordingMenuHost("");
        var tracker = new RadarCueTracker(host, key => new RadarCue(From, key, null, ShowBearingLabel: true));
        var row = new Button();
        tracker.Track(row, Target);
        RaisePointer(row, InputElement.PointerEnteredEvent);
        row.RaiseEvent(new FocusChangedEventArgs(InputElement.GotFocusEvent));
        Assert.Equal(Target, host.RadarCues[^1]?.TargetCallsign);

        tracker.Clear();

        Assert.Equal(((RadarCue?)null, (string?)null), (host.RadarCues[^1], host.Highlights[^1]));
    }

    [AvaloniaFact]
    public void Cue_ClearsWhenMenuCloses()
    {
        var host = new RecordingMenuHost("");
        var aircraft = new AircraftModel
        {
            Callsign = From,
            AircraftType = "C172",
            IsOnGround = false,
        };
        ContextMenu menu = AircraftMenuBuilder.Build(aircraft, new MenuClick(From, null, null, []), host, _ => []);
        var window = new Window { Width = 600, Height = 600 };
        window.Show();
        menu.Open(window);
        Dispatcher.UIThread.RunJobs();
        host.SetRadarCue(new RadarCue(From, Target, null, ShowBearingLabel: true));

        menu.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(host.RadarCues[^1]);
        window.Close();
    }

    private const string From = "N52TW";
    private const string Target = "SWA1";

    private static AircraftModel Airborne(string callsign, LatLon position) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = position,
            Heading = new TrueHeading(0),
            Altitude = 3000,
            IsOnGround = false,
        };

    private static (RadarCanvas Canvas, Window Window) ShowRadarView(MainViewModel main)
    {
        var view = new RadarView { DataContext = main.Radar };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (Assert.IsType<RadarCanvas>(view.FindControl<RadarCanvas>("Canvas")), window);
    }

    private static void RaisePointer(Control target, RoutedEvent routedEvent) =>
        target.RaiseEvent(
            new PointerEventArgs(
                routedEvent,
                target,
                new Pointer(0, PointerType.Mouse, isPrimary: true),
                rootVisual: null,
                rootVisualPosition: default,
                timestamp: 0,
                properties: default,
                modifiers: KeyModifiers.None
            )
        );
}
