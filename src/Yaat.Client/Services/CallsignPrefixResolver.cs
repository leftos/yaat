using Yaat.Client.Models;
using Yaat.Sim;
using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

/// <summary>
/// Resolves the leading token of a command-input string as a full or partial
/// callsign. Used by <c>MainViewModel.SendCommandAsync</c> to peel off the
/// callsign prefix before the remainder is parsed as a command.
///
/// Returns a discriminated <see cref="Result"/> so the caller can distinguish
/// "ambiguous callsign prefix" from "input does not look like a callsign
/// prefix at all" — the previous nullable-tuple shape collapsed both cases
/// into <c>null</c>, which let the command parser's "is not a recognized
/// command" message overwrite the ambiguity warning.
/// </summary>
internal static class CallsignPrefixResolver
{
    internal abstract record Result;

    /// <summary>Unique aircraft match; caller continues with <see cref="Remainder"/> as the command text.</summary>
    internal sealed record Resolved(AircraftModel Aircraft, string Remainder) : Result;

    /// <summary>
    /// First token is callsign-shaped and matches multiple aircraft as a substring.
    /// <see cref="Message"/> is the user-facing ambiguity message from <see cref="CallsignMatcher.FormatAmbiguityMessage"/>.
    /// </summary>
    internal sealed record Ambiguous(string Message) : Result;

    /// <summary>Input does not look like a callsign-prefixed command; caller should fall through to its normal parsing path.</summary>
    internal sealed record NotAPrefix : Result;

    private static readonly NotAPrefix NotAPrefixSingleton = new();

    /// <summary>
    /// Inspects <paramref name="input"/> as <c>&lt;callsign-prefix&gt; &lt;command&gt;</c>.
    /// Ambiguity is reported regardless of whether the remainder parses as a command —
    /// telling the user that the addressee is ambiguous is more useful than complaining
    /// about the command they tried to send to it.
    /// </summary>
    internal static Result Resolve(string input, CommandScheme scheme, IReadOnlyCollection<AircraftModel> aircraft)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return NotAPrefixSingleton;
        }

        var token = parts[0].ToUpperInvariant();
        var remainder = parts[1].Trim();

        if (!Callsign.IsValid(token))
        {
            return NotAPrefixSingleton;
        }

        var (match, outcome, candidates) = CallsignMatcher.Match(token, aircraft);

        if (outcome == CallsignMatcher.Outcome.Ambiguous)
        {
            return new Ambiguous(CallsignMatcher.FormatAmbiguityMessage(token, candidates));
        }

        if (match is null)
        {
            return NotAPrefixSingleton;
        }

        // Unique match: only claim the prefix if the remainder parses as a real command.
        // Otherwise inputs like `RTIS N17` where `N17` happens to be a callsign-shaped
        // arg would be hijacked — the argument-position resolver handles those.
        if (CommandSchemeParser.ParseCompound(remainder, scheme) is null)
        {
            return NotAPrefixSingleton;
        }

        return new Resolved(match, remainder);
    }
}
