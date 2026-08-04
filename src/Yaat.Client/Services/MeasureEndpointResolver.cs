using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.Services;

/// <summary>
/// Resolves one typed measurement endpoint token for the <c>.rbl A B</c> / <c>*T A B</c> command: an
/// exact callsign wins, then a fix or FRD, then a partial callsign — so a fix name is never shadowed by
/// an airline shorthand or vice versa. A callsign endpoint latches and travels with the aircraft.
/// </summary>
public static class MeasureEndpointResolver
{
    /// <summary>
    /// Resolves <paramref name="token" /> against the live aircraft list and the navigation database.
    /// Exactly one of the tuple's fields is non-null.
    /// </summary>
    /// <param name="resolveFix">Fix/FRD lookup, or null while the navigation database is still loading.</param>
    public static (RblEndpoint? Endpoint, string? Error) Resolve(
        string token,
        IReadOnlyCollection<AircraftModel> aircraft,
        Func<string, LatLon?>? resolveFix
    )
    {
        var (match, outcome, candidates) = CallsignMatcher.Match(token, aircraft);
        if (outcome == CallsignMatcher.Outcome.Exact)
        {
            return (RblEndpoint.OnAircraft(match!.Callsign), null);
        }

        if (resolveFix?.Invoke(token) is { } position)
        {
            return (RblEndpoint.AtPoint(position, token.Trim().ToUpperInvariant()), null);
        }

        switch (outcome)
        {
            case CallsignMatcher.Outcome.UniqueSubstring:
                return (RblEndpoint.OnAircraft(match!.Callsign), null);
            case CallsignMatcher.Outcome.Ambiguous:
                return (null, CallsignMatcher.FormatAmbiguityMessage(token, candidates));
            default:
                return (
                    null,
                    resolveFix is not null
                        ? $"Unknown fix or callsign: {token.ToUpperInvariant()}"
                        : $"Unknown callsign: {token.ToUpperInvariant()} (navdata still loading — fixes unavailable)"
                );
        }
    }
}
