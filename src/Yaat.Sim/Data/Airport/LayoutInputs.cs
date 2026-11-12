namespace Yaat.Sim.Data.Airport;

/// <summary>
/// The per-airport inputs a ground layout is built from besides its map: the sidecar stand lead-ins and spot-edge chains,
/// and the airport's tower-cab videomap. Every input is positional, so no caller can leave a sidecar section out unseen.
/// </summary>
/// <param name="StandLeadIns">
/// The airport's sidecar stand lead-ins (<see cref="AirportSidecarCatalog.GetStandLeadIns"/>): each listed stand is
/// connected to its named taxiway's vertex instead of the nearest taxiway edge.
/// </param>
/// <param name="VideoMap">The airport's tower-cab videomap (<see cref="TowerCabVideoMap.Read"/>), or null when it has none.</param>
/// <param name="SpotEdges">
/// The airport's sidecar spot-edge chains (<see cref="AirportSidecarCatalog.GetSpotEdges"/>): the parser adds each
/// chain's edges, joining its points in order, after the stand connectors and before the fillets. Empty when the airport
/// has none.
/// </param>
public sealed record LayoutInputs(IReadOnlyList<StandLeadIn> StandLeadIns, TowerCabVideoMap? VideoMap, IReadOnlyList<SpotEdgeChain> SpotEdges);
