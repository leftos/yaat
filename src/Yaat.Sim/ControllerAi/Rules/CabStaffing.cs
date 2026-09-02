using Yaat.Sim.Data;

namespace Yaat.Sim.ControllerAi.Rules;

/// <summary>
/// Whether a Local position is held at an airport — by an active AI position or by a human on a catalog Local position.
/// When nobody holds it the cab is combined: Ground works the runway itself and there is no tower to transfer to.
/// </summary>
public static class CabStaffing
{
    public static bool LocalIsStaffed(AiRuleScope scope, string? airport)
    {
        if (string.IsNullOrWhiteSpace(airport))
        {
            return false;
        }

        if (scope.Tick.Staffing.ActivePositions.Any(p => (p.Role == ControlRole.Local) && Covers(p, airport)))
        {
            return true;
        }

        return LocalCatalog(scope, airport).Any(p => scope.Tick.Staffing.IsHumanHeld(p));
    }

    /// <summary>The catalog's Local positions covering the airport, in the resolver's order; empty without an ARTCC config.</summary>
    public static IReadOnlyList<AiPositionConfig> LocalCatalog(AiRuleScope scope, string airport)
    {
        if (scope.Tick.Scenario.ArtccConfig is not { } config)
        {
            return [];
        }

        var overrides = scope.Tick.Scenario.ControllerAi?.RoleOverrides ?? new Dictionary<string, ControlRole>(StringComparer.Ordinal);
        return AiPositionResolver.Catalog(config, airport, overrides).Where(p => (p.Role == ControlRole.Local) && Covers(p, airport)).ToList();
    }

    private static bool Covers(AiPositionConfig position, string airport) =>
        position.AirportIds.Any(id => NavigationDatabase.AirportIdsMatch(id, airport));
}
