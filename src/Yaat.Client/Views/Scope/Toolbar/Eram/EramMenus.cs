using System.Globalization;

namespace Yaat.Client.Views.Scope.Toolbar.Eram;

/// <summary>One button of an ERAM toolbar menu: where it sits in the menu's expansion, its type, its label and its key.</summary>
/// <param name="Row">The 0-based row: 0 is the top row.</param>
/// <param name="Column">The 0-based column, left to right.</param>
/// <param name="Type">The button at that cell.</param>
public sealed record EramMenuButton(int Row, int Column, EramButtonType Type)
{
    /// <summary>The button's two text lines: the type's own (<see cref="EramButtonTypeExtensions.Lines"/>) unless the menu labels it.</summary>
    public (string Line1, string Line2) Lines { get; init; } = Type.Lines();

    /// <summary>What tells apart buttons of one type: a GeoMap filter's STARS id, a pref set's name; null for any other.</summary>
    public string? Key { get; init; }
}

/// <summary>
/// The ERAM Master Toolbar's menus as tables, each button where CRC's menu file adds it (<c>AddButton(type, row, column)</c>
/// in CRC's <c>Toolbars.Menus</c>), GeoMap's filters laid out as CRC's <c>GeomapMenu</c> lays them out, and PREFSET's items,
/// which CRC does not simulate.
/// </summary>
public static class EramMenus
{
    /// <summary>The most GeoMap filters CRC's GeoMap menu holds.</summary>
    public const int MaxGeomapFilters = 40;

    private const int GeomapFiltersPerRow = 10;
    private const int GeomapFiltersPerPage = 20;

    /// <summary>CRC's <c>AtcToolsMenu</c>.</summary>
    public static IReadOnlyList<EramMenuButton> AtcTools { get; } =
    [new(0, 0, EramButtonType.CrrFix), new(1, 1, EramButtonType.SpeedAdvisory), new(0, 2, EramButtonType.WeatherMenu)];

    /// <summary>CRC's <c>BrightnessMenu</c>.</summary>
    public static IReadOnlyList<EramMenuButton> Brightness { get; } =
    [
        new(0, 0, EramButtonType.MapBrightnessMenu),
        new(1, 0, EramButtonType.BacklightBrightness),
        new(0, 1, EramButtonType.CpdlcBrightnessMenu),
        new(1, 1, EramButtonType.ButtonBrightness),
        new(0, 2, EramButtonType.BackgroundBrightness),
        new(1, 2, EramButtonType.BorderBrightness),
        new(0, 3, EramButtonType.CursorBrightness),
        new(1, 3, EramButtonType.ToolbarBrightness),
        new(0, 4, EramButtonType.TextBrightness),
        new(1, 4, EramButtonType.ToolbarBorderBrightness),
        new(0, 5, EramButtonType.PairedTargetBrightness),
        new(1, 5, EramButtonType.SdBorderBrightness),
        new(0, 6, EramButtonType.UnpairedTargetBrightness),
        new(1, 6, EramButtonType.FdbBrightness),
        new(0, 7, EramButtonType.PairedTargetHistoryBrightness),
        new(1, 7, EramButtonType.FdbPortalBrightness),
        new(0, 8, EramButtonType.UnpairedTargetHistoryBrightness),
        new(1, 8, EramButtonType.SatCommBrightness),
        new(0, 9, EramButtonType.LdbBrightness),
        new(1, 9, EramButtonType.FdbOnFrequencyBrightness),
        new(0, 10, EramButtonType.SelectLdbBrightness),
        new(1, 10, EramButtonType.FdbLine4Brightness),
        new(0, 11, EramButtonType.WeatherBrightness),
        new(1, 11, EramButtonType.DwellBrightness),
        new(0, 12, EramButtonType.NexradBrightness),
        new(1, 12, EramButtonType.FdbPortalFenceBrightness),
        new(1, 13, EramButtonType.FelIndicatorBrightness),
        new(1, 14, EramButtonType.OutageBrightness),
    ];

    /// <summary>
    /// CRC's <c>MapBrightnessMenu</c> with YAAT's two map brightness groups, MAP A and MAP B, in the cells of CRC's first two
    /// numbered BCGs.
    /// </summary>
    public static IReadOnlyList<EramMenuButton> MapBrightness { get; } =
    [new(0, 0, EramButtonType.MapBcg1) { Lines = ("MAP", "A") }, new(0, 1, EramButtonType.MapBcg2) { Lines = ("MAP", "B") }];

