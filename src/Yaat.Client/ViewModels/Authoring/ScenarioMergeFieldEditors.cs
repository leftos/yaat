using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>The control a MERGED VALUE editor shows for one field.</summary>
public enum ScenarioMergeEditorKind
{
    /// <summary>A text box.</summary>
    Text,

    /// <summary>A time box; blank is 0.</summary>
    Time,

    /// <summary>A time box; blank is none.</summary>
    OptionalTime,

    /// <summary>A number box: a number, a whole number, a latitude, a longitude or a heading.</summary>
    Number,

    /// <summary>A combo of the field's options, or a text box when its options cannot load.</summary>
    Choice,

    /// <summary>A check box.</summary>
    Bool,

    /// <summary>A three-state check box whose third state is none (JSON null).</summary>
    OptionalBool,

    /// <summary>A text box of ids split at commas and spaces.</summary>
    CommaList,

    /// <summary>A command box.</summary>
    Command,

    /// <summary>A monospace box of the raw JSON, for a field no other kind covers.</summary>
    Json,
}

/// <summary>Where a choice editor's options come from.</summary>
public enum ScenarioMergeChoiceSource
{
    /// <summary>A fixed list.</summary>
    Fixed,

    /// <summary>The ARTCC's and its neighbours' positions.</summary>
    Positions,

    /// <summary>None, then the ARTCC's and its neighbours' positions.</summary>
    OptionalPositions,

    /// <summary>The airports the ARTCC config lists.</summary>
    Airports,
}

/// <summary>What an editor's text reads as.</summary>
/// <param name="Value">The typed JSON the text gives; null for JSON null or when the text is refused.</param>
/// <param name="Error">The editor's error line; null for none.</param>
/// <param name="BlocksKeep">Whether the text is refused, so Keep this is disabled.</param>
public sealed record ScenarioMergeParsed(JsonNode? Value, string? Error, bool BlocksKeep)
{
    /// <summary>A text that reads as a value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The reading.</returns>
    public static ScenarioMergeParsed Ok(JsonNode? value) => new(value, null, false);

    /// <summary>A text that is not a value of the field.</summary>
    /// <param name="error">The error line.</param>
    /// <returns>The reading.</returns>
    public static ScenarioMergeParsed Refused(string error) => new(null, error, true);
}

/// <summary>The ARTCC config's pickers the merge window loads once: null lists when the config could not be read.</summary>
/// <param name="Positions">The ARTCC's and its neighbours' positions; null for free text.</param>
/// <param name="Airports">The airports the config lists; null for free text.</param>
public sealed record ScenarioMergePickers(IReadOnlyList<ScenarioPickOption>? Positions, IReadOnlyList<string>? Airports)
{
    /// <summary>Gets the pickers before a load, or after one that failed: every position and airport is free text.</summary>
    public static ScenarioMergePickers FreeText { get; } = new(null, null);

    internal static ScenarioMergePickers Of(ScenarioGeneralViewModel.PickerData data) =>
        new(data.Positions?.Select(ScenarioGeneralViewModel.PositionOption).ToList(), data.Airports);
}

/// <summary>What a preset command resolves its fixes against: the aircraft's flight plan, as ScenarioValidator reads it.</summary>
/// <param name="Route">The filed route.</param>
/// <param name="Departure">The departure airport.</param>
/// <param name="Destination">The destination airport.</param>
public sealed record ScenarioMergeCommandContext(string? Route, string? Departure, string? Destination)
{
    /// <summary>The context of an aircraft's flight plan JSON; null when it has none.</summary>
    /// <param name="flightPlan">The aircraft's flightplan member.</param>
    /// <returns>The context, or null.</returns>
    public static ScenarioMergeCommandContext? Of(JsonObject? flightPlan) =>
        (flightPlan is null) ? null : new(Member(flightPlan, "route"), Member(flightPlan, "departure"), Member(flightPlan, "destination"));

    private static string? Member(JsonObject node, string key) =>
        ((node[key] is JsonValue value) && value.TryGetValue(out string? text)) ? text : null;
}

