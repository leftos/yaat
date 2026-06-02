using Yaat.Client.Models;
using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

/// <summary>
/// Builds the user-facing error for a command-input string that failed to parse.
///
/// When the leading token is a known callsign (exact or unique partial match), the error
/// focuses on the verb that follows it instead of labelling the callsign itself as an
/// unrecognized command — the addressee is never the problem. This is what the user sees
/// when, e.g., a verb is mistyped after a valid callsign (<c>N929AW SPEEDN 80</c>): the
/// callsign prefix resolver deliberately declines to claim the prefix when the remainder
/// doesn't parse (to avoid hijacking callsign-shaped arguments), so without this the raw
/// parser would blame the callsign token.
/// </summary>
internal static class CommandErrorFormatter
{
    internal sealed record Result(string Verb, string Reason, string StatusText);

    internal static Result Format(string commandText, ParseFailure? parseFailure, CommandScheme scheme, IReadOnlyCollection<AircraftModel> aircraft)
    {
        var errorText = commandText;
        var head = commandText.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (head.Length == 2 && CallsignMatcher.Match(head[0], aircraft).Match is not null)
        {
            // Re-derive the failure from the verb after the callsign so the message names
            // the real command, never the addressee.
            errorText = head[1].Trim();
            CommandSchemeParser.ParseCompound(errorText, scheme, out parseFailure);
        }

        if (parseFailure is not null)
        {
            var statusText = parseFailure.Expected is { } expected
                ? $"\"{parseFailure.Verb}\" {parseFailure.Reason}. Expected: {expected}"
                : $"\"{parseFailure.Verb}\" {parseFailure.Reason}";
            return new Result(parseFailure.Verb, parseFailure.Reason, statusText);
        }

        var verb = errorText.Split([' ', ',', ';'], 2)[0];
        return new Result(verb, "is not a recognized command", $"Unrecognized command \"{verb}\" — type a command like FH 270, CM 240, CLAND");
    }
}
