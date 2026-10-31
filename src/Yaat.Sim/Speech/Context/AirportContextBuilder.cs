using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Speech.Context;

/// <summary>
/// Collects every entity a controller working one airport can name: its runways, approaches, SIDs and STARs, its taxiways,
/// gates and spots, the fixes within a radius of it, the landmarks the caller supplies, and the named ground places
/// (<see cref="NavigationDatabase.GetGroundPlaces"/>) as landmarks.
/// </summary>
public static class AirportContextBuilder
{
    /// <summary>Builds the airport's entities, deduplicated by kind and value with the first one kept.</summary>
    /// <param name="airportIcao">The airport's ICAO id ("KOAK").</param>
    /// <param name="layout">The airport's ground layout, or null when none is loaded (no taxiways, gates or spots).</param>
    /// <param name="radiusNm">How far from the airport's reference point a fix is included, in nautical miles.</param>
    /// <param name="landmarks">Landmark entities for this airport, built by the caller.</param>
    /// <param name="nav">The navigation database.</param>
    /// <param name="settings">The decoder settings whose priors rank the forms.</param>
    /// <returns>The entities, each with its forms as built.</returns>
    /// <exception cref="InvalidOperationException">The navigation database has no reference point for the airport.</exception>
    public static IReadOnlyList<ContextEntity> Build(
        string airportIcao,
        AirportGroundLayout? layout,
        double radiusNm,
        IReadOnlyList<ContextEntity> landmarks,
        NavigationDatabase nav,
        DecoderSettings settings
    )
    {
        if (nav.GetAirportPosition(airportIcao) is not { } point)
        {
            throw new InvalidOperationException(
                $"Airport '{airportIcao}' has no reference point in the navigation database; its nearby fixes cannot be listed."
            );
        }

        var entities = new List<ContextEntity>();
        var seen = new HashSet<(EntityKind Kind, string Value)>();
        AddNew(entities, seen, ContextEntityFactory.Runways(airportIcao, nav, settings));
        AddNew(entities, seen, ContextEntityFactory.Approaches(airportIcao, nav, settings));
        AddNew(entities, seen, ContextEntityFactory.DeparturesAndArrivals(airportIcao, nav));
        if (layout is not null)
        {
            AddNew(entities, seen, ContextEntityFactory.GroundEntities(airportIcao, layout, nav));
        }

        IReadOnlyList<NearbyFix> fixes = nav.FixesWithin(new LatLon(point.Lat, point.Lon), radiusNm);
        AddNew(entities, seen, fixes.Select(fix => ContextEntityFactory.Fix(fix.Name, nav)));
        AddNew(entities, seen, landmarks);
        AddNew(
            entities,
            seen,
            nav.GetGroundPlaces(airportIcao).Select(place => ContextEntityFactory.Landmark(airportIcao, place.Name, place.Pronunciations))
        );
        return entities;
    }

    private static void AddNew(List<ContextEntity> entities, HashSet<(EntityKind Kind, string Value)> seen, IEnumerable<ContextEntity> candidates)
    {
        foreach (ContextEntity entity in candidates)
        {
            if (seen.Add((entity.Kind, entity.Value)))
            {
                entities.Add(entity);
            }
        }
    }
}
