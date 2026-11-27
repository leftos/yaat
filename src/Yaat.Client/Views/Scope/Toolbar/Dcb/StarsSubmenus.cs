using System.Globalization;

namespace Yaat.Client.Views.Scope.Toolbar.Dcb;

/// <summary>The pages a STARS DCB shows: its Main and Aux pages and the submenus they open.</summary>
public enum StarsDcbPage
{
    /// <summary>The Main page (<see cref="StarsMainPage"/>).</summary>
    Main,

    /// <summary>The MAPS submenu.</summary>
    Maps,

    /// <summary>The BRITE submenu.</summary>
    Brite,

    /// <summary>The CHAR SIZE submenu.</summary>
    CharSize,

    /// <summary>The PREF submenu.</summary>
    Pref,

    /// <summary>The Aux page, which SHIFT shows in place of the Main page; a page, not a submenu.</summary>
    Aux,

    /// <summary>The TPA/ATPA submenu, which the Aux page opens.</summary>
    TpaAtpa,

    /// <summary>The YAAT page, which the Main page's YAAT tile opens: the YAAT-only controls of the classic bar.</summary>
    Yaat,

    /// <summary>The YAAT page's PROFILE page.</summary>
    YaatProfile,

    /// <summary>The YAAT page's TOOLS page; while it shows, the Scope's open menu is TOOLS, so the Rings tool is armed.</summary>
    YaatTools,

    /// <summary>The YAAT page's DISPLAY page.</summary>
    YaatDisplay,

    /// <summary>The YAAT page's DB page.</summary>
    YaatDb,

    /// <summary>The YAAT page's VIEWS page.</summary>
    YaatViews,

    /// <summary>The YAAT page's PILOT LENS page; while it shows, the Scope's open menu is PILOT LENS.</summary>
    YaatLens,
}

/// <summary>
/// The STARS DCB submenus MAPS, BRITE, CHAR SIZE and PREF, the Aux page and its TPA/ATPA submenu in CRC's order
/// (DcbMenuExtensions.Buttons), with CRC's labels (DisplayElementDcb.CreateButtons) and shapes (DcbButtonKeyExtensions.Shape).
/// Each submenu starts with the buttons of the page it opens from that CRC dims while it is open
/// (DcbMenuExtensions.DimmedButtons), drawn disabled with that page's labels; a button with no YAAT feature yet is disabled in
/// place, and DONE returns to the page the submenu opens from. The YAAT page the Main page's YAAT tile shows and the pages it
/// opens carry the classic bar's YAAT-only controls, each page opened from it dimming the YAAT page's tiles up to its opener.
/// </summary>
public static class StarsSubmenus
{
    /// <summary>How many map tiles the MAPS submenu holds.</summary>
    public const int MapSlots = 32;

    /// <summary>How many numbered pref-set tiles the PREF submenu holds.</summary>
    public const int PrefSlots = 32;

    private const int NoMap = -1;

    private const string Done = "DONE";

    /// <summary>
    /// The MAPS submenu: RANGE to MAPS dimmed, DONE, CLR ALL, the 32 map slots (each the Scope map toggle in its place, a
    /// slot with no map empty and disabled), GEO MAPS and CURRENT.
    /// </summary>
    public static IReadOnlyList<StarsDcbEntry> Maps { get; } =
    [
        .. Dimmed(StarsDcbPage.Maps),
        DoneTile(DcbTileShape.Half, StarsDcbPage.Main),
        new("CLR ALL", DcbTileShape.Half, DcbTileKind.Normal, ScopeButtonId.MapClear, NoMap),
        .. Enumerable.Range(0, MapSlots).Select(slot => new StarsDcbEntry("", DcbTileShape.Half, DcbTileKind.Toggle, null, slot)),
        Disabled("GEO\nMAPS", DcbTileShape.Half),
        Disabled("CURRENT", DcbTileShape.Half),
    ];

