using Yaat.Sim.Commands;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Type to jump in a rich-row picker over rows worth <paramref name="values"/> (null for a row typing never lands on,
/// such as the MVA line). Each key extends an upper-cased buffer, which resolves as an altitude argument does
/// (<see cref="AltitudeResolver"/>: <c>35</c> is 3,500 ft, <c>350</c> 35,000 ft) after an optional <c>FL</c>, to the
/// row nearest it. The buffer empties when a key comes <see cref="ResetAfterMilliseconds"/> or more after the last one.
/// Key times are the caller's, so the class reads no clock.
/// </summary>
internal sealed class MenuTypeAhead(IReadOnlyList<int?> values)
{
    /// <summary>How long without a key empties the buffer.</summary>
    public const long ResetAfterMilliseconds = 1500;

    private long? _lastKeyAt;

    /// <summary>The text typed so far, upper-cased.</summary>
    public string Buffer { get; private set; } = "";

    /// <summary>
    /// Adds <paramref name="key"/> typed at <paramref name="nowMilliseconds"/>; the index of the row to select, or null to
    /// keep the selection.
    /// </summary>
    public int? Type(char key, long nowMilliseconds)
    {
        ExpireIfIdle(nowMilliseconds);
        Buffer += char.ToUpperInvariant(key);
        _lastKeyAt = nowMilliseconds;
        return Resolve();
    }

    /// <summary>
    /// Removes the buffer's last character at <paramref name="nowMilliseconds"/>; the index of the row to select, or null
    /// to keep the selection.
    /// </summary>
    public int? Backspace(long nowMilliseconds)
    {
        ExpireIfIdle(nowMilliseconds);
        Buffer = (Buffer.Length > 0) ? Buffer[..^1] : Buffer;
        _lastKeyAt = nowMilliseconds;
        return Resolve();
    }

    private void ExpireIfIdle(long nowMilliseconds)
    {
        if ((_lastKeyAt is { } last) && ((nowMilliseconds - last) >= ResetAfterMilliseconds))
        {
            Buffer = "";
        }
    }

    /// <summary>
    /// The row nearest the buffer's altitude, the first of two equally near; null when the buffer is no altitude or no row
    /// has a value.
    /// </summary>
    private int? Resolve()
    {
        string digits = Buffer.StartsWith("FL", StringComparison.Ordinal) ? Buffer[2..] : Buffer;
        if ((digits.Length == 0) || !digits.All(char.IsAsciiDigit) || (AltitudeResolver.Resolve(digits) is not { } altitude))
        {
            return null;
        }

        int? best = null;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } value)
            {
                continue;
            }

            int distance = Math.Abs(value - altitude);
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }
}
