using System.Globalization;

namespace Yaat.Client.Views.Scope.Toolbar;

/// <summary>Identity of one Scope toolbar button, top-level or in a menu.</summary>
public enum ScopeButtonId
{
    /// <summary>The Template menu.</summary>
    Template,

    /// <summary>The Template menu's STARS pick.</summary>
    TemplateStars,

    /// <summary>The Template menu's ERAM pick.</summary>
    TemplateEram,

    /// <summary>The Template menu's Mixed pick.</summary>
    TemplateMixed,

    /// <summary>The Template menu's EuroScope pick.</summary>
    TemplateEuroScope,

    /// <summary>The range spinner.</summary>
    Range,

    /// <summary>The MAPS menu; its tray holds CLR ALL and the Scope's map toggles.</summary>
    Maps,

    /// <summary>The MAPS menu's CLR ALL.</summary>
    MapClear,

    /// <summary>A MAPS menu map toggle: one id shared by every map toggle, each told apart by its STARS id (the button's value).</summary>
    MapToggle,

    /// <summary>The DB menu.</summary>
    Db,

    /// <summary>The DB menu's 4TH LINE toggle.</summary>
    Db4thLine,

    /// <summary>The DB menu's DWELL toggle.</summary>
    DbDwell,

    /// <summary>The DB menu's ATPA DIST toggle.</summary>
    DbAtpa,

    /// <summary>The DB menu's CHAR spinner.</summary>
    DbChar,

    /// <summary>The Views menu.</summary>
    Views,

    /// <summary>The Views menu's SSA toggle.</summary>
    ViewSsa,

    /// <summary>The Views menu's LA/CA/MCI toggle.</summary>
    ViewCa,

    /// <summary>The BRITE menu.</summary>
    Brite,

    /// <summary>The BRITE menu's MAP A spinner.</summary>
    BriteMapA,

    /// <summary>The BRITE menu's MAP B spinner.</summary>
    BriteMapB,

    /// <summary>The BRITE menu's TARGETS spinner.</summary>
    BriteTargets,

    /// <summary>The BRITE menu's FDB spinner.</summary>
    BriteFdb,

    /// <summary>The BRITE menu's LINE 4 spinner.</summary>
    BriteLine4,

    /// <summary>The BRITE menu's ALERTS spinner.</summary>
    BriteAlerts,

    /// <summary>The BRITE menu's DWELL spinner.</summary>
    BriteDwell,
}

/// <summary>How a Scope toolbar button acts.</summary>
public enum ScopeButtonKind
{
    /// <summary>A click runs the button's command.</summary>
    Command,

    /// <summary>A click turns the button's setting on or off.</summary>
    Toggle,

    /// <summary>A click opens or closes the tray of the button's children.</summary>
    Menu,

    /// <summary>The wheel steps the button's value; it shows the value after its label.</summary>
    Spinner,
}

/// <summary>One Scope toolbar button.</summary>
/// <param name="Id">The button's identity.</param>
/// <param name="Label">The uppercase label the button shows.</param>
/// <param name="Kind">How the button acts.</param>
/// <param name="Children">A menu's buttons, in tray order; empty for any other kind.</param>
/// <param name="Value">
/// The value the button stands for: the template id a template pick picks, the view name a view toggle opens, the group name
/// a BRITE spinner steps, the STARS id a map toggle turns on, the item a DB button sets; else null.
/// </param>
/// <param name="Command">
/// A command button's full dot command; a toggle's command stem, to which a press appends <c> on</c> or <c> off</c>; null for
/// a menu or a spinner.
/// </param>
public sealed record ScopeButton(
    ScopeButtonId Id,
    string Label,
    ScopeButtonKind Kind,
    IReadOnlyList<ScopeButton> Children,
    string? Value,
    string? Command
);

/// <summary>The Scope toolbar's buttons, in bar order, and the <c>.scope</c> and <c>.view</c> commands they run.</summary>
public static class ScopeToolbarButtons
{
    /// <summary>The bar's top-level buttons, left to right. A button is listed only once its behaviour exists.</summary>
    public static readonly IReadOnlyList<ScopeButton> All =
    [
        new(
            ScopeButtonId.Template,
            "TEMPLATE",
            ScopeButtonKind.Menu,
            [
                TemplatePick(ScopeButtonId.TemplateStars, "STARS", ScopeTemplates.StarsId),
                TemplatePick(ScopeButtonId.TemplateEram, "ERAM", ScopeTemplates.EramId),
                TemplatePick(ScopeButtonId.TemplateMixed, "MIXED", ScopeTemplates.MixedId),
                TemplatePick(ScopeButtonId.TemplateEuroScope, "EUROSCOPE", ScopeTemplates.EuroScopeId),
            ],
            null,
            null
        ),
        new(ScopeButtonId.Range, "RANGE", ScopeButtonKind.Spinner, [], null, null),
        // The Scope's map toggles are facility data: ScopeViewModel.TrayChildren builds this tray.
        new(ScopeButtonId.Maps, "MAPS", ScopeButtonKind.Menu, [], null, null),
        new(
            ScopeButtonId.Db,
            "DB",
            ScopeButtonKind.Menu,
            [
                DbToggle(ScopeButtonId.Db4thLine, "4TH LINE", DbLine4Item),
                DbToggle(ScopeButtonId.DbDwell, "DWELL", DbDwellItem),
                DbToggle(ScopeButtonId.DbAtpa, "ATPA DIST", DbAtpaItem),
                new(ScopeButtonId.DbChar, "CHAR", ScopeButtonKind.Spinner, [], DbCharItem, null),
            ],
            null,
            null
        ),
        new(
            ScopeButtonId.Views,
            "VIEWS",
            ScopeButtonKind.Menu,
            [ViewToggle(ScopeButtonId.ViewSsa, "SSA", ViewSsaName), ViewToggle(ScopeButtonId.ViewCa, "LA/CA/MCI", ViewCaName)],
            null,
            null
        ),
        new(
            ScopeButtonId.Brite,
            "BRITE",
            ScopeButtonKind.Menu,
            [
                BriteSpinner(ScopeButtonId.BriteMapA, "MAP A", "mapa"),
                BriteSpinner(ScopeButtonId.BriteMapB, "MAP B", "mapb"),
                BriteSpinner(ScopeButtonId.BriteTargets, "TARGETS", "targets"),
                BriteSpinner(ScopeButtonId.BriteFdb, "FDB", "fdb"),
                BriteSpinner(ScopeButtonId.BriteLine4, "LINE 4", "line4"),
                BriteSpinner(ScopeButtonId.BriteAlerts, "ALERTS", "alerts"),
                BriteSpinner(ScopeButtonId.BriteDwell, "DWELL", "dwell"),
            ],
            null,
            null
        ),
    ];

