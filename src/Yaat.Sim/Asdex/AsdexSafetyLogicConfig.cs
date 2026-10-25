using System.Text.Json.Serialization;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Asdex;

/// <summary>
/// The ASDE-X Safety Logic configuration of one facility, seeded at load (<see cref="AsdexSafetyLogicSeed"/>) or pushed
/// by a CRC surface display: the runway footprints the detector watches, the id of the runway configuration they came
/// from, and the positions whose arrival alerts are inhibited. Scenario state, per facility
/// (<see cref="Simulation.SimScenarioState.AsdexSafetyLogicConfigs"/>), and snapshotted, so every run kind carries it
/// and a rewind onto a snapshot that predates a push returns to what the snapshot holds instead of keeping the
/// configuration the live room happened to be holding. <see cref="RunwayConfigurationChosen"/> is set once a CRC push
/// names a different runway configuration than the facility held: from then on a change of the active runways no
/// longer re-picks it. Derived when a push applies: the CRC wire DTO has no such field, and <c>JsonIgnore</c> keeps it
/// out of the recorded push, so a replay derives it again; snapshots carry it
/// (<see cref="AsdexSafetyLogicConfigDto.RunwayConfigurationChosen"/>).
/// </summary>
public sealed record AsdexSafetyLogicConfig(
    IReadOnlyList<AsdexRunwayConfig> Runways,
    string RunwayConfigurationId,
    IReadOnlyList<string> InhibitedArrivalAlertPositionIds,
    [property: JsonIgnore] bool RunwayConfigurationChosen
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
            RunwayConfigurationChosen = RunwayConfigurationChosen,
        };

    public static AsdexSafetyLogicConfig FromSnapshot(AsdexSafetyLogicConfigDto dto) =>
        new(
            [.. dto.Runways.Select(r => new AsdexRunwayConfig(r.Id, [.. r.AreaPoints], r.IsClosed))],
            dto.RunwayConfigurationId,
            [.. dto.InhibitedArrivalAlertPositionIds],
            dto.RunwayConfigurationChosen
        );
}

/// <summary>
/// One runway in the safety-logic configuration: its identifier (e.g. "28R"), the footprint ring the display supplied,
/// and whether the configuration marks it closed. The detector's <see cref="AsdexRunwaySurface"/> is built from this
/// plus the navdata the runway id resolves to.
/// </summary>
public sealed record AsdexRunwayConfig(string Id, IReadOnlyList<LatLon> AreaPoints, bool IsClosed);
