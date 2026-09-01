using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.ControllerAi;

/// <summary>
/// Turns the ARTCC config into the positions the controller AI can play for a primary airport: the airport's tower-cab
/// facility and every facility above it (its TRACON, the ARTCC). Roles come from the position itself — an ERAM sector
/// is Center, a callsign suffix decides the rest (GND → Ground, TWR → Local, APP/DEP → Approach, CTR → Center) — with
/// per-position overrides on top. Clearance delivery (<c>_DEL</c>) is left out unless overridden: taxi information is
/// ground control's (AIM 4-3-14.c), and the pilot's "ready to taxi" must never be answered under the "… Clearance
/// Delivery" call name (AIM TBL 4-2-1). ATIS / ramp positions have no classifiable suffix and are skipped.
/// </summary>
public static class AiPositionResolver
{
    /// <summary>Every AI-playable position for <paramref name="primaryAirportId"/>, sorted by (role rank, position id).</summary>
    public static IReadOnlyList<AiPositionConfig> Catalog(
        ArtccConfigRoot config,
        string primaryAirportId,
        IReadOnlyDictionary<string, ControlRole> roleOverrides
    )
    {
        var cab =
            FindCabFacility(config.Facility, primaryAirportId)
            ?? throw new InvalidOperationException($"ARTCC {config.Id} has no tower-cab facility for airport '{primaryAirportId}'");
        var path = new List<FacilityConfig>();
        if (!ArtccConfigResolver.FindFacilityPath(config.Facility, cab.Id, path))
        {
            throw new InvalidOperationException($"Facility '{cab.Id}' is not reachable from the ARTCC root {config.Facility.Id}");
        }

        var catalog = new List<AiPositionConfig>();
        for (int depth = 0; depth < path.Count; depth++)
        {
            var facility = path[depth];
            var ancestry = path.GetRange(0, depth + 1);
            foreach (var position in facility.Positions)
            {
                if (InferRole(position, roleOverrides) is not { } role)
                {
                    continue;
                }

                catalog.Add(Build(config, facility, ancestry, position, role));
            }
        }

        return Sort(catalog);
    }

    /// <summary>The enabled positions of <paramref name="aiConfig"/> as playable configs, sorted by (role rank, position id).</summary>
    public static IReadOnlyList<AiPositionConfig> Resolve(ArtccConfigRoot config, string primaryAirportId, ControllerAiConfig aiConfig)
    {
        var catalog = Catalog(config, primaryAirportId, aiConfig.RoleOverrides).ToDictionary(p => p.PositionId, StringComparer.Ordinal);
        var resolved = new List<AiPositionConfig>();
        foreach (var positionId in aiConfig.EnabledPositionIds)
        {
            if (!catalog.TryGetValue(positionId, out var position))
            {
                throw new InvalidOperationException($"AI position id '{positionId}' is not in the {primaryAirportId} catalog of ARTCC {config.Id}");
            }

            resolved.Add(position);
        }

        return Sort(resolved);
    }

    /// <summary>
    /// The role a position plays: the override when one is given; Center for an ERAM sector; else by callsign suffix
    /// (GND → Ground, TWR → Local, APP/DEP → Approach). Null for clearance delivery without an override and for
    /// positions with no classifiable suffix (ATIS, ramp).
    /// </summary>
    public static ControlRole? InferRole(PositionConfig position, IReadOnlyDictionary<string, ControlRole> roleOverrides)
    {
        if (roleOverrides.TryGetValue(position.Id, out var overridden))
        {
            return overridden;
        }

        if (position.EramConfiguration is not null)
        {
            return ControlRole.Center;
        }

        var suffix = position.Callsign.LastIndexOf('_') is var underscore and >= 0 ? position.Callsign[(underscore + 1)..] : "";
        if (suffix.Equals("DEL", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return AtcPositionTypeClassifier.Classify(position.Callsign) switch
        {
            "GND" => ControlRole.Ground,
            "TWR" => ControlRole.Local,
            "APP" => ControlRole.Approach,
            "CTR" => ControlRole.Center,
            _ => null,
        };
    }

    private static AiPositionConfig Build(
        ArtccConfigRoot config,
        FacilityConfig facility,
        IReadOnlyList<FacilityConfig> ancestry,
        PositionConfig position,
        ControlRole role
    )
    {
        var identity =
            config.ResolvePosition(position.Id)
            ?? throw new InvalidOperationException($"Position {position.Callsign} ({position.Id}) did not resolve to a track owner");
        IReadOnlyList<string> airportIds = role switch
        {
            ControlRole.Ground or ControlRole.Local => [facility.Id],
            ControlRole.Approach => AreaAirports(ancestry, position),
            _ => [],
        };

        return new AiPositionConfig(
            role,
            identity,
            config.GetTcpForPosition(position.Id),
            position.Id,
            position.Callsign,
            string.IsNullOrWhiteSpace(position.RadioName) ? null : position.RadioName.Trim(),
            facility.Id,
            airportIds
        );
    }

    /// <summary>The STARS area's underlying airports, looked up in the nearest facility (self first, then up) that defines the area.</summary>
    private static IReadOnlyList<string> AreaAirports(IReadOnlyList<FacilityConfig> ancestry, PositionConfig position)
    {
        var areaId = position.StarsConfiguration?.AreaId;
        if (string.IsNullOrEmpty(areaId))
        {
            return [];
        }

        for (int i = ancestry.Count - 1; i >= 0; i--)
        {
            var area = ancestry[i].StarsConfiguration?.Areas.FirstOrDefault(a => a.Id == areaId);
            if (area is not null)
            {
                return area.UnderlyingAirports.ToList();
            }
        }

        return [];
    }

    /// <summary>
    /// The tower-cab facility for an airport: the one whose id is the airport's, else (combined ATCT/TRACON facilities
    /// often carry a non-airport id) the first cab facility with a position whose callsign prefix is the airport.
    /// </summary>
    private static FacilityConfig? FindCabFacility(FacilityConfig root, string airportId)
    {
        var cabs = new List<FacilityConfig>();
        CollectTowerCabs(root, cabs);
        return cabs.FirstOrDefault(f => NavigationDatabase.AirportIdsMatch(f.Id, airportId))
            ?? cabs.FirstOrDefault(f => f.Positions.Any(p => CallsignPrefixMatches(p.Callsign, airportId)));
    }

    private static void CollectTowerCabs(FacilityConfig facility, List<FacilityConfig> cabs)
    {
        if (IsTowerCab(facility))
        {
            cabs.Add(facility);
        }

        foreach (var child in facility.ChildFacilities)
        {
            CollectTowerCabs(child, cabs);
        }
    }

    private static bool CallsignPrefixMatches(string callsign, string airportId)
    {
        var underscore = callsign.IndexOf('_');
        return (underscore > 0) && NavigationDatabase.AirportIdsMatch(callsign[..underscore], airportId);
    }

    private static bool IsTowerCab(FacilityConfig facility) => facility.Type is "Atct" or "AtctTracon" or "AtctRapcon";

    private static List<AiPositionConfig> Sort(IEnumerable<AiPositionConfig> positions) =>
        positions.OrderBy(p => ControlRoles.Rank(p.Role)).ThenBy(p => p.PositionId, StringComparer.Ordinal).ToList();
}