/// <summary>One field's MERGED VALUE editor: its control, how a document value reads as text, and how its text reads back.</summary>
public sealed class ScenarioMergeFieldEditor
{
    private static readonly ScenarioPickOption NoPosition = new("", "None");

    private readonly Func<JsonNode?, string> _format;
    private readonly Func<string, ScenarioMergeCommandContext?, ScenarioMergeParsed> _parse;

    internal ScenarioMergeFieldEditor(ScenarioMergeEditorKind kind, Func<JsonNode?, string> format, Func<string, ScenarioMergeParsed> parse)
        : this(kind, format, (text, _) => parse(text)) { }

    internal ScenarioMergeFieldEditor(
        ScenarioMergeEditorKind kind,
        Func<JsonNode?, string> format,
        Func<string, ScenarioMergeCommandContext?, ScenarioMergeParsed> parse
    )
    {
        Kind = kind;
        _format = format;
        _parse = parse;
    }

    /// <summary>Gets the control the editor shows.</summary>
    public ScenarioMergeEditorKind Kind { get; }

    /// <summary>Gets where a choice editor's options come from.</summary>
    public ScenarioMergeChoiceSource Choices { get; init; }

    /// <summary>Gets a fixed choice editor's options.</summary>
    public IReadOnlyList<ScenarioPickOption> FixedOptions { get; init; } = [];

    /// <summary>The text a document value reads as in the editor.</summary>
    /// <param name="value">The value; null for JSON null or an absent value.</param>
    /// <returns>The text.</returns>
    public string Format(JsonNode? value) => _format(value);

    /// <summary>The typed JSON a text gives, or the error it shows.</summary>
    /// <param name="text">The editor's text.</param>
    /// <returns>The reading.</returns>
    public ScenarioMergeParsed Parse(string text) => _parse(text, null);

    /// <summary>The typed JSON a text gives, or the error it shows, a preset command resolving against its aircraft.</summary>
    /// <param name="text">The editor's text.</param>
    /// <param name="context">The aircraft's flight plan; null for none.</param>
    /// <returns>The reading.</returns>
    public ScenarioMergeParsed Parse(string text, ScenarioMergeCommandContext? context) => _parse(text, context);

    /// <summary>The editor for one value: a text, choice or command field holding something other than a string gets the JSON box.</summary>
    /// <param name="value">The value the editor opens with.</param>
    /// <returns>This editor, or the JSON box.</returns>
    public ScenarioMergeFieldEditor ForValue(JsonNode? value)
    {
        bool takesText = Kind is ScenarioMergeEditorKind.Text or ScenarioMergeEditorKind.Choice or ScenarioMergeEditorKind.Command;
        return (takesText && (value is not null) && (value.GetValueKind() != JsonValueKind.String)) ? ScenarioMergeFieldEditors.Json : this;
    }

    /// <summary>A choice editor's options, the current value offered when none lists it; null for free text or another kind.</summary>
    /// <param name="current">The editor's text.</param>
    /// <param name="pickers">The loaded pickers.</param>
    /// <returns>The options, or null.</returns>
    public IReadOnlyList<ScenarioPickOption>? OptionsFor(string current, ScenarioMergePickers pickers)
    {
        ArgumentNullException.ThrowIfNull(pickers);
        if (Kind != ScenarioMergeEditorKind.Choice)
        {
            return null;
        }

        string unlisted = $"{current} (not in the ARTCC config)";
        return Choices switch
        {
            ScenarioMergeChoiceSource.Positions when pickers.Positions is { } positions => ScenarioGeneralViewModel.WithCurrent(
                positions,
                current,
                unlisted
            ),
            ScenarioMergeChoiceSource.OptionalPositions when pickers.Positions is { } positions =>
            [
                NoPosition,
                .. ScenarioGeneralViewModel.WithCurrent(positions, current, unlisted),
            ],
            ScenarioMergeChoiceSource.Airports when pickers.Airports is { } airports => ScenarioGeneralViewModel.WithCurrent(
                [.. airports.Select(a => new ScenarioPickOption(a, a))],
                current,
                current
            ),
            ScenarioMergeChoiceSource.Fixed => ScenarioGeneralViewModel.WithCurrent(FixedOptions, current, current),
            _ => null,
        };
    }
}

