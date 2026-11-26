using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.FieldMenus;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Client.Views.Scope.Toolbar.Dcb;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The STARS DCB's MAPS, BRITE, CHAR SIZE and PREF submenus in the Scope host: each opens from its Main page tile with
/// CRC's dimmed main buttons, its live tiles act through the classic bar's own path, and DONE or Esc returns to the Main
/// page.
/// </summary>
public class StarsDcbSubmenuTests : IDisposable
{
    private static readonly ActivePositionDto Sector44 = new(
        Callsign: "OAK_44_CTR",
        Name: "Sector 44",
        RadioName: null,
        Frequency: "135.100",
        FacilityId: "ZOA",
        FacilityName: "Oakland Center",
        PositionType: "Artcc",
        HandoffCode: "C44",
        EramSectorId: "44"
    );

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-dcb-submenu-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>A STARS-profile Scope host in a window, with a pref store and maps of <paramref name="starsIds"/>.</summary>
    private async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeHost Host, Window Window)> ShowStarsHostAsync(params int[] starsIds)
    {
        MainViewModel main = await ScopeTestFixture.MainAsync();
        main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], "C44", Sector44, 3, NoStarsDisplay: true));
        main.ScopeMaps.ApplyVideoMapsDtoForTesting(
            new FacilityVideoMapsDto(
                "ZOA",
                "NCT",
                [new StarsAreaDto("A", "Area A", 37.62, -122.38, 60, [])],
                [.. starsIds.Select(id => new VideoMapInfoDto($"m{id}", $"Map {id}", $"MAP{id}", [], "A", id, false, false))],
                []
            ),
            null,
            null
        );
        var store = new ScopePrefSetStore(_root, AppLog.CreateLogger<ScopePrefSetStore>());
        var scope = new ScopeViewModel(main, store, profileStore: null, ScopePrefSetStore.PrimarySlot);
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

    private static void Close(Window window)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static StarsDcbBar Bar(ScopeHost host) => Assert.Single(host.GetVisualDescendants().OfType<StarsDcbBar>());

    /// <summary>The first tile on the bar's shown page whose entry matches <paramref name="match"/>.</summary>
    private static DcbTile Tile(ScopeHost host, Func<StarsDcbEntry, bool> match)
    {
        StarsDcbBar bar = Bar(host);
        int index = bar.Entries.ToList().FindIndex(e => match(e));
        Assert.True(index >= 0);
        return bar.Tiles[index];
    }

    private static DcbTile Labelled(ScopeHost host, string firstLine) => Tile(host, e => e.Label.Split('\n')[0] == firstLine);

    /// <summary>Presses the Main page tile that opens <paramref name="page"/>.</summary>
    private static void Open(Window window, ScopeHost host, StarsDcbPage page)
    {
        Assert.Equal(StarsDcbPage.Main, Bar(host).Page);
        Press(window, Tile(host, e => e.Opens == page));
        Assert.Equal(page, Bar(host).Page);
    }

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

    private static void Esc(Window window)
    {
        window.DispatchKey(Key.Escape);
        Settle(window);
    }

    private static void FocusCanvas(Window window, ScopeView view)
    {
        Point? inWindow = view.Canvas.TranslatePoint(new Point(5, view.Canvas.Bounds.Height - 5), window);
        Assert.NotNull(inWindow);
        window.MouseDown(inWindow.Value, MouseButton.Left);
        window.MouseUp(inWindow.Value, MouseButton.Left);
        Settle(window);
    }

    [AvaloniaFact]
    public async Task Maps_OpensTheSubmenu_WithTheDimmedMainButtonsAndThirtyTwoSlots()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync(2, 10);
        try
        {
            Open(window, host, StarsDcbPage.Maps);

            StarsDcbBar bar = Bar(host);
            Assert.True(scope.IsBarSubmenuOpen);
            Assert.Equal(StarsSubmenus.Maps.Count, bar.Tiles.Count);
            Assert.All(bar.Tiles.Take(StarsSubmenus.DimmedCountOf(StarsDcbPage.Maps)), t => Assert.Equal(DcbTileKind.Disabled, t.Kind));
            Assert.Equal(StarsSubmenus.MapSlots, bar.Entries.Count(e => e.MapSlot >= 0));
            DcbTile[] slots = [.. bar.Entries.Select((e, i) => (e, i)).Where(p => p.e.MapSlot >= 0).Select(p => bar.Tiles[p.i])];
            Assert.Equal(["2\nMAP2", "10\nMAP10"], slots.Take(2).Select(t => t.Label));
            Assert.All(slots.Take(2), t => Assert.Equal(DcbTileKind.Toggle, t.Kind));
            Assert.All(slots.Skip(2), t => Assert.Equal((DcbTileKind.Disabled, ""), (t.Kind, t.Label)));
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task Maps_ASlotTogglesItsMap_AndClrAllClearsThem()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync(2, 10);
        try
        {
            Open(window, host, StarsDcbPage.Maps);

            Press(window, Tile(host, e => e.MapSlot == 1));
            Assert.True(scope.MapToggles[1].IsOn);
            Assert.False(scope.MapToggles[0].IsOn);
            Assert.True(Tile(host, e => e.MapSlot == 1).IsPushed);
            Press(window, Tile(host, e => e.MapSlot == 0));
            Assert.True(scope.MapToggles[0].IsOn);

            Press(window, Labelled(host, "CLR ALL"));

            Assert.All(scope.MapToggles, t => Assert.False(t.IsOn));
            Assert.Equal(StarsDcbPage.Maps, Bar(host).Page);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task Done_ReturnsToTheMainPage()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync(2);
        try
        {
            Open(window, host, StarsDcbPage.Maps);

            Press(window, Labelled(host, "DONE"));

            Assert.Equal(StarsDcbPage.Main, Bar(host).Page);
            Assert.Equal(StarsMainPage.Entries.Count, Bar(host).Tiles.Count);
            Assert.False(scope.IsBarSubmenuOpen);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task Esc_WithTheMeasuringToolArmed_ReturnsToMain_ThenTheNextEscDisarmsTheTool()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync(2);
        try
        {
            Open(window, host, StarsDcbPage.Maps);
            FocusCanvas(window, host.ScopeView);
            scope.ArmMeasure();
            Assert.True(main.Measure.IsMeasuring);

            Esc(window);

            Assert.Equal(StarsDcbPage.Main, Bar(host).Page);
            Assert.False(scope.IsBarSubmenuOpen);
            Assert.True(main.Measure.IsMeasuring);

            Esc(window);

            Assert.False(main.Measure.IsMeasuring);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task AProfileChange_WhileMapsIsOpen_ClearsTheSubmenuFlag()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync(2);
        try
        {
            Open(window, host, StarsDcbPage.Maps);
            Assert.True(scope.IsBarSubmenuOpen);

            main.RunScopeCommand(".scope profile mixed");
            Settle(window);

            Assert.Empty(host.GetVisualDescendants().OfType<StarsDcbBar>());
            Assert.False(scope.IsBarSubmenuOpen);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task Brite_MpaWheelWhileLatched_StepsMapA_EscRestoresIt_AndASecondEscReturnsToMain()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync(2);
        try
        {
            Open(window, host, StarsDcbPage.Brite);
            int before = scope.Brightness.MapA;
            DcbTile mpa = Labelled(host, "MPA");
            Assert.Equal($"MPA\n{before}", mpa.Label);

            Press(window, mpa);
            Assert.True(mpa.IsLatched);
            Wheel(window, mpa, -1);

            Assert.Equal(before - 5, scope.Brightness.MapA);
            Assert.Equal($"MPA\n{before - 5}", mpa.Label);

            Esc(window);

            Assert.False(mpa.IsLatched);
            Assert.Equal(before, scope.Brightness.MapA);
            Assert.Equal(StarsDcbPage.Brite, Bar(host).Page);
            Assert.True(scope.IsBarSubmenuOpen);

            Esc(window);

            Assert.Equal(StarsDcbPage.Main, Bar(host).Page);
            Assert.False(scope.IsBarSubmenuOpen);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task Brite_PosAndOth_BothShowTheTargetsValue()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync(2);
        try
        {
            scope.RunCommand(".scope brite targets 45");
            Open(window, host, StarsDcbPage.Brite);

            Assert.Equal("POS\n45", Labelled(host, "POS").Label);
            Assert.Equal("OTH\n45", Labelled(host, "OTH").Label);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task CharSize_DataBlocksWheel_StepsTheCharSize()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync(2);
        try
        {
            scope.RunCommand(ScopeToolbarButtons.DbCharCommand(12));
            Open(window, host, StarsDcbPage.CharSize);
            DcbTile dataBlocks = Labelled(host, "DATA");
            Assert.Equal("DATA\nBLOCKS\n12", dataBlocks.Label);

            Press(window, dataBlocks);
            Wheel(window, dataBlocks, 1);
            Assert.Equal(13, scope.DbSettings.CharSize);
            Assert.Equal("DATA\nBLOCKS\n13", dataBlocks.Label);
            Press(window, dataBlocks);

            Assert.False(dataBlocks.IsLatched);
            Assert.Equal(13, scope.DbSettings.CharSize);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task Pref_ShowsTheSets_ASlotActivatesItsSet_AndSaveAsRunsTheClassicPress()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync(2);
        try
        {
            main.RunScopeCommand(".scope pref saveas A");
            main.RunScopeCommand(".scope pref saveas B");
            Open(window, host, StarsDcbPage.Pref);

            DcbTile[] slots = [.. Bar(host).Tiles.Take(StarsSubmenus.PrefSlots)];
            Assert.Equal(["A", "B", "3"], slots.Take(3).Select(t => t.Label));
            Assert.Equal([DcbTileKind.Normal, DcbTileKind.Normal, DcbTileKind.Disabled], slots.Take(3).Select(t => t.Kind));
            Assert.True(slots[1].IsPushed);
            Assert.False(slots[0].IsPushed);

            Press(window, slots[0]);

            Assert.Equal("A", scope.ActivePrefSet);
            Assert.True(Bar(host).Tiles[0].IsPushed);
            Assert.False(Bar(host).Tiles[1].IsPushed);

            Press(window, Labelled(host, "SAVE AS"));

            Assert.Equal(ScopeToolbarButtons.PrefSaveAsPrefix, main.CommandText);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task EveryDisabledTile_InEverySubmenu_PressChangesNothing()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync(2);
        try
        {
            foreach (StarsDcbPage page in new[] { StarsDcbPage.Maps, StarsDcbPage.Brite, StarsDcbPage.CharSize, StarsDcbPage.Pref })
            {
                Open(window, host, page);
                int[] disabled = [.. Bar(host).Tiles.Select((t, i) => (t, i)).Where(p => p.t.Kind == DcbTileKind.Disabled).Select(p => p.i)];
                Assert.NotEmpty(disabled);
                main.StatusText = "untouched";
                (double, ScopeBrightness, ScopeDbSettings, bool, string) before = (
                    scope.RangeNm,
                    scope.Brightness,
                    scope.DbSettings,
                    scope.MapToggles[0].IsOn,
                    main.CommandText
                );

                foreach (int index in disabled)
                {
                    DcbTile tile = Bar(host).Tiles[index];
                    Press(window, tile);
                    Assert.False(tile.IsPushed);
                }

                Assert.Equal(page, Bar(host).Page);
                Assert.Equal("untouched", main.StatusText);
                Assert.Equal(before, (scope.RangeNm, scope.Brightness, scope.DbSettings, scope.MapToggles[0].IsOn, main.CommandText));
                Press(window, Labelled(host, "DONE"));
            }
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task Docked_EscInTheCommandBox_WithMapsOpen_IsTheCommandBoxs_AndTheSubmenuStays()
    {
        (MainWindow main, MainViewModel vm) = await MainWindowHost.BootDockedScopeAsync();
        try
        {
            vm.RunScopeCommand(".scope profile stars");
            MainWindowHost.Pump();
            main.UpdateLayout();
            MainWindowHost.Pump();
            ScopeHost host = Assert.Single(main.GetVisualDescendants().OfType<ScopeHost>());
            Open(main, host, StarsDcbPage.Maps);
            TextBox box = main.GetVisualDescendants().OfType<TextBox>().First(b => b.Name == "CommandInput");
            Assert.True(box.Focus());
            Dispatcher.UIThread.RunJobs();
            vm.CommandText = "abc";

            main.DispatchKey(Key.Escape);
            MainWindowHost.Pump();

            Assert.Equal("", vm.CommandText);
            Assert.Equal(StarsDcbPage.Maps, Bar(host).Page);
            Assert.True(vm.PrimaryScope.IsBarSubmenuOpen);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public async Task Esc_WithAFieldPopupOpenAndMapsOpen_ClosesThePopup_ThenTheNextEscReturnsToMain()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync(2);
        ScopeView view = host.ScopeView;
        try
        {
            main.Preferences.SetScopeStarsFieldMenus(true);
            main.Preferences.SetScopeFieldMenuStyle(ScopeFieldMenuStyle.Tabbed);
            main.Aircraft.Add(
                new AircraftModel
                {
                    Callsign = "UAL238",
                    AircraftType = "B738",
                    FiledAircraftType = "B738",
                    FlightRules = "IFR",
                    TransponderMode = "C",
                    Altitude = 5000,
                    GroundSpeed = 250,
                    Position = new LatLon(37.62, -122.38),
                }
            );
            Settle(window);
            Open(window, host, StarsDcbPage.Maps);
            FocusCanvas(window, view);
            view.OnFieldClicked("UAL238", ScopeFieldAction.OpenAltitudeMenu, default, KeyModifiers.None);
            Settle(window);
            ScopeDataPopupBase popup = Assert.IsAssignableFrom<ScopeDataPopupBase>(view.ActiveFieldPopup);
            Assert.True(popup.IsOpen);
            // The key goes in where the popup holds the focus; its route tunnels through the window, as the app's does.
            TopLevel top = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(Assert.IsAssignableFrom<Control>(popup.Popup.Child)));

            top.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Settle(window);

            Assert.Equal((false, StarsDcbPage.Maps, true), (popup.IsOpen, Bar(host).Page, scope.IsBarSubmenuOpen));

            Esc(window);

            Assert.Equal(StarsDcbPage.Main, Bar(host).Page);
            Assert.False(scope.IsBarSubmenuOpen);
        }
        finally
        {
            view.ActiveFieldPopup?.Close();
            Dispatcher.UIThread.RunJobs();
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task ADataContextChange_WhileMapsIsOpen_ClearsTheFlagOnTheOldScope_AndShowsTheMainPage()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync(2);
        var second = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        try
        {
            Open(window, host, StarsDcbPage.Maps);
            StarsDcbBar bar = Bar(host);
            Assert.True(scope.IsBarSubmenuOpen);

            bar.DataContext = second;
            Settle(window);

            Assert.False(scope.IsBarSubmenuOpen);
            Assert.False(second.IsBarSubmenuOpen);
            Assert.Equal(StarsDcbPage.Main, bar.Page);
            Assert.Equal(StarsMainPage.Entries.Count, bar.Tiles.Count);
        }
        finally
        {
            Close(window);
            second.Dispose();
        }
    }

    /// <summary>Presses the tile on the shown page that opens <paramref name="page"/>.</summary>
    private static void OpenFromShown(Window window, ScopeHost host, StarsDcbPage page)
    {
        Press(window, Tile(host, e => e.Opens == page));
        Assert.Equal(page, Bar(host).Page);
    }

    [AvaloniaFact]
    public async Task Yaat_ShowsTheYaatPage_AndDoneReturnsToMain()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            Open(window, host, StarsDcbPage.Yaat);

            Assert.Equal(StarsSubmenus.EntriesOf(StarsDcbPage.Yaat), Bar(host).Entries);
            Assert.True(scope.IsBarSubmenuOpen);

            Press(window, Labelled(host, "DONE"));

            Assert.Equal(StarsDcbPage.Main, Bar(host).Page);
            Assert.False(scope.IsBarSubmenuOpen);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task YaatTools_EscReturnsToTheYaatPage_ThenToMain()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            Open(window, host, StarsDcbPage.Yaat);
            OpenFromShown(window, host, StarsDcbPage.YaatTools);
            FocusCanvas(window, host.ScopeView);

            Esc(window);

            Assert.Equal(StarsDcbPage.Yaat, Bar(host).Page);
            Assert.True(scope.IsBarSubmenuOpen);
            Assert.Null(scope.OpenMenu);

            Esc(window);

            Assert.Equal(StarsDcbPage.Main, Bar(host).Page);
            Assert.False(scope.IsBarSubmenuOpen);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task YaatTools_ArmsTheRingTool_AsTheClassicToolsTrayDoes()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            Open(window, host, StarsDcbPage.Yaat);
            Assert.Equal(ScopeRingTool.None, scope.ArmedRingTool);

            OpenFromShown(window, host, StarsDcbPage.YaatTools);

            Assert.Equal(ScopeButtonId.Tools, scope.OpenMenu);
            Assert.Equal(ScopeRingTool.JRing, scope.ArmedRingTool);
            Assert.True(Labelled(host, "J-RING").IsPushed);

            Press(window, Labelled(host, "CONE"));
            Assert.Equal(ScopeRingTool.Cone, scope.ArmedRingTool);
            Press(window, Labelled(host, "J-RING"));
            Assert.Equal(ScopeRingTool.JRing, scope.ArmedRingTool);
            Assert.True(Labelled(host, "J-RING").IsPushed);
            Assert.False(Labelled(host, "CONE").IsPushed);
            Press(window, Labelled(host, "J-RING"));
            Assert.Equal(ScopeRingTool.None, scope.ArmedRingTool);

            Press(window, Labelled(host, "J-RING"));
            Press(window, Labelled(host, "DONE"));

            Assert.Equal(StarsDcbPage.Yaat, Bar(host).Page);
            Assert.Null(scope.OpenMenu);
            Assert.Equal(ScopeRingTool.None, scope.ArmedRingTool);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task YaatTools_BarLeavesTheTreeAndComesBack_ShowsMain_WithTheTrayClosed()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            Open(window, host, StarsDcbPage.Yaat);
            OpenFromShown(window, host, StarsDcbPage.YaatTools);
            Assert.Equal(ScopeRingTool.JRing, scope.ArmedRingTool);

            window.Content = null;
            Settle(window);
            window.Content = host;
            Settle(window);

            Assert.Equal(StarsDcbPage.Main, Bar(host).Page);
            Assert.False(scope.IsBarSubmenuOpen);
            Assert.Null(scope.OpenMenu);
            Assert.Equal(ScopeRingTool.None, scope.ArmedRingTool);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task YaatPilotLens_IsLitWhileTheLensIsOn()
    {
        (MainViewModel main, _, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            main.RunScopeCommand(".scope lens on");
            Settle(window);
            Open(window, host, StarsDcbPage.Yaat);

            Assert.True(Labelled(host, "PILOT").IsPushed);

            main.RunScopeCommand(".scope lens off");
            Settle(window);

            Assert.False(Labelled(host, "PILOT").IsPushed);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task YaatLens_OpensTheLensTray_AndLeavingClosesIt()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            Open(window, host, StarsDcbPage.Yaat);
            OpenFromShown(window, host, StarsDcbPage.YaatLens);

            Assert.Equal(ScopeButtonId.Lens, scope.OpenMenu);

            Press(window, Labelled(host, "DONE"));

            Assert.Equal(StarsDcbPage.Yaat, Bar(host).Page);
            Assert.Null(scope.OpenMenu);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task YaatDisplay_DcnfShowsItsMode()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            Open(window, host, StarsDcbPage.Yaat);
            OpenFromShown(window, host, StarsDcbPage.YaatDisplay);
            Assert.Equal("DCNF", Labelled(host, "DCNF").Label);

            Press(window, Labelled(host, "DCNF"));
            string? mode = scope.SpinnerValue(ScopeToolbarButtons.Find(ScopeButtonId.DisplayDcnf)!);

            Assert.NotNull(mode);
            Assert.Equal($"DCNF\n{mode}", Labelled(host, "DCNF").Label);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task YaatProfile_LightsTheActiveProfile()
    {
        (_, _, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            Open(window, host, StarsDcbPage.Yaat);
            OpenFromShown(window, host, StarsDcbPage.YaatProfile);

            Assert.Equal((true, false, false), (Labelled(host, "STARS").IsPushed, Labelled(host, "ERAM").IsPushed, Labelled(host, "MIXED").IsPushed));
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task YaatTools_TrayClosedByAnotherRoute_ReturnsToTheYaatPage()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            Open(window, host, StarsDcbPage.Yaat);
            OpenFromShown(window, host, StarsDcbPage.YaatTools);

            scope.OpenMenu = null;
            Settle(window);

            Assert.Equal(StarsDcbPage.Yaat, Bar(host).Page);
            Assert.True(scope.IsBarSubmenuOpen);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task YaatToolsSize_LatchThenEsc_PutsTheRingSizeBack()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            Open(window, host, StarsDcbPage.Yaat);
            OpenFromShown(window, host, StarsDcbPage.YaatTools);
            DcbTile size = Labelled(host, "SIZE");
            double before = scope.RingSizeNm;

            Press(window, size);
            Assert.True(size.IsLatched);
            Wheel(window, size, 1);
            Assert.NotEqual(before, scope.RingSizeNm);

            Esc(window);

            Assert.False(size.IsLatched);
            Assert.Equal(before, scope.RingSizeNm);
            Assert.Equal(StarsDcbPage.YaatTools, Bar(host).Page);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task YaatTools_LeftByAProfileSwitch_ClosesTheTrayAndDisarms()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            Open(window, host, StarsDcbPage.Yaat);
            OpenFromShown(window, host, StarsDcbPage.YaatTools);

            main.RunScopeCommand(".scope profile eram");
            Settle(window);

            Assert.Null(scope.OpenMenu);
            Assert.Equal(ScopeRingTool.None, scope.ArmedRingTool);
            Assert.False(scope.IsBarSubmenuOpen);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task YaatTools_LeftByADataContextChange_ClosesTheTrayAndDisarms()
    {
        (_, ScopeViewModel scope, ScopeHost host, Window window) = await ShowStarsHostAsync();
        try
        {
            Open(window, host, StarsDcbPage.Yaat);
            OpenFromShown(window, host, StarsDcbPage.YaatTools);

            Bar(host).DataContext = null;
            Settle(window);

            Assert.Null(scope.OpenMenu);
            Assert.Equal(ScopeRingTool.None, scope.ArmedRingTool);
            Assert.False(scope.IsBarSubmenuOpen);
        }
        finally
        {
            Close(window);
        }
    }
}
