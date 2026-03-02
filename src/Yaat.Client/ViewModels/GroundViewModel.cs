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
        await _sendCommand(callsign, "CT", initials);
    }

    public async Task PushbackAsync(
        string callsign, string initials)
    {
        await _sendCommand(callsign, "PB", initials);
    }

    public async Task CrossRunwayAsync(
        string callsign, string initials, string runwayId)
    {
        await _sendCommand(callsign, $"CR {runwayId}", initials);
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

    public async Task DeleteAsync(string callsign, string initials)
    {
        await _sendCommand(callsign, "DEL", initials);
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