    /// <summary>
    /// The BRITE submenu: RANGE to BRITE dimmed, CRC's 17 brightness groups, each over the YAAT BRITE group it maps to (POS
    /// and OTH share YAAT's one targets group; RR steps YAAT's RINGS group: the range ring, the Rings tool's rings and the ATPA
    /// cones) or disabled where YAAT has none, then DONE.
    /// </summary>
    public static IReadOnlyList<StarsDcbEntry> Brite { get; } =
    [
        .. Dimmed(StarsDcbPage.Brite),
        Unlit("DCB"),
        Unlit("BKC"),
        Lit("MPA", ScopeButtonId.BriteMapA),
        Lit("MPB", ScopeButtonId.BriteMapB),
        Lit("FDB", ScopeButtonId.BriteFdb),
        Unlit("LST"),
        Lit("POS", ScopeButtonId.BriteTargets),
        Unlit("LDB"),
        Lit("OTH", ScopeButtonId.BriteTargets),
        Lit("TLS", ScopeButtonId.BriteVector),
        Lit("RR", ScopeButtonId.BriteRings),
        Unlit("CMP"),
        Unlit("BCN"),
        Unlit("PRI"),
        Lit("HST", ScopeButtonId.BriteHist),
        Unlit("WX"),
        Unlit("WXC"),
        DoneTile(DcbTileShape.Full, StarsDcbPage.Main),
    ];

    /// <summary>
    /// The CHAR SIZE submenu: RANGE to CHAR SIZE dimmed, DATA BLOCKS over the DB tray's CHAR, LISTS, DCB, TOOLS and POS
    /// disabled, then DONE.
    /// </summary>
    public static IReadOnlyList<StarsDcbEntry> CharSize { get; } =
    [
        .. Dimmed(StarsDcbPage.CharSize),
        new("DATA\nBLOCKS\n%value%", DcbTileShape.Full, DcbTileKind.Spinner, ScopeButtonId.DbChar, NoMap),
        Disabled("LISTS\n%value%", DcbTileShape.Full),
        Disabled("DCB\n%value%", DcbTileShape.Full),
        Disabled("TOOLS\n%value%", DcbTileShape.Full),
        Disabled("POS\n%value%", DcbTileShape.Full),
        DoneTile(DcbTileShape.Full, StarsDcbPage.Main),
    ];

    /// <summary>
    /// The PREF submenu: the 32 numbered set slots, each the Scope's pref set in that place in file order (an empty slot
    /// disabled under its number), DEFAULT, FSSTARS and RESTORE disabled, SAVE, CHG PIN disabled, SAVE AS, DELETE and DONE.
    /// </summary>
    public static IReadOnlyList<StarsDcbEntry> Pref { get; } =
    [
        .. Enumerable
            .Range(0, PrefSlots)
            .Select(slot => new StarsDcbEntry((slot + 1).ToString(CultureInfo.InvariantCulture), DcbTileShape.Half, DcbTileKind.Normal, null, NoMap)
            {
                PrefSlot = slot,
            }),
        Disabled("DEFAULT", DcbTileShape.Half),
        Disabled("FSSTARS", DcbTileShape.Half),
        Disabled("RESTORE", DcbTileShape.Half),
        new("SAVE", DcbTileShape.Half, DcbTileKind.Normal, ScopeButtonId.PrefSave, NoMap),
        Disabled("CHG PIN", DcbTileShape.Half),
        new("SAVE AS", DcbTileShape.Half, DcbTileKind.Normal, ScopeButtonId.PrefSaveAs, NoMap),
        new("DELETE", DcbTileShape.Half, DcbTileKind.Normal, ScopeButtonId.PrefDelete, NoMap),
        DoneTile(DcbTileShape.Half, StarsDcbPage.Main),
    ];

