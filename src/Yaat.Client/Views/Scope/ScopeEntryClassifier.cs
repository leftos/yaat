using Yaat.Client.Views.Scope.Entries;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim.Commands;

namespace Yaat.Client.Views.Scope;

/// <summary>What an entry typed on the Scope reads as.</summary>
internal enum ScopeEntryKind
{
    /// <summary>Nothing it knows: no label, nothing to send.</summary>
    None,

    /// <summary>A YAAT command, typed after a leading <c>$</c>.</summary>
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
/// The entry as Enter sends it: trimmed, with a leading <c>$</c> taken off; for a STARS entry, the YAAT command it translates
/// to (empty when not supported); empty for an entry that reads as nothing.
/// </param>
/// <param name="Supported">False for a STARS entry with no YAAT command, which Enter answers <c>NOT SUPPORTED</c>.</param>
internal sealed record ScopeEntryReading(ScopeEntryKind Kind, string Label, string SendText, bool Supported);

/// <summary>
/// Reads an entry typed on the Scope for its kind label. In order: a leading dot is a dot command; a leading <c>$</c> makes
/// the rest a YAAT command, as the main command input reads it (a callsign and a <c>** </c> override included), sent without
/// the <c>$</c>; on a STARS profile, a STARS shape or key word (<see cref="StarsEntryParser"/>) is a STARS entry, translated
/// to its YAAT command or not supported; on ERAM and Mixed (Mixed reads as STARS for a STARS-owned track: the clicked one,
/// else the selected one), a first word that is a verb the server dispatches (<see cref="EramEntryVerbs.All"/>, shared with
/// it), an implied command's shape or an implied FLID entry is an ERAM entry; the rest reads as nothing and sends nothing.
/// This is the one place on the client that reads a dialect verb.
/// </summary>
internal static class ScopeEntryClassifier
{
    /// <summary>The character a YAAT command typed on the Scope starts with.</summary>
    internal const char YaatPrefix = '$';

    private const string OverridePrefix = "** ";

    /// <summary>The STARS min-sep entry's word, which reads as STARS on Mixed while no track decides the dialect.</summary>
    private const string MinSepWord = "MIN";

    /// <summary>The leading characters of an implied ERAM VCI toggle: <c>//{FLID}</c>.</summary>
    private const string VciPrefix = "//";

    /// <summary>The field 60 override an implied entry's FLID may take: <c>/OK</c>.</summary>
    private const string OverrideField = "/OK";

    /// <summary>The longest bare FLID an implied ERAM entry starts with: a seven-character ACID.</summary>
    private const int MaxFlidLength = 7;

    private static readonly char[] BlockSeparators = [',', ';'];

    // An entry that reads as nothing: no label, nothing to send.
    private static readonly ScopeEntryReading Nothing = new(ScopeEntryKind.None, "", "", true);

    /// <summary>
    /// Reads <paramref name="text"/> as typed on a Scope showing a <paramref name="profile"/> profile, with
    /// <paramref name="ownerTemplateId"/> the owner kind of the track the entry is for (which picks the dialect on Mixed), and a
    /// <c>$</c> entry's command read in <paramref name="scheme"/>, the user's command scheme.
    /// </summary>
    public static ScopeEntryReading Classify(string text, ScopeProfileKind profile, string? ownerTemplateId, CommandScheme scheme)
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

        if (trimmed[0] == YaatPrefix)
        {
            return ReadYaat(trimmed[1..].TrimStart(), scheme);
        }

        string verb = FirstWord(trimmed);
        return ReadDialect(trimmed, verb, DialectOf(profile, ownerTemplateId, verb));
    }

    /// <summary>
    /// A <c>$</c> entry, <paramref name="text"/> the YAAT command after the <c>$</c>: it reads as a YAAT command whether or not
    /// it parses (the command pipeline answers it), labelled after its pilot command where it parses, after a <c>** </c>
    /// override kept in what it sends; a bare <c>$</c> has nothing to send. The command is read in <paramref name="scheme"/>.
    /// </summary>
    private static ScopeEntryReading ReadYaat(string text, CommandScheme scheme)
    {
        string command = text.StartsWith(OverridePrefix, StringComparison.Ordinal) ? text[OverridePrefix.Length..].TrimStart() : text;
        ScopeEntryReading pilot = ReadPilot(command, text, scheme);
        return (pilot.Kind == ScopeEntryKind.Pilot)
            ? pilot
            : new ScopeEntryReading(ScopeEntryKind.Pilot, $"{YaatPrefix} {FirstWord(command).ToUpperInvariant()}".TrimEnd(), text, true);
    }

    /// <summary>
    /// The dialect an entry reads in: on Mixed, the owner kind of the track the entry is for decides it, so a STARS-owned track
    /// (<paramref name="ownerTemplateId"/> is <see cref="ScopeTemplates.StarsId"/>) reads as STARS; a <c>MIN</c> entry with no
    /// track yet reads as STARS too, as ERAM has no MIN; else the profile's own.
    /// </summary>
    private static ScopeProfileKind DialectOf(ScopeProfileKind profile, string? ownerTemplateId, string verb) =>
        (
            (profile == ScopeProfileKind.Mixed)
            && (
                (ownerTemplateId == ScopeTemplates.StarsId)
                || ((ownerTemplateId is null) && string.Equals(verb, MinSepWord, StringComparison.OrdinalIgnoreCase))
            )
        )
            ? ScopeProfileKind.Stars
            : profile;

