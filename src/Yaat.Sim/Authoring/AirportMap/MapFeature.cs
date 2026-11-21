using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace Yaat.Sim.Authoring.AirportMap;

/// <summary>
/// One GeoJSON position of an airport map, in the file's own <c>[lon, lat]</c> order.
/// </summary>
/// <param name="Lon">The longitude in degrees, exactly as the file's double.</param>
/// <param name="Lat">The latitude in degrees, exactly as the file's double.</param>
public readonly record struct MapPosition(double Lon, double Lat);

/// <summary>
/// A JSON member the map model does not type, kept verbatim so it is written back as read.
/// </summary>
/// <param name="Name">The member's name.</param>
/// <param name="RawJson">The member's value as JSON text (<c>null</c> for a JSON null).</param>
public sealed record ForeignMember(string Name, string RawJson);

/// <summary>
/// One feature of an airport map (taxiway, runway, parking, spot, helipad, or a type the parser does not know), immutable:
/// its geometry as doubles, its <c>properties</c> object in source key order, and every other member verbatim.
/// </summary>
/// <remarks>
/// The geometry is typed only when it is a <c>Point</c> or <c>LineString</c> object holding just <c>type</c> and
/// <c>coordinates</c>, every position two numbers; any other geometry, like a <c>properties</c> that is not an object, is
/// kept whole in <see cref="ForeignMembers"/> and <see cref="GeometryType"/> is <c>null</c>.
/// <para>
/// A line string holds at least two positions unless the reader built it from a source file that holds fewer: the public
/// constructor refuses a shorter one, so no edit can create an empty or single-vertex line.
/// </para>
/// <para>
/// A feature the reader built also keeps its source <c>coordinates</c> text, written back while the feature is unedited:
/// QGIS exports carry 17-18 significant digits that a double cannot hold, so writing the doubles would change the decimal
/// value of every such coordinate (never the double the parser reads). A feature built through the public constructor,
/// as every edit is, writes the shortest text that reads back as each double.
/// </para>
/// </remarks>
public sealed class MapFeature
{
    /// <summary>
    /// The fewest positions a line string can hold: the public constructor refuses fewer, while the reader keeps a source
    /// file's shorter line readable and round-trippable.
    /// </summary>
    internal const int MinLineVertices = 2;

    private readonly JsonObject? _properties;

    /// <summary>
    /// Initializes a new instance of the <see cref="MapFeature"/> class.
    /// </summary>
    /// <param name="properties">The feature's <c>properties</c>, cloned here; <c>null</c> when it is kept as a foreign member.</param>
    /// <param name="geometryType"><c>Point</c> or <c>LineString</c>; <c>null</c> when the geometry is a foreign member.</param>
    /// <param name="coordinates">The geometry's positions: one for a point, each vertex in order for a line string.</param>
    /// <param name="foreignMembers">The feature's other members, in source order.</param>
    /// <exception cref="ArgumentException">
    /// The geometry cannot hold that many positions, or a line string has fewer than two.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A position's longitude or latitude is not finite.</exception>
    public MapFeature(
        JsonObject? properties,
        string? geometryType,
        ImmutableArray<MapPosition> coordinates,
        ImmutableList<ForeignMember> foreignMembers
    )
        : this(properties, geometryType, coordinates, foreignMembers, sourceCoordinatesJson: null)
    {
        int count = coordinates.IsDefault ? 0 : coordinates.Length;
        if ((string.Equals(geometryType, "LineString", StringComparison.Ordinal)) && (count < MinLineVertices))
        {
            throw new ArgumentException($"A LineString cannot hold {count} positions; it needs at least {MinLineVertices}", nameof(coordinates));
        }
    }

    // The reader's constructor: sourceCoordinatesJson is the geometry's "coordinates" text exactly as read, and
    // coordinates are its doubles.
    internal MapFeature(
        JsonObject? properties,
        string? geometryType,
        ImmutableArray<MapPosition> coordinates,
        ImmutableList<ForeignMember> foreignMembers,
        string? sourceCoordinatesJson
    )
    {
        ArgumentNullException.ThrowIfNull(foreignMembers);
        int count = coordinates.IsDefault ? 0 : coordinates.Length;
        bool valid = geometryType switch
        {
            null => count == 0,
            "Point" => count == 1,
            "LineString" => true,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException($"A {geometryType ?? "verbatim"} geometry cannot hold {count} positions", nameof(coordinates));
        }

        ThrowIfNotFinite(coordinates);
        _properties = properties?.DeepClone().AsObject();
        GeometryType = geometryType;
        Coordinates = coordinates.IsDefault ? [] : coordinates;
        ForeignMembers = foreignMembers;
        SourceCoordinatesJson = geometryType is null ? null : sourceCoordinatesJson;
    }

    /// <summary>
    /// Gets the geometry's <c>coordinates</c> JSON text as the reader found it, or <c>null</c> for a feature built through
    /// the public constructor (every edit), which writes <see cref="Coordinates"/> instead.
    /// </summary>
    internal string? SourceCoordinatesJson { get; }

    /// <summary>
    /// Gets the geometry's GeoJSON type (<c>Point</c> or <c>LineString</c>), or <c>null</c> when the geometry is kept verbatim.
    /// </summary>
    public string? GeometryType { get; }

    /// <summary>
    /// Gets the geometry's positions in source order; empty when <see cref="GeometryType"/> is <c>null</c>.
    /// </summary>
    public ImmutableArray<MapPosition> Coordinates { get; }

    /// <summary>
    /// Gets the feature's members other than <c>type</c> and the typed <c>properties</c> and <c>geometry</c>, in source order.
    /// </summary>
    public ImmutableList<ForeignMember> ForeignMembers { get; }

    /// <summary>
    /// Gets whether the feature has a typed <c>properties</c> object.
    /// </summary>
    public bool HasProperties => _properties is not null;

    /// <summary>
    /// Gets <c>properties.type</c> when it is a string (<c>parking</c>, <c>taxiway</c>, ... or an unknown kind), else <c>null</c>.
    /// The key is matched exactly first, then case-insensitively, as <c>GeoJsonParser</c> does: ATCTrainer exports use a
    /// capitalised <c>Type</c>. The value is returned as written.
    /// </summary>
    public string? FeatureType
    {
        get
        {
            if (_properties is null)
            {
                return null;
            }

            if (_properties.TryGetPropertyValue("type", out JsonNode? exact))
            {
                return AsText(exact);
            }

            foreach (KeyValuePair<string, JsonNode?> member in _properties)
            {
                if (string.Equals(member.Key, "type", StringComparison.OrdinalIgnoreCase))
                {
                    return AsText(member.Value);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Copies the feature's <c>properties</c>, keys in source order, so the caller can change the copy freely.
    /// </summary>
    /// <returns>A deep copy of <c>properties</c>, or <c>null</c> when <see cref="HasProperties"/> is <c>false</c>.</returns>
    public JsonObject? CloneProperties() => _properties?.DeepClone().AsObject();

    private static string? AsText(JsonNode? node) => ((node is JsonValue value) && (value.TryGetValue(out string? text))) ? text : null;

    private static void ThrowIfNotFinite(ImmutableArray<MapPosition> coordinates)
    {
        if (coordinates.IsDefault)
        {
            return;
        }

        foreach (MapPosition position in coordinates)
        {
            if (!((double.IsFinite(position.Lon)) && (double.IsFinite(position.Lat))))
            {
                throw new ArgumentOutOfRangeException(nameof(coordinates), position, "A map position needs a finite longitude and latitude");
            }
        }
    }
}
