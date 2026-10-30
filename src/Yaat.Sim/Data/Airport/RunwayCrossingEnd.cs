using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Which end of a runway a pilot names. A crossing bar's target is the combined centerline id ("28R/10L"); a pilot names an
/// active runway by its active end, so with SFO landing 1L and 1R the runway is "one left", never "one niner right", wherever
/// the aircraft is on it. A runway with no active end is named by the end whose threshold the aircraft is nearest to.
/// </summary>
public static class RunwayCrossingEnd
{
    /// <summary>
    /// The display designator of the end to name: the end the room lists as active at the layout's airport; with both ends
    /// listed, the nearer of them; with neither listed (or no list for the airport), the end nearest the aircraft. The first
    /// end when the ends cannot be resolved and neither is active.
    /// </summary>
    /// <param name="aircraft">The aircraft naming the runway.</param>
    /// <param name="combinedTarget">The runway id, combined ("01L/19R") or single.</param>
    /// <param name="layout">The airport's ground layout; null names the first end.</param>
    /// <param name="activeRunways">The room's active runways.</param>
    /// <returns>The display designator (<c>1L</c>).</returns>
    public static string Spoken(AircraftState aircraft, string combinedTarget, AirportGroundLayout? layout, ActiveRunways activeRunways)
    {
        var runway = RunwayIdentifier.Parse(combinedTarget);
        if (string.Equals(runway.End1, runway.End2, StringComparison.OrdinalIgnoreCase) || layout is null)
        {
            return RunwayIdentifier.ToDisplayDesignator(runway.End1);
        }

        bool end1Active = IsListed(activeRunways, layout.AirportId, runway.End1);
        bool end2Active = IsListed(activeRunways, layout.AirportId, runway.End2);
        if (end1Active != end2Active)
        {
            return RunwayIdentifier.ToDisplayDesignator(end1Active ? runway.End1 : runway.End2);
        }

        NavigationDatabase? db = NavigationDatabase.InstanceOrNull;
        RunwayInfo? end1 = db?.GetRunway(layout.AirportId, runway.End1);
        RunwayInfo? end2 = db?.GetRunway(layout.AirportId, runway.End2);
        if (end1 is null || end2 is null)
        {
            return RunwayIdentifier.ToDisplayDesignator(runway.End1);
        }

        double toEnd1 = GeoMath.DistanceNm(aircraft.Position, new LatLon(end1.ThresholdLatitude, end1.ThresholdLongitude));
        double toEnd2 = GeoMath.DistanceNm(aircraft.Position, new LatLon(end2.ThresholdLatitude, end2.ThresholdLongitude));
        return RunwayIdentifier.ToDisplayDesignator(toEnd1 <= toEnd2 ? runway.End1 : runway.End2);
    }

    /// <summary>True when the room lists either end of <paramref name="runway"/> as active at <paramref name="airportId"/>.</summary>
    public static bool HasActiveEnd(RunwayIdentifier runway, string airportId, ActiveRunways activeRunways) =>
        IsListed(activeRunways, airportId, runway.End1) || IsListed(activeRunways, airportId, runway.End2);

    private static bool IsListed(ActiveRunways activeRunways, string airportId, string end)
    {
        string normalized = RunwayIdentifier.NormalizeDesignator(end);
        foreach (ActiveRunway active in activeRunways.For(airportId))
        {
            if (string.Equals(active.Designator, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
