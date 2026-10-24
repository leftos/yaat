using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Asdex;

/// <summary>
/// The ASDE-X Safety Logic configuration a scenario load starts every ASDE-X facility on, derived from the ARTCC config
/// and navdata rather than recorded. One entry per facility whose <c>asdexConfiguration</c> is present and whose airport
/// has navdata runways. Its runways carry the facility's own ids: the vNAS pair form the facility's positions name
/// ("28R-10L"), unioned over the positions in config order, then every navdata runway no position names as
/// <c>{end1}-{end2}</c> in <see cref="NavigationDatabase.GetRunways"/> order, unpadded. Each footprint is the pavement
/// rectangle, every runway starts open, and no runway configuration id is seeded, so CRC keeps its own default.
/// </summary>
public static class AsdexSafetyLogicSeed
{
    private static readonly ILogger Log = SimLog.CreateLogger("AsdexSafetyLogicSeed");

    /// <summary>The empty per-facility map: no ARTCC config, no ASDE-X facility, or no navdata runways.</summary>
    public static ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> Empty { get; } =
        ImmutableSortedDictionary.Create<string, AsdexSafetyLogicConfig>(StringComparer.Ordinal);

    /// <summary>
    /// Builds the seed for every ASDE-X facility in <paramref name="config"/>, keyed by facility id (ordinal). With no
    /// config or no navdata it is empty; a facility with no navdata runways, and a position runway navdata lacks, are
    /// left out with a debug log.
    /// </summary>
    public static ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> Build(ArtccConfigRoot? config, NavigationDatabase? navDb)
    {
        if ((config is null) || (navDb is null))
        {
            Log.LogDebug("No ASDE-X safety-logic seed: the ARTCC config or the navdata is missing");
            return Empty;
        }

        var seed = Empty.ToBuilder();
        foreach (FacilityConfig facility in Walk(config.Facility))
        {
            if ((facility.AsdexConfiguration is not { } asdex) || seed.ContainsKey(facility.Id))
            {
                continue;
            }

            IReadOnlyList<RunwayInfo> navRunways = FacilityRunways(facility.Id, navDb);
            if (navRunways.Count == 0)
            {
                Log.LogDebug("No ASDE-X safety-logic seed for {FacilityId}: navdata has no runways for it", facility.Id);
                continue;
            }

            seed[facility.Id] = new AsdexSafetyLogicConfig(BuildRunways(facility.Id, asdex, navRunways), "", []);
        }

        return seed.ToImmutable();
    }

    private static IEnumerable<FacilityConfig> Walk(FacilityConfig facility)
    {
        yield return facility;
        foreach (FacilityConfig child in facility.ChildFacilities)
        {
            foreach (FacilityConfig descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>The facility's navdata runways, under its FAA id or, failing that, the ICAO "K" form.</summary>
    internal static IReadOnlyList<RunwayInfo> FacilityRunways(string facilityId, NavigationDatabase navDb)
    {
        IReadOnlyList<RunwayInfo> runways = navDb.GetRunways(facilityId);
        return (runways.Count > 0) || (facilityId.Length != 3) ? runways : navDb.GetRunways("K" + facilityId);
    }

    private static List<AsdexRunwayConfig> BuildRunways(string facilityId, AsdexConfig asdex, IReadOnlyList<RunwayInfo> navRunways)
    {
        var runways = new List<AsdexRunwayConfig>();
        var named = new HashSet<RunwayInfo>(ReferenceEqualityComparer.Instance);
        foreach (string id in asdex.Positions.SelectMany(position => position.RunwayIds).Distinct(StringComparer.Ordinal))
        {
            RunwayInfo? nav = navRunways.FirstOrDefault(runway => NamesRunway(id, runway));
            if (nav is null)
            {
                Log.LogDebug("ASDE-X safety-logic seed for {FacilityId} leaves out runway {RunwayId}: navdata does not have it", facilityId, id);
                continue;
            }

            named.Add(nav);
            runways.Add(new AsdexRunwayConfig(id, Footprint(nav), IsClosed: false));
        }

        foreach (RunwayInfo nav in navRunways.Where(runway => !named.Contains(runway)))
        {
            string id = $"{RunwayIdentifier.ToDisplayDesignator(nav.Id.End1)}-{RunwayIdentifier.ToDisplayDesignator(nav.Id.End2)}";
            runways.Add(new AsdexRunwayConfig(id, Footprint(nav), IsClosed: false));
        }

        return runways;
    }

    /// <summary>True when the pair id's two ends ("28R-10L") are the navdata runway's two ends, in either order.</summary>
    private static bool NamesRunway(string pairId, RunwayInfo runway)
    {
        string[] ends = pairId.Split('-');
        if (ends.Length != 2)
        {
            return false;
        }

        string a = RunwayIdentifier.NormalizeDesignator(ends[0]);
        string b = RunwayIdentifier.NormalizeDesignator(ends[1]);
        return ((a == runway.Id.End1) && (b == runway.Id.End2)) || ((a == runway.Id.End2) && (b == runway.Id.End1));
    }

    /// <summary>
    /// The pavement rectangle, offset half the width each side of the end-to-end centreline: end 1 left, end 1 right,
    /// end 2 right, end 2 left, facing from end 1 toward end 2 — counter-clockwise seen from above, the first edge
    /// across the width, the order CRC draws its closed-runway X from.
    /// </summary>
    private static List<LatLon> Footprint(RunwayInfo runway)
    {
        var end1 = new LatLon(runway.Lat1, runway.Lon1);
        var end2 = new LatLon(runway.Lat2, runway.Lon2);
        double axis = GeoMath.BearingTo(end1, end2);
        var left = new TrueHeading(axis - 90);
        var right = new TrueHeading(axis + 90);
        double halfWidthNm = runway.WidthFt / 2 / GeoMath.FeetPerNm;
        return
        [
            GeoMath.ProjectPoint(end1, left, halfWidthNm),
            GeoMath.ProjectPoint(end1, right, halfWidthNm),
            GeoMath.ProjectPoint(end2, right, halfWidthNm),
            GeoMath.ProjectPoint(end2, left, halfWidthNm),
        ];
    }
}
