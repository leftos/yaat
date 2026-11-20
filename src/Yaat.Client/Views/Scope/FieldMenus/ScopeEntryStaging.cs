using Yaat.Client.ContextMenus;
using Yaat.Sim.Commands;

namespace Yaat.Client.Views.Scope.FieldMenus;

/// <summary>
/// The text a Shift+row-click on a Scope field menu stages in the Scope's preview. It is a <c>$</c> YAAT command naming the
/// aircraft, which keeps a command whose verb is also a dialect verb (<c>DM</c>, <c>LA</c>, <c>QQ</c>) reading as a YAAT command
/// (<see cref="ScopeEntryClassifier"/>). Its commands are staged in the verbs of the user's command scheme, the scheme Enter
/// sends a <c>$</c> entry with.
/// </summary>
internal static class ScopeEntryStaging
{
    /// <summary>
    /// <paramref name="preview"/> with <paramref name="command"/> appended after a comma when it already holds a <c>$</c> command
    /// for <paramref name="callsign"/> (matched case-insensitively), else <c>$ {callsign} {command}</c> in its place. Each command
    /// in <paramref name="command"/> is staged in <paramref name="scheme"/>'s verbs.
    /// </summary>
    public static string Text(string preview, string callsign, string command, CommandScheme scheme)
    {
        string staged = InScheme(command, scheme);
        string current = preview.Trim();
        bool sameYaatEntry = false;
        if ((current.Length > 0) && (current[0] == ScopeEntryClassifier.YaatPrefix))
        {
            string[] words = current[1..].TrimStart().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            sameYaatEntry = (words.Length == 2) && string.Equals(words[0], callsign, StringComparison.OrdinalIgnoreCase);
        }

        return sameYaatEntry ? $"{current}, {staged}" : $"{ScopeEntryClassifier.YaatPrefix} {callsign} {staged}";
    }

    // The comma-separated commands in command, each led by the scheme's primary verb for its command type.
    private static string InScheme(string command, CommandScheme scheme) =>
        string.Join(", ", command.Split(',').Select(piece => InSchemePiece(piece.Trim(), scheme)));

    // piece with its first space-delimited word replaced by the scheme's primary verb for it, the rest unchanged.
    private static string InSchemePiece(string piece, CommandScheme scheme)
    {
        int space = piece.IndexOf(' ', StringComparison.Ordinal);
        string verb = (space < 0) ? piece : piece[..space];
        if (verb.Length == 0)
        {
            return piece;
        }

        return MenuAliases.PrimaryVerb(verb, scheme) + ((space < 0) ? "" : piece[space..]);
    }
}