    /// <summary>CRC's <c>FontSizeMenu</c>.</summary>
    public static IReadOnlyList<EramMenuButton> FontSize { get; } =
    [
        new(0, 0, EramButtonType.Fdb4thLineFontSize),
        new(1, 0, EramButtonType.RdbFontSize),
        new(0, 1, EramButtonType.FdbFontSize),
        new(1, 1, EramButtonType.LdbFontSize),
        new(0, 2, EramButtonType.FdbPortalFontSize),
        new(1, 2, EramButtonType.OutageFontSize),
        new(0, 3, EramButtonType.ToolbarFontSize),
    ];

    /// <summary>CRC's <c>DbFieldsMenu</c>.</summary>
    public static IReadOnlyList<EramMenuButton> DbFields { get; } =
    [
        new(0, 0, EramButtonType.NonRvsmIndicatorDisplay),
        new(1, 0, EramButtonType.NonAdsbIndicatorDisplay),
        new(0, 1, EramButtonType.VriOverride),
        new(1, 1, EramButtonType.NonAdsbBrightness),
        new(0, 2, EramButtonType.BeaconCodeDisplayOverride),
        new(1, 2, EramButtonType.SatCommDisplay),
        new(0, 3, EramButtonType.SpeedDisplayOverride),
        new(1, 3, EramButtonType.TfmRerouteIndicatorDisplay),
        new(0, 4, EramButtonType.DestinationDisplay),
        new(1, 4, EramButtonType.CrrRdbDisplay),
        new(0, 5, EramButtonType.AircraftDataDisplay),
        new(1, 5, EramButtonType.StaRdbDisplay),
        new(0, 6, EramButtonType.FdbLeaderLength),
        new(1, 6, EramButtonType.DelayRdbDisplay),
        new(0, 7, EramButtonType.BcastFlidDisplay),
        new(1, 7, EramButtonType.DelayFormatOverride),
        new(0, 8, EramButtonType.FdbPortalFenceDisplay),
    ];

    /// <summary>CRC's <c>ViewsMenu</c>.</summary>
    public static IReadOnlyList<EramMenuButton> Views { get; } =
    [
        new(0, 0, EramButtonType.AsView),
        new(1, 0, EramButtonType.DepartureView),
        new(0, 1, EramButtonType.AhiView),
        new(1, 1, EramButtonType.FlightEventView),
        new(0, 2, EramButtonType.CfrView),
        new(1, 2, EramButtonType.GroupSupView),
        new(0, 3, EramButtonType.CodeView),
        new(1, 3, EramButtonType.HoldViewView),
        new(0, 4, EramButtonType.CaView),
        new(1, 4, EramButtonType.InboundView),
        new(0, 5, EramButtonType.SavedAdvisoriesView),
        new(1, 5, EramButtonType.MrpView),
        new(0, 6, EramButtonType.MessageHistoryView),
        new(1, 6, EramButtonType.SaaFilterView),
        new(0, 7, EramButtonType.MessageOutView),
        new(1, 7, EramButtonType.DcrdView),
        new(0, 8, EramButtonType.TocSettingsView),
        new(1, 8, EramButtonType.WxView),
        new(0, 9, EramButtonType.CrrView),
    ];

    /// <summary>CRC's <c>RadarFilterMenu</c>.</summary>
    public static IReadOnlyList<EramMenuButton> RadarFilter { get; } =
    [
        new(0, 0, EramButtonType.AllLdbsFilter),
        new(1, 0, EramButtonType.SelectBeaconTargetFilter),
        new(0, 1, EramButtonType.PairedLdbsFilter),
        new(1, 1, EramButtonType.PermanentEchoFilter),
        new(0, 2, EramButtonType.UnpairedLdbsFilter),
        new(1, 2, EramButtonType.EcmStrobeLinesFilter),
        new(0, 3, EramButtonType.PrimaryTargetFilter),
        new(1, 3, EramButtonType.HistoryFilter),
        new(0, 4, EramButtonType.NonModeCFilter),
    ];

