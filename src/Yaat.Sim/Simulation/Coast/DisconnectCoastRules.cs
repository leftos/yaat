using Yaat.Sim.Data;

namespace Yaat.Sim.Simulation.Coast;

/// <summary>
/// The stateless checks behind a disconnect coast: whether the en-route radar still sees a track, and whether a
/// surface display sits at the track's destination (a drop rather than a coast).
/// </summary>
public static class DisconnectCoastRules
{
    /// <summary>
    /// ERAM en-route (ARSR) radar loses low targets sooner than terminal ASR (radar horizon), so the Center coverage
    /// floor sits well above the STARS terminal acquisition floor: field elevation + 1,500 ft AGL. There is no en-route
    /// coverage polygon in the vNAS config, so this is a deliberate fixed-AGL simplification.
    /// </summary>
    public const double EramCoverageFloorAglFt = 1500;

    /// <summary>
    /// Stateless ERAM visibility: a QH-frozen or unsupported track is always present; a vehicle or a track on the
    /// ground never is; otherwise the track shows at or above field elevation + <see cref="EramCoverageFloorAglFt"/>.
    /// </summary>
    public static bool IsVisibleOnEram(AircraftState ac, NavigationDatabase navDb)
    {
        if (ac.Ghost.IsUnsupported || ac.Eram.IsFrozen)
        {
            return true;
        }

        if (ac.Ghost.IsVehicle || ac.IsOnGround)
        {
            return false;
        }

        double fieldElev = FieldElevationResolver.Resolve(ac, navDb);
        return ac.Altitude >= fieldElev + EramCoverageFloorAglFt;
    }

    /// <summary>True when <paramref name="facilityId"/> names the (already normalized) destination airport.</summary>
    public static bool IsDestinationFacility(string normalizedDestination, string facilityId) =>
        !string.IsNullOrEmpty(normalizedDestination)
        && normalizedDestination.Equals(NavigationDatabase.NormalizeAirport(facilityId), StringComparison.OrdinalIgnoreCase);
}
