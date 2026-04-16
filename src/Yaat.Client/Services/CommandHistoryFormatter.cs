namespace Yaat.Client.Services;

/// <summary>
/// Builds the string stored in <c>MainViewModel.CommandHistory</c> for up-arrow recall
/// when a per-aircraft command has been successfully dispatched.
/// </summary>
internal static class CommandHistoryFormatter
{
    /// <summary>
    /// Returns the history entry to insert for a dispatched per-aircraft command.
    /// When <paramref name="resolvedCallsign"/> is non-null (the user typed a leading
    /// callsign token that resolved to a specific aircraft), the entry is the canonical
    /// callsign followed by the canonical command — so up-arrow recalls the dispatched
    /// form, not the partial-match input. When it is null (implicit target via the
    /// selected aircraft), the raw input is kept verbatim — we don't invent a callsign
    /// the user didn't type.
    /// </summary>
    public static string Format(string rawInput, string? resolvedCallsign, string canonicalCommand)
    {
        if (string.IsNullOrEmpty(resolvedCallsign))
        {
            return rawInput;
        }

        return $"{resolvedCallsign} {canonicalCommand}";
    }
}
