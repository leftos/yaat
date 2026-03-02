using System.Text.Json;

namespace Yaat.Sim.Data;

/// <summary>
/// Parses GeoJSON FeatureCollections into VideoMapData line geometry.
/// Extracts LineString and MultiLineString features.
/// </summary>
public static class VideoMapParser
{
    public static VideoMapData Parse(string mapId, string geoJson)
    {
        var lines = new List<VideoMapLine>();

        using var doc = JsonDocument.Parse(geoJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("type", out var typeProp)
            && typeProp.GetString() == "FeatureCollection"
            && root.TryGetProperty("features", out var features))
        {
            foreach (var feature in features.EnumerateArray())
            {
                if (!feature.TryGetProperty("geometry", out var geometry))
                {
                    continue;
                }

                ExtractLines(geometry, lines);
            }
        }
        else if (root.TryGetProperty("type", out var rootType))
        {
            // Handle bare geometry (not wrapped in FeatureCollection)
            ExtractLines(root, lines);
        }

        return new VideoMapData { MapId = mapId, Lines = lines };
    }

    private static void ExtractLines(
        JsonElement geometry, List<VideoMapLine> lines)
    {
        if (!geometry.TryGetProperty("type", out var geoType))
        {
            return;
        }

        var type = geoType.GetString();
        if (!geometry.TryGetProperty("coordinates", out var coords))
        {
            return;
        }

        switch (type)
        {
            case "LineString":
                var line = ParseLineString(coords);
                if (line is not null)
                {
                    lines.Add(line);
                }
                break;

            case "MultiLineString":
                foreach (var lineCoords in coords.EnumerateArray())
                {
                    var ml = ParseLineString(lineCoords);
                    if (ml is not null)
                    {
                        lines.Add(ml);
                    }
                }
                break;

            case "Polygon":
                // Treat polygon rings as lines (outline only)
                foreach (var ring in coords.EnumerateArray())
                {
                    var rl = ParseLineString(ring);
                    if (rl is not null)
                    {
                        lines.Add(rl);
                    }
                }
                break;

            case "MultiPolygon":
                foreach (var polygon in coords.EnumerateArray())
                {
                    foreach (var ring in polygon.EnumerateArray())
                    {
                        var mpl = ParseLineString(ring);
                        if (mpl is not null)
                        {
                            lines.Add(mpl);
                        }
                    }
                }
                break;

            case "GeometryCollection":
                if (geometry.TryGetProperty("geometries", out var geoms))
                {
                    foreach (var g in geoms.EnumerateArray())
                    {
                        ExtractLines(g, lines);
                    }
                }
                break;
        }
    }

    private static VideoMapLine? ParseLineString(JsonElement coordArray)
    {
        var points = new List<(double Lat, double Lon)>();

        foreach (var coord in coordArray.EnumerateArray())
        {
            var arr = coord.EnumerateArray().ToArray();
            if (arr.Length < 2)
            {
                continue;
            }

            // GeoJSON: [longitude, latitude]
            var lon = arr[0].GetDouble();
            var lat = arr[1].GetDouble();
            points.Add((lat, lon));
        }

        return points.Count >= 2
            ? new VideoMapLine { Points = points }
            : null;
    }
}
