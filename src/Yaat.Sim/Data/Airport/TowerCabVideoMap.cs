using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>One straight two-point segment of a tower-cab videomap, as drawn.</summary>
public readonly record struct VideoMapSegment(LatLon A, LatLon B);

/// <summary>
/// The two-point line segments of an airport's tower-cab videomap (the vNAS GeoJSON CRC Tower Cab draws over the satellite
/// image): every LineString of exactly two coordinates and every two-coordinate part of a MultiLineString, of any style, in
/// file order. Painted hold-short bars are drawn this way; polygons and longer lines are left out. <see cref="Read"/> is the
/// only way in, so the hash always describes the segments it carries.
/// </summary>
public sealed class TowerCabVideoMap
{
    private static readonly ILogger Log = SimLog.CreateLogger("TowerCabVideoMap");

    private TowerCabVideoMap(IReadOnlyList<VideoMapSegment> segments)
    {
        Segments = segments;
        Hash = HashOf(segments);
    }

    /// <summary>The map's two-point segments, in file order.</summary>
    public IReadOnlyList<VideoMapSegment> Segments { get; }

    /// <summary>
    /// The SHA-256 of the segments, lowercase hex: one line per segment, in order, of A's latitude and longitude and B's
    /// latitude and longitude (round-trip invariant text), tab-separated, LF-terminated. Feeds the precompute key.
    /// </summary>
    public string Hash { get; }

    /// <summary>Read the two-point segments of a tower-cab videomap.</summary>
    /// <param name="geoJson">The videomap's GeoJSON FeatureCollection.</param>
    /// <returns>The map.</returns>
    /// <exception cref="JsonException">
    /// <paramref name="geoJson"/> is not valid JSON, or its root is not a FeatureCollection with a features array.
    /// </exception>
    public static TowerCabVideoMap Read(string geoJson)
    {
        var segments = new List<VideoMapSegment>();
        using var doc = JsonDocument.Parse(geoJson);
        if (!TryGetFeatures(doc.RootElement, out JsonElement features))
        {
            throw new JsonException("tower-cab videomap: root is not a FeatureCollection with a features array");
        }

        for (int index = 0; index < features.GetArrayLength(); index++)
        {
            foreach (JsonElement candidate in LineCandidates(features[index], index))
            {
                if (TryReadSegment(candidate, index, out VideoMapSegment segment))
                {
                    segments.Add(segment);
                }
            }
        }

        return new TowerCabVideoMap(segments);
    }

    private static bool TryGetFeatures(JsonElement root, out JsonElement features)
    {
        features = default;
        return (root.ValueKind == JsonValueKind.Object)
            && root.TryGetProperty("type", out JsonElement type)
            && (type.ValueKind == JsonValueKind.String)
            && (type.GetString() == "FeatureCollection")
            && root.TryGetProperty("features", out features)
            && (features.ValueKind == JsonValueKind.Array);
    }

    /// <summary>The coordinate arrays of a feature's lines: a LineString's one, each part of a MultiLineString, else none.</summary>
    private static IEnumerable<JsonElement> LineCandidates(JsonElement feature, int index)
    {
        if (!TryGetGeometry(feature, index, out string? type, out JsonElement coordinates))
        {
            return [];
        }

        return type switch
        {
            "LineString" => [coordinates],
            "MultiLineString" when coordinates.ValueKind == JsonValueKind.Array => coordinates.EnumerateArray(),
            _ => [],
        };
    }

    private static bool TryGetGeometry(JsonElement feature, int index, out string? type, out JsonElement coordinates)
    {
        type = null;
        coordinates = default;
        if ((feature.ValueKind != JsonValueKind.Object) || !feature.TryGetProperty("geometry", out JsonElement geometry))
        {
            Log.LogWarning("tower-cab videomap: feature {Index} has no geometry; skipped", index);
            return false;
        }

        if (geometry.ValueKind == JsonValueKind.Null)
        {
            Log.LogWarning("tower-cab videomap: feature {Index} has a null geometry; skipped", index);
            return false;
        }

        if ((geometry.ValueKind != JsonValueKind.Object) || !geometry.TryGetProperty("type", out JsonElement typeElement))
        {
            return false;
        }

        type = typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : null;
        return geometry.TryGetProperty("coordinates", out coordinates);
    }

    /// <summary>A segment from a coordinate array of exactly two points, each at least two numbers; any other array is none.</summary>
    private static bool TryReadSegment(JsonElement candidate, int index, out VideoMapSegment segment)
    {
        segment = default;
        if ((candidate.ValueKind != JsonValueKind.Array) || (candidate.GetArrayLength() != 2))
        {
            return false;
        }

        if (!TryReadPoint(candidate[0], out LatLon a) || !TryReadPoint(candidate[1], out LatLon b))
        {
            Log.LogWarning("tower-cab videomap: feature {Index} has a two-point line with a malformed position; skipped", index);
            return false;
        }

        segment = new VideoMapSegment(a, b);
        return true;
    }

    /// <summary>A GeoJSON position, <c>[lon, lat, ...]</c>.</summary>
    private static bool TryReadPoint(JsonElement position, out LatLon point)
    {
        point = default;
        if ((position.ValueKind != JsonValueKind.Array) || (position.GetArrayLength() < 2))
        {
            return false;
        }

        if ((position[0].ValueKind != JsonValueKind.Number) || (position[1].ValueKind != JsonValueKind.Number))
        {
            return false;
        }

        if (!position[0].TryGetDouble(out double lon) || !double.IsFinite(lon))
        {
            return false;
        }

        if (!position[1].TryGetDouble(out double lat) || !double.IsFinite(lat))
        {
            return false;
        }

        point = new LatLon(lat, lon);
        return true;
    }

    private static string HashOf(IReadOnlyList<VideoMapSegment> segments)
    {
        var canonical = new StringBuilder();
        foreach (VideoMapSegment segment in segments)
        {
            LatLon a = segment.A;
            LatLon b = segment.B;
            canonical.Append(CultureInfo.InvariantCulture, $"{a.Lat:R}\t{a.Lon:R}\t{b.Lat:R}\t{b.Lon:R}\n");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
