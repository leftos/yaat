using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Speech.Context;

/// <summary>One aircraft of the room, as the client knows it.</summary>
/// <param name="Callsign">The ICAO callsign ("SWA2495", "N4926F").</param>
/// <param name="AircraftType">The ICAO type designator, or null when unknown.</param>
/// <param name="ProgrammedFixes">The fixes of the aircraft's programmed route; blank entries are skipped.</param>
/// <param name="AssignedRunway">The runway designator the aircraft is assigned at the room's airport ("28R"), or null.</param>
public sealed record RoomAircraftInput(string Callsign, string? AircraftType, IReadOnlyList<string> ProgrammedFixes, string? AssignedRunway);

/// <summary>What a room's transmission context is built from.</summary>
/// <param name="AirportIcao">The airport the controller works ("KOAK").</param>
/// <param name="ApproachPosition">Whether the controller works an approach position.</param>
/// <param name="Layout">The airport's ground layout, or null when none is loaded.</param>
/// <param name="Aircraft">The room's aircraft.</param>
/// <param name="FacilityRadioNames">The radio names of the room's facilities ("Oakland Tower", "NorCal Approach").</param>
public sealed record RoomContextRequest(
    string AirportIcao,
    bool ApproachPosition,
    AirportGroundLayout? Layout,
    IReadOnlyList<RoomAircraftInput> Aircraft,
    IReadOnlyList<string> FacilityRadioNames
);

/// <summary>Which airport entities a room needs.</summary>
/// <param name="AirportIcao">The airport the controller works ("KOAK").</param>
/// <param name="ApproachPosition">Whether the controller works an approach position.</param>
/// <param name="Layout">The airport's ground layout, or null when none is loaded.</param>
public sealed record AirportContextKey(string AirportIcao, bool ApproachPosition, AirportGroundLayout? Layout);

/// <summary>
/// Builds the <see cref="TransmissionContext"/> a room's transmissions are decoded against from what the room knows: its
/// airport, whether the controller works approach, the airport's ground layout, its aircraft and its facility radio names.
/// </summary>
public static class RoomContextFactory
{
    private static readonly ILogger Log = SimLog.CreateLogger("RoomContextFactory");

