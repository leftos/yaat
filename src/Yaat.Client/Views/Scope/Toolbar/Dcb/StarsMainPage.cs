namespace Yaat.Client.Views.Scope.Toolbar.Dcb;

/// <summary>One tile of a STARS DCB page: CRC's label, shape and interaction, and the Scope feature it acts through.</summary>
/// <param name="Label">
/// CRC's label, lines split by newlines; a <c>%value%</c> part is the value the bar fills in at runtime, and a map slot's label
/// is empty, as the bar names the map it holds.
/// </param>
/// <param name="Shape">The tile's shape, as CRC's DcbButtonKeyExtensions.Shape gives it.</param>
/// <param name="Kind">The tile's interaction; <see cref="DcbTileKind.Disabled"/> where YAAT has no feature for it yet.</param>
/// <param name="Button">The Scope toolbar button whose action the tile runs, or null.</param>
/// <param name="MapSlot">The map slot of a map tile, from 0, the place in the Scope's map toggles it shows; -1 for any other tile.</param>
public sealed record StarsDcbEntry(string Label, DcbTileShape Shape, DcbTileKind Kind, ScopeButtonId? Button, int MapSlot)
{
    /// <summary>
    /// The page a press on the tile shows: a submenu from the button that opens it; a submenu's DONE returns to the page it
    /// opens from; SHIFT shows the Aux or the Main page; else null.
    /// </summary>
    public StarsDcbPage? Opens { get; init; }

    /// <summary>The pref-set slot of a PREF submenu set tile, from 0, the place in the Scope's pref sets it shows; -1 for any other tile.</summary>
    public int PrefSlot { get; init; } = -1;

    /// <summary>The vector ownership a PTL OWN or PTL ALL tile sets and is lit by; null for any other tile.</summary>
    public ScopeVectorOwnership? Ownership { get; init; }
}

/// <summary>
/// The STARS DCB Main page in CRC's order (DcbMenuExtensions' Main menu), with CRC's labels (DisplayElementDcb.CreateButtons):
/// RANGE, PLACE CNTR, OFF CNTR, RR, PLACE RR, RR CNTR, MAPS, the six map slots, WX1 to WX6, BRITE, LDR DIR, LDR, CHAR SIZE,
/// MODE, PREF, SITE, SSA FILTER, GI TEXT FILTER and SHIFT, then YAAT's own YAAT tile. A button with no YAAT feature yet is
/// disabled in place; MAPS, BRITE, CHAR SIZE and PREF open their submenus, SHIFT shows the Aux page and YAAT the YAAT page
/// (<see cref="StarsSubmenus"/>), the bar drawing the YAAT tile at its right edge when it is wide enough.
/// </summary>
public static class StarsMainPage
{
    /// <summary>How many map tiles the Main page holds.</summary>
    public const int MapSlots = 6;

    /// <summary>How many weather tiles, WX1 to WX6, the Main page holds.</summary>
    public const int WeatherLevels = 6;

    private const int NoMap = -1;

    /// <summary>The Main page's tiles, left to right in CRC's order.</summary>
    public static IReadOnlyList<StarsDcbEntry> Entries { get; } =
    [
        new("RANGE\n%value%", DcbTileShape.Full, DcbTileKind.Spinner, ScopeButtonId.Range, NoMap),
        Disabled("PLACE\nCNTR", DcbTileShape.Half),
        new("OFF\nCNTR", DcbTileShape.Half, DcbTileKind.Toggle, ScopeButtonId.DisplayOffCntr, NoMap),
        new("RR\n%value%", DcbTileShape.Full, DcbTileKind.Spinner, ScopeButtonId.RrSize, NoMap),
        new("PLACE\nRR", DcbTileShape.Half, DcbTileKind.Toggle, ScopeButtonId.RrPlace, NoMap),
        new("RR\nCNTR", DcbTileShape.Half, DcbTileKind.Normal, ScopeButtonId.RrCenter, NoMap),
        Opener("MAPS", StarsDcbPage.Maps),
        .. Enumerable.Range(0, MapSlots).Select(slot => new StarsDcbEntry("", DcbTileShape.Half, DcbTileKind.Toggle, null, slot)),
        .. Enumerable.Range(1, WeatherLevels).Select(level => Disabled($"WX{level}\n", DcbTileShape.Weather)),
        Opener("BRITE", StarsDcbPage.Brite),
        Disabled("LDR DIR\n%value%", DcbTileShape.Half),
        Disabled("LDR\n%value%", DcbTileShape.Half),
        Opener("CHAR\nSIZE", StarsDcbPage.CharSize),
        Disabled("MODE\nFSL", DcbTileShape.Full),
        Opener("PREF", StarsDcbPage.Pref),
        Disabled("SITE\nFUSED", DcbTileShape.Full),
        Disabled("SSA\nFILTER", DcbTileShape.Half),
        Disabled("GI TEXT\nFILTER", DcbTileShape.Half),
        Opener("SHIFT", StarsDcbPage.Aux),
        Opener("YAAT", StarsDcbPage.Yaat),
    ];

    /// <summary>A tile with no YAAT feature yet, drawn dimmed in place.</summary>
    private static StarsDcbEntry Disabled(string label, DcbTileShape shape) => new(label, shape, DcbTileKind.Disabled, null, NoMap);

    /// <summary>A full-height normal tile that shows <paramref name="page"/>.</summary>
    private static StarsDcbEntry Opener(string label, StarsDcbPage page) =>
        new(label, DcbTileShape.Full, DcbTileKind.Normal, null, NoMap) { Opens = page };
}
