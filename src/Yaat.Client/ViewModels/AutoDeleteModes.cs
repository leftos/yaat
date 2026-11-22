namespace Yaat.Client.ViewModels;

/// <summary>
/// The auto-delete modes the client offers, in the order its pickers list them: the default (null, the server's or the
/// scenario's own choice), Never, OnLanding and Parked. The live session's picker and the Scenario Editor's General tab
/// both read them here, so the two always offer the same modes.
/// </summary>
public static class AutoDeleteModes
{
    /// <summary>Gets the modes in picker order; the first, null, is the default.</summary>
    public static IReadOnlyList<string?> Ordered { get; } = [null, "Never", "OnLanding", "Parked"];

    /// <summary>The picker index of a mode: its place in <see cref="Ordered"/>, compared exactly.</summary>
    /// <param name="mode">The mode, or null for the default.</param>
    /// <returns>The index; 0 (the default) for null and for any mode not listed.</returns>
    public static int ToIndex(string? mode)
    {
        if (mode is null)
        {
            return 0;
        }

        for (int i = 1; i < Ordered.Count; i++)
        {
            if (string.Equals(Ordered[i], mode, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>The mode at a picker index.</summary>
    /// <param name="index">The index.</param>
    /// <returns>The mode; null (the default) for 0 and for any index out of range.</returns>
    public static string? FromIndex(int index) => ((index > 0) && (index < Ordered.Count)) ? Ordered[index] : null;
}
