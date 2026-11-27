using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The editable value of one Airport Editor property row: the text being typed, the error of the last refused commit,
/// and the commit and cancel that the panel's Enter, focus loss and Esc run.
/// </summary>
public sealed partial class AirportEditorPropertyEditor : ObservableObject
{
    private readonly string _original;
    private readonly Func<string, string?> _commit;

    /// <summary>Initializes a new instance of the <see cref="AirportEditorPropertyEditor"/> class.</summary>
    /// <param name="text">The value's text as the row shows it.</param>
    /// <param name="commit">Applies a typed text; returns why it was refused, or null once it is applied or dropped.</param>
    public AirportEditorPropertyEditor(string text, Func<string, string?> commit)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(commit);
        _original = text;
        _text = text;
        _commit = commit;
    }

    /// <summary>The text in the value box.</summary>
    [ObservableProperty]
    private string _text;

    /// <summary>Why the last commit was refused, shown under the box, or null when it was not.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>
    /// Commits the typed text as one undo step; text equal to the row's value commits nothing, and a box whose binding
    /// wrote null (an emptied box) commits empty text.
    /// </summary>
    public void Commit()
    {
        string typed = Text ?? "";
        Error = string.Equals(typed, _original, StringComparison.Ordinal) ? null : _commit(typed);
    }

    /// <summary>Restores the row's value to the box and clears the error, committing nothing.</summary>
    public void Cancel()
    {
        Text = _original;
        Error = null;
    }
}

/// <summary>
/// Which Airport Editor properties the panel edits, and how each key's typed text becomes its value, in the forms
/// <c>GeoJsonParser</c> reads.
/// </summary>
public static class AirportEditorPropertyValues
{
    private const string Name = "name";
    private const string Heading = "heading";
    private const string Circular = "circular";
    private const string ThresholdError = "threshold is two distances in feet, end 1 - end 2 (e.g. 0 - 957)";
    private const string NoTurnoffError = "noTurnoff is two lists of taxiway names ([[\"A\"], [\"B\"]])";

    private static readonly HashSet<string> RunwayKeys = new(
        ["threshold", "turnoff", "noTurnoff", "holdShortDistance", "patternSize", "patternAltitude"],
        StringComparer.OrdinalIgnoreCase
    );

    // Each key's parser, from the key as the feature holds it (for the message) and the trimmed text.
    private static readonly Dictionary<string, Func<string, string, Parsed>> Parsers = new(StringComparer.OrdinalIgnoreCase)
    {
        [Name] = (_, text) => (text.Length > 0) ? new(JsonValue.Create(text), null) : Refused("A name cannot be empty"),
        [Heading] = (_, text) => HeadingNode(text) is { } heading ? new(heading, null) : Refused("heading is a whole number from 0 to 360"),
        [Circular] = (_, text) =>
            bool.TryParse(text, out bool circular) ? new(JsonValue.Create(circular), null) : Refused("circular is true or false"),
        ["threshold"] = (_, text) => ThresholdText(text) is { } threshold ? new(JsonValue.Create(threshold), null) : Refused(ThresholdError),
        ["turnoff"] = (_, text) => TurnoffNode(text) is { } side ? new(side, null) : Refused("turnoff is left or right"),
        ["noTurnoff"] = (_, text) => NoTurnoffNode(text) is { } lists ? new(lists, null) : Refused(NoTurnoffError),
        ["holdShortDistance"] = ParseNumber,
        ["patternSize"] = ParseNumber,
        ["patternAltitude"] = ParseNumber,
    };

    /// <summary>Whether the panel edits a key of a feature of a type: the name of every feature, and the type's own keys.</summary>
    /// <param name="featureType">The feature's type, or null.</param>
    /// <param name="key">The property's key, matched ignoring case.</param>
    /// <returns>True for name, a parking's or helipad's heading, a runway's keys and a taxiway's circular flag.</returns>
    public static bool IsEditable(string? featureType, string key) =>
        (Is(key, Name))
        || (
            featureType switch
            {
                "parking" or "helipad" => Is(key, Heading),
                "runway" => IsRunwayKey(key),
                "taxiway" => Is(key, Circular),
                _ => false,
            }
        );

    /// <summary>Turns the text typed for a key into the value the feature stores.</summary>
    /// <param name="key">The property's key, matched ignoring case.</param>
    /// <param name="text">The typed text.</param>
    /// <param name="value">The value to store, or null when the text is refused.</param>
    /// <returns>Why the text is refused, or null when <paramref name="value"/> holds the value.</returns>
    public static string? Parse(string key, string text, out JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(text);
        Parsed parsed = Parsers.TryGetValue(key, out Func<string, string, Parsed>? parser)
            ? parser(key, text.Trim())
            : Refused($"{key} is not a property the panel edits");
        value = parsed.Value;
        return parsed.Error;
    }

    private static bool IsRunwayKey(string key) => RunwayKeys.Contains(key);

    private static bool Is(string key, string wanted) => string.Equals(key, wanted, StringComparison.OrdinalIgnoreCase);

    private static Parsed Refused(string error) => new(null, error);

    private static Parsed ParseNumber(string key, string text) =>
        Number(text) is double number ? new(NumberNode(number), null) : Refused($"{key} must be a number");

    private static double? Number(string text) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) && (double.IsFinite(number)) ? number : null;

    // GeoJsonParser reads a parking's heading as a whole number of degrees.
    private static JsonValue? HeadingNode(string text) =>
        (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int heading)) && (heading >= 0) && (heading <= 360)
            ? JsonValue.Create((long)heading)
            : null;

    private static JsonValue? TurnoffNode(string text)
    {
        string side = text.ToLowerInvariant();
        return side is "left" or "right" ? JsonValue.Create(side) : null;
    }

    // A whole number is stored as an integer, as the vNAS maps write them.
    private static JsonValue NumberNode(double number) =>
        ((number == Math.Floor(number)) && (Math.Abs(number) < 1e15)) ? JsonValue.Create((long)number) : JsonValue.Create(number);

    // Written with at most three decimals, so the text always reads back as two distances.
    private static string? ThresholdText(string text)
    {
        string[] parts = text.Split('-', StringSplitOptions.TrimEntries);
        if ((parts.Length != 2) || (Number(parts[0]) is not double first) || (Number(parts[1]) is not double second))
        {
            return null;
        }

        if ((first < 0) || (second < 0))
        {
            return null;
        }

        CultureInfo inv = CultureInfo.InvariantCulture;
        return $"{first.ToString("0.###", inv)} - {second.ToString("0.###", inv)}";
    }

    private static JsonArray? NoTurnoffNode(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }

        bool valid =
            (node is JsonArray { Count: 2 } ends)
            && (ends.All(end => (end is JsonArray names) && (names.All(n => (n is JsonValue v) && (v.GetValueKind() == JsonValueKind.String)))));
        return valid ? (JsonArray)node! : null;
    }

    // A parse's outcome: the value to store, or why the text is refused.
    private readonly record struct Parsed(JsonNode? Value, string? Error);
}
