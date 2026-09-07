namespace Yaat.Sim.Commands.Arguments;

/// <summary>
/// Shape validator for an altitude argument: everything <see cref="AltitudeResolver"/> reads — the
/// hundreds shorthand (<c>15</c> = 1,500 ft, <c>015</c> = 1,500 ft), full feet (<c>1500</c>), and the
/// AGL form (<c>KOAK+005</c> = 500 ft above the field).
///
/// <para>It deliberately still accepts the two-digit shorthand, because the runway-versus-altitude
/// decision is <em>positional</em> and belongs to <see cref="CommandArgumentResolver"/>, not here:
/// where both types are candidates the resolver binds a runway first (so <c>MLT 15</c> is runway 15),
/// and once the runway slot is bound the remaining slot is an altitude only — so <c>MLT 15 15</c> is
/// runway 15 at 1,500 ft, and <c>MLT 28R 15</c> is 28R at 1,500 ft. Altitude-only slots
/// (<c>CM 15</c>) never had a competition to begin with.</para>
/// </summary>
public static class AltitudeArgument
{
    /// <summary>The altitude in feet MSL when <paramref name="token"/> reads as one, else null.</summary>
    public static int? TryParse(string? token) => AltitudeResolver.Resolve(token);
}
