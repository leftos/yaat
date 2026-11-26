using Xunit;
using Yaat.Client.Views.Scope.Toolbar.Eram;

namespace Yaat.Client.Tests.Views.Scope.Toolbar;

/// <summary>
/// The ERAM Master Toolbar's menus as tables: each fixed menu's buttons where CRC's menu file adds them, GeoMap's filters
/// and PREFSET's items laid out from what the Scope holds, each submenu button's lines and kind, and the buttons YAAT drives.
/// </summary>
public class EramMenusTests
{
    private static readonly (EramButtonType Type, int Row, int Column)[] RadarFilter =
    [
        (EramButtonType.AllLdbsFilter, 0, 0),
        (EramButtonType.SelectBeaconTargetFilter, 1, 0),
        (EramButtonType.PairedLdbsFilter, 0, 1),
        (EramButtonType.PermanentEchoFilter, 1, 1),
        (EramButtonType.UnpairedLdbsFilter, 0, 2),
        (EramButtonType.EcmStrobeLinesFilter, 1, 2),
        (EramButtonType.PrimaryTargetFilter, 0, 3),
        (EramButtonType.HistoryFilter, 1, 3),
        (EramButtonType.NonModeCFilter, 0, 4),
    ];

    private static readonly (EramButtonType Type, int Row, int Column)[] AtcTools =
    [
        (EramButtonType.CrrFix, 0, 0),
        (EramButtonType.SpeedAdvisory, 1, 1),
        (EramButtonType.WeatherMenu, 0, 2),
    ];

    private static readonly (EramButtonType Type, int Row, int Column)[] Brightness =
    [
        (EramButtonType.MapBrightnessMenu, 0, 0),
        (EramButtonType.BacklightBrightness, 1, 0),
        (EramButtonType.CpdlcBrightnessMenu, 0, 1),
        (EramButtonType.ButtonBrightness, 1, 1),
        (EramButtonType.BackgroundBrightness, 0, 2),
        (EramButtonType.BorderBrightness, 1, 2),
        (EramButtonType.CursorBrightness, 0, 3),
        (EramButtonType.ToolbarBrightness, 1, 3),
        (EramButtonType.TextBrightness, 0, 4),
        (EramButtonType.ToolbarBorderBrightness, 1, 4),
        (EramButtonType.PairedTargetBrightness, 0, 5),
        (EramButtonType.SdBorderBrightness, 1, 5),
        (EramButtonType.UnpairedTargetBrightness, 0, 6),
        (EramButtonType.FdbBrightness, 1, 6),
        (EramButtonType.PairedTargetHistoryBrightness, 0, 7),
        (EramButtonType.FdbPortalBrightness, 1, 7),
        (EramButtonType.UnpairedTargetHistoryBrightness, 0, 8),
        (EramButtonType.SatCommBrightness, 1, 8),
        (EramButtonType.LdbBrightness, 0, 9),
        (EramButtonType.FdbOnFrequencyBrightness, 1, 9),
        (EramButtonType.SelectLdbBrightness, 0, 10),
        (EramButtonType.FdbLine4Brightness, 1, 10),
        (EramButtonType.WeatherBrightness, 0, 11),
        (EramButtonType.DwellBrightness, 1, 11),
        (EramButtonType.NexradBrightness, 0, 12),
        (EramButtonType.FdbPortalFenceBrightness, 1, 12),
        (EramButtonType.FelIndicatorBrightness, 1, 13),
        (EramButtonType.OutageBrightness, 1, 14),
    ];

    private static readonly (EramButtonType Type, int Row, int Column)[] FontSize =
    [
        (EramButtonType.Fdb4thLineFontSize, 0, 0),
        (EramButtonType.RdbFontSize, 1, 0),
        (EramButtonType.FdbFontSize, 0, 1),
        (EramButtonType.LdbFontSize, 1, 1),
        (EramButtonType.FdbPortalFontSize, 0, 2),
        (EramButtonType.OutageFontSize, 1, 2),
        (EramButtonType.ToolbarFontSize, 0, 3),
    ];

