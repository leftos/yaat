using System.Collections.Immutable;
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
/// <see cref="InhibitStartSeconds"/> and <see cref="SupersededConfigurationIds"/> are derived and snapshotted the same
/// way: the sim second each inhibited position's inhibit was set (it lapses <see cref="InhibitLapseSeconds"/> later), and
/// each runway configuration an <c>ARWY</c> re-pick moved away from with the second of the re-pick (a push carrying it
/// within <see cref="SupersededIdWindowSeconds"/> is a stale display's, not a choice).
/// </summary>
public sealed record AsdexSafetyLogicConfig(
    IReadOnlyList<AsdexRunwayConfig> Runways,
    string RunwayConfigurationId,
    IReadOnlyList<string> InhibitedArrivalAlertPositionIds,
    [property: JsonIgnore] bool RunwayConfigurationChosen,
    IReadOnlyDictionary<string, double> InhibitStartSeconds,
    IReadOnlyDictionary<string, double> SupersededConfigurationIds
)
{
    // Off the recording and the wire; a recorded push read back gets null for both parameters, which is no seconds.
    [JsonIgnore]
    public IReadOnlyDictionary<string, double> InhibitStartSeconds { get; init; } = InhibitStartSeconds ?? NoSeconds;

    [JsonIgnore]
    public IReadOnlyDictionary<string, double> SupersededConfigurationIds { get; init; } = SupersededConfigurationIds ?? NoSeconds;

    /// <summary>A tower position's arrival-alert inhibit lapses this many sim seconds after it was set.</summary>
    public const double InhibitLapseSeconds = 180;

    /// <summary>A runway configuration an <c>ARWY</c> re-pick moved away from stays superseded this many sim seconds.</summary>
    public const double SupersededIdWindowSeconds = 10;

    /// <summary>The empty id → sim-second map, ordinal, shared so records compare equal by reference.</summary>
    public static ImmutableSortedDictionary<string, double> NoSeconds { get; } =
        ImmutableSortedDictionary.Create<string, double>(StringComparer.Ordinal);

    /// <summary>An ordinal id → sim-second map of <paramref name="entries"/>, <see cref="NoSeconds"/> when there are none.</summary>
    public static IReadOnlyDictionary<string, double> Seconds(IEnumerable<KeyValuePair<string, double>> entries)
    {
        ImmutableSortedDictionary<string, double> map = NoSeconds.AddRange(entries);
        return map.Count == 0 ? NoSeconds : map;
    }

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
            InhibitStartSeconds = InhibitStartSeconds.Count == 0 ? null : new Dictionary<string, double>(InhibitStartSeconds),
            SupersededConfigurationIds = SupersededConfigurationIds.Count == 0 ? null : new Dictionary<string, double>(SupersededConfigurationIds),
        };

    /// <summary>
    /// The configuration <paramref name="dto"/> holds. A snapshot written before inhibit starts were kept carries an
    /// inhibited position with no start; it restores as set at <paramref name="snapshotSeconds"/>, the snapshot's own second.
    /// </summary>
    public static AsdexSafetyLogicConfig FromSnapshot(AsdexSafetyLogicConfigDto dto, double snapshotSeconds)
    {
        Dictionary<string, double> starts = dto.InhibitStartSeconds ?? [];
        return new(
            [.. dto.Runways.Select(r => new AsdexRunwayConfig(r.Id, [.. r.AreaPoints], r.IsClosed))],
            dto.RunwayConfigurationId,
            [.. dto.InhibitedArrivalAlertPositionIds],
            dto.RunwayConfigurationChosen,
            Seconds(
                dto.InhibitedArrivalAlertPositionIds.Distinct().Select(id => KeyValuePair.Create(id, starts.GetValueOrDefault(id, snapshotSeconds)))
            ),
            Seconds(dto.SupersededConfigurationIds ?? [])
        );
    }
}

/// <summary>
/// One runway in the safety-logic configuration: its identifier (e.g. "28R"), the footprint ring the display supplied,
/// and whether the configuration marks it closed. The detector's <see cref="AsdexRunwaySurface"/> is built from this
/// plus the navdata the runway id resolves to.
/// </summary>
public sealed record AsdexRunwayConfig(string Id, IReadOnlyList<LatLon> AreaPoints, bool IsClosed);
