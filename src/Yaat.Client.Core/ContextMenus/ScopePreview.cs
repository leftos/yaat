using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// What a menu row shows on the ground views while it is pointed at or focused (<see cref="IMenuHost.SetScopePreview"/>):
/// the aircraft it names ringed, this aircraft's route solid, the other aircraft's route dashed, a hold-short bar boxed and
/// the point where the two routes meet ringed in amber. Every part is optional; a part left null is not drawn.
/// </summary>
/// <param name="RingCallsign">The aircraft to ring, at wherever it is drawn; null rings none.</param>
/// <param name="OwnRoute">The route of the aircraft the menu commands, drawn solid.</param>
/// <param name="TargetRoute">The route of the aircraft the row names, drawn dashed.</param>
/// <param name="BarPoint">A hold-short bar to box, which shows with no route when the bar is where the route starts.</param>
/// <param name="MeetPoint">Where the two routes meet, ringed in amber.</param>
public sealed record ScopePreview(string? RingCallsign, TaxiRoute? OwnRoute, TaxiRoute? TargetRoute, LatLon? BarPoint, LatLon? MeetPoint);
