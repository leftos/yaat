using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Toolbar;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope's FIX overlay: off by default, turned on and off by DISPLAY's FIX (its first entry) and <c>.scope fix</c>; it draws
/// the chart's fixes once the navigation data loads, labels the one within 20 px of the pointer, and labels the selected
/// aircraft's programmed fixes, following a change of its route.
/// </summary>
public class ScopeFixOverlayTests
{
    private const double CenterLat = 37.7;
    private const double CenterLon = -122.2;

    private static ScopeButton Button(ScopeButtonId id) => ScopeToolbarButtons.Find(id)!;

    [AvaloniaFact]
    public async Task FixVerb_TurnsTheOverlayOnAndOff_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            Assert.False(scope.ShowFixes);

            main.RunScopeCommand(".scope fix on");
            Assert.True(scope.ShowFixes);
            Assert.Equal("Radar FIX ON", main.StatusText);

            main.RunScopeCommand(".scope fix off");
            Assert.False(scope.ShowFixes);
            Assert.Equal("Radar FIX OFF", main.StatusText);

            main.RunScopeCommand(".scope fix");
            Assert.Equal("Usage: .scope fix <on|off>", main.StatusText);
        }
    }

    [AvaloniaFact]
    public async Task FixVerb_WithNoOtherScope_ActsOnThePrimaryScope()
    {
        MainViewModel main = await MainAsync();

        main.RunScopeCommand(".scope fix on");

        Assert.True(main.PrimaryScope.ShowFixes);
        Assert.Equal("Radar FIX ON", main.StatusText);
    }

    [AvaloniaFact]
    public async Task FixTile_FlipsTheOverlay_LitWhileOn()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            ScopeButton fix = Button(ScopeButtonId.DisplayFix);
            Assert.Equal(("FIX", ScopeButtonKind.Toggle, ".scope fix"), (fix.Label, fix.Kind, fix.Command));

            scope.Press(fix);
            Assert.True(scope.ShowFixes);
            Assert.True(scope.IsActive(fix));

            scope.Press(fix);
            Assert.False(scope.ShowFixes);
            Assert.False(scope.IsActive(fix));
        }
    }

    [AvaloniaFact]
    public void BeforeTheNavData_NoFixIsShown_ThenTheChartsFixesAre()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        Assert.Null(main.ScopeMaps.Fixes);
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot) { ShowFixes = true };

        Assert.Null(scope.ShownFixes);

        main.ScopeMaps.SetFixesForTesting([("OAK", 37.7213, -122.2208)]);

        Assert.Equal(["OAK"], scope.ShownFixes!.Select(f => f.Name));
    }

    [AvaloniaTheory]
    [InlineData(19, true)]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public async Task Hover_LabelsTheFixWithin20Px(int distancePx, bool labelled)
    {
        (MainViewModel main, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            ScopeCanvas canvas = view.Canvas;
            (double lat, double lon) = canvas.Viewport.ScreenToLatLon(400, 300);
            main.ScopeMaps.SetFixesForTesting([("TESTX", lat, lon)]);
            Dispatcher.UIThread.RunJobs();
            (float fx, float fy) = canvas.Viewport.LatLonToScreen(lat, lon);

            window.MouseMove(new Point(fx + distancePx, fy));
            Dispatcher.UIThread.RunJobs();

            ScopeFixLayer? layer = canvas.RenderSnapshotForTesting().Overlays?.Fixes;
            Assert.NotNull(layer);
            Assert.Equal(labelled ? "TESTX" : null, layer.Hovered);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task TheSelectedAircraftsProgrammedFixes_AreLabelled_FollowingItsRoute()
    {
        (MainViewModel main, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            main.ScopeMaps.SetFixesForTesting([("SUNOL", 37.6, -121.9), ("OAK", 37.7213, -122.2208)]);
            AircraftModel ac = AddAircraft(main, "AAL1", 5000);
            ac.Route = "SUNOL";
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(view.Canvas.RenderSnapshotForTesting().Overlays!.Fixes!.Programmed);

            ((ScopeViewModel)view.DataContext!).SelectedAircraft = ac;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("SUNOL", view.Canvas.RenderSnapshotForTesting().Overlays!.Fixes!.Programmed);

            ac.Route = "OAK";
            IReadOnlySet<string> programmed = view.Canvas.RenderSnapshotForTesting().Overlays!.Fixes!.Programmed;
            Assert.Contains("OAK", programmed);
            Assert.DoesNotContain("SUNOL", programmed);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task<(MainViewModel Main, ScopeView View, Window Window)> ShowScopeAsync()
    {
        MainViewModel main = await MainAsync();
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot)
        {
            CenterLat = CenterLat,
            CenterLon = CenterLon,
            ShowFixes = true,
        };
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
        return (main, view, window);
    }
}
