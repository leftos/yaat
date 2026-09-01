using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.ControllerAi;

/// <summary>
/// The controller AI's session configuration: which vNAS positions it staffs, per-position role overrides (a
/// clearance-delivery position played as Ground, say), and the seed of its own RNG stream. Lives on
/// <c>SimScenarioState.ControllerAi</c> and rides the scenario snapshot so a rewind keeps the AI on.
/// </summary>
public sealed class ControllerAiConfig
{
    public required int Seed { get; init; }

    public required IReadOnlyList<string> EnabledPositionIds { get; init; }

    public required IReadOnlyDictionary<string, ControlRole> RoleOverrides { get; init; }

    public ControllerAiConfigDto ToSnapshot() =>
        new()
        {
            Seed = Seed,
            EnabledPositionIds = EnabledPositionIds.ToList(),
            RoleOverrides = RoleOverrides
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal),
        };

    public static ControllerAiConfig FromSnapshot(ControllerAiConfigDto dto) =>
        new()
        {
            Seed = dto.Seed,
            EnabledPositionIds = dto.EnabledPositionIds.ToList(),
            RoleOverrides = dto.RoleOverrides.ToDictionary(
                kv => kv.Key,
                kv => Enum.Parse<ControlRole>(kv.Value, ignoreCase: true),
                StringComparer.Ordinal
            ),
        };
}
