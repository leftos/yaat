using Yaat.Client.Models;

namespace Yaat.Client.Services;

/// <summary>
/// Shared callsign matching logic used by both the first-word callsign prefix
/// resolution in MainViewModel and the command-argument callsign rewrite in
/// CallsignArgumentResolver. Single source of truth for the exact-then-substring
/// semantics with ambiguity detection.
/// </summary>
internal static class CallsignMatcher
{
    internal enum Outcome
    {
        /// <summary>Exact case-insensitive match against a single aircraft.</summary>
        Exact,

        /// <summary>Exactly one aircraft contains the token as a substring.</summary>
        UniqueSubstring,

        /// <summary>No aircraft matches the token.</summary>
        None,

        /// <summary>Multiple aircraft contain the token as a substring.</summary>
        Ambiguous,
    }

    /// <summary>
    /// Resolves <paramref name="token"/> against the live aircraft list.
    /// Tries exact match first, then substring-contains.
    /// </summary>
    internal static (AircraftModel? Match, Outcome Outcome, IReadOnlyList<AircraftModel> Candidates) Match(
        string token,
        IReadOnlyCollection<AircraftModel> aircraft
    )
    {
        foreach (var ac in aircraft)
        {
            if (string.Equals(ac.Callsign, token, StringComparison.OrdinalIgnoreCase))
            {
                return (ac, Outcome.Exact, [ac]);
            }
        }

        var matches = new List<AircraftModel>();
        foreach (var ac in aircraft)
        {
            if (ac.Callsign.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(ac);
            }
        }

        if (matches.Count == 0)
        {
            return (null, Outcome.None, matches);
        }

        if (matches.Count == 1)
        {
            return (matches[0], Outcome.UniqueSubstring, matches);
        }

        return (null, Outcome.Ambiguous, matches);
    }

    /// <summary>
    /// Builds the user-facing ambiguity message for <paramref name="token"/> matching
    /// multiple aircraft. Matches the format used by MainViewModel.ResolveAircraft.
    /// </summary>
    internal static string FormatAmbiguityMessage(string token, IReadOnlyList<AircraftModel> candidates)
    {
        var names = string.Join(", ", candidates.Select(a => a.Callsign).Take(5));
        return $"\"{token}\" matches multiple aircraft: {names}";
    }
}
