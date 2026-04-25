using Yaat.Sim.Data;

namespace Yaat.Sim;

/// <summary>
/// High-level wrappers around <see cref="VisualDetection"/> that bundle the
/// METAR / airport-elevation / bank-angle lookup shared by the RTIS / RFIS
/// command handlers (first-check) and <see cref="PilotObservationUpdater"/>
/// (per-tick re-check). Keeps both call sites in lockstep so they cannot
/// drift on which weather or elevation inputs they feed into the acquisition
/// check.
/// </summary>
public static class VisualAcquisition
{
    public static VisualAcquisitionResult TryAcquireTraffic(AircraftState ownship, AircraftState target, WeatherProfile? weather)
    {
        IReadOnlyList<MetarParser.CloudLayer>? layers = null;
        double? visibilitySm = null;
        double aptElevation = 0.0;
        var destination = ownship.Destination;
        if (!string.IsNullOrWhiteSpace(destination))
        {
            var metar = weather?.GetWeatherForAirport(destination);
            layers = metar?.Layers;
            visibilitySm = metar?.VisibilityStatuteMiles;
            aptElevation = NavigationDatabase.Instance.GetAirportElevation(destination) ?? 0.0;
        }

        return VisualDetection.TryAcquireTraffic(ownship, target, layers, aptElevation, visibilitySm, ownship.BankAngle);
    }

    /// <summary>
    /// Attempts to visually acquire <see cref="AircraftState.Destination"/>.
    /// Returns null if the aircraft has no destination assigned or the
    /// destination is not in the nav database — i.e. there is no field to
    /// look at, so callers should drop any pending observation rather than
    /// keep retrying. Any non-null result represents an actual acquisition
    /// outcome (success or a per-reason failure).
    /// </summary>
    public static VisualAcquisitionResult? TryAcquireAirport(AircraftState ownship, WeatherProfile? weather)
    {
        var destination = ownship.Destination;
        if (string.IsNullOrWhiteSpace(destination))
        {
            return null;
        }

        var navDb = NavigationDatabase.Instance;
        var aptPos = navDb.GetFixPosition(destination);
        var aptElevation = navDb.GetAirportElevation(destination);
        if (aptPos is null || aptElevation is null)
        {
            return null;
        }

        var metar = weather?.GetWeatherForAirport(destination);
        return VisualDetection.TryAcquireAirport(
            ownship,
            aptPos.Value.Lat,
            aptPos.Value.Lon,
            aptElevation.Value,
            metar?.Layers,
            metar?.VisibilityStatuteMiles,
            ownship.BankAngle
        );
    }
}
