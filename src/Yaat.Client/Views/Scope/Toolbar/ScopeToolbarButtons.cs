using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.Views.Scope.Toolbar;

/// <summary>Identity of one Scope toolbar button, top-level or in a menu.</summary>
public enum ScopeButtonId
{
    /// <summary>The PROFILE menu.</summary>
    Profile,

    /// <summary>The PROFILE menu's STARS pick.</summary>
    ProfileStars,

    /// <summary>The PROFILE menu's ERAM pick.</summary>
    ProfileEram,

    /// <summary>The PROFILE menu's MIXED pick.</summary>
    ProfileMixed,

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

    /// <summary>OTHERS: shows others' block level in effect and flips it, pinning it; lit while it is pinned.</summary>
    Others,

    /// <summary>VECTOR: the wheel steps the vector minutes in effect, pinning them; a click flips OWN and ALL.</summary>
    Vector,

    /// <summary>HIST: the wheel steps the history dot count in effect, pinning it.</summary>
    Hist,

    /// <summary>AUTO: clears every pinned band value; lit while any is pinned.</summary>
    Auto,

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

    /// <summary>The TOOLS menu; its tray holds the Rings tool.</summary>
    Tools,

    /// <summary>The TOOLS menu's J-RING: arms the Rings tool to place J-rings; lit while armed.</summary>
    ToolJRing,

    /// <summary>The TOOLS menu's CONE: arms the Rings tool to place cones; lit while armed.</summary>
    ToolCone,

    /// <summary>The TOOLS menu's DRI 5: arms the Rings tool to place 5 NM DRI halos; lit while armed.</summary>
    ToolDri5,

    /// <summary>The TOOLS menu's DRI 3: arms the Rings tool to place 3 NM DRI halos; lit while armed.</summary>
    ToolDri3,

    /// <summary>The TOOLS menu's SIZE spinner: the J-ring radius and cone length a placement takes.</summary>
    ToolSize,

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

    /// <summary>The BRITE menu's HIST spinner.</summary>
    BriteHist,

    /// <summary>The BRITE menu's VECTOR spinner.</summary>
    BriteVector,

    /// <summary>The BRITE menu's RINGS spinner.</summary>
    BriteRings,

    /// <summary>The PREF menu; its tray holds the facility's pref sets and the pref-set actions.</summary>
    Pref,

    /// <summary>A PREF menu set button: one id shared by every set, each told apart by its name (the button's value).</summary>
    PrefSet,

    /// <summary>The PREF menu's SAVE.</summary>
    PrefSave,

    /// <summary>The PREF menu's SAVE AS, which types a new set's name into the command box.</summary>
    PrefSaveAs,

    /// <summary>The PREF menu's RENAME, which types the active set's new name into the command box.</summary>
    PrefRename,

    /// <summary>The PREF menu's DELETE of the active set.</summary>
    PrefDelete,

