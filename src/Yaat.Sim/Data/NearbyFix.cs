namespace Yaat.Sim.Data;

/// <summary>A fix <see cref="NavigationDatabase.FixesNear"/> lists: its name, its position and its distance from where it was asked.</summary>
/// <param name="Name">The fix's name.</param>
/// <param name="Position">The fix's position.</param>
/// <param name="DistanceNm">The fix's distance from the position asked about, in nautical miles.</param>
public sealed record NearbyFix(string Name, LatLon Position, double DistanceNm);
