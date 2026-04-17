namespace Yaat.Client.Services;

/// <summary>
/// Builds the string stored in <c>MainViewModel.CommandHistory</c> for up-arrow recall
/// when a per-aircraft command has been successfully dispatched.
/// </summary>
internal static class CommandHistoryFormatter
{
    /// <summary>
    /// Returns the history entry to insert for a dispatched per-aircraft command.
    /// The leading callsign the command operated on is never stored, so up-arrow recall
    /// lets the RPO rerun the same command on a different aircraft without editing out
    /// the previous callsign. When <paramref name="resolvedCallsign"/> is non-null
    /// (the user typed a leading callsign token), the entry is the canonical command
    /// alone — any partial-match or lowercase input is replaced by the canonical form.
    /// When it is null (implicit target via the selected aircraft), the raw input is
    /// kept verbatim since it already contains no callsign prefix to strip.
    /// </summary>
    public static string Format(string rawInput, string? resolvedCallsign, string canonicalCommand)
    {
        if (string.IsNullOrEmpty(resolvedCallsign))
        {
            return rawInput;
        }

        return canonicalCommand;
    }
}
