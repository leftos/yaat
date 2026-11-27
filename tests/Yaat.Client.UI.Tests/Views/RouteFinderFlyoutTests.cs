using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The aircraft editor's Find Route flyout: its "Find Route…" button beside the route box shows only while the aircraft has
/// a flight plan; a click opens a 560 px flyout floating over the grid, which Escape and a click outside close; Apply
/// closes it and the route box shows the chosen route. The route sources are a stub, so no test reaches the network.
/// </summary>
public class RouteFinderFlyoutTests
{
    private const string Zoa = "ZOA";
    private const string OakId = "01HHWYG5C2DCFR09A7S5A0Q0KP";
    private const string OakSacFmg = "OAK SAC FMG";

    [AvaloniaFact]
    public async Task FindRouteButton_ShowsOnlyWhileTheAircraftHasAFlightPlan()
    {
        ScenarioEditorWindow window = NewWindow();
        try
        {
            ScenarioAircraftView view = await OpenFirstAircraftAsync(window);
            ScenarioAircraftViewModel aircraft = window.Editor.Aircraft!;
            Button button = view.FindControl<Button>("FindRouteButton")!;

            aircraft.HasFlightPlan = false;
            MainWindowHost.Pump();
            Assert.False(button.IsEffectivelyVisible);

            aircraft.HasFlightPlan = true;
            MainWindowHost.Pump();
            Assert.True(button.IsEffectivelyVisible);
            Assert.Equal("Find Route…", view.FindControl<TextBlock>("FindRouteText")!.Text);
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaFact]
    public async Task FindRoute_OpensA560PxFlyoutOverTheGrid_AndEscapeClosesIt()
    {
        ScenarioEditorWindow window = NewWindow();
        try
        {
            ScenarioAircraftView view = await OpenFirstAircraftAsync(window);
            (Flyout flyout, RouteFinderView panel) = await OpenFindRouteAsync(window, view);

            Assert.True(flyout.IsOpen);
            Assert.Equal(560, panel.Bounds.Width);
            Button button = view.FindControl<Button>("FindRouteButton")!;
            Assert.Contains("open", button.Classes);
            ScrollViewer detail = view.FindControl<ScrollViewer>("DetailPanel")!;
            Assert.True(panel.PointToScreen(new Point(0, 0)).X < detail.PointToScreen(new Point(0, 0)).X);
            Assert.Equal([OakSacFmg], window.Editor.Aircraft!.RouteFinder!.Routes.Select(r => r.RouteText));

            TopLevel.GetTopLevel(panel)!.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            MainWindowHost.Pump();

            Assert.False(flyout.IsOpen);
            Assert.Null(window.Editor.Aircraft.RouteFinder);
            Assert.DoesNotContain("open", button.Classes);
            Assert.Equal("OLD ROUTE", window.Editor.Session!.Document.Scenario.Aircraft[0].FlightPlan!.Route);
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaFact]
    public async Task FindRoute_AClickOutsideClosesIt()
    {
        ScenarioEditorWindow window = NewWindow();
        try
        {
            ScenarioAircraftView view = await OpenFirstAircraftAsync(window);
            (Flyout flyout, _) = await OpenFindRouteAsync(window, view);
            Assert.True(flyout.IsOpen);

            // Near the grid's bottom-left corner, clear of the flyout, which floats over the grid's right side.
            DataGrid grid = view.FindControl<DataGrid>("AircraftGrid")!;
            Point outside = grid.TranslatePoint(new Point(20, grid.Bounds.Height - 10), window)!.Value;
            window.MouseDown(outside, MouseButton.Left);
            window.MouseUp(outside, MouseButton.Left);
            MainWindowHost.Pump();

            Assert.False(flyout.IsOpen);
            Assert.Null(window.Editor.Aircraft!.RouteFinder);
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaFact]
    public async Task FindRoute_Apply_ClosesIt_AndTheRouteBoxShowsTheRoute()
    {
        ScenarioEditorWindow window = NewWindow();
        try
        {
            ScenarioAircraftView view = await OpenFirstAircraftAsync(window);
            (Flyout flyout, RouteFinderView panel) = await OpenFindRouteAsync(window, view);

            ClickInItsTopLevel(panel.FindControl<Button>("ApplyButton")!);

            Assert.False(flyout.IsOpen);
            Assert.Equal(OakSacFmg, view.FindControl<TextBox>("RouteBox")!.Text);
            Assert.Equal(OakSacFmg, window.Editor.Session!.Document.Scenario.Aircraft[0].FlightPlan!.Route);
            Assert.Equal("Apply found route", window.Editor.Session.UndoDescription);
        }
        finally
        {
            CloseNow(window);
        }
    }

    [AvaloniaTheory]
    [InlineData("CancelButton")]
    [InlineData("CloseButton")]
    public async Task FindRoute_CancelOrTheCloseButton_ClosesItWithoutWriting(string buttonName)
    {
        ScenarioEditorWindow window = NewWindow();
        try
        {
            ScenarioAircraftView view = await OpenFirstAircraftAsync(window);
            (Flyout flyout, RouteFinderView panel) = await OpenFindRouteAsync(window, view);
            string before = window.Editor.Session!.Document.Write();

            ClickInItsTopLevel(panel.FindControl<Button>(buttonName)!);

            Assert.False(flyout.IsOpen);
            Assert.Equal(before, window.Editor.Session.Document.Write());
            Assert.Equal("OLD ROUTE", view.FindControl<TextBox>("RouteBox")!.Text);
        }
        finally
        {
            CloseNow(window);
        }
    }

    // Clicks Find Route… and waits for the search to show its routes.
    private static async Task<(Flyout Flyout, RouteFinderView Panel)> OpenFindRouteAsync(ScenarioEditorWindow window, ScenarioAircraftView view)
    {
        Button button = view.FindControl<Button>("FindRouteButton")!;
        button.BringIntoView();
        MainWindowHost.Pump();
        ClickInItsTopLevel(button);
        RouteFinderViewModel finder = Assert.IsType<RouteFinderViewModel>(window.Editor.Aircraft!.RouteFinder);
        await finder.SearchTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        MainWindowHost.Pump();
        Flyout flyout = Assert.IsType<Flyout>(button.Flyout);
        return (flyout, Assert.IsType<RouteFinderView>(flyout.Content));
    }

    // Opens the OAK copy, shows the Aircraft tab with its first aircraft selected and filed KOAK to KRNO on route OLD ROUTE.
    private static async Task<ScenarioAircraftView> OpenFirstAircraftAsync(ScenarioEditorWindow window)
    {
        window.Width = 1400;
        window.Height = 900;
        window.Show();
        MainWindowHost.Pump();
        await window.BrowserReady.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        ScenarioBrowserViewModel browser = window.Browser;
        browser.SelectedTab = ScenarioBrowserTab.Local;
        browser.SelectedLocalRow = browser.LocalRows.Single(r => r.Id == OakId);
        await browser.OpenCommand.ExecuteAsync(null);
        await window.Editor.OpenTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await window.Editor.General!.PickersLoaded.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        window.FindControl<TabItem>("AircraftTab")!.IsSelected = true;
        MainWindowHost.Pump();
        ScenarioAircraftViewModel aircraft = window.Editor.Aircraft!;
        aircraft.Selected = aircraft.Rows[0];
        MainWindowHost.Pump();
        aircraft.HasFlightPlan = true;
        Commit(() => aircraft.Departure = "KOAK", aircraft.CommitDepartureCommand);
        Commit(() => aircraft.Destination = "KRNO", aircraft.CommitDestinationCommand);
        Commit(() => aircraft.FlightPlanRoute = "OLD ROUTE", aircraft.CommitFlightPlanRouteCommand);
        MainWindowHost.Pump();
        return window.FindControl<ScenarioAircraftView>("AircraftView")!;
    }

    private static void Commit(Action set, System.Windows.Input.ICommand command)
    {
        set();
        command.Execute(null);
    }

    // Clicks the middle of a control in the top level it shows in (the flyout's own, for its content).
    private static void ClickInItsTopLevel(Control control)
    {
        TopLevel top = TopLevel.GetTopLevel(control)!;
        Point at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        top.MouseDown(at, MouseButton.Left);
        top.MouseUp(at, MouseButton.Left);
        MainWindowHost.Pump();
    }

    // Closes without the unsaved-changes question, which would show a real dialog and hold the window open.
    private static void CloseNow(ScenarioEditorWindow window)
    {
        window.CanCloseNow = () => Task.FromResult(true);
        window.Close();
    }

    // A window over the committed OAK scenario whose data-api answers ZOA's config from the committed snapshot, and whose
    // Find Route searches only the stub source.
    private static ScenarioEditorWindow NewWindow()
    {
        TestVnasData.EnsureAircraftDataInitialized();
        var store = new LocalScenarioStore(YaatPaths.Combine("route-finder-tests", Guid.NewGuid().ToString("N")));
        store.CreateLocalScenario(Zoa, File.ReadAllText(SimTestData("issue153-s2-oak-5-2-scenario.json")), OakId);
        var preferences = new UserPreferences();
        string snapshot = File.ReadAllText(SimTestData("artcc-zoa-snapshot.json"));
        var configs = new ArtccConfigSource(
            new HttpClient(new ZoaOnlyHandler(snapshot)),
            YaatPaths.Combine("route-finder-tests", Guid.NewGuid().ToString("N"), "artcc"),
            () => DateTime.UtcNow
        );
        var routeFinding = new RouteFindingServices(
            new HttpClient(new ZoaOnlyHandler(snapshot)),
            RouteFindingServices.NoEndpoint,
            () => [new StubSource()]
        );
        return new ScenarioEditorWindow(
            preferences,
            prompts =>
                ScenarioBrowserServices.ForApp(preferences, prompts, new StubAccess([Zoa])) with
                {
                    Store = store,
                    Download = NeverFinishes,
                    Configs = configs,
                    RouteFinding = routeFinding,
                },
            _ => new FakeFilePickerService(),
            NoLayouts,
            Task.CompletedTask
        );
    }

    private static Task<AirportGroundLayout?> NoLayouts(string airportId, CancellationToken ct) => Task.FromResult<AirportGroundLayout?>(null);

    private static async Task<DownloadReport> NeverFinishes(string artccId, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new OperationCanceledException(ct);
    }

    // A file of the Sim tests' committed test data, found from the test's output folder up.
    private static string SimTestData(string name)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "tests", "Yaat.Sim.Tests", "TestData", name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException($"{name} is not in tests/Yaat.Sim.Tests/TestData above {AppContext.BaseDirectory}");
    }

    // One csko route over real fixes, so it survives the finder's check on the committed navdata.
    private sealed class StubSource : IRouteSource
    {
        public string Name => "csko";

        public Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct) =>
            Task.FromResult(new RouteSourceResult([new FoundRoute(OakSacFmg, 5, ["csko"], null, null, null)], null));
    }

    private sealed class StubAccess(string[] artccs) : IArtccAccessSource
    {
        public Task<IReadOnlyList<string>> GetEditableArtccsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(artccs);
    }

    // data-api answers ZOA with the snapshot and every other request as the network being down.
    private sealed class ZoaOnlyHandler(string zoa) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            (request.RequestUri?.AbsolutePath.EndsWith("/" + Zoa, StringComparison.Ordinal) == true)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(zoa, Encoding.UTF8, "application/json") })
                : Task.FromException<HttpResponseMessage>(new HttpRequestException("the network is down"));
    }
}
