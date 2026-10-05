using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The import target of the Import / Export hub opened from Settings: every item is staged in the open window
/// (<see cref="SettingsViewModel"/>), so Apply or OK commits it and Cancel drops it. One target serves the window for its
/// whole session.
/// </summary>
/// <remarks>
/// Favorites stage in a copy of the user's store, made in a folder of its own under <see cref="StagedFavoritesFolder"/>
/// at the first favorites import: the hub plans and imports against the copy, so it reports what the import did and a
/// second import sees the first. Each import's file, with every generated id already written into it, and the sets it
/// loads are staged in the window, and Apply imports them into the user's store and preferences in the order staged,
/// giving the same ids. The copy is deleted after Apply and when the target is disposed (the window closes). Its folder
/// holds the id of the process that made it (<see cref="OwnerMarkerFileName"/>): the first copy of a session deletes the
/// copies a crash left behind, and leaves alone each one whose process is still running.
/// </remarks>
public sealed class SettingsViewModelImportTarget : ISettingsImportTarget, IDisposable
{
    /// <summary>The folder under the app data root that holds each Settings session's staged copy of the favorites.</summary>
    public const string StagedFavoritesFolderName = "settings-staged-favorites";

    /// <summary>The file in each staged copy's folder that holds the id of the process that made it.</summary>
    public const string OwnerMarkerFileName = ".yaat-pid";

    private static readonly ILogger Log = AppLog.CreateLogger<SettingsViewModelImportTarget>();

    private readonly SettingsViewModel _settings;
    private readonly FavoriteStore _favorites;
    private FavoriteStore? _stagedFavorites;
    private string? _stagedFavoritesRoot;
    private bool _sweptLeftovers;

    /// <summary>Each staged import's sets to load and whether it replaces the loaded sets, in the order staged.</summary>
    private readonly List<(List<string> SetIds, bool Replace)> _stagedLoads = [];

    /// <summary>Builds the target over the open Settings window.</summary>
    /// <param name="settings">The open Settings window's view model.</param>
    /// <param name="favorites">The user's favorites store, which Apply imports the staged favorites into.</param>
    public SettingsViewModelImportTarget(SettingsViewModel settings, FavoriteStore favorites)
    {
        _settings = settings;
        _favorites = favorites;
        _settings.Applied += DiscardStagedFavorites;
    }

    /// <summary>The folder holding the staged copies of the favorites store.</summary>
    public static string StagedFavoritesFolder => YaatPaths.Combine(StagedFavoritesFolderName);

    public IReadOnlyList<SavedMacro> Macros => _settings.StagedMacros;

    public IReadOnlyList<SavedLayout> Layouts => _settings.StagedLayouts;

    /// <summary>The staged copy of the favorites store, made from the user's store on first use.</summary>
    public FavoriteStore Favorites => _stagedFavorites ??= CopyUserFavorites();

    /// <summary>The store a favorites export from this window reads: the staged copy once an import has made one, else the user's.</summary>
    public FavoriteStore FavoritesToExport => _stagedFavorites ?? _favorites;

    public FavoriteImportResult? ImportFavoritesFile(string fileName, byte[] content, FavoriteImportMode mode)
    {
        FavoriteImportResult? staged;
        using (var stream = new MemoryStream(content))
        {
            staged = FavoriteExport.ImportFile(Favorites, fileName, stream, mode);
        }

        if (staged is not null)
        {
            _settings.StageFavoritesWrite(_ => ImportIntoUserStore(fileName, content, mode));
        }

        return staged;
    }

    public void ReplaceMacros(IReadOnlyList<SavedMacro> macros) => _settings.StageMacros(macros);

    public void ReplaceLayouts(IReadOnlyList<SavedLayout> layouts) => _settings.StageLayouts(layouts);

    public int ApplyVerbs(CommandSchemeImport verbs) => _settings.StageVerbs(verbs);

    public void ReplaceGridLayout(SavedGridLayout layout) => _settings.StageGridLayout(layout);

    public PreferencesImportResult ReplacePreferences(JsonObject preferences) => _settings.StagePreferences(preferences);

    public void LoadImportedFavoriteSets(IReadOnlyList<string> setIds, bool replace)
    {
        List<string> ids = [.. setIds];
        _stagedLoads.Add((ids, replace));
        _settings.StageFavoritesWrite(preferences => new UserPreferencesImportTarget(preferences, _favorites).LoadImportedFavoriteSets(ids, replace));
    }