    private static readonly (EramButtonType Type, int Row, int Column)[] DbFields =
    [
        (EramButtonType.NonRvsmIndicatorDisplay, 0, 0),
        (EramButtonType.NonAdsbIndicatorDisplay, 1, 0),
        (EramButtonType.VriOverride, 0, 1),
        (EramButtonType.NonAdsbBrightness, 1, 1),
        (EramButtonType.BeaconCodeDisplayOverride, 0, 2),
        (EramButtonType.SatCommDisplay, 1, 2),
        (EramButtonType.SpeedDisplayOverride, 0, 3),
        (EramButtonType.TfmRerouteIndicatorDisplay, 1, 3),
        (EramButtonType.DestinationDisplay, 0, 4),
        (EramButtonType.CrrRdbDisplay, 1, 4),
        (EramButtonType.AircraftDataDisplay, 0, 5),
        (EramButtonType.StaRdbDisplay, 1, 5),
        (EramButtonType.FdbLeaderLength, 0, 6),
        (EramButtonType.DelayRdbDisplay, 1, 6),
        (EramButtonType.BcastFlidDisplay, 0, 7),
        (EramButtonType.DelayFormatOverride, 1, 7),
        (EramButtonType.FdbPortalFenceDisplay, 0, 8),
    ];

    private static readonly (EramButtonType Type, int Row, int Column)[] Views =
    [
        (EramButtonType.AsView, 0, 0),
        (EramButtonType.DepartureView, 1, 0),
        (EramButtonType.AhiView, 0, 1),
        (EramButtonType.FlightEventView, 1, 1),
        (EramButtonType.CfrView, 0, 2),
        (EramButtonType.GroupSupView, 1, 2),
        (EramButtonType.CodeView, 0, 3),
        (EramButtonType.HoldViewView, 1, 3),
        (EramButtonType.CaView, 0, 4),
        (EramButtonType.InboundView, 1, 4),
        (EramButtonType.SavedAdvisoriesView, 0, 5),
        (EramButtonType.MrpView, 1, 5),
        (EramButtonType.MessageHistoryView, 0, 6),
        (EramButtonType.SaaFilterView, 1, 6),
        (EramButtonType.MessageOutView, 0, 7),
        (EramButtonType.DcrdView, 1, 7),
        (EramButtonType.TocSettingsView, 0, 8),
        (EramButtonType.WxView, 1, 8),
        (EramButtonType.CrrView, 0, 9),
    ];

    private static readonly EramButtonType[] Live =
    [
        EramButtonType.AtcToolsMenu,
        EramButtonType.RangeView,
        EramButtonType.BrightnessMenu,
        EramButtonType.FontSizeMenu,
        EramButtonType.DbFieldsMenu,
        EramButtonType.VectorLength,
        EramButtonType.ViewsMenu,
        EramButtonType.GeomapMenu,
        EramButtonType.RadarFilterMenu,
        EramButtonType.PrefsetMenu,
        EramButtonType.DeleteTearoff,
        EramButtonType.CrrView,
        EramButtonType.CaView,
        EramButtonType.WeatherMenu,
        EramButtonType.PairedTargetBrightness,
        EramButtonType.UnpairedTargetBrightness,
        EramButtonType.PairedTargetHistoryBrightness,
        EramButtonType.UnpairedTargetHistoryBrightness,
        EramButtonType.FdbBrightness,
        EramButtonType.FdbLine4Brightness,
        EramButtonType.DwellBrightness,
        EramButtonType.MapBrightnessMenu,
        EramButtonType.MapBcg1,
        EramButtonType.MapBcg2,
        EramButtonType.FdbFontSize,
        EramButtonType.DestinationDisplay,
        EramButtonType.AircraftDataDisplay,
        EramButtonType.HistoryFilter,
        .. Enumerable.Range(0, EramMenus.MaxGeomapFilters).Select(i => EramButtonType.Filter1 + i),
        EramButtonType.PrefsetSet,
        EramButtonType.PrefsetSave,
        EramButtonType.PrefsetSaveAs,
        EramButtonType.PrefsetRename,
        EramButtonType.PrefsetDelete,
    ];

