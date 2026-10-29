using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Asdex;

/// <summary>
/// The ASDE-X Safety Logic configuration a scenario load starts every ASDE-X facility on, derived from the ARTCC config
/// and navdata rather than recorded. One entry per facility whose <c>asdexConfiguration</c> is present and whose airport
/// has navdata runways. Its runways carry the facility's own ids: the vNAS pair form the facility's positions name
/// ("28R-10L"), unioned over the positions in config order, then every navdata runway no position names as
/// <c>{end1}-{end2}</c> in <see cref="NavigationDatabase.GetRunways"/> order, unpadded. Each footprint is the pavement
/// rectangle and every runway starts open. The runway configuration is the facility's configuration whose arrival and
/// departure runway ids cover the room's active arrival and departure runways (the smallest such set, facility order on
/// a tie), re-picked when the active runways change unless a CRC user has chosen a configuration; with no active runways
/// or none covering them, the facility's first configuration, and with no configurations, "" (LIMITED).
/// </summary>
public static class AsdexSafetyLogicSeed
{
    private static readonly ILogger Log = SimLog.CreateLogger("AsdexSafetyLogicSeed");

    /// <summary>The empty per-facility map: no ARTCC config, no ASDE-X facility, or no navdata runways.</summary>
    public static ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> Empty { get; } =
        ImmutableSortedDictionary.Create<string, AsdexSafetyLogicConfig>(StringComparer.Ordinal);

    /// <summary>
    /// Builds the seed for every ASDE-X facility in <paramref name="config"/>, keyed by facility id (ordinal), each on the
    /// configuration <paramref name="activeRunways"/> select for its airport. With no config or no navdata it is empty; a
    /// facility with no navdata runways, and a position runway navdata lacks, are left out with a debug log.
    /// </summary>
    public static ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> Build(
        ArtccConfigRoot? config,
        NavigationDatabase? navDb,
        ActiveRunways activeRunways
    )
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

