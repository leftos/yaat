namespace Yaat.Sim.Data.Airport;

/// <summary>Which way the tug moves the aircraft over one leg of a ramp reposition.</summary>
public enum PushbackLegKind
{
    /// <summary>Tail-first: the tug reverses the aircraft.</summary>
    Push,

    /// <summary>Nose-first: the tug tows the aircraft forward.</summary>
    Pull,
}

/// <summary>
/// Where a tug-moved aircraft is: the reference point that travels along the aircraft's own axis, and the way the
/// nose points. The heading is normalised to [0, 360) on construction and on every <c>with</c>.
/// </summary>
/// <param name="Position">The aircraft reference point.</param>
/// <param name="NoseTrueDeg">The nose heading, degrees true.</param>
public readonly record struct PushbackPose(LatLon Position, double NoseTrueDeg)
{
    private readonly double _noseTrueDeg = new TrueHeading(NoseTrueDeg).Degrees;

    /// <summary>The nose heading, degrees true, in [0, 360).</summary>
    public double NoseTrueDeg
    {
        get => _noseTrueDeg;
        init => _noseTrueDeg = new TrueHeading(value).Degrees;
    }

    /// <summary>
    /// The direction the reference point travels on a move of <paramref name="kind"/>.
    /// </summary>
    /// <param name="kind">Push travels tail-first, pull travels nose-first.</param>
    /// <returns>Degrees true: the nose's reciprocal on a push, the nose on a pull.</returns>
    public double TravelTrueDeg(PushbackLegKind kind) => FlipForKind(NoseTrueDeg, kind);

    /// <summary>The nose for a direction of travel, or the travel for a nose: the reciprocal on a push, itself on a pull.</summary>
    internal static double FlipForKind(double headingTrueDeg, PushbackLegKind kind) =>
        new TrueHeading(kind == PushbackLegKind.Push ? headingTrueDeg + 180.0 : headingTrueDeg).Degrees;
}
