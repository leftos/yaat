using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim.Commands;

namespace Yaat.Client.Views.Scope;

/// <summary>What an entry typed on the Scope reads as.</summary>
internal enum ScopeEntryKind
{
    /// <summary>Nothing it knows: no label.</summary>
    None,

    /// <summary>A YAAT pilot command.</summary>
    Pilot,

    /// <summary>A client dot command, such as <c>.scope</c> or <c>.view</c>.</summary>
    Dot,

    /// <summary>A CRC STARS entry.</summary>
    Stars,

    /// <summary>A CRC ERAM entry.</summary>
    Eram,
}

/// <summary>An entry's reading: its kind, the label the preview shows over it and the text Enter sends.</summary>
/// <param name="Kind">What the entry reads as.</param>
/// <param name="Label">The kind label, empty for <see cref="ScopeEntryKind.None"/>.</param>
/// <param name="SendText">The entry as Enter sends it: trimmed, with a leading <c>PILOT</c> word taken off.</param>
internal sealed record ScopeEntryReading(ScopeEntryKind Kind, string Label, string SendText);

/// <summary>
/// Reads an entry typed on the Scope for its kind label. A leading dot is a dot command; a leading <c>PILOT</c> word forces a
/// pilot read; a first word that is a dialect verb of the profile's kind (Mixed reads as ERAM) is a STARS or ERAM entry;
/// anything else that parses as a pilot command (after a callsign or not) is one; the rest reads as nothing. This is the one
/// place that names a dialect verb.
/// </summary>
internal static class ScopeEntryClassifier
{
    private const string PilotWord = "PILOT";

    /// <summary>CRC ERAM's command verbs.</summary>
    private static readonly HashSet<string> EramVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "AM",
        "AR",
        "DM",
        "LA",
        "LB",
        "LC",
        "LF",
        "MR",
        "QB",
        "QD",
        "QF",
        "QH",
        "QP",
        "QQ",
        "QR",
        "QS",
        "QT",
        "QU",
        "QX",
        "QZ",
        "RF",
        "VP",
        "WR",
    };

    /// <summary>CRC STARS's command verbs: none read yet.</summary>
    private static readonly HashSet<string> StarsVerbs = new(StringComparer.OrdinalIgnoreCase);

    private static readonly CommandScheme Scheme = CommandScheme.Default();

    private static readonly char[] BlockSeparators = [',', ';'];

    /// <summary>Reads <paramref name="text"/> as typed on a Scope showing a <paramref name="profile"/> profile.</summary>
    public static ScopeEntryReading Classify(string text, ScopeProfileKind profile)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return new ScopeEntryReading(ScopeEntryKind.None, "", trimmed);
        }

        if (trimmed.StartsWith('.'))
        {
            return new ScopeEntryReading(ScopeEntryKind.Dot, $"DOT {FirstWord(trimmed)}", trimmed);
        }

        string verb = FirstWord(trimmed);
        if (string.Equals(verb, PilotWord, StringComparison.OrdinalIgnoreCase))
        {
            return ReadPilot(trimmed[verb.Length..].TrimStart());
        }

        (ScopeEntryKind dialect, string dialectName, HashSet<string> verbs) = DialectOf(profile);
        return verbs.Contains(verb) ? new ScopeEntryReading(dialect, $"{dialectName} {verb.ToUpperInvariant()}", trimmed) : ReadPilot(trimmed);
    }

    private static (ScopeEntryKind Kind, string Name, HashSet<string> Verbs) DialectOf(ScopeProfileKind profile) =>
        profile == ScopeProfileKind.Stars ? (ScopeEntryKind.Stars, "STARS", StarsVerbs) : (ScopeEntryKind.Eram, "ERAM", EramVerbs);

    /// <summary>
    /// Reads <paramref name="text"/> as a pilot command, with or without a leading callsign; only its first block (up to a
    /// comma or semicolon) names the label. None when neither parses.
    /// </summary>
    private static ScopeEntryReading ReadPilot(string text)
    {
        string block = text.Split(BlockSeparators, 2)[0].Trim();
        string[] words = block.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ParsedInput? parsed = (words.Length > 0) ? CommandSchemeParser.Parse(block, Scheme) : null;
        string verb = (words.Length > 0) ? words[0] : "";
        if ((parsed is null) && (words.Length > 1))
        {
            parsed = CommandSchemeParser.Parse(string.Join(' ', words[1..]), Scheme);
            verb = words[1];
        }

        if (parsed is null)
        {
            return new ScopeEntryReading(ScopeEntryKind.None, "", text);
        }

        string gloss = CommandRegistry.Get(parsed.Type)?.Label.ToLowerInvariant() ?? "";
        return new ScopeEntryReading(ScopeEntryKind.Pilot, $"{PilotWord} {verb.ToUpperInvariant()} {gloss}".TrimEnd(), text);
    }

    private static string FirstWord(string text)
    {
        int space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }
}
