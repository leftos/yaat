namespace Yaat.Sim.Data;

/// <summary>An airway <see cref="NavigationDatabase.AirwaysNear"/> lists: its id and the fix where it is nearest to where it was asked.</summary>
/// <param name="Id">The airway's id (<c>V25</c>).</param>
/// <param name="JoinFix">The airway's nearest qualifying fix, with its distance from the position asked about.</param>
public sealed record NearbyAirway(string Id, NearbyFix JoinFix);
