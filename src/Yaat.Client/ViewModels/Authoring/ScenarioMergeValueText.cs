using System.Text.Json.Nodes;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>How a merge pane shows one side's value: absent, JSON null, a time, or the value itself.</summary>
public static class ScenarioMergeValueText
{
    /// <summary>The text of a value the side does not hold.</summary>
    public const string NotSet = "(not set)";

    /// <summary>The text of a JSON null.</summary>
    public const string None = "(none)";

    private static readonly HashSet<(string Section, string FieldKey)> TimeFields =
    [
        ("Initialization triggers", "timeOffset"),
        ("Aircraft", "spawnDelay"),
        ("Aircraft", "presetCommands[]/timeOffset"),
        .. GeneratorTimes("Generators"),
        .. GeneratorTimes("VFR arrival generators"),
        .. GeneratorTimes("Overflight generators"),
    ];

    /// <summary>The text one side's value reads as.</summary>
    /// <param name="section">The row's section.</param>
    /// <param name="fieldKey">The row's label path.</param>
    /// <param name="present">Whether the side holds a value at the row's path.</param>
    /// <param name="value">The side's value; null for JSON null or an absent value.</param>
    /// <returns>The text shown.</returns>
    public static string Format(string section, string fieldKey, bool present, JsonNode? value)
    {
        if (!present)
        {
            return NotSet;
        }

        if (value is null)
        {
            return None;
        }

        if (IsTime(section, fieldKey) && (value is JsonValue time) && time.TryGetValue(out int seconds))
        {
            return ScenarioFieldText.FormatTime(seconds);
        }

        return ((value is JsonValue text) && text.TryGetValue(out string? words)) ? words : value.ToJsonString();
    }

    /// <summary>Whether a field holds a time in seconds.</summary>
    /// <param name="section">The row's section.</param>
    /// <param name="fieldKey">The row's label path.</param>
    /// <returns>True for a time field.</returns>
    public static bool IsTime(string section, string fieldKey) => TimeFields.Contains((section, fieldKey));

    private static (string, string)[] GeneratorTimes(string section) =>
        [(section, "startTimeOffset"), (section, "intervalTime"), (section, "maxTime")];
}
