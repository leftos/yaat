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
    private AirportGroundLayout? _domainLayout;

    [ObservableProperty]
    private GroundLayoutDto? _layout;

    [ObservableProperty]
    private AircraftModel? _selectedAircraft;

    [ObservableProperty]
    private TaxiRoute? _activeRoute;

    public ObservableCollection<AircraftModel> GroundAircraft { get; } = [];

    public GroundViewModel(
        ServerConnection connection,
        Func<string, string, string, Task> sendCommand)
    {
        _connection = connection;
        _sendCommand = sendCommand;
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
            _log.LogInformation(
                "Ground layout loaded for {Id}: {Nodes} nodes, {Edges} edges",
                airportId, dto.Nodes.Count, dto.Edges.Count);
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

    public async Task TaxiToNodeAsync(
        string callsign, string initials, int toNodeId)
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
            _log.LogWarning("No route from node {From} to {To}",
                fromNodeId, toNodeId);
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

    public async Task HoldPositionAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "HP", initials);
    }

    public async Task ResumeAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "RES", initials);
    }

    public async Task PushbackAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "PUSH", initials);
    }

    public async Task CrossRunwayAsync(
        string callsign, string initials, string runwayId)
    {
        await _sendCommand(callsign, $"CROSS {runwayId}", initials);
    }

    public async Task LineUpAndWaitAsync(
        string callsign, string initials, string runwayId)
    {
        await _sendCommand(callsign, $"LUAW {runwayId}", initials);
    }

    public async Task ClearedForTakeoffAsync(
        string callsign, string initials, string runwayId)
    {
        await _sendCommand(callsign, $"CTO {runwayId}", initials);
    }

    public async Task GoAroundAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "GA", initials);
    }

    public async Task CancelTakeoffClearanceAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "CTOC", initials);
    }

    public async Task ClearedToLandAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "CTL", initials);
    }

    public async Task CancelLandingClearanceAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "CLC", initials);
    }

    public async Task TouchAndGoAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "TG", initials);
    }

    public async Task StopAndGoAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "SG", initials);
    }

    public async Task LowApproachAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "LA", initials);
    }

    public async Task ClearedForOptionAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "COPT", initials);
    }

    public async Task ExitLeftAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "EL", initials);
    }

    public async Task ExitRightAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "ER", initials);
    }

    public async Task PushbackHeadingAsync(
        string callsign, string initials, int heading)
    {
        await _sendCommand(callsign, $"PUSH {heading}", initials);
    }

    public async Task SendRawCommandAsync(
        string callsign, string initials, string command)
    {
        await _sendCommand(callsign, command, initials);
    }

    public async Task HoldShortAsync(
        string callsign, string initials, string target)
    {
        await _sendCommand(callsign, $"HS {target}", initials);
    }

    public async Task DeleteAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "DEL", initials);
    }

    public List<TaxiRoute> FindRoutesToNode(int fromNodeId, int toNodeId)
    {
        if (_domainLayout is null)
        {
            return [];
        }

        return TaxiPathfinder.FindRoutes(_domainLayout, fromNodeId, toNodeId);
    }

    public string BuildTaxiCommandWithCrossings(TaxiRoute route)
    {
        var taxiways = BuildTaxiCommand(route);
        if (string.IsNullOrEmpty(taxiways))
        {
            return "";
        }

        var crossings = new List<string>();
        if (_domainLayout is not null)
        {
            foreach (var hs in route.HoldShortPoints)
            {
                if (hs.Reason == HoldShortReason.RunwayCrossing
                    && hs.TargetName is not null)
                {
                    // Use first part of compound runway ID (e.g., "28L/10R" → "28L")
                    var rwyId = hs.TargetName.Split('/')[0];
                    crossings.Add(rwyId);
                }
            }
        }

        if (crossings.Count == 0)
        {
            return $"TAXI {taxiways}";
        }

        var crossPart = string.Join(", ", crossings.Select(r => $"CROSS {r}"));
        return $"TAXI {taxiways}, {crossPart}";
    }

    public string GetTaxiwayDisplayName(TaxiRoute route)
    {
        var names = new List<string>();
        foreach (var seg in route.Segments)
        {
            var name = seg.TaxiwayName;
            if (name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase))
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
            int otherId = edge.FromNodeId == nodeId.Value
                ? edge.ToNodeId : edge.FromNodeId;

            if (!_domainLayout.Nodes.TryGetValue(otherId, out var otherNode))
            {
                continue;
            }

            var bearing = Yaat.Sim.GeoMath.BearingTo(
                node.Latitude, node.Longitude,
                otherNode.Latitude, otherNode.Longitude);
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

    public List<(string DisplayName, string Target)> GetHoldShortTargets(
        AircraftModel ac)
    {
        if (_domainLayout is null)
        {
            return [];
        }

        var routeTaxiways = ParseRouteTaxiways(ac.TaxiRoute);
        if (routeTaxiways.Count == 0)
        {
            return [];
        }

        var runways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var taxiways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routeSet = new HashSet<string>(routeTaxiways, StringComparer.OrdinalIgnoreCase);

        // Walk the layout edges that belong to route taxiways;
        // at each node, collect runway hold-shorts and crossing taxiways
        foreach (var edge in _domainLayout.Edges)
        {
            if (!routeSet.Contains(edge.TaxiwayName))
            {
                continue;
            }

            foreach (int nodeId in new[] { edge.FromNodeId, edge.ToNodeId })
            {
                if (!_domainLayout.Nodes.TryGetValue(nodeId, out var node))
                {
                    continue;
                }

                // Runway hold-short at this node
                if (node.Type == GroundNodeType.RunwayHoldShort
                    && node.RunwayId is not null)
                {
                    foreach (var part in node.RunwayId.Split('/'))
                    {
                        runways.Add(part);
                    }
                }

                // Other taxiways that intersect at this node
                foreach (var adj in node.Edges)
                {
                    var name = adj.TaxiwayName;
                    if (routeSet.Contains(name))
                    {
                        continue;
                    }

                    if (name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    taxiways.Add(name);
                }
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

    private static AirportGroundLayout ReconstructLayout(
        GroundLayoutDto dto)
    {
        var layout = new AirportGroundLayout
        {
            AirportId = dto.AirportId,
        };

        foreach (var nodeDto in dto.Nodes)
        {
            var type = Enum.TryParse<GroundNodeType>(
                nodeDto.Type, out var t)
                ? t
                : GroundNodeType.TaxiwayIntersection;

            var node = new GroundNode
            {
                Id = nodeDto.Id,
                Latitude = nodeDto.Latitude,
                Longitude = nodeDto.Longitude,
                Type = type,
                Name = nodeDto.Name,
                Heading = nodeDto.Heading,
                RunwayId = nodeDto.RunwayId,
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
