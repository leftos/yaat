using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Asdex;

/// <summary>
/// The ASDE-X Safety Logic configuration a CRC surface display pushes for a facility: the runway footprints the
/// detector watches, the id of the runway configuration they came from, and the positions whose arrival alerts are
/// inhibited. Scenario state (<see cref="Simulation.SimScenarioState.AsdexSafetyLogicConfig"/>) and snapshotted, so
/// every run kind carries it and a rewind onto a snapshot that predates a push rebuilds it from the action log
/// instead of keeping the configuration the live room happened to be holding.
/// </summary>
public sealed record AsdexSafetyLogicConfig(
    IReadOnlyList<AsdexRunwayConfig> Runways,
    string RunwayConfigurationId,
    IReadOnlyList<string> InhibitedArrivalAlertPositionIds
)
{
    public AsdexSafetyLogicConfigDto ToSnapshot() =>
        new()
        {
            Runways =
            [
                .. Runways.Select(r => new AsdexRunwayConfigDto
                {
                    Id = r.Id,
                    AreaPoints = [.. r.AreaPoints],
                    IsClosed = r.IsClosed,
                }),
            ],
            RunwayConfigurationId = RunwayConfigurationId,
            InhibitedArrivalAlertPositionIds = [.. InhibitedArrivalAlertPositionIds],
        };

    public static AsdexSafetyLogicConfig FromSnapshot(AsdexSafetyLogicConfigDto dto) =>
        new(
            [.. dto.Runways.Select(r => new AsdexRunwayConfig(r.Id, [.. r.AreaPoints], r.IsClosed))],
            dto.RunwayConfigurationId,
            [.. dto.InhibitedArrivalAlertPositionIds]
        );
}

/// <summary>
/// One runway in the safety-logic configuration: its identifier (e.g. "28R"), the footprint ring the display supplied,
/// and whether the configuration marks it closed. The detector's <see cref="AsdexRunwaySurface"/> is built from this
/// plus the navdata the runway id resolves to.
/// </summary>
public sealed record AsdexRunwayConfig(string Id, IReadOnlyList<LatLon> AreaPoints, bool IsClosed);
