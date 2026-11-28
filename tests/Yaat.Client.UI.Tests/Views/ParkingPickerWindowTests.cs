using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Gates;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The parking picker window with no room, over the real KOAK vNAS map served by a stub HTTP handler: it shows the
/// stands with the current one selected and returns the clicked stand's name on OK; Esc and Cancel return null; a 404
/// and an unreachable vNAS with nothing cached say "No vNAS map for OAK"; a loader that fails (or cancels on its own)
/// says why; the loader runs
/// off the UI thread; and picking and closing leave the vNAS cache folder as the load wrote it (only that folder is
/// checked: closing also saves the window's geometry to the preferences).
/// </summary>
public sealed class ParkingPickerWindowTests : IDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    private static readonly UserPreferences Preferences = new();

    private readonly string _root = YaatPaths.Combine("parking-picker-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (IDisposable owned in _owned)
        {
            owned.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task OpensWithAStubMapAndNoRoom_ReturnsTheClickedStandsName()
    {
        AirportLayoutDownloader downloader = Downloader(_ => Ok());
        string current = OakParkingLayout.FirstParkingName;
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("KOAK", current.ToLowerInvariant(), Preferences, downloader.GetLayoutAsync, null);
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);
            ParkingPickerCanvas canvas = picker.FindControl<ParkingPickerCanvas>("MapCanvas")!;
            Button ok = picker.FindControl<Button>("OkButton")!;

            Assert.NotEmpty(canvas.Scene.Stands);
            Assert.Equal(current, canvas.Selected);
            Assert.True(ok.IsEnabled);
            Assert.Equal("KOAK", picker.FindControl<TextBlock>("AirportText")!.Text);

            (ParkingPickerStand stand, Point at) = OakParkingLayout.IsolatedStand(canvas.Scene, canvas.Viewport, notNamed: current);
            Click(picker, canvas, at);
            Assert.Equal(stand.Name, canvas.Selected);
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(stand.Name, await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EscOrCancel_ReturnsNull(bool escape)
    {
        AirportLayoutDownloader downloader = Downloader(_ => Ok());
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("OAK", null, Preferences, downloader.GetLayoutAsync, null);
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);
            ParkingPickerCanvas canvas = picker.FindControl<ParkingPickerCanvas>("MapCanvas")!;
            (_, Point at) = OakParkingLayout.IsolatedStand(canvas.Scene, canvas.Viewport);
            Click(picker, canvas, at);
            Assert.NotNull(canvas.Selected);

            if (escape)
            {
                picker.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            }
            else
            {
                picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }

            Dispatcher.UIThread.RunJobs();

            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoMap_OnA404_OrUnreachableWithNoCache_SaysNoVnasMap(bool unreachable)
    {
        AirportLayoutDownloader downloader = Downloader(_ =>
            unreachable ? throw new HttpRequestException("offline") : new HttpResponseMessage(HttpStatusCode.NotFound)
        );
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("OAK", "A1", Preferences, downloader.GetLayoutAsync, null);
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);

            Assert.Equal("No vNAS map for OAK", picker.FindControl<TextBlock>("StatusText")!.Text);
            Assert.Empty(picker.FindControl<ParkingPickerCanvas>("MapCanvas")!.Scene.Stands);
            Assert.False(picker.FindControl<Button>("OkButton")!.IsEnabled);

            picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task PickingAndClosing_LeaveTheVnasCacheAsTheLoadWroteIt()
    {
        AirportLayoutDownloader downloader = Downloader(_ => Ok());
        string cachePath = downloader.GetCachePath("KOAK");
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("KOAK", null, Preferences, downloader.GetLayoutAsync, null);
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);
            byte[] bytes = await File.ReadAllBytesAsync(cachePath, TestContext.Current.CancellationToken);
            DateTime lastWrite = File.GetLastWriteTimeUtc(cachePath);
            string[] entries = Directory.GetFileSystemEntries(downloader.CacheDir);
            ParkingPickerCanvas canvas = picker.FindControl<ParkingPickerCanvas>("MapCanvas")!;
            (_, Point at) = OakParkingLayout.IsolatedStand(canvas.Scene, canvas.Viewport);

            Click(picker, canvas, at);
            picker.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(await pending.WaitAsync(HangGuard));

            Assert.Equal(bytes, await File.ReadAllBytesAsync(cachePath, TestContext.Current.CancellationToken));
            Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(cachePath));
            Assert.Equal(entries, Directory.GetFileSystemEntries(downloader.CacheDir));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task ALoaderThatThrows_EndsWithTheCouldNotLoadStatus()
    {
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow(
                "OAK",
                null,
                Preferences,
                (_, _) => Task.FromException<AirportGroundLayout?>(new IOException("disk gone")),
                null
            );
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);

            Assert.Equal("Could not load the vNAS map for OAK: disk gone", picker.FindControl<TextBlock>("StatusText")!.Text);
            Assert.False(picker.FindControl<Button>("OkButton")!.IsEnabled);

            picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheLayoutLoader_RunsOffTheUiThread()
    {
        AirportLayoutDownloader downloader = Downloader(_ => Ok());
        bool? onUiThread = null;
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow(
                "OAK",
                null,
                Preferences,
                (id, ct) =>
                {
                    onUiThread = Dispatcher.UIThread.CheckAccess();
                    return downloader.GetLayoutAsync(id, ct);
                },
                null
            );
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);

            Assert.False(onUiThread);
            Assert.NotEmpty(picker.FindControl<ParkingPickerCanvas>("MapCanvas")!.Scene.Stands);

            picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task ALoaderThatCancelsOnItsOwn_WhileTheWindowIsOpen_EndsWithTheCouldNotLoadStatus()
    {
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow(
                "OAK",
                null,
                Preferences,
                (_, _) => Task.FromException<AirportGroundLayout?>(new OperationCanceledException("gave up")),
                null
            );
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);

            Assert.Equal("Could not load the vNAS map for OAK: gave up", picker.FindControl<TextBlock>("StatusText")!.Text);

            picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Ranked_FromMap_OnATooSmallStand_AssignsIt_AndTheStatusLineNamesTheReason()
    {
        AirportLayoutDownloader downloader = Downloader(_ => Ok());
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("OAK", null, Preferences, downloader.GetLayoutAsync, OakRanking("UAL1", "B748", 5));
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);
            picker.FindControl<RadioButton>("MapMode")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            ParkingPickerCanvas canvas = picker.FindControl<ParkingPickerCanvas>("MapCanvas")!;
            (ParkingPickerStand stand, Point at) = IsolatedWith(canvas, GateReason.TooSmall);

            Click(picker, canvas, at);

            string status = picker.FindControl<TextBlock>("StatusText")!.Text!;
            Assert.StartsWith($"{stand.Name.Trim()}: too small (span cap ", status);
            Assert.EndsWith(" ft); assigned anyway", status);
            Assert.Equal(stand.Name, canvas.Selected);
            picker.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(stand.Name, await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Ranked_Random_PicksWithinTheRankedSet()
    {
        AirportLayoutDownloader downloader = Downloader(_ => Ok());
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("OAK", null, Preferences, downloader.GetLayoutAsync, OakRanking("UAL1", "B738", 11));
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);
            ParkingPickerCanvas canvas = picker.FindControl<ParkingPickerCanvas>("MapCanvas")!;

            Assert.True(picker.FindControl<RadioButton>("RandomMode")!.IsChecked);
            string picked = canvas.Selected!;
            Assert.Contains(canvas.Scene.StatusOf(picked), new GateReason?[] { GateReason.Preferred, GateReason.Fits });
            Assert.StartsWith(picked.Trim() + ": ", picker.FindControl<TextBlock>("StatusText")!.Text);
            Assert.Equal(canvas.Scene.Status.Count, picker.FindControl<ListBox>("RankList")!.ItemCount);
            Assert.True(picker.FindControl<Button>("OkButton")!.IsEnabled);

            picker.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(picked, await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ranked_EscOrCancel_ReturnsNull(bool escape)
    {
        AirportLayoutDownloader downloader = Downloader(_ => Ok());
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("OAK", null, Preferences, downloader.GetLayoutAsync, OakRanking("UAL1", "B738", 3));
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);
            Assert.NotNull(picker.FindControl<ParkingPickerCanvas>("MapCanvas")!.Selected);

            if (escape)
            {
                picker.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            }
            else
            {
                picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }

            Dispatcher.UIThread.RunJobs();

            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task NullRanking_ShowsThePlainPicker()
    {
        AirportLayoutDownloader downloader = Downloader(_ => Ok());
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("OAK", null, Preferences, downloader.GetLayoutAsync, null);
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);

            Assert.Equal("Pick parking — OAK", picker.Title);
            Assert.False(picker.FindControl<StackPanel>("GateModes")!.IsVisible);
            Assert.False(picker.FindControl<ListBox>("RankList")!.IsVisible);
            Assert.False(picker.FindControl<TextBlock>("NoteText")!.IsVisible);
            Assert.Empty(picker.FindControl<ParkingPickerCanvas>("MapCanvas")!.Scene.Status);
            Assert.Null(picker.FindControl<ParkingPickerCanvas>("MapCanvas")!.Selected);
            Assert.Equal(
                "Click a stand, then OK, or double-click it. Right-drag pans, the wheel zooms.",
                picker.FindControl<TextBlock>("StatusText")!.Text
            );

            picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Ranked_NoFreeStand_SaysParkingUnchanged_ListsEveryStand_AndReturnsNothing()
    {
        GateAssignContext ranking = Occupied(OakRanking("N123AB", "C172", 5));
        int count = ranking.Rank(OakParkingLayout.Layout, GateScope.Any).Count;
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("OAK", null, Preferences, Loads(OakParkingLayout.Layout), ranking);
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);

            Assert.Equal(GateAssignContext.NoFitText, picker.FindControl<TextBlock>("StatusText")!.Text);
            Assert.False(picker.FindControl<Button>("OkButton")!.IsEnabled);
            Assert.Null(picker.FindControl<ParkingPickerCanvas>("MapCanvas")!.Selected);
            List<string> listed = [.. picker.FindControl<ListBox>("RankList")!.Items.Cast<string>()];
            Assert.Equal(count, listed.Count);
            Assert.All(listed, l => Assert.EndsWith(": taken until the scenario's end", l));

            picker.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(pending.IsCompleted);
            picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Ranked_SwitchingToInAreaWithNoArea_OrToFromMap_ClearsTheDraw()
    {
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("OAK", null, Preferences, Loads(OakParkingLayout.Layout), OakRanking("UAL1", "B738", 3));
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);
            ParkingPickerCanvas canvas = picker.FindControl<ParkingPickerCanvas>("MapCanvas")!;
            Button ok = picker.FindControl<Button>("OkButton")!;
            Assert.NotNull(canvas.Selected);

            picker.FindControl<RadioButton>("AreaMode")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(canvas.Selected);
            Assert.False(ok.IsEnabled);

            picker.FindControl<RadioButton>("RandomMode")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(canvas.Selected);

            picker.FindControl<RadioButton>("MapMode")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(canvas.Selected);
            Assert.False(ok.IsEnabled);

            picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Ranked_Random_DrawsFromTheLoadedRanking_WithTheSameSeed()
    {
        string expected = GateRanking.Pick(OakRanking("UAL1", "B738", 11).Rank(OakParkingLayout.Layout, GateScope.Any), new Random(11))!.Stand;
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("OAK", null, Preferences, Loads(OakParkingLayout.Layout), OakRanking("UAL1", "B738", 11));
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);
            ParkingPickerCanvas canvas = picker.FindControl<ParkingPickerCanvas>("MapCanvas")!;

            Assert.Equal(canvas.Scene.FindAnyCase(expected)!.Name, canvas.Selected);

            picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Ranked_InArea_DrawsAStandOfTheChosenArea()
    {
        AirportGroundLayout sfo = SfoLayout.Value;
        GateAssignContext ranking = SfoRanking("N123AB", "C172", 7);
        List<string> inArea = [.. SfoRanking("N123AB", "C172", 7).Rank(sfo, GateScope.Area("SIGNATURE")).Where(c => c.IsFit).Select(c => c.Stand)];
        Assert.NotEmpty(inArea);
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("SFO", null, Preferences, Loads(sfo), ranking);
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);
            ParkingPickerCanvas canvas = picker.FindControl<ParkingPickerCanvas>("MapCanvas")!;
            ComboBox areas = picker.FindControl<ComboBox>("AreaBox")!;

            picker.FindControl<RadioButton>("AreaMode")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("SIGNATURE", areas.Items.Cast<string>());
            areas.SelectedItem = "SIGNATURE";
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(canvas.Selected!.Trim(), inArea);
            Assert.True(picker.FindControl<Button>("OkButton")!.IsEnabled);
            picker.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(canvas.Selected, await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Ranked_WhileNavdataLoads_ShowsTheNote()
    {
        GateAssignContext loaded = OakRanking("UAL1", "B738", 3);
        GateAssignContext ranking = loaded with { Sidecars = AirportSidecarCatalog.Empty, NavdataLoading = true };
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("OAK", null, Preferences, Loads(OakParkingLayout.Layout), ranking);
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);

            TextBlock note = picker.FindControl<TextBlock>("NoteText")!;
            Assert.True(note.IsVisible);
            Assert.Equal("Navdata loading; ranking without airline tags", note.Text);

            picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    // A1x stands lie in TERMINALA (tagged UAL) first and in SECOND (untagged) too; B1, SECOND's other stand, is taken, so the
    // draw in SECOND is an A1x stand, which fits there though the whole-airport ranking calls it another airline's.
    [AvaloniaFact]
    public async Task Ranked_InArea_OnAStandOfTwoDifferentlyTaggedAreas_ListAndStatusLineGiveTheSameReason()
    {
        AirportGroundLayout sfo = SfoLayout.Value;
        var sidecars = new AirportSidecarCatalog([
            new AirportSidecar("SFO")
            {
                ParkingAreas =
                [
                    new ParkingAreaAlias("TERMINALA", ["A*"], null) { MaxAdg = AirplaneDesignGroup.IV, Airlines = ["UAL"] },
                    new ParkingAreaAlias("SECOND", ["A1*", "B1"], null) { MaxAdg = AirplaneDesignGroup.IV },
                ],
            },
        ]);
        var aircraft = new ScenarioAircraft
        {
            Id = "a1",
            AircraftId = "DAL123",
            AircraftType = "A320",
            AirportId = "SFO",
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.Parking },
        };
        var holder = new ScenarioAircraft
        {
            Id = "b1",
            AircraftId = "N9BX",
            AircraftType = "C172",
            AirportId = "SFO",
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.Parking, Parking = "B1" },
        };
        var ranking = new GateAssignContext(new Scenario { Aircraft = [aircraft, holder] }, aircraft, sidecars, false, new Random(5));
        Window owner = ShowOwner();
        try
        {
            var picker = new ParkingPickerWindow("SFO", null, Preferences, Loads(sfo), ranking);
            Task<string?> pending = picker.ShowAsync(owner);
            await LoadAsync(picker);
            ParkingPickerCanvas canvas = picker.FindControl<ParkingPickerCanvas>("MapCanvas")!;

            picker.FindControl<RadioButton>("AreaMode")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            picker.FindControl<ComboBox>("AreaBox")!.SelectedItem = "SECOND";
            Dispatcher.UIThread.RunJobs();

            string stand = canvas.Selected!.Trim();
            Assert.StartsWith("A1", stand);
            string listed = Assert.Single(
                picker.FindControl<ListBox>("RankList")!.Items.Cast<string>(),
                l => l.StartsWith(stand + ": ", StringComparison.Ordinal)
            );
            Assert.Equal($"{stand}: fits", listed);
            Assert.Equal(listed, picker.FindControl<TextBlock>("StatusText")!.Text);

            picker.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(await pending.WaitAsync(HangGuard));
        }
        finally
        {
            owner.Close();
        }
    }

    private static readonly Lazy<AirportGroundLayout> SfoLayout = new(() =>
        GeoJsonParser.Parse(
            "sfo",
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sfo.geojson")),
            "SFO",
            new LayoutInputs([], VideoMap: null, SpotEdges: [], CornerFillets: CornerFilletOverrides.None)
        )
    );

    // A loader that hands back the given layout, so a test ranks the very layout the window shows.
    private static Func<string, CancellationToken, Task<AirportGroundLayout?>> Loads(AirportGroundLayout layout) =>
        (_, _) => Task.FromResult<AirportGroundLayout?>(layout);

    // The ranking with one more aircraft parked, from the start to the scenario's end, at each stand it ranks.
    private static GateAssignContext Occupied(GateAssignContext ranking)
    {
        List<ScenarioAircraft> occupants =
        [
            .. ranking
                .Rank(OakParkingLayout.Layout, GateScope.Any)
                .Select(
                    (c, i) =>
                        new ScenarioAircraft
                        {
                            Id = $"occ{i}",
                            AircraftId = $"N9{i}X",
                            AircraftType = "C172",
                            AirportId = "OAK",
                            StartingConditions = new StartingConditions { Type = StartingConditionKinds.Parking, Parking = c.Stand },
                        }
                ),
        ];
        return ranking with { Scenario = new Scenario { Aircraft = [ranking.Aircraft, .. occupants] } };
    }

    private static GateAssignContext SfoRanking(string callsign, string type, int seed)
    {
        GateAssignContext oak = OakRanking(callsign, type, seed);
        oak.Aircraft.AirportId = "SFO";
        return oak;
    }

    // A ranking for one OAK Parking start alone in its scenario, over the committed sidecars, drawing from a seeded Random.
    private static GateAssignContext OakRanking(string callsign, string type, int seed)
    {
        TestVnasData.EnsureInitialized();
        var aircraft = new ScenarioAircraft
        {
            Id = "a1",
            AircraftId = callsign,
            AircraftType = type,
            AirportId = "OAK",
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.Parking },
        };
        return new GateAssignContext(
            new Scenario { Aircraft = [aircraft] },
            aircraft,
            NavigationDatabase.Instance.AirportSidecars,
            false,
            new Random(seed)
        );
    }

    // A stand of the reason whose nearest other stand is at least three hit ranges away on the screen, with its position.
    private static (ParkingPickerStand Stand, Point At) IsolatedWith(ParkingPickerCanvas canvas, GateReason reason)
    {
        List<(ParkingPickerStand Stand, Point At)> projected =
        [
            .. canvas.Scene.Stands.Select(s =>
            {
                (float x, float y) = canvas.Viewport.LatLonToScreen(s.Lat, s.Lon);
                return (s, new Point(x, y));
            }),
        ];
        foreach ((ParkingPickerStand stand, Point at) in projected.Where(p => canvas.Scene.StatusOf(p.Stand.Name) == reason))
        {
            bool inView = (at.X > 40) && (at.Y > 40) && (at.X < canvas.Viewport.PixelWidth - 40) && (at.Y < canvas.Viewport.PixelHeight - 40);
            if (
                inView
                && (
                    projected.Where(p => !ReferenceEquals(p.Stand, stand)).Min(p => OakParkingLayout.Distance(p.At, at))
                    >= 3 * ParkingPickerHitTest.Range
                )
            )
            {
                return (stand, at);
            }
        }

        throw new InvalidOperationException($"no isolated {reason} stand in view");
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent(OakParkingLayout.GeoJson) };

    private static Window ShowOwner()
    {
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        return owner;
    }

    private static async Task LoadAsync(ParkingPickerWindow picker)
    {
        Dispatcher.UIThread.RunJobs();
        await picker.LoadTask.WaitAsync(HangGuard);
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            picker.UpdateLayout();
        }
    }

    private static void Click(Window window, ParkingPickerCanvas canvas, Point at)
    {
        Point inWindow = canvas.TranslatePoint(at, window)!.Value;
        window.MouseMove(inWindow);
        window.MouseDown(inWindow, MouseButton.Left);
        window.MouseUp(inWindow, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // A downloader over this test's own cache folder whose every request respond answers; vNAS is never reached.
    private AirportLayoutDownloader Downloader(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new StubHandler(respond));
        var downloader = new AirportLayoutDownloader(http, Path.Combine(_root, "cache", "airports"));
        _owned.Add(downloader);
        _owned.Add(http);
        return downloader;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
