using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring.Routes;

/// <summary>What <see cref="RouteLibrary.List"/> found: the routes that loaded and the files that did not.</summary>
/// <param name="Routes">The routes that loaded and match the filter, ordered by name.</param>
/// <param name="CorruptFiles">The file names (no folder) of route files that could not be read.</param>
public sealed record RouteLibraryListing(IReadOnlyList<SavedRoute> Routes, IReadOnlyList<string> CorruptFiles);

/// <summary>
/// The per-user route library: one JSON file per <see cref="SavedRoute"/>, named by its id, in one folder
/// (<c>YaatPaths.Combine("routes")</c> by default). Writes are atomic, so a reader sees the old file or the new one.
/// </summary>
/// <param name="rootDir">The folder the route files live in, created on the first save; null for the per-user routes folder.</param>
public sealed class RouteLibrary(string? rootDir)
{
    private static readonly ILogger Log = SimLog.CreateLogger("RouteLibrary");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>A library in the per-user routes folder.</summary>
    public RouteLibrary()
        : this(null) { }

    /// <summary>The folder the route files live in.</summary>
    public string Root { get; } = Path.GetFullPath(rootDir ?? YaatPaths.Combine("routes"));

    /// <summary>
    /// Every route whose ARTCC is <paramref name="artccId"/> (ignoring case), or every route when it is null. A file that
    /// cannot be read is listed by name in <see cref="RouteLibraryListing.CorruptFiles"/> and the others still load.
    /// </summary>
    /// <param name="artccId">The ARTCC to list; null for all.</param>
    /// <returns>The routes found and the files that could not be read.</returns>
    public RouteLibraryListing List(string? artccId)
    {
        List<SavedRoute> routes = [];
        List<string> corrupt = [];
        if (!Directory.Exists(Root))
        {
            return new RouteLibraryListing(routes, corrupt);
        }

        foreach (string path in Directory.GetFiles(Root, "*.json").Order(StringComparer.Ordinal))
        {
            SavedRoute? route = Read(path);
            if (route is null)
            {
                corrupt.Add(Path.GetFileName(path));
            }
            else if ((artccId is null) || string.Equals(route.ArtccId, artccId, StringComparison.OrdinalIgnoreCase))
            {
                routes.Add(route);
            }
        }

        routes.Sort(ByNameThenId);
        return new RouteLibraryListing(routes, corrupt);
    }

    /// <summary>The route with id <paramref name="id"/>, or null when there is none or its file cannot be read.</summary>
    /// <param name="id">The route id.</param>
    /// <returns>The route, or null.</returns>
    public SavedRoute? Load(string id)
    {
        string path = PathFor(id);
        return File.Exists(path) ? Read(path) : null;
    }

    /// <summary>Writes <paramref name="route"/>, replacing any route with the same id.</summary>
    /// <param name="route">The route to save.</param>
    /// <exception cref="ArgumentException">
    /// The route's id is not a GUID string, or its name, ARTCC or points are missing, or a point is null.
    /// </exception>
    public void Save(SavedRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!IsComplete(route))
        {
            throw new ArgumentException("A route needs a name, an ARTCC and a points list with no null point.", nameof(route));
        }

        StoreFiles.WriteAtomically(PathFor(route.Id), JsonSerializer.Serialize(route, JsonOptions));
    }

    /// <summary>Deletes the route with id <paramref name="id"/>.</summary>
    /// <param name="id">The route id.</param>
    /// <returns>True when a route was deleted; false when there was none.</returns>
    public bool Delete(string id)
    {
        string path = PathFor(id);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private string PathFor(string id)
    {
        if (!Guid.TryParse(id, out Guid guid))
        {
            throw new ArgumentException($"A route id must be a GUID string; got '{id}'.", nameof(id));
        }

        return Path.Combine(Root, guid.ToString("D") + ".json");
    }

    private static int ByNameThenId(SavedRoute a, SavedRoute b)
    {
        int byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        return byName != 0 ? byName : StringComparer.Ordinal.Compare(a.Id, b.Id);
    }

    // Fields a hand-edited or truncated file can leave null despite the non-nullable declarations.
    private static bool IsComplete(SavedRoute route) =>
        (route.Name is not null) && (route.ArtccId is not null) && (route.Points is not null) && route.Points.All(p => p is not null);

    private static bool IsNamedByItsId(SavedRoute route, string path) =>
        Guid.TryParse(route.Id, out Guid guid) && string.Equals(Path.GetFileName(path), guid.ToString("D") + ".json", StringComparison.Ordinal);

    private static SavedRoute? Read(string path)
    {
        try
        {
            SavedRoute? route = JsonSerializer.Deserialize<SavedRoute>(File.ReadAllText(path), JsonOptions);
            if ((route is not null) && IsNamedByItsId(route, path) && IsComplete(route))
            {
                return route;
            }

            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning("Route file {File} holds no complete route named by its id", Path.GetFileName(path));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Route file {File} could not be read", Path.GetFileName(path));
            }
        }

        return null;
    }
}
