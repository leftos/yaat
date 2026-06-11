using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Mva;

/// <summary>
/// Minimum Vectoring Altitude sectors loaded from committed FAA AIXM-derived GeoJSON
/// (built by tools/build-mva-data.py). Mirrors <see cref="Airspace.AirspaceDatabase"/>: a lazy
/// process-wide <see cref="Default"/> singleton, a 3-tier fixture search, Brotli support, and a
/// silent no-op (empty database) when no fixture is present. Query <see cref="FindSector"/> for the
/// controlling MVA sector at a point.
/// </summary>
public sealed class MvaDatabase
{
    private const string DefaultFixtureRelativePath = "Data/Mva";

    private static readonly ILogger Log = SimLog.CreateLogger<MvaDatabase>();
    private static readonly Lazy<MvaDatabase> DefaultInstance = new(LoadDefault);
    private static MvaDatabase? _instanceOverride;

    public static MvaDatabase Default => _instanceOverride ?? DefaultInstance.Value;

    /// <summary>Pin an explicit instance (tests). Pass null to revert to the lazy default.</summary>
    public static void SetInstance(MvaDatabase? instance) => _instanceOverride = instance;

    public IReadOnlyList<MvaSector> Sectors { get; }

    public MvaDatabase(IReadOnlyList<MvaSector> sectors)
    {
        Sectors = sectors;
    }

    /// <summary>
    /// The controlling MVA sector at a point, or null if no sector covers it. When sectors overlap
    /// (e.g. a higher-floor obstacle island the surrounding sector's hole did not fully exclude), the
    /// highest floor wins — the conservative (safest) minimum vectoring altitude.
    /// </summary>
    public MvaSector? FindSector(LatLon position)
    {
        MvaSector? best = null;
        foreach (var sector in Sectors)
        {
            if (sector.Contains(position) && (best is null || sector.FloorFtMsl > best.FloorFtMsl))
            {
                best = sector;
            }
        }

        return best;
    }

    /// <summary>The controlling MVA floor (ft MSL) at a point, or null if no sector covers it.</summary>
    public int? GetFloorFtMsl(LatLon position) => FindSector(position)?.FloorFtMsl;

    /// <summary>
    /// Classify an altitude against the controlling MVA floor at a position. An altitude within
    /// <paramref name="atBandFt"/> of the floor reads as <see cref="MvaRelation.At"/>. The returned
    /// sector is null only when no sector covers the position (<see cref="MvaRelation.NoData"/>).
    /// </summary>
    public (MvaRelation Relation, MvaSector? Sector) Classify(LatLon position, double altitudeFtMsl, int atBandFt)
    {
        var sector = FindSector(position);
        if (sector is null)
        {
            return (MvaRelation.NoData, null);
        }

        if (altitudeFtMsl < sector.FloorFtMsl - atBandFt)
        {
            return (MvaRelation.Below, sector);
        }

        if (altitudeFtMsl <= sector.FloorFtMsl + atBandFt)
        {
            return (MvaRelation.At, sector);
        }

        return (MvaRelation.Above, sector);
    }

    public static MvaDatabase LoadDefault()
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
                Log.LogWarning("No FAA MVA fixtures found under {Path}", dataDir);
                return new MvaDatabase([]);
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

    public static MvaDatabase FromGeoJsonFiles(IEnumerable<string> paths)
    {
        var sectors = new List<MvaSector>();
        foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            sectors.AddRange(FromGeoJson(ReadGeoJsonText(path)).Sectors);
        }

        return new MvaDatabase(sectors);
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

    public static MvaDatabase FromGeoJson(string geoJson)
    {
        using var doc = JsonDocument.Parse(geoJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            return new MvaDatabase([]);
        }

        var sectors = new List<MvaSector>();
        foreach (var feature in features.EnumerateArray())
        {
            var sector = ParseFeature(feature);
            if (sector is not null)
            {
                sectors.Add(sector);
            }
        }

        return new MvaDatabase(sectors);
    }

    private static MvaSector? ParseFeature(JsonElement feature)
    {
        if (!feature.TryGetProperty("properties", out var props) || !feature.TryGetProperty("geometry", out var geometry))
        {
            return null;
        }

        if (
            !props.TryGetProperty("mvaFloorFt", out var floorElement)
            || floorElement.ValueKind != JsonValueKind.Number
            || !floorElement.TryGetInt32(out int floorFt)
        )
        {
            return null;
        }

        var rings = ParseRings(geometry);
        if (rings.Count == 0)
        {
            return null;
        }

        return new MvaSector
        {
            Sector = GetString(props, "sector") ?? "",
            FloorFtMsl = floorFt,
            Facility = GetString(props, "facility") ?? "",
            Rings = rings,
        };
    }

    private static List<IReadOnlyList<LatLon>> ParseRings(JsonElement geometry)
    {
        var rings = new List<IReadOnlyList<LatLon>>();
        if (
            !geometry.TryGetProperty("type", out var typeElement)
            || typeElement.GetString() != "Polygon"
            || !geometry.TryGetProperty("coordinates", out var coords)
        )
        {
            return rings;
        }

        // GeoJSON Polygon: ring 0 is the exterior boundary, rings 1+ are interior holes. Order is
        // preserved so MvaSector can apply exterior-minus-holes containment.
        foreach (var ringElement in coords.EnumerateArray())
        {
            var ring = new List<LatLon>();
            foreach (var coordinate in ringElement.EnumerateArray())
            {
                var pair = coordinate.EnumerateArray().ToArray();
                if (pair.Length < 2)
                {
                    continue;
                }

                ring.Add(new LatLon(pair[1].GetDouble(), pair[0].GetDouble()));
            }

            if (ring.Count >= 4)
            {
                rings.Add(ring);
            }
        }

        return rings;
    }

    private static string? GetString(JsonElement props, string name) =>
        props.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
