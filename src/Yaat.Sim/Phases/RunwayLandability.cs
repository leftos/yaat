using Yaat.Sim.Data;

namespace Yaat.Sim.Phases;

/// <summary>
/// Whether a type can land on a runway end, judged as a menu judges it before offering the end: the end's landing distance
/// available (LDA, AIM 4-3-6.d.3(d); a displaced threshold shortens it, AIM 2-3-3.h.2) against the type's landing distance
/// (<see cref="AircraftProfile.LandingDistance"/>) times <see cref="LandingDistanceFactor"/>. No wind, wet or dry surface
/// or elevation correction is applied.
/// </summary>
public static class RunwayLandability
{
    /// <summary>
    /// The margin a landing distance is multiplied by before it is compared with the runway: 1.15, the factor 14 CFR
    /// 121.195(d) applies to a wet runway, borrowed as a judgement call rather than an operating rule.
    /// </summary>
    public const double LandingDistanceFactor = 1.15;

    /// <summary>
    /// The landing distance available on <paramref name="runwayEnd"/>'s active end, in feet: the figure
    /// <paramref name="navDb"/> declares for it (<see cref="NavigationDatabase.DeclaredLandingDistanceFt"/>), else the
    /// pavement length, since the nav data carries no displaced threshold to subtract from it.
    /// </summary>
    public static double LandingDistanceAvailableFt(NavigationDatabase navDb, RunwayInfo runwayEnd) =>
        navDb.DeclaredLandingDistanceFt(runwayEnd.AirportId, runwayEnd.Designator) ?? runwayEnd.PavementLengthFt;

    /// <summary>
    /// Whether <paramref name="aircraftType"/> can land on a runway end with <paramref name="landingDistanceAvailableFt"/>
    /// of landing distance available: true when it is at least <see cref="LandingDistanceFactor"/> times the type's landing
    /// distance. A type with no landing distance (no profile, or a profile without the figure), a helicopter and a ground
    /// vehicle (a type code starting <c>VEH</c>) always count as landable.
    /// </summary>
    public static bool IsLandable(double landingDistanceAvailableFt, string aircraftType)
    {
        if (aircraftType.StartsWith("VEH", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        AircraftProfile? profile = AircraftProfileDatabase.Get(aircraftType);
        if ((profile is null) || profile.IsHelo || (profile.LandingDistance <= 0))
        {
            return true;
        }

        return landingDistanceAvailableFt >= (LandingDistanceFactor * profile.LandingDistance);
    }
}
