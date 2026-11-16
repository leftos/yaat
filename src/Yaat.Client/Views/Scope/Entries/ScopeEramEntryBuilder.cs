using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope.Entries;

/// <summary>
/// The two ends of an ERAM entry typed on the Scope: the entry the server's ERAM dispatch takes (<see cref="Build"/>: one
/// token per whitespace run, uppercase, as CRC's ERAM keyboard sends it; the server parses it, never the client), and the
/// Scope's response to its answer (<see cref="ResponseTo"/>). A clicked track or map point is already text in the entry; a
/// clicked track's callsign is also sent as a pick of that aircraft, as CRC sends one, while its text stays intact, except in
/// free text (an SM, SW or WX entry, or after the free-text marker), where it goes as plain text.
/// </summary>
internal static class ScopeEramEntryBuilder
{
    private static readonly char[] LineBreaks = ['\r', '\n'];

    // The verbs whose operands are free text, where a clicked callsign goes as plain text.
    private static readonly HashSet<string> FreeTextVerbs = new(StringComparer.OrdinalIgnoreCase) { "SM", "SW", "WX" };

    // The free-text marker as typed (the backtick) and as ScopeEramKeys.MapTyped sends it.
    private static readonly char[] FreeTextMarkers = ['`', ScopeEramKeys.ClearWeather];

    /// <summary>
    /// The tokens of <paramref name="entry"/>, for <c>SendScopeEramEntry</c>, with the last token equal to
    /// <paramref name="pickedCallsign"/> (the clicked track's) marked as its pick; no pick when that is null or no token
    /// equals it, as when the callsign was edited after the click, nor when the callsign is free text.
    /// </summary>
    public static ScopeEramEntryDto Build(string entry, string? pickedCallsign)
    {
        List<string> tokens = [.. entry.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        int index = PickIndex(tokens, pickedCallsign);
        return new(tokens, false, (index < 0) ? null : [new ScopeEramPickDto(index, tokens[index])]);
    }

    // The index of the last token equal to pickedCallsign ahead of any free text, or -1: none in an SM, SW or WX entry, whose
    // operands are free text, and none at or after the first token holding the free-text marker, where a callsign is text.
    private static int PickIndex(List<string> tokens, string? pickedCallsign)
    {
        if ((pickedCallsign is null) || (tokens.Count == 0) || FreeTextVerbs.Contains(tokens[0]))
        {
            return -1;
        }

        int freeText = tokens.FindIndex(token => token.IndexOfAny(FreeTextMarkers) >= 0);
        for (int i = ((freeText < 0) ? tokens.Count : freeText) - 1; i >= 0; i--)
        {
            if (string.Equals(tokens[i], pickedCallsign, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The Scope's response to <paramref name="result"/>, echoing <paramref name="entry"/>: an acceptance shows every feedback
    /// line as CRC does (<c>ACCEPT</c>, the message descriptor, the ACID); a readout shows its response text, one line per
    /// line; a refusal shows its error line.
    /// </summary>
    public static ScopeEntryResponse ResponseTo(ScopeEramEntryResultDto result, string entry)
    {
        if (!result.Ok)
        {
            return new ScopeEntryResponse(ScopeResponseKind.Error, result.Feedback.FirstOrDefault() ?? "", entry, [.. result.Feedback.Skip(1)]);
        }

        if (result.Response is { } response)
        {
            string[] lines = response.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries);
            return new ScopeEntryResponse(ScopeResponseKind.Text, lines.FirstOrDefault() ?? "", entry, [.. lines.Skip(1)]);
        }

        return new ScopeEntryResponse(
            ScopeResponseKind.Accept,
            result.Feedback.FirstOrDefault() ?? ScopeViewModel.AcceptMessage,
            entry,
            [.. result.Feedback.Skip(1)]
        );
    }
}
