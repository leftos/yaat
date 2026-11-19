using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Sim;
using Yaat.Sim.Data;
using static Yaat.Client.UI.Tests.Helpers.ScopeTestFixture;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope's pinned markers: <c>.ff</c>, <c>.marker</c> and <c>.markers</c> toggle fixes or FRDs on the open Scope, and
/// <c>.nomarkers</c> clears them, with no Scope open an error; markers resolve once the navigation data loads, survive a track
/// reset and are kept in a pref set; the map point menu pins one at the click, removes the one near it and clears them all.
/// </summary>
public class ScopeMarkerTests
{
    private const string Separator = "-";

    [AvaloniaFact]
    public async Task Ff_TogglesAMarkerOnThenOff_AndEchoes()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            main.RunScopeMarkerCommand(".ff SUNOL");
            Assert.Equal(["SUNOL"], scope.Markers.Select(m => m.Name));
            Assert.True(scope.HasMarkers);
            Assert.Equal("Scope markers updated", main.StatusText);

            main.RunScopeMarkerCommand(".ff sunol");
            Assert.Empty(scope.Markers);
            Assert.False(scope.HasMarkers);
        }
    }

    [AvaloniaFact]
    public async Task Marker_AnUnknownFix_IsReported_AndPinsNothing()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            main.RunScopeMarkerCommand(".marker NOTAFIXX");

            Assert.Empty(scope.Markers);
            Assert.Equal("Scope markers updated; unknown fix: NOTAFIXX", main.StatusText);
        }
    }

    [AvaloniaFact]
    public async Task Markers_PinsSeveral_AndNoMarkersClearsThem()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            main.RunScopeMarkerCommand(".markers SUNOL OAK");
            Assert.Equal(["SUNOL", "OAK"], scope.Markers.Select(m => m.Name));

            main.RunScopeMarkerCommand(".nomarkers");
            Assert.Empty(scope.Markers);
            Assert.Equal("Scope markers cleared", main.StatusText);

            main.RunScopeMarkerCommand(".ff");
            Assert.Equal("Usage: .ff <fix> [fix ...]", main.StatusText);
        }
    }

    [AvaloniaFact]
    public async Task Ff_WithNoOtherScope_PinsOnThePrimaryScope()
    {
        MainViewModel main = await MainAsync();

        main.RunScopeMarkerCommand(".ff SUNOL");
        Assert.Equal(["SUNOL"], main.PrimaryScope.MarkerTokens);

        main.RunScopeMarkerCommand(".nomarkers");
        Assert.Empty(main.PrimaryScope.MarkerTokens);
        Assert.Equal("Scope markers cleared", main.StatusText);
    }

    [AvaloniaFact]
    public async Task AddMarker_PinsATokenOnce()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.AddMarker("sunol");
            scope.AddMarker("SUNOL");

            Assert.Equal(["SUNOL"], scope.MarkerTokens);
            Assert.Single(scope.Markers);
        }
    }

    [AvaloniaFact]
    public async Task RemoveNearestMarker_TakesOnlyAMarkerInsideTheRadius()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.AddMarker("SUNOL");
            LatLon pin = scope.Markers[0].Position;
            var click = new LatLon(pin.Lat + (0.3 / 60.0), pin.Lon);

            Assert.False(scope.RemoveNearestMarker(click, 0.2));
            Assert.Single(scope.Markers);

            Assert.True(scope.RemoveNearestMarker(click, 0.5));
            Assert.Empty(scope.Markers);
            Assert.Empty(scope.MarkerTokens);
        }
    }

    [AvaloniaFact]
    public async Task AMarkerRestoredBeforeTheNavData_ResolvesOnceItLoads()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        Assert.Null(main.ScopeMaps.Fixes);
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;

        var tools = new ScopePrefMapTools(false, false, null, null, new ScopePrefFixesAndMarkers(false, ["SUNOL"]));
        scope.ApplyPrefSet(SetWith(tools));
        Assert.Equal(["SUNOL"], scope.MarkerTokens);
        Assert.Empty(scope.Markers);

        await main.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["SUNOL"], scope.Markers.Select(m => m.Name));
    }

    [AvaloniaFact]
    public async Task Markers_SurviveATrackReset()
    {
        (_, ScopeViewModel scope) = await OpenScopeAsync();
        using (scope)
        {
            scope.AddMarker("SUNOL");

            scope.ClearTrackState();

            Assert.Equal(["SUNOL"], scope.Markers.Select(m => m.Name));
        }
    }

    [AvaloniaFact]
    public async Task MarkersAndFix_AreKeptInAPrefSet_ASetWithoutThemLoadsWithFixOffAndNoMarkers()
    {
        MainViewModel main = await MainAsync();
        ScopeMapToolsCommandTests.LoadSource(main);
        using var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        scope.AddMarker("SUNOL");
        scope.ShowFixes = true;

        ScopePrefSet set = scope.CapturePrefSet("MINE");
        ScopePrefFixesAndMarkers? saved = set.MapTools.FixesAndMarkers;
        Assert.NotNull(saved);
        Assert.True(saved.ShowFixes);
        Assert.Equal(["SUNOL"], saved.Markers);

        scope.ApplyPrefSet(set with { MapTools = set.MapTools with { FixesAndMarkers = null } });
        Assert.False(scope.ShowFixes);
        Assert.Empty(scope.Markers);

        scope.ApplyPrefSet(set);
        Assert.True(scope.ShowFixes);
        Assert.Equal(["SUNOL"], scope.Markers.Select(m => m.Name));

        scope.ApplyPrefSet(set with { MapTools = new ScopePrefMapTools(false, false, null, null, null) });
        Assert.False(scope.ShowFixes);
        Assert.Empty(scope.MarkerTokens);
    }

    [AvaloniaFact]
    public async Task PointMenu_NoMarkers_OffersOnlyPinMarkerHere()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window, LatLon position, string frd) = await ShowScopeAsync();
        try
        {
            List<string> labels = Labels(view.BuildMapPointMenu(scope, position, default).Items);

            Assert.Equal([frd, "Copy FRD", "Pin marker here", Separator], labels[..4]);
            Assert.DoesNotContain("Remove marker", labels);
            Assert.DoesNotContain("Clear pinned markers", labels);
        }
        finally
        {
            Close(window, view);
        }
    }

    [AvaloniaFact]
    public async Task PointMenu_PinMarkerHere_PinsTheFrd()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window, LatLon position, string frd) = await ShowScopeAsync();
        try
        {
            Click(view.BuildMapPointMenu(scope, position, default), "Pin marker here");

            Assert.Equal([frd], scope.MarkerTokens);
        }
        finally
        {
            Close(window, view);
        }
    }

    [AvaloniaFact]
    public async Task PointMenu_AMarkerNearTheClick_OffersRemoveAndClear()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window, LatLon position, string frd) = await ShowScopeAsync();
        try
        {
            scope.AddMarker(frd);

            List<string> labels = Labels(view.BuildMapPointMenu(scope, position, default).Items);

            Assert.Equal([frd, "Copy FRD", "Pin marker here", "Remove marker", "Clear pinned markers", Separator], labels[..6]);
        }
        finally
        {
            Close(window, view);
        }
    }

    [AvaloniaFact]
    public async Task PointMenu_AMarkerFarFromTheClick_OffersClearButNotRemove()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window, LatLon position, string frd) = await ShowScopeAsync();
        try
        {
            scope.AddMarker("SUNOL");

            List<string> labels = Labels(view.BuildMapPointMenu(scope, position, default).Items);

            Assert.Equal([frd, "Copy FRD", "Pin marker here", "Clear pinned markers", Separator], labels[..5]);
        }
        finally
        {
            Close(window, view);
        }
    }

    [AvaloniaFact]
    public async Task PointMenu_SeveralMarkers_RemoveTakesTheOneNearTheClick_ClearTakesThemAll()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window, LatLon position, string frd) = await ShowScopeAsync();
        try
        {
            scope.AddMarker("SUNOL");
            scope.AddMarker(frd);

            Click(view.BuildMapPointMenu(scope, position, default), "Remove marker");
            Assert.Equal(["SUNOL"], scope.MarkerTokens);

            Click(view.BuildMapPointMenu(scope, position, default), "Clear pinned markers");
            Assert.Empty(scope.MarkerTokens);
        }
        finally
        {
            Close(window, view);
        }
    }

    private static ScopePrefSet SetWith(ScopePrefMapTools tools) =>
        new()
        {
            Name = "MINE",
            RangeNm = 60,
            CenterLat = 37.62,
            CenterLon = -122.38,
            TemplateId = ScopeTemplates.StarsId,
            Db = new ScopePrefDb(true, false, true, true, true, true, 14),
            Kind = "stars",
            Lens = new ScopePrefLens(false, false, false, false),
            MapTools = tools,
        };

    /// <summary>An open Scope in a window whose fixes name the point (37.80, -122.20) by an FRD off OAK; the point and its FRD.</summary>
    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window, LatLon Position, string Frd)> ShowScopeAsync()
    {
        (MainViewModel main, ScopeViewModel scope) = await OpenScopeAsync();
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
        main.ScopeMaps.SetFixesForTesting([("OAK", 37.7213, -122.2208)]);
        var position = new LatLon(37.80, -122.20);
        string frd = FrdResolver.ToFrd(position.Lat, position.Lon, main.ScopeMaps.Fixes!, NavigationDatabase.Instance)!;
        return (main, scope, view, window, position, frd);
    }

    private static void Click(ContextMenu menu, string header)
    {
        MenuItem item = menu.Items.OfType<MenuItem>().Single(i => (i.Header as string) == header);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Close(Window window, ScopeView view)
    {
        view.ActiveContextMenu?.Close();
        Dispatcher.UIThread.RunJobs();
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static List<string> Labels(IEnumerable<object?> items) =>
        [
            .. items.Select(i =>
                i switch
                {
                    Avalonia.Controls.Separator => Separator,
                    MenuItem m => m.Header as string ?? "",
                    _ => i?.GetType().Name ?? "null",
                }
            ),
        ];
}