    /// <summary>
    /// The Aux page SHIFT shows: VOL to MCP disabled but HISTORY (the Scope's history spinner), DCB TOP, LEFT, RIGHT and
    /// BOTTOM disabled, PTL LNTH (the Scope's vector spinner), PTL OWN and PTL ALL (the vector ownership), DWELL ON (the DB
    /// tray's DWELL), TPA/ATPA (its submenu), TSAS and TIME LINE disabled, then SHIFT back to the Main page.
    /// </summary>
    public static IReadOnlyList<StarsDcbEntry> Aux { get; } =
    [
        Disabled("VOL\n%value%", DcbTileShape.Full),
        new("HISTORY\n%value%", DcbTileShape.Half, DcbTileKind.Spinner, ScopeButtonId.Hist, NoMap),
        Disabled("H_RATE\n%value%", DcbTileShape.Half),
        Disabled("CURSOR\nHOME", DcbTileShape.Full),
        Disabled("CSR SPD\n%value%", DcbTileShape.Full),
        Disabled("MAP\nUNCOR", DcbTileShape.Full),
        Disabled("UNCOR", DcbTileShape.Full),
        Disabled("BEACON\nMODE-2", DcbTileShape.Full),
        Disabled("RTQC", DcbTileShape.Full),
        Disabled("MCP", DcbTileShape.Full),
        Disabled("DCB\nTOP", DcbTileShape.Half),
        Disabled("DCB\nLEFT", DcbTileShape.Half),
        Disabled("DCB\nRIGHT", DcbTileShape.Half),
        Disabled("DCB\nBOTTOM", DcbTileShape.Half),
        new("PTL\nLNTH\n%value%", DcbTileShape.Full, DcbTileKind.Spinner, ScopeButtonId.Vector, NoMap),
        Ptl("PTL OWN", ScopeVectorOwnership.Own),
        Ptl("PTL ALL", ScopeVectorOwnership.All),
        new("DWELL\nON", DcbTileShape.Full, DcbTileKind.Toggle, ScopeButtonId.DbDwell, NoMap),
        new("TPA/\nATPA", DcbTileShape.Full, DcbTileKind.Normal, null, NoMap) { Opens = StarsDcbPage.TpaAtpa },
        Disabled("TSAS", DcbTileShape.Half),
        Disabled("TIME\nLINE", DcbTileShape.Half),
        new("SHIFT", DcbTileShape.Full, DcbTileKind.Normal, null, NoMap) { Opens = StarsDcbPage.Main },
    ];

    /// <summary>
    /// The TPA/ATPA submenu: VOL to TPA/ATPA of the Aux page dimmed, A/TPA MILEAGE disabled, INTRAIL DISTANCE, ALERT CONES and
    /// MONITOR CONES over the DB tray's ATPA DIST, ALERT CONES and MONITOR CONES, then DONE back to the Aux page.
    /// </summary>
    public static IReadOnlyList<StarsDcbEntry> TpaAtpa { get; } =
    [
        .. Dimmed(StarsDcbPage.TpaAtpa),
        Disabled("A/TPA\nMILEAGE\n%value%", DcbTileShape.Full),
        new("INTRAIL\nDISTANCE\n%value%", DcbTileShape.Full, DcbTileKind.Toggle, ScopeButtonId.DbAtpa, NoMap),
        new("ALERT\nCONES\n%value%", DcbTileShape.Full, DcbTileKind.Toggle, ScopeButtonId.DbAtpaAlertCones, NoMap),
        new("MONITOR\nCONES\n%value%", DcbTileShape.Full, DcbTileKind.Toggle, ScopeButtonId.DbAtpaMonitorCones, NoMap),
        DoneTile(DcbTileShape.Full, StarsDcbPage.Aux),
    ];