    [Fact]
    public void RadarFilter_IsCrcsTable() => Assert.Equal(RadarFilter, Cells(EramMenus.RadarFilter));

    [Fact]
    public void AtcTools_IsCrcsTable() => Assert.Equal(AtcTools, Cells(EramMenus.AtcTools));

    [Fact]
    public void Brightness_IsCrcsTable() => Assert.Equal(Brightness, Cells(EramMenus.Brightness));

    [Fact]
    public void FontSize_IsCrcsTable() => Assert.Equal(FontSize, Cells(EramMenus.FontSize));

    [Fact]
    public void DbFields_IsCrcsTable() => Assert.Equal(DbFields, Cells(EramMenus.DbFields));

    [Fact]
    public void Views_IsCrcsTable() => Assert.Equal(Views, Cells(EramMenus.Views));

    [Fact]
    public void MapBrightness_HoldsMapAAndMapB_WhereCrcsFirstTwoBcgsSit()
    {
        Assert.Equal([(EramButtonType.MapBcg1, 0, 0), (EramButtonType.MapBcg2, 0, 1)], Cells(EramMenus.MapBrightness));
        Assert.Equal([("MAP", "A"), ("MAP", "B")], EramMenus.MapBrightness.Select(b => b.Lines));
    }

    [Theory]
    [InlineData(EramButtonType.AtcToolsMenu)]
    [InlineData(EramButtonType.BrightnessMenu)]
    [InlineData(EramButtonType.MapBrightnessMenu)]
    [InlineData(EramButtonType.FontSizeMenu)]
    [InlineData(EramButtonType.DbFieldsMenu)]
    [InlineData(EramButtonType.ViewsMenu)]
    [InlineData(EramButtonType.RadarFilterMenu)]
    public void Fixed_NamesEachFixedMenu(EramButtonType menu) => Assert.NotNull(EramMenus.Fixed(menu));

    [Theory]
    [InlineData(EramButtonType.GeomapMenu)]
    [InlineData(EramButtonType.PrefsetMenu)]
    [InlineData(EramButtonType.CursorMenu)]
    [InlineData(EramButtonType.CpdlcBrightnessMenu)]
    [InlineData(EramButtonType.RangeView)]
    public void Fixed_IsNull_ForAMenuWithNoFixedTable(EramButtonType menu) => Assert.Null(EramMenus.Fixed(menu));

    [Fact]
    public void Geomap_LaysItsFiltersOutAsCrcsGeomapMenu()
    {
        (int, string)[] maps = [.. Enumerable.Range(1, 25).Select(i => (i, $"M{i}"))];

        IReadOnlyList<EramMenuButton> menu = EramMenus.Geomap(maps);

        Assert.Equal(25, menu.Count);
        Assert.Equal((EramButtonType.Filter1, 0, 0), Cell(menu[0]));
        Assert.Equal((EramButtonType.Filter10, 0, 9), Cell(menu[9]));
        Assert.Equal((EramButtonType.Filter11, 1, 0), Cell(menu[10]));
        Assert.Equal((EramButtonType.Filter20, 1, 9), Cell(menu[19]));
        Assert.Equal((EramButtonType.Filter21, 0, 10), Cell(menu[20]));
        Assert.Equal(("1", "M1"), menu[0].Lines);
        Assert.Equal("1", menu[0].Key);
    }

    [Fact]
    public void Geomap_StopsAtCrcsFortyFilters()
    {
        (int, string)[] maps = [.. Enumerable.Range(1, 45).Select(i => (i, ""))];

        IReadOnlyList<EramMenuButton> menu = EramMenus.Geomap(maps);

        Assert.Equal(EramMenus.MaxGeomapFilters, menu.Count);
        Assert.Equal((EramButtonType.Filter40, 1, 19), Cell(menu[^1]));
    }

