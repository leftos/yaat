using System.Globalization;

namespace Yaat.Client.Views.Scope.Toolbar.Dcb;

/// <summary>The pages a STARS DCB shows: its Main page and the submenus the Main page opens.</summary>
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
}

/// <summary>
/// The STARS DCB submenus MAPS, BRITE, CHAR SIZE and PREF in CRC's order (DcbMenuExtensions.Buttons), with CRC's labels
/// (DisplayElementDcb.CreateButtons) and shapes (DcbButtonKeyExtensions.Shape). Each starts with the Main page buttons CRC
/// dims while it is open (DcbMenuExtensions.DimmedButtons), drawn disabled with the Main page's labels; a button with no
/// YAAT feature yet is disabled in place, and DONE returns to the Main page.
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
        DoneTile(DcbTileShape.Half),
        new("CLR ALL", DcbTileShape.Half, DcbTileKind.Normal, ScopeButtonId.MapClear, NoMap),
        .. Enumerable.Range(0, MapSlots).Select(slot => new StarsDcbEntry("", DcbTileShape.Half, DcbTileKind.Toggle, null, slot)),
        Disabled("GEO\nMAPS", DcbTileShape.Half),
        Disabled("CURRENT", DcbTileShape.Half),
    ];

    /// <summary>
    /// The BRITE submenu: RANGE to BRITE dimmed, CRC's 17 brightness groups, each over the YAAT BRITE group it maps to (POS
    /// and OTH share YAAT's one targets group) or disabled where YAAT has none, then DONE.
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
        Unlit("RR"),
        Unlit("CMP"),
        Unlit("BCN"),
        Unlit("PRI"),
        Lit("HST", ScopeButtonId.BriteHist),
        Unlit("WX"),
        Unlit("WXC"),
        DoneTile(DcbTileShape.Full),
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
        DoneTile(DcbTileShape.Full),
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
        DoneTile(DcbTileShape.Half),
    ];

    /// <summary>The tiles of <paramref name="page"/>, left to right.</summary>
    public static IReadOnlyList<StarsDcbEntry> EntriesOf(StarsDcbPage page) =>
        page switch
        {
            StarsDcbPage.Maps => Maps,
            StarsDcbPage.Brite => Brite,
            StarsDcbPage.CharSize => CharSize,
            StarsDcbPage.Pref => Pref,
            _ => StarsMainPage.Entries,
        };

    /// <summary>How many of the Main page's buttons, from its first, <paramref name="page"/> shows dimmed at its start.</summary>
    public static int DimmedCountOf(StarsDcbPage page) =>
        page switch
        {
            StarsDcbPage.Maps => 7,
            StarsDcbPage.Brite => 20,
            StarsDcbPage.CharSize => 23,
            _ => 0,
        };

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

    /// <summary>The Main page buttons <paramref name="page"/> dims (<see cref="DimmedCountOf"/>), disabled in place with their labels.</summary>
    private static IEnumerable<StarsDcbEntry> Dimmed(StarsDcbPage page) =>
        StarsMainPage.Entries.Take(DimmedCountOf(page)).Select(e => e with { Kind = DcbTileKind.Disabled, Opens = null });

    private static StarsDcbEntry DoneTile(DcbTileShape shape) => new(Done, shape, DcbTileKind.Normal, null, NoMap) { Opens = StarsDcbPage.Main };

    /// <summary>A BRITE spinner over the YAAT BRITE group of <paramref name="id"/>, its value on its second line.</summary>
    private static StarsDcbEntry Lit(string label, ScopeButtonId id) => new($"{label}\n%value%", DcbTileShape.Half, DcbTileKind.Spinner, id, NoMap);

    /// <summary>A BRITE group YAAT does not have, disabled in place.</summary>
    private static StarsDcbEntry Unlit(string label) => Disabled($"{label}\n%value%", DcbTileShape.Half);

    private static StarsDcbEntry Disabled(string label, DcbTileShape shape) => new(label, shape, DcbTileKind.Disabled, null, NoMap);
}
