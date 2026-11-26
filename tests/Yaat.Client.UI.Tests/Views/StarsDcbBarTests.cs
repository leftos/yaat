using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Client.Views.Scope.Toolbar.Dcb;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The STARS DCB Main page in the Scope host under the STARS profile: each live tile acts through today's Scope feature, a
/// latched spinner steps it live and Esc puts it back, and a tile with no feature yet is disabled.
/// </summary>
public class StarsDcbBarTests
{
    private const double HomeLat = 37.62;
    private const double HomeLon = -122.38;

    // One minute of latitude is one nautical mile.
    private const double DegPerNm = 1.0 / 60.0;

    /// <summary>A STARS-profile Scope host in a window, over a map source centred on the Bay Area with maps of <paramref name="starsIds"/>.</summary>
    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeHost Host, Window Window)> ShowStarsHostAsync(params int[] starsIds)
    {
        MainViewModel main = await ScopeTestFixture.MainAsync();
        LoadMaps(main, starsIds);
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        var host = new ScopeHost { DataContext = scope };
        var window = new Window
        {
            Width = 1200,
            Height = 800,
            Content = host,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        main.RunScopeCommand(".scope profile stars");
        Settle(window);
        return (main, scope, host, window);
    }

    /// <summary>Loads a map source centred on the Bay Area with maps of <paramref name="starsIds"/>.</summary>
    private static void LoadMaps(MainViewModel main, params int[] starsIds) =>
        main.ScopeMaps.ApplyVideoMapsDtoForTesting(
            new FacilityVideoMapsDto(
                "ZOA",
                "NCT",
                [new StarsAreaDto("A", "Area A", HomeLat, HomeLon, 60, [])],
                [.. starsIds.Select(id => new VideoMapInfoDto($"m{id}", $"Map {id}", $"MAP{id}", [], "A", id, false, false))],
                []
            ),
            null,
            null
        );

    /// <summary>Lays out and renders the window, as hit testing reads the rendered scene.</summary>
    private static void Settle(Window window)
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static StarsDcbBar Bar(ScopeHost host) => Assert.Single(host.GetVisualDescendants().OfType<StarsDcbBar>());

    /// <summary>The tile of the Main page entry that acts through <paramref name="id"/>.</summary>
    private static DcbTile TileOf(ScopeHost host, ScopeButtonId id) => Bar(host).Tiles[IndexOf(e => e.Button == id)];

    /// <summary>The tile of map slot <paramref name="slot"/>.</summary>
    private static DcbTile MapTile(ScopeHost host, int slot) => Bar(host).Tiles[IndexOf(e => e.MapSlot == slot)];

    private static int IndexOf(Func<StarsDcbEntry, bool> match) => StarsMainPage.Entries.ToList().FindIndex(e => match(e));

    private static Point Centre(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Press(Window window, Control control)
    {
        Point point = Centre(window, control);
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static void Wheel(Window window, Control control, int notches)
    {
        window.MouseWheel(Centre(window, control), new Vector(0, notches));
        Settle(window);
    }

    // A press on an empty corner of the canvas: it takes the focus and hits no track.
    private static void FocusCanvas(Window window, ScopeView view)
    {
        Point? inWindow = view.Canvas.TranslatePoint(new Point(5, view.Canvas.Bounds.Height - 5), window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Left);
        window.MouseUp(inWindow.Value, MouseButton.Left);
        Settle(window);
    }

    [AvaloniaFact]
    public async Task TheStarsProfile_ShowsTheStarsDcb()
    {
        (_, _, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            StarsDcbBar bar = Bar(host);
            Assert.Empty(host.GetVisualDescendants().OfType<ScopeToolbar>());
            Assert.Equal(StarsMainPage.Entries.Count, bar.Tiles.Count);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Range_WheelWhileLatched_StepsTheRangeLive_AndEscPutsItBack()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            DcbTile range = TileOf(host, ScopeButtonId.Range);
            double before = scope.RangeNm;

            Press(window, range);
            Assert.True(range.IsLatched);
            Wheel(window, range, 1);

            Assert.Equal(before - 1, scope.RangeNm);
            Assert.Equal($"RANGE\n{ScopeToolbarButtons.RangeLabel(before - 1)}", range.Label);

            window.DispatchKey(Key.Escape);
            Settle(window);

            Assert.False(range.IsLatched);
            Assert.Equal(before, scope.RangeNm);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Range_EscWhileLatched_WithTheMeasuringToolArmed_CancelsTheSpinnerAndKeepsTheTool()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            FocusCanvas(window, host.ScopeView);
            scope.ArmMeasure();
            Assert.True(main.Measure.IsMeasuring);
            DcbTile range = TileOf(host, ScopeButtonId.Range);
            double before = scope.RangeNm;

            Press(window, range);
            Wheel(window, range, 1);
            Assert.Equal(before - 1, scope.RangeNm);
            window.DispatchKey(Key.Escape);
            Settle(window);

            Assert.False(range.IsLatched);
            Assert.Equal(before, scope.RangeNm);
            Assert.True(main.Measure.IsMeasuring);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Rr_WheelStepsAlongTheRingSizeGrid()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            DcbTile rr = TileOf(host, ScopeButtonId.RrSize);
            Assert.Equal(5, scope.RangeRingSizeNm);

            Press(window, rr);
            Wheel(window, rr, 1);
            Assert.Equal(10, scope.RangeRingSizeNm);
            Wheel(window, rr, 1);
            Assert.Equal(15, scope.RangeRingSizeNm);
            Assert.Equal("RR\n15", rr.Label);
            Wheel(window, rr, -1);
            Wheel(window, rr, -1);
            Wheel(window, rr, -1);
            Assert.Equal(2, scope.RangeRingSizeNm);
            Press(window, rr);

            Assert.False(rr.IsLatched);
            Assert.Equal(2, scope.RangeRingSizeNm);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task OffCntr_DrawsPushedWhileOffCentre_AndAClickRecentres()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            DcbTile offCntr = TileOf(host, ScopeButtonId.DisplayOffCntr);
            Assert.False(offCntr.IsPushed);

            scope.CenterLat = HomeLat + (0.6 * DegPerNm);
            Settle(window);
            Assert.True(scope.IsOffCenter);
            Assert.True(offCntr.IsPushed);

            Press(window, offCntr);

            Assert.False(scope.IsOffCenter);
            Assert.Equal((HomeLat, HomeLon), (scope.CenterLat, scope.CenterLon));
            Assert.False(offCntr.IsPushed);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task MapOne_TogglesTheFirstMap()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync(2, 10);
        try
        {
            DcbTile map1 = MapTile(host, 0);
            Assert.Equal("2\nMAP2", map1.Label);
            Assert.False(scope.MapToggles[0].IsOn);

            Press(window, map1);

            Assert.True(scope.MapToggles[0].IsOn);
            Assert.False(scope.MapToggles[1].IsOn);
            Assert.True(map1.IsPushed);

            Press(window, MapTile(host, 0));

            Assert.False(scope.MapToggles[0].IsOn);
            Assert.False(MapTile(host, 0).IsPushed);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task WithTwoMaps_SlotsThreeToSix_AreDisabledAndEmpty()
    {
        (_, _, ScopeHost host, Window window) = await ShowStarsHostAsync(2, 10);
        try
        {
            Assert.Equal(DcbTileKind.Toggle, MapTile(host, 1).Kind);
            for (int slot = 2; slot < StarsMainPage.MapSlots; slot++)
            {
                DcbTile tile = MapTile(host, slot);
                Assert.Equal(DcbTileKind.Disabled, tile.Kind);
                Assert.Equal("", tile.Label);
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task ADisabledTile_ClickChangesNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync(2);
        try
        {
            DcbTile placeCntr = Bar(host).Tiles[IndexOf(e => e.Label == "PLACE\nCNTR")];
            Assert.Equal(DcbTileKind.Disabled, placeCntr.Kind);
            string status = main.StatusText;
            (double, double, double) view = (scope.RangeNm, scope.CenterLat, scope.CenterLon);

            Press(window, placeCntr);

            Assert.False(placeCntr.IsPushed);
            Assert.Equal(status, main.StatusText);
            Assert.Equal(view, (scope.RangeNm, scope.CenterLat, scope.CenterLon));
            Assert.False(scope.MapToggles[0].IsOn);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Esc with the canvas focused and the measuring tool armed reaches the Scope's own Esc chain, which disarms it.</summary>
    private static void AssertEscReachesTheScope(MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window)
    {
        FocusCanvas(window, host.ScopeView);
        scope.ArmMeasure();
        Assert.True(main.Measure.IsMeasuring);
        window.DispatchKey(Key.Escape);
        Settle(window);
        Assert.False(main.Measure.IsMeasuring);
    }

    [AvaloniaFact]
    public async Task Range_BarReplacedWhileLatched_CancelsOnTheScope()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            DcbTile range = TileOf(host, ScopeButtonId.Range);
            double before = scope.RangeNm;
            Press(window, range);
            Wheel(window, range, 1);
            Assert.True(scope.IsBarSpinnerLatched);

            main.RunScopeCommand(".scope profile mixed");
            Settle(window);

            Assert.Empty(host.GetVisualDescendants().OfType<StarsDcbBar>());
            Assert.False(scope.IsBarSpinnerLatched);
            Assert.Equal(before, scope.RangeNm);
            AssertEscReachesTheScope(main, scope, host, window);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Range_Accept_ClearsTheLatch_SoEscReachesTheScope()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            DcbTile range = TileOf(host, ScopeButtonId.Range);
            Press(window, range);
            Assert.True(scope.IsBarSpinnerLatched);
            Press(window, range);

            Assert.False(range.IsLatched);
            Assert.False(scope.IsBarSpinnerLatched);
            AssertEscReachesTheScope(main, scope, host, window);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Range_EscWithNoWheel_RunsNoCommand()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            DcbTile range = TileOf(host, ScopeButtonId.Range);
            Press(window, range);
            main.StatusText = "untouched";

            window.DispatchKey(Key.Escape);
            Settle(window);

            Assert.False(range.IsLatched);
            Assert.False(scope.IsBarSpinnerLatched);
            Assert.Equal("untouched", main.StatusText);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Range_MapSlotCountChangingWhileLatched_CancelsTheLatch()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync(2);
        try
        {
            DcbTile range = TileOf(host, ScopeButtonId.Range);
            Press(window, range);
            Wheel(window, range, 1);
            Assert.True(scope.IsBarSpinnerLatched);

            LoadMaps(main, 2, 10);
            Settle(window);

            Assert.Equal(2, scope.MapToggles.Count);
            Assert.False(range.IsLatched);
            Assert.False(scope.IsBarSpinnerLatched);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task Range_DataContextSwappedWhileLatched_RestoresOnTheFirstScope()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        var second = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        try
        {
            StarsDcbBar bar = Bar(host);
            DcbTile range = TileOf(host, ScopeButtonId.Range);
            double before = scope.RangeNm;
            double secondBefore = second.RangeNm;
            Press(window, range);
            Wheel(window, range, 1);
            Assert.Equal(before - 1, scope.RangeNm);

            bar.DataContext = second;
            Settle(window);

            Assert.False(range.IsLatched);
            Assert.Equal(before, scope.RangeNm);
            Assert.Equal(secondBefore, second.RangeNm);
            Assert.False(scope.IsBarSpinnerLatched);
            Assert.False(second.IsBarSpinnerLatched);
        }
        finally
        {
            window.Close();
            second.Dispose();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
