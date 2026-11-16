namespace Yaat.Client.Views.Scope.FieldMenus;

/// <summary>
/// The text a Shift+row-click on a Scope field menu stages in the Scope's preview. It always names the aircraft after the
/// <c>PILOT</c> word, which keeps a command whose verb is also a dialect verb (<c>DM</c>, <c>LA</c>, <c>QQ</c>) reading as a
/// pilot command (<see cref="ScopeEntryClassifier"/>).
/// </summary>
internal static class ScopeEntryStaging
{
    private const string PilotWord = "PILOT";

    /// <summary>
    /// <paramref name="preview"/> with <paramref name="command"/> appended after a comma when it already holds a pilot entry for
    /// <paramref name="callsign"/> (both words matched case-insensitively), else <c>PILOT {callsign} {command}</c> in its place.
    /// </summary>
    public static string Text(string preview, string callsign, string command)
    {
        string current = preview.Trim();
        string[] words = current.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        bool samePilotEntry =
            (words.Length == 3)
            && string.Equals(words[0], PilotWord, StringComparison.OrdinalIgnoreCase)
            && string.Equals(words[1], callsign, StringComparison.OrdinalIgnoreCase);
        return samePilotEntry ? $"{current}, {command}" : $"{PilotWord} {callsign} {command}";
    }
}