    /// <summary>The SSA's view name, as <c>.view</c> takes it.</summary>
    public const string ViewSsaName = "ssa";

    /// <summary>The LA/CA/MCI list's view name, as <c>.view</c> takes it.</summary>
    public const string ViewCaName = "ca";

    /// <summary>The DB 4TH LINE item, as <c>.scope db</c> takes it.</summary>
    public const string DbLine4Item = "4thline";

    /// <summary>The DB DWELL item, as <c>.scope db</c> takes it.</summary>
    public const string DbDwellItem = "dwell";

    /// <summary>The DB ATPA DIST item, as <c>.scope db</c> takes it.</summary>
    public const string DbAtpaItem = "atpa";

    /// <summary>The DB CHAR item, as <c>.scope db</c> takes it.</summary>
    public const string DbCharItem = "char";

    /// <summary>The command that turns every Scope map off (the MAPS tray's CLR ALL).</summary>
    public const string MapClearCommand = ".scope map clear";

    /// <summary>The MAPS tray's first button, CLR ALL.</summary>
    public static readonly ScopeButton MapClear = new(ScopeButtonId.MapClear, "CLR ALL", ScopeButtonKind.Command, [], null, MapClearCommand);

    /// <summary>The MAPS tray's toggle for the map with <paramref name="starsId"/>, labelled <c>{starsId} {shortName}</c>.</summary>
    public static ScopeButton MapToggle(int starsId, string shortName)
    {
        string id = starsId.ToString(CultureInfo.InvariantCulture);
        return new(ScopeButtonId.MapToggle, $"{id} {shortName}", ScopeButtonKind.Toggle, [], id, $".scope map {id}");
    }

    /// <summary>The command that turns the Scope map with <paramref name="starsId"/> on (<paramref name="on"/>) or off.</summary>
    public static string MapCommand(int starsId, bool on) => $".scope map {starsId.ToString(CultureInfo.InvariantCulture)} {(on ? "on" : "off")}";

    /// <summary>The command that turns the DB item <paramref name="item"/> on (<paramref name="on"/>) or off.</summary>
    public static string DbToggleCommand(string item, bool on) => $"{DbCommandStem(item)} {(on ? "on" : "off")}";

    /// <summary>The command that sets the datablock point size to <paramref name="size"/>.</summary>
    public static string DbCharCommand(int size) => $"{DbCommandStem(DbCharItem)} {size.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The command that opens (<paramref name="open"/>) or closes the view named <paramref name="viewName"/>.</summary>
    public static string ViewCommand(string viewName, bool open) => $"{ViewCommandStem(viewName)} {(open ? "on" : "off")}";

    /// <summary>The command that picks the template (or Mixed) with <paramref name="templateId"/>.</summary>
    public static string TemplateCommand(string templateId) => $".scope template {templateId}";

    /// <summary>The command that sets the scope's range to <paramref name="rangeNm"/> NM.</summary>
    public static string RangeCommand(int rangeNm) => $".scope range {rangeNm}";

    /// <summary>The command that sets the BRITE group named <paramref name="group"/> to <paramref name="value"/>.</summary>
    public static string BriteCommand(string group, int value) =>
        $".scope brite {group.ToLowerInvariant()} {value.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The button with <paramref name="id"/>, top-level or in a menu, or null.</summary>
    public static ScopeButton? Find(ScopeButtonId id) =>
        All.FirstOrDefault(b => b.Id == id) ?? All.SelectMany(b => b.Children).FirstOrDefault(c => c.Id == id);

    /// <summary>The BRITE group the spinner with <paramref name="id"/> steps, or null when it is no BRITE spinner.</summary>
    public static ScopeBrightnessGroup? BriteGroupOf(ScopeButtonId id) =>
        (Find(id) is { Kind: ScopeButtonKind.Spinner, Value: { } group }) ? ScopeBrightness.FindGroup(group) : null;

    private static ScopeButton BriteSpinner(ScopeButtonId id, string label, string group) => new(id, label, ScopeButtonKind.Spinner, [], group, null);

    private static ScopeButton TemplatePick(ScopeButtonId id, string label, string templateId) =>
        new(id, label, ScopeButtonKind.Command, [], templateId, TemplateCommand(templateId));

    private static ScopeButton ViewToggle(ScopeButtonId id, string label, string viewName) =>
        new(id, label, ScopeButtonKind.Toggle, [], viewName, ViewCommandStem(viewName));

    private static string ViewCommandStem(string viewName) => $".view {viewName}";

    private static ScopeButton DbToggle(ScopeButtonId id, string label, string item) =>
        new(id, label, ScopeButtonKind.Toggle, [], item, DbCommandStem(item));

    private static string DbCommandStem(string item) => $".scope db {item}";
}
