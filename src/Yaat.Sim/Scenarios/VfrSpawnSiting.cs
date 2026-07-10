using Yaat.Sim.Data.Airspace;

namespace Yaat.Sim.Scenarios;

/// <summary>
/// Decides whether a rolled VFR spawn point is a legal, safe place to put a new aircraft.
///
/// Two independent constraints:
/// <list type="bullet">
/// <item>
/// <b>Airspace.</b> A VFR aircraft squawking 1200 cannot appear inside Class B (an ATC clearance is
/// required before entry — AIM 3-2-3.b) or Class C (two-way communications must be established before
/// entry — AIM 3-2-4.c.3). Beyond the realism problem, a fresh inbound born inside the boundary trips
/// <c>AirspaceBoundaryHoldPhase</c> in solo mode and orbits instead of flying its arrival.
/// </item>
/// <item>
/// <b>Traffic.</b> No aircraft may be born already in a loss of standard radar separation. Terminal minima
/// are 3 NM <em>or</em> 1000 ft (7110.65 §5-5-4) — so a spawn is only rejected when it violates
/// <em>both</em>. This is a spawn-injection buffer, deliberately wider than the Class C VFR-to-VFR
/// standard (which is traffic advisories only, §7-8-2.a.4).
/// </item>
/// </list>
/// </summary>
public static class VfrSpawnSiting
{
    public const double MinLateralSeparationNm = 3.0;
    public const double MinVerticalSeparationFt = 1000.0;

    /// <summary>Rolls to attempt before giving up and reporting that the configured ranges cannot be satisfied.</summary>
    public const int MaxSpawnAttempts = 20;

    /// <summary>False when the point lies inside any Class B or Class C volume.</summary>
    public static bool IsClearOfControlledAirspace(LatLon position, double altitudeFtMsl, AirspaceDatabase airspace) =>
        !airspace.FindContaining(position, altitudeFtMsl).Any();

    /// <summary>False when the point is inside standard radar separation of any existing aircraft.</summary>
    public static bool IsClearOfTraffic(LatLon position, double altitudeFtMsl, IReadOnlyCollection<AircraftState> existingAircraft)
    {
        foreach (var other in existingAircraft)
        {
            if (other.IsOnGround)
            {
                continue;
            }

            var lateralNm = GeoMath.DistanceNm(position.Lat, position.Lon, other.Position.Lat, other.Position.Lon);
            var verticalFt = Math.Abs(other.Altitude - altitudeFtMsl);
            if (lateralNm < MinLateralSeparationNm && verticalFt < MinVerticalSeparationFt)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsUsableSpawn(
        LatLon position,
        double altitudeFtMsl,
        AirspaceDatabase airspace,
        IReadOnlyCollection<AircraftState> existingAircraft
    ) => IsClearOfControlledAirspace(position, altitudeFtMsl, airspace) && IsClearOfTraffic(position, altitudeFtMsl, existingAircraft);

    /// <summary>
    /// A magnetic bearing uniformly inside the arc running clockwise from <paramref name="fromDeg"/> to
    /// <paramref name="toDeg"/>, wrapping through 360 when the arc straddles north (e.g. 340°→020°).
    /// </summary>
    public static double RollBearing(double fromDeg, double toDeg, Random rng)
    {
        var span = (toDeg - fromDeg) % 360.0;
        if (span <= 0)
        {
            span += 360.0;
        }
        return (fromDeg + (rng.NextDouble() * span)) % 360.0;
    }

    public static double RollInRange(double min, double max, Random rng) => min + (rng.NextDouble() * (max - min));
}
