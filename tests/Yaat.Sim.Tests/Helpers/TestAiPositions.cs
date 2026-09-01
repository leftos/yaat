using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>Builds <see cref="AiPositionConfig"/>s for real ZOA positions (from <see cref="TestArtccConfig.LoadZoa"/>).</summary>
public static class TestAiPositions
{
    public static AiPositionConfig OakGround(ArtccConfigRoot config) => TowerCab(config, "OAK_GND", ControlRole.Ground, "OAK");

    public static AiPositionConfig OakTower(ArtccConfigRoot config) => TowerCab(config, "OAK_TWR", ControlRole.Local, "OAK");

    public static AiPositionConfig SfoGround(ArtccConfigRoot config) => TowerCab(config, "SFO_GND", ControlRole.Ground, "SFO");

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
