using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// Find Route over the aircraft editor: the query comes from the aircraft (flight plan airports, the runway of a runway
/// start, the filed type's suffix, an airline callsign's prefix, the type); the routes keep the finder's order with a
/// badge per source; each failed source shows one line; routes only navdata offers past the first ten wait for Show all
/// navdata routes; Apply replaces the route, fills an empty cruise altitude, and with Use a real callsign and type checked
/// on a row with an observed sample sets a free callsign of its prefix and its type, all as one undo step. Every source
/// is a stub, so no test reaches the network; the finder's route check runs on the committed navdata.
/// </summary>
public sealed class RouteFinderViewModelTests : IDisposable
{
    private const string OakSacFmg = "OAK SAC FMG";
    private const string OakLinFmg = "OAK LIN FMG";

    private static readonly string[] NavdataFixes = ["OAK", "SAC", "FMG", "LIN", "MOD"];

    private readonly ScenarioEditorFixture _fx = new();

    public RouteFinderViewModelTests()
    {
        TestVnasData.EnsureInitialized();
        TestVnasData.EnsureAircraftDataInitialized();
    }

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Query_OnRunwayStart_TakesTheRunway_TheSuffixAfterTheSlash_TheAirlinePrefix_AndTheType()
    {
        (_, ScenarioAircraftViewModel vm) = Open(a => a["startingConditions"] = new JsonObject { ["type"] = "OnRunway", ["runway"] = "30" });

        RouteQuery query = (await Search(vm, new Random(1))).Query;

        Assert.Equal(("KOAK", "KRNO"), (query.Departure, query.Destination));
        Assert.Equal(["30"], query.DepartureRunways);
        Assert.Empty(query.ArrivalRunways);
        Assert.Equal("L", query.EquipmentSuffix);
        Assert.Equal("SWA", query.AirlinePrefix);
        Assert.Equal("B738", query.AircraftType);
    }

    [Fact]
    public async Task Query_ParkingStart_HasNoRunway()
    {
        (_, ScenarioAircraftViewModel vm) = Open();

        RouteQuery query = (await Search(vm, new Random(1))).Query;

        Assert.Empty(query.DepartureRunways);
    }

    [Fact]
    public async Task Query_FiledTypeWithoutASlash_HasNoSuffix()
    {
        (_, ScenarioAircraftViewModel vm) = Open(a => a["flightplan"]!["aircraftType"] = "B738");

        RouteQuery query = (await Search(vm, new Random(1))).Query;

        Assert.Null(query.EquipmentSuffix);
    }

    [Fact]
    public async Task Query_NNumberCallsign_HasNoAirlinePrefix()
    {
        (_, ScenarioAircraftViewModel vm) = Open(a => a["aircraftId"] = "N123AB");

        RouteQuery query = (await Search(vm, new Random(1))).Query;

        Assert.Null(query.AirlinePrefix);
    }

    [Fact]
    public async Task Results_KeepTheFindersOrder_WithABadgePerSource_AndTheirNotes()
    {
        (_, ScenarioAircraftViewModel vm) = Open();
        var preferred = new PreferredRouteInfo("H", null, null, null, null, null, null);

        RouteFinderViewModel finder = await Search(
            vm,
            new Random(1),
            new StubSource("csko", null, Route(OakSacFmg, 5, "csko"), Route(OakLinFmg, 9, "csko")),
            new StubSource("preferred", null, Route(OakSacFmg, 0, "preferred") with { Preferred = preferred })
        );

        Assert.Equal([OakLinFmg, OakSacFmg], finder.Routes.Select(r => r.RouteText));
        Assert.Equal(["csko"], finder.Routes[0].Sources);
        Assert.Equal(["csko", "preferred"], finder.Routes[1].Sources);
        Assert.Equal(["9", "5"], finder.Routes.Select(r => r.CountText));
        Assert.Contains(finder.Routes[1].Notes, n => n.StartsWith("preferred: H route", StringComparison.Ordinal));
        Assert.Empty(finder.Failures);
        Assert.False(finder.IsSearching);
    }

