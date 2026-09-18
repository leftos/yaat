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
        JsonElement root = doc.RootElement;

        if (
            root.TryGetProperty("type", out JsonElement typeProp)
            && typeProp.GetString() == "FeatureCollection"
            && root.TryGetProperty("features", out JsonElement features)
        )
        {
            foreach (JsonElement feature in features.EnumerateArray())
            {
                if (!feature.TryGetProperty("geometry", out JsonElement geometry) || geometry.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                ExtractLines(geometry, lines);
            }
        }
        else if (root.TryGetProperty("type", out _))
        {
            // Handle bare geometry (not wrapped in FeatureCollection)
            ExtractLines(root, lines);
        }

        return new VideoMapData { MapId = mapId, Lines = lines };
    }

    private static void ExtractLines(JsonElement geometry, List<VideoMapLine> lines)
    {
        if (!geometry.TryGetProperty("type", out JsonElement geoType))
        {
            return;
        }

        string? type = geoType.GetString();
        if (!geometry.TryGetProperty("coordinates", out JsonElement coords))
        {
            return;
        }

        switch (type)
        {
            case "LineString":
                VideoMapLine? line = ParseLineString(coords);
                if (line is not null)
                {
                    lines.Add(line);
                }
                break;

            case "MultiLineString":
                foreach (JsonElement lineCoords in coords.EnumerateArray())
                {
                    VideoMapLine? ml = ParseLineString(lineCoords);
                    if (ml is not null)
                    {
                        lines.Add(ml);
                    }
                }
                break;

            case "Polygon":
                // Treat polygon rings as lines (outline only)
                foreach (JsonElement ring in coords.EnumerateArray())
                {
                    VideoMapLine? rl = ParseLineString(ring);
                    if (rl is not null)
                    {
                        lines.Add(rl);
                    }
                }
                break;

            case "MultiPolygon":
                foreach (JsonElement polygon in coords.EnumerateArray())
                {
                    foreach (JsonElement ring in polygon.EnumerateArray())
                    {
                        VideoMapLine? mpl = ParseLineString(ring);
                        if (mpl is not null)
                        {
                            lines.Add(mpl);
                        }
                    }
                }
                break;

            case "GeometryCollection":
                if (geometry.TryGetProperty("geometries", out JsonElement geoms))
                {
                    foreach (JsonElement g in geoms.EnumerateArray())
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

        foreach (JsonElement coord in coordArray.EnumerateArray())
        {
            JsonElement[] arr = coord.EnumerateArray().ToArray();
            if (arr.Length < 2)
            {
                continue;
            }

            // Skip coordinates with null values
            if (arr[0].ValueKind == JsonValueKind.Null || arr[1].ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            // GeoJSON: [longitude, latitude]
            double lon = arr[0].GetDouble();
            double lat = arr[1].GetDouble();
            points.Add((lat, lon));
        }

        return points.Count >= 2 ? new VideoMapLine { Points = points } : null;
    }
}
