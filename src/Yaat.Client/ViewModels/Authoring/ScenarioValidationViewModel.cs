using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>A live-validation finding as the field beside which it shows: its worst severity and its messages.</summary>
/// <param name="Severity">The worst severity of the field's findings.</param>
/// <param name="Text">The findings' messages, joined.</param>
public sealed record ScenarioFieldIssue(ScenarioEditIssueSeverity Severity, string Text)
{
    /// <summary>Gets a value indicating whether the finding is a warning or a check not run, shown in amber rather than red.</summary>
    public bool IsAdvisory => Severity != ScenarioEditIssueSeverity.Error;

    /// <summary>The field issue of some findings: the worst severity and every message; null for none.</summary>
    /// <param name="issues">The findings of one field.</param>
    /// <returns>The field issue, or null.</returns>
    public static ScenarioFieldIssue? Of(IEnumerable<ScenarioEditIssue> issues)
    {
        ScenarioEditIssue[] all = [.. issues];
        return (all.Length == 0) ? null : new ScenarioFieldIssue(all.Min(i => i.Severity), string.Join("; ", all.Select(i => i.Message)));
    }
}

/// <summary>
/// The Scenario Editor's live validation of the open working copy: <see cref="ScenarioEditChecks"/> run off the UI thread on a
/// snapshot of the document after each edit, undo, redo or reload and when the position catalog arrives, the result marshalled
/// back through the post seam. Only the latest run's result is applied, and none once the session is closed (disposed). TAXI
/// presets are routed on the layouts the given source fetches, each airport fetched once per session.
/// </summary>
/// <param name="session">The open working copy's session.</param>
/// <param name="post">Runs an action on the UI thread.</param>
/// <param name="isNavDataLoaded">Whether navdata is loaded; the checks do not run without it.</param>
/// <param name="fetchLayout">Fetches an airport's ground layout; null when it has none.</param>
/// <param name="navDataReady">
/// The startup navdata load; a run that finds navdata not loaded while it is still going runs the checks again once it ends.
/// </param>
/// <remarks>Nothing runs until <see cref="Recompute"/>.</remarks>
public sealed partial class ScenarioValidationViewModel(
    ScenarioEditSession session,
    Action<Action> post,
    Func<bool> isNavDataLoaded,
    Func<string, CancellationToken, Task<AirportGroundLayout?>> fetchLayout,
    Task navDataReady
) : ObservableObject, IDisposable
{
    private static readonly ILogger Log = AppLog.CreateLogger<ScenarioValidationViewModel>();

    // One downloader for the app's lifetime: every editor session shares its cache.
    private static readonly Lazy<AirportLayoutDownloader> SharedDownloader = new(() => new AirportLayoutDownloader());

    private readonly ScenarioEditSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly Action<Action> _post = post ?? throw new ArgumentNullException(nameof(post));
    private readonly Func<bool> _isNavDataLoaded = isNavDataLoaded ?? throw new ArgumentNullException(nameof(isNavDataLoaded));
    private readonly Func<string, CancellationToken, Task<AirportGroundLayout?>> _fetchLayout =
        fetchLayout ?? throw new ArgumentNullException(nameof(fetchLayout));
    private readonly Task _navDataReady = navDataReady ?? throw new ArgumentNullException(nameof(navDataReady));
    private readonly ConcurrentDictionary<string, Task<AirportGroundLayout?>> _layouts = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlySet<string>? _positionIds;
    private int _generation;
    private bool _disposed;
    private bool _navDataRecheckArranged;

    [ObservableProperty]
    private IReadOnlyList<ScenarioEditIssue> _issues = [];

    [ObservableProperty]
    private string? _scenarioIssue;

    /// <summary>Gets the app's layout source: the shared <see cref="AirportLayoutDownloader"/>'s cache, else data-api.</summary>
    public static Func<string, CancellationToken, Task<AirportGroundLayout?>> AppLayouts { get; } =
        (airportId, ct) => SharedDownloader.Value.GetLayoutAsync(airportId, ct);

    /// <summary>Gets the latest run, finished once its result is applied or dropped. Tests await it.</summary>
    public Task Recomputed { get; private set; } = Task.CompletedTask;

    /// <summary>Sets the position ids the position checks read (null skips them) and runs the checks again.</summary>
    /// <param name="positionIds">The ARTCC's and its neighbours' position ids; null when a config could not be read.</param>
    public void SetPositionIds(IReadOnlySet<string>? positionIds)
    {
        _positionIds = positionIds;
        Recompute();
    }

    /// <summary>Runs the checks on the document as it is now, off the UI thread; a run already going is overtaken.</summary>
    public void Recompute()
    {
        if (_disposed)
        {
            return;
        }

        if ((!_navDataRecheckArranged) && (!_navDataReady.IsCompleted) && (!_isNavDataLoaded()))
        {
            _navDataRecheckArranged = true;
            _ = RecheckWhenNavDataReadyAsync();
        }

        int generation = ++_generation;
        string json = _session.Document.Write();
        Recomputed = RunAsync(json, _positionIds, generation);
    }

    /// <summary>Drops every result still to come; the session is closed.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    // Runs the checks again on the UI thread once the startup navdata load ends, unless the session is closed by then; a
    // failed load runs nothing, so the navdata status stays.
    private async Task RecheckWhenNavDataReadyAsync()
    {
        try
        {
            await _navDataReady.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Scenario editor: the navdata load failed; {ScenarioId} is not checked again", _session.ScenarioId);
            }

            return;
        }

        if (!_disposed)
        {
            _post(Recompute);
        }
    }

    private async Task RunAsync(string json, IReadOnlySet<string>? positionIds, int generation)
    {
        IReadOnlyList<ScenarioEditIssue> issues;
        CancellationToken ct = _cts.Token;
        try
        {
            issues = await Task.Run(() => CheckAsync(json, positionIds, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Scenario editor: the live checks of {ScenarioId} failed", _session.ScenarioId);
            }

            issues =
            [
                new ScenarioEditIssue(
                    ScenarioEditIssueSeverity.Warning,
                    null,
                    null,
                    ScenarioEditFields.Scenario,
                    null,
                    $"The live checks failed: {ex.Message}"
                ),
            ];
        }

        await PostAsync(() => Apply(issues, generation)).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ScenarioEditIssue>> CheckAsync(string json, IReadOnlySet<string>? positionIds, CancellationToken ct)
    {
        Scenario scenario = ScenarioDocument.Read(json).Scenario;
        IAirportGroundData? layouts = null;
        if (_isNavDataLoaded())
        {
            layouts = await FetchLayoutsAsync(scenario, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return ScenarioEditChecks.Run(scenario, new ScenarioEditInputs(_isNavDataLoaded, positionIds, layouts));
    }

    // The layouts of every airport an aircraft with a TAXI preset may start at, each fetched once per session.
    private async Task<IAirportGroundData> FetchLayoutsAsync(Scenario scenario, CancellationToken ct)
    {
        string[] airports =
        [
            .. scenario
                .Aircraft.Where(a => a.PresetCommands.Any(p => p.Command.Contains("TAXI", StringComparison.OrdinalIgnoreCase)))
                .SelectMany(a => new[] { a.AirportId, scenario.PrimaryAirportId, a.FlightPlan?.Departure, a.FlightPlan?.Destination })
                .OfType<string>()
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(KeyOf)
                .Distinct(StringComparer.Ordinal),
        ];
        var fetched = new Dictionary<string, AirportGroundLayout?>(StringComparer.Ordinal);
        foreach (string airport in airports)
        {
            fetched[airport] = await _layouts.GetOrAdd(airport, id => FetchOneAsync(id, ct)).ConfigureAwait(false);
        }

        return new FetchedLayouts(fetched);
    }

    private async Task<AirportGroundLayout?> FetchOneAsync(string airportId, CancellationToken ct)
    {
        try
        {
            return await _fetchLayout(airportId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Scenario editor: fetching the layout of {AirportId} failed; its TAXI presets are not checked", airportId);
            }

            return null;
        }
    }

    private static string KeyOf(string airportId) => AirportLayoutDownloader.ToFaaCode(airportId.Trim()).ToUpperInvariant();

    private void Apply(IReadOnlyList<ScenarioEditIssue> issues, int generation)
    {
        if (_disposed || (generation != _generation))
        {
            return;
        }

        ScenarioIssue = issues.FirstOrDefault(i => i.Field == ScenarioEditFields.Scenario)?.Message;
        Issues = issues;
    }

    // Runs the action through the post seam and finishes when it has run.
    private Task PostAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    // The layouts one run fetched, by FAA code.
    private sealed class FetchedLayouts(Dictionary<string, AirportGroundLayout?> layouts) : IAirportGroundData
    {
        public AirportGroundLayout? GetLayout(string airportId) => layouts.GetValueOrDefault(KeyOf(airportId));

        public string? GetSourceGeoJson(string airportId) => null;
    }
}
