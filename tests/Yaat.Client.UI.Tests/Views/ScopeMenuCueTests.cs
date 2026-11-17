using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope's side of a menu row's map cue: the host's highlight reaches every Scope and the view repaints when it changes;
/// the host's cue reaches every Scope, whose canvas places its ends against the frame's tracks; a callsign the Scope does
/// not draw gets nothing.
/// </summary>
public class ScopeMenuCueTests
{
    private const string From = "N52TW";
    private const string Target = "SWA1";

    [AvaloniaFact]
    public void HighlightAircraft_ReachesScopeViews()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null);
        main.OpenScope = scope;
        var host = new ClientMenuHost(main, null, new Border(), mvaFacilityId: null);
        scope.DataBlockState.ToggleHighlight("KEEP1");

        host.HighlightAircraft("SWA1");
        Assert.All(main.AllScopeViews, s => Assert.Equal(["KEEP1", "SWA1"], s.DataBlockState.HighlightedCallsigns.Order()));

        host.HighlightAircraft(null);
        Assert.All(main.AllScopeViews, s => Assert.Equal(["KEEP1"], s.DataBlockState.HighlightedCallsigns.Order()));
    }

    [AvaloniaFact]
    public void ScopeView_RepaintsOnHighlightsChanged()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            int before = view.OverlayRepaints;

            scope.DataBlockState.SetMenuHighlight(Target);

            Assert.Equal(before + 1, view.OverlayRepaints);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void SetMapCue_ResolvesBothEndsOnTheUiThread()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            var host = new ClientMenuHost(main, null, new Border(), mvaFacilityId: null);
            AircraftModel from = AddAircraft(main, view.Canvas, From, 300, 400);
            AircraftModel target = AddAircraft(main, view.Canvas, Target, 300, 200);
            var cue = new MapCue(From, Target, null, ShowBearingLabel: true);

            host.SetMapCue(cue);
            Dispatcher.UIThread.RunJobs();

            Assert.All(main.AllScopeViews, s => Assert.Same(cue, s.MenuCue));
            Assert.Same(cue, view.Canvas.MenuCue);
            ResolvedMapCue? resolved = CueOf(view.Canvas);
            Assert.NotNull(resolved);
            Assert.Equal((target.Position, (LatLon?)null, from.Position), (resolved.Target, resolved.ResolvedOther, resolved.From));

            host.SetMapCue(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(view.Canvas.MenuCue);
            Assert.Null(CueOf(view.Canvas));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void SetMapCue_UnknownCallsign_DrawsNothing()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            AircraftModel from = AddAircraft(main, view.Canvas, From, 300, 400);
            AircraftModel target = AddAircraft(main, view.Canvas, Target, 300, 200);
            ScopeCanvas canvas = view.Canvas;

            // A target the view does not draw: no ring and no line.
            canvas.MenuCue = new MapCue(From, "NOPE1", null, ShowBearingLabel: true);
            Assert.Null(CueOf(canvas));

            // A commanded aircraft the view does not draw: the ring stays, the line and its label go.
            canvas.MenuCue = new MapCue("NOPE1", Target, null, ShowBearingLabel: true);
            ResolvedMapCue? noFrom = CueOf(canvas);
            Assert.NotNull(noFrom);
            Assert.Equal((target.Position, (LatLon?)null, (string?)null), (noFrom.Target, noFrom.From, noFrom.BearingLabel));

            // A resolved aircraft the view does not draw: no dashed ring.
            canvas.MenuCue = new MapCue(From, Target, "NOPE1", ShowBearingLabel: true);
            Assert.Null(CueOf(canvas)?.ResolvedOther);

            // A target the view does not draw beside a resolved aircraft it does: the dashed ring alone.
            canvas.MenuCue = new MapCue(From, "NOPE1", Target, ShowBearingLabel: true);
            Assert.Equal(new ResolvedMapCue(null, target.Position, null, null), CueOf(canvas));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void MenuCue_Change_RepaintsTheView()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, From, 300, 400);
            AddAircraft(main, view.Canvas, Target, 300, 200);
            int before = view.OverlayRepaints;
            var cue = new MapCue(From, Target, null, ShowBearingLabel: true);

            scope.MenuCue = cue;

            Assert.Equal(before + 1, view.OverlayRepaints);
            Assert.Same(cue, view.Canvas.MenuCue);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DetachedViewModel_ChangesDoNotRepaintTheView()
    {
        (MainViewModel main, ScopeViewModel oldScope, ScopeView view, Window window) = ShowScope();
        try
        {
            oldScope.DataBlockState.ToggleHighlight("KEEP1");
            var newScope = new ScopeViewModel(main, prefStore: null, profileStore: null);
            main.OpenScope = newScope;
            view.DataContext = newScope;
            Dispatcher.UIThread.RunJobs();
            int before = view.OverlayRepaints;

            oldScope.DataBlockState.SetMenuHighlight(Target);
            oldScope.MenuCue = new MapCue(From, Target, null, ShowBearingLabel: true);

            Assert.Equal(before, view.OverlayRepaints);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static ResolvedMapCue? CueOf(ScopeCanvas canvas) => canvas.RenderSnapshotForTesting().Overlays?.MenuCue;

    private static (MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window) ShowScope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null);
        main.OpenScope = scope;
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (main, scope, view, window);
    }

    // An airborne aircraft whose symbol sits at the given canvas point, added to the traffic the scope draws.
    private static AircraftModel AddAircraft(MainViewModel main, ScopeCanvas canvas, string callsign, double screenX, double screenY)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)screenX, (float)screenY);
        var ac = new AircraftModel
        {
            Callsign = callsign,
            AircraftType = "B738",
            FiledAircraftType = "B738",
            FlightRules = "IFR",
            TransponderMode = "C",
            Altitude = 5000,
            GroundSpeed = 250,
            Position = new LatLon(lat, lon),
        };
        main.Aircraft.Add(ac);
        Dispatcher.UIThread.RunJobs();
        return ac;
    }
}
