using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Reports the 3 closest ground layout nodes to an aircraft's position.
/// Use in diagnostic tick-by-tick logging to verify the aircraft is following
/// the expected taxiway edges.
/// </summary>
public static class NearestNodeHelper
{
    public static string Describe(AircraftState aircraft, AirportGroundLayout layout, int count = 3)
    {
        var ranked = layout
            .Nodes.Values.Select(n => (Node: n, Dist: GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, n.Latitude, n.Longitude)))
            .OrderBy(x => x.Dist)
            .Take(count);

        var parts = new List<string>();
        foreach (var (node, dist) in ranked)
        {
            string type = node.Type switch
            {
                GroundNodeType.RunwayHoldShort => $"HS({node.RunwayId})",
                GroundNodeType.Parking => "Parking",
                GroundNodeType.Spot => "Spot",
                _ => "Twy",
            };
            string twys = string.Join("/", node.Edges.Select(e => e.TaxiwayName).Distinct());
            parts.Add($"#{node.Id} {type}[{twys}] {dist * 6076.12:F0}ft");
        }

        return string.Join(", ", parts);
    }

    public static void Log(ITestOutputHelper output, string prefix, AircraftState aircraft, AirportGroundLayout layout, int count = 3)
    {
        output.WriteLine($"{prefix} nearestNodes=[{Describe(aircraft, layout, count)}]");
    }
}
