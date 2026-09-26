namespace Yaat.Sim.Simulation.Coast;

/// <summary>A facet <see cref="SimulationEngine.TickDisconnectCoastExpiry"/> expired, with the callsign whose entry held it.</summary>
public sealed record ExpiredDisconnectCoastFacet(string Callsign, DisconnectCoastFacet Facet);
