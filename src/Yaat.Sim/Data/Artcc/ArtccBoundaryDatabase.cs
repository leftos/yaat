using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Artcc;

/// <summary>
/// Lateral ARTCC boundaries, loaded once from the bundled <c>Data/Artcc/ArtccBoundaries.geojson</c>
/// (one feature per center, <c>properties.id</c> = ARTCC id). The bundled set is coarse — one
/// polygon per center with generous margins, so neighbours overlap — which is why center rooms are
/// the only consumer: a tower or TRACON room scopes by its facility's airspace volumes instead.
/// Same shape as <see cref="Airspace.AirspaceDatabase"/>: bundled fixture, bbox pre-filter, ring test.
/// </summary>
public sealed class ArtccBoundaryDatabase
{
    private static readonly ILogger Log = SimLog.CreateLogger("ArtccBoundaryDatabase");
    private const string DefaultFixtureRelativePath = "Data/Artcc";

    private static readonly Lazy<ArtccBoundaryDatabase> DefaultInstance = new(LoadDefault, LazyThreadSafetyMode.ExecutionAndPublication);

    public static ArtccBoundaryDatabase Default => DefaultInstance.Value;

    private readonly Dictionary<string, ArtccBoundary> _byId;

    public IReadOnlyList<ArtccBoundary> Boundaries { get; }

    public ArtccBoundaryDatabase(IReadOnlyList<ArtccBoundary> boundaries)
    {
        Boundaries = boundaries;
        _byId = new Dictionary<string, ArtccBoundary>(StringComparer.OrdinalIgnoreCase);
        foreach (var boundary in boundaries)
        {
            _byId.TryAdd(boundary.Id, boundary);
        }
    }

    public ArtccBoundary? FindById(string artccId) => _byId.GetValueOrDefault(artccId);

    public IEnumerable<ArtccBoundary> FindContaining(LatLon position) => Boundaries.Where(b => b.Contains(position));

    public static ArtccBoundaryDatabase LoadDefault()
    {
        var baseDir = AppContext.BaseDirectory;
        var dataDir = Path.Combine(baseDir, DefaultFixtureRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var files = FindGeoJsonFiles(dataDir);
        if (files.Length == 0)
        {
            files = [.. FindFixturesFromWorkingTree()];
        }

        if (files.Length == 0)
        {
            Log.LogWarning("No ARTCC boundary fixtures found under {Path}", dataDir);
            return new ArtccBoundaryDatabase([]);
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

    public static ArtccBoundaryDatabase FromGeoJsonFiles(IEnumerable<string> paths)
    {
        var boundaries = new List<ArtccBoundary>();
        foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            boundaries.AddRange(FromGeoJson(ReadGeoJsonText(path)).Boundaries);
        }

        return new ArtccBoundaryDatabase(boundaries);
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

    public static ArtccBoundaryDatabase FromGeoJson(string geoJson)
    {
        using var doc = JsonDocument.Parse(geoJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            return new ArtccBoundaryDatabase([]);
        }

        var boundaries = new List<ArtccBoundary>();
        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("properties", out var props) || !feature.TryGetProperty("geometry", out var geometry))
            {
                continue;
            }

            if (!props.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var rings = ParseRings(geometry);
            if (rings.Count == 0)
            {
                continue;
            }

            boundaries.Add(new ArtccBoundary { Id = idElement.GetString()!, Rings = rings });
        }

        return new ArtccBoundaryDatabase(boundaries);
    }

    private static List<IReadOnlyList<LatLon>> ParseRings(JsonElement geometry)
    {
        var rings = new List<IReadOnlyList<LatLon>>();
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

    private static void ParsePolygon(JsonElement polygon, List<IReadOnlyList<LatLon>> rings)
    {
        foreach (var ringElement in polygon.EnumerateArray())
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
    }
}
