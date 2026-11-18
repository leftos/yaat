using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Sim;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope's range ring: on by default at 5 NM on the facility's home centre; the RR tile shows its size or OFF, and its
/// tray holds SHOW, PLACE RR (the next map click places the centre), SIZE (2, 5, 10, 15, 20) and RR CNTR; Esc cancels an
/// armed PLACE RR, and arming the Rings tool disarms it; the armed press lands on a datablock or a symbol ahead of the block
/// drag; a reseed and a late recentre of the map source put the ring back on the home centre, and a set applied while the
/// scope waits for the map source keeps its ring after the seed; the ring is kept in a pref set.
/// </summary>
public class ScopeRangeRingTests
{
    private const double HomeLat = 37.62;
    private const double HomeLon = -122.38;

    private static ScopeButton Button(ScopeButtonId id) => ScopeToolbarButtons.Find(id)!;

    [AvaloniaFact]
    public void ByDefault_TheRingIsOnAt5Nm_CentredOnTheHomeCentre()
    {
        (_, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            Assert.True(scope.ShowRangeRing);
            Assert.Equal(5, scope.RangeRingSizeNm);
            Assert.Null(scope.RangeRingCenter);
            Assert.Equal(new LatLon(HomeLat, HomeLon), scope.RangeRingCenterShown);
            Assert.Equal("5", scope.SpinnerValue(Button(ScopeButtonId.Rr)));
        }
    }

    [AvaloniaFact]
    public void RrTile_IsAMenuOfShowPlaceSizeAndCntr_ShowingItsSizeOrOff()
    {
        ScopeButton rr = Button(ScopeButtonId.Rr);
        Assert.Equal(("RR", ScopeButtonKind.Menu), (rr.Label, rr.Kind));
        Assert.Equal([ScopeButtonId.RrShow, ScopeButtonId.RrPlace, ScopeButtonId.RrSize, ScopeButtonId.RrCenter], rr.Children.Select(c => c.Id));
        Assert.Equal(["SHOW", "PLACE RR", "SIZE", "RR CNTR"], rr.Children.Select(c => c.Label));
        Assert.Equal(
            [ScopeButtonKind.Toggle, ScopeButtonKind.Command, ScopeButtonKind.Spinner, ScopeButtonKind.Command],
            rr.Children.Select(c => c.Kind)
        );
        Assert.Equal([".scope rr", ".scope rr place", null, ".scope rr center"], rr.Children.Select(c => c.Command));

        (_, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            scope.Press(Button(ScopeButtonId.RrShow));
            Assert.False(scope.ShowRangeRing);
            Assert.Equal("OFF", scope.SpinnerValue(rr));
            Assert.False(scope.IsActive(Button(ScopeButtonId.RrShow)));
        }
    }

    [AvaloniaFact]
    public async Task RrVerbs_SetTheRing_AndEcho()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            main.RunScopeCommand(".scope rr off");
            Assert.False(scope.ShowRangeRing);
            Assert.Equal("Scope RR OFF", main.StatusText);

            main.RunScopeCommand(".scope rr on");
            Assert.True(scope.ShowRangeRing);
            Assert.Equal("Scope RR ON", main.StatusText);

            main.RunScopeCommand(".scope rr size 10");
            Assert.Equal(10, scope.RangeRingSizeNm);
            Assert.Equal("Scope RR SIZE 10", main.StatusText);

            main.RunScopeCommand(".scope rr place");
            Assert.True(scope.IsPlacingRangeRing);
            Assert.True(scope.IsActive(Button(ScopeButtonId.RrPlace)));
            Assert.Equal("Scope RR PLACE: click the map", main.StatusText);

            scope.PlaceRangeRing(new LatLon(37.5, -122.1));
            main.RunScopeCommand(".scope rr center");
            Assert.Null(scope.RangeRingCenter);
            Assert.Equal("Scope RR CNTR", main.StatusText);

            foreach (string bad in new[] { ".scope rr", ".scope rr size 7", ".scope rr size", ".scope rr maybe" })
            {
                main.RunScopeCommand(bad);
                Assert.Equal("Usage: .scope rr <on|off|place|center> | .scope rr size <2|5|10|15|20>", main.StatusText);
            }

