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

    /// <summary>
    /// The runway in use at the scenario's primary airport for the session (a designator such as <c>30</c>), or null to
    /// let <see cref="RunwayInUseResolver"/> pick from the wind. The scenario/runner's stand-in for the supervisor's
    /// runway designation (7110.65 §3-5-1.a).
    /// </summary>
    public required string? RunwayInUse { get; init; }

    public ControllerAiConfigDto ToSnapshot() =>
        new()
        {
            Seed = Seed,
            RunwayInUse = RunwayInUse,
            EnabledPositionIds = EnabledPositionIds.ToList(),
            RoleOverrides = RoleOverrides
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal),
        };

    public static ControllerAiConfig FromSnapshot(ControllerAiConfigDto dto) =>
        new()
        {
            Seed = dto.Seed,
            RunwayInUse = dto.RunwayInUse,
            EnabledPositionIds = dto.EnabledPositionIds.ToList(),
            RoleOverrides = dto.RoleOverrides.ToDictionary(
                kv => kv.Key,
                kv => Enum.Parse<ControlRole>(kv.Value, ignoreCase: true),
                StringComparer.Ordinal
            ),
        };
}
