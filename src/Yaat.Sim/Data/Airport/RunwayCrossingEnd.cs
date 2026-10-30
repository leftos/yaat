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
    public static string Spoken(AircraftState aircraft, string combinedTarget, AirportGroundLayout? layout, ActiveRunways activeRunways) =>
        SpokenFrom(aircraft.Position, combinedTarget, layout, activeRunways);

    /// <summary>
    /// <see cref="Spoken"/> from a position rather than an aircraft: the end named from <paramref name="position"/>; the first end
    /// where the nearer end is needed and <paramref name="position"/> is null.
    /// </summary>
    /// <param name="position">Where the runway is named from; null when not known.</param>
    /// <param name="combinedTarget">The runway id, combined ("01L/19R") or single.</param>
    /// <param name="layout">The airport's ground layout; null names the first end.</param>
    /// <param name="activeRunways">The room's active runways.</param>
    /// <returns>The display designator (<c>1L</c>).</returns>
    public static string SpokenFrom(LatLon? position, string combinedTarget, AirportGroundLayout? layout, ActiveRunways activeRunways)
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
        if ((end1 is null) || (end2 is null) || (position is not { } from))
        {
            return RunwayIdentifier.ToDisplayDesignator(runway.End1);
        }

        double toEnd1 = GeoMath.DistanceNm(from, new LatLon(end1.ThresholdLatitude, end1.ThresholdLongitude));
        double toEnd2 = GeoMath.DistanceNm(from, new LatLon(end2.ThresholdLatitude, end2.ThresholdLongitude));
        return RunwayIdentifier.ToDisplayDesignator(toEnd1 <= toEnd2 ? runway.End1 : runway.End2);
    }

    /// <summary>
    /// What names the runway of a tail-over warning (<see cref="HoldShortAnnotator.ComputeHoldShortPositions"/>): when the room
    /// lists an end of it, the aircraft's own runway by its assigned end and any other by its active end (with both ends listed,
    /// the end nearer <paramref name="position"/>, else the first); both ends otherwise (<see cref="HoldShortAnnotator.CombinedName"/>).
    /// </summary>
    /// <param name="position">Where the aircraft stands on the route; null for a route it is not on yet.</param>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="activeRunways">The room's active runways.</param>
    /// <param name="assignedRunway">The aircraft's assigned runway; null for a route it is not on yet.</param>
    /// <returns>The namer the annotator calls with the runway the tail is over.</returns>
    public static Func<RunwayIdentifier, string> TailOverNamer(
        LatLon? position,
        AirportGroundLayout layout,
        ActiveRunways activeRunways,
        RunwayInfo? assignedRunway
    ) => runway => ActiveEnd(position, runway, layout, activeRunways, assignedRunway) ?? HoldShortAnnotator.CombinedName(runway);

    /// <summary>
    /// The end controller-facing text names the runway <paramref name="targetName"/> by when the room lists an end of it as
    /// active at <paramref name="layout"/>'s airport: the aircraft's own runway (<see cref="PhaseList.AssignedRunway"/>) by its
    /// assigned end, any other runway by its active end (with both ends active, the nearer; <see cref="Spoken"/>). Null when
    /// the target is not a combined runway id (<c>01R/19L</c>), there is no layout, or neither end is active, so the caller
    /// keeps its own text.
    /// </summary>
    public static string? ActiveEndOf(AircraftState aircraft, string? targetName, AirportGroundLayout? layout, ActiveRunways activeRunways)
    {
        if ((targetName is not { } combined) || !combined.Contains('/') || (layout is null))
        {
            return null;
        }

        return ActiveEnd(aircraft.Position, RunwayIdentifier.Parse(combined), layout, activeRunways, aircraft.Phases?.AssignedRunway);
    }

    /// <summary>
    /// The end a runway is named by from <paramref name="position"/>: the aircraft's own runway (<paramref name="assignedRunway"/>,
    /// when it overlaps <paramref name="combinedTarget"/>) by its assigned end, as the departure "ready" call names it; any other
    /// runway by the end a pilot names it by (<see cref="SpokenFrom"/>: the active end, else the nearest).
    /// </summary>
    /// <param name="position">Where the runway is named from; null when not known.</param>
    /// <param name="combinedTarget">The runway id, combined ("01L/19R") or single.</param>
    /// <param name="layout">The airport's ground layout; null names the first end.</param>
    /// <param name="activeRunways">The room's active runways.</param>
    /// <param name="assignedRunway">The aircraft's assigned runway; null when it has none.</param>
    /// <returns>The display designator (<c>1L</c>).</returns>
    public static string EndFacing(
        LatLon? position,
        string combinedTarget,
        AirportGroundLayout? layout,
        ActiveRunways activeRunways,
        RunwayInfo? assignedRunway
    ) =>
        (assignedRunway is { } assigned) && assigned.Id.Overlaps(RunwayIdentifier.Parse(combinedTarget))
            ? RunwayIdentifier.ToDisplayDesignator(assigned.Designator)
            : SpokenFrom(position, combinedTarget, layout, activeRunways);

    private static string? ActiveEnd(
        LatLon? position,
        RunwayIdentifier runway,
        AirportGroundLayout layout,
        ActiveRunways activeRunways,
        RunwayInfo? assignedRunway
    ) => HasActiveEnd(runway, layout.AirportId, activeRunways) ? EndFacing(position, runway.ToString(), layout, activeRunways, assignedRunway) : null;

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
