using System.Text.Json;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Data;

/// <summary>
/// The scenario-authored sidecar for one scenario: settings that ride beside the scenario JSON instead of inside it.
/// Loaded from <c>ARTCCs/{ARTCC}/Scenarios/{scenarioId}.json</c>; authored data committed by pull request, never
/// written by the app.
/// </summary>
public sealed class ScenarioSidecar
{
    /// <summary>
    /// The runways the scenario's room starts with, read from the sidecar's tokens without a navigation-database check.
    /// Empty when the sidecar names none.
    /// </summary>
    public ActiveRunways ActiveRunways { get; init; } = ActiveRunways.Empty;
}

/// <summary>The scenario sidecars found under one ARTCCs base directory, looked up by ARTCC id and scenario id.</summary>
public sealed class ScenarioSidecarLoadResult
{
    private readonly Dictionary<(string ArtccId, string ScenarioId), ScenarioSidecar> _byKey = [];
    private readonly Dictionary<(string ArtccId, string ScenarioId), string> _sourceByKey = [];
    private readonly List<(string? ArtccId, string Message)> _warnings = [];

    /// <summary>
    /// What could not be loaded, in the order the loader met it. Every entry names the file it came from; an entry
    /// about one airport also names the airport. A caller surfaces these however it surfaces load problems.
    /// </summary>
    public IReadOnlyList<string> Warnings => [.. _warnings.Select(w => w.Message)];

    /// <summary>The warnings from <paramref name="artccId"/>'s own sidecars (matched case-insensitively), in load order.</summary>
    public IReadOnlyList<string> WarningsFor(string artccId)
    {
        string artcc = NormalizeArtcc(artccId);
        return [.. _warnings.Where(w => w.ArtccId == artcc).Select(w => w.Message)];
    }

    /// <summary>
    /// Every other warning, in load order: those from other ARTCCs' sidecars, and those that belong to no ARTCC (a
    /// missing ARTCCs directory).
    /// </summary>
    public IReadOnlyList<string> WarningsOutside(string artccId)
    {
        string artcc = NormalizeArtcc(artccId);
        return [.. _warnings.Where(w => w.ArtccId != artcc).Select(w => w.Message)];
    }

    /// <summary>The sidecar for a scenario, or null when that ARTCC has no <c>Scenarios/{scenarioId}.json</c> for it.</summary>
    public ScenarioSidecar? Find(string artccId, string scenarioId)
    {
        (string ArtccId, string ScenarioId) key = (NormalizeArtcc(artccId), ScenarioIdentity.Normalize(scenarioId));
        return _byKey.TryGetValue(key, out ScenarioSidecar? sidecar) ? sidecar : null;
    }

    /// <summary>Records a warning from <paramref name="artccId"/>'s sidecars, or from none when it is null.</summary>
    internal void Warn(string? artccId, string message) => _warnings.Add((artccId is null ? null : NormalizeArtcc(artccId), message));

    internal void Add(string artccId, string filePath, ScenarioSidecar sidecar)
    {
        string scenarioId = Path.GetFileNameWithoutExtension(filePath);
        (string ArtccId, string ScenarioId) key = (NormalizeArtcc(artccId), ScenarioIdentity.Normalize(scenarioId));
        if (_sourceByKey.TryGetValue(key, out string? existing))
        {
            Warn(artccId, $"{filePath}: scenario id {scenarioId} is already loaded from {existing}; skipping");
            return;
        }

        _sourceByKey[key] = filePath;
        _byKey[key] = sidecar;
    }

    internal static string NormalizeArtcc(string artccId) => artccId.Trim().ToUpperInvariant();
}

/// <summary>
/// Scans <c>{artccsBaseDir}/{ARTCC}/Scenarios/*.json</c> across every ARTCC subdirectory and loads each scenario's
/// sidecar. The file name without its extension is the scenario id, matched the way the rest of the app matches one
/// (<see cref="ScenarioIdentity.Normalize"/>). A file that does not parse, and anything inside one that does not read,
/// is recorded in the result's warnings and skipped — nothing throws, so one author's typo cannot stop every other
/// scenario from loading.
/// </summary>
public static class ScenarioSidecarLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static ScenarioSidecarLoadResult LoadAll(string artccsBaseDir)
    {
        var result = new ScenarioSidecarLoadResult();
        if (!Directory.Exists(artccsBaseDir))
        {
            result.Warn(null, $"{artccsBaseDir}: ARTCCs directory not found; no scenario sidecars loaded");
            return result;
        }

        foreach (string artccDir in Directory.EnumerateDirectories(artccsBaseDir).Order(StringComparer.Ordinal))
        {
            string scenariosDir = Path.Combine(artccDir, "Scenarios");
            if (!Directory.Exists(scenariosDir))
            {
                continue;
            }

            string artccId = ScenarioSidecarLoadResult.NormalizeArtcc(Path.GetFileName(artccDir));
            foreach (string file in Directory.GetFiles(scenariosDir, "*.json").Order(StringComparer.Ordinal))
            {
                LoadFile(file, artccId, result);
            }
        }

        return result;
    }

    private static void LoadFile(string filePath, string artccId, ScenarioSidecarLoadResult result)
    {
        ScenarioSidecarFile? sidecarFile;
        try
        {
            sidecarFile = JsonSerializer.Deserialize<ScenarioSidecarFile>(File.ReadAllText(filePath), JsonOptions);
        }
        catch (Exception ex)
        {
            result.Warn(artccId, $"{filePath}: could not be read or parsed: {ex.Message}");
            return;
        }

        if (sidecarFile is null)
        {
            result.Warn(artccId, $"{filePath}: deserialized to null; skipping");
            return;
        }

        var warnings = new List<string>();
        ActiveRunways runways = ActiveRunwayListParser.FromTokenLists(sidecarFile.ActiveRunways, filePath, warnings);
        foreach (string warning in warnings)
        {
            result.Warn(artccId, warning);
        }

        result.Add(artccId, filePath, new ScenarioSidecar { ActiveRunways = runways });
    }

    /// <summary>
    /// The sidecar file's shape. Unknown top-level properties are ignored, so the file can carry settings a newer build
    /// understands without an older one refusing the scenario. A null list or a null entry is a warning, not a crash.
    /// </summary>
    private sealed class ScenarioSidecarFile
    {
        public Dictionary<string, List<string>?>? ActiveRunways { get; init; }
    }
}
