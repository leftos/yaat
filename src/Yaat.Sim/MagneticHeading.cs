namespace Yaat.Sim;

/// <summary>
/// Heading in degrees referenced to magnetic north.
/// Used by user commands, controller instructions, display.
/// </summary>
public readonly record struct MagneticHeading(double Degrees)
{
    /// <summary>Normalized to [0, 360).</summary>
    public double Degrees { get; } = ((Degrees % 360.0) + 360.0) % 360.0;

    // --- Frame conversion ---

    public TrueHeading ToTrue(double declination) => new(Degrees + declination);

    public static MagneticHeading FromTrue(TrueHeading trueHdg, double declination) => trueHdg.ToMagnetic(declination);

    // --- Geometric helpers ---

    /// <summary>Reciprocal heading (180° opposite).</summary>
    public MagneticHeading ToReciprocal() => new(Degrees + 180.0);

    /// <summary>Degrees converted to radians for trig math.</summary>
    public double ToRadians() => Degrees * (Math.PI / 180.0);

    /// <summary>Display-format as 001..360 (never zero).</summary>
    public int ToDisplayInt() => Degrees < 0.5 ? 360 : (int)Math.Round(Degrees);

    /// <summary>Signed angle from this heading to <paramref name="other"/>, in [-180, 180).</summary>
    public double SignedAngleTo(MagneticHeading other)
    {
        double diff = other.Degrees - Degrees;
        diff %= 360.0;
        if (diff > 180.0)
        {
            diff -= 360.0;
        }

        if (diff < -180.0)
        {
            diff += 360.0;
        }

        return diff;
    }

    /// <summary>Absolute angular difference to <paramref name="other"/>, in [0, 180].</summary>
    public double AbsAngleTo(MagneticHeading other) => Math.Abs(SignedAngleTo(other));

    /// <summary>True if this heading is within <paramref name="toleranceDeg"/> of <paramref name="other"/>.</summary>
    public bool IsCloseTo(MagneticHeading other, double toleranceDeg) => AbsAngleTo(other) < toleranceDeg;

    // --- Arithmetic ---

    public static MagneticHeading operator +(MagneticHeading h, double d) => new(h.Degrees + d);

    public static MagneticHeading operator -(MagneticHeading h, double d) => new(h.Degrees - d);

    public static MagneticHeading operator +(double d, MagneticHeading h) => new(d + h.Degrees);

    /// <summary>Difference between two magnetic headings — raw double (angle delta, NOT normalized).</summary>
    public static double operator -(MagneticHeading a, MagneticHeading b) => a.Degrees - b.Degrees;

    public override string ToString() => $"{Degrees:F1}°M";
}
