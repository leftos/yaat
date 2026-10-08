using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;

namespace Yaat.Sim.Simulation;

/// <summary>The outcome of reading a runway-token list: the ordered ends, or the first token that failed and why.</summary>
public sealed record ActiveRunwayParseResult(IReadOnlyList<ActiveRunway> Runways, string? Error)
{
    public bool IsSuccess => Error is null;

    public static ActiveRunwayParseResult Success(IReadOnlyList<ActiveRunway> runways) => new(runways, null);

    public static ActiveRunwayParseResult Failed(string error) => new([], error);
}

/// <summary>
/// Reads the active-runway list a command, a scenario sidecar or a snapshot carries. A token is a bare designator
/// (<c>30</c>, used for both departures and arrivals) or one prefixed <c>D</c> for departures or <c>A</c> for arrivals
/// (<c>D28L</c>, <c>A28R</c>). Tokens are read case-insensitively and the designator is normalized to the zero-padded
/// form the navigation database matches.
/// </summary>
public static partial class ActiveRunwayListParser
{
    /// <summary>The characters that separate runway tokens: space, tab, comma, carriage return and line feed.</summary>
    public static readonly char[] Separators = [' ', '\t', '\r', '\n', ','];

    /// <summary>
    /// Reads <paramref name="text"/> — tokens separated by any mix of commas, spaces, tabs and newlines — for the
    /// airport, rejecting any end the navigation database does not know there so an instructor's typo is caught before
    /// it reaches the room. One bad token and the whole list is refused.
    /// </summary>
    public static ActiveRunwayParseResult Parse(string airportId, string text, NavigationDatabase navDb)
    {
        string airport = NavigationDatabase.NormalizeAirport(airportId);
        var runways = new List<ActiveRunway>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryReadOne(token, out ActiveRunway? runway, out string? error))
            {
                return ActiveRunwayParseResult.Failed(error);
            }

            if (!seen.Add(runway.Designator))
            {
                return ActiveRunwayParseResult.Failed($"Runway {runway.Designator} listed twice");
            }

            if (navDb.GetRunway(airport, runway.Designator) is null)
            {
                return ActiveRunwayParseResult.Failed($"Unknown runway {token} at {airport}");
            }

            runways.Add(runway);
        }

        return ActiveRunwayParseResult.Success(runways);
    }

    /// <summary>
    /// The inverse of <see cref="FromTokenLists"/>: every airport naming at least one end, keyed by its id, each end as
    /// its token (<see cref="ActiveRunway.ToToken"/>) in listed order. Empty when <paramref name="runways"/> names none.
    /// </summary>
    public static Dictionary<string, List<string>?> ToTokenLists(ActiveRunways runways)
    {
        var byAirport = new Dictionary<string, List<string>?>(StringComparer.Ordinal);
        foreach (string airport in runways.Airports)
        {
            byAirport[airport] = [.. runways.For(airport).Select(runway => runway.ToToken())];
        }

        return byAirport;
    }

    /// <summary>
    /// Builds the active runways from a token list per airport — the shape a scenario sidecar and a snapshot both carry
    /// — with no navigation-database check, because the ends were valid wherever the tokens were written. Every airport
    /// is read on its own: a null list, a null entry or a token that does not parse drops <em>that</em> airport and adds
    /// a warning naming <paramref name="source"/> and the airport, and every other airport (and every other file) still
    /// loads. Each list element is exactly one token, so <c>["28L 28R"]</c> is one bad entry, not two good ones.
    /// </summary>
    public static ActiveRunways FromTokenLists(IReadOnlyDictionary<string, List<string>?>? byAirport, string source, List<string> warnings)
    {
        ActiveRunways runways = ActiveRunways.Empty;
        if (byAirport is null)
        {
            return runways;
        }

        var firstSpelling = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string airportId, List<string>? tokens) in byAirport)
        {
            string airport = NavigationDatabase.NormalizeAirport(airportId);
            if (firstSpelling.TryGetValue(airport, out string? first))
            {
                warnings.Add($"{source}: {airportId} names the same airport as {first}; skipping");
                continue;
            }

            firstSpelling[airport] = airportId;

            if (tokens is null)
            {
                warnings.Add($"{source}: {airport} has no runway list; skipping");
                continue;
            }

            if (TryReadList(tokens, source, airport, warnings, out List<ActiveRunway> parsed))
            {
                runways = runways.With(airport, parsed);
            }
        }

        return runways;
    }

    /// <summary>
    /// The starting active runways a recording or checkpoint manifest stored, airport → token list; null when it stored
    /// none. A token that does not read drops its airport (<see cref="FromTokenLists"/>) and is logged against
    /// <paramref name="source"/>.
    /// </summary>
    public static ActiveRunways? ReadStored(Dictionary<string, List<string>?>? byAirport, string source, ILogger logger)
    {
        if (byAirport is null)
        {
            return null;
        }

        var warnings = new List<string>();
        ActiveRunways runways = FromTokenLists(byAirport, source, warnings);
        foreach (string warning in warnings)
        {
            logger.LogWarning("{Warning}", warning);
        }

        return runways;
    }

    private static bool TryReadList(List<string> tokens, string source, string airport, List<string> warnings, out List<ActiveRunway> runways)
    {
        runways = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? token in tokens)
        {
            if (token is null)
            {
                warnings.Add($"{source}: {airport} has a null runway entry; skipping");
                return false;
            }

            if (!TryReadOne(token, out ActiveRunway? runway, out string? error))
            {
                warnings.Add($"{source}: {airport}: {error}");
                return false;
            }

            if (!seen.Add(runway.Designator))
            {
                warnings.Add($"{source}: {airport}: runway {runway.Designator} listed twice; skipping");
                return false;
            }

            runways.Add(runway);
        }

        return true;
    }

    private static bool TryReadOne(string token, [NotNullWhen(true)] out ActiveRunway? runway, [NotNullWhen(false)] out string? error)
    {
        string trimmed = token.Trim();
        Match match = RunwayToken().Match(trimmed);
        if (!match.Success)
        {
            runway = null;
            error = $"Not a runway: {trimmed}";
            return false;
        }

        runway = new ActiveRunway(match.Groups["rwy"].Value, ParseUse(match.Groups["use"].Value));
        error = null;
        return true;
    }

    private static ActiveRunwayUse ParseUse(string prefix) =>
        prefix.ToUpperInvariant() switch
        {
            "D" => ActiveRunwayUse.Departure,
            "A" => ActiveRunwayUse.Arrival,
            _ => ActiveRunwayUse.Both,
        };

    [GeneratedRegex(@"^(?<use>[DA])?(?<rwy>(?:0?[1-9]|[12][0-9]|3[0-6])[LRC]?)$", RegexOptions.IgnoreCase)]
    private static partial Regex RunwayToken();
}
