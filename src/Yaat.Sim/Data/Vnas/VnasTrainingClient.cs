using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Reads training scenarios from the vNAS data-api (<c>https://data-api.vnas.vatsim.net/api/training</c>): an ARTCC's
/// scenario summaries and one scenario's JSON. It only reads; no request carries a token. Each request gets 15 seconds,
/// and its result tells a failed call from an empty answer.
/// </summary>
/// <param name="http">The client to call data-api with, or null for one of its own.</param>
public sealed class VnasTrainingClient(HttpClient? http) : IDisposable
{
    /// <summary>The data-api training base URL every request goes under.</summary>
    public const string BaseUrl = "https://data-api.vnas.vatsim.net/api/training";

    private const int RequestTimeoutSeconds = 15;

    private static readonly ILogger Log = SimLog.CreateLogger<VnasTrainingClient>();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = http ?? new HttpClient();
    private readonly bool _ownsHttp = http is null;

    /// <summary>
    /// Initializes a new instance of the <see cref="VnasTrainingClient"/> class with its own HTTP client.
    /// </summary>
    public VnasTrainingClient()
        : this(http: null) { }

    /// <summary>Gets how long one request may take before it is reported as failed.</summary>
    public static TimeSpan RequestTimeout { get; } = TimeSpan.FromSeconds(RequestTimeoutSeconds);

    /// <summary>Reads the summaries of every training scenario of an ARTCC.</summary>
    /// <param name="artccId">The ARTCC id, e.g. ZOA.</param>
    /// <param name="ct">Cancels the call; a cancel is thrown, not reported as a failure.</param>
    /// <returns>The summaries, or the reason the call failed.</returns>
    public async Task<VnasFetchResult<IReadOnlyList<TrainingScenarioSummary>>> GetScenarioSummariesAsync(string artccId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artccId);
        string url = $"{BaseUrl}/scenario-summaries/by-artcc/{Uri.EscapeDataString(artccId)}";
        VnasFetchResult<string> body = await GetTextAsync(url, ct);
        if (!body.Succeeded)
        {
            return Fail<IReadOnlyList<TrainingScenarioSummary>>(body.Failure);
        }

        try
        {
            List<TrainingScenarioSummary?>? summaries = JsonSerializer.Deserialize<List<TrainingScenarioSummary?>>(body.Value, JsonOptions);
            return summaries is null
                ? Fail<IReadOnlyList<TrainingScenarioSummary>>("data-api answered null for the scenario summaries")
                : new VnasFetchResult<IReadOnlyList<TrainingScenarioSummary>>(WithoutNulls(summaries, artccId), null);
        }
        catch (JsonException ex)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Scenario summaries for {ArtccId} are not readable JSON", artccId);
            }

            return Fail<IReadOnlyList<TrainingScenarioSummary>>("data-api sent unreadable scenario summaries: " + ex.Message);
        }
    }

    /// <summary>Reads one scenario's JSON exactly as data-api sends it.</summary>
    /// <param name="scenarioId">The scenario id.</param>
    /// <param name="ct">Cancels the call; a cancel is thrown, not reported as a failure.</param>
    /// <returns>The body text, or the reason the call failed.</returns>
    public Task<VnasFetchResult<string>> GetScenarioJsonAsync(string scenarioId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scenarioId);
        return GetTextAsync($"{BaseUrl}/scenarios/{Uri.EscapeDataString(scenarioId)}", ct);
    }

    /// <summary>Disposes the HTTP client when this client created it.</summary>
    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    // The HttpClient may be shared, so the per-request limit is a token linked to the caller's, not HttpClient.Timeout.
    private async Task<VnasFetchResult<string>> GetTextAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Failed(url, "data-api answered HTTP " + (int)response.StatusCode + " (" + response.ReasonPhrase + ")", null);
            }

            return new VnasFetchResult<string>(await response.Content.ReadAsStringAsync(timeout.Token), null);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return Failed(url, "data-api did not answer within " + RequestTimeoutSeconds + " s", ex);
        }
        catch (HttpRequestException ex)
        {
            return Failed(url, "data-api could not be reached: " + ex.Message, ex);
        }
    }

    // A null element of the reply is skipped and logged; every other summary is kept in order.
    private static List<TrainingScenarioSummary> WithoutNulls(List<TrainingScenarioSummary?> summaries, string artccId)
    {
        var kept = new List<TrainingScenarioSummary>(summaries.Count);
        foreach (TrainingScenarioSummary? summary in summaries)
        {
            if (summary is not null)
            {
                kept.Add(summary);
            }
            else if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning("vNAS listed a null scenario summary for {Artcc}; skipped", artccId);
            }
        }

        return kept;
    }

    private static VnasFetchResult<T> Fail<T>(string reason) => new(default, reason);

    private static VnasFetchResult<string> Failed(string url, string reason, Exception? ex)
    {
        if (Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning(ex, "GET {Url} failed: {Reason}", url, reason);
        }

        return Fail<string>(reason);
    }
}

/// <summary>The outcome of one data-api read: the value, or why the call failed. A failure is never an empty value.</summary>
/// <typeparam name="T">The value read.</typeparam>
/// <param name="Value">The value read; null when the call failed.</param>
/// <param name="Failure">Why the call failed; null when it succeeded.</param>
public sealed record VnasFetchResult<T>(T? Value, string? Failure)
{
    /// <summary>Gets a value indicating whether the call succeeded: true with a value, false with a failure.</summary>
    /// <exception cref="InvalidOperationException">The result holds neither a value nor a failure.</exception>
    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool Succeeded
    {
        get
        {
            if (Failure is not null)
            {
                return false;
            }

            if (Value is not null)
            {
                return true;
            }

            throw new InvalidOperationException("A data-api result holds neither a value nor a failure.");
        }
    }
}

/// <summary>One training scenario as data-api's scenario-summaries endpoint lists it.</summary>
/// <param name="Id">The scenario id.</param>
/// <param name="Name">The scenario name.</param>
/// <param name="ArtccId">The ARTCC the scenario belongs to.</param>
/// <param name="PrimaryAirportId">The scenario's primary airport, when it names one.</param>
/// <param name="MinimumRating">The lowest VATSIM rating the scenario is meant for, when it names one.</param>
public sealed record TrainingScenarioSummary(string Id, string Name, string ArtccId, string? PrimaryAirportId, string? MinimumRating);
