using System.Text.RegularExpressions;

namespace Yaat.Client.Services;

/// <summary>
/// Parses the text of a CRC alias file into <see cref="CrcAlias" /> definitions.
/// Pure — takes text, returns aliases, touches no filesystem.
/// </summary>
/// <remarks>
/// Mirrors CRC's own <c>AliasParser.LoadAliases</c>: a line is an alias only when it starts with a dot,
/// is at least four characters long, and matches <c>^(\.\w+)\s+(.+)$</c>. Every other line is skipped
/// silently, which is what makes both <c>#</c> comment lines and the free-text header blocks that ARTCC
/// alias files open with work — CRC has no dedicated comment syntax.
///
/// One deliberate divergence: CRC tests that regex against the raw line while testing the leading dot
/// against the trimmed line, so an indented alias definition is silently dropped. We match the trimmed
/// line in both places, which loads the alias the author plainly intended.
/// </remarks>
public static partial class CrcAliasFileParser
{
    private const int MinimumLineLength = 4;

    private static readonly Regex DefinitionRegex = GetDefinitionRegex();

    /// <summary>Splits on spaces the way CRC does — empty entries removed, so runs of spaces collapse.</summary>
    public static string[] Tokenize(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Parses every alias definition in <paramref name="lines" />, in file order. Callers that load
    /// several files rely on that order: later definitions overwrite earlier ones of the same name.
    /// </summary>
    public static List<CrcAlias> Parse(IEnumerable<string> lines, string sourceFile)
    {
        var aliases = new List<CrcAlias>();
        var lineNumber = 0;

        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length < MinimumLineLength || !line.StartsWith('.'))
            {
                continue;
            }

            var match = DefinitionRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var body = match.Groups[2].Value;
            aliases.Add(new CrcAlias(match.Groups[1].Value, Tokenize(body), CountArguments(body), sourceFile, lineNumber));
        }

        return aliases;
    }

    /// <summary>
    /// Counts the <c>$1</c>, <c>$2</c>, … slots present in <paramref name="body" />, consecutively from
    /// <c>$1</c>, stopping at the first missing number. Matches CRC, which means a body referencing
    /// <c>$1</c> and <c>$3</c> but not <c>$2</c> only ever consumes one argument and leaves <c>$3</c> literal.
    /// </summary>
    private static int CountArguments(string body)
    {
        var count = 0;
        while (body.Contains($"${count + 1}", StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [GeneratedRegex(@"^(\.\w+)\s+(.+)$")]
    private static partial Regex GetDefinitionRegex();
}
