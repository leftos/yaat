using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Ground;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ViewModels;

public partial class GroundViewModel : ObservableObject
{
    private readonly ILogger _log = AppLog.CreateLogger<GroundViewModel>();

    private readonly ServerConnection _connection;
    private readonly Func<string, string, string, Task> _sendCommand;
    private readonly Action<AircraftModel?>? _onSelectionChanged;
    private AirportGroundLayout? _domainLayout;
    private Func<string, double?>? _getAirportElevation;

    public UserPreferences? Preferences { get; }

    [ObservableProperty]
    private GroundLayoutDto? _layout;

    [ObservableProperty]
    private AircraftModel? _selectedAircraft;

    [ObservableProperty]
    private WeatherDisplayInfo? _weatherInfo;

    [ObservableProperty]
    private TaxiRoute? _activeRoute;

    [ObservableProperty]
    private TaxiRoute? _previewRoute;

    [ObservableProperty]
    private bool _isDrawingRoute;

    [ObservableProperty]
    private TaxiRoute? _drawnRoutePreview;

    [ObservableProperty]
    private IReadOnlyList<int>? _drawWaypoints;

    [ObservableProperty]
    private TaxiRoute? _drawHoverPreview;

    [ObservableProperty]
    private double _airportCenterLat;

    [ObservableProperty]
    private double _airportCenterLon;

    [ObservableProperty]
    private double _airportElevation;

    [ObservableProperty]
    private bool _showRunwayLabels = true;

    [ObservableProperty]
    private bool _showTaxiwayLabels = true;

    [ObservableProperty]
    private GroundFilterMode _showHoldShort = GroundFilterMode.LabelsAndIcons;

    [ObservableProperty]
    private GroundFilterMode _showParking = GroundFilterMode.LabelsAndIcons;

    [ObservableProperty]
    private GroundFilterMode _showSpot = GroundFilterMode.LabelsAndIcons;

    [ObservableProperty]
    private bool _isPanZoomLocked;

    [ObservableProperty]
    private double _viewCenterLat;

    [ObservableProperty]
    private double _viewCenterLon;

    [ObservableProperty]
    private double _viewZoom = 1.0;

    [ObservableProperty]
    private double _viewRotation;

    private static readonly SKColor[] TaxiRouteColors =
    [
        SKColor.Parse("#FF6B6B"),
        SKColor.Parse("#4ECDC4"),
        SKColor.Parse("#FFE66D"),
        SKColor.Parse("#A8E6CF"),
        SKColor.Parse("#FF8B94"),
        SKColor.Parse("#B088F9"),
        SKColor.Parse("#F8B500"),
        SKColor.Parse("#45B7D1"),
    ];

    private readonly HashSet<string> _shownTaxiRouteCallsigns = new();
    private readonly Dictionary<string, int> _taxiColorIndices = new();
    private readonly Stack<int> _freeColorIndices = new();

    [ObservableProperty]
    private IReadOnlyList<ShownTaxiRouteEntry>? _shownTaxiRoutes;

    private Func<string, AircraftModel?>? _findAircraft;

    private string? _activeScenarioId;
    private bool _isRestoring;

    private AircraftModel? _drawAircraft;
    private List<int> _drawWaypointIds = [];
    private List<TaxiRoute> _drawSubRoutes = [];

    public ObservableCollection<AircraftModel> GroundAircraft { get; } = [];

    public GroundViewModel(
        ServerConnection connection,
        Func<string, string, string, Task> sendCommand,
        Action<AircraftModel?>? onSelectionChanged = null,
        UserPreferences? preferences = null
    )
    {
        _connection = connection;
        _sendCommand = sendCommand;
        _onSelectionChanged = onSelectionChanged;
        Preferences = preferences;

        if (preferences is not null)
        {
            ShowRunwayLabels = preferences.GroundShowRunwayLabels;
            ShowTaxiwayLabels = preferences.GroundShowTaxiwayLabels;
            ShowHoldShort = preferences.GroundShowHoldShort;
            ShowParking = preferences.GroundShowParking;
            ShowSpot = preferences.GroundShowSpot;
            IsPanZoomLocked = preferences.GroundPanZoomLocked;
        }
    }

