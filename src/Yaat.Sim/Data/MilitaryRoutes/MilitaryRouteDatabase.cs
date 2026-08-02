using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.MilitaryRoutes;

/// <summary>
/// Military training routes and aerial refueling tracks loaded from the committed AP/1B-derived
/// fixture (built by tools/build-mtr-data.py). Mirrors <see cref="Mva.MvaDatabase"/>: a lazy
/// process-wide <see cref="Default"/>, a 3-tier fixture search, Brotli support, and a silent empty
/// database when no fixture is present.
///
/// Unlike <see cref="Mva.MvaDatabase"/> this database is read from inside the
/// <see cref="NavigationDatabase"/> constructor, so a leaked test override would poison every
/// navigation database built afterwards. <see cref="ScopedOverride"/> exists for that reason —
/// prefer it over the bare <see cref="SetInstance"/>, and prefer constructor injection over both.
/// </summary>
public sealed class MilitaryRouteDatabase
{
    private const string DefaultFixtureRelativePath = "Data/MilitaryRoutes";

    private static readonly ILogger Log = SimLog.CreateLogger<MilitaryRouteDatabase>();
    private static readonly Lazy<MilitaryRouteDatabase> DefaultInstance = new(LoadDefault);
    private static MilitaryRouteDatabase? _instanceOverride;

    private readonly Dictionary<string, MilitaryRoute> _byDesignator;

    public static MilitaryRouteDatabase Default => _instanceOverride ?? DefaultInstance.Value;

    public IReadOnlyList<MilitaryRoute> Routes { get; }

    public int Count => Routes.Count;

