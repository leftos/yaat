using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Authoring;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>An ARTCC's browser rows as one store load found them, with every store problem as a notice line.</summary>
/// <param name="ArtccId">The ARTCC.</param>
/// <param name="Server">The server rows, by name.</param>
/// <param name="Local">The working-copy rows, by name.</param>
/// <param name="Problems">Every store problem, as "file: reason".</param>
internal sealed record ScenarioBrowserSnapshot(
    string ArtccId,
    IReadOnlyList<ScenarioBrowserRowViewModel> Server,
    IReadOnlyList<ScenarioBrowserRowViewModel> Local,
    IReadOnlyList<string> Problems
);

/// <summary>Builds the browser rows from <see cref="LocalScenarioStore.Load"/>; runs off the UI thread.</summary>
internal static class ScenarioBrowserRows
{
    // StoreProblem.FileName names a file relative to the ARTCC folder with LocalScenarioStore's private folder names
    // and sidecar suffix; these must match them, or every row loses its problem text.
    private const string ServerFolder = "server";
    private const string LocalFolder = "local";
    private const string SidecarSuffix = ".meta.json";

    private static readonly ILogger Log = AppLog.CreateLogger(nameof(ScenarioBrowserRows));

    /// <summary>Loads an ARTCC's store and builds its rows; a store that cannot be read gives no rows and one notice.</summary>
    /// <param name="store">The store.</param>
    /// <param name="artccId">The ARTCC.</param>
    /// <returns>The rows and the store's problems.</returns>
    public static ScenarioBrowserSnapshot Read(LocalScenarioStore store, string artccId)
    {
        ArtccStoreListing listing;
        try
        {
            listing = store.Load(artccId);
        }
        catch (Exception ex)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Listing the scenarios of {ArtccId} failed", artccId);
            }

            return new ScenarioBrowserSnapshot(artccId, [], [], [$"{artccId}: {ex.Message}"]);
        }

        MirrorListing[] mirrorList = [.. listing.Mirrors.DistinctBy(m => m.Mirror.Id, StringComparer.Ordinal)];
        Dictionary<string, MirrorListing> mirrors = mirrorList.ToDictionary(m => m.Mirror.Id, StringComparer.Ordinal);
        HashSet<string> copyIds = [.. listing.WorkingCopies.Select(c => c.Id)];
        ILookup<string, string> problems = listing.Problems.ToLookup(p => p.FileName, p => p.Reason, StringComparer.Ordinal);
        ScenarioBrowserRowViewModel[] server = [.. mirrorList.Select(m => ServerRow(m, copyIds.Contains(m.Mirror.Id), problems))];
        ScenarioBrowserRowViewModel[] local = [.. listing.WorkingCopies.Select(c => LocalRow(store, artccId, c, mirrors, problems))];
        return new ScenarioBrowserSnapshot(artccId, ByName(server), ByName(local), [.. listing.Problems.Select(p => $"{p.FileName}: {p.Reason}")]);
    }

    private static ScenarioBrowserRowViewModel ServerRow(MirrorListing listing, bool hasCopy, ILookup<string, string> problems)
    {
        List<ScenarioBadge> badges = [];
        if (listing.Mirror.GoneFromServer)
        {
            badges.Add(ScenarioBadge.GoneFromServer);
        }

        if (listing.Corrupt)
        {
            badges.Add(ScenarioBadge.Corrupt);
        }

        string id = listing.Mirror.Id;
        return new ScenarioBrowserRowViewModel
        {
            Id = id,
            Name = string.IsNullOrEmpty(listing.Mirror.Name) ? id : listing.Mirror.Name,
            Kind = ScenarioRowKind.Server,
            Badges = badges,
            Problem = ProblemOf(problems, $"{ServerFolder}/{id}.json"),
            HasWorkingCopy = hasCopy,
        };
    }

    private static ScenarioBrowserRowViewModel LocalRow(
        LocalScenarioStore store,
        string artccId,
        WorkingCopyEntry copy,
        Dictionary<string, MirrorListing> mirrors,
        ILookup<string, string> problems
    )
    {
        bool originGone =
            (copy.OriginId is not null) && mirrors.TryGetValue(copy.OriginId, out MirrorListing? origin) && origin.Mirror.GoneFromServer;
        return new ScenarioBrowserRowViewModel
        {
            Id = copy.Id,
            Name = (copy.Corrupt ? null : ReadName(store, artccId, copy.Id)) ?? copy.Id,
            Kind = ScenarioRowKind.WorkingCopy,
            Badges = LocalBadges(copy, originGone),
            Problem = ProblemOf(problems, $"{LocalFolder}/{copy.Id}.json", $"{LocalFolder}/{copy.Id}{SidecarSuffix}"),
            OriginId = copy.OriginId,
            BaseVersion = copy.BaseVersion,
        };
    }

    private static List<ScenarioBadge> LocalBadges(WorkingCopyEntry copy, bool originGone)
    {
        (bool On, ScenarioBadge Badge)[] candidates =
        [
            (copy.Modified, ScenarioBadge.Modified),
            (copy.ServerChanged, ScenarioBadge.ServerChanged),
            (originGone, ScenarioBadge.GoneFromServer),
            (copy.Corrupt, ScenarioBadge.Corrupt),
            (copy.OriginId is null, ScenarioBadge.LocalOnly),
            (copy.UnexportedChanges, ScenarioBadge.Unexported),
        ];
        return [.. candidates.Where(c => c.On).Select(c => c.Badge)];
    }

    // The working copy's name field, or null (logged) when the file cannot be read or has no text name.
    private static string? ReadName(LocalScenarioStore store, string artccId, string id)
    {
        try
        {
            JsonNode? name = JsonNode.Parse(store.ReadWorkingCopyText(artccId, id)) is JsonObject scenario ? scenario["name"] : null;
            if (name?.GetValueKind() == JsonValueKind.String)
            {
                return name.GetValue<string>();
            }

            LogNoName(artccId, id, "it has no text name");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogNoName(artccId, id, ex.Message);
            return null;
        }
    }

    private static void LogNoName(string artccId, string id, string reason)
    {
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Working copy {Id} of {ArtccId} is listed by its id: {Reason}", id, artccId, reason);
        }
    }

    private static string? ProblemOf(ILookup<string, string> problems, params string[] fileNames)
    {
        string[] reasons = [.. fileNames.SelectMany(f => problems[f])];
        return reasons.Length == 0 ? null : string.Join("; ", reasons);
    }

    private static ScenarioBrowserRowViewModel[] ByName(ScenarioBrowserRowViewModel[] rows) =>
        [.. rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id, StringComparer.Ordinal)];
}