    public void SetElevationLookup(Func<string, double?> lookup)
    {
        _getAirportElevation = lookup;
    }

    partial void OnSelectedAircraftChanged(AircraftModel? value)
    {
        _onSelectionChanged?.Invoke(value);

        if (IsDrawingRoute && value != _drawAircraft)
        {
            CancelDrawRoute();
        }
    }

    partial void OnViewCenterLatChanged(double value) => SaveSettings();

    partial void OnViewCenterLonChanged(double value) => SaveSettings();

    partial void OnViewZoomChanged(double value) => SaveSettings();

    partial void OnViewRotationChanged(double value) => SaveSettings();

    public void SaveLabelAndLockSettings()
    {
        SaveSettings();
        Preferences?.SetGroundLabelFilters(ShowRunwayLabels, ShowTaxiwayLabels, ShowHoldShort, ShowParking, ShowSpot);
        Preferences?.SetGroundPanZoomLocked(IsPanZoomLocked);
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

            // Compute airport center from node centroid
            if (dto.Nodes.Count > 0)
            {
                double sumLat = 0,
                    sumLon = 0;
                foreach (var node in dto.Nodes)
                {
                    sumLat += node.Latitude;
                    sumLon += node.Longitude;
                }

                AirportCenterLat = sumLat / dto.Nodes.Count;
                AirportCenterLon = sumLon / dto.Nodes.Count;
            }

            AirportElevation = _getAirportElevation?.Invoke(airportId) ?? 0;

            _log.LogInformation("Ground layout loaded for {Id}: {Nodes} nodes, {Edges} edges", airportId, dto.Nodes.Count, dto.Edges.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load ground layout for {Id}", airportId);
        }
    }

    public void SetScenarioId(string? scenarioId)
    {
        _activeScenarioId = scenarioId;
        if (scenarioId is not null)
        {
            RestoreSettings();
        }
    }

    public void ClearLayout()
    {
        _activeScenarioId = null;
        Layout = null;
        _domainLayout = null;
        ActiveRoute = null;
        PreviewRoute = null;
        AirportCenterLat = 0;
        AirportCenterLon = 0;
        AirportElevation = 0;
        GroundAircraft.Clear();
        ClearShownTaxiRoutes();
    }

    public void CopySettingsFrom(string sourceScenarioId)
    {
        if (Preferences is null || _activeScenarioId is null)
        {
            return;
        }

        var saved = Preferences.GetGroundSettings(sourceScenarioId);
        if (saved is null)
        {
            return;
        }

        ApplySettings(saved);
        SaveSettings();
    }

    private void ApplySettings(SavedGroundSettings saved)
    {
        _isRestoring = true;

        ViewCenterLat = saved.CenterLat;
        ViewCenterLon = saved.CenterLon;
        ViewZoom = saved.Zoom;
        ViewRotation = saved.Rotation;
        IsPanZoomLocked = saved.IsPanZoomLocked;
        ShowRunwayLabels = saved.ShowRunwayLabels;
        ShowTaxiwayLabels = saved.ShowTaxiwayLabels;
        ShowHoldShort = saved.ShowHoldShort;
        ShowParking = saved.ShowParking;
        ShowSpot = saved.ShowSpot;

        _isRestoring = false;
    }

