using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Simulation;

/// <summary>
/// How a STARS area classifies a track relative to its own facility, mirroring CRC's
/// <c>StarsFlightType</c> plus its separate primary-arrival flag. A flight departing and arriving
/// within the same facility is both a departure and an arrival; the display rules resolve that by
/// checking the arrival cases first.
/// </summary>
public readonly record struct StarsFlightClassification(bool IsDeparture, bool IsArrival, bool IsPrimaryArrival)
{
    /// <summary>True when the track neither departs nor lands at a facility airport.</summary>
    public bool IsOverflight => !IsDeparture && !IsArrival;

    /// <summary>An arrival into a facility airport that is not the area's primary airport.</summary>
    public bool IsSatelliteArrival => IsArrival && !IsPrimaryArrival;
}

/// <summary>
/// Resolves the value STARS shows in the primary-scratchpad slot when no controller-entered
/// scratchpad is present. Real STARS falls back to displaying the destination airport, gated per
/// area by the facility's display adaptation — so a controller can tell where a track is going
/// without anyone having typed a scratchpad.
///
/// Mirrors CRC's <c>DisplayElementTracks.GetScratchpads</c> and <c>TrackManager.UpdateFlightType</c>.
/// This is a display projection computed fresh per broadcast, never written back to
/// <see cref="AircraftStarsState.Scratchpad1"/> — persisting it would send a synthetic value to CRC
/// as though a controller had entered it, bypassing CRC's own gating and breaking the
/// clear/undo toggle.
/// </summary>
public static class AutoScratchpadResolver
{
    /// <summary>
    /// Returns the destination airport to display in the primary-scratchpad slot, or null when the
    /// slot should stay empty — because a real scratchpad is set, because the controller explicitly
    /// cleared it, because the area's adaptation suppresses the destination for this kind of track,
    /// or because the track is an overflight.
    /// </summary>
    /// <param name="ac">The track to classify.</param>
    /// <param name="starsConfig">Facility STARS config supplying the internal-airport list and scratchpad length.</param>
    /// <param name="area">The viewing position's STARS area, supplying the display gates and tower list.</param>
    public static string? ResolveAutoScratchpad1(AircraftState ac, StarsConfig? starsConfig, StarsAreaConfig? area)
    {
        if (starsConfig is null || area is null)
        {
            return null;
        }

        // A controller-entered scratchpad always wins, and an explicit clear suppresses the
        // fallback entirely — otherwise clearing the slot would appear to do nothing.
        if (!string.IsNullOrEmpty(ac.Stars.Scratchpad1) || ac.Stars.WasScratchpad1Cleared)
        {
            return null;
        }

        var destination = ResolveDisplayAirportId(ac.FlightPlan.Destination);
        if (string.IsNullOrWhiteSpace(destination))
        {
            return null;
        }

        var classification = Classify(ac, starsConfig, area);
        bool showDestination = classification switch
        {
            { IsPrimaryArrival: true } => area.ShowDestinationPrimaryArrivals,
            { IsSatelliteArrival: true } => area.ShowDestinationSatelliteArrivals,
            { IsDeparture: true } => area.ShowDestinationDepartures,
            _ => false,
        };

        if (!showDestination)
        {
            return null;
        }

        // Deliberately not truncated to the scratchpad character limit. STARS applies that limit only
        // to controller-entered text; the destination fallback renders in full, so a 4-character
        // identifier stays readable at a facility that allows only 3-character scratchpads. Clipping
        // it here would turn "CYVR" into a "CYV" that reads like an adapted 3-letter code.
        return destination;
    }

    /// <summary>
    /// Classifies a track as departure / arrival / primary arrival relative to a STARS area.
    /// The area's primary airport is the first entry of its tower-list adaptation; areas with no
    /// tower list have no primary airport, so every facility arrival is a satellite arrival.
    /// </summary>
    public static StarsFlightClassification Classify(AircraftState ac, StarsConfig starsConfig, StarsAreaConfig area)
    {
        var departure = ResolveDisplayAirportId(ac.FlightPlan.Departure);
        var destination = ResolveDisplayAirportId(ac.FlightPlan.Destination);

        bool isDeparture = IsFacilityAirport(departure, starsConfig.InternalAirports);
        bool isArrival = IsFacilityAirport(destination, starsConfig.InternalAirports);

        bool isPrimaryArrival = false;
        if (isArrival && area.TowerListConfigurations.Count > 0)
        {
            var primaryAirport = area.TowerListConfigurations[0].AirportId;
            isPrimaryArrival = NavigationDatabase.AirportIdsMatch(destination, primaryAirport);
        }

        return new StarsFlightClassification(isDeparture, isArrival, isPrimaryArrival);
    }

    private static bool IsFacilityAirport(string airportId, List<string> internalAirports)
    {
        if (string.IsNullOrEmpty(airportId))
        {
            return false;
        }

        foreach (var internalAirport in internalAirports)
        {
            if (NavigationDatabase.AirportIdsMatch(airportId, internalAirport))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a filed airport identifier to the FAA form STARS displays and adapts against,
    /// falling back to the identifier as filed when it carries no published FAA id.
    /// </summary>
    private static string ResolveDisplayAirportId(string? filedId)
    {
        if (string.IsNullOrWhiteSpace(filedId))
        {
            return string.Empty;
        }

        var navDb = NavigationDatabase.InstanceOrNull;
        if (navDb is not null && navDb.TryResolveFaaId(filedId, out var faaId))
        {
            return faaId;
        }

        return filedId.Trim().ToUpperInvariant();
    }
}
