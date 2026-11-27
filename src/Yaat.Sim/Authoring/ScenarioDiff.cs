using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring;

/// <summary>How a field differs from the left scenario to the right one.</summary>
public enum DiffKind
{
    /// <summary>Both sides hold the field, with different values.</summary>
    Changed,

    /// <summary>Only the right side holds the field or list item.</summary>
    Added,

    /// <summary>Only the left side holds the field or list item.</summary>
    Removed,
}

/// <summary>One field or list item that differs between two scenarios.</summary>
/// <param name="Path">
/// The JSON Pointer of the field. A list matched by id numbers its items left first, then the items only the right holds.
/// </param>
/// <param name="Section">The editor tab the field sits on (<c>General</c>, <c>Aircraft</c>) or its General-tab list section.</param>
/// <param name="Label">The editor's label for the field; a field the editor does not show is labelled by its JSON name.</param>
/// <param name="ItemKey">The top-level list item's id, or <c>#&lt;index&gt;</c> when it has none; null outside a list.</param>
/// <param name="Kind">How it differs.</param>
public sealed record ScenarioDiffEntry(string Path, string Section, string Label, string? ItemKey, DiffKind Kind);

/// <summary>
/// Compares two scenario JSON texts field by field on the raw JSON, so fields the model does not know are compared too. List
/// items match by id where every item on both sides carries one, else by position; a list of plain values is one field.
/// </summary>
public static class ScenarioDiff
{
    private static readonly ILogger Log = SimLog.CreateLogger("ScenarioDiff");

    /// <summary>Lists every field and list item that differs from <paramref name="leftJson"/> to <paramref name="rightJson"/>.</summary>
    /// <param name="leftJson">The older scenario JSON.</param>
    /// <param name="rightJson">The newer scenario JSON.</param>
    /// <returns>The differences in document order, the left side's order first.</returns>
    /// <exception cref="System.Text.Json.JsonException">Either text is malformed or not a JSON object.</exception>
    public static IReadOnlyList<ScenarioDiffEntry> Compare(string leftJson, string rightJson)
    {
        ArgumentNullException.ThrowIfNull(leftJson);
        ArgumentNullException.ThrowIfNull(rightJson);
        var tree = new MergeTree(null, MergeTree.ParseRoot(leftJson), MergeTree.ParseRoot(rightJson));
        List<ScenarioDiffEntry> entries = [.. tree.Rows.Select(Entry)];
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Scenario diff: {Entries} differences", entries.Count);
        }

        return entries;
    }

    private static ScenarioDiffEntry Entry(PlannedRow planned)
    {
        DiffKind kind =
            !planned.Mine.Present ? DiffKind.Added
            : !planned.Theirs.Present ? DiffKind.Removed
            : DiffKind.Changed;
        MergeRow row = planned.Row;
        return new ScenarioDiffEntry(row.Path, row.Section, row.Label, row.ItemKey, kind);
    }
}

/// <summary>The editor's tab or section for each top-level scenario key, and its label for each field.</summary>
internal static class ScenarioFieldLabels
{
    public const string General = "General";