    /// <summary>
    /// The loaded favorite sets as Apply would leave them: <paramref name="loaded"/> with each staged import's sets to load
    /// applied in the order staged, a Replace taking the place of the list and a Merge adding the sets not loaded yet.
    /// </summary>
    /// <param name="loaded">The loaded favorite set ids the preferences hold now.</param>
    public IReadOnlyList<string> LoadedFavoriteSetIdsToExport(IReadOnlyList<string> loaded)
    {
        List<string> ids = [.. loaded];
        foreach ((List<string> setIds, bool replace) in _stagedLoads)
        {
            if (replace)
            {
                ids = [.. setIds];
                continue;
            }

            ids.AddRange(setIds.Where(id => !ids.Contains(id, StringComparer.OrdinalIgnoreCase)));
        }

        return ids;
    }

    /// <summary>Stops following the window's Apply and deletes the staged copy of the favorites.</summary>
    public void Dispose()
    {
        _settings.Applied -= DiscardStagedFavorites;
        DiscardStagedFavorites();
    }

    private void ImportIntoUserStore(string fileName, byte[] content, FavoriteImportMode mode)
    {
        using var stream = new MemoryStream(content);
        if (FavoriteExport.ImportFile(_favorites, fileName, stream, mode) is null)
        {
            Log.LogWarning("The staged favorites file {File} imported into the staged copy but not into the favorites store", fileName);
        }
    }

    // A library export of the user's store holds every set and favorite with its id, each as the store's own file, so
    // unpacking it into the store's folders (sets/ and commands/) gives a store with the same ids, Global's included. A
    // copy that fails partway deletes its folder before the failure goes on, so a retry leaves no orphan behind.
    private FavoriteStore CopyUserFavorites()
    {
        if (!_sweptLeftovers)
        {
            DeleteLeftoverCopies();
            _sweptLeftovers = true;
        }

        string root = Path.Combine(StagedFavoritesFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FavoriteStore copy = FillCopy(root);
            _stagedFavoritesRoot = root;
            return copy;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Staging a copy of the favorites store in {Folder} failed; deleting the partial copy", root);
            DeleteFolder(root);
            throw;
        }
    }

    private FavoriteStore FillCopy(string root)
    {
        File.WriteAllText(Path.Combine(root, OwnerMarkerFileName), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.Combine(root, "sets"));
        Directory.CreateDirectory(Path.Combine(root, "commands"));

        using var library = new MemoryStream();
        FavoriteExport.ExportLibrary(_favorites, [], library);
        library.Position = 0;
        using var zip = new ZipArchive(library, ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string? folder =
                entry.FullName.StartsWith("sets/", StringComparison.Ordinal) ? "sets"
                : entry.FullName.StartsWith("favorites/", StringComparison.Ordinal) ? "commands"
                : null;
            if (folder is not null)
            {
                entry.ExtractToFile(Path.Combine(root, folder, entry.Name));
            }
        }

        Log.LogDebug("Staged a copy of the favorites store in {Folder}", root);
        return new FavoriteStore(root);
    }

    // Copies left by a session that ended without closing its window (a crash): each copy whose owner marker names no
    // running process. A copy owned by a running process belongs to a Settings window open there (another YAAT, or an
    // earlier window of this one still deleting it) and stays; this session has made none yet.
    private static void DeleteLeftoverCopies()
    {
        if (!Directory.Exists(StagedFavoritesFolder))
        {
            return;
        }

        foreach (string leftover in Directory.EnumerateDirectories(StagedFavoritesFolder))
        {
            if (!IsOwnedByARunningProcess(leftover))
            {
                DeleteFolder(leftover);
            }
        }
    }

    // A copy with no marker, or a marker that holds no process id, has no owner.
    private static bool IsOwnedByARunningProcess(string folder)
    {
        string marker = Path.Combine(folder, OwnerMarkerFileName);
        if (!File.Exists(marker))
        {
            return false;
        }

        string text;
        try
        {
            text = File.ReadAllText(marker).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Could not read the owner of the staged favorites copy {Folder}; keeping it for a later session", folder);
            return true;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int processId) && IsRunning(processId);
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // GetProcessById: no process with this id is running.
            return false;
        }
        catch (InvalidOperationException)
        {
            // The process exited between the lookup and the check.
            return false;
        }
        catch (Win32Exception ex)
        {
            Log.LogDebug(ex, "Could not check whether process {ProcessId} is running; treating it as running", processId);
            return true;
        }
    }

    private void DiscardStagedFavorites()
    {
        _stagedLoads.Clear();
        _stagedFavorites = null;
        if (_stagedFavoritesRoot is { } root)
        {
            DeleteFolder(root);
            _stagedFavoritesRoot = null;
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Could not delete the staged favorites copy {Folder}; a later YAAT session deletes it", folder);
        }
    }
}