    /// <summary>
    /// The YAAT page the Main page's YAAT tile shows: the classic bar's YAAT-only controls, PROFILE (its value the profile
    /// name), OTHERS (its value the others level), AUTO, TOOLS, TDM and MVA, DISPLAY, DB, VIEWS and PILOT LENS, then DONE
    /// back to the Main page. PROFILE, TOOLS, DISPLAY, DB, VIEWS and PILOT LENS open a page of their own; the rest act in
    /// place.
    /// </summary>
    public static IReadOnlyList<StarsDcbEntry> Yaat { get; } =
    [
        new("PROFILE\n%value%", DcbTileShape.Full, DcbTileKind.Normal, ScopeButtonId.Profile, NoMap) { Opens = StarsDcbPage.YaatProfile },
        new("OTHERS\n%value%", DcbTileShape.Full, DcbTileKind.Normal, ScopeButtonId.Others, NoMap),
        new("AUTO", DcbTileShape.Full, DcbTileKind.Normal, ScopeButtonId.Auto, NoMap),
        YaatOpener("TOOLS", StarsDcbPage.YaatTools),
        new("TDM", DcbTileShape.Half, DcbTileKind.Toggle, ScopeButtonId.Tdm, NoMap),
        new("MVA", DcbTileShape.Half, DcbTileKind.Toggle, ScopeButtonId.Mva, NoMap),
        YaatOpener("DISPLAY", StarsDcbPage.YaatDisplay),
        YaatOpener("DB", StarsDcbPage.YaatDb),
        YaatOpener("VIEWS", StarsDcbPage.YaatViews),
        // Lit as the classic PILOT LENS tile is: while the lens is on or its page shows.
        new("PILOT\nLENS", DcbTileShape.Full, DcbTileKind.Normal, ScopeButtonId.Lens, NoMap) { Opens = StarsDcbPage.YaatLens },
        DoneTile(DcbTileShape.Full, StarsDcbPage.Main),
    ];

    /// <summary>The PROFILE page: STARS, ERAM, MIXED and EUROSCOPE, each switching the Scope's profile.</summary>
    public static IReadOnlyList<StarsDcbEntry> YaatProfile { get; } = YaatMenu(StarsDcbPage.YaatProfile, ScopeButtonId.Profile, []);

    /// <summary>The TOOLS page: J-RING, CONE, DRI 5, DRI 3 and the SIZE spinner.</summary>
    public static IReadOnlyList<StarsDcbEntry> YaatTools { get; } = YaatMenu(StarsDcbPage.YaatTools, ScopeButtonId.Tools, []);

    /// <summary>
    /// The DISPLAY page: FIX, WX, LOCK and DCNF, then the RR tray's SHOW as RR SHOW; OFF CNTR is on the Main page, as are the
    /// RR tray's PLACE RR, ring size and RR CNTR.
    /// </summary>
    public static IReadOnlyList<StarsDcbEntry> YaatDisplay { get; } =
        YaatMenu(
            StarsDcbPage.YaatDisplay,
            ScopeButtonId.Display,
            [new("RR\nSHOW", DcbTileShape.Half, DcbTileKind.Toggle, ScopeButtonId.RrShow, NoMap)],
            ScopeButtonId.DisplayOffCntr
        );

    /// <summary>
    /// The DB page: 4TH LINE and CA RING, then the ALERTS, LINE 4 and DWELL brightness spinners, which CRC's BRITE submenu has
    /// no group for; the DB tray's DWELL toggle is the Aux page's DWELL ON, ATPA DIST and the cones are on the TPA/ATPA submenu
    /// and CHAR is CHAR SIZE's DATA BLOCKS.
    /// </summary>
    public static IReadOnlyList<StarsDcbEntry> YaatDb { get; } =
        YaatMenu(
            StarsDcbPage.YaatDb,
            ScopeButtonId.Db,
            [
                BriteTile("ALERTS", ScopeButtonId.BriteAlerts),
                BriteTile("LINE 4", ScopeButtonId.BriteLine4),
                BriteTile("DWELL", ScopeButtonId.BriteDwell),
            ],
            ScopeButtonId.DbDwell,
            ScopeButtonId.DbAtpa,
            ScopeButtonId.DbAtpaAlertCones,
            ScopeButtonId.DbAtpaMonitorCones,
            ScopeButtonId.DbChar
        );

    /// <summary>The VIEWS page: SSA, LA/CA/MCI, RPO QUEUE and CRR.</summary>
    public static IReadOnlyList<StarsDcbEntry> YaatViews { get; } = YaatMenu(StarsDcbPage.YaatViews, ScopeButtonId.Views, []);

    /// <summary>The PILOT LENS page: ON, PATH, QUEUE and CHECKIN, then the lens brightness spinner LENS BRITE.</summary>
    public static IReadOnlyList<StarsDcbEntry> YaatLens { get; } =
        YaatMenu(StarsDcbPage.YaatLens, ScopeButtonId.Lens, [BriteTile("LENS\nBRITE", ScopeButtonId.BriteLens)]);

