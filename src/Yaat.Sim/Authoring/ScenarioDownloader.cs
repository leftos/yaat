using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Authoring;

/// <summary>
/// Downloads every training scenario of an ARTCC from data-api into a <see cref="LocalScenarioStore"/>: the list first,
/// then each scenario with at most four requests in flight, each applied to the store as it lands. A failed scenario is
/// listed by id and reason while the others still land; a failed list call changes nothing on disk. A cancel keeps what
/// already arrived and is thrown.
/// </summary>
/// <param name="client">The data-api client to read through.</param>
/// <param name="store">The store the scenarios land in.</param>
public sealed class ScenarioDownloader(VnasTrainingClient client, LocalScenarioStore store)
{
    /// <summary>The most scenario requests in flight at once.</summary>
    public const int MaxInFlight = 4;

    private static readonly ILogger Log = SimLog.CreateLogger<ScenarioDownloader>();

    private readonly VnasTrainingClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly LocalScenarioStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Downloads every scenario data-api lists for <paramref name="artccId"/> into the store.</summary>
    /// <param name="artccId">The ARTCC id, e.g. ZOA.</param>
    /// <param name="progress">Told after each scenario is done, landed or failed; may be null.</param>
    /// <param name="ct">Cancels the download; what already landed stays on disk.</param>
    /// <returns>What landed, what was unchanged, changed or gone, and every failure.</returns>
    public async Task<DownloadReport> DownloadArtccAsync(string artccId, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!StoreFiles.IsValidId(artccId))
        {
            return DownloadReport.ListFailed(artccId ?? "", "invalid ARTCC id");
        }

        VnasFetchResult<IReadOnlyList<TrainingScenarioSummary>> list = await _client.GetScenarioSummariesAsync(artccId, ct);
        if (!list.Succeeded)
        {
            return DownloadReport.ListFailed(artccId, list.Failure);
        }

        // An id listed twice is fetched once, so it lands in one list.
        TrainingScenarioSummary[] summaries = [.. list.Value.DistinctBy(s => s.Id, StringComparer.Ordinal)];
        var tally = new Tally();
        int completed = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxInFlight, CancellationToken = ct };
        await Parallel.ForEachAsync(
            summaries,
            options,
            async (summary, token) =>
            {
                await DownloadOneAsync(artccId, summary, tally, token);
                progress?.Report(new DownloadProgress(Interlocked.Increment(ref completed), summaries.Length, summary.Id));
            }
        );

        string[] listed = [.. summaries.Select(s => s.Id).Where(StoreFiles.IsValidId)];
        MarkGoneResult gone = _store.MarkGoneFromServer(artccId, listed);
        return tally.ToReport(artccId, gone);
    }

    private async Task DownloadOneAsync(string artccId, TrainingScenarioSummary summary, Tally tally, CancellationToken ct)
    {
        string id = summary.Id ?? "";
        string name = summary.Name ?? "";
        if (!StoreFiles.IsValidId(id))
        {
            tally.Fail(id, name, "invalid id");
            return;
        }

        VnasFetchResult<string> body = await _client.GetScenarioJsonAsync(id, ct);
        if (!body.Succeeded)
        {
            tally.Fail(id, name, body.Failure);
            return;
        }

        try
        {
            tally.Add(id, name, _store.ApplyServerBody(artccId, id, body.Value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Scenario {Id} of {ArtccId} could not be stored", id, artccId);
            }

            tally.Fail(id, name, "could not be stored: " + ex.Message);
        }
    }

    // Collects the outcomes the parallel workers report; every member is taken under the one lock. Each problem file is
    // kept once, since every scenario's apply re-reads the same sidecars.
    private sealed class Tally
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<ServerApplyOutcome, List<string>> _byOutcome = [];
        private readonly List<DownloadFailure> _failed = [];
        private readonly List<StoreProblem> _problems = [];
        private readonly HashSet<string> _problemFiles = new(StringComparer.Ordinal);

        public void Fail(string id, string name, string reason)
        {
            lock (_gate)
            {
                _failed.Add(new DownloadFailure(id, name, reason));
            }
        }

        public void Add(string id, string name, ServerApplyResult result)
        {
            lock (_gate)
            {
                AddProblems(result.Problems);
                if (result.Outcome == ServerApplyOutcome.Rejected)
                {
                    _failed.Add(new DownloadFailure(id, name, result.Reason ?? "rejected"));
                    return;
                }

                if (!_byOutcome.TryGetValue(result.Outcome, out List<string>? ids))
                {
                    ids = [];
                    _byOutcome[result.Outcome] = ids;
                }

                ids.Add(id);
            }
        }

        public DownloadReport ToReport(string artccId, MarkGoneResult gone)
        {
            lock (_gate)
            {
                AddProblems(gone.Problems);
                return new DownloadReport(
                    artccId,
                    null,
                    Ids(ServerApplyOutcome.Added),
                    Ids(ServerApplyOutcome.Updated),
                    Ids(ServerApplyOutcome.Unchanged),
                    Ids(ServerApplyOutcome.ServerChanged),
                    [.. gone.Missing.Order(StringComparer.Ordinal)],
                    [.. _failed.OrderBy(f => f.Id, StringComparer.Ordinal)],
                    [.. _problems]
                );
            }
        }

        // Called under _gate.
        private void AddProblems(IEnumerable<StoreProblem> problems)
        {
            foreach (StoreProblem problem in problems)
            {
                if (_problemFiles.Add(problem.FileName))
                {
                    _problems.Add(problem);
                }
            }
        }

        private string[] Ids(ServerApplyOutcome outcome) =>
            _byOutcome.TryGetValue(outcome, out List<string>? ids) ? [.. ids.Order(StringComparer.Ordinal)] : [];
    }
}

