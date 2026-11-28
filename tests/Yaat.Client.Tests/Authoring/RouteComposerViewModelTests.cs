using Xunit;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests.Authoring;

/// <summary>
/// The route composer's view model on the committed navdata around SFO: a click snapped to a fix adds that fix, any other
/// click is anchored to an FRD or, with no DME NAVAID in range, written as a lat/lon token; a click inserts after the
/// selected point; a move re-resolves the point; every add, move and delete is one undo step; with no navdata the status
/// says so and a click adds nothing; Save refuses a blank name and lists the route under its ARTCC only until Show all
/// ARTCCs is checked.
/// </summary>
public sealed class RouteComposerViewModelTests : IDisposable
{
    private readonly string _root = YaatPaths.Combine("route-composer-tests", Guid.NewGuid().ToString("N"));

    public RouteComposerViewModelTests() => TestVnasData.EnsureInitialized();

    private static NavigationDatabase NavDb => NavigationDatabase.Instance;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (string path in Directory.GetFiles(_root))
            {
                File.Delete(path);
            }

            Directory.Delete(_root);
        }
    }

    [Fact]
    public void AClickSnappedToSfo_AddsTheFixSfo()
    {
        RouteComposerViewModel vm = Open("ZOA");
        LatLon sfo = FixPosition("SFO");

        Assert.True(vm.AddAt(new LatLon(sfo.Lat + 0.001, sfo.Lon), "SFO"));

        RouteComposerPoint point = Assert.Single(vm.Points);
        Assert.Equal(RoutePoint.AtFix("SFO"), point.Point);
        Assert.Equal(sfo, point.Position);
        Assert.Equal(0, vm.SelectedIndex);
        Assert.Equal("SFO", vm.NavPathText);
    }

    [Fact]
    public void AnUnsnappedClick_AnchorsToAnFrdWithinOneNmOrWarns()
    {
        RouteComposerViewModel vm = Open("ZOA");
        LatLon sfo = FixPosition("SFO");
        var click = new LatLon(sfo.Lat + 0.11, sfo.Lon + 0.07);

        Assert.True(vm.AddAt(click, null));

        RouteComposerPoint point = Assert.Single(vm.Points);
        Assert.Equal(RoutePointKind.Frd, point.Point.Kind);
        Assert.Matches("^[A-Z0-9]{2,5}\\d{6}$", point.Text);
        Assert.Equal(point.Text, vm.NavPathText);
        double movedNm = GeoMath.DistanceNm(click, point.Position!.Value);
        Assert.True((movedNm <= RoutePointResolver.DisplacementWarningNm) || (point.Warning is not null), $"moved {movedNm:0.00} NM, no warning");
        Assert.Equal(point.Point.Fix, AnchorNameOf(point.Text!));
        Assert.Equal(FixPosition(point.Point.Fix!), point.AnchorPosition);
    }

    [Fact]
    public void AClickFarFromEveryDmeNavaid_AddsALatLonPoint()
    {
        RouteComposerViewModel vm = Open("ZOA");
        LatLon[] candidates = [new(30.0, -140.0), new(10.0, -150.0), new(-40.0, -120.0), new(-60.0, 0.0)];
        LatLon ocean = candidates.First(c => NavDb.FixesWithin(c, RoutePointResolver.NavaidAnchorRangeNm).All(f => !NavDb.IsDmeRadialNavaid(f.Name)));

        Assert.True(vm.AddAt(ocean, null));

        RouteComposerPoint point = Assert.Single(vm.Points);
        Assert.Equal(RoutePointKind.LatLon, point.Point.Kind);
        Assert.Matches(@"^\d{4}[NS]/\d{5}[EW]$", point.Text);
        Assert.Equal(point.Text, vm.NavPathText);
        Assert.Equal("", vm.MessageText);
        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void AClickAtAPlainVor_AddsALatLonPointAtTheToken()
    {
        RouteComposerViewModel vm = Open("ZOA");
        LatLon vcv = FixPosition("VCV");
        var click = new LatLon(vcv.Lat - (2.0 / 60.0), vcv.Lon);

        Assert.True(vm.AddAt(click, null));

        RouteComposerPoint point = Assert.Single(vm.Points);
        Assert.Equal("3434N/11723W", point.Text);
        Assert.Equal(RoutePoint.AtLatLon(new LatLon(34 + (34 / 60.0), -(117 + (23 / 60.0)))), point.Point);
        Assert.Equal("3434N/11723W", vm.NavPathText);
    }

    [Fact]
    public void WithAPointSelected_AClickInsertsAfterIt()
    {
        RouteComposerViewModel vm = Open("ZOA");
        AddFixes(vm, "SFO", "SUNOL", "OAK");

        vm.SelectedIndex = 1;
        Assert.True(vm.AddAt(FixPosition("SJC"), "SJC"));

        Assert.Equal(["SFO", "SUNOL", "SJC", "OAK"], vm.Points.Select(p => p.Point.Fix));
        Assert.Equal(2, vm.SelectedIndex);
        Assert.Equal("SFO SUNOL SJC OAK", vm.NavPathText);
    }

    [Fact]
    public void AMove_ReResolvesThePoint_OneUndoRestoresIt_AndRedoReappliesIt()
    {
        RouteComposerViewModel vm = Open("ZOA");
        AddFixes(vm, "SFO", "OAK");
        RouteComposerPoint before = vm.Points[0];
        LatLon sfo = FixPosition("SFO");

        Assert.True(vm.MovePoint(0, new LatLon(sfo.Lat - 0.12, sfo.Lon + 0.05), null));

        RouteComposerPoint moved = vm.Points[0];
        Assert.Equal(RoutePointKind.Frd, moved.Point.Kind);
        Assert.Equal(2, vm.Points.Count);

        vm.UndoCommand.Execute(null);
        Assert.Equal(before, vm.Points[0]);
        Assert.Equal("SFO OAK", vm.NavPathText);

        vm.RedoCommand.Execute(null);
        Assert.Equal(moved, vm.Points[0]);
        Assert.Equal($"{moved.Text} OAK", vm.NavPathText);
    }

    [Fact]
    public void DeleteSelected_RemovesThePoint_AndUndoRestoresItAtTheSameIndex()
    {
        RouteComposerViewModel vm = Open("ZOA");
        AddFixes(vm, "SFO", "SUNOL", "OAK");
        RouteComposerPoint middle = vm.Points[1];

        vm.SelectedIndex = 1;
        vm.DeleteSelectedCommand.Execute(null);

        Assert.Equal(["SFO", "OAK"], vm.Points.Select(p => p.Point.Fix));

        vm.UndoCommand.Execute(null);
        Assert.Equal(["SFO", "SUNOL", "OAK"], vm.Points.Select(p => p.Point.Fix));
        Assert.Equal(middle, vm.Points[1]);
        Assert.Equal(1, vm.SelectedIndex);
    }

    [Fact]
    public void NavdataNotLoaded_SaysSo_AndAClickAddsNothing()
    {
        var vm = new RouteComposerViewModel(Context("ZOA"), new RouteLibrary(_root), () => null);

        Assert.Equal("Navdata is loading…", vm.StatusText);
        Assert.False(vm.AddAt(new LatLon(37.6, -122.4), "SFO"));
        Assert.False(vm.AddAt(new LatLon(37.6, -122.4), null));
        Assert.Empty(vm.Points);
    }

    [Fact]
    public void Save_WithABlankName_IsRefused()
    {
        RouteComposerViewModel vm = Open("ZOA");
        AddFixes(vm, "SFO", "OAK");
        vm.Name = "   ";

        vm.SaveCommand.Execute(null);

        Assert.Equal("A route needs a name.", vm.MessageText);
        Assert.Empty(new RouteLibrary(_root).List(null).Routes);
    }

    [Fact]
    public void ASavedRoute_IsListedUnderItsArtcc_HiddenUnderAnotherUntilShowAll_AndReloadsTheSamePoints()
    {
        RouteComposerViewModel zoa = Open("ZOA");
        AddFixes(zoa, "SFO", "SUNOL");
        Assert.True(zoa.AddAt(new LatLon(37.75, -121.7), null));
        zoa.Name = "Bay tour";
        zoa.Color = "#FF8800";
        RoutePoint[] points = [.. zoa.Points.Select(p => p.Point)];

        zoa.SaveCommand.Execute(null);

        SavedRoute listed = Assert.Single(zoa.Routes);
        Assert.Equal(("Bay tour", "ZOA", "#FF8800"), (listed.Name, listed.ArtccId, listed.Color));

        RouteComposerViewModel zla = Open("ZLA");
        Assert.Empty(zla.Routes);
        zla.ShowAllArtccs = true;
        Assert.Equal(listed.Id, Assert.Single(zla.Routes).Id);

        RouteComposerViewModel reopened = Open("ZOA");
        reopened.SelectedRoute = Assert.Single(reopened.Routes);
        Assert.Equal(points, reopened.Points.Select(p => p.Point));
        Assert.Equal(("Bay tour", "#FF8800"), (reopened.Name, reopened.Color));
        Assert.Equal(zoa.NavPathText, reopened.NavPathText);
    }

    [Fact]
    public void ASnapToADuplicatedIdent_ShowsThatTheSimReadsTheOtherInstance()
    {
        (NavigationDatabase db, LatLon terminal) = DuplicatedAddmm();
        var vm = new RouteComposerViewModel(Context("ZOA", new FixResolutionContext("KSFO", null)), new RouteLibrary(_root), () => db);

        Assert.True(vm.AddAt(terminal, "ADDMM"));

        Assert.StartsWith("Point 1: The sim reads ADDMM as another fix of that name, 30.0 NM away", vm.MessageText);
    }

    [Fact]
    public void ANavdataProviderThatLoadsLater_IsReadAtEachClick()
    {
        NavigationDatabase? current = null;
        var vm = new RouteComposerViewModel(Context("ZOA"), new RouteLibrary(_root), () => current);

        Assert.False(vm.AddAt(FixPosition("SFO"), "SFO"));
        Assert.Equal("Navdata is loading…", vm.StatusText);

        current = NavDb;
        Assert.True(vm.AddAt(FixPosition("SFO"), "SFO"));
        Assert.Single(vm.Points);
        Assert.Equal(RouteComposerViewModel.ReadyText, vm.StatusText);
    }

    [Fact]
    public void ResavingARouteMadeInAnotherArtcc_KeepsItsArtcc()
    {
        RouteComposerViewModel zla = Open("ZLA");
        AddFixes(zla, "SFO");
        zla.Name = "Made in ZLA";
        zla.SaveCommand.Execute(null);

        RouteComposerViewModel zoa = Open("ZOA");
        zoa.ShowAllArtccs = true;
        zoa.SelectedRoute = Assert.Single(zoa.Routes);
        AddFixes(zoa, "OAK");
        zoa.SaveCommand.Execute(null);

        SavedRoute saved = Assert.Single(new RouteLibrary(_root).List(null).Routes);
        Assert.Equal("ZLA", saved.ArtccId);
        Assert.Equal(2, saved.Points.Count);
    }

    [Fact]
    public void ASaveTheLibraryCannotWrite_SaysWhy()
    {
        Directory.CreateDirectory(_root);
        string notAFolder = Path.Combine(_root, "routes-file");
        File.WriteAllText(notAFolder, "");
        var vm = new RouteComposerViewModel(Context("ZOA"), new RouteLibrary(notAFolder), () => NavDb);
        AddFixes(vm, "SFO");
        vm.Name = "Blocked";

        vm.SaveCommand.Execute(null);

        Assert.StartsWith("Couldn't save Blocked: ", vm.MessageText);
        Assert.Null(vm.LoadedRouteId);
    }

    [Fact]
    public void CorruptRouteFiles_AreNamedOnceInTheMessageLine()
    {
        Directory.CreateDirectory(_root);
        string corrupt = Guid.NewGuid().ToString("D") + ".json";
        File.WriteAllText(Path.Combine(_root, corrupt), "{ not json");

        RouteComposerViewModel vm = Open("ZOA");

        Assert.Equal($"Couldn't read route files: {corrupt}", vm.MessageText);
        vm.MessageText = "";
        vm.ShowAllArtccs = true;
        Assert.Equal("", vm.MessageText);
    }

    [Fact]
    public async Task AMapLoadFinishingAfterCancel_FillsNothing()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<RouteComposerMap>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new RouteComposerContext(
            "ZOA",
            FixResolutionContext.None,
            _ =>
            {
                started.SetResult();
                return pending.Task;
            },
            (_, _) => Task.FromResult<VideoMapData?>(null)
        );
        var vm = new RouteComposerViewModel(context, new RouteLibrary(_root), () => NavDb);
        using var cts = new CancellationTokenSource();

        Task load = vm.LoadMapsAsync(cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        pending.SetResult([new RouteComposerMap("M", "M")]);
        await load.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Empty(vm.Maps);
        Assert.Equal("", vm.MessageText);
    }

    [Fact]
    public async Task LoadMapsAsync_ListsTheMaps_AndLoadsNone()
    {
        List<string> asked = [];
        var vm = new RouteComposerViewModel(
            MapContext(
                [new("East", "EAST"), new("West", "WEST")],
                (id, _) =>
                {
                    asked.Add(id);
                    return Task.FromResult<VideoMapData?>(null);
                }
            ),
            new RouteLibrary(_root),
            () => NavDb
        );

        await vm.LoadMapsAsync(CancellationToken.None);

        Assert.Equal(["East", "West"], vm.Maps.Select(m => m.Name));
        Assert.All(vm.Maps, m => Assert.Null(m.Data));
        Assert.All(vm.Maps, m => Assert.False(m.IsEnabled));
        Assert.Empty(asked);
    }

    [Fact]
    public async Task TickingALayer_LoadsItOnce_AndTickingAgainAfterUntickingLoadsNothing()
    {
        VideoMapData east = new() { MapId = "EAST", Lines = [] };
        List<string> asked = [];
        var vm = new RouteComposerViewModel(
            MapContext(
                [new("East", "EAST")],
                (id, _) =>
                {
                    asked.Add(id);
                    return Task.FromResult<VideoMapData?>(east);
                }
            ),
            new RouteLibrary(_root),
            () => NavDb
        );
        await vm.LoadMapsAsync(CancellationToken.None);
        RouteComposerMapLayer layer = Assert.Single(vm.Maps);

        layer.IsEnabled = true;
        await vm.MapLoadTask.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(["EAST"], asked);
        Assert.Same(east, layer.Data);
        Assert.True(layer.IsEnabled);

        layer.IsEnabled = false;
        layer.IsEnabled = true;
        await vm.MapLoadTask.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(["EAST"], asked);
        Assert.Same(east, layer.Data);
    }

    [Fact]
    public async Task ALayerWhoseMapIsNotCached_SaysSoAndUnticks()
    {
        var vm = new RouteComposerViewModel(
            MapContext([new("East", "EAST")], (_, _) => Task.FromResult<VideoMapData?>(null)),
            new RouteLibrary(_root),
            () => NavDb
        );
        await vm.LoadMapsAsync(CancellationToken.None);
        RouteComposerMapLayer layer = Assert.Single(vm.Maps);

        layer.IsEnabled = true;
        await vm.MapLoadTask.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal("The video map East is not cached; it needs one online open.", vm.MessageText);
        Assert.False(layer.IsEnabled);
        Assert.Null(layer.Data);
        Assert.False(layer.IsLoading);
    }

    [Fact]
    public async Task ALayerThatFailsToLoad_SaysWhyAndUnticks()
    {
        var vm = new RouteComposerViewModel(
            MapContext([new("East", "EAST")], (_, _) => Task.FromException<VideoMapData?>(new IOException("the disk is gone"))),
            new RouteLibrary(_root),
            () => NavDb
        );
        await vm.LoadMapsAsync(CancellationToken.None);
        RouteComposerMapLayer layer = Assert.Single(vm.Maps);

        layer.IsEnabled = true;
        await vm.MapLoadTask.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal("Could not load the video map East: the disk is gone", vm.MessageText);
        Assert.False(layer.IsEnabled);
        Assert.Null(layer.Data);
        Assert.False(layer.IsLoading);
    }

    [Fact]
    public async Task ALoadCompletedAfterUnticking_StoresTheMap_AndLeavesTheLayerOff()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<VideoMapData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoMapData east = new() { MapId = "EAST", Lines = [] };
        var vm = new RouteComposerViewModel(
            MapContext(
                [new("East", "EAST")],
                (_, _) =>
                {
                    started.TrySetResult();
                    return pending.Task;
                }
            ),
            new RouteLibrary(_root),
            () => NavDb
        );
        await vm.LoadMapsAsync(CancellationToken.None);
        RouteComposerMapLayer layer = Assert.Single(vm.Maps);

        layer.IsEnabled = true;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        layer.IsEnabled = false;
        pending.SetResult(east);
        await vm.MapLoadTask.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Same(east, layer.Data);
        Assert.False(layer.IsEnabled);
        Assert.False(layer.IsLoading);
    }

    [Fact]
    public async Task TickingALayerAgainWhileItLoads_StartsNoSecondLoad()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<VideoMapData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoMapData east = new() { MapId = "EAST", Lines = [] };
        List<string> asked = [];
        var vm = new RouteComposerViewModel(
            MapContext(
                [new("East", "EAST")],
                (id, _) =>
                {
                    asked.Add(id);
                    started.TrySetResult();
                    return pending.Task;
                }
            ),
            new RouteLibrary(_root),
            () => NavDb
        );
        await vm.LoadMapsAsync(CancellationToken.None);
        RouteComposerMapLayer layer = Assert.Single(vm.Maps);

        layer.IsEnabled = true;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        layer.IsEnabled = false;
        layer.IsEnabled = true;
        pending.SetResult(east);
        await vm.MapLoadTask.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Single(asked);
        Assert.Same(east, layer.Data);
    }

    // ADDMM twice: the real KSFO terminal waypoint from the committed CIFP and a NavData fix 0.5 degrees north of it, the one
    // the sim reads first (a case real data cannot express goes through a database of its own, tests/CLAUDE.md).
    private static (NavigationDatabase Db, LatLon Terminal) DuplicatedAddmm()
    {
        Assert.True(NavDb.TryGetTerminalWaypoint("KSFO", "ADDMM", out (double Lat, double Lon) terminal));
        var db = NavigationDatabase.ForTesting(
            fixes: new Dictionary<string, (double Lat, double Lon)> { ["ADDMM"] = (terminal.Lat + 0.5, terminal.Lon) },
            cifpFilePath: TestVnasData.GetCifpPath()
        );
        return (db, new LatLon(terminal.Lat, terminal.Lon));
    }

    private static string AnchorNameOf(string frd) => frd[..^6];

    private static LatLon FixPosition(string name)
    {
        (double Lat, double Lon) pos = NavDb.GetFixPosition(name) ?? throw new InvalidOperationException(name + " is not in navdata");
        return new LatLon(pos.Lat, pos.Lon);
    }

    private static void AddFixes(RouteComposerViewModel vm, params string[] names)
    {
        foreach (string name in names)
        {
            vm.SelectedIndex = vm.Points.Count == 0 ? null : vm.Points.Count - 1;
            Assert.True(vm.AddAt(FixPosition(name), name), name);
        }
    }

    private static RouteComposerContext Context(string artccId) => Context(artccId, FixResolutionContext.None);

    private static RouteComposerContext MapContext(
        IReadOnlyList<RouteComposerMap> maps,
        Func<string, CancellationToken, Task<VideoMapData?>> loadMap
    ) => new("ZOA", FixResolutionContext.None, _ => Task.FromResult(maps), loadMap);

    private static RouteComposerContext Context(string artccId, FixResolutionContext fixes) =>
        new(artccId, fixes, _ => Task.FromResult<IReadOnlyList<RouteComposerMap>>([]), (_, _) => Task.FromResult<VideoMapData?>(null));

    private RouteComposerViewModel Open(string artccId) => new(Context(artccId), new RouteLibrary(_root), () => NavDb);
}
