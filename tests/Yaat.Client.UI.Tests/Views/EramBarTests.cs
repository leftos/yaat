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
using Yaat.Client.Views;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Client.Views.Scope.Toolbar.Dcb;
using Yaat.Client.Views.Scope.Toolbar.Eram;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The ERAM bar under the ERAM Scope profile: CRC's Master Toolbar in a bar row of its own height, its menus expanding as
/// CRC's do, and each live button acting through the classic bar's own path.
/// </summary>
public class EramBarTests : IDisposable
{
    /// <summary>A host wide enough for every menu to open right of its button.</summary>
    private const double WideWidth = 2400;

    /// <summary>A host narrower than the Master Toolbar, which then scrolls sideways.</summary>
    private const double NarrowBarWidth = 400;

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
    public async Task Hiding_WithAMenuAndItsSubmenuOpen_ClosesThem()
    {
        await using Opened scope = await OpenAsync();
        scope.Click(EramButtonType.BrightnessMenu);
        scope.Click(EramButtonType.MapBrightnessMenu);
        Assert.Equal(EramButtonType.BrightnessMenu, scope.Toolbar.OpenMenu);
        Assert.Equal(EramButtonType.MapBrightnessMenu, scope.Toolbar.OpenSubmenu);

        scope.Main.RunScopeCommand(".scope bar hide");
        Dispatcher.UIThread.RunJobs();

        Assert.Null(scope.Toolbar.OpenMenu);
        Assert.Null(scope.Toolbar.OpenSubmenu);
        Assert.Null(scope.Toolbar.Expansion);
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
        scope.Scope.RunCommand(".scope db 4thline off");
        scope.Click(EramButtonType.DbFieldsMenu);
        EramBar old = Assert.Single(scope.Host.GetVisualDescendants().OfType<EramBar>());
        EramToolbarButton destination = old.Toolbar.Find(EramButtonType.DestinationDisplay)!;
        Assert.False(destination.IsOn);

        scope.Main.RunScopeCommand(".scope profile mixed");
        Dispatcher.UIThread.RunJobs();
        scope.Scope.RunCommand(".scope db 4thline on");

        Assert.True(scope.Scope.DbSettings.Line4);
        Assert.Null(old.DataContext);
        Assert.False(destination.IsOn);
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
    public async Task Yaat_OpenMenu_HidesTheTenthColumnsHideToolbarInPlace()
    {
        await using Opened scope = await OpenAsync();
        EramToolbarButton hide = scope.Toolbar.Find(EramButtonType.HideToolbar)!;
        Rect hideBounds = hide.Bounds;

        scope.Click(EramButtonType.YaatMenu);

        Assert.Equal(0, hide.Opacity);
        Assert.False(hide.IsHitTestVisible);
        Assert.Equal(hideBounds, hide.Bounds);
    }

    [AvaloniaFact]
    public async Task HideToolbar_HidesTheBar_ToTheShowToolbarTile()
    {
        await using Opened scope = await OpenAsync();
        EramToolbarButton hide = scope.Toolbar.Find(EramButtonType.HideToolbar)!;
        Assert.Equal(("HIDE", "TOOLBAR"), (hide.Line1, hide.Line2));

        scope.Click(hide);

        Assert.True(scope.Scope.IsBarHidden);
        EramToolbarButton tile = Assert.IsType<EramToolbarButton>(scope.Host.FindControl<ContentControl>("ShowSlot")!.Content);
        Assert.Equal(("SHOW", "TOOLBAR"), (tile.Line1, tile.Line2));
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
    public async Task YaatTools_ThenTheBarsDataContextCleared_ClosesTools_AndDisarmsTheRingTool()
    {
        await using Opened scope = await OpenAsync();
        scope.Click(EramButtonType.YaatMenu);
        scope.Click(EramButtonType.YaatToolsMenu);
        Assert.Equal(ScopeButtonId.Tools, scope.Scope.OpenMenu);
        EramBar bar = Assert.Single(scope.Host.GetVisualDescendants().OfType<EramBar>());

        bar.DataContext = null;

        Assert.Null(scope.Scope.OpenMenu);
        Assert.Equal(ScopeRingTool.None, scope.Scope.ArmedRingTool);
    }

    [AvaloniaFact]
    public async Task YaatTools_ThenTheBarLeavesTheWindow_ClosesTools_AndDisarmsTheRingTool()
    {
        await using Opened scope = await OpenAsync();
        scope.Click(EramButtonType.YaatMenu);
        scope.Click(EramButtonType.YaatToolsMenu);
        Assert.Equal(ScopeButtonId.Tools, scope.Scope.OpenMenu);
        EramBar bar = Assert.Single(scope.Host.GetVisualDescendants().OfType<EramBar>());
        Window window = Assert.IsType<Window>(TopLevel.GetTopLevel(scope.Host));

        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        Assert.Same(scope.Scope, bar.DataContext);
        Assert.Null(scope.Scope.OpenMenu);
        Assert.Equal(ScopeRingTool.None, scope.Scope.ArmedRingTool);
    }

    [AvaloniaFact]
    public async Task YaatTools_ThenProfileStars_ClosesTools_AndDisarmsTheRingTool()
    {
        await using Opened scope = await OpenAsync();
        scope.Click(EramButtonType.YaatMenu);
        scope.Click(EramButtonType.YaatToolsMenu);
        Assert.Equal(ScopeButtonId.Tools, scope.Scope.OpenMenu);

        scope.Main.RunScopeCommand(".scope profile stars");
        Dispatcher.UIThread.RunJobs();

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

    [AvaloniaFact]
    public async Task YaatProfileEuroScope_ShowsTheEuroScopeLookOnTheClassicToolbar()
    {
        await using Opened scope = await OpenAsync();
        scope.Click(EramButtonType.YaatMenu);
        scope.Click(EramButtonType.YaatProfileMenu);

        scope.Click(EramButtonType.YaatProfileEuroScope);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ScopeProfileKind.EuroScope, scope.Scope.ProfileKind);
        Assert.Equal(ScopeTemplates.EuroScopeId, scope.Scope.TemplateId);
        Assert.Single(scope.Host.GetVisualDescendants().OfType<ScopeToolbar>());
        Assert.Empty(scope.Host.GetVisualDescendants().OfType<EramBar>());
    }

    [AvaloniaFact]
    public async Task YaatBriteRings_StepsTheValueTheClassicRingsSpinnerSteps()
    {
        await using Opened scope = await OpenAsync();
        ScopeBrightnessGroup rings = ScopeBrightness.FindGroup("rings")!;
        int start = rings.Min + rings.Step;
        scope.Scope.RunCommand(ScopeToolbarButtons.BriteCommand("rings", start));
        scope.Click(EramButtonType.YaatMenu);
        scope.Click(EramButtonType.YaatBriteMenu);

        scope.Click(EramButtonType.YaatBriteRings, MouseButton.Middle);

        Assert.Equal(start + rings.Step, rings.Get(scope.Scope.Brightness));
        ScopeButton classic = ScopeToolbarButtons.Find(ScopeButtonId.BriteRings)!;
        Assert.Equal(scope.Scope.SpinnerValue(classic), scope.Toolbar.Find(EramButtonType.YaatBriteRings)!.Line2);

        scope.Scope.StepSpinner(ScopeButtonId.BriteRings, -1);

        Assert.Equal(start, rings.Get(scope.Scope.Brightness));
        Assert.Equal(scope.Scope.SpinnerValue(classic), scope.Toolbar.Find(EramButtonType.YaatBriteRings)!.Line2);
    }

    [AvaloniaFact]
    public async Task YaatDisplayRrShow_TogglesTheRangeRings_AsTheClassicRrShowDoes()
    {
        await using Opened scope = await OpenAsync();
        ScopeButton classic = ScopeToolbarButtons.Find(ScopeButtonId.RrShow)!;
        bool before = scope.Scope.ShowRangeRing;
        scope.Click(EramButtonType.YaatMenu);
        scope.Click(EramButtonType.YaatDisplayMenu);

        scope.Click(EramButtonType.YaatDisplayRrShow);

        Assert.Equal(!before, scope.Scope.ShowRangeRing);
        Assert.Equal(!before, scope.Scope.IsActive(classic));
        Assert.Equal(!before, scope.Toolbar.Find(EramButtonType.YaatDisplayRrShow)!.IsOn);

        scope.Scope.Press(classic);

        Assert.Equal(before, scope.Scope.ShowRangeRing);
        Assert.Equal(before, scope.Toolbar.Find(EramButtonType.YaatDisplayRrShow)!.IsOn);
    }

    [AvaloniaTheory]
    [InlineData(1460)]
    [InlineData(1280)]
    public async Task Bright_AtTheRadarsUsualWidths_EscClosesIt(double width)
    {
        await using Opened scope = await OpenAtAsync(width);
        scope.FocusCanvas();
        scope.Click(EramButtonType.BrightnessMenu);
        Border expansion = scope.Toolbar.Expansion!;
        EramToolbarButton bright = scope.Toolbar.Find(EramButtonType.BrightnessMenu)!;
        Visual root = TopLevel.GetTopLevel(scope.Host)!;
        double left = expansion.TranslatePoint(default, root)!.Value.X;
        double brightLeft = bright.TranslatePoint(default, root)!.Value.X;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"width {width}: BRIGHT expansion {left}..{left + expansion.Bounds.Width}, BRIGHT button {brightLeft}..{brightLeft + bright.Bounds.Width}"
        );

        scope.Press(Key.Escape);

        AssertAllMenusClosed(scope.Toolbar);
    }

    [AvaloniaFact]
    public async Task AMenuThatCoversItsOpener_EscClosesIt_AndShowsTheOtherButtonsAgain()
    {
        await using Opened scope = await OpenAtAsync(1280);
        EramMenuButton[] wide = [.. Enumerable.Range(0, 20).Select(column => new EramMenuButton(0, column, EramButtonType.YaatOthers))];
        scope.Toolbar.MenuSource = menu => (menu == EramButtonType.BrightnessMenu) ? wide : EramMenus.Fixed(menu);
        scope.FocusCanvas();
        scope.Click(EramButtonType.BrightnessMenu);
        Border expansion = scope.Toolbar.Expansion!;
        EramToolbarButton bright = scope.Toolbar.Find(EramButtonType.BrightnessMenu)!;
        double left = expansion.TranslatePoint(default, bright)!.Value.X;
        Assert.True((left < bright.Bounds.Width) && (left + expansion.Bounds.Width > 0));

        scope.Press(Key.Escape);

        AssertAllMenusClosed(scope.Toolbar);
    }

    [AvaloniaFact]
    public async Task YaatDbSubmenu_TheFirstEscClosesTheSubmenu_TheSecondTheMenu()
    {
        await using Opened scope = await OpenAsync();
        scope.FocusCanvas();
        scope.Click(EramButtonType.YaatMenu);
        scope.Click(EramButtonType.YaatDbMenu);

        scope.Press(Key.Escape);

        Assert.Null(scope.Toolbar.OpenSubmenu);
        Assert.Null(scope.Toolbar.SubmenuExpansion);
        Assert.Equal(EramButtonType.YaatMenu, scope.Toolbar.OpenMenu);

        scope.Press(Key.Escape);

        AssertAllMenusClosed(scope.Toolbar);
    }

    [AvaloniaFact]
    public async Task YaatTools_EscClosesIt_AndDisarmsTheRingTool()
    {
        await using Opened scope = await OpenAsync();
        scope.FocusCanvas();
        scope.Click(EramButtonType.YaatMenu);
        scope.Click(EramButtonType.YaatToolsMenu);
        Assert.NotEqual(ScopeRingTool.None, scope.Scope.ArmedRingTool);

        scope.Press(Key.Escape);

        Assert.Null(scope.Toolbar.OpenSubmenu);
        Assert.Null(scope.Scope.OpenMenu);
        Assert.Equal(ScopeRingTool.None, scope.Scope.ArmedRingTool);
    }

    [AvaloniaFact]
    public async Task Docked_EscInTheCommandBox_LeavesTheMenuOpen()
    {
        (MainWindow main, MainViewModel vm) = await MainWindowHost.BootDockedScopeAsync();
        try
        {
            vm.RunScopeCommand(".scope profile eram");
            MainWindowHost.Pump();
            main.UpdateLayout();
            MainWindowHost.Pump();
            EramMasterToolbar toolbar = Assert.Single(main.GetVisualDescendants().OfType<EramBar>()).Toolbar;
            EramToolbarButton yaat = toolbar.Find(EramButtonType.YaatMenu)!;
            Point centre = yaat.TranslatePoint(new Point(yaat.Bounds.Width / 2, yaat.Bounds.Height / 2), main)!.Value;
            main.MouseDown(centre, MouseButton.Left);
            main.MouseUp(centre, MouseButton.Left);
            MainWindowHost.Pump();
            Assert.Equal(EramButtonType.YaatMenu, toolbar.OpenMenu);
            TextBox box = main.GetVisualDescendants().OfType<TextBox>().First(b => b.Name == "CommandInput");
            Assert.True(box.Focus());
            Dispatcher.UIThread.RunJobs();

            main.DispatchKey(Key.Escape);
            MainWindowHost.Pump();

            Assert.Equal(EramButtonType.YaatMenu, toolbar.OpenMenu);
        }
        finally
        {
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public async Task NoMenuOpen_EscReachesTheScope()
    {
        await using Opened scope = await OpenAsync();
        scope.FocusCanvas();
        scope.Click(EramButtonType.YaatMenu);
        scope.Click(EramButtonType.YaatMenu);
        scope.Scope.ArmMeasure();
        Assert.True(scope.Main.Measure.IsMeasuring);

        scope.Press(Key.Escape);

        Assert.False(scope.Main.Measure.IsMeasuring);
    }

    [AvaloniaFact]
    public async Task NarrowBar_TheYaatColumnIsReachable_AfterScrolling()
    {
        await using Opened scope = await OpenAtAsync(NarrowBarWidth);
        EramBar bar = scope.Bar;
        EramMasterToolbar toolbar = scope.Toolbar;
        EramToolbarButton first = toolbar.Buttons.MinBy(button => LeftIn(bar, button))!;
        EramToolbarButton yaat = toolbar.Find(EramButtonType.YaatMenu)!;
        EramToolbarButton hide = toolbar.Find(EramButtonType.HideToolbar)!;
        double firstLeft = LeftIn(bar, first);
        Assert.True(LeftIn(bar, hide) + hide.Bounds.Width > bar.Bounds.Width);

        // The wheel over RANGE steps the range and leaves the bar where it is.
        double range = scope.Scope.RangeNm;
        scope.Wheel(EramButtonType.RangeView, 1);
        Assert.Equal(range - 1, scope.Scope.RangeNm);
        Assert.Equal(firstLeft, LeftIn(bar, first));

        // Anywhere else the wheel scrolls the bar a 25 DIP step a notch, and a sideways swipe does too.
        scope.WheelOverBar(new Vector(0, -1));
        Assert.Equal(firstLeft - DcbBar.WheelStep, LeftIn(bar, first));
        scope.WheelOverBar(new Vector(1, 0));
        Assert.Equal(firstLeft, LeftIn(bar, first));
        scope.WheelOverBar(new Vector(0, -1000));

        Assert.InRange(LeftIn(bar, yaat), 0, bar.Bounds.Width - yaat.Bounds.Width);
        Assert.InRange(LeftIn(bar, hide), 0, bar.Bounds.Width - hide.Bounds.Width);
        Assert.Equal(range - 1, scope.Scope.RangeNm);
        scope.Click(EramButtonType.YaatMenu);
        Assert.Equal(EramButtonType.YaatMenu, toolbar.OpenMenu);
    }

    [AvaloniaFact]
    public async Task NarrowBar_SidewaysSwipeOverRange_ScrollsTheBarByOneStep()
    {
        await using Opened scope = await OpenAtAsync(NarrowBarWidth);
        EramBar bar = scope.Bar;
        EramToolbarButton first = scope.Toolbar.Buttons.MinBy(button => LeftIn(bar, button))!;
        double firstLeft = LeftIn(bar, first);
        double range = scope.Scope.RangeNm;

        scope.WheelOver(EramButtonType.RangeView, new Vector(-1, 0));

        Assert.Equal(firstLeft - DcbBar.WheelStep, LeftIn(bar, first));
        Assert.Equal(range, scope.Scope.RangeNm);
    }

    [AvaloniaFact]
    public async Task NarrowBar_OpenMenu_ScrollsWithItsButton()
    {
        await using Opened scope = await OpenAtAsync(NarrowBarWidth);
        EramBar bar = scope.Bar;
        scope.WheelOverBar(new Vector(0, -1000));
        scope.Click(EramButtonType.YaatMenu);
        Border menu = scope.Toolbar.Expansion!;
        EramToolbarButton yaat = scope.Toolbar.Find(EramButtonType.YaatMenu)!;
        double menuLeft = LeftIn(bar, menu);
        double yaatLeft = LeftIn(bar, yaat);

        scope.WheelOverBar(new Vector(0, 1));

        Assert.Equal(yaatLeft + DcbBar.WheelStep, LeftIn(bar, yaat));
        Assert.Equal(menuLeft + DcbBar.WheelStep, LeftIn(bar, menu));
        Assert.Equal(EramButtonType.YaatMenu, scope.Toolbar.OpenMenu);

        // The scrolled menu still takes the pointer: its lowest button in view is the one under it.
        EramToolbarButton lowest = scope.Toolbar.MenuButtons.Where(button => InView(bar, button)).MaxBy(button => TopIn(bar, button))!;
        Point centre = lowest.TranslatePoint(new Point(lowest.Bounds.Width / 2, lowest.Bounds.Height / 2), scope.Window)!.Value;
        Assert.True((scope.Window.InputHitTest(centre) is Visual hit) && ((hit == lowest) || lowest.IsVisualAncestorOf(hit)));
    }

    private static double LeftIn(EramBar bar, Control control) => control.TranslatePoint(default, bar)!.Value.X;

    private static double TopIn(EramBar bar, Control control) => control.TranslatePoint(default, bar)!.Value.Y;

    private static bool InView(EramBar bar, Control control) =>
        (LeftIn(bar, control) >= 0) && ((LeftIn(bar, control) + control.Bounds.Width) <= bar.Bounds.Width);

    [Fact]
    public void EveryButton_HasAnAction_ExactlyWhenItIsBuilt() =>
        Assert.All(Enum.GetValues<EramButtonType>(), type => Assert.Equal(type.IsBuilt(), EramBar.HasAction(type)));

    /// <summary>Asserts that no menu or submenu is open and no Master Toolbar button is hidden in place.</summary>
    private static void AssertAllMenusClosed(EramMasterToolbar toolbar)
    {
        Assert.Null(toolbar.OpenMenu);
        Assert.Null(toolbar.Expansion);
        Assert.All(toolbar.Buttons, button => Assert.False(button.IsSuppressed));
    }

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

    /// <summary>A Scope under the ERAM profile in a host <paramref name="width"/> wide.</summary>
    private static async Task<Opened> OpenAtAsync(double width)
    {
        MainViewModel main = await ScopeTestFixture.MainAsync();
        return Show(main, prefStore: null, width);
    }

    private static Opened Show(MainViewModel main, ScopePrefSetStore? prefStore) => Show(main, prefStore, WideWidth);

    private static Opened Show(MainViewModel main, ScopePrefSetStore? prefStore, double width)
    {
        var scope = new ScopeViewModel(main, prefStore, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
        main.RunScopeCommand(".scope profile eram");
        var host = new ScopeHost { DataContext = scope };
        var window = new Window
        {
            Width = width,
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

        public Window Window => window;

        public EramBar Bar => Assert.Single(Host.GetVisualDescendants().OfType<EramBar>());

        public EramMasterToolbar Toolbar => Bar.Toolbar;

        /// <summary>Turns the wheel by <paramref name="delta"/> over the bar's bottom row near its left end, away from RANGE.</summary>
        public void WheelOverBar(Vector delta)
        {
            window.UpdateLayout();
            Point? at = Bar.TranslatePoint(new Point(40, Bar.Bounds.Height * 3 / 4), window);
            Assert.NotNull(at);
            window.MouseWheel(at.Value, delta);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        /// <summary>Presses an empty corner of the Scope's canvas, which takes the keyboard focus and hits no track.</summary>
        public void FocusCanvas()
        {
            Point? corner = Host.ScopeView.Canvas.TranslatePoint(new Point(5, Host.ScopeView.Canvas.Bounds.Height - 5), window);
            Assert.NotNull(corner);
            window.MouseDown(corner.Value, MouseButton.Left);
            window.MouseUp(corner.Value, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        /// <summary>Presses and releases <paramref name="key"/> on the window, as the keyboard does.</summary>
        public void Press(Key key)
        {
            window.DispatchKey(key);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

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
        public void Wheel(EramButtonType type, int steps) => WheelOver(type, new Vector(0, steps));

        /// <summary>Turns the wheel by <paramref name="delta"/> over the shown button of <paramref name="type"/>.</summary>
        public void WheelOver(EramButtonType type, Vector delta)
        {
            window.UpdateLayout();
            EramToolbarButton target = Toolbar.Find(type) ?? throw new InvalidOperationException($"No {type} button is shown");
            Point? centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window);
            Assert.NotNull(centre);
            window.MouseWheel(centre.Value, delta);
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