    /// <summary>Words naming a facility's function rather than its place; a facility name is what remains without them.</summary>
    private static readonly IReadOnlySet<string> FacilityFunctionWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "tower",
        "ground",
        "approach",
        "departure",
        "center",
        "clearance",
        "delivery",
        "ramp",
        "radio",
        "control",
    };

    /// <summary>
    /// The room's transmission context: the airport's entities from <paramref name="cache"/>, one aircraft per input whose
    /// callsign builds (an airline with unknown telephony is left out), in input order, each owning its programmed fixes and
    /// its assigned runway, and the spoken facility names (<see cref="FacilityNames"/>).
    /// </summary>
    /// <param name="cache">The cache holding the airport's entities.</param>
    /// <param name="request">The room's airport, position, layout, aircraft and facility radio names.</param>
    /// <param name="nav">The navigation database.</param>
    /// <param name="settings">The decoder settings whose priors rank the forms.</param>
    /// <returns>The context the room's transmissions are decoded against.</returns>
    /// <exception cref="InvalidOperationException">The navigation database has no reference point for the airport.</exception>
    public static TransmissionContext Build(RoomContextCache cache, RoomContextRequest request, NavigationDatabase nav, DecoderSettings settings)
    {
        string airportIcao = request.AirportIcao;
        IReadOnlyList<ContextEntity> airportEntities = cache.AirportEntities(
            new AirportContextKey(airportIcao, request.ApproachPosition, request.Layout),
            nav,
            settings
        );
        string[] roomCallsigns = [.. request.Aircraft.Select(input => input.Callsign)];
        var roomAircraft = new List<RoomAircraft>();
        foreach (RoomAircraftInput input in request.Aircraft)
        {
            ContextEntity? callsign = ContextEntityFactory.Callsign(
                input.Callsign,
                input.AircraftType,
                new Dictionary<string, string>(),
                roomCallsigns,
                settings
            );
            if (callsign is null)
            {
                Log.LogDebug("Room aircraft {Callsign} left out of the speech context: its airline telephony is unknown", input.Callsign);
                continue;
            }

            roomAircraft.Add(new RoomAircraft(callsign, OwnEntities(input, airportIcao, airportEntities, nav)));
        }

        return new TransmissionContext(
            airportIcao.Trim().ToUpperInvariant(),
            roomAircraft,
            airportEntities,
            FacilityNames(request.FacilityRadioNames)
        );
    }

    /// <summary>
    /// The spoken facility names of the room's radio names: each lowercased, split on whitespace and stripped of its function
    /// words ("tower", "approach", "clearance delivery" …), kept when one or two words remain, deduplicated and ordinal-sorted.
    /// </summary>
    /// <param name="facilityRadioNames">The radio names ("Oakland Tower", "San Francisco Ground").</param>
    /// <returns>The facility names ("oakland", "san francisco").</returns>
    public static IReadOnlyList<string> FacilityNames(IReadOnlyList<string> facilityRadioNames)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string radioName in facilityRadioNames)
        {
            string[] words =
            [
                .. radioName
                    .ToLowerInvariant()
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Where(word => !FacilityFunctionWords.Contains(word)),
            ];
            if (words.Length is 1 or 2)
            {
                names.Add(string.Join(' ', words));
            }
        }

        return [.. names];
    }

    /// <summary>The aircraft's programmed fixes, then its assigned runway's airport entity, deduplicated by kind and value.</summary>
    private static List<ContextEntity> OwnEntities(
        RoomAircraftInput input,
        string airportIcao,
        IReadOnlyList<ContextEntity> airportEntities,
        NavigationDatabase nav
    )
    {
        var own = new List<ContextEntity>();
        foreach (string fix in input.ProgrammedFixes.Where(fix => !string.IsNullOrWhiteSpace(fix)))
        {
            own.Add(ContextEntityFactory.Fix(fix, nav));
        }

        if (!string.IsNullOrWhiteSpace(input.AssignedRunway))
        {
            string value = $"{airportIcao.Trim().ToUpperInvariant()}/{RunwayIdentifier.NormalizeDesignator(input.AssignedRunway.Trim())}";
            ContextEntity? runway = airportEntities.FirstOrDefault(entity =>
                (entity.Kind == EntityKind.Runway) && string.Equals(entity.Value, value, StringComparison.Ordinal)
            );
            if (runway is null)
            {
                Log.LogDebug("Room aircraft {Callsign}: assigned runway {Runway} is not a runway of {Airport}", input.Callsign, value, airportIcao);
            }
            else
            {
                own.Add(runway);
            }
        }

        var seen = new HashSet<(EntityKind Kind, string Value)>();
        return [.. own.Where(entity => seen.Add((entity.Kind, entity.Value)))];
    }

    /// <summary>How far from a tower or ground position's airport a fix is included, in nautical miles.</summary>
    public const double LocalRadiusNm = 10;

    /// <summary>How far from an approach position's airport a fix or an instrument airport is included, in nautical miles.</summary>
    public const double ApproachRadiusNm = 40;

    /// <summary>
    /// The airport's entities (<see cref="AirportContextBuilder.Build"/> at <see cref="LocalRadiusNm"/>, or
    /// <see cref="ApproachRadiusNm"/> for an approach position, which also gets the runways and approaches of every other
    /// airport within that radius with an instrument approach), deduplicated by kind and value with the first kept.
    /// </summary>
    /// <param name="key">The airport, whether the position is approach, and the airport's ground layout or null.</param>
    /// <param name="landmarks">Landmark entities for this airport.</param>
    /// <param name="nav">The navigation database.</param>
    /// <param name="settings">The decoder settings whose priors rank the forms.</param>
    /// <returns>The airport entities.</returns>
    /// <exception cref="InvalidOperationException">The navigation database has no reference point for the airport.</exception>
    public static IReadOnlyList<ContextEntity> AirportEntities(
        AirportContextKey key,
        IReadOnlyList<ContextEntity> landmarks,
        NavigationDatabase nav,
        DecoderSettings settings
    )
    {
        double radius = key.ApproachPosition ? ApproachRadiusNm : LocalRadiusNm;
        var entities = new List<ContextEntity>(AirportContextBuilder.Build(key.AirportIcao, key.Layout, radius, landmarks, nav, settings));
        if (key.ApproachPosition)
        {
            entities.AddRange(NearbyInstrumentAirports(key.AirportIcao, nav, settings));
        }

        var seen = new HashSet<(EntityKind Kind, string Value)>();
        return [.. entities.Where(entity => seen.Add((entity.Kind, entity.Value)))];
    }

    /// <summary>The runways and approaches of every other airport within <see cref="ApproachRadiusNm"/> that has an instrument approach.</summary>
    /// <param name="airportIcao">The room's airport ("KOAK").</param>
    /// <param name="nav">The navigation database.</param>
    /// <param name="settings">The decoder settings whose priors rank the forms.</param>
    /// <returns>Each nearby airport's runways, then its approaches, in ordinal airport-id order.</returns>
    /// <exception cref="InvalidOperationException">The navigation database has no reference point for the airport.</exception>
    public static IEnumerable<ContextEntity> NearbyInstrumentAirports(string airportIcao, NavigationDatabase nav, DecoderSettings settings)
    {
        (double Lat, double Lon) point =
            nav.GetAirportPosition(airportIcao)
            ?? throw new InvalidOperationException($"Airport '{airportIcao}' has no reference point in the navigation database.");
        string home = AirportLayoutDownloader.ToFaaCode(airportIcao);
        foreach ((string id, LatLon _) in nav.FindAirportsWithin(new LatLon(point.Lat, point.Lon), ApproachRadiusNm))
        {
            string icao = ToIcao(id);
            if (AirportLayoutDownloader.ToFaaCode(icao) == home)
            {
                continue;
            }

            IReadOnlyList<ContextEntity> approaches = ContextEntityFactory.Approaches(icao, nav, settings);
            if (approaches.Count == 0)
            {
                continue;
            }

            foreach (ContextEntity entity in ContextEntityFactory.Runways(icao, nav, settings).Concat(approaches))
            {
                yield return entity;
            }
        }
    }

    /// <summary>A navdata airport id as an ICAO id: a three-letter FAA id gains its "K".</summary>
    /// <param name="id">The navdata airport id ("OAK", "KOAK", "O61").</param>
    public static string ToIcao(string id) => ((id.Length == 3) && id.All(char.IsLetter)) ? "K" + id : id;

    /// <summary>The landmark entities of the airport's own landmark file (<see cref="NavigationDatabase.CustomFixLandmarks"/>).</summary>
    /// <param name="airportIcao">The airport's ICAO id ("KOAK").</param>
    /// <param name="nav">The navigation database.</param>
    /// <returns>One landmark entity per landmark, valued "&lt;ICAO&gt;/&lt;ALIAS&gt;", in load order.</returns>
    public static IReadOnlyList<ContextEntity> Landmarks(string airportIcao, NavigationDatabase nav)
    {
        string faa = AirportLayoutDownloader.ToFaaCode(airportIcao).ToUpperInvariant();
        return
        [
            .. nav
                .CustomFixLandmarks.Where(landmark => string.Equals(landmark.Airport, faa, StringComparison.Ordinal))
                .Select(landmark => ContextEntityFactory.Landmark(airportIcao, landmark.Alias, landmark.SpokenPatterns)),
        ];
    }
}
