using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airspace;

public sealed class AirspaceDatabase
{
    private const string DefaultFixtureRelativePath = "Data/Airspace";

    private static readonly ILogger Log = SimLog.CreateLogger<AirspaceDatabase>();
    private static readonly Lazy<AirspaceDatabase> DefaultInstance = new(LoadDefault);

    public static AirspaceDatabase Default => DefaultInstance.Value;

    public IReadOnlyList<AirspaceVolume> Volumes { get; }

    public AirspaceDatabase(IReadOnlyList<AirspaceVolume> volumes)
    {
        Volumes = volumes;
    }

    public IEnumerable<AirspaceVolume> FindContaining(LatLon position, double altitudeFtMsl) =>
        Volumes.Where(v => v.Contains(position, altitudeFtMsl));

    public AirspaceBoundaryCrossing? FindFirstProjectedEntry(AircraftState aircraft, double lookaheadSeconds)
    {
        if (aircraft.GroundSpeed <= 1)
        {
            return null;
        }

        var from = aircraft.Position;
        double lookaheadNm = aircraft.GroundSpeed * lookaheadSeconds / 3600.0;
        if (lookaheadNm <= 0)
        {
            return null;
        }

        var to = GeoMath.ProjectPoint(from, aircraft.TrueTrack, lookaheadNm);
        AirspaceBoundaryCrossing? best = null;
        foreach (var volume in Volumes)
        {
            if (!volume.ContainsAltitude(aircraft.Altitude))
            {
                continue;
            }

            if (volume.Contains(from, aircraft.Altitude))
            {
                continue;
            }

            bool projectedInside = volume.Contains(to, aircraft.Altitude);
            bool crosses = volume.Crosses(from, to, aircraft.Altitude, out var intersection);
            if (!projectedInside && !crosses)
            {
                continue;
            }

            var hit = crosses ? intersection : to;
            double dist = GeoMath.DistanceNm(from, hit);
            if (best is null || dist < best.DistanceNm)
            {
                best = new AirspaceBoundaryCrossing
                {
                    Volume = volume,
                    Intersection = hit,
                    DistanceNm = dist,
                    LookaheadSeconds = lookaheadSeconds,
                };
            }
        }

        return best;
    }

    public static AirspaceDatabase LoadDefault()
    {
        var baseDir = AppContext.BaseDirectory;
        var dataDir = Path.Combine(baseDir, DefaultFixtureRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var files = FindGeoJsonFiles(dataDir);

        if (files.Length == 0)
        {
            files = FindGeoJsonFiles(baseDir);
        }

        if (files.Length == 0)
        {
            var sourcePaths = FindFixturesFromWorkingTree();
            if (sourcePaths.Count > 0)
            {
                files = [.. sourcePaths];
            }
            else
            {
                Log.LogWarning("No FAA airspace fixtures found under {Path}", dataDir);
                return new AirspaceDatabase([]);
            }
        }

        return FromGeoJsonFiles(files);
    }

    private static List<string> FindFixturesFromWorkingTree()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Yaat.Sim", DefaultFixtureRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate))
            {
                return FindGeoJsonFiles(candidate).ToList();
            }

            dir = dir.Parent;
        }

