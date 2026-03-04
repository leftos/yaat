using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ViewModels;

public partial class GroundViewModel : ObservableObject
{
    private readonly ILogger _log = AppLog.CreateLogger<GroundViewModel>();

    private readonly ServerConnection _connection;
    private readonly Func<string, string, string, Task> _sendCommand;
    private readonly Action<AircraftModel?>? _onSelectionChanged;
    private AirportGroundLayout? _domainLayout;

    [ObservableProperty]
    private GroundLayoutDto? _layout;

    [ObservableProperty]
    private AircraftModel? _selectedAircraft;

    [ObservableProperty]
    private TaxiRoute? _activeRoute;

    [ObservableProperty]
    private TaxiRoute? _previewRoute;

    public ObservableCollection<AircraftModel> GroundAircraft { get; } = [];

    public GroundViewModel(
        ServerConnection connection,
        Func<string, string, string, Task> sendCommand,
        Action<AircraftModel?>? onSelectionChanged = null
    )
    {
        _connection = connection;
        _sendCommand = sendCommand;
        _onSelectionChanged = onSelectionChanged;
    }

    partial void OnSelectedAircraftChanged(AircraftModel? value)
    {
        _onSelectionChanged?.Invoke(value);
    }

    public async Task LoadLayoutAsync(string airportId)
    {
        try
        {
            var dto = await _connection.GetAirportGroundLayoutAsync(airportId);
            if (dto is null)
            {
                _log.LogWarning("No ground layout for airport {Id}", airportId);
                Layout = null;
                _domainLayout = null;
                return;
            }

            Layout = dto;
            _domainLayout = ReconstructLayout(dto);
            _log.LogInformation("Ground layout loaded for {Id}: {Nodes} nodes, {Edges} edges", airportId, dto.Nodes.Count, dto.Edges.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load ground layout for {Id}", airportId);
        }
    }

    public void ClearLayout()
    {
        Layout = null;
        _domainLayout = null;
        ActiveRoute = null;
        PreviewRoute = null;
        GroundAircraft.Clear();
    }

    public void UpdateAircraftList(IEnumerable<AircraftModel> allAircraft)
    {
        GroundAircraft.Clear();
        foreach (var ac in allAircraft)
        {
            if (ac.IsOnGround)
            {
                GroundAircraft.Add(ac);
            }
        }
    }

    public TaxiRoute? FindRouteToNode(int fromNodeId, int toNodeId)
    {
        if (_domainLayout is null)
        {
            return null;
        }

        return TaxiPathfinder.FindRoute(_domainLayout, fromNodeId, toNodeId);
    }

    public string BuildTaxiCommand(TaxiRoute route)
    {
        var taxiways = new List<string>();
        foreach (var seg in route.Segments)
        {
            var name = seg.TaxiwayName;
            if (name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (taxiways.Count == 0 || taxiways[^1] != name)
            {
                taxiways.Add(name);
            }
        }

        return string.Join(" ", taxiways);
    }

    public int? FindNearestNodeId(double lat, double lon)
    {
        if (_domainLayout is null)
        {
            return null;
        }

        var node = _domainLayout.FindNearestNode(lat, lon);
        return node?.Id;
    }

    public int? GetAircraftNearestNodeId(AircraftModel ac)
    {
        if (_domainLayout is null)
        {
            return null;
        }

        var node = _domainLayout.FindNearestNode(ac.Latitude, ac.Longitude);
        return node?.Id;
    }

    public GroundNodeDto? GetNode(int nodeId)
    {
        return Layout?.Nodes.Find(n => n.Id == nodeId);
    }

    // --- Command methods ---

    public async Task TaxiToNodeAsync(string callsign, string initials, int toNodeId)
    {
        if (_domainLayout is null || SelectedAircraft is null)
        {
            return;
        }

        var fromNodeId = GetAircraftNearestNodeId(SelectedAircraft);
        if (fromNodeId is null)
        {
            return;
        }

        var route = FindRouteToNode(fromNodeId.Value, toNodeId);
        if (route is null)
        {
            _log.LogWarning("No route from node {From} to {To}", fromNodeId, toNodeId);
            return;
        }

        var taxiways = BuildTaxiCommand(route);
        if (string.IsNullOrEmpty(taxiways))
        {
            return;
        }

        ActiveRoute = route;
        await _sendCommand(callsign, $"TAXI {taxiways}", initials);
    }

    public async Task HoldPositionAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "HP", initials);
    }