            string configurationId = SelectRunwayConfigurationId(asdex, activeRunways.For(facility.Id));
            seed[facility.Id] = new AsdexSafetyLogicConfig(
                BuildRunways(facility.Id, asdex, navRunways),
                configurationId,
                [],
                RunwayConfigurationChosen: false,
                AsdexSafetyLogicConfig.NoSeconds,
                AsdexSafetyLogicConfig.NoSeconds
            );
        }

        return seed.ToImmutable();
    }

    /// <summary>
    /// The id of the facility's runway configuration that covers <paramref name="active"/>: every active arrival end in
    /// its arrival runway ids, every departure end in its departure runway ids, and an end used for both in both. Of
    /// several, the one naming the fewest distinct runway ends, the first in the facility's order on a tie. With no
    /// active runways, or none covering them, the facility's first configuration; with no configurations, "".
    /// </summary>
    public static string SelectRunwayConfigurationId(AsdexConfig asdex, IReadOnlyList<ActiveRunway> active)
    {
        if (asdex.RunwayConfigurations.Count == 0)
        {
            return "";
        }

        AsdexRunwayConfigurationConfig first = asdex.RunwayConfigurations[0];
        if (active.Count == 0)
        {
            return first.Id;
        }

        AsdexRunwayConfigurationConfig? best = null;
        int bestSize = int.MaxValue;
        foreach (AsdexRunwayConfigurationConfig candidate in asdex.RunwayConfigurations)
        {
            HashSet<string> arrivals = NormalizedEnds(candidate.ArrivalRunwayIds);
            HashSet<string> departures = NormalizedEnds(candidate.DepartureRunwayIds);
            int size = arrivals.Union(departures).Count();
            if ((size < bestSize) && active.All(runway => Covers(arrivals, departures, runway)))
            {
                best = candidate;
                bestSize = size;
            }
        }

        return (best ?? first).Id;
    }

    /// <summary>
    /// <paramref name="configs"/> with the runway configuration of every facility re-picked
    /// (<see cref="SelectRunwayConfigurationId"/>) whose airport's active runways differ between
    /// <paramref name="before"/> and <paramref name="after"/>, unless a CRC user has chosen its configuration
    /// (<see cref="AsdexSafetyLogicConfig.RunwayConfigurationChosen"/>). Every other entry is returned as it stands.
    /// A configuration change empties the facility's inhibit list and its starts. With <paramref name="recordSuperseded"/>
    /// (an <c>ARWY</c> change) the configuration moved away from is also remembered as superseded at
    /// <paramref name="elapsedSeconds"/>, so a stale display's push of it is not taken for a choice, and the set drops the
    /// configuration re-picked to and every entry whose window has passed; a load's own re-pick passes false, since no
    /// display can hold the seed's configuration yet.
    /// </summary>
    public static ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> Repick(
        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> configs,
        ArtccConfigRoot? config,
        ActiveRunways before,
        ActiveRunways after,
        double elapsedSeconds,
        bool recordSuperseded
    )
    {
        if (config is null)
        {
            return configs;
        }

        var repicked = configs.ToBuilder();
        foreach ((string facilityId, AsdexSafetyLogicConfig current) in configs)
        {
            if (current.RunwayConfigurationChosen || before.For(facilityId).SequenceEqual(after.For(facilityId)))
            {
                continue;
            }

            if (FindAsdex(config, facilityId) is not { } asdex)
            {
                continue;
            }

            string id = SelectRunwayConfigurationId(asdex, after.For(facilityId));
            if (id != current.RunwayConfigurationId)
            {
                Log.LogDebug("ASDE-X facility {FacilityId} re-picks runway configuration {ConfigurationId}", facilityId, id);
                IReadOnlyDictionary<string, double> superseded = recordSuperseded
                    ? AsdexSafetyLogicConfig.Seconds(
                        current
                            .SupersededConfigurationIds.Where(entry =>
                                (entry.Key != current.RunwayConfigurationId)
                                && (entry.Key != id)
                                && (entry.Value + AsdexSafetyLogicConfig.SupersededIdWindowSeconds > elapsedSeconds)
                            )
                            .Append(KeyValuePair.Create(current.RunwayConfigurationId, elapsedSeconds))
                    )
                    : current.SupersededConfigurationIds;
                repicked[facilityId] = current with
                {
                    RunwayConfigurationId = id,
                    InhibitedArrivalAlertPositionIds = [],
                    InhibitStartSeconds = AsdexSafetyLogicConfig.NoSeconds,
                    SupersededConfigurationIds = superseded,
                };
            }
        }

        return repicked.ToImmutable();
    }

    /// <summary>
    /// True when <paramref name="configurationId"/> names one of the facility's runway configurations. Anything else
    /// ("limited", "", an id the facility does not have, a facility or ARTCC config that cannot be found) is the LIMITED
    /// configuration, under which the detector raises closed-runway alerts only.
    /// </summary>
    public static bool NamesRunwayConfiguration(ArtccConfigRoot? config, string facilityId, string configurationId) =>
        (config is not null)
        && (configurationId.Length > 0)
        && (FindAsdex(config, facilityId) is { } asdex)
        && asdex.RunwayConfigurations.Any(candidate => candidate.Id == configurationId);

    /// <summary>
    /// True when ASDE-X Safety Logic raises occupied-runway alerts at <paramref name="airportId"/> now: the room's live
    /// <paramref name="configs"/> hold an entry with runways (the detector runs there) for its facility
    /// (<see cref="ArtccConfigResolver.FindAirportFacility"/>), and that entry's configuration is one of the facility's runway
    /// configurations (<see cref="NamesRunwayConfiguration"/>, not LIMITED), which a display-only ASDE-X configuration has
    /// none of. False with no ARTCC config: no evidence of a running system.
    /// </summary>
    public static bool OccupiedRunwayAlertingIsLive(
        ArtccConfigRoot? config,
        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> configs,
        string airportId
    ) =>
        (config is not null)
        && (config.FindAirportFacility(airportId) is { } facility)
        && configs.TryGetValue(facility.Id, out AsdexSafetyLogicConfig? live)
        && (live.Runways.Count > 0)
        && NamesRunwayConfiguration(config, facility.Id, live.RunwayConfigurationId);

    /// <summary>The ASDE-X configuration of the first facility in walk order with this id, the one the seed built from.</summary>
    private static AsdexConfig? FindAsdex(ArtccConfigRoot config, string facilityId) =>
        Walk(config.Facility)
            .FirstOrDefault(facility => (facility.Id == facilityId) && (facility.AsdexConfiguration is not null))
            ?.AsdexConfiguration;

    private static HashSet<string> NormalizedEnds(IEnumerable<string> ids) =>
        [.. ids.Select(id => RunwayIdentifier.NormalizeDesignator(id.Trim().ToUpperInvariant()))];

    private static bool Covers(HashSet<string> arrivals, HashSet<string> departures, ActiveRunway runway) =>
        runway.Use switch
        {
            ActiveRunwayUse.Arrival => arrivals.Contains(runway.Designator),
            ActiveRunwayUse.Departure => departures.Contains(runway.Designator),
            _ => arrivals.Contains(runway.Designator) && departures.Contains(runway.Designator),
        };

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