    /// <summary>The tiles of <paramref name="page"/>, left to right.</summary>
    public static IReadOnlyList<StarsDcbEntry> EntriesOf(StarsDcbPage page) =>
        page switch
        {
            StarsDcbPage.Maps => Maps,
            StarsDcbPage.Brite => Brite,
            StarsDcbPage.CharSize => CharSize,
            StarsDcbPage.Pref => Pref,
            StarsDcbPage.Aux => Aux,
            StarsDcbPage.TpaAtpa => TpaAtpa,
            StarsDcbPage.Yaat => Yaat,
            StarsDcbPage.YaatProfile => YaatProfile,
            StarsDcbPage.YaatTools => YaatTools,
            StarsDcbPage.YaatDisplay => YaatDisplay,
            StarsDcbPage.YaatDb => YaatDb,
            StarsDcbPage.YaatViews => YaatViews,
            StarsDcbPage.YaatLens => YaatLens,
            _ => StarsMainPage.Entries,
        };

    /// <summary>
    /// How many of the buttons of the page <paramref name="page"/> opens from (<see cref="ParentOf"/>), from its first,
    /// <paramref name="page"/> shows dimmed at its start.
    /// </summary>
    public static int DimmedCountOf(StarsDcbPage page) =>
        page switch
        {
            StarsDcbPage.Maps => 7,
            StarsDcbPage.Brite => 20,
            StarsDcbPage.CharSize => 23,
            StarsDcbPage.TpaAtpa => 19,
            _ when IsYaatMenu(page) => IndexOfOpener(page) + 1,
            _ => 0,
        };

    /// <summary>
    /// The page <paramref name="page"/> opens from, which its DONE and Esc return to: the Aux page for TPA/ATPA, the YAAT page
    /// for each page it opens, else the Main page.
    /// </summary>
    public static StarsDcbPage ParentOf(StarsDcbPage page) =>
        page switch
        {
            StarsDcbPage.TpaAtpa => StarsDcbPage.Aux,
            _ when IsYaatMenu(page) => StarsDcbPage.Yaat,
            _ => StarsDcbPage.Main,
        };

    /// <summary>Whether <paramref name="page"/> is one the YAAT page opens.</summary>
    private static bool IsYaatMenu(StarsDcbPage page) =>
        page
            is StarsDcbPage.YaatProfile
                or StarsDcbPage.YaatTools
                or StarsDcbPage.YaatDisplay
                or StarsDcbPage.YaatDb
                or StarsDcbPage.YaatViews
                or StarsDcbPage.YaatLens;

    /// <summary>The place of the YAAT page tile that opens <paramref name="page"/>, from 0.</summary>
    private static int IndexOfOpener(StarsDcbPage page) => Yaat.ToList().FindIndex(e => e.Opens == page);

    /// <summary>Whether <paramref name="page"/> is a submenu, which takes Esc, rather than the Main or the Aux page.</summary>
    public static bool IsSubmenu(StarsDcbPage page) => page is not (StarsDcbPage.Main or StarsDcbPage.Aux);

    /// <summary>How many map slots <paramref name="page"/> holds.</summary>
    public static int MapSlotsOf(StarsDcbPage page) =>
        page switch
        {
            StarsDcbPage.Main or StarsDcbPage.Brite or StarsDcbPage.CharSize => StarsMainPage.MapSlots,
            StarsDcbPage.Maps => MapSlots,
            _ => 0,
        };

    /// <summary>How many pref-set slots <paramref name="page"/> holds.</summary>
    public static int PrefSlotsOf(StarsDcbPage page) => (page == StarsDcbPage.Pref) ? PrefSlots : 0;

    /// <summary>
    /// The buttons of the page <paramref name="page"/> opens from that it dims (<see cref="DimmedCountOf"/>), disabled in
    /// place with their labels.
    /// </summary>
    private static IEnumerable<StarsDcbEntry> Dimmed(StarsDcbPage page) =>
        EntriesOf(ParentOf(page)).Take(DimmedCountOf(page)).Select(e => e with { Kind = DcbTileKind.Disabled, Opens = null, Ownership = null });

