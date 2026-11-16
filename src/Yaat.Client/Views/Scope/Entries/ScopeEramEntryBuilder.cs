using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope.Entries;

/// <summary>
/// The two ends of an ERAM entry typed on the Scope: the entry the server's ERAM dispatch takes (<see cref="Build"/>: one
/// token per whitespace run, uppercase, as CRC's ERAM keyboard sends it; the server parses it, never the client), and the
/// Scope's response to its answer (<see cref="ResponseTo"/>). A clicked track or map point is already text in the entry.
/// </summary>
internal static class ScopeEramEntryBuilder
{
    private static readonly char[] LineBreaks = ['\r', '\n'];

    /// <summary>The tokens of <paramref name="entry"/>, for <c>SendScopeEramEntry</c>.</summary>
    public static ScopeEramEntryDto Build(string entry) =>
        new([.. entry.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)], false);

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