    [Fact]
    public void Prefset_ListsTheSetsThenSaveSaveAsRenameAndDelete_TwoToAColumn()
    {
        IReadOnlyList<EramMenuButton> menu = EramMenus.Prefset(["DAY", "NIGHT SHIFT B"]);

        Assert.Equal(
            [
                (EramButtonType.PrefsetSet, 0, 0),
                (EramButtonType.PrefsetSet, 1, 0),
                (EramButtonType.PrefsetSave, 0, 1),
                (EramButtonType.PrefsetSaveAs, 1, 1),
                (EramButtonType.PrefsetRename, 0, 2),
                (EramButtonType.PrefsetDelete, 1, 2),
            ],
            Cells(menu)
        );
        Assert.Equal(("DAY", ""), menu[0].Lines);
        Assert.Equal(("NIGHT", "SHIFT B"), menu[1].Lines);
        Assert.Equal(["DAY", "NIGHT SHIFT B"], menu.Take(2).Select(b => b.Key));
    }

    [Theory]
    [InlineData(EramButtonType.PairedTargetHistoryBrightness, "PR HST", "")]
    [InlineData(EramButtonType.MapBrightnessMenu, "MAP", "BRIGHT")]
    [InlineData(EramButtonType.WeatherMenu, "WX", "")]
    [InlineData(EramButtonType.CaView, "CONFLCT", "ALERT")]
    [InlineData(EramButtonType.HistoryFilter, "HISTORY", "")]
    [InlineData(EramButtonType.AircraftDataDisplay, "TYPE", "")]
    [InlineData(EramButtonType.FdbFontSize, "FDB", "")]
    [InlineData(EramButtonType.PrefsetSaveAs, "SAVE", "AS")]
    public void SubmenuButtons_HaveCrcsTwoLines(EramButtonType type, string line1, string line2) => Assert.Equal((line1, line2), type.Lines());

    [Fact]
    public void Lines_CoversEveryButton() => Assert.All(Enum.GetValues<EramButtonType>(), type => type.Lines());

    [Theory]
    [InlineData(EramButtonType.MapBrightnessMenu, EramButtonKind.Menu)]
    [InlineData(EramButtonType.CpdlcBrightnessMenu, EramButtonKind.Menu)]
    [InlineData(EramButtonType.WeatherMenu, EramButtonKind.Toggle)]
    [InlineData(EramButtonType.FdbBrightness, EramButtonKind.IncDec)]
    [InlineData(EramButtonType.DwellBrightness, EramButtonKind.IncDec)]
    [InlineData(EramButtonType.MapBcg1, EramButtonKind.IncDec)]
    [InlineData(EramButtonType.NonAdsbBrightness, EramButtonKind.IncDec)]
    [InlineData(EramButtonType.FdbFontSize, EramButtonKind.IncDec)]
    [InlineData(EramButtonType.HistoryFilter, EramButtonKind.IncDec)]
    [InlineData(EramButtonType.FdbLeaderLength, EramButtonKind.IncDec)]
    [InlineData(EramButtonType.AllLdbsFilter, EramButtonKind.Toggle)]
    [InlineData(EramButtonType.DestinationDisplay, EramButtonKind.Toggle)]
    [InlineData(EramButtonType.CrrView, EramButtonKind.Toggle)]
    [InlineData(EramButtonType.Filter1, EramButtonKind.Toggle)]
    [InlineData(EramButtonType.VriOverride, EramButtonKind.Command)]
    [InlineData(EramButtonType.PrefsetSet, EramButtonKind.Command)]
    [InlineData(EramButtonType.PrefsetDelete, EramButtonKind.Command)]
    public void SubmenuButtons_HaveCrcsKind(EramButtonType type, EramButtonKind kind) => Assert.Equal(kind, type.Kind());

    [Fact]
    public void IsBuilt_IsTrueExactlyForTheLiveButtons()
    {
        EramButtonType[] built = [.. Enum.GetValues<EramButtonType>().Where(type => type.IsBuilt()).OrderBy(type => type)];

        Assert.Equal([.. Live.OrderBy(type => type)], built);
    }

    private static (EramButtonType Type, int Row, int Column) Cell(EramMenuButton button) => (button.Type, button.Row, button.Column);

    private static (EramButtonType Type, int Row, int Column)[] Cells(IEnumerable<EramMenuButton> menu) => [.. menu.Select(Cell)];
}