/// <summary>
/// The merge window's field-to-editor table: each (section, label path) the editor shows maps to that field's own control,
/// typed to match the document (times and numbers as numbers, text quoted); any other field, a whole list item among them,
/// gets the JSON box.
/// </summary>
public static class ScenarioMergeFieldEditors
{
    private const string General = "General";
    private const string Aircraft = "Aircraft";
    private const string Atc = "ATC positions";
    private const string Triggers = "Initialization triggers";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly ScenarioMergeFieldEditor Time = new(
        ScenarioMergeEditorKind.Time,
        Format(ScenarioFieldText.Time, ReadWhole),
        ParseWith(ScenarioFieldText.Time, Node)
    );

    private static readonly ScenarioMergeFieldEditor OptionalTime = new(
        ScenarioMergeEditorKind.OptionalTime,
        Format(ScenarioFieldText.OptionalTime, ReadOptionalWhole),
        ParseWith(ScenarioFieldText.OptionalTime, Node)
    );

    private static readonly ScenarioMergeFieldEditor WholeNumber = Parsed(ScenarioFieldText.WholeNumber, ReadWhole, Node);

    private static readonly ScenarioMergeFieldEditor Number = Parsed(ScenarioFieldText.Number, ReadNumber, Node);

    private static readonly ScenarioMergeFieldEditor OptionalNumber = Parsed(ScenarioFieldText.OptionalNumber, ReadOptionalNumber, Node);

    private static readonly ScenarioMergeFieldEditor Bool = BoolBox(false);

    private static readonly ScenarioMergeFieldEditor OptionalBool = new(ScenarioMergeEditorKind.OptionalBool, FormatOptionalBool, ParseOptionalBool);

    private static readonly ScenarioMergeFieldEditor JsonBox = new(ScenarioMergeEditorKind.Json, FormatJson, ParseJson);

    // Built after the editors above, which the table holds.
    private static readonly Dictionary<(string Section, string FieldKey), ScenarioMergeFieldEditor> Table = Build();

    /// <summary>Gets the JSON box: the raw JSON, refused with "Not valid JSON: {message}" when it does not parse.</summary>
    public static ScenarioMergeFieldEditor Json => JsonBox;

    internal static IReadOnlyCollection<(string Section, string FieldKey)> MappedFields => Table.Keys;

    /// <summary>The editor for a row's field.</summary>
    /// <param name="section">The row's section.</param>
    /// <param name="fieldKey">The row's label path.</param>
    /// <returns>The field's editor, or the JSON box for a field the table does not map.</returns>
    public static ScenarioMergeFieldEditor For(string section, string fieldKey) => Table.GetValueOrDefault((section, fieldKey)) ?? Json;

    private static Dictionary<(string, string), ScenarioMergeFieldEditor> Build()
    {
        var table = new Dictionary<(string, string), ScenarioMergeFieldEditor>();
        void Add(string section, IEnumerable<(string Key, ScenarioMergeFieldEditor Editor)> entries)
        {
            foreach ((string key, ScenarioMergeFieldEditor editor) in entries)
            {
                table[(section, key)] = editor;
            }
        }

        Add(General, GeneralEntries());
        Add(Aircraft, AircraftEntries());
        Add(Aircraft, AutoTrackEntries("autoTrackConditions"));
        Add(Atc, AtcEntries());
        Add(Triggers, [("timeOffset", Time), ("command", Command(false))]);
        Add("Generators", [.. GeneratorEntries(), ("intervalDistance", Number), ("randomizeWeightCategory", Bool)]);
        Add("VFR arrival generators", [.. GeneratorEntries(), .. VfrEntries()]);
        Add("Overflight generators", [.. GeneratorEntries(), .. OverflightEntries()]);
        Add("Strip bays", [("facilityId", Text(true)), ("bayId", Text(true)), ("rack", WholeNumber)]);
        return table;
    }