    private void SaveSettings()
    {
        if (Preferences is null || _activeScenarioId is null || _isRestoring)
        {
            return;
        }

        if (ViewCenterLat == 0 && ViewCenterLon == 0)
        {
            return;
        }

        var settings = new SavedGroundSettings
        {
            CenterLat = ViewCenterLat,
            CenterLon = ViewCenterLon,
            Zoom = ViewZoom,
            Rotation = ViewRotation,
            IsPanZoomLocked = IsPanZoomLocked,
            ShowRunwayLabels = ShowRunwayLabels,
            ShowTaxiwayLabels = ShowTaxiwayLabels,
            ShowHoldShort = ShowHoldShort,
            ShowParking = ShowParking,
            ShowSpot = ShowSpot,
        };

        Preferences.SetGroundSettings(_activeScenarioId, settings);
    }

    private void RestoreSettings()
    {
        if (Preferences is null || _activeScenarioId is null)
        {
            return;
        }

        var saved = Preferences.GetGroundSettings(_activeScenarioId);
        if (saved is null)
        {
            return;
        }

        ApplySettings(saved);
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
            if (name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "RAMP", StringComparison.OrdinalIgnoreCase))
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

    public List<string> GetNodeTaxiwayNames(int nodeId)
    {
        if (_domainLayout is null || !_domainLayout.Nodes.TryGetValue(nodeId, out var node))
        {
            return [];
        }

        var names = new List<string>();
        foreach (var edge in node.Edges)
        {
            if (
                !edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(edge.TaxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase)
                && !names.Contains(edge.TaxiwayName)
            )
            {
                names.Add(edge.TaxiwayName);
            }
        }

        return names;
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

    public async Task ClearedForTakeoffModifierAsync(string callsign, string initials, string modifier)
    {
        await _sendCommand(callsign, $"CTO {modifier}", initials);
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
        await _sendCommand(callsign, "CLAND", initials);
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

    public async Task WarpToNodeAsync(string callsign, string initials, int nodeId)
    {
        await _sendCommand(callsign, $"WARPG #{nodeId}", initials);
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
    public List<(string Label, string Command, TaxiRoute Preview)> BuildTaxiCrossingVariants(
        TaxiRoute route,
        string? spotName = null,
        string? pathOverride = null
    )
    {
        var taxiways = pathOverride ?? BuildTaxiCommand(route);
        var spotSuffix = spotName is not null ? $" @{spotName}" : "";

        if (string.IsNullOrEmpty(taxiways))
        {
            // No taxiway path but have a spot destination — route via @SPOT only
            if (spotName is not null)
            {
                return [("", $"TAXI @{spotName}", route)];
            }

            return [];
        }

        var crossingHoldShorts = new List<(string RwyName, HoldShortPoint Hs)>();
        foreach (var hs in route.HoldShortPoints)
        {
            if (hs.Reason == HoldShortReason.RunwayCrossing && hs.TargetName is not null)
            {
                crossingHoldShorts.Add((RunwayIdentifier.Parse(hs.TargetName).End1, hs));
            }
        }

        if (crossingHoldShorts.Count == 0)
        {
            return [("", $"TAXI {taxiways}{spotSuffix}", route)];
        }

        var results = new List<(string Label, string Command, TaxiRoute Preview)>();

        // Variation 0: hold short of first crossing
        var firstHs = crossingHoldShorts[0];
        results.Add(($"HS {firstHs.RwyName}", $"TAXI {taxiways} HS {firstHs.RwyName}{spotSuffix}", route.TruncateAt(firstHs.Hs.NodeId)));

        // Variations 1..N-1: cross some, hold short of next
        for (int i = 0; i < crossingHoldShorts.Count - 1; i++)
        {
            var crossParts = crossingHoldShorts.Take(i + 1).Select(c => $"CROSS {c.RwyName}");
            var holdEntry = crossingHoldShorts[i + 1];
            var label = $"CROSS {string.Join(" ", crossingHoldShorts.Take(i + 1).Select(c => c.RwyName))} HS {holdEntry.RwyName}";
            var cmd = $"TAXI {taxiways} HS {holdEntry.RwyName}{spotSuffix}, {string.Join(", ", crossParts)}";
            results.Add((label, cmd, route.TruncateAt(holdEntry.Hs.NodeId)));
        }

        // Variation N: cross all
        var allCrossParts = crossingHoldShorts.Select(c => $"CROSS {c.RwyName}");
        results.Add(
            (
                $"CROSS {string.Join(" ", crossingHoldShorts.Select(c => c.RwyName))}",
                $"TAXI {taxiways}{spotSuffix}, {string.Join(", ", allCrossParts)}",
                route
            )
        );

        return results;
    }

    /// <summary>
    /// Returns all taxi variants for a route ending at a runway hold-short.
    /// Two groups separated by a null entry: RWY variants (with runway assignment),
    /// then non-RWY variants (without). Each group has N+1 entries for N crossings.
    /// Example with crossings [28R, 28L] and dest 30:
    ///   RWY 30 HS 28R  |  RWY 30 CROSS 28R HS 28L  |  RWY 30 CROSS 28R 28L
    ///   (separator)
    ///   HS 28R  |  CROSS 28R HS 28L  |  CROSS 28R 28L HS 30
    /// </summary>
    public List<(string Label, string Command, TaxiRoute Preview)?> BuildTaxiDestVariants(TaxiRoute route, string destRunway, string? spotName = null)
    {
        var taxiways = BuildTaxiCommand(route);
        var spotSuffix = spotName is not null ? $" @{spotName}" : "";

        if (string.IsNullOrEmpty(taxiways))
        {
            return [];
        }

        // Find the destination hold-short node (last segment's ToNodeId is the dest)
        var destHsNodeId = route.Segments.Count > 0 ? route.Segments[^1].ToNodeId : -1;

        var crossingHoldShorts = new List<(string RwyName, HoldShortPoint Hs)>();
        foreach (var hs in route.HoldShortPoints)
        {
            if (hs.Reason == HoldShortReason.RunwayCrossing && hs.TargetName is not null)
            {
                var rwyName = RunwayIdentifier.Parse(hs.TargetName).End1;
                if (!string.Equals(rwyName, destRunway, StringComparison.OrdinalIgnoreCase))
                {
                    crossingHoldShorts.Add((rwyName, hs));
                }
            }
        }

        var results = new List<(string Label, string Command, TaxiRoute Preview)?>();

        if (crossingHoldShorts.Count == 0)
        {
            results.Add(($"RWY {destRunway}", $"TAXI {taxiways} {destRunway}{spotSuffix}", route));
            results.Add(null); // separator
            results.Add(($"HS {destRunway}", $"TAXI {taxiways} HS {destRunway}{spotSuffix}", route.TruncateAt(destHsNodeId)));
            return results;
        }

        // --- RWY variants: progressive crossings with runway assignment ---

        // Hold short of first crossing
        var firstHs = crossingHoldShorts[0];
        results.Add(
            (
                $"RWY {destRunway} HS {firstHs.RwyName}",
                $"TAXI {taxiways} HS {firstHs.RwyName} RWY {destRunway}{spotSuffix}",
                route.TruncateAt(firstHs.Hs.NodeId)
            )
        );

        // Cross some, hold short of next
        for (int i = 0; i < crossingHoldShorts.Count - 1; i++)
        {
            var crossParts = crossingHoldShorts.Take(i + 1).Select(c => $"CROSS {c.RwyName}");
            var holdEntry = crossingHoldShorts[i + 1];
            var label = $"RWY {destRunway} CROSS {string.Join(" ", crossingHoldShorts.Take(i + 1).Select(c => c.RwyName))} HS {holdEntry.RwyName}";
            var cmd = $"TAXI {taxiways} HS {holdEntry.RwyName} RWY {destRunway}{spotSuffix}, {string.Join(", ", crossParts)}";
            results.Add((label, cmd, route.TruncateAt(holdEntry.Hs.NodeId)));
        }

        // Cross all, arrive at destination with RWY assignment
        var allCross = crossingHoldShorts.Select(c => $"CROSS {c.RwyName}");
        results.Add(
            (
                $"RWY {destRunway} CROSS {string.Join(" ", crossingHoldShorts.Select(c => c.RwyName))}",
                $"TAXI {taxiways} {destRunway}{spotSuffix}, {string.Join(", ", allCross)}",
                route
            )
        );

        results.Add(null); // separator

        // --- Non-RWY variants: progressive crossings without runway assignment ---

        // Hold short of first crossing
        results.Add(($"HS {firstHs.RwyName}", $"TAXI {taxiways} HS {firstHs.RwyName}{spotSuffix}", route.TruncateAt(firstHs.Hs.NodeId)));

        // Cross some, hold short of next
        for (int i = 0; i < crossingHoldShorts.Count - 1; i++)
        {
            var crossParts = crossingHoldShorts.Take(i + 1).Select(c => $"CROSS {c.RwyName}");
            var holdEntry = crossingHoldShorts[i + 1];
            var label = $"CROSS {string.Join(" ", crossingHoldShorts.Take(i + 1).Select(c => c.RwyName))} HS {holdEntry.RwyName}";
            var cmd = $"TAXI {taxiways} HS {holdEntry.RwyName}{spotSuffix}, {string.Join(", ", crossParts)}";
            results.Add((label, cmd, route.TruncateAt(holdEntry.Hs.NodeId)));
        }

        // Cross all, hold short at destination (no RWY assignment)
        results.Add(
            (
                $"CROSS {string.Join(" ", crossingHoldShorts.Select(c => c.RwyName))} HS {destRunway}",
                $"TAXI {taxiways} HS {destRunway}{spotSuffix}, {string.Join(", ", allCross)}",
                route.TruncateAt(destHsNodeId)
            )
        );

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

    // --- Shown taxi routes ---

    public void SetAircraftLookup(Func<string, AircraftModel?> lookup)
    {
        _findAircraft = lookup;
    }

    public bool IsPathShown(string callsign)
    {
        return _shownTaxiRouteCallsigns.Contains(callsign);
    }

    public void ToggleShowTaxiRoute(string callsign)
    {
        if (_shownTaxiRouteCallsigns.Remove(callsign))
        {
            if (_taxiColorIndices.Remove(callsign, out var freedIdx))
            {
                _freeColorIndices.Push(freedIdx);
            }
        }
        else
        {
            _shownTaxiRouteCallsigns.Add(callsign);
            int colorIdx = _freeColorIndices.Count > 0 ? _freeColorIndices.Pop() : _taxiColorIndices.Count % TaxiRouteColors.Length;
            _taxiColorIndices[callsign] = colorIdx;
        }

        RefreshShownTaxiRoutes();
    }

    public void RefreshShownTaxiRoutes()
    {
        if (_shownTaxiRouteCallsigns.Count == 0)
        {
            ShownTaxiRoutes = null;
            return;
        }

        var entries = new List<ShownTaxiRouteEntry>();
        foreach (var callsign in _shownTaxiRouteCallsigns)
        {
            var ac = _findAircraft?.Invoke(callsign);
            if (ac is null)
            {
                continue;
            }

            var route = ResolveRemainingRoute(ac);
            if (route is null || route.Segments.Count == 0)
            {
                continue;
            }

            var colorIdx = _taxiColorIndices.GetValueOrDefault(callsign, 0);
            entries.Add(new ShownTaxiRouteEntry(callsign, route, TaxiRouteColors[colorIdx % TaxiRouteColors.Length]));
        }

        ShownTaxiRoutes = entries.Count > 0 ? entries : null;
    }

    public void RemoveShownTaxiRoute(string callsign)
    {
        if (_shownTaxiRouteCallsigns.Remove(callsign))
        {
            if (_taxiColorIndices.Remove(callsign, out var freedIdx))
            {
                _freeColorIndices.Push(freedIdx);
            }

            RefreshShownTaxiRoutes();
        }
    }

    public void ClearShownTaxiRoutes()
    {
        _shownTaxiRouteCallsigns.Clear();
        _taxiColorIndices.Clear();
        _freeColorIndices.Clear();
        ShownTaxiRoutes = null;
    }

    // --- Draw route mode ---

    public void StartDrawRoute(AircraftModel aircraft)
    {
        var startNode = GetAircraftNearestNodeId(aircraft);
        if (startNode is null)
        {
            return;
        }

        _drawAircraft = aircraft;
        _drawWaypointIds = [startNode.Value];
        _drawSubRoutes = [];
        DrawnRoutePreview = null;
        DrawWaypoints = [startNode.Value];
        IsDrawingRoute = true;
    }

    public bool AddDrawWaypoint(int nodeId)
    {
        if (_drawWaypointIds.Count == 0 || nodeId == _drawWaypointIds[^1])
        {
            return false;
        }

        var subRoute = FindRouteToNode(_drawWaypointIds[^1], nodeId);
        if (subRoute is null)
        {
            return false;
        }

        _drawSubRoutes.Add(subRoute);
        _drawWaypointIds.Add(nodeId);
        DrawWaypoints = [.. _drawWaypointIds];
        DrawnRoutePreview = MergeSubRoutes();
        DrawHoverPreview = null;
        return true;
    }

    public void UndoDrawWaypoint()
    {
        if (_drawWaypointIds.Count <= 1)
        {
            return;
        }

        _drawWaypointIds.RemoveAt(_drawWaypointIds.Count - 1);
        _drawSubRoutes.RemoveAt(_drawSubRoutes.Count - 1);
        DrawWaypoints = [.. _drawWaypointIds];
        DrawnRoutePreview = _drawSubRoutes.Count > 0 ? MergeSubRoutes() : null;
    }

    public (TaxiRoute Route, string Command, string NodeRefPath)? FinishDrawRoute()
    {
        if (_drawSubRoutes.Count == 0)
        {
            CancelDrawRoute();
            return null;
        }

        var merged = MergeSubRoutes();
        // Skip index 0 (aircraft's starting node)
        var nodeRefs = _drawWaypointIds.Skip(1).Select(id => $"!{id}");
        var nodeRefPath = string.Join(" ", nodeRefs);
        ClearDrawState();

        if (string.IsNullOrEmpty(nodeRefPath))
        {
            return null;
        }

        return (merged, $"TAXI {nodeRefPath}", nodeRefPath);
    }

    public void UpdateDrawHoverPreview(int? nodeId)
    {
        if (!IsDrawingRoute || _drawWaypointIds.Count == 0 || nodeId is null || nodeId == _drawWaypointIds[^1])
        {
            DrawHoverPreview = null;
            return;
        }

        DrawHoverPreview = FindRouteToNode(_drawWaypointIds[^1], nodeId.Value);
    }

    public void CancelDrawRoute()
    {
        ClearDrawState();
    }

    private void ClearDrawState()
    {
        _drawAircraft = null;
        _drawWaypointIds = [];
        _drawSubRoutes = [];
        IsDrawingRoute = false;
        DrawnRoutePreview = null;
        DrawHoverPreview = null;
        DrawWaypoints = null;
    }

    private TaxiRoute MergeSubRoutes()
    {
        var segments = new List<TaxiRouteSegment>();
        var holdShorts = new List<HoldShortPoint>();
        var seenHoldShortNodes = new HashSet<int>();

        foreach (var sub in _drawSubRoutes)
        {
            segments.AddRange(sub.Segments);
            foreach (var hs in sub.HoldShortPoints)
            {
                if (seenHoldShortNodes.Add(hs.NodeId))
                {
                    holdShorts.Add(hs);
                }
            }
        }

        return new TaxiRoute { Segments = segments, HoldShortPoints = holdShorts };
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

public record ShownTaxiRouteEntry(string Callsign, TaxiRoute Route, SKColor Color);
