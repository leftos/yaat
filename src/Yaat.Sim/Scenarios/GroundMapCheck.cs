using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Scenarios;

/// <summary>
/// Checks one airport's vNAS ground map for the scenario validator: whether vNAS serves a map, and whether
/// <see cref="GeoJsonParser"/> can build a layout from it and which features it had to drop.
/// </summary>
public static class GroundMapCheck
{
    private static readonly ILogger Log = SimLog.CreateLogger("GroundMapCheck");

    /// <summary>Classifies a fetched map and, when there is content, parses it the way the runtime does (Standard fillets).</summary>
    /// <param name="faaId">The airport's FAA id, as the layout downloader keys maps.</param>
    /// <param name="fetched">The outcome of <see cref="AirportLayoutDownloader.FetchGeoJsonAsync"/> for that airport.</param>
    /// <returns>
    /// <see cref="GroundMapOutcome.NoMap"/> for a 404 with no cached copy, <see cref="GroundMapOutcome.FetchFailed"/> for any other
    /// fetch with no content, <see cref="GroundMapOutcome.ParseFailed"/> with the exception when the parser throws, otherwise
    /// <see cref="GroundMapOutcome.Ok"/> with the dropped features as issues.
    /// </returns>
    public static GroundMapCheckResult Check(string faaId, HttpCacheResult fetched)
    {
        if (fetched.Content is null)
        {
            GroundMapOutcome outcome = fetched.NotFound ? GroundMapOutcome.NoMap : GroundMapOutcome.FetchFailed;
            return new GroundMapCheckResult(faaId, outcome, Error: null, Issues: [], Exception: null);
        }

        try
        {
            IReadOnlyList<GroundMapIssue> issues = GeoJsonParser.ParseWithDiagnostics(faaId.ToLowerInvariant(), fetched.Content, faaId).Issues;
            return new GroundMapCheckResult(faaId, GroundMapOutcome.Ok, Error: null, issues, Exception: null);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "The vNAS ground map for {Airport} could not be parsed", faaId);
            return new GroundMapCheckResult(faaId, GroundMapOutcome.ParseFailed, $"{ex.GetType().Name}: {ex.Message}", Issues: [], ex);
        }
    }
}

/// <summary>How one airport's ground-map check came out.</summary>
public enum GroundMapOutcome
{
    /// <summary>The map parsed; any dropped features are advisories.</summary>
    Ok,

    /// <summary>vNAS has no map for the airport (404) — normal for many scenario airports.</summary>
    NoMap,

    /// <summary>vNAS could not be reached and no cached copy exists — an advisory.</summary>
    FetchFailed,

    /// <summary>vNAS serves a map the parser cannot read at all — a failure.</summary>
    ParseFailed,
}

/// <summary>The result of <see cref="GroundMapCheck.Check"/> for one airport.</summary>
/// <param name="Airport">The airport's FAA id.</param>
/// <param name="Outcome">How the check came out.</param>
/// <param name="Error">For <see cref="GroundMapOutcome.ParseFailed"/>, the exception's type name and message; otherwise null.</param>
/// <param name="Issues">For <see cref="GroundMapOutcome.Ok"/>, the features the parser dropped; otherwise empty.</param>
/// <param name="Exception">
/// For <see cref="GroundMapOutcome.ParseFailed"/>, the exception the parser threw, stack trace included; otherwise null. Left out of
/// JSON, which carries <paramref name="Error"/>.
/// </param>
public sealed record GroundMapCheckResult(
    string Airport,
    GroundMapOutcome Outcome,
    string? Error,
    IReadOnlyList<GroundMapIssue> Issues,
    [property: JsonIgnore] Exception? Exception
);
