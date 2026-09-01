using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>Builds <see cref="AiPositionConfig"/>s for real ZOA positions (from <see cref="TestArtccConfig.LoadZoa"/>).</summary>
public static class TestAiPositions
{
    public static AiPositionConfig OakGround(ArtccConfigRoot config) => TowerCab(config, "OAK_GND", ControlRole.Ground, "OAK");

    public static AiPositionConfig OakTower(ArtccConfigRoot config) => TowerCab(config, "OAK_TWR", ControlRole.Local, "OAK");

    public static AiPositionConfig SfoGround(ArtccConfigRoot config) => TowerCab(config, "SFO_GND", ControlRole.Ground, "SFO");

    /// <summary>The combined-area NCT_APP as the resolver builds it (Approach, TCP from the config, every underlying airport).</summary>
    public static AiPositionConfig NorCalApproach(ArtccConfigRoot config) =>
        AiPositionResolver
            .Catalog(config, "OAK", new Dictionary<string, ControlRole>(StringComparer.Ordinal))
            .Where(p => p.Callsign == "NCT_APP")
            .OrderByDescending(p => p.AirportIds.Count)
            .ThenBy(p => p.PositionId, StringComparer.Ordinal)
            .First();

    /// <summary>The resolver's OAK catalog entry with this callsign (first by position id when the callsign repeats).</summary>
    public static AiPositionConfig FromCatalog(ArtccConfigRoot config, string primaryAirportId, string callsign) =>
        AiPositionResolver
            .Catalog(config, primaryAirportId, new Dictionary<string, ControlRole>(StringComparer.Ordinal))
            .First(p => string.Equals(p.Callsign, callsign, StringComparison.OrdinalIgnoreCase));

    public static AiPositionConfig TowerCab(ArtccConfigRoot config, string callsign, ControlRole role, string airportId)
    {
        var position = config.FindPositionByCallsign(callsign) ?? throw new InvalidOperationException($"{callsign} not in the ZOA fixture");
        var identity = config.ResolvePosition(position.Id) ?? throw new InvalidOperationException($"{callsign} did not resolve to a TrackOwner");
        var facility = config.FindFacilityForPositionCallsign(callsign) ?? throw new InvalidOperationException($"{callsign} has no facility");
        return new AiPositionConfig(
            role,
            identity,
            config.GetTcpForPosition(position.Id),
            position.Id,
            callsign,
            position.RadioName,
            facility.Id,
            [airportId]
        );
    }
}
