using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Authoring;

/// <summary>
/// The starting-condition kinds <see cref="ScenarioLoader"/> reads (the <c>startingConditions.type</c> values it matches) and,
/// for each, the <see cref="StartingConditions"/> members its load path reads, by JSON name. The Scenario Editor shows a
/// kind's members and switches between kinds through this one table.
/// </summary>
public static class StartingConditionKinds
{
    /// <summary>The loader's <c>Parking</c> kind: a named stand or spot at the aircraft's airport.</summary>
    public const string Parking = "Parking";

    /// <summary>The loader's <c>OnRunway</c> kind: lined up on a runway at the aircraft's airport.</summary>
    public const string OnRunway = "OnRunway";

    /// <summary>The loader's <c>Coordinates</c> kind: a latitude and longitude, airborne or on the ground.</summary>
    public const string Coordinates = "Coordinates";

    /// <summary>The loader's <c>FixOrFrd</c> kind: a fix or fix-radial-distance, airborne or on the ground.</summary>
    public const string FixOrFrd = "FixOrFrd";

    /// <summary>The loader's <c>OnFinal</c> kind: on final to a runway at the flight plan's destination.</summary>
    public const string OnFinal = "OnFinal";

    private static readonly ILogger Log = SimLog.CreateLogger("StartingConditionKinds");

    // Every modelled member a switch can carry over, by JSON name, in the model's declaration order.
    private static readonly string[] Members =
    [
        "fix",
        "coordinates",
        "runway",
        "altitude",
        "speed",
        "navigationPath",
        "heading",
        "parking",
        "distanceFromRunway",
    ];

    private static readonly (string Kind, string[] Fields)[] Table =
    [
        (Parking, ["type", "parking"]),
        (OnRunway, ["type", "runway"]),
        (Coordinates, ["type", "coordinates", "altitude", "speed", "navigationPath", "heading"]),
        (FixOrFrd, ["type", "fix", "altitude", "speed", "navigationPath", "heading"]),
        (OnFinal, ["type", "runway", "altitude", "speed", "distanceFromRunway"]),
    ];

    /// <summary>Gets the kinds, in the order the editor lists them.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [.. Table.Select(row => row.Kind)];

    /// <summary>The members the loader reads for <paramref name="kind"/>, by JSON name, <c>type</c> included.</summary>
    /// <param name="kind">One of <see cref="Kinds"/>, matched exactly as the loader matches it.</param>
    /// <returns>The member names, in the model's declaration order.</returns>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not one of <see cref="Kinds"/>.</exception>
    public static IReadOnlyList<string> FieldsOf(string kind) => Row(kind);

    /// <summary>
    /// New starting conditions of <paramref name="newKind"/>: the members it uses are carried over from
    /// <paramref name="current"/>, every other one is cleared, and the unmodelled members are kept. A switch to the kind
    /// <paramref name="current"/> already has is an equal copy.
    /// </summary>
    /// <remarks>
    /// The result is read from JSON holding <c>type</c>, the carried members that have a value and the unmodelled ones,
    /// so a scenario document writes only those, never a cleared member as <c>null</c>.
    /// </remarks>
    /// <param name="current">The conditions switched from; left unchanged.</param>
    /// <param name="newKind">One of <see cref="Kinds"/>.</param>
    /// <returns>The new conditions.</returns>
    /// <exception cref="ArgumentException"><paramref name="newKind"/> is not one of <see cref="Kinds"/>.</exception>
    public static StartingConditions Switch(StartingConditions current, string newKind)
    {
        ArgumentNullException.ThrowIfNull(current);
        string[] uses = Row(newKind);
        bool sameKind = string.Equals(current.Type, newKind, StringComparison.Ordinal);
        JsonObject source = JsonSerializer.SerializeToNode(current)?.AsObject() ?? [];
        var json = new JsonObject { ["type"] = newKind };
        foreach (string name in Members.Where(m => sameKind || uses.Contains(m)))
        {
            if (source[name] is JsonNode value)
            {
                json[name] = value.DeepClone();
            }
        }

        foreach (string key in current.ExtensionData?.Keys ?? Enumerable.Empty<string>())
        {
            json[key] = source[key]?.DeepClone();
        }

        StartingConditions switched = ScenarioAircraftFactory.ReadAircraft(new JsonObject { ["startingConditions"] = json }).StartingConditions;
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Switched starting conditions {From} -> {To}", current.Type, newKind);
        }

        return switched;
    }

    private static string[] Row(string kind)
    {
        foreach ((string rowKind, string[] fields) in Table)
        {
            if (string.Equals(rowKind, kind, StringComparison.Ordinal))
            {
                return fields;
            }
        }

        throw new ArgumentException($"Unknown starting-condition kind '{kind}'; expected one of {string.Join(", ", Kinds)}.", nameof(kind));
    }
}
