using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Airport;

public enum GroundNodeType
{
    TaxiwayIntersection,
    Parking,
    Spot,
    RunwayHoldShort,
}

public sealed class GroundNode
{
    public required int Id { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required GroundNodeType Type { get; init; }
    public string? Name { get; init; }

    /// <summary>
    /// Parking heading (nose-in direction, degrees true). Only set for Parking nodes.
    /// </summary>
    public double? Heading { get; init; }

    /// <summary>
    /// Runway ID that this hold-short node protects. Only set for RunwayHoldShort nodes.
    /// </summary>
    public string? RunwayId { get; init; }

    /// <summary>
    /// Adjacent edges for graph traversal. Populated during layout construction.
    /// </summary>
    public List<GroundEdge> Edges { get; } = [];
}

public sealed class GroundEdge
{
    public required int FromNodeId { get; init; }
    public required int ToNodeId { get; init; }
    public required string TaxiwayName { get; init; }
    public required double DistanceNm { get; init; }

    /// <summary>
    /// Intermediate coordinates along this edge (lat, lon pairs) for curved paths.
    /// Does NOT include the from/to node positions — those are looked up from nodes.
    /// </summary>
    public List<(double Lat, double Lon)> IntermediatePoints { get; init; } = [];
}

public sealed class AirportGroundLayout
{
    public required string AirportId { get; init; }
    public Dictionary<int, GroundNode> Nodes { get; } = [];
    public List<GroundEdge> Edges { get; } = [];

    public GroundNode? FindParkingByName(string name)
    {
        foreach (var node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.Parking
                && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }

        return null;
    }

    public GroundNode? FindNearestNode(double lat, double lon)
    {
        GroundNode? best = null;
        double bestDist = double.MaxValue;

        foreach (var node in Nodes.Values)
        {
            double dist = GeoMath.DistanceNm(lat, lon, node.Latitude, node.Longitude);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = node;
            }
        }

        return best;
    }

    /// <summary>
    /// Find the nearest taxiway node suitable as a runway exit, considering aircraft heading.
    /// Prefers exits that don't require turns greater than 90 degrees.
    /// </summary>
    public GroundNode? FindNearestExit(
        double lat, double lon, double runwayHeading, double maxSearchNm = 0.5)
    {
        GroundNode? best = null;
        double bestScore = double.MaxValue;

        foreach (var node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.Parking)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(lat, lon, node.Latitude, node.Longitude);
            if (dist > maxSearchNm)
            {
                continue;
            }

            bool hasTaxiwayEdge = false;
            foreach (var edge in node.Edges)
            {
                if (!IsRunwayEdge(edge))
                {
                    hasTaxiwayEdge = true;
                    break;
                }
            }

            if (!hasTaxiwayEdge)
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(lat, lon, node.Latitude, node.Longitude);
            double turnAngle = Math.Abs(NormalizeAngle(bearing - runwayHeading));
            double score = dist + (turnAngle > 90 ? 10.0 : 0.0);

            if (score < bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        return best;
    }

    /// <summary>
    /// Find the nearest exit on the specified side of the runway heading.
    /// Falls back to FindNearestExit if no exits match the requested side.
    /// </summary>
    public GroundNode? FindExitBySide(
        double lat, double lon, double runwayHeading, ExitSide side,
        double maxSearchNm = 0.5)
    {
        GroundNode? best = null;
        double bestScore = double.MaxValue;

        foreach (var node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.Parking)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(lat, lon, node.Latitude, node.Longitude);
            if (dist > maxSearchNm)
            {
                continue;
            }

            bool hasTaxiwayEdge = false;
            foreach (var edge in node.Edges)
            {
                if (!IsRunwayEdge(edge))
                {
                    hasTaxiwayEdge = true;
                    break;
                }
            }

            if (!hasTaxiwayEdge)
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(lat, lon, node.Latitude, node.Longitude);
            double relative = NormalizeAngle(bearing - runwayHeading);

            // Left = negative relative angle, Right = positive
            bool isOnRequestedSide = side == ExitSide.Left ? relative < 0 : relative > 0;
            if (!isOnRequestedSide)
            {
                continue;
            }

            double turnAngle = Math.Abs(relative);
            double score = dist + (turnAngle > 90 ? 10.0 : 0.0);

            if (score < bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        // Fall back to nearest exit if none found on the requested side
        return best ?? FindNearestExit(lat, lon, runwayHeading, maxSearchNm);
    }

    /// <summary>
    /// Find an exit node connected to the named taxiway.
    /// Uses a wider search radius since the taxiway might be further ahead.
    /// </summary>
    public GroundNode? FindExitByTaxiway(
        double lat, double lon, string taxiwayName,
        double maxSearchNm = 1.0)
    {
        GroundNode? best = null;
        double bestDist = double.MaxValue;

        foreach (var node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.Parking)
            {
                continue;
            }

            double dist = GeoMath.DistanceNm(lat, lon, node.Latitude, node.Longitude);
            if (dist > maxSearchNm)
            {
                continue;
            }

            bool hasMatchingEdge = false;
            foreach (var edge in node.Edges)
            {
                if (!IsRunwayEdge(edge)
                    && string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
                {
                    hasMatchingEdge = true;
                    break;
                }
            }

            if (!hasMatchingEdge)
            {
                continue;
            }

            if (dist < bestDist)
            {
                bestDist = dist;
                best = node;
            }
        }

        return best;
    }

    /// <summary>
    /// Get the taxiway name for the edge connected to a node that leads away from the runway.
    /// </summary>
    public string? GetExitTaxiwayName(GroundNode exitNode)
    {
        foreach (var edge in exitNode.Edges)
        {
            if (!IsRunwayEdge(edge))
            {
                return edge.TaxiwayName;
            }
        }

        return null;
    }

    /// <summary>
    /// Get all hold-short nodes for a specific runway.
    /// </summary>
    public List<GroundNode> GetRunwayHoldShortNodes(string runwayId)
    {
        var result = new List<GroundNode>();
        foreach (var node in Nodes.Values)
        {
            if (node.Type == GroundNodeType.RunwayHoldShort
                && string.Equals(node.RunwayId, runwayId, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(node);
            }
        }

        return result;
    }

    private static bool IsRunwayEdge(GroundEdge edge)
    {
        return edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase);
    }

    private static double NormalizeAngle(double angle)
    {
        angle %= 360.0;
        if (angle > 180.0)
        {
            angle -= 360.0;
        }

        if (angle < -180.0)
        {
            angle += 360.0;
        }

        return angle;
    }
}
