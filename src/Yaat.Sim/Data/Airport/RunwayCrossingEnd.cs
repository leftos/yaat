using Yaat.Sim.Data;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Which end of a crossed runway to name. A crossing bar's target is the combined centerline id ("28R/10L"); the pilot
/// reporting "holding short runway two eight right" and the controller clearing "cross runway two eight right" both
/// mean the end whose threshold the aircraft is nearest to.
/// </summary>
public static class RunwayCrossingEnd
{
    /// <summary>The display designator of the nearest end, or of the first end when the ends cannot be resolved.</summary>
    public static string Nearest(AircraftState aircraft, string combinedTarget, AirportGroundLayout? layout)
    {
        var runway = RunwayIdentifier.Parse(combinedTarget);
        if (string.Equals(runway.End1, runway.End2, StringComparison.OrdinalIgnoreCase) || layout is null)
        {
            return RunwayIdentifier.ToDisplayDesignator(runway.End1);
        }

        var db = NavigationDatabase.InstanceOrNull;
        var end1 = db?.GetRunway(layout.AirportId, runway.End1);
        var end2 = db?.GetRunway(layout.AirportId, runway.End2);
        if (end1 is null || end2 is null)
        {
            return RunwayIdentifier.ToDisplayDesignator(runway.End1);
        }

        double toEnd1 = GeoMath.DistanceNm(aircraft.Position, new LatLon(end1.ThresholdLatitude, end1.ThresholdLongitude));
        double toEnd2 = GeoMath.DistanceNm(aircraft.Position, new LatLon(end2.ThresholdLatitude, end2.ThresholdLongitude));
        return RunwayIdentifier.ToDisplayDesignator(toEnd1 <= toEnd2 ? runway.End1 : runway.End2);
    }
}
