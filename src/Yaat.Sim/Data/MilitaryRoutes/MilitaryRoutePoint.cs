namespace Yaat.Sim.Data.MilitaryRoutes;

/// <summary>
/// One published point on a military route: its AP/1B label, its position, and the altitude for the
/// segment terminating there.
/// </summary>
public sealed record MilitaryRoutePoint
{
    /// <summary>The AP/1B label — A..Z, then AA, AB, …; alternates are digit-suffixed (D1, P2, AE3).</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The synthetic fix name this point is registered under, e.g. <c>IR149A</c>.
    /// <para>
    /// Minted by the build tool rather than derived at use sites, so that a collision fallback stays
    /// consistent everywhere. The name is deliberately shaped so
    /// <see cref="FrdResolver.ParseFrd"/> reads it as a plain fix: that method treats a name whose
    /// last three or six characters are all digits as an FRD anchor, and because AP/1B labels always
    /// begin with a letter, a minted name never has three trailing digits.
    /// </para>
    /// </summary>
    public required string Name { get; init; }

    public required LatLon Position { get; init; }

    public required MilitaryRoutePointRole Role { get; init; }

    public required MilitaryRouteAltitude Altitude { get; init; }

    /// <summary>
    /// The published Fac/Rad/Dist for this point in <c>{FIX}{radial:3}{distance:3}</c> form, or null.
    /// Present on roughly 97% of IR and VR points but almost no SR points — AP/1B chapter 4 states
    /// that SR routes frequently omit it, so lat/long is the only universal locator.
    /// </summary>
    public string? Frd { get; init; }

    /// <summary>True for the digit-suffixed alternate entry and exit points printed inline in the table.</summary>
    public bool IsAlternate => Role is MilitaryRoutePointRole.AlternateEntry or MilitaryRoutePointRole.AlternateExit;
}