    /// <summary>
    /// The YAAT menu, YAAT's own: the classic bar's YAAT-only buttons in two rows, column by column, as the YAAT-2870 mock
    /// lays them out. ERAM's own menus already hold the classic entries it leaves out.
    /// </summary>
    public static IReadOnlyList<EramMenuButton> Yaat { get; } =
    [
        new(0, 0, EramButtonType.YaatProfileMenu),
        new(1, 0, EramButtonType.YaatTemplateMenu),
        new(0, 1, EramButtonType.YaatOthers),
        new(1, 1, EramButtonType.YaatAuto),
        new(0, 2, EramButtonType.YaatToolsMenu),
        new(1, 2, EramButtonType.YaatTdm),
        new(0, 3, EramButtonType.YaatMva),
        new(1, 3, EramButtonType.YaatDisplayMenu),
        new(0, 4, EramButtonType.YaatDbMenu),
        new(1, 4, EramButtonType.YaatViewsMenu),
        new(0, 5, EramButtonType.YaatLensMenu),
    ];

    /// <summary>The YAAT menu's PROFILE: the classic PROFILE tray's picks, two rows column by column.</summary>
    public static IReadOnlyList<EramMenuButton> YaatProfile { get; } =
        TwoRows(EramButtonType.YaatProfileStars, EramButtonType.YaatProfileEram, EramButtonType.YaatProfileMixed);

    /// <summary>The YAAT menu's TEMPLATE: the classic TEMPLATE tray's picks, two rows column by column.</summary>
    public static IReadOnlyList<EramMenuButton> YaatTemplate { get; } =
        TwoRows(
            EramButtonType.YaatTemplateStars,
            EramButtonType.YaatTemplateEram,
            EramButtonType.YaatTemplateMixed,
            EramButtonType.YaatTemplateEuroScope
        );

    /// <summary>The YAAT menu's TOOLS: the classic TOOLS tray, two rows column by column.</summary>
    public static IReadOnlyList<EramMenuButton> YaatTools { get; } =
        TwoRows(
            EramButtonType.YaatToolJRing,
            EramButtonType.YaatToolCone,
            EramButtonType.YaatToolDri5,
            EramButtonType.YaatToolDri3,
            EramButtonType.YaatToolSize
        );

    /// <summary>The YAAT menu's DISPLAY: the classic DISPLAY tray but WX, which ATC TOOLS holds, two rows column by column.</summary>
    public static IReadOnlyList<EramMenuButton> YaatDisplay { get; } =
        TwoRows(EramButtonType.YaatDisplayFix, EramButtonType.YaatDisplayLock, EramButtonType.YaatDisplayDcnf, EramButtonType.YaatDisplayOffCntr);

    /// <summary>
    /// The YAAT menu's DB: the classic DB tray but 4TH LINE, which DB FIELDS' DEST and TYPE press, and CHAR, which FONT's FDB
    /// steps; two rows column by column.
    /// </summary>
    public static IReadOnlyList<EramMenuButton> YaatDb { get; } =
        TwoRows(
            EramButtonType.YaatDbDwell,
            EramButtonType.YaatDbAtpa,
            EramButtonType.YaatDbAtpaAlertCones,
            EramButtonType.YaatDbAtpaMonitorCones,
            EramButtonType.YaatDbCaRing
        );

    /// <summary>The YAAT menu's VIEWS: the classic VIEWS tray but LA/CA/MCI and CRR, which ERAM's VIEWS holds, two rows column by column.</summary>
    public static IReadOnlyList<EramMenuButton> YaatViews { get; } = TwoRows(EramButtonType.YaatViewSsa, EramButtonType.YaatViewRpoQueue);

    /// <summary>The YAAT menu's PILOT LENS: the classic PILOT LENS tray, two rows column by column.</summary>
    public static IReadOnlyList<EramMenuButton> YaatLens { get; } =
        TwoRows(EramButtonType.YaatLensOn, EramButtonType.YaatLensPath, EramButtonType.YaatLensQueue, EramButtonType.YaatLensCheckIn);

