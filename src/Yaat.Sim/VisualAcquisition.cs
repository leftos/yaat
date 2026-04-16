using Yaat.Sim.Data;

namespace Yaat.Sim;

/// <summary>
/// High-level wrapper around <see cref="VisualDetection.TryAcquireTraffic"/>
/// that bundles the METAR / airport-elevation / bank-angle lookup shared by
/// the RTIS command handler (first-check) and
/// <see cref="PilotObservationUpdater"/> (per-tick re-check). Keeps both call
/// sites in lockstep so they cannot drift on which weather or elevation inputs
/// they feed into the acquisition check.
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
}
