using Yaat.Sim.Data;

namespace Yaat.Sim.Pilot;

/// <summary>
/// The direction of flight a VFR departure names when it asks a delivery student for its departure ("VFR departure to the
/// north"): a cardinal whose 90° sector (±45°) has no other airport within <see cref="ClearRadiusNm"/> of the departure
/// field, chosen per aircraft from a callsign hash; when every sector has one, the cardinal whose nearest airport is farthest.
/// </summary>
public static class VfrDepartureDirection
{
    /// <summary>How far out another airport makes a sector a poor choice, nautical miles.</summary>
    public const double ClearRadiusNm = 10.0;

    /// <summary>Salt for the direction draw, so it is not correlated with the other per-callsign draws.</summary>
    private const string DirectionSalt = "vfr-departure-direction:";

    private static readonly string[] Cardinals = ["north", "east", "south", "west"];

    /// <summary>
    /// The cardinal for <paramref name="callsign"/> departing <paramref name="fieldId"/>, from the navigation database's
    /// airport positions; null when the field's position is unknown.
    /// </summary>
    public static string? Choose(string callsign, string fieldId)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        if (navDb.GetAirportPosition(fieldId) is not { } fieldPosition)
        {
            return null;
        }

        var field = new LatLon(fieldPosition.Lat, fieldPosition.Lon);
        List<(string Id, LatLon Position)> neighbours = navDb.FindAirportsWithin(field, ClearRadiusNm);
        neighbours.RemoveAll(n => NavigationDatabase.AirportIdsMatch(n.Id, fieldId));
        return Choose(callsign, field, neighbours);
    }

    /// <summary>
    /// The cardinal for <paramref name="callsign"/> departing a field at <paramref name="field"/> with
    /// <paramref name="neighbours"/> around it.
    /// </summary>
    public static string Choose(string callsign, LatLon field, IReadOnlyList<(string Id, LatLon Position)> neighbours)
    {
        double[] nearestNm = [double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue];
        foreach ((string _, LatLon position) in neighbours)
        {
            int sector = SectorOf(GeoMath.BearingTo(field, position));
            nearestNm[sector] = Math.Min(nearestNm[sector], GeoMath.DistanceNm(field, position));
        }

        var clear = Enumerable.Range(0, Cardinals.Length).Where(s => nearestNm[s] > ClearRadiusNm).ToList();
        if (clear.Count > 0)
        {
            // A fixed per-aircraft choice (replay-safe, no RNG state).
            return Cardinals[clear[(int)(DeterministicHash.Fnv1a(DirectionSalt, callsign) % (uint)clear.Count)]];
        }

        int farthest = 0;
        for (int s = 1; s < Cardinals.Length; s++)
        {
            if (nearestNm[s] > nearestNm[farthest])
            {
                farthest = s;
            }
        }

        return Cardinals[farthest];
    }

    /// <summary>The sector index (0 north, 1 east, 2 south, 3 west) a true bearing falls in, each ±45° about its cardinal.</summary>
    private static int SectorOf(double bearingDeg)
    {
        double b = ((bearingDeg % 360.0) + 360.0) % 360.0;
        return (int)((b + 45.0) % 360.0 / 90.0);
    }
}
