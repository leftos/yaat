using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Client.Views.Scope.Toolbar.Eram;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The ERAM bar under the ERAM Scope profile: CRC's Master Toolbar in a bar row of its own height, its menus expanding as
/// CRC's do, and each live button acting through the classic bar's own path.
/// </summary>
public class EramBarTests : IDisposable
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

    private readonly string _root = ScopeStoreFixture.NewStoreRoot();

    public void Dispose() => ScopeStoreFixture.DeleteStoreRoot(_root);

    [AvaloniaFact]
    public async Task Geomap_PressingAFilter_KeepsItsButton_AndTogglesTheMap()
    {
        await using Opened scope = await OpenAsync(31, 2, 10);
        scope.Click(EramButtonType.GeomapMenu);
        EramToolbarButton[] before = [.. scope.Toolbar.MenuButtons];
        EramToolbarButton first = scope.Toolbar.Find(EramButtonType.Filter1)!;

        scope.Click(first);

        Assert.True(scope.Scope.MapToggles[0].IsOn);
        Assert.Same(first, scope.Toolbar.Find(EramButtonType.Filter1));
        Assert.Equal(before, scope.Toolbar.MenuButtons);
        Assert.True(first.IsOn);
    }

    [AvaloniaFact]
    public async Task Geomap_ANewMapList_RebuildsTheOpenMenuWithItsFilters()
    {
        await using Opened scope = await OpenAsync(2, 10);
        scope.Click(EramButtonType.GeomapMenu);
        Assert.Equal(["2", "10"], scope.Toolbar.MenuButtons.Select(b => b.Line1));

        LoadMaps(scope.Main, 5, 6, 7);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["5", "6", "7"], scope.Toolbar.MenuButtons.Select(b => b.Line1));
        Assert.Equal(["5", "6", "7"], scope.Toolbar.MenuButtons.Select(b => b.Key));
    }

    [AvaloniaFact]
    public async Task Prefset_ActivatingAnotherSet_KeepsTheButtons_AndMovesTheLitOne()
    {
        await using Opened scope = await OpenOnSector44Async(ScopeStoreFixture.Store(_root));
        scope.Main.RunScopeCommand(".scope pref saveas A");
        scope.Main.RunScopeCommand(".scope pref saveas B");
        scope.Click(EramButtonType.PrefsetMenu);
        EramToolbarButton[] before = [.. scope.Toolbar.MenuButtons];
        EramToolbarButton a = before.Single(b => b.Key == "A");
        EramToolbarButton b = before.Single(b => b.Key == "B");
        Assert.True(b.IsOn);
        Assert.False(a.IsOn);

        scope.Click(a);

        Assert.Equal("A", scope.Scope.ActivePrefSet);
        Assert.Equal(before, scope.Toolbar.MenuButtons);
        Assert.True(a.IsOn);
        Assert.False(b.IsOn);
    }

    [AvaloniaFact]
    public async Task EramProfile_ShowsTheEramBar_InARowOfItsHeight()
    {
        await using Opened scope = await OpenAsync();

        Assert.Single(scope.Host.GetVisualDescendants().OfType<EramBar>());
        Assert.Empty(scope.Host.GetVisualDescendants().OfType<ScopeToolbar>());
        Assert.Equal(EramMasterLayout.BarHeight, BarRow(scope.Host).Height.Value);

        scope.Main.RunScopeCommand(".scope profile mixed");
        Dispatcher.UIThread.RunJobs();

        Assert.Single(scope.Host.GetVisualDescendants().OfType<ScopeToolbar>());
        Assert.Equal(45, BarRow(scope.Host).Height.Value);
    }

    [AvaloniaFact]
    public async Task Bright_OpensWithItsOutline_HidingTheOtherButtonsInPlace_AndAgainCloses()
    {
        await using Opened scope = await OpenAsync();
        EramMasterToolbar toolbar = scope.Toolbar;
        EramToolbarButton views = toolbar.Find(EramButtonType.ViewsMenu)!;
        Rect viewsBounds = views.Bounds;

        scope.Click(EramButtonType.BrightnessMenu);

        Assert.Equal(EramButtonType.BrightnessMenu, toolbar.OpenMenu);
        Assert.NotNull(toolbar.Expansion);
        Assert.Equal(EramColors.BurntCoral, ((ISolidColorBrush)toolbar.Expansion!.BorderBrush!).Color);
        Assert.Equal(new Thickness(2), toolbar.Expansion.BorderThickness);
        Assert.Contains(toolbar.MenuButtons, b => b.Type == EramButtonType.FdbBrightness);
        Assert.Equal(0, views.Opacity);
        Assert.False(views.IsHitTestVisible);
        Assert.Equal(viewsBounds, views.Bounds);
        Assert.Equal(1, toolbar.Find(EramButtonType.BrightnessMenu)!.Opacity);

        scope.Click(EramButtonType.BrightnessMenu);

        Assert.Null(toolbar.OpenMenu);
        Assert.Null(toolbar.Expansion);
        Assert.Empty(toolbar.MenuButtons);
        Assert.Equal(1, views.Opacity);
        Assert.True(views.IsHitTestVisible);
        Assert.Equal(EramColors.DisabledOpacity, toolbar.Find(EramButtonType.AnnotationMenu)!.Opacity);
    }

    [AvaloniaFact]
    public async Task BrightFdb_MiddleRaisesTheFdbBrightnessOneStep_LeftLowersIt()
    {
        await using Opened scope = await OpenAsync();
        ScopeBrightnessGroup fdb = ScopeBrightness.FindGroup("fdb")!;
        int start = fdb.Min + fdb.Step;
        scope.Scope.RunCommand(ScopeToolbarButtons.BriteCommand("fdb", start));
        scope.Click(EramButtonType.BrightnessMenu);

        scope.Click(EramButtonType.FdbBrightness, MouseButton.Middle);

        Assert.Equal(start + fdb.Step, fdb.Get(scope.Scope.Brightness));
        Assert.StartsWith(".scope brite fdb", scope.Main.StatusText);

        scope.Click(EramButtonType.FdbBrightness, MouseButton.Left);

        Assert.Equal(start, fdb.Get(scope.Scope.Brightness));
    }

    [AvaloniaFact]
    public async Task Vector_MiddleAndLeft_StepTheVectorMinutes()
    {
        await using Opened scope = await OpenAsync();
        scope.Scope.RunCommand(ScopeToolbarButtons.VectorCommand(1));

        scope.Click(EramButtonType.VectorLength, MouseButton.Middle);

        Assert.Equal(ScopeViewModel.SteppedVectorMinutes(1, 1), scope.Scope.EffectiveBand.VectorMin);

        scope.Click(EramButtonType.VectorLength, MouseButton.Left);

        Assert.Equal(1, scope.Scope.EffectiveBand.VectorMin);
    }

    [AvaloniaFact]
    public async Task RadarFilterHistory_StepsTheHistoryCount()
    {
        await using Opened scope = await OpenAsync();
        scope.Scope.RunCommand(ScopeToolbarButtons.HistCommand(2));
        scope.Click(EramButtonType.RadarFilterMenu);

        scope.Click(EramButtonType.HistoryFilter, MouseButton.Middle);

        Assert.Equal(3, scope.Scope.EffectiveBand.History);

        scope.Click(EramButtonType.HistoryFilter, MouseButton.Left);

        Assert.Equal(2, scope.Scope.EffectiveBand.History);
    }

    [AvaloniaFact]
    public async Task Geomap_TogglesTheFirstMap()
    {
        await using Opened scope = await OpenAsync(31, 2, 10);
        scope.Click(EramButtonType.GeomapMenu);
        EramToolbarButton first = scope.Toolbar.Find(EramButtonType.Filter1)!;
        Assert.Equal(("2", "MAP2"), (first.Line1, first.Line2));

        scope.Click(EramButtonType.Filter1);

        Assert.True(scope.Scope.MapToggles[0].IsOn);
        Assert.True(scope.Toolbar.Find(EramButtonType.Filter1)!.IsOn);

        scope.Click(EramButtonType.Filter1);

        Assert.False(scope.Scope.MapToggles[0].IsOn);
    }

    [AvaloniaFact]
    public async Task ViewsCrr_OpensTheCrrView()
    {
        await using Opened scope = await OpenAsync();
        scope.Click(EramButtonType.ViewsMenu);

        scope.Click(EramButtonType.CrrView);

        Assert.True(scope.Scope.IsViewOpen(ScopeToolbarButtons.ViewCrrName));
        Assert.True(scope.Toolbar.Find(EramButtonType.CrrView)!.IsOn);
    }

    [AvaloniaFact]
    public async Task DbFieldsDest_LightsDestAndType_AndShowsTheFourthLine()
    {
        await using Opened scope = await OpenAsync();
        scope.Scope.RunCommand(".scope db 4thline off");
        scope.Click(EramButtonType.DbFieldsMenu);

        scope.Click(EramButtonType.DestinationDisplay);

        Assert.True(scope.Scope.DbSettings.Line4);
        Assert.True(scope.Toolbar.Find(EramButtonType.DestinationDisplay)!.IsOn);
        Assert.True(scope.Toolbar.Find(EramButtonType.AircraftDataDisplay)!.IsOn);
    }

    [AvaloniaFact]
    public async Task DeleteTearoff_ArmsDeleteMode()
    {
        await using Opened scope = await OpenAsync();

        scope.Click(EramButtonType.DeleteTearoff);

        Assert.True(scope.Scope.IsDeletingTearoffs);
        Assert.True(scope.Toolbar.Find(EramButtonType.DeleteTearoff)!.IsOn);
    }

    [AvaloniaFact]
    public async Task DisabledButton_PressChangesNothing()
    {
        await using Opened scope = await OpenAsync();
        scope.Click(EramButtonType.BrightnessMenu);
        ScopeBrightness before = scope.Scope.Brightness;
        string? status = scope.Main.StatusText;
        EramToolbarButton background = scope.Toolbar.Find(EramButtonType.BackgroundBrightness)!;
        Assert.False(background.IsBuilt);
        // The click never reaches the button: a disabled button is not hit-test visible, so the press falls through.
        Assert.False(background.IsHitTestVisible);

        scope.Click(EramButtonType.BackgroundBrightness, MouseButton.Middle);

        Assert.Equal(before, scope.Scope.Brightness);
        Assert.Equal(status, scope.Main.StatusText);
        Assert.Equal(EramButtonType.BrightnessMenu, scope.Toolbar.OpenMenu);
    }

    [AvaloniaFact]
    public async Task MapBright_InsideBright_ExpandsMapAAndMapB_AndMapAStepsBriteMapA()
    {
        await using Opened scope = await OpenAsync();
        ScopeBrightnessGroup mapA = ScopeBrightness.FindGroup("mapa")!;
        int start = mapA.Min + mapA.Step;
        scope.Scope.RunCommand(ScopeToolbarButtons.BriteCommand("mapa", start));
        scope.Click(EramButtonType.BrightnessMenu);

        scope.Click(EramButtonType.MapBrightnessMenu);

        Assert.Equal(EramButtonType.MapBrightnessMenu, scope.Toolbar.OpenSubmenu);
        EramToolbarButton a = scope.Toolbar.Find(EramButtonType.MapBcg1)!;
        EramToolbarButton b = scope.Toolbar.Find(EramButtonType.MapBcg2)!;
        Assert.Equal(("MAP", "A"), (a.Line1, a.Line2));
        Assert.Equal(("MAP", "B"), (b.Line1, b.Line2));
        Assert.NotNull(scope.Toolbar.SubmenuExpansion);
        Assert.Equal(EramColors.BurntCoral, ((ISolidColorBrush)scope.Toolbar.SubmenuExpansion!.BorderBrush!).Color);

        scope.Click(EramButtonType.MapBcg1, MouseButton.Middle);

        Assert.Equal(start + mapA.Step, mapA.Get(scope.Scope.Brightness));
    }

    [AvaloniaFact]
    public async Task Range_WheelUp_StepsTheRangeOneNmIn()
    {
        await using Opened scope = await OpenAsync();
        double before = scope.Scope.RangeNm;

        scope.Wheel(EramButtonType.RangeView, 1);

        Assert.Equal(before - 1, scope.Scope.RangeNm);
        Assert.Equal(ScopeToolbarButtons.RangeLabel(before - 1), scope.Toolbar.Find(EramButtonType.RangeView)!.Line2);
    }

    [AvaloniaFact]
    public async Task FontFdb_MiddleRaisesTheCharSizeOneStep()
    {
        await using Opened scope = await OpenAsync();
        scope.Scope.RunCommand(ScopeToolbarButtons.DbCharCommand(ScopeDbSettings.MinCharSize));
        scope.Click(EramButtonType.FontSizeMenu);

        scope.Click(EramButtonType.FdbFontSize, MouseButton.Middle);

        Assert.Equal(ScopeDbSettings.MinCharSize + 1, scope.Scope.DbSettings.CharSize);
    }

    [AvaloniaFact]
    public async Task ViewsConflictAlert_OpensTheCaView()
    {
        await using Opened scope = await OpenAsync();
        scope.Click(EramButtonType.ViewsMenu);

        scope.Click(EramButtonType.CaView);

        Assert.True(scope.Scope.IsViewOpen(ScopeToolbarButtons.ViewCaName));
        Assert.True(scope.Toolbar.Find(EramButtonType.CaView)!.IsOn);
    }

    [AvaloniaFact]
    public async Task AtcToolsWx_TogglesWeather()
    {
        await using Opened scope = await OpenAsync();
        bool before = scope.Scope.ShowWeather;
        scope.Click(EramButtonType.AtcToolsMenu);

        scope.Click(EramButtonType.WeatherMenu);

        Assert.Equal(!before, scope.Scope.ShowWeather);
        Assert.Equal(!before, scope.Toolbar.Find(EramButtonType.WeatherMenu)!.IsOn);
    }

    [AvaloniaFact]
    public async Task PrefsetSet_LoadsItsSet()
    {
        await using Opened scope = await OpenPrefsetAsync();

        scope.Click(scope.Toolbar.MenuButtons.Single(b => b.Key == "A"));

        Assert.Equal("A", scope.Scope.ActivePrefSet);
        Assert.Equal(".scope pref load A", scope.Main.StatusText);
    }

    [AvaloniaFact]
    public async Task PrefsetSave_SavesTheActiveSet()
    {
        await using Opened scope = await OpenPrefsetAsync();

        scope.Click(EramButtonType.PrefsetSave);

        Assert.StartsWith(ScopeToolbarButtons.PrefSaveCommand, scope.Main.StatusText);
        Assert.Equal("B", scope.Scope.ActivePrefSet);
    }

    [AvaloniaFact]
    public async Task PrefsetDelete_DeletesTheActiveSet()
    {
        await using Opened scope = await OpenPrefsetAsync();

        scope.Click(EramButtonType.PrefsetDelete);

        Assert.Equal(".scope pref delete B", scope.Main.StatusText);
        Assert.DoesNotContain("B", scope.Scope.PrefSetNames);
    }

    [AvaloniaFact]
    public async Task PrefsetSaveAs_TypesThePrefixIntoTheCommandBox()
    {
        await using Opened scope = await OpenPrefsetAsync();

        scope.Click(EramButtonType.PrefsetSaveAs);

        Assert.Equal(".scope pref saveas ", scope.Main.CommandText);
        Assert.Equal(scope.Main.CommandText.Length, scope.Main.CommandCaretIndex);
    }

    [AvaloniaFact]
    public async Task PrefsetRename_TypesTheActiveSetsRenameIntoTheCommandBox()
    {
        await using Opened scope = await OpenPrefsetAsync();

        scope.Click(EramButtonType.PrefsetRename);

        Assert.Equal(".scope pref rename B ", scope.Main.CommandText);
        Assert.Equal(scope.Main.CommandText.Length, scope.Main.CommandCaretIndex);
    }

    [AvaloniaFact]
    public async Task AfterASwitchToMixed_TheOldEramBarNoLongerFollowsTheScope()
    {
        await using Opened scope = await OpenAsync();
        EramBar old = Assert.Single(scope.Host.GetVisualDescendants().OfType<EramBar>());
        EramToolbarButton delete = old.Toolbar.Find(EramButtonType.DeleteTearoff)!;
        Assert.False(delete.IsOn);

        scope.Main.RunScopeCommand(".scope profile mixed");
        Dispatcher.UIThread.RunJobs();
        scope.Scope.Press(ScopeToolbarButtons.Find(ScopeButtonId.DeleteTearoff)!);

        Assert.True(scope.Scope.IsDeletingTearoffs);
        Assert.Null(old.DataContext);
        Assert.False(delete.IsOn);
    }

    [AvaloniaFact]
    public async Task Geomap_WithNoMaps_LightsItsButton_AndExpandsNothing()
    {
        await using Opened scope = await OpenAsync();
        Assert.Empty(scope.Scope.MapToggles);

        scope.Click(EramButtonType.GeomapMenu);

        EramToolbarButton geomap = scope.Toolbar.Find(EramButtonType.GeomapMenu)!;
        Assert.Equal(EramButtonType.GeomapMenu, scope.Toolbar.OpenMenu);
        Assert.True(geomap.IsOpenMenu);
        Assert.Equal(EramColors.BurntCoral, ((ISolidColorBrush)geomap.PickArea.Background!).Color);
        Assert.Null(scope.Toolbar.Expansion);
        Assert.Empty(scope.Toolbar.MenuButtons);
    }

    [AvaloniaFact]
    public async Task Yaat_OpensItsMenu_HidingTheOtherButtonsInPlace_AndAgainCloses()
    {
        await using Opened scope = await OpenAsync();
        EramMasterToolbar toolbar = scope.Toolbar;
        EramToolbarButton views = toolbar.Find(EramButtonType.ViewsMenu)!;
        Rect viewsBounds = views.Bounds;

        scope.Click(EramButtonType.YaatMenu);

        Assert.Equal(EramButtonType.YaatMenu, toolbar.OpenMenu);
        Assert.Equal(EramColors.BurntCoral, ((ISolidColorBrush)toolbar.Expansion!.BorderBrush!).Color);
        Assert.True(toolbar.Find(EramButtonType.YaatMenu)!.IsOpenMenu);
        Assert.Equal(EramMenus.Yaat.Select(b => b.Type).Order(), toolbar.MenuButtons.Select(b => b.Type).Order());
        Assert.Equal(0, views.Opacity);
        Assert.False(views.IsHitTestVisible);
        Assert.Equal(viewsBounds, views.Bounds);

        scope.Click(EramButtonType.YaatMenu);

        Assert.Null(toolbar.OpenMenu);
        Assert.Null(toolbar.Expansion);
        Assert.Empty(toolbar.MenuButtons);
        Assert.Equal(1, views.Opacity);
    }

    [AvaloniaFact]
    public async Task YaatDb_OpensItsSubmenu_AndAlertConesTogglesWhatTheClassicDbTrayToggles()
    {
        await using Opened scope = await OpenAsync();
        bool before = scope.Scope.DbSettings.AtpaAlertCones;
        scope.Click(EramButtonType.YaatMenu);

        scope.Click(EramButtonType.YaatDbMenu);

        Assert.Equal(EramButtonType.YaatDbMenu, scope.Toolbar.OpenSubmenu);
        Assert.NotNull(scope.Toolbar.SubmenuExpansion);

        scope.Click(EramButtonType.YaatDbAtpaAlertCones);

        Assert.Equal(!before, scope.Scope.DbSettings.AtpaAlertCones);
        Assert.Equal(!before, scope.Scope.IsActive(ScopeToolbarButtons.Find(ScopeButtonId.DbAtpaAlertCones)!));
        Assert.Equal(!before, scope.Toolbar.Find(EramButtonType.YaatDbAtpaAlertCones)!.IsOn);
    }

    [AvaloniaFact]
    public async Task YaatToolsJRing_ArmsTheRingTool_AsTheClassicToolsTrayDoes()
    {
        await using Opened scope = await OpenAsync();
        scope.Click(EramButtonType.YaatMenu);

        scope.Click(EramButtonType.YaatToolsMenu);

        Assert.Equal(ScopeButtonId.Tools, scope.Scope.OpenMenu);
        Assert.Equal(scope.Scope.BandRingTool(), scope.Scope.ArmedRingTool);

        scope.Click(EramButtonType.YaatToolCone);
        Assert.Equal(ScopeRingTool.Cone, scope.Scope.ArmedRingTool);

        scope.Click(EramButtonType.YaatToolJRing);

        Assert.Equal(ScopeRingTool.JRing, scope.Scope.ArmedRingTool);
        Assert.True(scope.Toolbar.Find(EramButtonType.YaatToolJRing)!.IsOn);
        Assert.False(scope.Toolbar.Find(EramButtonType.YaatToolCone)!.IsOn);

        scope.Click(EramButtonType.YaatToolsMenu);

        Assert.Null(scope.Scope.OpenMenu);
        Assert.Equal(ScopeRingTool.None, scope.Scope.ArmedRingTool);
    }

    [AvaloniaFact]
    public async Task YaatPilotLens_OpeningSetsTheScopesOpenMenuToLens_AndClosingClearsIt()
    {
        await using Opened scope = await OpenAsync();
        scope.Click(EramButtonType.YaatMenu);

        scope.Click(EramButtonType.YaatLensMenu);

        Assert.Equal(EramButtonType.YaatLensMenu, scope.Toolbar.OpenSubmenu);
        Assert.Equal(ScopeButtonId.Lens, scope.Scope.OpenMenu);

        scope.Click(EramButtonType.YaatLensMenu);

        Assert.Null(scope.Toolbar.OpenSubmenu);
        Assert.Null(scope.Scope.OpenMenu);
    }

    [AvaloniaFact]
    public async Task YaatOthers_WhileToolsIsOpen_ClosesTools_AndDisarmsTheRingTool()
    {
        await using Opened scope = await OpenAsync();
        scope.Click(EramButtonType.YaatMenu);
        scope.Click(EramButtonType.YaatToolsMenu);
        Assert.Equal(ScopeButtonId.Tools, scope.Scope.OpenMenu);

        scope.Click(EramButtonType.YaatOthers);

        Assert.Null(scope.Toolbar.OpenSubmenu);
        Assert.Null(scope.Scope.OpenMenu);
        Assert.Equal(ScopeRingTool.None, scope.Scope.ArmedRingTool);
    }

    [AvaloniaFact]
    public async Task ClosingYaat_WhileToolsIsOpen_ClosesTools_AndDisarmsTheRingTool()
    {
        await using Opened scope = await OpenAsync();
        scope.Click(EramButtonType.YaatMenu);
        scope.Click(EramButtonType.YaatToolsMenu);
        Assert.Equal(ScopeButtonId.Tools, scope.Scope.OpenMenu);

        scope.Click(EramButtonType.YaatMenu);

        Assert.Null(scope.Toolbar.OpenSubmenu);
        Assert.Null(scope.Scope.OpenMenu);
        Assert.Equal(ScopeRingTool.None, scope.Scope.ArmedRingTool);
    }

    [AvaloniaFact]
    public async Task YaatProfileMixed_SwapsTheBarToTheClassicToolbar()
    {
        await using Opened scope = await OpenAsync();
        scope.Click(EramButtonType.YaatMenu);
        scope.Click(EramButtonType.YaatProfileMenu);

        scope.Click(EramButtonType.YaatProfileMixed);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ScopeProfileKind.Mixed, scope.Scope.ProfileKind);
        Assert.Single(scope.Host.GetVisualDescendants().OfType<ScopeToolbar>());
        Assert.Empty(scope.Host.GetVisualDescendants().OfType<EramBar>());
    }

    [Fact]
    public void EveryButton_HasAnAction_ExactlyWhenItIsBuilt() =>
        Assert.All(Enum.GetValues<EramButtonType>(), type => Assert.Equal(type.IsBuilt(), EramBar.HasAction(type)));

    private static RowDefinition BarRow(ScopeHost host) => host.FindControl<Grid>("HostGrid")!.RowDefinitions[0];

    /// <summary>A Scope with the maps of <paramref name="starsIds"/>, under the ERAM profile, in a host wide enough for BRIGHT's menu.</summary>
    private static async Task<Opened> OpenAsync(params int[] starsIds)
    {
        MainViewModel main = await ScopeTestFixture.MainAsync();
        if (starsIds.Length > 0)
        {
            LoadMaps(main, starsIds);
        }

        return Show(main, prefStore: null);
    }

    /// <summary>A Scope on ZOA's sector 44 (the ERAM profile) saving its pref sets in <paramref name="prefStore"/>.</summary>
    private static async Task<Opened> OpenOnSector44Async(ScopePrefSetStore prefStore)
    {
        MainViewModel main = await ScopeTestFixture.MainAsync();
        main.SetActivePositionFromServer(new PositionDisplayConfigDto([], [], [], "C44", Sector44, 3, NoStarsDisplay: true));
        LoadMaps(main, 2, 10);
        return Show(main, prefStore);
    }

    /// <summary>A Scope on sector 44 with the pref sets A and B saved, B active, and its PREFSET menu open.</summary>
    private async Task<Opened> OpenPrefsetAsync()
    {
        Opened scope = await OpenOnSector44Async(ScopeStoreFixture.Store(_root));
        scope.Main.RunScopeCommand(".scope pref saveas A");
        scope.Main.RunScopeCommand(".scope pref saveas B");
        scope.Click(EramButtonType.PrefsetMenu);
        return scope;
    }

    /// <summary>Loads the Scope's map source with one map per STARS id, in the order given.</summary>
    private static void LoadMaps(MainViewModel main, params int[] starsIds) =>
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

    private static Opened Show(MainViewModel main, ScopePrefSetStore? prefStore)
    {
        var scope = new ScopeViewModel(main, prefStore, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        main.RunScopeCommand(".scope profile eram");
        var host = new ScopeHost { DataContext = scope };
        var window = new Window
        {
            Width = 2400,
            Height = 800,
            Content = host,
        };
        window.ShowAndRunLayout();
        return new Opened(main, scope, window, host);
    }

    /// <summary>An open ERAM Scope in its window, closed and disposed at the end of the test.</summary>
    private sealed class Opened(MainViewModel main, ScopeViewModel scope, Window window, ScopeHost host) : IAsyncDisposable
    {
        public MainViewModel Main { get; } = main;

        public ScopeViewModel Scope { get; } = scope;

        public ScopeHost Host { get; } = host;

        public EramMasterToolbar Toolbar => Assert.Single(Host.GetVisualDescendants().OfType<EramBar>()).Toolbar;

        /// <summary>Presses and releases the centre of the shown button of <paramref name="type"/> with <paramref name="button"/>.</summary>
        public void Click(EramButtonType type, MouseButton button = MouseButton.Left)
        {
            window.UpdateLayout();
            Click(Toolbar.Find(type) ?? throw new InvalidOperationException($"No {type} button is shown"), button);
        }

        /// <summary>
        /// Turns the wheel <paramref name="steps"/> notches up (negative for down) over the shown button of
        /// <paramref name="type"/>.
        /// </summary>
        public void Wheel(EramButtonType type, int steps)
        {
            window.UpdateLayout();
            EramToolbarButton target = Toolbar.Find(type) ?? throw new InvalidOperationException($"No {type} button is shown");
            Point? centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window);
            Assert.NotNull(centre);
            window.MouseWheel(centre.Value, new Vector(0, steps));
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Presses and releases the centre of <paramref name="target"/> with <paramref name="button"/>.</summary>
        public void Click(EramToolbarButton target, MouseButton button = MouseButton.Left)
        {
            window.UpdateLayout();
            Point? centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window);
            Assert.NotNull(centre);
            window.MouseDown(centre.Value, button);
            window.MouseUp(centre.Value, button);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        public ValueTask DisposeAsync()
        {
            window.Close();
            Scope.Dispose();
            Dispatcher.UIThread.RunJobs();
            return ValueTask.CompletedTask;
        }
    }
}