    /// <summary>DEL TEAROFF: marks tear-offs, then deletes the marked ones.</summary>
    DeleteTearoff,
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
/// The value the button stands for: the profile name a profile pick picks, the template id a template pick picks, the view
/// name a view toggle opens, the group name
/// a BRITE spinner steps, the STARS id a map toggle turns on, the set name a PREF set button loads, the item a DB button
/// sets; else null.
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
    /// <summary>
    /// The bar's top-level buttons, left to right, before a profile orders them (<c>ScopeViewModel.ToolbarButtons</c>).
    /// A button is listed only once its behaviour exists.
    /// </summary>
    public static readonly IReadOnlyList<ScopeButton> All =
    [
        new(
            ScopeButtonId.Profile,
            "PROFILE",
            ScopeButtonKind.Menu,
            [
                ProfilePick(ScopeButtonId.ProfileStars, "STARS", ScopeProfileKind.Stars),
                ProfilePick(ScopeButtonId.ProfileEram, "ERAM", ScopeProfileKind.Eram),
                ProfilePick(ScopeButtonId.ProfileMixed, "MIXED", ScopeProfileKind.Mixed),
            ],
            null,
            null
        ),
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
        // A press runs .scope others with the level other than the one in effect: ScopeViewModel.Press builds it.
        new(ScopeButtonId.Others, "OTHERS", ScopeButtonKind.Command, [], null, null),
        // A click runs .scope vector with the other of OWN and ALL: ScopeViewModel.Press builds it.
        new(ScopeButtonId.Vector, "VECTOR", ScopeButtonKind.Spinner, [], null, null),
        new(ScopeButtonId.Hist, "HIST", ScopeButtonKind.Spinner, [], null, null),
        new(
            ScopeButtonId.Tools,
            "TOOLS",
            ScopeButtonKind.Menu,
            [
                RingTool(ScopeButtonId.ToolJRing, "J-RING", ScopeRingTool.JRing),
                RingTool(ScopeButtonId.ToolCone, "CONE", ScopeRingTool.Cone),
                RingTool(ScopeButtonId.ToolDri5, "DRI 5", ScopeRingTool.Dri5),
                RingTool(ScopeButtonId.ToolDri3, "DRI 3", ScopeRingTool.Dri3),
                new(ScopeButtonId.ToolSize, "SIZE", ScopeButtonKind.Spinner, [], null, null),
            ],
            null,
            null
        ),
        new(ScopeButtonId.Auto, "AUTO", ScopeButtonKind.Command, [], null, AutoCommand),
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
                BriteSpinner(ScopeButtonId.BriteHist, "HIST", "hist"),
                BriteSpinner(ScopeButtonId.BriteVector, "VECTOR", "vector"),
                BriteSpinner(ScopeButtonId.BriteRings, "RINGS", "rings"),
            ],
            null,
            null
        ),
        // The PREF tray lists the facility's pref sets: ScopeViewModel.TrayChildren builds this tray.
        new(ScopeButtonId.Pref, "PREF", ScopeButtonKind.Menu, [], null, null),
        new(ScopeButtonId.DeleteTearoff, "DEL TEAROFF", ScopeButtonKind.Command, [], null, null),
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

    /// <summary>The command that saves the active pref set again (the PREF tray's SAVE).</summary>
    public const string PrefSaveCommand = ".scope pref save";

    /// <summary>The command that loads the pref set named <paramref name="name"/>.</summary>
    public static string PrefLoadCommand(string name) => $".scope pref load {name}";

    /// <summary>The typed prefix of SAVE AS: the command box takes the new set's name.</summary>
    public const string PrefSaveAsPrefix = ".scope pref saveas ";

    /// <summary>The typed prefix of the rename of the pref set <paramref name="name"/>: the command box takes its new name.</summary>
    public static string PrefRenamePrefix(string name) => $".scope pref rename {name} ";

    /// <summary>The command that deletes the pref set named <paramref name="name"/>.</summary>
    public static string PrefDeleteCommand(string name) => $".scope pref delete {name}";

    /// <summary>The PREF tray's SAVE, which saves the active set again; with none active, its verb says so.</summary>
    public static readonly ScopeButton PrefSave = new(ScopeButtonId.PrefSave, "SAVE", ScopeButtonKind.Command, [], null, PrefSaveCommand);

    /// <summary>The PREF tray's SAVE AS, which types a new set's name into the command box.</summary>
    public static readonly ScopeButton PrefSaveAs = new(ScopeButtonId.PrefSaveAs, "SAVE AS", ScopeButtonKind.Command, [], null, null);

    /// <summary>The PREF tray's RENAME, which types the active set's new name into the command box.</summary>
    public static readonly ScopeButton PrefRename = new(ScopeButtonId.PrefRename, "RENAME", ScopeButtonKind.Command, [], null, null);

    /// <summary>The PREF tray's DELETE, which deletes the active set.</summary>
    public static readonly ScopeButton PrefDelete = new(ScopeButtonId.PrefDelete, "DELETE", ScopeButtonKind.Command, [], null, null);

    /// <summary>The buttons that sit in a tray alone, in no bar slot and in no menu's children, so <see cref="Find"/> reaches them here.</summary>
    private static readonly IReadOnlyList<ScopeButton> TrayOnly = [MapClear, PrefSave, PrefSaveAs, PrefRename, PrefDelete];

    /// <summary>The PREF tray's button for the pref set named <paramref name="name"/>, which loads it.</summary>
    public static ScopeButton PrefSetButton(string name) =>
        new(ScopeButtonId.PrefSet, name, ScopeButtonKind.Command, [], name, PrefLoadCommand(name));

    /// <summary>The command that turns the DB item <paramref name="item"/> on (<paramref name="on"/>) or off.</summary>
    public static string DbToggleCommand(string item, bool on) => $"{DbCommandStem(item)} {(on ? "on" : "off")}";

    /// <summary>The command that sets the datablock point size to <paramref name="size"/>.</summary>
    public static string DbCharCommand(int size) => $"{DbCommandStem(DbCharItem)} {size.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The command that opens (<paramref name="open"/>) or closes the view named <paramref name="viewName"/>.</summary>
    public static string ViewCommand(string viewName, bool open) => $"{ViewCommandStem(viewName)} {(open ? "on" : "off")}";

    /// <summary>The command that floats the view named <paramref name="viewName"/> on the scope at <paramref name="place"/>.</summary>
    public static string ViewTearCommand(string viewName, ScopePlacement place) =>
        string.Create(CultureInfo.InvariantCulture, $"{ViewCommandStem(viewName)} tear {ScopePlacement.NameOf(place.Corner)} {place.Dx} {place.Dy}");

    /// <summary>The command that returns the view named <paramref name="viewName"/> to the dock.</summary>
    public static string ViewDockCommand(string viewName) => $"{ViewCommandStem(viewName)} dock";

    /// <summary>The command that picks the profile <paramref name="kind"/> by hand.</summary>
    public static string ProfileCommand(ScopeProfileKind kind) => $".scope profile {ScopeProfilePicker.NameOf(kind)}";

    /// <summary>Whether the button with <paramref name="id"/> is a PROFILE menu pick.</summary>
    public static bool IsProfilePick(ScopeButtonId id) => id is ScopeButtonId.ProfileStars or ScopeButtonId.ProfileEram or ScopeButtonId.ProfileMixed;

    /// <summary>The command that picks the template (or Mixed) with <paramref name="templateId"/>.</summary>
    public static string TemplateCommand(string templateId) => $".scope template {templateId}";

    /// <summary>The command that clears every pinned band value (AUTO).</summary>
    public const string AutoCommand = ".scope auto";

    /// <summary>The command that pins others' block level to <paramref name="level"/> (<c>full</c> or <c>partial</c>).</summary>
    public static string OthersCommand(string level) => $".scope others {level}";

    /// <summary>The command that pins the vector length to <paramref name="minutes"/> minutes.</summary>
    public static string VectorCommand(double minutes) => $".scope vector {minutes.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The command that draws vectors on the tracks <paramref name="ownership"/> names (<c>own</c> or <c>all</c>).</summary>
    public static string VectorOwnershipCommand(ScopeVectorOwnership ownership) => $".scope vector {ScopeVectorOwnerships.NameOf(ownership)}";

    /// <summary>The command that pins the history dot count to <paramref name="count"/>.</summary>
    public static string HistCommand(int count) => $".scope hist {count.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The command that places, resizes or removes the ring <paramref name="tool"/> arms on the track with
    /// <paramref name="callsign"/>: <c>.scope rings jring 3.0 N123AB</c>, or <c>.scope rings dri5 N123AB</c> for a DRI.
    /// </summary>
    public static string RingsCommand(ScopeRingTool tool, double sizeNm, string callsign) =>
        (tool is ScopeRingTool.JRing or ScopeRingTool.Cone)
            ? $".scope rings {ScopeRingTools.NameOf(tool)} {ScopeRingGeometry.FormatSize(sizeNm)} {callsign}"
            : $".scope rings {ScopeRingTools.NameOf(tool)} {callsign}";

    /// <summary>The ring tool a TOOLS tray button arms, or <see cref="ScopeRingTool.None"/> for any other button.</summary>
    public static ScopeRingTool RingToolOf(ScopeButtonId id) =>
        id switch
        {
            ScopeButtonId.ToolJRing => ScopeRingTool.JRing,
            ScopeButtonId.ToolCone => ScopeRingTool.Cone,
            ScopeButtonId.ToolDri5 => ScopeRingTool.Dri5,
            ScopeButtonId.ToolDri3 => ScopeRingTool.Dri3,
            _ => ScopeRingTool.None,
        };

    /// <summary>The command that sets the scope's range to <paramref name="rangeNm"/> NM.</summary>
    public static string RangeCommand(int rangeNm) => $".scope range {rangeNm}";

    /// <summary>The command that sets the BRITE group named <paramref name="group"/> to <paramref name="value"/>.</summary>
    public static string BriteCommand(string group, int value) =>
        $".scope brite {group.ToLowerInvariant()} {value.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The button with <paramref name="id"/>, on the bar, in a menu or a tray-only button, or null.</summary>
    public static ScopeButton? Find(ScopeButtonId id) =>
        All.FirstOrDefault(b => b.Id == id)
        ?? All.SelectMany(b => b.Children).FirstOrDefault(c => c.Id == id)
        ?? TrayOnly.FirstOrDefault(b => b.Id == id);

    /// <summary>The BRITE group the spinner with <paramref name="id"/> steps, or null when it is no BRITE spinner.</summary>
    public static ScopeBrightnessGroup? BriteGroupOf(ScopeButtonId id) =>
        (Find(id) is { Kind: ScopeButtonKind.Spinner, Value: { } group }) ? ScopeBrightness.FindGroup(group) : null;

    /// <summary>The tear-off key of <paramref name="button"/>: its id, with a map toggle's STARS id as the value.</summary>
    public static ScopeButtonKey KeyOf(ScopeButton button) => new(button.Id, button.Id == ScopeButtonId.MapToggle ? button.Value : null);

    /// <summary>
    /// The text name of <paramref name="key"/>, as <c>.scope tearoff</c> takes it: <c>map</c> and the STARS id for a map
    /// toggle, else the id's name in lowercase.
    /// </summary>
    public static string NameOf(ScopeButtonKey key) => key.Id == ScopeButtonId.MapToggle ? $"map{key.Value}" : key.Id.ToString().ToLowerInvariant();

    /// <summary>
    /// Whether the button with <paramref name="id"/> tears off: every button but DEL TEAROFF and a pref set, which have no
    /// strip on the bar and no text name.
    /// </summary>
    public static bool CanTearOff(ScopeButtonId id) => id is not (ScopeButtonId.DeleteTearoff or ScopeButtonId.PrefSet);

    /// <summary>
    /// Parses a button's text name (<see cref="NameOf"/>), case-insensitive: <c>map</c> and a STARS id, or any id's name
    /// but <see cref="ScopeButtonId.MapToggle"/> and the ids that never tear off (<see cref="CanTearOff"/>).
    /// </summary>
    /// <returns>False when <paramref name="name"/> names no button that tears off.</returns>
    public static bool TryParseKey(string name, [NotNullWhen(true)] out ScopeButtonKey? key)
    {
        key = MapKeyNamed(name) ?? IdKeyNamed(name);
        return key is not null;
    }

    /// <summary>The map toggle key <paramref name="name"/> names (<c>map</c> and a STARS id), or null.</summary>
    private static ScopeButtonKey? MapKeyNamed(string name) =>
        (
            name.StartsWith("map", StringComparison.OrdinalIgnoreCase)
            && (name.Length > 3)
            && int.TryParse(name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int starsId)
        )
            ? new ScopeButtonKey(ScopeButtonId.MapToggle, starsId.ToString(CultureInfo.InvariantCulture))
            : null;

    /// <summary>The key of the id <paramref name="name"/> names, but no map toggle or button that never tears off; else null.</summary>
    private static ScopeButtonKey? IdKeyNamed(string name) =>
        Enum.GetValues<ScopeButtonId>()
            .Where(id => (id != ScopeButtonId.MapToggle) && CanTearOff(id) && string.Equals(id.ToString(), name, StringComparison.OrdinalIgnoreCase))
            .Select(id => new ScopeButtonKey(id, null))
            .FirstOrDefault();

    /// <summary>The command that tears <paramref name="key"/> off, or moves its tear-off, to <paramref name="place"/>.</summary>
    public static string TearoffCommand(ScopeButtonKey key, ScopePlacement place) =>
        string.Create(CultureInfo.InvariantCulture, $".scope tearoff {NameOf(key)} {ScopePlacement.NameOf(place.Corner)} {place.Dx} {place.Dy}");

    /// <summary>The command that deletes the tear-offs of <paramref name="keys"/>, in the order given.</summary>
    public static string TearoffDeleteCommand(IEnumerable<ScopeButtonKey> keys) => $".scope tearoff delete {string.Join(' ', keys.Select(NameOf))}";

    // A ring tool arms locally on a press (ScopeViewModel.Press), so it carries no command; its value is the verb's kind word.
    private static ScopeButton RingTool(ScopeButtonId id, string label, ScopeRingTool tool) =>
        new(id, label, ScopeButtonKind.Toggle, [], ScopeRingTools.NameOf(tool), null);

    private static ScopeButton BriteSpinner(ScopeButtonId id, string label, string group) => new(id, label, ScopeButtonKind.Spinner, [], group, null);

    private static ScopeButton ProfilePick(ScopeButtonId id, string label, ScopeProfileKind kind) =>
        new(id, label, ScopeButtonKind.Command, [], ScopeProfilePicker.NameOf(kind), ProfileCommand(kind));

    private static ScopeButton TemplatePick(ScopeButtonId id, string label, string templateId) =>
        new(id, label, ScopeButtonKind.Command, [], templateId, TemplateCommand(templateId));

    private static ScopeButton ViewToggle(ScopeButtonId id, string label, string viewName) =>
        new(id, label, ScopeButtonKind.Toggle, [], viewName, ViewCommandStem(viewName));

    private static string ViewCommandStem(string viewName) => $".view {viewName}";

    private static ScopeButton DbToggle(ScopeButtonId id, string label, string item) =>
        new(id, label, ScopeButtonKind.Toggle, [], item, DbCommandStem(item));

    private static string DbCommandStem(string item) => $".scope db {item}";
}
