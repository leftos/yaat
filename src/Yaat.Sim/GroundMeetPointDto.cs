namespace Yaat.Sim;

/// <summary>
/// Where a ground aircraft's route meets another aircraft's, as the ground conflict detector predicts it: the first junction
/// both driven routes reach within the convergence look-ahead (<c>GroundConflictDetector.FindSharedUpcomingNode</c>; a
/// <c>GIVEWAY</c> hold judges its junction over the whole of both routes, <c>GroundConflictDetector.GiveWayJunctionOf</c>).
/// <see cref="Callsign"/> is the other aircraft, <see cref="NodeId"/> the junction's
/// ground-layout node, <see cref="Position"/> that node's position.
/// </summary>
public sealed record GroundMeetPointDto(string Callsign, int NodeId, LatLon Position);