    private static readonly Dictionary<string, (string Section, string Noun)> Lists = new(StringComparer.Ordinal)
    {
        ["aircraft"] = ("Aircraft", "Aircraft"),
        ["atc"] = ("ATC positions", "ATC position"),
        ["initializationTriggers"] = ("Initialization triggers", "Initialization trigger"),
        ["aircraftGenerators"] = ("Generators", "Arrival generator"),
        ["vfrArrivalGenerators"] = ("VFR arrival generators", "VFR arrival generator"),
        ["overflightGenerators"] = ("Overflight generators", "Overflight generator"),
        ["flightStripConfigurations"] = ("Strip bays", "Strip bay"),
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Fields = new(StringComparer.Ordinal)
    {
        [General] = GeneralFields(),
        ["Aircraft"] = AircraftFields(),
        ["ATC positions"] = Map(
            ("artccId", "Position"),
            ("facilityId", "Position"),
            ("positionId", "Position"),
            ("autoConnect", "Auto-connect"),
            ("autoTrackAirportIds", "Auto-track airports")
        ),
        ["Initialization triggers"] = Map(("timeOffset", "Time"), ("command", "Command")),
        ["Generators"] = GeneratorFields(("intervalDistance", "Spacing nm"), ("randomizeWeightCategory", "Randomize weight")),
        ["VFR arrival generators"] = GeneratorFields(
            ("bearingFrom", "VFR arrival bearings"),
            ("bearingTo", "VFR arrival bearings"),
            ("directTo", "Direct to"),
            ("initialVsFpm", "Initial VS fpm"),
            ("descendToAltitude", "Descend to")
        ),
        ["Overflight generators"] = GeneratorFields(
            ("fromBearingFrom", "Entry bearings"),
            ("fromBearingTo", "Entry bearings"),
            ("toBearingFrom", "Exit bearings"),
            ("toBearingTo", "Exit bearings"),
            ("snapHemisphericAltitude", "Snap to hemispheric altitude"),
            ("exitDistance", "Exit distance nm")
        ),
        ["Strip bays"] = Map(("facilityId", "Bay"), ("bayId", "Bay"), ("rack", "Rack"), ("aircraftIds", "Aircraft")),
    };

    /// <summary>The tab or General-tab section a top-level key's value sits on.</summary>
    public static string SectionOf(string rootKey) => Lists.TryGetValue(rootKey, out (string Section, string Noun) list) ? list.Section : General;

    /// <summary>
    /// The label of a field: <paramref name="labelPath"/> is its member names below the section's list item (below the root on
    /// the General tab), each list level marked <c>[]</c>; empty is the section's list itself, <c>[]</c> one of its items.
    /// </summary>
    public static string Label(string section, string labelPath)
    {
        if (labelPath.Length == 0)
        {
            return section;
        }

        if (labelPath == "[]")
        {
            return Lists.Values.First(list => list.Section == section).Noun;
        }

        if (Fields.TryGetValue(section, out Dictionary<string, string>? fields) && fields.TryGetValue(labelPath, out string? label))
        {
            return label;
        }

        string last = labelPath[(labelPath.LastIndexOf('/') + 1)..];
        return last.EndsWith("[]", StringComparison.Ordinal) ? last[..^2] : last;
    }

    private static Dictionary<string, string> Map(params (string Path, string Label)[] entries) =>
        entries.ToDictionary(e => e.Path, e => e.Label, StringComparer.Ordinal);

    private static Dictionary<string, string> GeneralFields() =>
        Map(
            ("id", "Id"),
            ("name", "Name"),
            ("artccId", "ARTCC"),
            ("minimumRating", "Minimum rating"),
            ("studentPositionId", "Student position"),
            ("autoDeleteMode", "Auto-delete"),
            ("primaryAirportId", "Primary airport"),
            ("primaryApproach", "Primary approach")
        );

    private static Dictionary<string, string> AircraftFields() =>
        Map(
            ("aircraftId", "Callsign"),
            ("aircraftType", "Type"),
            ("transponderMode", "Transponder"),
            ("spawnDelay", "Spawn delay"),
            (WorkingCopyExporter.AircraftTaxiSecondsKey, "Taxi time (seconds)"),
            ("airportId", "Airport"),
            ("expectedApproach", "Approach"),
            ("difficulty", "Difficulty"),
            ("onAltitudeProfile", "On altitude profile"),
            (WorkingCopyExporter.AircraftRpoNoteKey, "RPO note"),
            ("startingConditions", "Starting conditions"),
            ("startingConditions/type", "Kind"),
            ("startingConditions/parking", "Parking"),
            ("startingConditions/runway", "Runway"),
            ("startingConditions/fix", "Fix or FRD"),
            ("startingConditions/coordinates", "Coordinates"),
            ("startingConditions/coordinates/lat", "Latitude"),
            ("startingConditions/coordinates/lon", "Longitude"),
            ("startingConditions/altitude", "Altitude (ft)"),
            ("startingConditions/speed", "Speed (kt)"),
            ("startingConditions/heading", "Heading (deg)"),
            ("startingConditions/navigationPath", "Navigation path"),
            ("startingConditions/distanceFromRunway", "Distance from runway (nm)"),
            ("flightplan", "Flight plan"),
            ("flightplan/rules", "Rules"),
            ("flightplan/departure", "Departure"),
            ("flightplan/destination", "Destination"),
            ("flightplan/cruiseAltitude", "Cruise altitude (ft)"),
            ("flightplan/cruiseSpeed", "Cruise speed (kt)"),
            ("flightplan/route", "Route"),
            ("flightplan/remarks", "Remarks"),
            ("flightplan/aircraftType", "Filed type"),
            ("presetCommands", "Preset commands"),
            ("presetCommands[]", "Preset command"),
            ("presetCommands[]/command", "Preset command"),
            ("presetCommands[]/timeOffset", "Preset time"),
            ("autoTrackConditions", "Auto-track"),
            ("autoTrackConditions/positionId", "Position"),
            ("autoTrackConditions/handoffDelay", "Handoff delay (seconds)"),
            ("autoTrackConditions/scratchPad", "Scratchpad"),
            ("autoTrackConditions/interimAltitude", "Interim altitude"),
            ("autoTrackConditions/clearedAltitude", "Cleared altitude")
        );

    // The columns every generator row shares, then the kind's own fields.
    private static Dictionary<string, string> GeneratorFields(params (string Path, string Label)[] own)
    {
        (string, string)[] shared =
        [
            ("runway", "Runway"),
            ("engineType", "Engine"),
            ("weightCategory", "Weight"),
            ("initialDistance", "Distance nm"),
            ("maxDistance", "Distance nm"),
            ("altitudeMin", "Altitude"),
            ("altitudeMax", "Altitude"),
            ("startTimeOffset", "Start"),
            ("intervalTime", "Every"),
            ("randomizeInterval", "Rnd"),
            ("maxTime", "Stop"),
            ("enabled", "Enabled"),
            ("autoTrackConfiguration", "Auto-track"),
            ("autoTrackConfiguration/positionId", "Auto-track"),
            ("autoTrackConfiguration/handoffDelay", "Auto-track"),
            ("autoTrackConfiguration/scratchPad", "Auto-track"),
            ("autoTrackConfiguration/interimAltitude", "Auto-track"),
            ("autoTrackConfiguration/clearedAltitude", "Auto-track"),
        ];
        return Map([.. shared, .. own]);
    }
}