    /// <summary>
    /// The fixed buttons of the menu <paramref name="menu"/> opens, or null for a menu whose buttons follow the Scope
    /// (GeoMap, PREFSET) and for any button with no YAAT menu.
    /// </summary>
    /// <param name="menu">The menu button.</param>
    public static IReadOnlyList<EramMenuButton>? Fixed(EramButtonType menu) =>
        menu switch
        {
            EramButtonType.AtcToolsMenu => AtcTools,
            EramButtonType.BrightnessMenu => Brightness,
            EramButtonType.MapBrightnessMenu => MapBrightness,
            EramButtonType.FontSizeMenu => FontSize,
            EramButtonType.DbFieldsMenu => DbFields,
            EramButtonType.ViewsMenu => Views,
            EramButtonType.RadarFilterMenu => RadarFilter,
            EramButtonType.YaatMenu => Yaat,
            EramButtonType.YaatProfileMenu => YaatProfile,
            EramButtonType.YaatTemplateMenu => YaatTemplate,
            EramButtonType.YaatToolsMenu => YaatTools,
            EramButtonType.YaatDisplayMenu => YaatDisplay,
            EramButtonType.YaatDbMenu => YaatDb,
            EramButtonType.YaatViewsMenu => YaatViews,
            EramButtonType.YaatLensMenu => YaatLens,
            _ => null,
        };

    /// <summary>
    /// The GeoMap menu: one filter toggle per entry of <paramref name="filters"/>, up to <see cref="MaxGeomapFilters"/>, the
    /// i-th at CRC's <c>GeomapMenu</c> cell (row <c>i / 10 % 2</c>, column <c>i % 10</c>); CRC shows filters 21 to 40 in place
    /// of the first twenty while Alt is held, so YAAT, with no Alt page, sets them in the ten columns after the first twenty's.
    /// Each filter is labelled with its map's STARS id over its short name and keyed by the STARS id.
    /// </summary>
    /// <param name="maps">Each filter's map, in order.</param>
    public static IReadOnlyList<EramMenuButton> Geomap(IReadOnlyList<(int StarsId, string ShortName)> maps) =>
        [
            .. maps.Take(MaxGeomapFilters)
                .Select(
                    (map, i) =>
                        new EramMenuButton(
                            (i / GeomapFiltersPerRow) % 2,
                            (i % GeomapFiltersPerRow) + (i / GeomapFiltersPerPage * GeomapFiltersPerRow),
                            EramButtonType.Filter1 + i
                        )
                        {
                            Lines = (map.StarsId.ToString(CultureInfo.InvariantCulture), map.ShortName),
                            Key = map.StarsId.ToString(CultureInfo.InvariantCulture),
                        }
                ),
        ];

    /// <summary>
    /// The PREFSET menu: one button per pref set of <paramref name="setNames"/> in their order, each labelled with its name
    /// split at the first space and keyed by it, then SAVE, SAVE AS, RENAME and DELETE, two to a column, top then bottom.
    /// </summary>
    /// <param name="setNames">The pref sets' names, in file order.</param>
    public static IReadOnlyList<EramMenuButton> Prefset(IReadOnlyList<string> setNames)
    {
        List<EramMenuButton> buttons =
        [
            .. setNames.Select((name, i) => PrefsetCell(i, EramButtonType.PrefsetSet) with { Lines = SplitName(name), Key = name }),
        ];
        EramButtonType[] items =
        [
            EramButtonType.PrefsetSave,
            EramButtonType.PrefsetSaveAs,
            EramButtonType.PrefsetRename,
            EramButtonType.PrefsetDelete,
        ];
        buttons.AddRange(items.Select((type, i) => PrefsetCell(setNames.Count + i, type)));
        return buttons;
    }

    /// <summary>A YAAT submenu: <paramref name="types"/> in order, two rows filled column by column, top then bottom.</summary>
    private static EramMenuButton[] TwoRows(params EramButtonType[] types) => [.. types.Select((type, i) => new EramMenuButton(i % 2, i / 2, type))];

    private static EramMenuButton PrefsetCell(int index, EramButtonType type) => new(index % 2, index / 2, type);

    /// <summary>A pref set's name as two lines: up to its first space, then the rest (empty for a one-word name).</summary>
    private static (string Line1, string Line2) SplitName(string name)
    {
        int space = name.IndexOf(' ', StringComparison.Ordinal);
        return (space < 0) ? (name, "") : (name[..space], name[(space + 1)..]);
    }
}
