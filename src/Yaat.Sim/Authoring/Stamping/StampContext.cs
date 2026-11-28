namespace Yaat.Sim.Authoring.Stamping;

/// <summary>What a stamp draws on besides the aircraft, the route and the profile: its clock, its random sources and the callsigns in use.</summary>
/// <param name="Now">The time each new preset id is made at.</param>
/// <param name="PoolRandom">The source of every pool pick; a profile with no pool draws nothing from it.</param>
/// <param name="IdRandom">The source of the random bits of each new preset id.</param>
/// <param name="TakenCallsigns">Callsigns a random callsign must not repeat.</param>
/// <param name="MagneticModelDateUtc">The date the magnetic model is evaluated at for the direction of flight, as the route composer uses.</param>
public sealed record StampContext(
    DateTimeOffset Now,
    Random PoolRandom,
    Random IdRandom,
    IReadOnlySet<string> TakenCallsigns,
    DateTime MagneticModelDateUtc
);
