namespace Yaat.Client.Views.Scope.Toolbar.Dcb;

/// <summary>One tile of a STARS DCB page: CRC's label, shape and interaction, and the Scope feature it acts through.</summary>
/// <param name="Label">
/// CRC's label, lines split by newlines; a <c>%value%</c> part is the value the bar fills in at runtime, and a map slot's label
/// is empty, as the bar names the map it holds.
/// </param>
/// <param name="Shape">The tile's shape, as CRC's DcbButtonKeyExtensions.Shape gives it.</param>
/// <param name="Kind">The tile's interaction; <see cref="DcbTileKind.Disabled"/> where YAAT has no feature for it yet.</param>
/// <param name="Button">The Scope toolbar button whose action the tile runs, or null.</param>
/// <param name="MapSlot">The map slot, 0 to 5, of a map tile; -1 for any other tile.</param>
public sealed record StarsDcbEntry(string Label, DcbTileShape Shape, DcbTileKind Kind, ScopeButtonId? Button, int MapSlot);

/// <summary>
/// The STARS DCB Main page in CRC's order (DcbMenuExtensions' Main menu), with CRC's labels (DisplayElementDcb.CreateButtons):
/// RANGE, PLACE CNTR, OFF CNTR, RR, PLACE RR, RR CNTR, MAPS, the six map slots, WX1 to WX6, BRITE, LDR DIR, LDR, CHAR SIZE,
/// MODE, PREF, SITE, SSA FILTER, GI TEXT FILTER and SHIFT. A button with no YAAT feature yet is disabled in place.
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
        Inert("MAPS"),
        .. Enumerable.Range(0, MapSlots).Select(slot => new StarsDcbEntry("", DcbTileShape.Half, DcbTileKind.Toggle, null, slot)),
        .. Enumerable.Range(1, WeatherLevels).Select(level => Disabled($"WX{level}\n", DcbTileShape.Weather)),
        Inert("BRITE"),
        Disabled("LDR DIR\n%value%", DcbTileShape.Half),
        Disabled("LDR\n%value%", DcbTileShape.Half),
        Inert("CHAR\nSIZE"),
        Disabled("MODE\nFSL", DcbTileShape.Full),
        Inert("PREF"),
        Disabled("SITE\nFUSED", DcbTileShape.Full),
        Disabled("SSA\nFILTER", DcbTileShape.Half),
        Disabled("GI TEXT\nFILTER", DcbTileShape.Half),
        Inert("SHIFT"),
    ];

    /// <summary>A tile with no YAAT feature yet, drawn dimmed in place.</summary>
    private static StarsDcbEntry Disabled(string label, DcbTileShape shape) => new(label, shape, DcbTileKind.Disabled, null, NoMap);

    /// <summary>A live full-height normal tile that does nothing yet: its submenu or page is still to come.</summary>
    private static StarsDcbEntry Inert(string label) => new(label, DcbTileShape.Full, DcbTileKind.Normal, null, NoMap);
}