    private static (string, ScenarioMergeFieldEditor)[] GeneralEntries() =>
        [
            ("id", Text(false)),
            ("name", new(ScenarioMergeEditorKind.Text, FormatText, ParseName)),
            ("artccId", Text(false)),
            ("minimumRating", Fixed(true, [new("", "None"), .. Options(ScenarioRatingClassifier.LongFormRatings)])),
            ("studentPositionId", Choice(ScenarioMergeChoiceSource.OptionalPositions, true)),
            ("autoDeleteMode", Fixed(true, [.. AutoDeleteModes.Ordered.Select(m => new ScenarioPickOption(m ?? "", m ?? "Default"))])),
            ("primaryAirportId", Choice(ScenarioMergeChoiceSource.Airports, true)),
            ("primaryApproach", Text(true)),
            ("liveSession", Bool),
        ];

    private static (string, ScenarioMergeFieldEditor)[] AircraftEntries() =>
        [
            ("aircraftId", Text(false)),
            ("aircraftType", Text(false)),
            ("transponderMode", Text(false)),
            ("spawnDelay", Time),
            ("airportId", Text(true)),
            ("expectedApproach", Text(true)),
            ("difficulty", Text(true)),
            ("onAltitudeProfile", Bool),
            ("startingConditions/type", Fixed(false, StartKinds)),
            ("startingConditions/parking", Text(true)),
            ("startingConditions/runway", Text(true)),
            ("startingConditions/fix", Text(true)),
            ("startingConditions/navigationPath", Text(true)),
            ("startingConditions/coordinates/lat", Parsed(ScenarioFieldText.Latitude, ReadNumber, Node)),
            ("startingConditions/coordinates/lon", Parsed(ScenarioFieldText.Longitude, ReadNumber, Node)),
            ("startingConditions/altitude", OptionalNumber),
            ("startingConditions/speed", OptionalNumber),
            ("startingConditions/heading", Parsed(ScenarioFieldText.OptionalHeading, ReadOptionalNumber, Node)),
            ("startingConditions/distanceFromRunway", OptionalNumber),
            ("flightplan/rules", Fixed(true, Options(ScenarioAircraftViewModel.FlightRulesChoices))),
            ("flightplan/departure", Text(false)),
            ("flightplan/destination", Text(false)),
            ("flightplan/cruiseAltitude", WholeNumber),
            ("flightplan/cruiseSpeed", WholeNumber),
            ("flightplan/route", Text(false)),
            ("flightplan/remarks", Text(false)),
            ("flightplan/aircraftType", Text(false)),
            ("presetCommands[]/command", Command(true)),
            ("presetCommands[]/timeOffset", Time),
        ];

    private static IReadOnlyList<ScenarioPickOption> StartKinds =>
        [.. StartingConditionKinds.Kinds.Select(k => new ScenarioPickOption(k, ScenarioAircraftViewModel.KindLabel(k)))];

    private static (string, ScenarioMergeFieldEditor)[] AutoTrackEntries(string member) =>
        [
            (member + "/positionId", Choice(ScenarioMergeChoiceSource.OptionalPositions, false)),
            (member + "/handoffDelay", Parsed(ScenarioFieldText.OptionalWholeNumber, ReadOptionalWhole, Node)),
            (member + "/scratchPad", Text(true)),
            (member + "/interimAltitude", Text(true)),
            (member + "/clearedAltitude", Text(true)),
        ];

    private static (string, ScenarioMergeFieldEditor)[] AtcEntries() =>
        [
            ("artccId", Text(false)),
            ("facilityId", Text(false)),
            ("positionId", Choice(ScenarioMergeChoiceSource.Positions, false)),
            ("autoConnect", Bool),
            ("autoTrackAirportIds", new(ScenarioMergeEditorKind.CommaList, FormatIds, ParseIds)),
        ];

