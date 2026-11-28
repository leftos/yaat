using System.Globalization;
using Yaat.Sim.Authoring.Stamping;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>What a list's text reads as: the entries it names and one "Line {n} of {list}: {reason}" per line it cannot.</summary>
/// <typeparam name="T">The entry type.</typeparam>
/// <param name="Items">The entries of the lines that read, in order.</param>
/// <param name="Problems">One line per line that does not read, in order; empty when every line reads.</param>
public sealed record ProfileListRead<T>(IReadOnlyList<T> Items, IReadOnlyList<string> Problems);

/// <summary>
/// The profile editor's lists as the Apply Route window's text boxes show them: a pool as its entries separated by commas,
/// STAR aliases one per line as "FROM TO", preset commands one per line as the seconds after spawn and then the command.
/// </summary>
public static class ProfileListText
{
    /// <summary>The STAR alias list's name in a problem line.</summary>
    public const string AliasesList = "STAR aliases";

    /// <summary>The preset command list's name in a problem line.</summary>
    public const string PresetsList = "preset commands";

    private static readonly char[] PoolSeparators = [',', '\n', '\r'];

    private static readonly char[] Blanks = [' ', '\t'];

    /// <summary>A pool's entries, separated by commas.</summary>
    /// <param name="items">The entries.</param>
    /// <returns>The text.</returns>
    public static string JoinPool(IEnumerable<string> items) => string.Join(", ", items);

    /// <summary>The entries a pool's text names, each trimmed, blank ones dropped.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The entries.</returns>
    public static IReadOnlyList<string> SplitPool(string? text) =>
        [.. (text ?? "").Split(PoolSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>The aliases, one per line as "FROM TO".</summary>
    /// <param name="aliases">The aliases.</param>
    /// <returns>The text.</returns>
    public static string JoinAliases(IEnumerable<StarAlias> aliases) => string.Join("\n", aliases.Select(a => $"{a.From} {a.To}"));

    /// <summary>The aliases a text names, one per non-blank line of exactly two words: the STAR, then the one replacing it.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The aliases, and a problem for each line that is not two words.</returns>
    public static ProfileListRead<StarAlias> ReadAliases(string? text)
    {
        var items = new List<StarAlias>();
        var problems = new List<string>();
        foreach ((int number, string line) in Lines(text))
        {
            string[] words = line.Split(Blanks, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 2)
            {
                items.Add(new StarAlias(words[0], words[1]));
            }
            else
            {
                problems.Add(Problem(number, AliasesList, "write the STAR and the STAR replacing it, two words"));
            }
        }

        return new ProfileListRead<StarAlias>(items, problems);
    }

    /// <summary>The preset commands, one per line as the seconds after spawn and then the command.</summary>
    /// <param name="commands">The commands.</param>
    /// <returns>The text.</returns>
    public static string JoinPresets(IEnumerable<StampPresetCommand> commands) =>
        string.Join("\n", commands.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.TimeOffset} {c.Command}")));

    /// <summary>
    /// The preset commands a text names, one per non-blank line: a line starting with whole seconds runs the rest at that
    /// offset, any other line runs at spawn. A lone number is a problem, and so is a first token that is a run of digits
    /// with a sign or one too large for an int.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The commands, and a problem for each line that does not read.</returns>
    public static ProfileListRead<StampPresetCommand> ReadPresets(string? text)
    {
        var items = new List<StampPresetCommand>();
        var problems = new List<string>();
        foreach ((int number, string line) in Lines(text))
        {
            string[] parts = line.Split(Blanks, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            SecondsRead read = JudgeSeconds(parts[0], out int offset);
            if (read is SecondsRead.NotSeconds)
            {
                items.Add(new StampPresetCommand(line, 0));
            }
            else if (ReasonFor(read) is { } reason)
            {
                problems.Add(Problem(number, PresetsList, reason));
            }
            else if (parts.Length == 2)
            {
                items.Add(new StampPresetCommand(parts[1], offset));
            }
            else
            {
                problems.Add(Problem(number, PresetsList, "a command must follow the seconds"));
            }
        }

        return new ProfileListRead<StampPresetCommand>(items, problems);
    }

    // What a first token reads as: whole seconds, or the reason it is not them. A token that is not a sign and then
    // digits is no seconds at all, so its line is a command at spawn.
    private static SecondsRead JudgeSeconds(string token, out int seconds)
    {
        seconds = 0;
        if (!IsSignedOrUnsignedDigits(token))
        {
            return SecondsRead.NotSeconds;
        }

        if (token[0] == '-')
        {
            return SecondsRead.Negative;
        }

        if (token[0] == '+')
        {
            return SecondsRead.Signed;
        }

        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) ? SecondsRead.Whole : SecondsRead.TooLarge;
    }

    private static bool IsSignedOrUnsignedDigits(string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        int start = ((token[0] == '-') || (token[0] == '+')) ? 1 : 0;
        foreach (char digit in token.AsSpan(start))
        {
            if ((digit < '0') || (digit > '9'))
            {
                return false;
            }
        }

        return start < token.Length;
    }

    private static string? ReasonFor(SecondsRead read) =>
        read switch
        {
            SecondsRead.Negative => "the seconds can't be negative",
            SecondsRead.Signed => "write the seconds without a sign",
            SecondsRead.TooLarge => "the seconds are too large",
            _ => null,
        };

    private static string Problem(int number, string list, string reason) =>
        string.Create(CultureInfo.InvariantCulture, $"Line {number} of {list}: {reason}");

    // What a preset line's first token reads as.
    private enum SecondsRead
    {
        Whole,
        Negative,
        Signed,
        TooLarge,
        NotSeconds,
    }

    // The non-blank lines of a text, trimmed, each with its 1-based line number.
    private static IEnumerable<(int Number, string Line)> Lines(string? text) =>
        (text ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select((line, i) => (i + 1, line.Trim()))
            .Where(l => l.Item2.Length > 0);
}
