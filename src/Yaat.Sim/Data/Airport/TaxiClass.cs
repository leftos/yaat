using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Data.Airport;

/// <summary>The aircraft class a graph search runs for: its performance category and wake class.</summary>
public readonly record struct TaxiClass(AircraftCategory Category, WakeTurbulenceData.WakeClass Wake)
{
    /// <summary>The class of <paramref name="aircraft"/>'s type.</summary>
    public static TaxiClass Of(AircraftState aircraft)
    {
        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        return new TaxiClass(category, WakeTurbulenceData.WakeClassForType(aircraft.AircraftType, category));
    }

    /// <summary>
    /// The auto route from <paramref name="fromNodeId"/> to the cheapest goal of <paramref name="request"/> it reaches as the
    /// request holds it (<see cref="NearestGoalRequest"/>); null when none.
    /// </summary>
    public GoalRoute? FindRoute(AirportGroundLayout layout, int fromNodeId, NearestGoalRequest request) =>
        TaxiPathfinder.FindRouteToNearestGoal(layout, fromNodeId, Category, Wake, request);
}