    private static (string, ScenarioMergeFieldEditor)[] GeneratorEntries() =>
        [
            ("runway", Text(false)),
            ("engineType", Fixed(false, Options(ScenarioFieldText.EngineTypes))),
            ("weightCategory", Fixed(false, Options(ScenarioFieldText.WeightCategories))),
            ("initialDistance", Number),
            ("maxDistance", Number),
            ("altitudeMin", Number),
            ("altitudeMax", Number),
            ("startTimeOffset", Time),
            ("intervalTime", Time),
            ("maxTime", OptionalTime),
            ("randomizeInterval", Bool),
            ("enabled", OptionalBool),
            .. AutoTrackEntries("autoTrackConfiguration"),
        ];

    private static (string, ScenarioMergeFieldEditor)[] VfrEntries() =>
        [
            ("bearingFrom", Number),
            ("bearingTo", Number),
            ("directTo", Text(false)),
            ("initialVsFpm", WholeNumber),
            ("descendToAltitude", OptionalNumber),
        ];

    private static (string, ScenarioMergeFieldEditor)[] OverflightEntries() =>
        [
            ("fromBearingFrom", Number),
            ("fromBearingTo", Number),
            ("toBearingFrom", Number),
            ("toBearingTo", Number),
            ("snapHemisphericAltitude", BoolBox(true)),
            ("exitDistance", OptionalNumber),
        ];

    private static ScenarioPickOption[] Options(IEnumerable<string> values) => [.. values.Select(v => new ScenarioPickOption(v, v))];

    private static ScenarioMergeFieldEditor Text(bool optional) => new(ScenarioMergeEditorKind.Text, FormatText, t => ParseText(t, optional));

    private static ScenarioMergeFieldEditor Choice(ScenarioMergeChoiceSource source, bool optional) =>
        new(ScenarioMergeEditorKind.Choice, FormatText, t => ParseText(t, optional)) { Choices = source };

    private static ScenarioMergeFieldEditor Fixed(bool optional, IReadOnlyList<ScenarioPickOption> options) =>
        new(ScenarioMergeEditorKind.Choice, FormatText, t => ParseText(t, optional))
        {
            Choices = ScenarioMergeChoiceSource.Fixed,
            FixedOptions = options,
        };

    private static ScenarioMergeFieldEditor Command(bool checkParse) =>
        new(ScenarioMergeEditorKind.Command, FormatText, (t, c) => ParseCommand(t, checkParse, c));

    private static ScenarioMergeFieldEditor Parsed<T>(ScenarioFieldKind<T> kind, ReadNode<T> read, Func<T, JsonNode?> write) =>
        new(ScenarioMergeEditorKind.Number, Format(kind, read), ParseWith(kind, write));

    private delegate bool ReadNode<T>(JsonNode node, out T value);

    private static Func<JsonNode?, string> Format<T>(ScenarioFieldKind<T> kind, ReadNode<T> read) =>
        value =>
            value switch
            {
                null => "",
                _ when read(value, out T typed) => kind.Format(typed),
                _ => value.ToJsonString(),
            };

    private static Func<string, ScenarioMergeParsed> ParseWith<T>(ScenarioFieldKind<T> kind, Func<T, JsonNode?> write) =>
        text => kind.TryParse(text, out T value) ? ScenarioMergeParsed.Ok(write(value)) : ScenarioMergeParsed.Refused(kind.ErrorText);