    [Fact]
    public async Task Failures_ShowOneLinePerFailedSource_BesideTheOtherSourcesRoutes()
    {
        (_, ScenarioAircraftViewModel vm) = Open();

        RouteFinderViewModel finder = await Search(
            vm,
            new Random(1),
            new StubSource("csko", null, Route(OakSacFmg, 5, "csko")),
            new StubSource("history", "Not connected to a YAAT server.")
        );

        Assert.Equal(["history: Not connected to a YAAT server."], finder.Failures);
        Assert.Equal([OakSacFmg], finder.Routes.Select(r => r.RouteText));
    }

    [Fact]
    public async Task NavdataOnlyRoutes_PastTheFirstTen_ShowOnlyWithShowAllNavdataRoutes()
    {
        (_, ScenarioAircraftViewModel vm) = Open();
        FoundRoute[] navdata = [.. NavdataPairs().Take(12).Select(text => Route(text, 0, "navdata"))];

        RouteFinderViewModel finder = await Search(
            vm,
            new Random(1),
            new StubSource("csko", null, Route(OakSacFmg, 3, "csko")),
            new StubSource("navdata", null, navdata)
        );

        Assert.Equal(1 + RouteFinderViewModel.NavdataFirstPage, finder.Routes.Count);
        Assert.Equal(OakSacFmg, finder.Routes[0].RouteText);
        Assert.Equal(2, finder.HiddenNavdataCount);

        finder.ShowAllNavdata = true;

        Assert.Equal(13, finder.Routes.Count);
        Assert.Equal(0, finder.HiddenNavdataCount);

        finder.ShowAllNavdata = false;

        Assert.Equal(11, finder.Routes.Count);
    }

    [Fact]
    public async Task Apply_EmptyCruiseAltitude_ReplacesTheRoute_AndFillsTheFiledCruiseAltitude()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open();
        RouteFinderViewModel finder = await Search(vm, new Random(1), new StubSource("csko", null, Route(OakSacFmg, 5, "csko")));
        bool applied = false;
        finder.Applied += (_, _) => applied = true;

        finder.ApplyCommand.Execute(null);

