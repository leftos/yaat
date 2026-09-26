using System.Collections.Immutable;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Asdex;

/// <summary>
/// Which ASDE-X and SAAB SAID surface displays a track shows on. Each configured airport is entered when the aircraft
/// is inside its range and at or below its vertical limit, and left when it goes out of range or climbs to the limit
/// plus a hysteresis band, so a track hovering at the limit does not flicker on and off the display. The ASDE-X limit
/// is the configured visibility ceiling (MSL); the SAID limit is the field elevation plus a fixed 2,500 ft AGL, since
/// the SAID configuration carries none. A pure phantom (an unsupported ghost that is not an overlay) belongs on no
/// surface display, and an airport that is no longer configured drops out of every membership. Pure functions over
/// the aircraft, the configured airports and the membership it held last second; the engine stores the result on
/// <see cref="AircraftStarsState"/>.
/// </summary>
public static class SurfaceMembership
{
    public const double AsdexHysteresisFt = 600;
    public const double SaidHysteresisFt = 600;
    public const double SaidAglCeilingFt = 2500;

    /// <summary>The empty membership, ordered by ordinal airport id so every run kind enumerates it the same way.</summary>
    public static ImmutableSortedSet<string> Empty { get; } = ImmutableSortedSet.Create<string>(StringComparer.Ordinal);

    /// <summary>A membership from airport ids, ordinal-ordered; null (a snapshot that carries none) is empty.</summary>
    public static ImmutableSortedSet<string> FromIds(IEnumerable<string>? ids) => ids is null ? Empty : Empty.Union(ids);

    /// <summary>Next second's ASDE-X membership, from the one <paramref name="visible"/> held this second.</summary>
    public static ImmutableSortedSet<string> EvaluateAsdex(AircraftState ac, ImmutableSortedSet<string> visible, SurfaceAirports airports)
    {
        if (IsPurePhantom(ac))
        {
            return Empty;
        }

        visible = visible.Intersect(airports.AsdexIds);
        foreach (AsdexAirportInfo apt in airports.Asdex)
        {
            double dist = GeoMath.DistanceNm(ac.Position, new LatLon(apt.Lat, apt.Lon));
            bool mayEnter = (dist <= apt.Range) && (ac.Altitude <= apt.Ceiling);
            bool mustLeave = (dist > apt.Range) || (ac.Altitude >= apt.Ceiling + AsdexHysteresisFt);
            visible = Next(visible, apt.AirportId, mayEnter, mustLeave);
        }

        return visible;
    }

    /// <summary>Next second's SAAB SAID membership, from the one <paramref name="visible"/> held this second.</summary>
    public static ImmutableSortedSet<string> EvaluateSaid(AircraftState ac, ImmutableSortedSet<string> visible, SurfaceAirports airports)
    {
        if (IsPurePhantom(ac))
        {
            return Empty;
        }

        visible = visible.Intersect(airports.SaidIds);
        foreach (SaidSurfaceAirport apt in airports.Said)
        {
            double dist = GeoMath.DistanceNm(ac.Position, apt.Position);
            bool mayEnter = (dist <= apt.Range) && (ac.Altitude <= apt.CeilingFt);
            bool mustLeave = (dist > apt.Range) || (ac.Altitude >= apt.CeilingFt + SaidHysteresisFt);
            visible = Next(visible, apt.AirportId, mayEnter, mustLeave);
        }

        return visible;
    }

    private static bool IsPurePhantom(AircraftState ac) => ac.Ghost.IsUnsupported && !ac.Ghost.IsOverlay;

    private static ImmutableSortedSet<string> Next(ImmutableSortedSet<string> visible, string airportId, bool mayEnter, bool mustLeave)
    {
        if (visible.Contains(airportId))
        {
            return mustLeave ? visible.Remove(airportId) : visible;
        }

        return mayEnter ? visible.Add(airportId) : visible;
    }
}

/// <summary>A configured SAAB SAID airport with its vertical limit resolved: field elevation plus 2,500 ft AGL.</summary>
public sealed record SaidSurfaceAirport(string AirportId, LatLon Position, double Range, double CeilingFt);

/// <summary>
/// The surface-display airports an ARTCC config declares, with each list's airport ids as an ordinal set (what a
/// membership is trimmed to) and each SAID ceiling resolved once. An airport declared twice keeps its first declaration.
/// </summary>
public sealed record SurfaceAirports(
    IReadOnlyList<AsdexAirportInfo> Asdex,
    ImmutableSortedSet<string> AsdexIds,
    IReadOnlyList<SaidSurfaceAirport> Said,
    ImmutableSortedSet<string> SaidIds
)
{
    /// <summary>No configured surface airports: every membership evaluated against this clears.</summary>
    public static SurfaceAirports None { get; } = new([], SurfaceMembership.Empty, [], SurfaceMembership.Empty);

    /// <summary>Walks <paramref name="config"/>'s facility tree; a SAID field elevation <paramref name="navDb"/> does not know is 0.</summary>
    public static SurfaceAirports Resolve(ArtccConfigRoot config, NavigationDatabase navDb)
    {
        var asdex = config.GetAllAsdexAirports().DistinctBy(apt => apt.AirportId).ToList();
        var said = config
            .GetAllSaidAirports()
            .DistinctBy(apt => apt.AirportId)
            .Select(apt => new SaidSurfaceAirport(
                apt.AirportId,
                new LatLon(apt.Lat, apt.Lon),
                apt.Range,
                (navDb.GetAirportElevation(apt.AirportId) ?? 0) + SurfaceMembership.SaidAglCeilingFt
            ))
            .ToList();
        return new SurfaceAirports(
            asdex,
            SurfaceMembership.FromIds(asdex.Select(apt => apt.AirportId)),
            said,
            SurfaceMembership.FromIds(said.Select(apt => apt.AirportId))
        );
    }
}