    /// <summary>
    /// Reads <paramref name="trimmed"/> in the profile's dialect: a STARS entry on STARS; on ERAM and Mixed, a verb the server
    /// dispatches (<see cref="EramEntryVerbs.All"/>), an implied command's shape (<see cref="IsImpliedEramShape"/>), or a lone
    /// FLID or a sector number then a FLID (<see cref="IsImpliedFlidEntry"/>) is an ERAM entry; anything else reads as nothing,
    /// so <c>UAL238 FH 270</c> is no entry without its <c>$</c>.
    /// </summary>
    private static ScopeEntryReading ReadDialect(string trimmed, string verb, ScopeProfileKind profile)
    {
        if (profile == ScopeProfileKind.Stars)
        {
            return (StarsEntryParser.TryRead(trimmed) is { } stars) ? ReadStars(stars) : Nothing;
        }

        bool eram = EramEntryVerbs.All.Contains(verb.ToUpperInvariant()) || IsImpliedEramShape(trimmed, verb) || IsImpliedFlidEntry(trimmed);
        return eram ? ReadEram(trimmed, verb) : Nothing;
    }

    private static ScopeEntryReading ReadEram(string trimmed, string verb) =>
        new(ScopeEntryKind.Eram, $"ERAM {verb.ToUpperInvariant()}", trimmed, true);

    /// <summary>
    /// The implied ERAM commands that no pilot command shares a shape with (ERAM Table 16): a VCI toggle (<c>//{FLID}</c>),
    /// and a data block position (<c>1</c>-<c>9</c>), a leader line length (<c>/0</c>-<c>/3</c>) or both (<c>3/2</c>) followed by
    /// its FLID.
    /// </summary>
    private static bool IsImpliedEramShape(string trimmed, string verb)
    {
        if (trimmed.StartsWith(VciPrefix, StringComparison.Ordinal))
        {
            return trimmed.Length > VciPrefix.Length;
        }

        return (trimmed.Length > verb.Length) && IsPositionOrLeader(verb);
    }

    // A data block position 1-9, a leader line length /0-/3, or a position then a length (3/2).
    private static bool IsPositionOrLeader(string word) =>
        word.Length switch
        {
            1 => IsPosition(word[0]),
            2 => (word[0] == '/') && IsLeaderLength(word[1]),
            3 => IsPosition(word[0]) && (word[1] == '/') && IsLeaderLength(word[2]),
            _ => false,
        };

    private static bool IsPosition(char c) => c is >= '1' and <= '9';

    private static bool IsLeaderLength(char c) => c is >= '0' and <= '3';

    /// <summary>
    /// The implied ERAM entries a FLID-shaped word leads or ends (ERAM Table 16): a lone FLID (accept, recall, block toggle),
    /// or a handoff sector then a FLID (an implied handoff), the FLID either way taking a <c>/OK</c> override on its end
    /// (<c>UAL238/OK</c>) or as a word after it (<c>26 UAL238 /OK</c>). Anything longer, or with a word of another shape, is no
    /// implied entry.
    /// </summary>
    private static bool IsImpliedFlidEntry(string trimmed)
    {
        string[] words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            1 => IsFlidWithOverride(words[0]),
            2 => IsHandoffSector(words[0]) && IsFlidWithOverride(words[1]),
            3 => IsHandoffSector(words[0]) && IsFlidShaped(words[1]) && string.Equals(words[2], OverrideField, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>
    /// A handoff sector token the server's <c>ToHandoffCode</c> takes: a sector number of two or three digits, with or without
    /// a centre letter (<c>44</c>, <c>C44</c>, <c>L25</c>), or an ERAM-to-STARS code, a letter, a digit and a letter (<c>Q2B</c>).
    /// </summary>
    private static bool IsHandoffSector(string word)
    {
        string number = ((word.Length > 0) && char.IsAsciiLetter(word[0])) ? word[1..] : word;
        return ((number.Length is 2 or 3) && number.All(char.IsAsciiDigit)) || IsStarsHandoffCode(word);
    }

    // An ERAM-to-STARS handoff code: a letter, a digit and a letter (Q2B).
    private static bool IsStarsHandoffCode(string word) =>
        (word.Length == 3) && char.IsAsciiLetter(word[0]) && char.IsAsciiDigit(word[1]) && char.IsAsciiLetter(word[2]);

    // A FLID, bare or with the /OK override on its end.
    private static bool IsFlidWithOverride(string word) =>
        IsFlidShaped(word.EndsWith(OverrideField, StringComparison.OrdinalIgnoreCase) ? word[..^OverrideField.Length] : word);

    // A FLID: an ACID, a CID or a beacon code, letters and digits only.
    private static bool IsFlidShaped(string word) => (word.Length is >= 2 and <= MaxFlidLength) && word.All(char.IsAsciiLetterOrDigit);

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
    /// comma or semicolon) names the label. None when neither parses in <paramref name="scheme"/>. <paramref name="sendText"/> is
    /// what Enter sends.
    /// </summary>
    private static ScopeEntryReading ReadPilot(string text, string sendText, CommandScheme scheme)
    {
        string block = text.Split(BlockSeparators, 2)[0].Trim();
        string[] words = block.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ParsedInput? parsed = (words.Length > 0) ? CommandSchemeParser.Parse(block, scheme) : null;
        string verb = (words.Length > 0) ? words[0] : "";
        if ((parsed is null) && (words.Length > 1))
        {
            parsed = CommandSchemeParser.Parse(string.Join(' ', words[1..]), scheme);
            verb = words[1];
        }

        if (parsed is null)
        {
            return new ScopeEntryReading(ScopeEntryKind.None, "", sendText, true);
        }

        string gloss = CommandRegistry.Get(parsed.Type)?.Label.ToLowerInvariant() ?? "";
        return new ScopeEntryReading(ScopeEntryKind.Pilot, $"{YaatPrefix} {verb.ToUpperInvariant()} {gloss}".TrimEnd(), sendText, true);
    }

    private static string FirstWord(string text)
    {
        int space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }
}