        ScenarioFlightPlan plan = ScenarioAircraftPanel.First(session).FlightPlan!;
        Assert.Equal(OakSacFmg, plan.Route);
        Assert.Equal(23000, plan.CruiseAltitude);
        Assert.Equal(OakSacFmg, vm.FlightPlanRoute);
        Assert.Equal("23000", vm.CruiseAltitude);
        Assert.True(applied);
    }

    [Fact]
    public async Task Apply_SetCruiseAltitude_KeepsIt()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open(a => a["flightplan"]!["cruiseAltitude"] = 31000);
        RouteFinderViewModel finder = await Search(vm, new Random(1), new StubSource("csko", null, Route(OakSacFmg, 5, "csko")));

        finder.ApplyCommand.Execute(null);

        ScenarioFlightPlan plan = ScenarioAircraftPanel.First(session).FlightPlan!;
        Assert.Equal(OakSacFmg, plan.Route);
        Assert.Equal(31000, plan.CruiseAltitude);
    }

    [Fact]
    public async Task Apply_DepartureEqualsDestination_LeavesTheCruiseAltitudeEmpty()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open(a => a["flightplan"]!["destination"] = "KOAK");
        RouteFinderViewModel finder = await Search(vm, new Random(1), new StubSource("csko", null, Route(OakSacFmg, 5, "csko")));

        finder.ApplyCommand.Execute(null);

        ScenarioFlightPlan plan = ScenarioAircraftPanel.First(session).FlightPlan!;
        Assert.Equal(OakSacFmg, plan.Route);
        Assert.Equal(0, plan.CruiseAltitude);
    }

    [Fact]
    public async Task Apply_RealCallsignAndTypeUnchecked_ChangesNeither()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open();
        RouteFinderViewModel finder = await Search(vm, new Random(1), new StubSource("history", null, Observed(OakSacFmg, "UAL", "A320")));
        Assert.False(finder.UseRealCallsignAndType);

        finder.ApplyCommand.Execute(null);

        ScenarioAircraft aircraft = ScenarioAircraftPanel.First(session);
        Assert.Equal(("SWA2190", "B738"), (aircraft.AircraftId, aircraft.AircraftType));
        Assert.Equal(OakSacFmg, aircraft.FlightPlan!.Route);
    }

    [Fact]
    public async Task Apply_RealCallsignAndTypeChecked_TakesThePrefixWithAFreeNumber_AndTheType()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open(a => a["aircraftId"] = "SWA1234");
        RouteFinderViewModel finder = await Search(
            vm,
            new FixedRandom(1234, 4321),
            new StubSource("history", null, Observed(OakSacFmg, "SWA", "B737"))
        );
        finder.UseRealCallsignAndType = true;

        finder.ApplyCommand.Execute(null);

        ScenarioAircraft aircraft = ScenarioAircraftPanel.First(session);
        Assert.Equal(("SWA4321", "B737"), (aircraft.AircraftId, aircraft.AircraftType));
        Assert.Equal("B738/L", aircraft.FlightPlan!.AircraftType);
        Assert.Equal(("KOAK", "KRNO"), (aircraft.FlightPlan.Departure, aircraft.FlightPlan.Destination));
        Assert.Equal("SWA4321", vm.Callsign);
    }

    [Fact]
    public async Task Apply_RealCallsignAndTypeChecked_OnARowWithNoObservedSample_ChangesNeither()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open();
        RouteFinderViewModel finder = await Search(vm, new Random(1), new StubSource("csko", null, Route(OakSacFmg, 5, "csko")));
        Assert.False(finder.CanUseRealCallsignAndType);
        finder.UseRealCallsignAndType = true;

        finder.ApplyCommand.Execute(null);

        ScenarioAircraft aircraft = ScenarioAircraftPanel.First(session);
        Assert.Equal(("SWA2190", "B738"), (aircraft.AircraftId, aircraft.AircraftType));
    }

    [Fact]
    public async Task Apply_IsOneUndoStep_AndOneUndoRestoresRouteAltitudeCallsignAndType()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open();
        string original = session.Document.Write();
        RouteFinderViewModel finder = await Search(vm, new FixedRandom(4321), new StubSource("history", null, Observed(OakSacFmg, "UAL", "A320")));
        finder.UseRealCallsignAndType = true;

        finder.ApplyCommand.Execute(null);

        ScenarioAircraft aircraft = ScenarioAircraftPanel.First(session);
        Assert.Equal(("UAL4321", "A320", OakSacFmg), (aircraft.AircraftId, aircraft.AircraftType, aircraft.FlightPlan!.Route));
        Assert.NotEqual(0, aircraft.FlightPlan.CruiseAltitude);
        Assert.Equal("Apply found route", session.UndoDescription);
        Assert.Equal(1, ScenarioAircraftPanel.UndoSteps(session));

        Assert.True(session.Undo());

        Assert.Equal(original, session.Document.Write());
        Assert.Equal(("SWA2190", "B738", "OLD ROUTE", "0"), (vm.Callsign, vm.AircraftType, vm.FlightPlanRoute, vm.CruiseAltitude));
    }

    [Fact]
    public void FreeCallsign_EveryDrawTaken_FallsBackToTheLowestFreeNumber()
    {
        var scenario = new Scenario();
        scenario.Aircraft.Add(new ScenarioAircraft { AircraftId = "SWA1234" });

        Assert.Equal("SWA100", RouteFinderViewModel.FreeCallsign(scenario, "SWA", new FixedRandom(1234)));
    }

    [Fact]
    public void FreeCallsign_EveryNumberTaken_IsNull()
    {
        var scenario = new Scenario();
        for (int n = RouteFinderViewModel.FirstFlightNumber; n <= RouteFinderViewModel.LastFlightNumber; n++)
        {
            scenario.Aircraft.Add(new ScenarioAircraft { AircraftId = "SWA" + n.ToString(CultureInfo.InvariantCulture) });
        }

        Assert.Null(RouteFinderViewModel.FreeCallsign(scenario, "SWA", new FixedRandom(1234)));
    }

    [Fact]
    public void SuffixAndAirlinePrefix_AreTrimmedAndUpperCased()
    {
        Assert.Equal("L", RouteFinderViewModel.SuffixOf("b738/l"));
        Assert.Equal("SWA", RouteFinderViewModel.AirlinePrefixOf(" swa123 "));
    }

    [Fact]
    public async Task Apply_RealTypeChecked_FilesTheCruiseAltitudeForTheObservedType()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open();
        RouteFinderViewModel finder = await Search(vm, new FixedRandom(4321), new StubSource("history", null, Observed(OakSacFmg, "SWA", "C172")));
        Assert.Equal("23000", finder.CruisePreview);
        Assert.Equal("From the chosen route; the flight plan has none.", finder.CruiseCaption);

        finder.UseRealCallsignAndType = true;

        Assert.Equal("9000", finder.CruisePreview);

        finder.ApplyCommand.Execute(null);

        ScenarioAircraft aircraft = ScenarioAircraftPanel.First(session);
        Assert.Equal("C172", aircraft.AircraftType);
        Assert.Equal(9000, aircraft.FlightPlan!.CruiseAltitude);
    }

    [Theory]
    [InlineData("")]
    [InlineData("S1")]
    public async Task Apply_EmptyOrInvalidObservedPrefix_LeavesTheCallsign_AndSetsTheType(string prefix)
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open();
        RouteFinderViewModel finder = await Search(vm, new FixedRandom(4321), new StubSource("history", null, Observed(OakSacFmg, prefix, "B737")));
        Assert.True(finder.CanUseRealCallsignAndType);
        Assert.Equal("Also apply type B737", finder.RealCallsignText);
        finder.UseRealCallsignAndType = true;

        finder.ApplyCommand.Execute(null);

        ScenarioAircraft aircraft = ScenarioAircraftPanel.First(session);
        Assert.Equal(("SWA2190", "B737"), (aircraft.AircraftId, aircraft.AircraftType));
    }

    [Fact]
    public async Task Apply_UnknownAirport_LeavesTheCruiseAltitudeEmpty()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open(a => a["flightplan"]!["destination"] = "ZZZZ");
        RouteFinderViewModel finder = await Search(vm, new Random(1), new StubSource("csko", null, Route(OakSacFmg, 5, "csko")));

        finder.ApplyCommand.Execute(null);

        ScenarioFlightPlan plan = ScenarioAircraftPanel.First(session).FlightPlan!;
        Assert.Equal(OakSacFmg, plan.Route);
        Assert.Equal(0, plan.CruiseAltitude);
    }

    [Fact]
    public async Task ShowAllNavdataRoutes_KeepsTheSelectedRoute()
    {
        (_, ScenarioAircraftViewModel vm) = Open();
        FoundRoute[] navdata = [.. NavdataPairs().Take(12).Select(text => Route(text, 0, "navdata"))];
        RouteFinderViewModel finder = await Search(
            vm,
            new Random(1),
            new StubSource("csko", null, Route(OakSacFmg, 3, "csko")),
            new StubSource("navdata", null, navdata)
        );
        Assert.True(finder.HasHiddenNavdata);
        Assert.Equal("Show all 12 navdata routes", finder.ShowAllNavdataText);
        finder.SelectedRoute = finder.Routes[5];
        string selected = finder.Routes[5].RouteText;

        finder.ShowAllNavdataRoutesCommand.Execute(null);

        Assert.Equal(13, finder.Routes.Count);
        Assert.False(finder.HasHiddenNavdata);
        Assert.Equal(selected, finder.SelectedRoute?.RouteText);
    }

    [Fact]
    public async Task CancelledSearch_LeavesTheRoutesEmpty()
    {
        (_, ScenarioAircraftViewModel vm) = Open();
        RouteFinderViewModel finder = Assert.IsType<RouteFinderViewModel>(vm.StartRouteFinder([new BlockingSource()], new Random(1)));

        vm.CloseRouteFinder();
        await _fx.Ui.Run(finder.SearchTask);

        Assert.Empty(finder.Routes);
        Assert.Empty(finder.Failures);
        Assert.Null(vm.RouteFinder);
    }

    [Fact]
    public async Task FinderFailure_ShowsTheFailureLine()
    {
        (_, ScenarioAircraftViewModel vm) = Open();

        RouteFinderViewModel finder = await Search(vm, new Random(1), new StubSource("csko", null, Route(null!, 1, "csko")));

        Assert.StartsWith("Find Route failed: ", Assert.Single(finder.Failures), StringComparison.Ordinal);
        Assert.Empty(finder.Routes);
        Assert.False(finder.IsSearching);
    }

    [Fact]
    public async Task Search_WithAnEditedType_SearchesAgainWithIt()
    {
        (_, ScenarioAircraftViewModel vm) = Open();
        var source = new RecordingSource();
        RouteFinderViewModel finder = await Search(vm, new Random(1), source);
        Assert.Equal(("", "/L", "SWA", "B738"), (finder.RunwayInput, finder.SuffixInput, finder.AirlineInput, finder.TypeInput));

        finder.TypeInput = "a320";
        finder.SearchCommand.Execute(null);
        await _fx.Ui.Run(finder.SearchTask);

        Assert.Equal(["B738", "A320"], source.Types());
        Assert.Equal("A320", finder.Query.AircraftType);
        Assert.Equal(("L", "SWA"), (finder.Query.EquipmentSuffix, finder.Query.AirlinePrefix));
    }

    [Fact]
    public async Task RealCallsignText_NamesTheDrawnCallsignAndTheType()
    {
        (_, ScenarioAircraftViewModel vm) = Open();

        RouteFinderViewModel finder = await Search(vm, new FixedRandom(4321), new StubSource("history", null, Observed(OakSacFmg, "SWA", "B737")));

        Assert.True(finder.CanUseRealCallsignAndType);
        Assert.Equal("SWA4321", finder.DrawnCallsign);
        Assert.Equal("Also apply callsign SWA4321 and type B737", finder.RealCallsignText);
    }

    [Fact]
    public async Task CruisePreview_SetAltitude_ReadsKeeps()
    {
        (_, ScenarioAircraftViewModel vm) = Open(a => a["flightplan"]!["cruiseAltitude"] = 31000);

        RouteFinderViewModel finder = await Search(vm, new Random(1), new StubSource("csko", null, Route(OakSacFmg, 5, "csko")));

        Assert.Equal("Keeps 31000", finder.CruisePreview);
        Assert.Equal("The flight plan already says 31000; it is kept.", finder.CruiseCaption);
    }

    [Fact]
    public async Task Cancel_RequestsTheClose_AndWritesNothing()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open();
        string original = session.Document.Write();
        RouteFinderViewModel finder = await Search(vm, new Random(1), new StubSource("csko", null, Route(OakSacFmg, 5, "csko")));
        bool closed = false;
        bool applied = false;
        finder.CloseRequested += (_, _) => closed = true;
        finder.Applied += (_, _) => applied = true;

        finder.CancelCommand.Execute(null);

        Assert.True(closed);
        Assert.False(applied);
        Assert.Equal(original, session.Document.Write());
    }

    [Fact]
    public async Task Apply_AfterTheSelectionMovedToAnotherAircraft_WritesNothing()
    {
        (ScenarioEditSession session, ScenarioAircraftViewModel vm) = Open();
        RouteFinderViewModel finder = await Search(vm, new Random(1), new StubSource("csko", null, Route(OakSacFmg, 5, "csko")));
        string original = session.Document.Write();
        vm.Selected = vm.Rows[1];

        finder.ApplyCommand.Execute(null);

        Assert.Equal(original, session.Document.Write());
    }

    [Fact]
    public async Task ShowAllNavdataRoutes_KeepsTheDrawnCallsign()
    {
        (_, ScenarioAircraftViewModel vm) = Open();
        FoundRoute[] navdata = [.. NavdataPairs().Take(12).Select(text => Route(text, 0, "navdata"))];
        RouteFinderViewModel finder = await Search(
            vm,
            new FixedRandom(4321, 5678, 6789),
            new StubSource("history", null, Observed(OakSacFmg, "SWA", "B737")),
            new StubSource("navdata", null, navdata)
        );
        Assert.Equal("SWA4321", finder.DrawnCallsign);

        finder.ShowAllNavdataRoutesCommand.Execute(null);

        Assert.Equal(OakSacFmg, finder.SelectedRoute?.RouteText);
        Assert.Equal("SWA4321", finder.DrawnCallsign);
        Assert.Equal("Also apply callsign SWA4321 and type B737", finder.RealCallsignText);
    }

    [Fact]
    public async Task ReplacedSearch_FinishingLate_LeavesTheNewSearchesRoutes_AndIsSearchingFollowsTheLiveSearch()
    {
        (_, ScenarioAircraftViewModel vm) = Open();
        var source = new GatedSource();
        RouteFinderViewModel finder = Assert.IsType<RouteFinderViewModel>(vm.StartRouteFinder([source], new Random(1)));
        Task first = finder.SearchTask;
        await source.Asked[0].Task.WaitAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        finder.SearchCommand.Execute(null);
        Task second = finder.SearchTask;
        await source.Asked[1].Task.WaitAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        source.Release[0].SetResult();
        await _fx.Ui.Run(first);

        Assert.Empty(finder.Routes);
        Assert.True(finder.IsSearching);

        source.Release[1].SetResult();
        await _fx.Ui.Run(second);

        Assert.Equal([OakSacFmg], finder.Routes.Select(r => r.RouteText));
        Assert.False(finder.IsSearching);
    }

    [Fact]
    public async Task SourcePills_CountEachSourcesRoutes_AndAFailedSourceIsRed()
    {
        (_, ScenarioAircraftViewModel vm) = Open();

        RouteFinderViewModel finder = await Search(
            vm,
            new Random(1),
            new StubSource("csko", null, Route(OakSacFmg, 5, "csko"), Route(OakLinFmg, 4, "csko"), Route("OAK MOD FMG", 3, "csko")),
            new StubSource("preferred", "Preferred routes are from a stale cycle.", Route(OakSacFmg, 0, "preferred")),
            new StubSource("history", "Not connected to a YAAT server.")
        );

        Assert.Equal(
            [("csko", "csko 3", false), ("preferred", "preferred 1", true), ("history", "history failed", true)],
            finder.SourcePills.Select(p => (p.Source, p.Text, p.Failed))
        );
    }

    [Fact]
    public async Task Search_RunwayBox_SplitsOnCommasAndSpaces_AndUpperCases()
    {
        (_, ScenarioAircraftViewModel vm) = Open();
        RouteFinderViewModel finder = await Search(vm, new Random(1), new StubSource("csko", null, Route(OakSacFmg, 5, "csko")));

        finder.RunwayInput = "30, 28r";
        finder.SearchCommand.Execute(null);
        await _fx.Ui.Run(finder.SearchTask);

        Assert.Equal(["30", "28R"], finder.Query.DepartureRunways);
    }

    // The first aircraft as SWA2190, a B738 parked at KOAK filed to KRNO as a B738/L with no cruise altitude.
    private (ScenarioEditSession Session, ScenarioAircraftViewModel Aircraft) Open(Action<JsonObject>? more = null) =>
        ScenarioAircraftPanel.Open(
            _fx,
            a =>
            {
                a["aircraftId"] = "SWA2190";
                a["aircraftType"] = "B738";
                a["startingConditions"] = new JsonObject { ["type"] = "Parking", ["parking"] = "22" };
                a["flightplan"] = new JsonObject
                {
                    ["rules"] = "IFR",
                    ["departure"] = "KOAK",
                    ["destination"] = "KRNO",
                    ["cruiseAltitude"] = 0,
                    ["cruiseSpeed"] = 450,
                    ["route"] = "OLD ROUTE",
                    ["remarks"] = "",
                    ["aircraftType"] = "B738/L",
                };
                more?.Invoke(a);
            }
        );

    // Opens Find Route for the selected aircraft over the stub sources and waits for its routes to show.
    private async Task<RouteFinderViewModel> Search(ScenarioAircraftViewModel vm, Random random, params IRouteSource[] sources)
    {
        RouteFinderViewModel finder = Assert.IsType<RouteFinderViewModel>(vm.StartRouteFinder(sources, random));
        await _fx.Ui.Run(finder.SearchTask);
        return finder;
    }

    // Two-fix routes over real fixes, each pair once in each order, so every one survives the finder's checks and none merge.
    private static IEnumerable<string> NavdataPairs() =>
        NavdataFixes.SelectMany(a => NavdataFixes.Where(b => !string.Equals(a, b, StringComparison.Ordinal)).Select(b => a + " " + b));

    private static FoundRoute Route(string text, int count, string source) =>
        new(text, count, [source], Published: null, Preferred: null, Observed: null);

    private static FoundRoute Observed(string text, string prefix, string type) =>
        Route(text, 4, "history") with
        {
            Observed = new ObservedRouteSample(prefix, type),
        };

    private sealed class StubSource(string name, string? failure, params FoundRoute[] routes) : IRouteSource
    {
        public string Name => name;

        public Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RouteSourceResult(routes, failure));
        }
    }

    // Each search's answer waits on its own release, ignoring cancellation, so a replaced search can finish late; the
    // first search answers OAK LIN FMG, the second OAK SAC FMG.
    private sealed class GatedSource : IRouteSource
    {
        private int _calls;

        public TaskCompletionSource[] Asked { get; } =
        [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];

        public TaskCompletionSource[] Release { get; } =
        [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];

        public string Name => "csko";

        public async Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct)
        {
            int call = Interlocked.Increment(ref _calls) - 1;
            Asked[call].SetResult();
            await Release[call].Task;
            return new RouteSourceResult([Route(call == 0 ? OakLinFmg : OakSacFmg, 5, "csko")], null);
        }
    }

    // Waits on the search's cancellation and never answers.
    private sealed class BlockingSource : IRouteSource
    {
        public string Name => "csko";

        public async Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new RouteSourceResult([], null);
        }
    }

    // Answers one route and keeps each query it is asked.
    private sealed class RecordingSource : IRouteSource
    {
        private readonly Lock _gate = new();
        private readonly List<RouteQuery> _queries = [];

        public string Name => "csko";

        public string[] Types()
        {
            lock (_gate)
            {
                return [.. _queries.Select(q => q.AircraftType ?? "")];
            }
        }

        public Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct)
        {
            lock (_gate)
            {
                _queries.Add(query);
            }

            return Task.FromResult(new RouteSourceResult([Route(OakSacFmg, 5, "csko")], null));
        }
    }

    // Draws the given numbers in turn, so a test knows the flight numbers a callsign is tried with.
    private sealed class FixedRandom(params int[] draws) : Random
    {
        private int _next;

        public override int Next(int minValue, int maxValue) => draws[_next++ % draws.Length];
    }
}