    /// <summary>A DONE tile of <paramref name="shape"/> that returns to <paramref name="page"/>.</summary>
    private static StarsDcbEntry DoneTile(DcbTileShape shape, StarsDcbPage page) =>
        new(Done, shape, DcbTileKind.Normal, null, NoMap) { Opens = page };

    /// <summary>A PTL OWN or PTL ALL toggle, lit while the Scope's vector ownership is <paramref name="ownership"/>.</summary>
    private static StarsDcbEntry Ptl(string label, ScopeVectorOwnership ownership) =>
        new(label, DcbTileShape.Half, DcbTileKind.Toggle, null, NoMap) { Ownership = ownership };

    /// <summary>A BRITE spinner over the YAAT BRITE group of <paramref name="id"/>, its value on its second line.</summary>
    private static StarsDcbEntry Lit(string label, ScopeButtonId id) => new($"{label}\n%value%", DcbTileShape.Half, DcbTileKind.Spinner, id, NoMap);

    /// <summary>A BRITE group YAAT does not have, disabled in place.</summary>
    private static StarsDcbEntry Unlit(string label) => Disabled($"{label}\n%value%", DcbTileShape.Half);

    private static StarsDcbEntry Disabled(string label, DcbTileShape shape) => new(label, shape, DcbTileKind.Disabled, null, NoMap);

    /// <summary>A full-height YAAT page tile that opens <paramref name="page"/>.</summary>
    private static StarsDcbEntry YaatOpener(string label, StarsDcbPage page) =>
        new(label, DcbTileShape.Full, DcbTileKind.Normal, null, NoMap) { Opens = page };

    /// <summary>
    /// A page the YAAT page opens: the YAAT page's tiles up to its opener dimmed, the classic tray's buttons of
    /// <paramref name="menu"/> in tray order but <paramref name="dropped"/>, the tiles <paramref name="extras"/> the tray does not
    /// hold, then DONE back to the YAAT page.
    /// </summary>
    private static IReadOnlyList<StarsDcbEntry> YaatMenu(
        StarsDcbPage page,
        ScopeButtonId menu,
        StarsDcbEntry[] extras,
        params ScopeButtonId[] dropped
    ) =>
        [
            .. Dimmed(page),
            .. (ScopeToolbarButtons.Find(menu) ?? throw new InvalidOperationException($"The YAAT page's {menu} menu has no classic toolbar button."))
                .Children.Where(c => !dropped.Contains(c.Id))
                .Select(TrayTile),
            .. extras,
            DoneTile(DcbTileShape.Full, StarsDcbPage.Yaat),
        ];

    /// <summary>
    /// A full-height spinner over the YAAT BRITE group of <paramref name="id"/>, its value on the line after
    /// <paramref name="label"/>.
    /// </summary>
    private static StarsDcbEntry BriteTile(string label, ScopeButtonId id) =>
        new($"{label}\n%value%", DcbTileShape.Full, DcbTileKind.Spinner, id, NoMap);

    /// <summary>
    /// The tile of a classic tray button: a spinner a full-height spinner with its value on its second line, a toggle a half
    /// toggle, DCNF a half normal tile with its mode on its second line, anything else a half normal tile.
    /// </summary>
    private static StarsDcbEntry TrayTile(ScopeButton button) =>
        button.Kind switch
        {
            ScopeButtonKind.Spinner => new($"{button.Label}\n%value%", DcbTileShape.Full, DcbTileKind.Spinner, button.Id, NoMap),
            ScopeButtonKind.Toggle => new(button.Label, DcbTileShape.Half, DcbTileKind.Toggle, button.Id, NoMap),
            // DCNF shows its mode on a second line, as the classic tray's DCNF does.
            _ when button.Id == ScopeButtonId.DisplayDcnf => new($"{button.Label}\n%value%", DcbTileShape.Half, DcbTileKind.Normal, button.Id, NoMap),
            _ => new(button.Label, DcbTileShape.Half, DcbTileKind.Normal, button.Id, NoMap),
        };
}
