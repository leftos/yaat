namespace Yaat.Client.Views.Scope.FieldMenus;

/// <summary>
/// The text a Shift+row-click on a Scope field menu stages in the Scope's preview. It is a <c>$</c> YAAT command naming the
/// aircraft, which keeps a command whose verb is also a dialect verb (<c>DM</c>, <c>LA</c>, <c>QQ</c>) reading as a YAAT command
/// (<see cref="ScopeEntryClassifier"/>).
/// </summary>
internal static class ScopeEntryStaging
{
    /// <summary>
    /// <paramref name="preview"/> with <paramref name="command"/> appended after a comma when it already holds a <c>$</c> command
    /// for <paramref name="callsign"/> (matched case-insensitively), else <c>$ {callsign} {command}</c> in its place.
    /// </summary>
    public static string Text(string preview, string callsign, string command)
    {
        string current = preview.Trim();
        bool sameYaatEntry = false;
        if ((current.Length > 0) && (current[0] == ScopeEntryClassifier.YaatPrefix))
        {
            string[] words = current[1..].TrimStart().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            sameYaatEntry = (words.Length == 2) && string.Equals(words[0], callsign, StringComparison.OrdinalIgnoreCase);
        }

        return sameYaatEntry ? $"{current}, {command}" : $"{ScopeEntryClassifier.YaatPrefix} {callsign} {command}";
    }
}
