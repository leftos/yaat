using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Yaat.Sim.Authoring.AirportMap;

/// <summary>
/// Reads an airport map's combined GeoJSON into an <see cref="AirportMapDocument"/>, accepting what <c>GeoJsonParser</c>
/// accepts: comments, trailing commas and leading-zero number literals.
/// </summary>
public static partial class GeoJsonMapReader
{
    private static readonly JsonDocumentOptions LenientOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>
    /// Reads a GeoJSON FeatureCollection, keeping every feature in source order and every member it does not type verbatim.
    /// </summary>
    /// <param name="json">The FeatureCollection text.</param>
    /// <returns>The document.</returns>
    /// <exception cref="JsonException">The text is malformed, is not a FeatureCollection, or a feature is not a Feature object.</exception>
    public static AirportMapDocument Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonObject root =
            JsonNode.Parse(SanitizeJson(json), documentOptions: LenientOptions) as JsonObject
            ?? throw new JsonException("Airport map JSON is not an object; expected a GeoJSON FeatureCollection.");
        if (!IsString(root["type"], "FeatureCollection") || (root["features"] is not JsonArray features))
        {
            throw new JsonException("Airport map JSON is not a GeoJSON FeatureCollection with a features array.");
        }

        ImmutableList<ForeignMember>.Builder foreign = ImmutableList.CreateBuilder<ForeignMember>();
        foreach (KeyValuePair<string, JsonNode?> member in root)
        {
            if (member.Key is not ("type" or "features"))
            {
                foreign.Add(ToForeign(member));
            }
        }

        ImmutableList<MapFeature>.Builder read = ImmutableList.CreateBuilder<MapFeature>();
        for (int i = 0; i < features.Count; i++)
        {
            read.Add(ReadFeature(features[i], i));
        }

        return new AirportMapDocument(read.ToImmutable(), foreign.ToImmutable());
    }

    /// <summary>
    /// Strips leading zeros from JSON number literals (<c>03</c> becomes <c>3</c>), as <c>GeoJsonParser</c> does before it
    /// parses, since RFC 8259 forbids them.
    /// </summary>
    /// <param name="json">The map text.</param>
    /// <returns>The text with every leading-zero literal shortened.</returns>
    internal static string SanitizeJson(string json) => LeadingZeroRegex().Replace(json, "$1");

    // A copy of GeoJsonParser's private LeadingZeroRegex: the parser lives under Data/Airport, whose sources the
    // precompute hash covers, so it is not widened for this reader.
    [GeneratedRegex(@"(?<=[:,\[]\s*)0+(\d)")]
    private static partial Regex LeadingZeroRegex();

    private static MapFeature ReadFeature(JsonNode? node, int index)
    {
        if ((node is not JsonObject feature) || !IsString(feature["type"], "Feature"))
        {
            throw new JsonException($"Airport map feature {index} is not a GeoJSON Feature object.");
        }

        var properties = feature["properties"] as JsonObject;
        (string? geometryType, ImmutableArray<MapPosition> coordinates) = TryReadGeometry(feature["geometry"]);
        ImmutableList<ForeignMember>.Builder foreign = ImmutableList.CreateBuilder<ForeignMember>();
        foreach (KeyValuePair<string, JsonNode?> member in feature)
        {
            bool typed = member.Key switch
            {
                "type" => true,
                "properties" => properties is not null,
                "geometry" => geometryType is not null,
                _ => false,
            };
            if (!typed)
            {
                foreign.Add(ToForeign(member));
            }
        }

        string? sourceCoordinates = geometryType is null ? null : feature["geometry"]!["coordinates"]!.ToJsonString();
        return new MapFeature(properties, geometryType, coordinates, foreign.ToImmutable(), sourceCoordinates);
    }

    // A Point or LineString holding only type and coordinates, every position two finite numbers; anything else is
    // (null, default) and stays a foreign member.
    private static (string? Type, ImmutableArray<MapPosition> Coordinates) TryReadGeometry(JsonNode? node)
    {
        if ((node is not JsonObject geometry) || (geometry.Count != 2) || (geometry["coordinates"] is not JsonArray coordinates))
        {
            return (null, default);
        }

        if (IsString(geometry["type"], "Point"))
        {
            return TryReadPosition(coordinates, out MapPosition position) ? ("Point", [position]) : (null, default);
        }

        return IsString(geometry["type"], "LineString") && TryReadLine(coordinates, out ImmutableArray<MapPosition> line)
            ? ("LineString", line)
            : (null, default);
    }

    private static bool TryReadLine(JsonArray coordinates, out ImmutableArray<MapPosition> line)
    {
        ImmutableArray<MapPosition>.Builder vertices = ImmutableArray.CreateBuilder<MapPosition>(coordinates.Count);
        foreach (JsonNode? vertex in coordinates)
        {
            if (!TryReadPosition(vertex, out MapPosition position))
            {
                line = default;
                return false;
            }

            vertices.Add(position);
        }

        line = vertices.MoveToImmutable();
        return true;
    }

    private static bool TryReadPosition(JsonNode? node, out MapPosition position)
    {
        if ((node is JsonArray { Count: 2 } pair) && TryReadNumber(pair[0], out double lon) && TryReadNumber(pair[1], out double lat))
        {
            position = new MapPosition(lon, lat);
            return true;
        }

        position = default;
        return false;
    }

    private static bool TryReadNumber(JsonNode? node, out double number)
    {
        number = 0;
        return (node is JsonValue value)
            && (value.GetValueKind() == JsonValueKind.Number)
            && value.TryGetValue(out number)
            && double.IsFinite(number);
    }

    private static bool IsString(JsonNode? node, string expected) =>
        (node is JsonValue value) && value.TryGetValue(out string? text) && string.Equals(text, expected, StringComparison.Ordinal);

    private static ForeignMember ToForeign(KeyValuePair<string, JsonNode?> member) => new(member.Key, member.Value?.ToJsonString() ?? "null");
}
