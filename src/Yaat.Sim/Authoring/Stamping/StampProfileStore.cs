using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring.Stamping;

/// <summary>
/// The saved stamp profiles, one JSON file per profile named by its id, under <c>stamp-profiles/</c> in the YAAT data folder.
/// A corrupt file is reported by name and never stops the others from loading. Ids compare ignoring case, as the file
/// systems YAAT runs on do: ids that differ only by case are the same profile.
/// </summary>
/// <param name="rootDir">The folder holding the profiles; null for <c>stamp-profiles/</c> under the YAAT data folder.</param>
public sealed class StampProfileStore(string? rootDir)
{
    private const string Extension = ".json";

    private static readonly ILogger Log = SimLog.CreateLogger<StampProfileStore>();

    /// <summary>Initializes a new instance of the <see cref="StampProfileStore"/> class over the default folder.</summary>
    public StampProfileStore()
        : this(null) { }

    /// <summary>Gets the folder holding the profiles: the one given, or <c>stamp-profiles/</c> under the YAAT data folder.</summary>
    public string Root { get; } = Path.GetFullPath(rootDir ?? YaatPaths.Combine("stamp-profiles"));

    /// <summary>Lists every readable profile, sorted by name then id, and a problem for each file that is not one.</summary>
    /// <returns>The profiles and the problems.</returns>
    public StampProfileListing List()
    {
        var profiles = new List<StampProfile>();
        var problems = new List<StoreProblem>();
        if (Directory.Exists(Root))
        {
            foreach (string path in Directory.EnumerateFiles(Root, "*" + Extension).Order(StringComparer.OrdinalIgnoreCase))
            {
                string fileName = Path.GetFileName(path);
                if (TryRead(path, out StampProfile? profile, out string reason))
                {
                    profiles.Add(profile);
                    continue;
                }

                problems.Add(new StoreProblem(fileName, reason));
                if (Log.IsEnabled(LogLevel.Warning))
                {
                    Log.LogWarning("Stamp profile {File} is not readable: {Reason}", fileName, reason);
                }
            }
        }

        profiles.Sort(CompareByNameThenId);
        return new StampProfileListing(profiles, problems);
    }

    /// <summary>Writes <paramref name="profile"/> to its file in one step, replacing a profile with the same id.</summary>
    /// <param name="profile">The profile to save.</param>
    /// <exception cref="ArgumentException">The profile's id cannot name a file.</exception>
    public void Save(StampProfile profile) => StoreFiles.WriteAtomically(PathFor(profile.Id), profile.ToJson());

    /// <summary>Reads the profile with <paramref name="id"/>.</summary>
    /// <param name="id">The profile id.</param>
    /// <returns>The profile, or null when no file has that id.</returns>
    /// <exception cref="ArgumentException">The id cannot name a file.</exception>
    /// <exception cref="InvalidDataException">The file is not a readable profile.</exception>
    public StampProfile? Load(string id)
    {
        string path = PathFor(id);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Read(path);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            throw new InvalidDataException($"Stamp profile {Path.GetFileName(path)} is not readable: {ex.Message}", ex);
        }
    }

    /// <summary>Deletes the profile with <paramref name="id"/>.</summary>
    /// <param name="id">The profile id.</param>
    /// <returns>Whether a file was deleted.</returns>
    /// <exception cref="ArgumentException">The id cannot name a file.</exception>
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

    private static int CompareByNameThenId(StampProfile a, StampProfile b)
    {
        int byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        return byName != 0 ? byName : StringComparer.Ordinal.Compare(a.Id, b.Id);
    }

    private static bool TryRead(string path, [NotNullWhen(true)] out StampProfile? profile, out string reason)
    {
        try
        {
            profile = Read(path);
            reason = "";
            return true;
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            profile = null;
            reason = ex.Message;
            return false;
        }
    }

    private static StampProfile Read(string path)
    {
        var profile = StampProfile.FromJson(File.ReadAllText(path));
        string expectedId = Path.GetFileNameWithoutExtension(path);
        return string.Equals(profile.Id, expectedId, StringComparison.OrdinalIgnoreCase)
            ? profile
            : throw new InvalidDataException($"its id '{profile.Id}' does not match the file name");
    }

    private static bool IsReadFailure(Exception ex) =>
        ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException;

    private string PathFor(string id)
    {
        if (!StoreFiles.IsValidId(id))
        {
            throw new ArgumentException($"'{id}' cannot name a stamp profile file: use letters, digits, '_' and '-'.", nameof(id));
        }

        string fileName = id + Extension;
        if (Directory.Exists(Root))
        {
            string? existing = Directory
                .EnumerateFiles(Root, "*" + Extension)
                .FirstOrDefault(p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }
        }

        return Path.Combine(Root, fileName);
    }
}

/// <summary>What <see cref="StampProfileStore.List"/> found.</summary>
/// <param name="Profiles">The readable profiles, sorted by name then id.</param>
/// <param name="Problems">One entry per file that is not a readable profile, by file name.</param>
public sealed record StampProfileListing(IReadOnlyList<StampProfile> Profiles, IReadOnlyList<StoreProblem> Problems);