            Assert.Equal(10, scope.RangeRingSizeNm);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task RrVerb_WithNoOtherScope_ActsOnThePrimaryScope()
    {
        MainViewModel main = await MainAsync();

        main.RunScopeCommand(".scope rr on");

        Assert.True(main.PrimaryScope.ShowRangeRing);
        Assert.Equal("Scope RR ON", main.StatusText);
    }

    [AvaloniaFact]
    public async Task SizeSpinner_StepsAlong2_5_10_15_20_AndStopsAtTheEnds()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        try
        {
            ScopeButton size = Button(ScopeButtonId.RrSize);

            scope.StepSpinner(ScopeButtonId.RrSize, 1);
            Assert.Equal(10, scope.RangeRingSizeNm);
            scope.StepSpinner(ScopeButtonId.RrSize, 5);
            Assert.Equal(20, scope.RangeRingSizeNm);
            scope.StepSpinner(ScopeButtonId.RrSize, -10);
            Assert.Equal(2, scope.RangeRingSizeNm);
            Assert.Equal("2", scope.SpinnerValue(size));
        }
        finally
        {
            scope.Dispose();
        }
    }

    [AvaloniaFact]
    public void RrCntr_ReturnsThePlacedRingToTheHomeCentre()
    {
        (MainViewModel main, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            scope.PlaceRangeRing(new LatLon(37.5, -122.1));
            Assert.Equal(new LatLon(37.5, -122.1), scope.RangeRingCenterShown);

            scope.Press(Button(ScopeButtonId.RrCenter));

            Assert.Equal(new LatLon(HomeLat, HomeLon), scope.RangeRingCenterShown);
            Assert.Equal("Scope RR CNTR", main.StatusText);
        }
    }

    [AvaloniaFact]
    public void PlaceRr_Armed_AClickOnTheMap_PlacesTheCentre_AndDisarms()
    {
        (ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            scope.ShowRangeRing = false;
            scope.Main.RunScopeCommand(".scope rr place");
            Settle(window);
            var at = new Point(300, 200);
            (double lat, double lon) = view.Canvas.Viewport.ScreenToLatLon((float)at.X, (float)at.Y);

            Click(window, view, at);

            Assert.False(scope.IsPlacingRangeRing);
            Assert.True(scope.ShowRangeRing);
            Assert.NotNull(scope.RangeRingCenter);
            Assert.Equal(lat, scope.RangeRingCenter.Value.Lat, 6);
            Assert.Equal(lon, scope.RangeRingCenter.Value.Lon, 6);
            Assert.Equal(new ScopeRangeRing(scope.RangeRingCenter.Value, 5), view.Canvas.RangeRing);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void PlaceRr_Armed_Esc_Disarms_AndLeavesTheRing()
    {
        (ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            Click(window, view, new Point(5, view.Canvas.Bounds.Height - 5));
            scope.Main.RunScopeCommand(".scope rr place");
            Settle(window);

            window.DispatchKey(Key.Escape);
            Settle(window);

            Assert.False(scope.IsPlacingRangeRing);
            Assert.Null(scope.RangeRingCenter);
            Assert.Equal(new ScopeRangeRing(new LatLon(HomeLat, HomeLon), 5), view.Canvas.RangeRing);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void PlaceRr_Armed_ThenTheRingsToolArmed_Disarms()
    {
        (_, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            scope.Main.RunScopeCommand(".scope rr place");
            Assert.True(scope.IsPlacingRangeRing);

            scope.Press(Button(ScopeButtonId.Tools));

            Assert.True(scope.IsRingToolArmed);
            Assert.False(scope.IsPlacingRangeRing);
        }
    }

    [AvaloniaFact]
    public void PlaceRr_Armed_APressOnADatablock_PlacesTheRingThere_AndStartsNoBlockDrag()
    {
        (ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(scope.Main, view, "UAL238", new Point(300, 300));
            scope.Main.RunScopeCommand(".scope rr place");
            Settle(window);
            Point grab = CentreOf(view, ScopeFieldSlot.Callsign);
            (double lat, double lon) = view.Canvas.Viewport.ScreenToLatLon((float)grab.X, (float)grab.Y);

            Drag(window, view, grab, new Point(grab.X + 40, grab.Y - 30));

            Assert.False(scope.IsPlacingRangeRing);
            Assert.NotNull(scope.RangeRingCenter);
            Assert.Equal(lat, scope.RangeRingCenter.Value.Lat, 6);
            Assert.Equal(lon, scope.RangeRingCenter.Value.Lon, 6);
            Assert.Empty(scope.DataBlockState.ManualOffsets);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void PlaceRr_Armed_APressOnATrackSymbol_PlacesTheRingThere()
    {
        (ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(scope.Main, view, "UAL238", new Point(300, 300));
            scope.Main.RunScopeCommand(".scope rr place");
            Settle(window);
            var at = new Point(300, 300);
            (double lat, double lon) = view.Canvas.Viewport.ScreenToLatLon((float)at.X, (float)at.Y);

            Click(window, view, at);

            Assert.False(scope.IsPlacingRangeRing);
            Assert.NotNull(scope.RangeRingCenter);
            Assert.Equal(lat, scope.RangeRingCenter.Value.Lat, 6);
            Assert.Equal(lon, scope.RangeRingCenter.Value.Lon, 6);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void PlaceRr_NotArmed_APressOnADatablock_StartsTheBlockDrag_AndLeavesTheRing()
    {
        (ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(scope.Main, view, "UAL238", new Point(300, 300));
            Point grab = CentreOf(view, ScopeFieldSlot.Callsign);

            Drag(window, view, grab, new Point(grab.X + 40, grab.Y - 30));

            Assert.True(scope.DataBlockState.ManualOffsets.ContainsKey("UAL238"));
            Assert.Null(scope.RangeRingCenter);
            Assert.False(scope.IsPlacingRangeRing);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void PlaceRr_Armed_ARightPress_LeavesTheRingAndTheArming()
    {
        (ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            scope.Main.RunScopeCommand(".scope rr place");
            Settle(window);
            Point at = InWindow(view, window, new Point(400, 200));

            window.MouseDown(at, MouseButton.Right);
            window.MouseUp(at, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();

            Assert.True(scope.IsPlacingRangeRing);
            Assert.Null(scope.RangeRingCenter);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void AReseed_ReturnsThePlacedRingToTheHomeCentre()
    {
        (MainViewModel main, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            scope.PlaceRangeRing(new LatLon(37.5, -122.1));

            ScopeMapToolsCommandTests.LoadSource(main, HomeLat, HomeLon);

            Assert.Null(scope.RangeRingCenter);
            Assert.Equal(new LatLon(HomeLat, HomeLon), scope.RangeRingCenterShown);
        }
    }

    [AvaloniaFact]
    public void APrefSetAppliedWhileWaiting_KeepsItsPlacedRing_AfterTheSeed()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        using ScopeViewModel scope = new(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        Assert.True(scope.IsWaitingForMapSource);
        var at = new LatLon(37.5, -122.1);
        var set = new ScopePrefSet
        {
            Name = "MINE",
            RangeNm = 60,
            CenterLat = HomeLat,
            CenterLon = HomeLon,
            TemplateId = ScopeTemplates.StarsId,
            Db = new ScopePrefDb(true, false, true, 14),
            Kind = "stars",
            Lens = new ScopePrefLens(false, false, false, false),
            MapTools = new ScopePrefMapTools(true, false, new ScopePrefRangeRing(true, 10, at.Lat, at.Lon), false, null),
        };

        scope.ApplyPrefSet(set);
        ScopeMapToolsCommandTests.LoadSource(main, HomeLat, HomeLon);

        Assert.Equal(at, scope.RangeRingCenter);
        Assert.Equal(10, scope.RangeRingSizeNm);
        Assert.Equal(at, scope.RangeRingCenterShown);
        Assert.False(scope.ShowWeather);
    }

    [AvaloniaFact]
    public void ALateRecentreOfTheMapSource_MovesTheUnplacedRingsCentre()
    {
        (MainViewModel main, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            Assert.Null(scope.RangeRingCenter);
            List<string?> changed = [];
            scope.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            var recentred = new LatLon(HomeLat + 1.0, HomeLon + 1.0);

            main.ScopeMaps.RecenterOnPrimaryAirport(recentred.Lat, recentred.Lon);

            Assert.Equal(recentred, scope.RangeRingCenterShown);
            Assert.Contains(nameof(ScopeViewModel.RangeRingCenterShown), changed);
        }
    }

    [AvaloniaFact]
    public void TheCanvasRing_FollowsShowAndSize()
    {
        (ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            Assert.Equal(new ScopeRangeRing(new LatLon(HomeLat, HomeLon), 5), view.Canvas.RangeRing);

            scope.Main.RunScopeCommand(".scope rr size 15");
            Assert.Equal(new ScopeRangeRing(new LatLon(HomeLat, HomeLon), 15), view.Canvas.RangeRing);

            scope.Main.RunScopeCommand(".scope rr off");
            Assert.Null(view.Canvas.RangeRing);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ThePlacedRingAndWx_AreKeptInAPrefSet_DefaultMapToolsTakesTheDefaults()
    {
        (_, ScopeViewModel scope) = SeededScope();
        using (scope)
        {
            scope.RangeRingSizeNm = 10;
            scope.PlaceRangeRing(new LatLon(37.5, -122.1));
            scope.ShowWeather = false;

            ScopePrefSet set = scope.CapturePrefSet("MINE");
            Assert.Equal(new ScopePrefRangeRing(true, 10, 37.5, -122.1), set.MapTools.RangeRing);
            Assert.False(set.MapTools.Weather);

            scope.ApplyPrefSet(set with { MapTools = new ScopePrefMapTools(false, false, null, null, null) });
            Assert.True(scope.ShowRangeRing);
            Assert.Equal(5, scope.RangeRingSizeNm);
            Assert.Null(scope.RangeRingCenter);
            Assert.True(scope.ShowWeather);

            scope.ApplyPrefSet(set);
            Assert.Equal(10, scope.RangeRingSizeNm);
            Assert.Equal(new LatLon(37.5, -122.1), scope.RangeRingCenter);
            Assert.False(scope.ShowWeather);
        }
    }

    /// <summary>A scope seeded from one STARS area centred on the home centre, with no window.</summary>
    private static (MainViewModel Main, ScopeViewModel Scope) SeededScope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        ScopeMapToolsCommandTests.LoadSource(main, HomeLat, HomeLon);
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        return (main, scope);
    }

    private static (ScopeViewModel Scope, ScopeView View, Window Window) ShowScope()
    {
        (_, ScopeViewModel scope) = SeededScope();
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        Settle(window);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (scope, view, window);
    }

    private static void Settle(Window window)
    {
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static void Click(Window window, ScopeView view, Point onCanvas)
    {
        Point inWindow = InWindow(view, window, onCanvas);
        window.MouseDown(inWindow, MouseButton.Left);
        window.MouseUp(inWindow, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Drag(Window window, ScopeView view, Point fromCanvas, Point toCanvas)
    {
        Point from = InWindow(view, window, fromCanvas);
        Point to = InWindow(view, window, toCanvas);
        window.MouseDown(from, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point InWindow(ScopeView view, Window window, Point onCanvas)
    {
        Point? inWindow = view.Canvas.TranslatePoint(onCanvas, window);
        Assert.NotNull(inWindow);
        return inWindow.Value;
    }

    private static void AddAircraft(MainViewModel main, ScopeView view, string callsign, Point symbol)
    {
        ScopeCanvas canvas = view.Canvas;
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)symbol.X, (float)symbol.Y);
        main.Aircraft.Add(
            new AircraftModel
            {
                Callsign = callsign,
                AircraftType = "B738",
                FiledAircraftType = "B738",
                FlightRules = "IFR",
                TransponderMode = "C",
                Altitude = 5000,
                GroundSpeed = 250,
                Position = new LatLon(lat, lon),
            }
        );
        canvas.MarkDirty();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static Point CentreOf(ScopeView view, ScopeFieldSlot slot)
    {
        FieldRect field = Assert.Single(view.Canvas.CurrentFrame.Tracks).Block.Fields.First(f => f.Slot == slot);
        return new Point(field.Rect.MidX, field.Rect.MidY);
    }
}
