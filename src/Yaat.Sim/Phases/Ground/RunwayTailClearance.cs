namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// The one tail-clear margin past a runway holding position marking: a runway exit, a bare runway crossing and a
/// <c>CLRWY</c> stop all leave every part of the aircraft this far past the marking (AIM 4-3-21.b).
/// </summary>
public static class RunwayTailClearance
{
    /// <summary>
    /// The margin (ft) past the runway holding position marking every runway exit, bare crossing and <c>CLRWY</c> stop keeps,
    /// so all parts of the aircraft are past the marking (AIM 4-3-21.b) rather than the tail on the line itself.
    /// </summary>
    public const double MarginFt = 10.0;

    /// <summary>
    /// How far (ft) past the hold-short node the aircraft's reference point stops so its tail is <see cref="MarginFt"/> past
    /// the marking: half of <paramref name="aircraftLengthFt"/> plus the margin.
    /// </summary>
    public static double PastBarFt(double aircraftLengthFt) => (aircraftLengthFt / 2.0) + MarginFt;
}