    public MilitaryRouteDatabase(IReadOnlyList<MilitaryRoute> routes)
    {
        Routes = routes;
        _byDesignator = new Dictionary<string, MilitaryRoute>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            _byDesignator.TryAdd(route.Designator, route);
        }
    }

    /// <summary>Pin an explicit instance (tests). Pass null to revert to the lazy default.</summary>
    public static void SetInstance(MilitaryRouteDatabase? instance) => _instanceOverride = instance;

    /// <summary>
    /// Pin an instance for the lifetime of the returned scope, then restore whatever was pinned
    /// before. Use this in tests rather than <see cref="SetInstance"/>: a leaked override changes
    /// what every later <see cref="NavigationDatabase"/> is built from, and xUnit runs test classes
    /// in parallel.
    /// </summary>
    public static IDisposable ScopedOverride(MilitaryRouteDatabase instance) => new OverrideScope(instance);

    private sealed class OverrideScope : IDisposable
    {
        private readonly MilitaryRouteDatabase? _previous;

        public OverrideScope(MilitaryRouteDatabase instance)
        {
            _previous = _instanceOverride;
            _instanceOverride = instance;
        }

        public void Dispose() => _instanceOverride = _previous;
    }

    /// <summary>
    /// Look a route up by designator. Accepts the hyphenated form AP/1B prints (<c>IR-149</c>) as
    /// well as the unhyphenated form flight plans use (<c>IR149</c>).
    /// </summary>
    public MilitaryRoute? Get(string designator)
    {
        if (string.IsNullOrWhiteSpace(designator))
        {
            return null;
        }

        var normalized = Normalize(designator);
        return _byDesignator.GetValueOrDefault(normalized);
    }

    public bool Contains(string designator) => Get(designator) is not null;

    public IEnumerable<MilitaryRoute> OfType(MilitaryRouteType type) => Routes.Where(r => r.Type == type);

    /// <summary>Strip the hyphen AP/1B prints between prefix and number; flight plans never write it.</summary>
    public static string Normalize(string designator) => designator.Trim().Replace("-", string.Empty).ToUpperInvariant();

    public static MilitaryRouteDatabase LoadDefault()
    {
        var baseDir = AppContext.BaseDirectory;
        var dataDir = Path.Combine(baseDir, DefaultFixtureRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var files = FindFixtureFiles(dataDir);

        if (files.Length == 0)
        {
            files = FindFixtureFiles(baseDir);
        }

        if (files.Length == 0)
        {
            files = [.. FindFixturesFromWorkingTree()];
        }

        if (files.Length == 0)
        {
            Log.LogWarning("No AP/1B military route fixtures found under {Path}", dataDir);
            return new MilitaryRouteDatabase([]);
        }

        return FromFiles(files);
    }

    private static List<string> FindFixturesFromWorkingTree()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Yaat.Sim", DefaultFixtureRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate))
            {
                return [.. FindFixtureFiles(candidate)];
            }

            dir = dir.Parent;
        }

        return [];
    }

    private static string[] FindFixtureFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return [.. Directory.GetFiles(directory, "*.json"), .. Directory.GetFiles(directory, "*.json.br")];
    }

    public static MilitaryRouteDatabase FromFiles(IEnumerable<string> paths)
    {
        var ordered = paths.Order(StringComparer.OrdinalIgnoreCase).ToList();
        var routes = new List<MilitaryRoute>();
        foreach (var path in ordered)
        {
            try
            {
                routes.AddRange(FromJson(ReadFixtureText(path)).Routes);
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
            {
                Log.LogError(ex, "Failed to load military route fixture {Path}", path);
            }
        }

        Log.LogInformation("Loaded {RouteCount} military routes from {FileCount} fixture(s)", routes.Count, ordered.Count);
        return new MilitaryRouteDatabase(routes);
    }

    private static string ReadFixtureText(string path)
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

    public static MilitaryRouteDatabase FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array)
        {
            return new MilitaryRouteDatabase([]);
        }

        var parsed = new List<MilitaryRoute>();
        foreach (var element in routes.EnumerateArray())
        {
            var route = ParseRoute(element);
            if (route is not null)
            {
                parsed.Add(route);
            }
        }

        return new MilitaryRouteDatabase(parsed);
    }

    private static MilitaryRoute? ParseRoute(JsonElement element)
    {
        var designator = element.GetPropertyOrNull("designator")?.GetString();
        var typeText = element.GetPropertyOrNull("type")?.GetString();
        if (string.IsNullOrEmpty(designator) || !TryParseType(typeText, out var type))
        {
            Log.LogWarning("Skipping military route with missing designator or unknown type '{Type}'", typeText);
            return null;
        }

        var variants = ParseVariants(element, designator);
        // A chapter 5 entry publishes its geometry per direction, so the first variant supplies the
        // Points the expander and the airway shadow index read; chapters 2-4 publish points directly.
        var points = variants.Count > 0 ? variants[0].Points : ParsePoints(element, "points", designator);
        if (points.Count == 0)
        {
            Log.LogWarning("Skipping military route {Designator}: no usable points", designator);
            return null;
        }

        return new MilitaryRoute
        {
            Designator = designator,
            Printed = element.GetPropertyOrNull("printed")?.GetString() ?? designator,
            Type = type,
            Points = points,
            Widths = ParseWidths(element),
            EntryPoints = variants.Count > 0 ? variants[0].EntryPoints : ParseStringArray(element, "entryPoints"),
            ExitPoints = variants.Count > 0 ? variants[0].ExitPoints : ParseStringArray(element, "exitPoints"),
            TerrainFollowing = element.GetPropertyOrNull("terrainFollowing")?.GetBoolean() ?? false,
            OriginatingActivity = element.GetPropertyOrNull("originatingActivity")?.GetString() ?? string.Empty,
            SchedulingActivity = element.GetPropertyOrNull("schedulingActivity")?.GetString() ?? string.Empty,
            Hours = element.GetPropertyOrNull("hours")?.GetString() ?? string.Empty,
            ArKind = ParseArKind(element.GetPropertyOrNull("arKind")?.GetString()),
            Variants = variants,
            RouteAltitude = ParseAltitude(element.GetPropertyOrNull("altitude")),
            AtcAssignedAirspace = ParseAirspace(element),
        };
    }

    private static List<MilitaryRouteVariant> ParseVariants(JsonElement element, string designator)
    {
        var variants = new List<MilitaryRouteVariant>();
        if (element.GetPropertyOrNull("variants") is not { ValueKind: JsonValueKind.Array } array)
        {
            return variants;
        }

        foreach (var item in array.EnumerateArray())
        {
            var points = ParsePoints(item, "points", designator);
            if (points.Count == 0)
            {
                continue;
            }

            variants.Add(
                new MilitaryRouteVariant
                {
                    Direction = item.GetPropertyOrNull("direction")?.GetString() ?? string.Empty,
                    Points = points,
                    Pattern = ParsePoints(item, "pattern", designator),
                    EntryPoints = ParseStringArray(item, "entryPoints"),
                    ExitPoints = ParseStringArray(item, "exitPoints"),
                }
            );
        }

        return variants;
    }

    private static List<LatLon> ParseAirspace(JsonElement element)
    {
        var vertices = new List<LatLon>();
        if (element.GetPropertyOrNull("airspace") is not { ValueKind: JsonValueKind.Array } array)
        {
            return vertices;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() == 2)
            {
                vertices.Add(new LatLon(item[0].GetDouble(), item[1].GetDouble()));
            }
        }

        return vertices;
    }

    private static List<MilitaryRoutePoint> ParsePoints(JsonElement element, string property, string designator)
    {
        var points = new List<MilitaryRoutePoint>();
        if (element.GetPropertyOrNull(property) is not { ValueKind: JsonValueKind.Array } array)
        {
            return points;
        }

        foreach (var item in array.EnumerateArray())
        {
            var id = item.GetPropertyOrNull("id")?.GetString();
            var lat = item.GetPropertyOrNull("lat")?.GetDouble();
            var lon = item.GetPropertyOrNull("lon")?.GetDouble();
            if (string.IsNullOrEmpty(id) || lat is null || lon is null)
            {
                continue;
            }

            points.Add(
                new MilitaryRoutePoint
                {
                    Id = id,
                    Name = item.GetPropertyOrNull("name")?.GetString() ?? $"{designator}{id}",
                    Position = new LatLon(lat.Value, lon.Value),
                    Role = ParseRole(item.GetPropertyOrNull("role")?.GetString()),
                    Altitude = ParseAltitude(item.GetPropertyOrNull("altitude")),
                    Frd = item.GetPropertyOrNull("frd")?.GetString(),
                }
            );
        }

        return points;
    }

    private static MilitaryRouteAltitude ParseAltitude(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } altitude)
        {
            return MilitaryRouteAltitude.None;
        }

        return new MilitaryRouteAltitude
        {
            Kind = ParseAltitudeKind(altitude.GetPropertyOrNull("kind")?.GetString()),
            Raw = altitude.GetPropertyOrNull("raw")?.GetString() ?? string.Empty,
            FloorFt = altitude.GetPropertyOrNull("floor_ft")?.GetInt32(),
            FloorReference = ParseReference(altitude.GetPropertyOrNull("floor_ref")?.GetString()),
            CeilingFt = altitude.GetPropertyOrNull("ceiling_ft")?.GetInt32(),
            CeilingReference = ParseReference(altitude.GetPropertyOrNull("ceiling_ref")?.GetString()),
        };
    }

    private static List<MilitaryRouteWidthSpan> ParseWidths(JsonElement element)
    {
        var widths = new List<MilitaryRouteWidthSpan>();
        if (element.GetPropertyOrNull("widths") is not { ValueKind: JsonValueKind.Array } array)
        {
            return widths;
        }

        foreach (var item in array.EnumerateArray())
        {
            double left = item.GetPropertyOrNull("left_nm")?.GetDouble() ?? 0;
            double right = item.GetPropertyOrNull("right_nm")?.GetDouble() ?? 0;
            widths.Add(
                new MilitaryRouteWidthSpan(
                    item.GetPropertyOrNull("from_point")?.GetString(),
                    item.GetPropertyOrNull("to_point")?.GetString(),
                    left,
                    right
                )
            );
        }

        return widths;
    }

    private static List<string> ParseStringArray(JsonElement element, string property)
    {
        var values = new List<string>();
        if (element.GetPropertyOrNull(property) is not { ValueKind: JsonValueKind.Array } array)
        {
            return values;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.GetString() is { Length: > 0 } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static bool TryParseType(string? text, out MilitaryRouteType type)
    {
        type = MilitaryRouteType.Ir;
        switch (text?.ToUpperInvariant())
        {
            case "IR":
                type = MilitaryRouteType.Ir;
                return true;
            case "VR":
                type = MilitaryRouteType.Vr;
                return true;
            case "SR":
                type = MilitaryRouteType.Sr;
                return true;
            case "AR":
                type = MilitaryRouteType.Ar;
                return true;
            default:
                return false;
        }
    }

    private static MilitaryRoutePointRole ParseRole(string? text) =>
        text switch
        {
            "entry" => MilitaryRoutePointRole.Entry,
            "exit" => MilitaryRoutePointRole.Exit,
            "alternateEntry" => MilitaryRoutePointRole.AlternateEntry,
            "alternateExit" => MilitaryRoutePointRole.AlternateExit,
            "arip" => MilitaryRoutePointRole.Arip,
            "arcp" => MilitaryRoutePointRole.Arcp,
            "checkPoint" => MilitaryRoutePointRole.CheckPoint,
            "anchorPoint" => MilitaryRoutePointRole.AnchorPoint,
            "patternCorner" => MilitaryRoutePointRole.PatternCorner,
            _ => MilitaryRoutePointRole.Point,
        };

    private static MilitaryRouteArKind ParseArKind(string? text) =>
        text switch
        {
            "track" => MilitaryRouteArKind.Track,
            "anchor" => MilitaryRouteArKind.Anchor,
            _ => MilitaryRouteArKind.None,
        };

    private static MilitaryRouteAltitudeKind ParseAltitudeKind(string? text) =>
        text switch
        {
            "single" => MilitaryRouteAltitudeKind.Single,
            "block" => MilitaryRouteAltitudeKind.Block,
            "atOrBelow" => MilitaryRouteAltitudeKind.AtOrBelow,
            "asAssigned" => MilitaryRouteAltitudeKind.AsAssigned,
            "unparsed" => MilitaryRouteAltitudeKind.Unparsed,
            _ => MilitaryRouteAltitudeKind.None,
        };

    private static AltitudeReference? ParseReference(string? text) =>
        text switch
        {
            "AGL" => AltitudeReference.Agl,
            "MSL" => AltitudeReference.Msl,
            _ => null,
        };
}

internal static class JsonElementExtensions
{
    /// <summary>The named property, or null when absent or JSON null.</summary>
    internal static JsonElement? GetPropertyOrNull(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;
}
