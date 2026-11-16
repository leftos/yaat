using Yaat.Client.Views.Scope.Entries;
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

/// <summary>An entry's reading: its kind, the label the preview shows over it, the text Enter sends and whether it is supported.</summary>
/// <param name="Kind">What the entry reads as.</param>
/// <param name="Label">The kind label, empty for <see cref="ScopeEntryKind.None"/>.</param>
/// <param name="SendText">
/// The entry as Enter sends it: trimmed, with a leading <c>PILOT</c> word taken off; for a STARS entry, the YAAT command it
/// translates to (empty when not supported).
/// </param>
/// <param name="Supported">False for a STARS entry with no YAAT command, which Enter answers <c>NOT SUPPORTED</c>.</param>
internal sealed record ScopeEntryReading(ScopeEntryKind Kind, string Label, string SendText, bool Supported);

/// <summary>
/// Reads an entry typed on the Scope for its kind label. In order: a leading dot is a dot command; a leading <c>PILOT</c> word
/// forces a pilot read; a leading <c>** </c> override is a pilot command with the override kept; on a STARS profile, a STARS
/// shape or key word (<see cref="StarsEntryParser"/>) is a STARS entry, translated to its YAAT command or not supported; on
/// ERAM and Mixed, a first word that is an ERAM verb is an ERAM entry; anything else that parses as a pilot command (after a
/// callsign or not) is one; the rest reads as nothing. This is the one place that names a dialect verb.
/// </summary>
internal static class ScopeEntryClassifier
{
    private const string PilotWord = "PILOT";

    private const string OverridePrefix = "** ";

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

    private static readonly CommandScheme Scheme = CommandScheme.Default();

    private static readonly char[] BlockSeparators = [',', ';'];

    /// <summary>Reads <paramref name="text"/> as typed on a Scope showing a <paramref name="profile"/> profile.</summary>
    public static ScopeEntryReading Classify(string text, ScopeProfileKind profile)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return new ScopeEntryReading(ScopeEntryKind.None, "", trimmed, true);
        }

        if (trimmed.StartsWith('.'))
        {
            return new ScopeEntryReading(ScopeEntryKind.Dot, $"DOT {FirstWord(trimmed)}", trimmed, true);
        }

        string verb = FirstWord(trimmed);
        if (string.Equals(verb, PilotWord, StringComparison.OrdinalIgnoreCase))
        {
            string rest = trimmed[verb.Length..].TrimStart();
            return ReadPilot(rest, rest);
        }

        if (trimmed.StartsWith(OverridePrefix, StringComparison.Ordinal))
        {
            return ReadPilot(trimmed[OverridePrefix.Length..].TrimStart(), trimmed);
        }

        return ReadDialect(trimmed, verb, profile);
    }

    /// <summary>
    /// Reads <paramref name="trimmed"/> in the profile's dialect: a STARS entry on STARS, an ERAM verb on ERAM and Mixed; else
    /// as a pilot command.
    /// </summary>
    private static ScopeEntryReading ReadDialect(string trimmed, string verb, ScopeProfileKind profile)
    {
        if (profile == ScopeProfileKind.Stars)
        {
            return (StarsEntryParser.TryRead(trimmed) is { } stars) ? ReadStars(stars) : ReadPilot(trimmed, trimmed);
        }

        return EramVerbs.Contains(verb)
            ? new ScopeEntryReading(ScopeEntryKind.Eram, $"ERAM {verb.ToUpperInvariant()}", trimmed, true)
            : ReadPilot(trimmed, trimmed);
    }

    /// <summary>
    /// A STARS entry: labelled <c>STARS &lt;first word&gt; &lt;gloss&gt;</c>, or <c>STARS &lt;first word&gt;</c> when not
    /// supported.
    /// </summary>
    private static ScopeEntryReading ReadStars(StarsEntryRead read) =>
        (read.Translation is { } translation)
            ? new ScopeEntryReading(ScopeEntryKind.Stars, $"STARS {read.FirstWord} {read.Gloss}".TrimEnd(), translation, true)
            : new ScopeEntryReading(ScopeEntryKind.Stars, $"STARS {read.FirstWord}", "", false);

    /// <summary>
    /// Reads <paramref name="text"/> as a pilot command, with or without a leading callsign; only its first block (up to a
    /// comma or semicolon) names the label. None when neither parses. <paramref name="sendText"/> is what Enter sends.
    /// </summary>
    private static ScopeEntryReading ReadPilot(string text, string sendText)
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
            return new ScopeEntryReading(ScopeEntryKind.None, "", sendText, true);
        }

        string gloss = CommandRegistry.Get(parsed.Type)?.Label.ToLowerInvariant() ?? "";
        return new ScopeEntryReading(ScopeEntryKind.Pilot, $"{PilotWord} {verb.ToUpperInvariant()} {gloss}".TrimEnd(), sendText, true);
    }

    private static string FirstWord(string text)
    {
        int space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }
}
