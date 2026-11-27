using System.Text.Json;

namespace Yaat.Sim.Authoring.Stamping;

/// <summary>
/// A saved set of aircraft fields a route stamp writes onto scenario aircraft. A profile holds no route points: the route
/// comes from the stamped route, and the profile says what else is written alongside it.
/// </summary>
public sealed record StampProfile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>Gets the profile's id, which is also its file name in the store.</summary>
    public string Id { get; init; } = "";

    /// <summary>Gets the name the author gave the profile.</summary>
    public string Name { get; init; } = "";

    /// <summary>Gets a value indicating whether only aircraft whose type is wake class Heavy (not Super) are stamped.</summary>
    public bool HeavyOnly { get; init; }

    /// <summary>Gets the STAR aliases applied to the stamped route, in order.</summary>
    public IReadOnlyList<StarAlias> StarAliases { get; init; } = [];

    /// <summary>Gets the starting-condition fields the stamp writes.</summary>
    public StampStartingConditions StartingConditions { get; init; } = new();

    /// <summary>Gets the flight plan fields the stamp writes.</summary>
    public StampFlightPlan FlightPlan { get; init; } = new();

    /// <summary>Gets the preset commands the stamp writes.</summary>
    public StampPresets Presets { get; init; } = new();

    /// <summary>Gets the auto-track conditions the stamp writes.</summary>
    public StampAutoTrack AutoTrack { get; init; } = new();

    /// <summary>Gets the primary airport the stamp writes, or null to leave it.</summary>
    public string? PrimaryAirport { get; init; }

    /// <summary>Gets the expected approach the stamp writes, or null to leave it.</summary>
    public string? ExpectedApproach { get; init; }

    /// <summary>Gets the pools the stamp picks random values from.</summary>
    public StampPools Pools { get; init; } = new();

    /// <summary>Serializes the profile to indented camel-case JSON.</summary>
    /// <returns>The profile's JSON text.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Reads a profile from its JSON text.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The profile.</returns>
    /// <exception cref="JsonException">The text is not a profile object.</exception>
    public static StampProfile FromJson(string json) =>
        JsonSerializer.Deserialize<StampProfile>(json, JsonOptions) ?? throw new JsonException("The profile JSON is null.");
}

/// <summary>A STAR alias: route tokens naming <paramref name="From"/> are rewritten to <paramref name="To"/>.</summary>
/// <param name="From">The STAR to replace.</param>
/// <param name="To">The STAR written in its place.</param>
public sealed record StarAlias(string From, string To);

/// <summary>The starting-condition fields of a stamp profile.</summary>
public sealed record StampStartingConditions
{
    /// <summary>Gets a value indicating whether the starting conditions are written.</summary>
    public bool Apply { get; init; }

    /// <summary>Gets the starting-condition type, or null to leave it.</summary>
    public string? Type { get; init; }

    /// <summary>Gets the starting altitude in feet, or null to leave it.</summary>
    public double? Altitude { get; init; }

    /// <summary>Gets the starting speed in knots, or null to leave it.</summary>
    public double? Speed { get; init; }

    /// <summary>Gets the starting heading in degrees, or null to leave it.</summary>
    public double? Heading { get; init; }

    /// <summary>Gets a value indicating whether the navigation path is written from the stamped route.</summary>
    public bool ApplyNavigationPath { get; init; }

    /// <summary>Gets the starting runway, or null to leave it.</summary>
    public string? Runway { get; init; }

    /// <summary>Gets the starting parking spot, or null to leave it.</summary>
    public string? Parking { get; init; }
}

/// <summary>The flight plan fields of a stamp profile.</summary>
public sealed record StampFlightPlan
{
    /// <summary>Gets a value indicating whether the flight plan is written.</summary>
    public bool Apply { get; init; }

    /// <summary>Gets the flight rules (IFR or VFR), or null to leave them.</summary>
    public string? Rules { get; init; }

    /// <summary>Gets the departure airport, or null to leave it.</summary>
    public string? Departure { get; init; }

    /// <summary>Gets the destination airport, or null to leave it.</summary>
    public string? Destination { get; init; }

    /// <summary>Gets a value indicating whether the flight plan route is written from the stamped route.</summary>
    public bool ApplyRoute { get; init; }

    /// <summary>Gets the filed cruise altitude in feet, or null to leave it.</summary>
    public int? CruiseAltitude { get; init; }

    /// <summary>Gets the filed cruise speed in knots, or null to leave it.</summary>
    public int? CruiseSpeed { get; init; }

    /// <summary>Gets the remarks, or null to leave them.</summary>
    public string? Remarks { get; init; }
}

/// <summary>The preset commands of a stamp profile.</summary>
public sealed record StampPresets
{
    /// <summary>Gets a value indicating whether the presets are written.</summary>
    public bool Apply { get; init; }

    /// <summary>Gets a value indicating whether every existing preset of the aircraft is removed first.</summary>
    public bool Replace { get; init; }

    /// <summary>Gets the preset commands, whose text may hold stamp tokens.</summary>
    public IReadOnlyList<StampPresetCommand> Commands { get; init; } = [];
}

/// <summary>One preset command of a stamp profile.</summary>
/// <param name="Command">The command text, which may hold stamp tokens such as {CS}.</param>
/// <param name="TimeOffset">The seconds after spawn at which the command runs.</param>
public sealed record StampPresetCommand(string Command, int TimeOffset);

/// <summary>The auto-track conditions of a stamp profile.</summary>
public sealed record StampAutoTrack
{
    /// <summary>Gets a value indicating whether the auto-track conditions are written.</summary>
    public bool Apply { get; init; }

    /// <summary>Gets the position id that tracks the aircraft.</summary>
    public string PositionId { get; init; } = "";

    /// <summary>Gets the handoff delay in seconds, or null for none.</summary>
    public int? HandoffDelay { get; init; }

    /// <summary>Gets the scratch pad, or null for none.</summary>
    public string? ScratchPad { get; init; }

    /// <summary>Gets the interim altitude, or null for none.</summary>
    public string? InterimAltitude { get; init; }

    /// <summary>Gets the cleared altitude, or null for none.</summary>
    public string? ClearedAltitude { get; init; }
}

/// <summary>The pools a stamp picks random values from; an empty pool picks nothing.</summary>
public sealed record StampPools
{
    /// <summary>Gets the aircraft types to pick from.</summary>
    public IReadOnlyList<string> AircraftTypes { get; init; } = [];

    /// <summary>Gets the callsign prefixes a random callsign is built on.</summary>
    public IReadOnlyList<string> CallsignPrefixes { get; init; } = [];

    /// <summary>Gets the runways to pick from.</summary>
    public IReadOnlyList<string> Runways { get; init; } = [];

    /// <summary>Gets the parking spots to pick from.</summary>
    public IReadOnlyList<string> ParkingSpots { get; init; } = [];
}