        return [];
    }

    private static string[] FindGeoJsonFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return [.. Directory.GetFiles(directory, "*.geojson"), .. Directory.GetFiles(directory, "*.geojson.br")];
    }

    public static AirspaceDatabase FromGeoJsonFiles(IEnumerable<string> paths)
    {
        var volumesById = new Dictionary<string, AirspaceVolume>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            var db = FromGeoJson(ReadGeoJsonText(path));
            foreach (var volume in db.Volumes)
            {
                volumesById.TryAdd(volume.Id, volume);
            }
        }

        return new AirspaceDatabase([.. volumesById.Values]);
    }

    private static string ReadGeoJsonText(string path)
    {
        if (!path.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
        {
            return File.ReadAllText(path);
        }

        using var file = File.OpenRead(path);
        using var brotli = new BrotliStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli);
        return reader.ReadToEnd();
    }

    public static AirspaceDatabase FromGeoJson(string geoJson)
    {
        using var doc = JsonDocument.Parse(geoJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            return new AirspaceDatabase([]);
        }

        var volumes = new List<AirspaceVolume>();
        foreach (var feature in features.EnumerateArray())
        {
            var volume = ParseFeature(feature);
            if (volume is not null)
            {
                volumes.Add(volume);
            }
        }

        return new AirspaceDatabase(volumes);
    }

    private static AirspaceVolume? ParseFeature(JsonElement feature)
    {
        if (!feature.TryGetProperty("properties", out var props) || !feature.TryGetProperty("geometry", out var geometry))
        {
            return null;
        }

        var classText = GetString(props, "CLASS");
        var airspaceClass = classText switch
        {
            "B" => AirspaceClass.Bravo,
            "C" => AirspaceClass.Charlie,
            _ => (AirspaceClass?)null,
        };
        if (airspaceClass is null)
        {
            return null;
        }

        var rings = ParseRings(geometry);
        if (rings.Count == 0)
        {
            return null;
        }

        var objectId = GetInt(props, "OBJECTID") ?? 0;
        var ident = GetString(props, "IDENT") ?? "";
        var icaoId = GetString(props, "ICAO_ID") ?? "";
        var name = GetString(props, "NAME") ?? ident;
        int lower = ResolveAltitudeFt(props, "LOWER", defaultValue: 0);
        int upper = ResolveAltitudeFt(props, "UPPER", defaultValue: int.MaxValue);

        return new AirspaceVolume
        {
            Id = objectId > 0 ? $"FAA-AIS-{objectId}" : $"{ident}-{name}-{lower}-{upper}",
            Ident = ident,
            IcaoId = icaoId,
            Name = name,
            Class = airspaceClass.Value,
            LowerFtMsl = lower,
            UpperFtMsl = upper,
            Rings = rings,
        };
    }

    private static int ResolveAltitudeFt(JsonElement props, string prefix, int defaultValue)
    {
        var code = GetString(props, prefix + "_CODE");
        if (code is "SFC")
        {
            return 0;
        }

        var value = GetDouble(props, prefix + "_VAL");
        if (value is null || value <= -9990)
        {
            return defaultValue;
        }

        return (int)Math.Round(value.Value);
    }

    private static List<IReadOnlyList<AirspacePoint>> ParseRings(JsonElement geometry)
    {
        var rings = new List<IReadOnlyList<AirspacePoint>>();
        if (!geometry.TryGetProperty("type", out var typeElement) || !geometry.TryGetProperty("coordinates", out var coords))
        {
            return rings;
        }

        switch (typeElement.GetString())
        {
            case "Polygon":
                ParsePolygon(coords, rings);
                break;
            case "MultiPolygon":
                foreach (var polygon in coords.EnumerateArray())
                {
                    ParsePolygon(polygon, rings);
                }
                break;
        }

        return rings;
    }

    private static void ParsePolygon(JsonElement polygon, List<IReadOnlyList<AirspacePoint>> rings)
    {
        foreach (var ringElement in polygon.EnumerateArray())
        {
            var ring = new List<AirspacePoint>();
            foreach (var coordinate in ringElement.EnumerateArray())
            {
                var pair = coordinate.EnumerateArray().ToArray();
                if (pair.Length < 2)
                {
                    continue;
                }

                ring.Add(new AirspacePoint(pair[1].GetDouble(), pair[0].GetDouble()));
            }

            if (ring.Count >= 4)
            {
                rings.Add(ring);
            }
        }
    }

    private static string? GetString(JsonElement props, string name) =>
        props.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement props, string name)
    {
        if (!props.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        return null;
    }

    private static double? GetDouble(JsonElement props, string name)
    {
        if (!props.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
        {
            return number;
        }

        return null;
    }
}
