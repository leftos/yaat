namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Identifies a physical runway by both end designators (e.g., "28R" and "10L").
/// Provides matching semantics: strict equality (order-independent), single-end
/// containment, and overlap detection.
/// </summary>
public readonly struct RunwayIdentifier : IEquatable<RunwayIdentifier>
{
    public string End1 { get; }
    public string End2 { get; }

    public RunwayIdentifier(string end1, string end2)
    {
        End1 = end1;
        End2 = end2;
    }

    /// <summary>
    /// Create from a single designator, inferring the opposite end.
    /// E.g., "10L" → End1="10L", End2="28R".
    /// </summary>
    public RunwayIdentifier(string designator)
    {
        End1 = designator;
        End2 = ComputeOpposite(designator);
    }

    /// <summary>
    /// Parse a combined runway string in "/" or " - " format.
    /// E.g., "28R/10L", "28R - 10L", or just "28R" (infers opposite).
    /// </summary>
    public static RunwayIdentifier Parse(string input)
    {
        int slashIdx = input.IndexOf('/');
        if (slashIdx >= 0)
        {
            return new RunwayIdentifier(input[..slashIdx].Trim(), input[(slashIdx + 1)..].Trim());
        }

        int dashIdx = input.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx >= 0)
        {
            return new RunwayIdentifier(input[..dashIdx].Trim(), input[(dashIdx + 3)..].Trim());
        }

        return new RunwayIdentifier(input.Trim());
    }

    /// <summary>
    /// Compute the opposite runway designator. 10L→28R, 10C→28C, 10→28.
    /// </summary>
    internal static string ComputeOpposite(string designator)
    {
        // Extract numeric part and optional suffix (L/R/C)
        int numLen = 0;
        for (int i = 0; i < designator.Length; i++)
        {
            if (char.IsAsciiDigit(designator[i]))
            {
                numLen = i + 1;
            }
            else
            {
                break;
            }
        }

        if (numLen == 0)
        {
            return designator;
        }

        int number = int.Parse(designator[..numLen]);
        string suffix = designator[numLen..];

        // Opposite number: add/subtract 18 (mod 36, with 0→36)
        int opposite = number <= 18 ? number + 18 : number - 18;
        if (opposite <= 0)
        {
            opposite += 36;
        }

        if (opposite > 36)
        {
            opposite -= 36;
        }

        // Flip L↔R, keep C
        string oppSuffix = suffix.ToUpperInvariant() switch
        {
            "L" => "R",
            "R" => "L",
            _ => suffix,
        };

        return $"{opposite}{oppSuffix}";
    }

    /// <summary>
    /// Returns true if <paramref name="designator"/> matches either end
    /// (case-insensitive).
    /// </summary>
    public bool Contains(string designator)
    {
        return string.Equals(End1, designator, StringComparison.OrdinalIgnoreCase)
            || string.Equals(End2, designator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if this identifier and <paramref name="other"/> share
    /// at least one designator (i.e., they reference the same physical runway).
    /// </summary>
    public bool Overlaps(RunwayIdentifier other)
    {
        return Contains(other.End1) || Contains(other.End2);
    }

    public bool Equals(RunwayIdentifier other)
    {
        // Order-independent: (28R,10L) == (10L,28R)
        return (Eq(End1, other.End1) && Eq(End2, other.End2)) || (Eq(End1, other.End2) && Eq(End2, other.End1));
    }

    public override bool Equals(object? obj)
    {
        return obj is RunwayIdentifier other && Equals(other);
    }

    public override int GetHashCode()
    {
        // Order-independent hash
        int h1 = StringComparer.OrdinalIgnoreCase.GetHashCode(End1);
        int h2 = StringComparer.OrdinalIgnoreCase.GetHashCode(End2);
        return h1 ^ h2;
    }

    /// <summary>
    /// Returns "{End1}/{End2}" preserving construction order.
    /// </summary>
    public override string ToString()
    {
        return string.Equals(End1, End2, StringComparison.OrdinalIgnoreCase) ? End1 : $"{End1}/{End2}";
    }

    public static bool operator ==(RunwayIdentifier left, RunwayIdentifier right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(RunwayIdentifier left, RunwayIdentifier right)
    {
        return !left.Equals(right);
    }

    private static bool Eq(string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
