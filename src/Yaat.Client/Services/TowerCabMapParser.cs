using System.Globalization;
using System.Text.Json;
using SkiaSharp;
using Yaat.Sim;

namespace Yaat.Client.Services;

/// <summary>
/// Parsed tower cab video map with filled polygons and colored/styled lines.
/// </summary>
public sealed class TowerCabMapData
{
    public required List<TowerCabPolygon> Polygons { get; init; }
    public required List<TowerCabLine> Lines { get; init; }
}

public sealed class TowerCabPolygon
{
    public required List<LatLon> Points { get; init; }
    public required SKColor Color { get; init; }
}

public sealed class TowerCabLine
{
    public required List<LatLon> Points { get; init; }
    public required SKColor Color { get; init; }
    public required int Thickness { get; init; }
}

/// <summary>
/// Parses tower cab GeoJSON video maps into filled polygons and colored lines.
/// Extracts per-feature properties (color, thickness) following the vNAS/CRC convention
/// where a feature with "isDefaults": true provides defaults for unspecified features.
/// </summary>
public static class TowerCabMapParser
{
    private static readonly SKColor DefaultLineColor = SKColors.Yellow;
    private static readonly SKColor DefaultPolygonColor = SKColors.LightGray;
    private const int DefaultThickness = 1;

    public static TowerCabMapData Parse(string geoJson)
    {
        var polygons = new List<TowerCabPolygon>();
        var lines = new List<TowerCabLine>();

        using var doc = JsonDocument.Parse(geoJson);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("type", out JsonElement typeProp) || typeProp.GetString() != "FeatureCollection")
        {
            return new TowerCabMapData { Polygons = polygons, Lines = lines };
        }

        if (!root.TryGetProperty("features", out JsonElement features) || features.ValueKind != JsonValueKind.Array)
        {
            return new TowerCabMapData { Polygons = polygons, Lines = lines };
        }

        // First pass: find defaults feature
        SKColor defaultLineColor = DefaultLineColor;
        int defaultThickness = DefaultThickness;
        foreach (JsonElement feature in features.EnumerateArray())
        {
            if (!IsDefaultsFeature(feature))
            {
                continue;
            }

            if (feature.TryGetProperty("properties", out JsonElement defProps))
            {
                defaultLineColor = GetColor(defProps, DefaultLineColor);
                defaultThickness = GetThickness(defProps, DefaultThickness);
            }

            break;
        }

        // Second pass: parse features
        foreach (JsonElement feature in features.EnumerateArray())
        {
            if (IsDefaultsFeature(feature))
            {
                continue;
            }

            if (!feature.TryGetProperty("geometry", out JsonElement geometry) || geometry.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (!geometry.TryGetProperty("type", out JsonElement geoType))
            {
                continue;
            }

            if (!geometry.TryGetProperty("coordinates", out JsonElement coords))
            {
                continue;
            }

            JsonElement? props = feature.TryGetProperty("properties", out JsonElement p) ? p : (JsonElement?)null;
            string? type = geoType.GetString();

            switch (type)
            {
                case "Polygon":
                    ParsePolygon(coords, props, defaultLineColor, polygons);
                    break;
                case "MultiPolygon":
                    foreach (JsonElement polyCoords in coords.EnumerateArray())
                    {
                        ParsePolygon(polyCoords, props, defaultLineColor, polygons);
                    }
                    break;
                case "LineString":
                    ParseLineString(coords, props, defaultLineColor, defaultThickness, lines);
                    break;
                case "MultiLineString":
                    foreach (JsonElement lineCoords in coords.EnumerateArray())
                    {
                        ParseLineString(lineCoords, props, defaultLineColor, defaultThickness, lines);
                    }
                    break;
            }
        }

        return new TowerCabMapData { Polygons = polygons, Lines = lines };
    }

    private static void ParsePolygon(JsonElement coords, JsonElement? props, SKColor defaultColor, List<TowerCabPolygon> polygons)
    {
        // A polygon has one or more rings; take the outer ring (first)
        foreach (JsonElement ring in coords.EnumerateArray())
        {
            List<LatLon> points = ParseCoordinateArray(ring);
            if (points.Count >= 3)
            {
                SKColor color = props.HasValue ? GetColor(props.Value, defaultColor) : defaultColor;
                polygons.Add(new TowerCabPolygon { Points = points, Color = color });
            }

            break; // Only outer ring for fill
        }
    }

    private static void ParseLineString(JsonElement coords, JsonElement? props, SKColor defaultColor, int defaultThickness, List<TowerCabLine> lines)
    {
        List<LatLon> points = ParseCoordinateArray(coords);
        if (points.Count >= 2)
        {
            SKColor color = props.HasValue ? GetColor(props.Value, defaultColor) : defaultColor;
            int thickness = props.HasValue ? GetThickness(props.Value, defaultThickness) : defaultThickness;
            lines.Add(
                new TowerCabLine
                {
                    Points = points,
                    Color = color,
                    Thickness = thickness,
                }
            );
        }
    }

    private static List<LatLon> ParseCoordinateArray(JsonElement coordArray)
    {
        var points = new List<LatLon>();
        foreach (JsonElement coord in coordArray.EnumerateArray())
        {
            JsonElement[] arr = [.. coord.EnumerateArray()];
            if (arr.Length < 2)
            {
                continue;
            }

            if (arr[0].ValueKind == JsonValueKind.Null || arr[1].ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            // GeoJSON: [longitude, latitude]
            double lon = arr[0].GetDouble();
            double lat = arr[1].GetDouble();
            points.Add(new LatLon(lat, lon));
        }

        return points;
    }

    private static bool IsDefaultsFeature(JsonElement feature)
    {
        if (!feature.TryGetProperty("properties", out JsonElement props))
        {
            return false;
        }

        if (!props.TryGetProperty("isDefaults", out JsonElement val))
        {
            return false;
        }

        return val.ValueKind == JsonValueKind.True;
    }

    private static SKColor GetColor(JsonElement props, SKColor defaultColor)
    {
        if (!props.TryGetProperty("color", out JsonElement colorProp))
        {
            return defaultColor;
        }

        string? colorStr = colorProp.GetString();
        if (string.IsNullOrWhiteSpace(colorStr))
        {
            return defaultColor;
        }

        // vNAS colors can be hex (#RRGGBB) or named
        if (SKColor.TryParse(colorStr, out SKColor parsed))
        {
            return parsed;
        }

        return defaultColor;
    }

    private static int GetThickness(JsonElement props, int defaultThickness)
    {
        if (!props.TryGetProperty("thickness", out JsonElement thickProp))
        {
            return defaultThickness;
        }

        if (thickProp.ValueKind == JsonValueKind.Number)
        {
            return thickProp.GetInt32();
        }

        return defaultThickness;
    }
}
