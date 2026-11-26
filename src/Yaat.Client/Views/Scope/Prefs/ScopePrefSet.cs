using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Yaat.Client.Views.Scope.Prefs;

/// <summary>
/// One YAAT Scope display state as the pref-set files store it: range and centre, template, the BRITE groups, the DB
/// settings, the maps that are on, the open views with their places, the classic bar's button names
/// (empty when the Scope showed the STARS or ERAM bar) and the views array, the pinned band values, the vector lines' OWN
/// or ALL, top-down mode, whether the bar is hidden and the pilot lens's switches. Every field holds text names rather
/// than types from the app, so a rename in code leaves the file readable.
/// </summary>
public sealed record ScopePrefSet : IJsonOnDeserialized
{
    /// <summary>The key of the button tear-offs older files hold; dropped on load, so a resave does not write it.</summary>
    private const string RetiredTearoffsKey = "tearoffs";

    /// <summary>
    /// The serializer options every pref-set file is written and read with: camelCase names, indented, enums as text, an
    /// explicit null in a non-nullable field rejected so that entry is a bad entry, and a positional record entry missing
    /// one of its constructor fields rejected the same way.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    /// <summary>The set's uppercase name, or null for the Scope's saved current state.</summary>
    public string? Name { get; init; }

    /// <summary>The Scope's range in nautical miles.</summary>
    public double RangeNm { get; init; }

    /// <summary>The centre latitude in degrees.</summary>
    public double CenterLat { get; init; }

    /// <summary>The centre longitude in degrees.</summary>
    public double CenterLon { get; init; }

    /// <summary>The template id the Scope draws with.</summary>
    public required string TemplateId { get; init; }

    /// <summary>
    /// The BRITE groups, keyed by group name: <c>mapa</c>, <c>mapb</c>, <c>targets</c>, <c>fdb</c>, <c>line4</c>,
    /// <c>alerts</c>, <c>dwell</c>, <c>hist</c>, <c>vector</c>, <c>rings</c> and <c>lens</c>.
    /// </summary>
    public IReadOnlyDictionary<string, int> Brightness { get; init; } = new Dictionary<string, int>();

    /// <summary>The DB toggles and the datablock character size.</summary>
    public required ScopePrefDb Db { get; init; }

    /// <summary>The STARS ids of the maps that are on.</summary>
    public IReadOnlyList<int> Maps { get; init; } = [];

    /// <summary>The open view names, in dock order.</summary>
    public IReadOnlyList<string> OpenViews { get; init; } = [];

    /// <summary>Every view's place, docked or floating.</summary>
    public IReadOnlyList<ScopePrefView> ViewPlaces { get; init; } = [];

    /// <summary>
    /// The top-left of the RUNDOWN list's title line in canvas points, or null while the list sits at its default place;
    /// a null origin is left out of the file, and a file without one restores the default.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScopePrefPoint? RundownOrigin { get; init; }

    /// <summary>The classic bar's button names; empty when the Scope showed the STARS or ERAM bar.</summary>
    public IReadOnlyList<string> Toolbar { get; init; } = [];

    /// <summary>The profile's views array: the view names.</summary>
    public IReadOnlyList<string> Views { get; init; } = [];

    /// <summary>
    /// The name of the profile the Scope showed (<c>stars</c>, <c>eram</c> or <c>mixed</c>). <c>.scope pref load</c> switches
    /// to it; the saved-state restore leaves the profile alone.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>The pinned block level of others' tracks (<c>full</c> or <c>partial</c>), or null when it follows the band.</summary>
    public string? PinOthersLevel { get; init; }

    /// <summary>The pinned vector length in minutes, or null when it follows the band.</summary>
    public double? PinVectorMin { get; init; }

    /// <summary>The pinned history dot count, or null when it follows the band.</summary>
    public int? PinHistory { get; init; }

    /// <summary>The pinned ring kind (<c>jring</c> or <c>dri</c>), or null when it follows the band.</summary>
    public string? PinRings { get; init; }

    /// <summary>
    /// The tracks vector lines draw on: <c>own</c> (the default) or <c>all</c>. A setting, not a pin: it is neither banded
    /// nor cleared by AUTO.
    /// </summary>
    public string VectorOwnership { get; init; } = ScopeVectorOwnerships.OwnName;

    /// <summary>Whether top-down mode shows ground targets.</summary>
    public bool Tdm { get; init; }

    /// <summary>Whether the Scope's bar is hidden to its SHOW tile; a file without it loads with the bar shown.</summary>
    public bool BarHidden { get; init; }

    /// <summary>The pilot lens's switches. The tracks whose intent path is toggled on are never saved.</summary>
    public required ScopePrefLens Lens { get; init; }

    /// <summary>The map tools' settings.</summary>
    public required ScopePrefMapTools MapTools { get; init; }

    /// <summary>Fields this build does not know, kept so a resave does not lose them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <inheritdoc/>
    void IJsonOnDeserialized.OnDeserialized() => Extra?.Remove(RetiredTearoffsKey);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Converters = { new JsonStringEnumConverter() },
        };
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>The Scope's DB toggles and datablock character size.</summary>
/// <param name="Line4">Whether the datablock draws its fourth line.</param>
/// <param name="HoverDwell">Whether hovering a target dwells it.</param>
/// <param name="Atpa">Whether blocks show the ATPA in-trail distance (ATPA DIST).</param>
/// <param name="AtpaAlertCones">Whether an ATPA pair in the Warning or Alert state draws its cone (ALERT CONES).</param>
/// <param name="AtpaMonitorCones">Whether an ATPA pair in the Monitor state draws its cone (MONITOR CONES).</param>
/// <param name="EramCaRing">Whether an ERAM conflict pair draws its ring (CA RING).</param>
/// <param name="CharSize">The datablock character size.</param>
public sealed record ScopePrefDb(bool Line4, bool HoverDwell, bool Atpa, bool AtpaAlertCones, bool AtpaMonitorCones, bool EramCaRing, int CharSize);

/// <summary>The pilot lens's switches.</summary>
/// <param name="Enabled">Whether the lens is on.</param>
/// <param name="Path">Whether its intent path overlay is on.</param>
/// <param name="Queue">Whether its queued-commands badge overlay is on.</param>
/// <param name="CheckIn">Whether its check-in tag overlay is on.</param>
public sealed record ScopePrefLens(bool Enabled, bool Path, bool Queue, bool CheckIn);

/// <summary>One view's place: docked, or floating at a corner with a pixel offset.</summary>
/// <param name="Name">The view name, such as <c>ssa</c> or <c>ca</c>.</param>
/// <param name="Corner">Null when the view is docked, else <c>tl</c>, <c>tr</c>, <c>bl</c> or <c>br</c>.</param>
/// <param name="Dx">The horizontal offset in pixels.</param>
/// <param name="Dy">The vertical offset in pixels.</param>
public sealed record ScopePrefView(string Name, string? Corner, int Dx, int Dy);

/// <summary>A point on the Scope's canvas, in canvas points.</summary>
/// <param name="X">The distance from the canvas's left edge.</param>
/// <param name="Y">The distance from the canvas's top edge.</param>
public sealed record ScopePrefPoint(double X, double Y);
