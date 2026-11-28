using System.Text.Json;
using System.Text.RegularExpressions;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Stamping;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>One field Apply Route would change, as the preview lists it: the editor's label for the field and its old and new values.</summary>
/// <param name="Label">The field's label as the Aircraft tab shows it, "Preset {command}" for a preset, or the raw path when unmapped.</param>
/// <param name="Path">The JSON Pointer of the field in the aircraft.</param>
/// <param name="Kind">Whether the field changed, was added or was removed.</param>
/// <param name="OldValue">The old value as JSON text; null when the field was added.</param>
/// <param name="NewValue">The new value as JSON text; null when the field was removed.</param>
public sealed partial record ApplyRoutePreviewRow(string Label, string Path, DiffKind Kind, string? OldValue, string? NewValue)
{
    // The Aircraft tab's label for each field a stamp writes, by its JSON Pointer.
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["/aircraftId"] = "Callsign",
        ["/aircraftType"] = "Type",
        ["/airportId"] = "Airport",
        ["/expectedApproach"] = "Approach",
        ["/startingConditions/type"] = "Kind",
        ["/startingConditions/altitude"] = "Altitude (ft)",
        ["/startingConditions/speed"] = "Speed (kt)",
        ["/startingConditions/heading"] = "Heading (deg)",
        ["/startingConditions/runway"] = "Runway",
        ["/startingConditions/parking"] = "Parking",
        ["/startingConditions/navigationPath"] = "Navigation path",
        ["/flightplan"] = "Flight plan",
        ["/flightplan/rules"] = "Rules",
        ["/flightplan/departure"] = "Departure",
        ["/flightplan/destination"] = "Destination",
        ["/flightplan/cruiseAltitude"] = "Cruise altitude (ft)",
        ["/flightplan/cruiseSpeed"] = "Cruise speed (kt)",
        ["/flightplan/route"] = "Route",
        ["/flightplan/remarks"] = "Remarks",
        ["/flightplan/aircraftType"] = "Filed type",
        ["/autoTrackConditions"] = "Auto-track",
        ["/autoTrackConditions/positionId"] = "Position",
        ["/autoTrackConditions/handoffDelay"] = "Handoff delay (seconds)",
        ["/autoTrackConditions/scratchPad"] = "Scratchpad",
        ["/autoTrackConditions/interimAltitude"] = "Interim altitude",
        ["/autoTrackConditions/clearedAltitude"] = "Cleared altitude",
    };

    /// <summary>The preview row of one stamp change.</summary>
    /// <param name="change">The change.</param>
    /// <returns>The row, labelled.</returns>
    public static ApplyRoutePreviewRow From(StampChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return new ApplyRoutePreviewRow(LabelFor(change), change.Path, change.Kind, change.OldValue, change.NewValue);
    }

    /// <summary>The label of a change: the editor's field label, "Preset {command}" for a whole preset, else the raw path.</summary>
    /// <param name="change">The change.</param>
    /// <returns>The label.</returns>
    public static string LabelFor(StampChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Labels.TryGetValue(change.Path, out string? label))
        {
            return label;
        }

        return (PresetPath().IsMatch(change.Path) && (PresetCommand(change.NewValue ?? change.OldValue) is { } command))
            ? $"Preset {command}"
            : change.Path;
    }

    // The command text of a preset's JSON, or null when the text is not a preset object.
    private static string? PresetCommand(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return ((doc.RootElement.ValueKind == JsonValueKind.Object) && doc.RootElement.TryGetProperty("command", out JsonElement command))
                ? command.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^/presetCommands/\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex PresetPath();
}