    public async Task ResumeAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "RES", initials);
    }

    public async Task PushbackAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "PUSH", initials);
    }

    public async Task CrossRunwayAsync(string callsign, string initials, string runwayId)
    {
        await _sendCommand(callsign, $"CROSS {runwayId}", initials);
    }

    public async Task LineUpAndWaitAsync(string callsign, string initials, string runwayId)
    {
        await _sendCommand(callsign, $"LUAW {runwayId}", initials);
    }

    public async Task ClearedForTakeoffAsync(string callsign, string initials, string runwayId)
    {
        await _sendCommand(callsign, $"CTO {runwayId}", initials);
    }

    public async Task GoAroundAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "GA", initials);
    }

    public async Task CancelTakeoffClearanceAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "CTOC", initials);
    }

    public async Task ClearedToLandAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "CTL", initials);
    }

    public async Task CancelLandingClearanceAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "CLC", initials);
    }

    public async Task TouchAndGoAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "TG", initials);
    }

    public async Task StopAndGoAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "SG", initials);
    }

    public async Task LowApproachAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "LA", initials);
    }

    public async Task ClearedForOptionAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "COPT", initials);
    }

    public async Task ExitLeftAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "EL", initials);
    }

    public async Task ExitRightAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "ER", initials);
    }

    public async Task PushbackHeadingAsync(string callsign, string initials, int heading)
    {
        await _sendCommand(callsign, $"PUSH {heading}", initials);
    }

    public async Task ParkAtAsync(string callsign, string initials, string spotName)
    {
        await _sendCommand(callsign, $"TAXI @{spotName}", initials);
    }

    public async Task SendRawCommandAsync(string callsign, string initials, string command)
    {
        await _sendCommand(callsign, command, initials);
    }

    public async Task HoldShortAsync(string callsign, string initials, string target)
    {
        await _sendCommand(callsign, $"HS {target}", initials);
    }

    public async Task DeleteAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "DEL", initials);
    }

    public async Task WarpToNodeAsync(string callsign, int nodeId)
    {
        if (_domainLayout is null || !_domainLayout.Nodes.TryGetValue(nodeId, out var node))
        {
            return;
        }

        var ac = GroundAircraft.FirstOrDefault(a => a.Callsign == callsign);
        var currentHeading = ac?.Heading ?? 0;

        var heading = PickBestEdgeHeading(_domainLayout, node, currentHeading);
        await _connection.WarpAircraftAsync(callsign, node.Latitude, node.Longitude, heading);
    }

    private static double PickBestEdgeHeading(AirportGroundLayout layout, GroundNode node, double currentHeading)
    {
        double bestHeading = currentHeading;
        double bestDelta = 360;

        foreach (var edge in node.Edges)
        {
            double bearing = ComputeEdgeBearing(layout, node, edge);

            double delta = Math.Abs(NormalizeDelta(bearing - currentHeading));
            if (delta < bestDelta)
            {
                bestDelta = delta;
                bestHeading = bearing;
            }
        }

        return bestHeading;
    }

    private static double ComputeEdgeBearing(AirportGroundLayout layout, GroundNode node, GroundEdge edge)
    {
        // Use the first intermediate point along the direction away from node
        if (edge.FromNodeId == node.Id && edge.IntermediatePoints.Count > 0)
        {
            var pt = edge.IntermediatePoints[0];
            return Yaat.Sim.GeoMath.BearingTo(node.Latitude, node.Longitude, pt.Lat, pt.Lon);
        }

        if (edge.ToNodeId == node.Id && edge.IntermediatePoints.Count > 0)
        {
            var pt = edge.IntermediatePoints[^1];
            return Yaat.Sim.GeoMath.BearingTo(node.Latitude, node.Longitude, pt.Lat, pt.Lon);
        }

        // No intermediate points — use the other node's position
        int otherId = edge.FromNodeId == node.Id ? edge.ToNodeId : edge.FromNodeId;
        if (layout.Nodes.TryGetValue(otherId, out var otherNode))
        {
            return Yaat.Sim.GeoMath.BearingTo(node.Latitude, node.Longitude, otherNode.Latitude, otherNode.Longitude);
        }

        return 0;
    }

    private static double NormalizeDelta(double delta)
    {
        delta %= 360;
        if (delta > 180)
        {
            delta -= 360;
        }
        else if (delta < -180)
        {
            delta += 360;
        }

        return delta;
    }

    public List<TaxiRoute> FindRoutesToNode(int fromNodeId, int toNodeId)
    {
        if (_domainLayout is null)
        {
            return [];
        }

        return TaxiPathfinder.FindRoutes(_domainLayout, fromNodeId, toNodeId);
    }

    /// <summary>
    /// Returns all crossing variations for a taxi route.
    /// For N runway crossings, produces N+1 entries:
    /// hold-short at first crossing, cross 1 then HS next, ..., cross all.
    /// Routes with no crossings return a single entry.
    /// Each entry: (displayLabel, command).
    /// </summary>
    public List<(string Label, string Command)> BuildTaxiCrossingVariants(TaxiRoute route)
    {
        var taxiways = BuildTaxiCommand(route);
        if (string.IsNullOrEmpty(taxiways))
        {
            return [];
        }

        var crossings = new List<string>();
        foreach (var hs in route.HoldShortPoints)
        {
            if (hs.Reason == HoldShortReason.RunwayCrossing && hs.TargetName is not null)
            {
                crossings.Add(RunwayIdentifier.Parse(hs.TargetName).End1);
            }
        }

        if (crossings.Count == 0)
        {
            return [("", $"TAXI {taxiways}")];
        }

        var results = new List<(string Label, string Command)>();

        // Variation 0: hold short of first crossing
        results.Add(($"HS {crossings[0]}", $"TAXI {taxiways} HS {crossings[0]}"));

        // Variations 1..N-1: cross some, hold short of next
        for (int i = 0; i < crossings.Count - 1; i++)
        {
            var crossParts = crossings.Take(i + 1).Select(r => $"CROSS {r}");
            var holdAt = crossings[i + 1];
            var label = $"CROSS {string.Join(" ", crossings.Take(i + 1))} HS {holdAt}";
            var cmd = $"TAXI {taxiways} HS {holdAt}, {string.Join(", ", crossParts)}";
            results.Add((label, cmd));
        }

        // Variation N: cross all
        var allCrossParts = crossings.Select(r => $"CROSS {r}");
        results.Add(($"CROSS {string.Join(" ", crossings)}", $"TAXI {taxiways}, {string.Join(", ", allCrossParts)}"));

        return results;
    }

    public string GetTaxiwayDisplayName(TaxiRoute route)
    {
        var names = new List<string>();
        foreach (var seg in route.Segments)
        {
            var name = seg.TaxiwayName;
            if (name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (names.Count == 0 || !string.Equals(names[^1], name, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names.Count > 0 ? $"via {string.Join(" ", names)}" : "direct";
    }

    public List<(string Label, int Heading)> GetPushbackDirections(AircraftModel ac)
    {
        if (_domainLayout is null)
        {
            return [];
        }

        var nodeId = GetAircraftNearestNodeId(ac);
        if (nodeId is null || !_domainLayout.Nodes.TryGetValue(nodeId.Value, out var node))
        {
            return [];
        }

        var directions = new List<(string Label, int Heading)>();
        foreach (var edge in node.Edges)
        {
            int otherId = edge.FromNodeId == nodeId.Value ? edge.ToNodeId : edge.FromNodeId;

            if (!_domainLayout.Nodes.TryGetValue(otherId, out var otherNode))
            {
                continue;
            }

            var bearing = Yaat.Sim.GeoMath.BearingTo(node.Latitude, node.Longitude, otherNode.Latitude, otherNode.Longitude);
            var heading = (int)Math.Round(bearing);
            if (heading <= 0)
            {
                heading += 360;
            }

            var label = edge.TaxiwayName;
            if (!string.Equals(label, "RAMP", StringComparison.OrdinalIgnoreCase))
            {
                directions.Add(($"face {label}", heading));
            }
        }

        return directions;
    }

    public List<(string DisplayName, string Target)> GetHoldShortTargets(AircraftModel ac)
    {
        if (_domainLayout is null)
        {
            return [];
        }

        // Resolve the actual remaining path from the aircraft's position
        var route = ResolveRemainingRoute(ac);
        if (route is null || route.Segments.Count == 0)
        {
            return [];
        }

        var routeTaxiways = ParseRouteTaxiways(ac.TaxiRoute);
        var routeSet = new HashSet<string>(routeTaxiways, StringComparer.OrdinalIgnoreCase);
        var runways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var taxiways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Collect all node IDs along the resolved path
        var routeNodeIds = new HashSet<int> { route.Segments[0].FromNodeId };
        foreach (var seg in route.Segments)
        {
            routeNodeIds.Add(seg.ToNodeId);
        }

        // Scan only nodes on the actual path
        foreach (int nodeId in routeNodeIds)
        {
            if (!_domainLayout.Nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } rwyId)
            {
                runways.Add(rwyId.End1);
                if (!string.Equals(rwyId.End1, rwyId.End2, StringComparison.OrdinalIgnoreCase))
                {
                    runways.Add(rwyId.End2);
                }
            }

            foreach (var adj in node.Edges)
            {
                var name = adj.TaxiwayName;
                if (routeSet.Contains(name))
                {
                    continue;
                }

                if (name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                taxiways.Add(name);
            }
        }

        var results = new List<(string DisplayName, string Target)>();
        foreach (var rwy in runways.Order())
        {
            results.Add(($"Runway {rwy}", rwy));
        }

        foreach (var tw in taxiways.Order())
        {
            results.Add(($"Taxiway {tw}", tw));
        }

        return results;
    }

    public TaxiRoute? FindHoldShortPreviewRoute(AircraftModel ac, string target)
    {
        if (_domainLayout is null)
        {
            return null;
        }

        var route = ResolveRemainingRoute(ac);
        if (route is null)
        {
            return null;
        }

        for (int i = 0; i < route.Segments.Count; i++)
        {
            var seg = route.Segments[i];
            int nodeId = seg.ToNodeId;

            if (!_domainLayout.Nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            bool matches = node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } hsRwyId && hsRwyId.Contains(target);

            if (!matches)
            {
                foreach (var edge in node.Edges)
                {
                    if (string.Equals(edge.TaxiwayName, target, StringComparison.OrdinalIgnoreCase))
                    {
                        matches = true;
                        break;
                    }
                }
            }

            if (matches)
            {
                return new TaxiRoute { Segments = route.Segments.GetRange(0, i + 1), HoldShortPoints = [] };
            }
        }

        return null;
    }

    private TaxiRoute? ResolveRemainingRoute(AircraftModel ac)
    {
        if (_domainLayout is null)
        {
            return null;
        }

        var routeTaxiways = ParseRouteTaxiways(ac.TaxiRoute);
        if (routeTaxiways.Count == 0)
        {
            return null;
        }

        var nodeId = GetAircraftNearestNodeId(ac);
        if (nodeId is null)
        {
            return null;
        }

        // Trim to start from the aircraft's current taxiway
        if (!string.IsNullOrEmpty(ac.CurrentTaxiway))
        {
            int startIdx = routeTaxiways.FindIndex(tw => string.Equals(tw, ac.CurrentTaxiway, StringComparison.OrdinalIgnoreCase));
            if (startIdx > 0)
            {
                routeTaxiways = routeTaxiways.GetRange(startIdx, routeTaxiways.Count - startIdx);
            }
        }

        return TaxiPathfinder.ResolveExplicitPath(_domainLayout, nodeId.Value, routeTaxiways, out _);
    }

    private static List<string> ParseRouteTaxiways(string taxiRoute)
    {
        if (string.IsNullOrWhiteSpace(taxiRoute))
        {
            return [];
        }

        var result = new List<string>();
        foreach (var part in taxiRoute.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Stop at "HS" marker — everything after is hold-short metadata
            if (string.Equals(part, "HS", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            result.Add(part);
        }

        return result;
    }

    private static AirportGroundLayout ReconstructLayout(GroundLayoutDto dto)
    {
        var layout = new AirportGroundLayout { AirportId = dto.AirportId };

        foreach (var nodeDto in dto.Nodes)
        {
            var type = Enum.TryParse<GroundNodeType>(nodeDto.Type, out var t) ? t : GroundNodeType.TaxiwayIntersection;

            var node = new GroundNode
            {
                Id = nodeDto.Id,
                Latitude = nodeDto.Latitude,
                Longitude = nodeDto.Longitude,
                Type = type,
                Name = nodeDto.Name,
                Heading = nodeDto.Heading,
                RunwayId = nodeDto.RunwayId is not null ? RunwayIdentifier.Parse(nodeDto.RunwayId) : null,
            };
            layout.Nodes[node.Id] = node;
        }

        foreach (var edgeDto in dto.Edges)
        {
            var intermediates = new List<(double Lat, double Lon)>();
            if (edgeDto.IntermediatePoints is not null)
            {
                foreach (var pt in edgeDto.IntermediatePoints)
                {
                    if (pt.Length >= 2)
                    {
                        intermediates.Add((pt[0], pt[1]));
                    }
                }
            }

            var edge = new GroundEdge
            {
                FromNodeId = edgeDto.FromNodeId,
                ToNodeId = edgeDto.ToNodeId,
                TaxiwayName = edgeDto.TaxiwayName,
                DistanceNm = edgeDto.DistanceNm,
                IntermediatePoints = intermediates,
            };
            layout.Edges.Add(edge);

            // Build adjacency
            if (layout.Nodes.TryGetValue(edge.FromNodeId, out var fromNode))
            {
                fromNode.Edges.Add(edge);
            }

            if (layout.Nodes.TryGetValue(edge.ToNodeId, out var toNode))
            {
                // Add reverse edge reference for undirected graph
                toNode.Edges.Add(edge);
            }
        }

        return layout;
    }
}
