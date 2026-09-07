using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Commands.Arguments;

/// <summary>
/// Shape validator for a runway argument: one or two digits with an optional L/C/R suffix
/// (<c>9</c>, <c>09</c>, <c>15</c>, <c>28R</c>), normalised through
/// <see cref="RunwayIdentifier.NormalizeDesignator"/> so a one-digit form matches the stored,
/// zero-padded ends (<c>9</c> → <c>09</c>).
///
/// <para>Pairs with <see cref="AltitudeArgument"/>, and the two accept-sets are disjoint by
/// construction: 1–2 digits is a runway, 3 or more is an altitude. That is what lets a slot which
/// takes either decide from the token alone, without knowing the airport.</para>
///
/// <para>This is a <em>shape</em> test only. Whether the airport actually has the runway is decided
/// at dispatch, which rejects a runway the aircraft's airport does not have rather than silently
/// re-reading it as something else.</para>
/// </summary>
public static class RunwayArgument
{
    /// <summary>Lowest and highest runway number (FAA designators run 01 through 36).</summary>
    private const int MinRunwayNumber = 1;
    private const int MaxRunwayNumber = 36;

    /// <summary>
    /// The normalised designator when <paramref name="token"/> has runway shape, else null.
    /// </summary>
    public static string? TryParse(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var upper = token.ToUpperInvariant();
        int digitCount = upper.Length;
        if (upper[^1] is 'L' or 'C' or 'R')
        {
            digitCount--;
        }

        if (digitCount is < 1 or > 2)
        {
            return null;
        }

        for (int i = 0; i < digitCount; i++)
        {
            if (!char.IsAsciiDigit(upper[i]))
            {
                return null;
            }
        }

        int number = int.Parse(upper[..digitCount]);
        if (number is < MinRunwayNumber or > MaxRunwayNumber)
        {
            return null;
        }

        return RunwayIdentifier.NormalizeDesignator(upper);
    }
}