    private static bool ReadNumber(JsonNode node, out double value)
    {
        value = 0;
        return (node.GetValueKind() == JsonValueKind.Number)
            && double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool ReadOptionalNumber(JsonNode node, out double? value)
    {
        bool read = ReadNumber(node, out double number);
        value = read ? number : null;
        return read;
    }

    private static bool ReadWhole(JsonNode node, out int value)
    {
        value = 0;
        return (node.GetValueKind() == JsonValueKind.Number)
            && int.TryParse(node.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool ReadOptionalWhole(JsonNode node, out int? value)
    {
        bool read = ReadWhole(node, out int number);
        value = read ? number : null;
        return read;
    }

    private static string FormatText(JsonNode? value) =>
        value switch
        {
            null => "",
            JsonValue text when text.TryGetValue(out string? words) => words,
            _ => value.ToJsonString(),
        };

    private static ScenarioMergeParsed ParseText(string text, bool optional)
    {
        string trimmed = text.Trim();
        return (optional && (trimmed.Length == 0)) ? ScenarioMergeParsed.Ok(null) : ScenarioMergeParsed.Ok(Node(trimmed));
    }

    private static ScenarioMergeParsed ParseName(string text) =>
        (text.Trim().Length == 0) ? ScenarioMergeParsed.Refused(ScenarioGeneralViewModel.NameRequiredText) : ParseText(text, false);

    // A check box: an absent or null value reads as the field's default.
    private static ScenarioMergeFieldEditor BoolBox(bool absent) => new(ScenarioMergeEditorKind.Bool, value => FormatBool(value, absent), ParseBool);

    private static string FormatBool(JsonNode? value, bool absent) =>
        value?.GetValueKind() switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => absent ? "true" : "false",
        };

    private static ScenarioMergeParsed ParseBool(string text) =>
        bool.TryParse(text.Trim(), out bool value) ? ScenarioMergeParsed.Ok(Node(value)) : ScenarioMergeParsed.Refused("Enter true or false.");

    private static string FormatOptionalBool(JsonNode? value) =>
        value?.GetValueKind() switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };

    private static ScenarioMergeParsed ParseOptionalBool(string text) => (text.Trim().Length == 0) ? ScenarioMergeParsed.Ok(null) : ParseBool(text);

    private static JsonValue Node(int value) => JsonValue.Create(value);

    private static JsonValue? Node(int? value) => JsonValue.Create(value);

    private static JsonValue Node(double value) => JsonValue.Create(value);

    private static JsonValue? Node(double? value) => JsonValue.Create(value);

    private static JsonValue Node(bool value) => JsonValue.Create(value);

    private static JsonValue? Node(string value) => JsonValue.Create(value);

    private static string FormatIds(JsonNode? value) =>
        (value is JsonArray ids) ? ScenarioIdListText.Join(ids.Select(FormatText)) : FormatText(value);

    private static ScenarioMergeParsed ParseIds(string text) =>
        ScenarioMergeParsed.Ok(new JsonArray([.. ScenarioIdListText.Split(text).Select(Node)]));

    private static ScenarioMergeParsed ParseCommand(string text, bool checkParse, ScenarioMergeCommandContext? context)
    {
        string command = text.Trim();
        string? error = checkParse ? PresetParseError(command, context) : null;
        return new ScenarioMergeParsed(Node(command), error, false);
    }

    // The editor's own finding for a preset that does not parse: ScenarioValidator over an aircraft holding only this command
    // and the flight plan it resolves its fixes against.
    private static string? PresetParseError(string command, ScenarioMergeCommandContext? context)
    {
        if (command.Length == 0)
        {
            return null;
        }

        var aircraft = new ScenarioAircraft { PresetCommands = [new PresetCommand { Command = command }] };
        if (context is not null)
        {
            aircraft.FlightPlan = new ScenarioFlightPlan
            {
                Route = context.Route ?? "",
                Departure = context.Departure ?? "",
                Destination = context.Destination ?? "",
            };
        }

        var scenario = new Scenario { Aircraft = [aircraft] };
        PresetParseFailure? failure = ScenarioValidator.Validate(scenario).Failures.FirstOrDefault();
        return (failure is null) ? null : $"The command does not parse: {failure.Reason ?? "no reason given"}";
    }

    private static string FormatJson(JsonNode? value) => (value is null) ? "null" : value.ToJsonString(Indented);

    private static ScenarioMergeParsed ParseJson(string text)
    {
        try
        {
            return ScenarioMergeParsed.Ok(JsonNode.Parse(text));
        }
        catch (JsonException ex)
        {
            return ScenarioMergeParsed.Refused("Not valid JSON: " + ex.Message);
        }
    }
}
