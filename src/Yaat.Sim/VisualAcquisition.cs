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
        var (layers, visibilitySm, referenceElevation) = ResolveWeatherNearOwnship(ownship, weather);
        return VisualDetection.TryAcquireTraffic(ownship, target, layers, referenceElevation, visibilitySm, ownship.BankAngle);
    }

    /// <summary>
    /// Maintained-contact variant of <see cref="TryAcquireTraffic"/> for traffic
    /// already in sight: runs only the weather obstruction (cloud layer between),
    /// skipping the acquisition-range, forward-hemisphere, and bank-occlusion checks.
    /// See <see cref="VisualDetection.TryMaintainTrafficContact"/>.
    /// </summary>
    public static VisualAcquisitionResult TryMaintainTrafficContact(AircraftState ownship, AircraftState target, WeatherProfile? weather)
    {
        var (layers, _, referenceElevation) = ResolveWeatherNearOwnship(ownship, weather);
        return VisualDetection.TryMaintainTrafficContact(ownship, target, layers, referenceElevation);
    }

    /// <summary>
    /// Weather for traffic acquisition comes from the air mass the ownship is
    /// flying in — the nearest reporting station to its position — NOT from its
    /// flight-plan destination, which may be hundreds of miles away (or absent
    /// entirely for an overflight) and previously produced a clear-sky assumption
    /// in exactly those cases. The station's field elevation is the AGL→MSL datum
    /// for its cloud-layer bases. Airport (RFIS) acquisition keeps destination
    /// sourcing: there the destination field itself is the thing being looked at.
    /// </summary>
    private static (IReadOnlyList<MetarParser.CloudLayer>? Layers, double? VisibilitySm, double ReferenceElevation) ResolveWeatherNearOwnship(
        AircraftState ownship,
        WeatherProfile? weather
    )
    {
        var near = weather?.GetWeatherNearPosition(ownship.Position, MetarInterpolator.MaxInterpolationRangeNm);
        if (near is null)
        {
            return (null, null, 0.0);
        }

        var (metar, stationAirportId) = near.Value;
        double elevation = NavigationDatabase.Instance.GetAirportElevation(stationAirportId) ?? 0.0;
        return (metar.Layers, metar.VisibilityStatuteMiles, elevation);
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
        var destination = ownship.FlightPlan.Destination;
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
            ownship.BankAngle,
            AirportSizeCapNm(destination)
        );
    }

    /// <summary>
    /// Estimates the airport-conspicuity acquisition cap for a destination
    /// from its runway envelope. Larger / multi-runway hubs are visually
    /// distinctive at greater range than a single short GA strip; the
    /// max-pairwise-distance between runway endpoints is a reasonable proxy
    /// for that distinctiveness. 7110.65 §7-4-3.g's worked example asks a
    /// pilot to report an airport in sight at 12 miles, and §7-4-3.f presumes
    /// the pilot hunts for a field that is not yet visually obvious once told
    /// where to look — which anchors the 15–25 nm scale as the clear-day
    /// acquisition envelope.
    /// </summary>
    public static double AirportSizeCapNm(string airportId)
    {
        var runways = NavigationDatabase.Instance.GetRunways(airportId);
        if (runways.Count == 0)
        {
            return SmallAirportFloorNm;
        }

        double maxExtentNm = 0.0;
        for (int i = 0; i < runways.Count; i++)
        {
            var ri = runways[i];
            for (int j = i; j < runways.Count; j++)
            {
                var rj = runways[j];
                maxExtentNm = Math.Max(maxExtentNm, GeoMath.DistanceNm(ri.Lat1, ri.Lon1, rj.Lat1, rj.Lon1));
                maxExtentNm = Math.Max(maxExtentNm, GeoMath.DistanceNm(ri.Lat1, ri.Lon1, rj.Lat2, rj.Lon2));
                maxExtentNm = Math.Max(maxExtentNm, GeoMath.DistanceNm(ri.Lat2, ri.Lon2, rj.Lat1, rj.Lon1));
                maxExtentNm = Math.Max(maxExtentNm, GeoMath.DistanceNm(ri.Lat2, ri.Lon2, rj.Lat2, rj.Lon2));
            }
        }

        // Anchors: GA single-runway field ~0.7 nm extent → 15 nm cap;
        // major hub like KSFO ~3 nm extent → 25 nm cap. Linear in between
        // and clamped at both ends.
        double cap = SizeCapInterceptNm + (maxExtentNm * SizeCapSlopePerNm);
        return Math.Clamp(cap, SmallAirportFloorNm, LargeAirportCeilingNm);
    }

    private const double SmallAirportFloorNm = 15.0;
    private const double LargeAirportCeilingNm = 25.0;
    private const double SizeCapInterceptNm = 10.0;
    private const double SizeCapSlopePerNm = 5.0;
}
