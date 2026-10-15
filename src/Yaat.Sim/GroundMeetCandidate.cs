using Yaat.Sim.Data.Airport;

namespace Yaat.Sim;

/// <summary>
/// An on-ground aircraft that can meet another on the taxiways (<see cref="GroundConflictDetector.MeetPointsOf"/>): its driven
/// <see cref="Route"/>, the from-node position of that route's current segment (<see cref="RouteStart"/>, where the route's
/// look-ahead counts from), and the <see cref="AirportId"/> of the ground layout its route's node ids belong to.
/// </summary>
public readonly record struct GroundMeetCandidate(AircraftState Aircraft, TaxiRoute Route, LatLon RouteStart, string AirportId);