/// <summary>How far a download has got.</summary>
/// <param name="Completed">Scenarios done so far, landed or failed.</param>
/// <param name="Total">Scenarios the ARTCC lists.</param>
/// <param name="ScenarioId">The scenario just done.</param>
public sealed record DownloadProgress(int Completed, int Total, string ScenarioId);

/// <summary>One scenario that did not land.</summary>
/// <param name="Id">The scenario id as listed.</param>
/// <param name="Name">The scenario name as listed.</param>
/// <param name="Reason">Why it did not land.</param>
public sealed record DownloadFailure(string Id, string Name, string Reason);

/// <summary>What <see cref="ScenarioDownloader.DownloadArtccAsync"/> did, by scenario id; each listed id is in exactly
/// one of Downloaded, Updated, Unchanged, ServerChanged and Failed.</summary>
/// <param name="ArtccId">The ARTCC.</param>
/// <param name="ListFailure">Why the list call failed, in which case nothing else ran; null when it succeeded.</param>
/// <param name="Downloaded">New scenarios, written to the mirror.</param>
/// <param name="Updated">Scenarios whose server copy changed, the mirror replaced; none has a working copy.</param>
/// <param name="Unchanged">Scenarios equal to the mirror as JSON; nothing written.</param>
/// <param name="ServerChanged">Scenarios whose server copy changed, the mirror replaced, and whose working copies are now server-changed.</param>
/// <param name="Missing">Mirrored scenarios the server no longer lists; kept on disk and flagged.</param>
/// <param name="Failed">Every scenario that did not land, with its reason.</param>
/// <param name="Problems">File problems the store found on the way.</param>
public sealed record DownloadReport(
    string ArtccId,
    string? ListFailure,
    IReadOnlyList<string> Downloaded,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> ServerChanged,
    IReadOnlyList<string> Missing,
    IReadOnlyList<DownloadFailure> Failed,
    IReadOnlyList<StoreProblem> Problems
)
{
    /// <summary>Gets a value indicating whether the list call failed (data-api offline or refusing), so nothing ran.</summary>
    public bool Offline => ListFailure is not null;

    /// <summary>A report for a list call that failed: nothing else ran and nothing on disk changed.</summary>
    /// <param name="artccId">The ARTCC.</param>
    /// <param name="reason">Why the list call failed.</param>
    /// <returns>The report.</returns>
    public static DownloadReport ListFailed(string artccId, string reason) => new(artccId, reason, [], [], [], [], [], [], []);
}
