using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// P4.1/P4.2: E2E tests using real airport GeoJSON layouts.
/// Tests go through GroundCommandHandler with real OAK/SFO data.
/// Silently skip if yaat-server ArtccResources are not available.
/// </summary>
public class AirportE2ETests
{
    private static readonly ILogger Logger = new NullLogger<AirportE2ETests>();
    private const string TestDataDir = "TestData";

    private static AirportGroundLayout? LoadLayout(string airportId, string subdir)
    {
        string path = Path.Combine(TestDataDir, $"{subdir}.geojson");
        if (File.Exists(path))
        {
            return GeoJsonParser.Parse(airportId, File.ReadAllText(path));
        }

        return null;
    }

    private static AircraftState MakeGroundAircraft(string departure = "OAK", double lat = 37.728, double lon = -122.218)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = lat,
            Longitude = lon,
            Heading = 280,
            Altitude = 6,
            GroundSpeed = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            Departure = departure,
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(new AtParkingPhase());
        ac.Phases.Start(MinCtx(ac));
        return ac;
    }

    private static PhaseContext MinCtx(AircraftState ac) => CommandDispatcher.BuildMinimalContext(ac, NullLogger.Instance);

    private static GroundNode? FindParking(AirportGroundLayout layout, string name) =>
        layout.Nodes.Values.FirstOrDefault(n =>
            (n.Type == GroundNodeType.Parking || n.Type == GroundNodeType.Spot) && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)
        );

    // -------------------------------------------------------------------------
    // P4.1: OAK E2E — real routes verified against oak.geojson graph
    //
    // OAK taxiway connectivity (verified):
    //   D connects to: C, G, H, J, K
    //   C connects to: A, B, C1, D, E, F, G, H, J
    //   B connects to: A, B1-B5, C, R, S, T, V, W
    //   W connects to: B, U, V, W1-W7
    //   K connects to: D, F, J, L
    //   F connects to: C, K, L
    //
    // NEW7 parking (lat 37.740, lon -122.221) connects via RAMP to D.
    // D crosses runway 15/33 (lat range 37.730-37.740).
    // Route to runway 30: D → C → B → W (W serves the 30/12 threshold area).
    // -------------------------------------------------------------------------

    [Fact]
    public void OAK_TaxiFromParking_D_Succeeds()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var ac = MakeGroundAircraft(lat: parking.Latitude, lon: parking.Longitude);

        // TAXI D — walks south along D from NEW7 parking (via RAMP edge)
        var taxi = new TaxiCommand(["D"], []);
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);

        Assert.True(result.Success, $"Taxi should succeed: {result.Message}");
        Assert.NotNull(ac.AssignedTaxiRoute);

        // Route should start with RAMP (parking→taxiway) then have D segments
        Assert.Contains(ac.AssignedTaxiRoute!.Segments, s => string.Equals(s.TaxiwayName, "D", StringComparison.OrdinalIgnoreCase));
        Assert.IsType<TaxiingPhase>(ac.Phases!.CurrentPhase);
    }

    [Fact]
    public void OAK_TaxiFromParking_DC_ReachesC()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var ac = MakeGroundAircraft(lat: parking.Latitude, lon: parking.Longitude);

        // TAXI D C — D south to C junction
        var taxi = new TaxiCommand(["D", "C"], []);
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);

        Assert.True(result.Success, $"Taxi should succeed: {result.Message}");

        Assert.Contains(ac.AssignedTaxiRoute!.Segments, s => string.Equals(s.TaxiwayName, "D", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ac.AssignedTaxiRoute.Segments, s => string.Equals(s.TaxiwayName, "C", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OAK_TaxiFromParking_DCBW_ToRunway30_HasHoldShortAndPhases()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var ac = MakeGroundAircraft(lat: parking.Latitude, lon: parking.Longitude);

        // TAXI D C B W to runway 30 — full route from NEW7 to runway 30
        var taxi = new TaxiCommand(["D", "C", "B", "W"], [], DestinationRunway: "30");
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);

        Assert.True(result.Success, $"Taxi should succeed: {result.Message}");

        // Should now be in TaxiingPhase
        Assert.IsType<TaxiingPhase>(ac.Phases!.CurrentPhase);

        // Route should have D, C, B, and W segments
        Assert.Contains(ac.AssignedTaxiRoute!.Segments, s => string.Equals(s.TaxiwayName, "D", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ac.AssignedTaxiRoute.Segments, s => string.Equals(s.TaxiwayName, "B", StringComparison.OrdinalIgnoreCase));

        // Should end with a hold-short for runway 30 (destination)
        var destHs = ac.AssignedTaxiRoute.HoldShortPoints.Where(h => h.Reason == HoldShortReason.DestinationRunway).ToList();
        Assert.True(destHs.Count > 0, "Should have destination runway hold-short");
    }

    [Fact]
    public void OAK_TaxiDCBTUW_HasHoldShortsForBothRunways()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var ac = MakeGroundAircraft(lat: parking.Latitude, lon: parking.Longitude);

        var taxi = new TaxiCommand(["D", "C", "B", "T", "U", "W"], []);
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);
        Assert.True(result.Success, $"Taxi should succeed: {result.Message}");

        var route = ac.AssignedTaxiRoute!;

        var allHs = route.HoldShortPoints;
        var hsInfo = string.Join("; ", allHs.Select(h => $"node={h.NodeId} target={h.TargetName} reason={h.Reason}"));

        // Also dump all RunwayHoldShort nodes encountered in the route
        var hsNodesInRoute = route
            .Segments.Where(s => layout.Nodes.TryGetValue(s.ToNodeId, out var n) && n.Type == GroundNodeType.RunwayHoldShort)
            .Select(s =>
            {
                var n = layout.Nodes[s.ToNodeId];
                return $"seg→{s.ToNodeId}(rwy={n.RunwayId},lat={n.Latitude:F6},lon={n.Longitude:F6})";
            })
            .ToList();
        var hsNodeInfo = string.Join("; ", hsNodesInRoute);

        // Route crosses both 28R/10L and 28L/10R on B taxiway
        var has28R = allHs.Any(h =>
            h.Reason == HoldShortReason.RunwayCrossing && h.TargetName is not null && RunwayIdentifier.Parse(h.TargetName).Contains("28R")
        );
        var has28L = allHs.Any(h =>
            h.Reason == HoldShortReason.RunwayCrossing && h.TargetName is not null && RunwayIdentifier.Parse(h.TargetName).Contains("28L")
        );

        Assert.True(has28R, $"Route should have hold-short for 28R. HS: [{hsInfo}]. HS nodes in route: [{hsNodeInfo}]");
        Assert.True(has28L, $"Route should have hold-short for 28L. HS: [{hsInfo}]. HS nodes in route: [{hsNodeInfo}]");
    }

    [Theory]
    [InlineData(150)] // Default — same as test without runway lookup
    [InlineData(75)] // Narrow runways (OAK 12/30, 15/33)
    [InlineData(200)] // Wide
    public void OAK_TaxiDCBTUW_HasHoldShortsForBothRunways_WithRunwayWidth(int widthFt)
    {
        var layout = LoadLayoutWithWidth("OAK", "oak", widthFt);
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var ac = MakeGroundAircraft(lat: parking.Latitude, lon: parking.Longitude);

        var taxi = new TaxiCommand(["D", "C", "B", "T", "U", "W"], []);
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);
        Assert.True(result.Success, $"Taxi should succeed (width={widthFt}): {result.Message}");

        var route = ac.AssignedTaxiRoute!;
        var allHs = route.HoldShortPoints;
        var hsInfo = string.Join("; ", allHs.Select(h => $"node={h.NodeId} target={h.TargetName} reason={h.Reason}"));

        var has28R = allHs.Any(h =>
            h.Reason == HoldShortReason.RunwayCrossing && h.TargetName is not null && RunwayIdentifier.Parse(h.TargetName).Contains("28R")
        );
        var has28L = allHs.Any(h =>
            h.Reason == HoldShortReason.RunwayCrossing && h.TargetName is not null && RunwayIdentifier.Parse(h.TargetName).Contains("28L")
        );

        Assert.True(has28R, $"Route should have hold-short for 28R (width={widthFt}). HS: [{hsInfo}]");
        Assert.True(has28L, $"Route should have hold-short for 28L (width={widthFt}). HS: [{hsInfo}]");
    }

    private static AirportGroundLayout? LoadLayoutWithWidth(string airportId, string subdir, int widthFt)
    {
        string path = Path.Combine(TestDataDir, $"{subdir}.geojson");
        if (!File.Exists(path))
        {
            return null;
        }

        var runwayLookup = new AllWidthRunwayLookup(widthFt);
        return GeoJsonParser.Parse(airportId, File.ReadAllText(path), runwayLookup: runwayLookup, runwayAirportCode: airportId);
    }

    private class AllWidthRunwayLookup(int widthFt) : IRunwayLookup
    {
        public RunwayInfo? GetRunway(string airportCode, string runwayId) =>
            new RunwayInfo
            {
                AirportId = airportCode,
                Id = RunwayIdentifier.Parse(runwayId),
                Designator = RunwayIdentifier.Parse(runwayId).End1,
                Lat1 = 0,
                Lon1 = 0,
                Elevation1Ft = 0,
                Heading1 = 0,
                Lat2 = 0,
                Lon2 = 0,
                Elevation2Ft = 0,
                Heading2 = 0,
                LengthFt = 0,
                WidthFt = widthFt,
            };

        public IReadOnlyList<RunwayInfo> GetRunways(string airportCode) => [];
    }

    [Fact]
    public void OAK_PushbackFromParking_FacingD_Succeeds()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var ac = MakeGroundAircraft(lat: parking.Latitude, lon: parking.Longitude);

        var push = new PushbackCommand(FacingTaxiway: "D");
        var result = GroundCommandHandler.TryPushback(ac, push, layout, Logger);

        Assert.True(result.Success, $"Pushback should succeed: {result.Message}");
        Assert.IsType<PushbackPhase>(ac.Phases!.CurrentPhase);
    }

    [Fact]
    public void OAK_TaxiDF_MultipleHoldShorts_CrossesRunway15_33()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // Start from a D node north of the 15/33 crossing (lat > 37.735)
        var dEdges = layout.Edges.Where(e => string.Equals(e.TaxiwayName, "D", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(dEdges.Count > 0);

        GroundNode? startNode = null;
        foreach (var edge in dEdges)
        {
            var node = layout.Nodes[edge.FromNodeId];
            if (node.Latitude > 37.735)
            {
                startNode = node;
                break;
            }
        }

        if (startNode is null)
        {
            return;
        }

        var ac = MakeGroundAircraft(lat: startNode.Latitude, lon: startNode.Longitude);

        // D → K → F: D connects to K, K connects to F. Both D and F cross 15/33.
        var taxi = new TaxiCommand(["D", "K", "F"], []);
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);

        Assert.True(result.Success, $"Taxi D K F should succeed: {result.Message}");

        // Should have hold-short(s) for runway 15/33 crossing
        var hsRwy = ac.AssignedTaxiRoute!.HoldShortPoints.Where(h => h.Reason == HoldShortReason.RunwayCrossing).ToList();
        Assert.True(hsRwy.Count > 0, "Should have runway crossing hold-short(s)");
    }

    [Fact]
    public void OAK_TaxiDKF_AutoCrossRunway_ClearsHoldShorts()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var dEdges = layout.Edges.Where(e => string.Equals(e.TaxiwayName, "D", StringComparison.OrdinalIgnoreCase)).ToList();

        GroundNode? startNode = null;
        foreach (var edge in dEdges)
        {
            var node = layout.Nodes[edge.FromNodeId];
            if (node.Latitude > 37.735)
            {
                startNode = node;
                break;
            }
        }

        if (startNode is null)
        {
            return;
        }

        var ac = MakeGroundAircraft(lat: startNode.Latitude, lon: startNode.Longitude);

        // Taxi with auto-cross-runway flag
        var taxi = new TaxiCommand(["D", "K", "F"], []);
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger, autoCrossRunway: true);

        Assert.True(result.Success, $"Taxi D K F (auto-cross) should succeed: {result.Message}");

        // All runway crossing hold-shorts should be pre-cleared
        var unclearedCrossings = ac.AssignedTaxiRoute!.HoldShortPoints.Where(h => h.Reason == HoldShortReason.RunwayCrossing && !h.IsCleared)
            .ToList();
        Assert.Empty(unclearedCrossings);
    }

    [Fact]
    public void OAK_FullTaxiToTakeoff_DCBW_HoldShort30_HasPhases()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var ac = MakeGroundAircraft(lat: parking.Latitude, lon: parking.Longitude);

        // Step 1: Taxi from parking to runway 30 via D C B W
        var taxi = new TaxiCommand(["D", "C", "B", "W"], [], DestinationRunway: "30");
        var taxiResult = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);
        Assert.True(taxiResult.Success, $"Taxi failed: {taxiResult.Message}");

        // Verify we're in TaxiingPhase
        Assert.IsType<TaxiingPhase>(ac.Phases!.CurrentPhase);

        // Verify route has destination hold-short for runway 30
        var destHs = ac.AssignedTaxiRoute!.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.DestinationRunway);
        Assert.NotNull(destHs);

        // The destination hold-short target should reference runway 30
        Assert.NotNull(destHs.TargetName);
        Assert.True(
            RunwayIdentifier.Parse(destHs.TargetName!).Contains("30"),
            $"Hold-short target should be for runway 30, got: {destHs.TargetName}"
        );

        // HoldingShortPhase is inserted by TaxiingPhase at runtime when the
        // aircraft reaches the hold-short position — it's not in the phase list yet.
    }

    [Fact]
    public void OAK_PushbackThenTaxi_NEW7_PushD_TaxiDC()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var ac = MakeGroundAircraft(lat: parking.Latitude, lon: parking.Longitude);

        // Step 1: Pushback facing D
        var push = new PushbackCommand(FacingTaxiway: "D");
        var pushResult = GroundCommandHandler.TryPushback(ac, push, layout, Logger);
        Assert.True(pushResult.Success, $"Pushback failed: {pushResult.Message}");
        Assert.IsType<PushbackPhase>(ac.Phases!.CurrentPhase);

        // Step 2: Complete pushback by ticking until done
        for (int i = 0; i < 200; i++)
        {
            var ctx = MinCtx(ac);
            FlightPhysics.Update(ac, ctx.DeltaSeconds);
            PhaseRunner.Tick(ac, ctx);

            if (ac.Phases.CurrentPhase is not PushbackPhase)
            {
                break;
            }
        }

        // After pushback, issue taxi via D C
        var taxi = new TaxiCommand(["D", "C"], []);
        var taxiResult = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);
        Assert.True(taxiResult.Success, $"Taxi after pushback failed: {taxiResult.Message}");
        Assert.IsType<TaxiingPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void OAK_TaxiD_NeedsVariantForRunway30()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var ac = MakeGroundAircraft(lat: parking.Latitude, lon: parking.Longitude);

        // TAXI D to runway 30 — D doesn't reach runway 30 (it's in the 28/15-33 area).
        // Should fail because D alone can't reach the 30 threshold.
        var taxi = new TaxiCommand(["D"], [], DestinationRunway: "30");
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);

        Assert.False(result.Success, "D alone should not reach runway 30");
    }

    [Fact]
    public void OAK_HoldShortNodes_NotAtJunctions()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var hsNodes = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort).ToList();

        Assert.True(hsNodes.Count > 0, "OAK should have hold-short nodes");

        var failures = new List<string>();
        foreach (var hs in hsNodes)
        {
            // A junction node connects to multiple distinct non-runway taxiways.
            // Hold-short nodes should NOT be at junctions — they should be on a
            // single taxiway between the runway surface and the next intersection.
            var taxiwayNames = hs
                .Edges.Where(e => !e.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.TaxiwayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (taxiwayNames.Count > 1)
            {
                failures.Add($"Node {hs.Id} (rwy={hs.RunwayId}): [{string.Join(", ", taxiwayNames)}]");
            }
        }

        Assert.True(failures.Count == 0, $"Hold-short nodes at junctions:\n{string.Join("\n", failures)}");
    }

    [Fact]
    public void OAK_NoDuplicateEdgesOnNodes()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        foreach (var (id, node) in layout.Nodes)
        {
            // Each edge should appear exactly once in the node's adjacency list
            var dupes = node.Edges.GroupBy(e => (e.FromNodeId, e.ToNodeId, e.TaxiwayName)).Where(g => g.Count() > 1).ToList();
            Assert.True(
                dupes.Count == 0,
                $"Node {id} has duplicate edges: {string.Join(", ", dupes.Select(g => $"{g.Key.TaxiwayName}({g.Key.FromNodeId}->{g.Key.ToNodeId}) x{g.Count()}"))}"
            );
        }
    }

    // -------------------------------------------------------------------------
    // Pathfinder route ranking: fewest taxiway transitions should rank first
    // -------------------------------------------------------------------------

    private static GroundNode? FindNodeOnTaxiway(AirportGroundLayout layout, string taxiwayName)
    {
        foreach (var node in layout.Nodes.Values)
        {
            if (node.Type != GroundNodeType.TaxiwayIntersection)
            {
                continue;
            }

            foreach (var edge in node.Edges)
            {
                if (string.Equals(edge.TaxiwayName, taxiwayName, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }
            }
        }

        return null;
    }

    private static GroundNode? FindHoldShortForRunway(AirportGroundLayout layout, string runway)
    {
        foreach (var node in layout.Nodes.Values)
        {
            if (node.Type == GroundNodeType.RunwayHoldShort && node.RunwayId is { } rwyId && rwyId.Contains(runway))
            {
                return node;
            }
        }

        return null;
    }

    private static List<string> GetTaxiwaySequence(TaxiRoute route)
    {
        var names = new List<string>();
        foreach (var seg in route.Segments)
        {
            if (
                seg.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
                || string.Equals(seg.TaxiwayName, "RAMP", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            if (names.Count == 0 || !string.Equals(names[^1], seg.TaxiwayName, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(seg.TaxiwayName.ToUpperInvariant());
            }
        }

        return names;
    }

    [Fact]
    public void OAK_FindRoutes_FromC_ToRunway30_PrefersCBW()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // Find a node on C that also connects to B (the C/B junction)
        GroundNode? startNode = null;
        foreach (var node in layout.Nodes.Values)
        {
            if (node.Type != GroundNodeType.TaxiwayIntersection)
            {
                continue;
            }

            bool hasC = false;
            bool hasB = false;
            foreach (var edge in node.Edges)
            {
                if (string.Equals(edge.TaxiwayName, "C", StringComparison.OrdinalIgnoreCase))
                {
                    hasC = true;
                }

                if (string.Equals(edge.TaxiwayName, "B", StringComparison.OrdinalIgnoreCase))
                {
                    hasB = true;
                }
            }

            if (hasC && hasB)
            {
                startNode = node;
                break;
            }
        }

        Assert.NotNull(startNode);

        var hsNode = FindHoldShortForRunway(layout, "30");
        Assert.NotNull(hsNode);

        var routes = TaxiPathfinder.FindRoutes(layout, startNode.Id, hsNode.Id);
        Assert.True(routes.Count > 0, "Should find at least one route from C/B junction to RWY 30");

        // The first (best) route should use B → W (with optional variant suffix
        // like W1 if the destination is a variant hold-short) — fewest taxiway transitions
        var bestSeq = GetTaxiwaySequence(routes[0]);
        Assert.True(
            bestSeq.Count <= 4 && bestSeq.Contains("B") && bestSeq.Any(n => n.StartsWith("W")),
            $"Best route should go via B → W (few taxiways), got: [{string.Join(", ", bestSeq)}]"
        );

        // Should NOT route through S → T → U (many intermediate taxiways)
        Assert.DoesNotContain("S", bestSeq);
        Assert.DoesNotContain("T", bestSeq);
        Assert.DoesNotContain("U", bestSeq);
    }

    [Fact]
    public void OAK_FindRoutes_RankedByTaxiwayTransitions()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        // From a C node to a runway 30 hold-short: verify routes are ranked
        // with fewer taxiway transitions first
        var startNode = FindNodeOnTaxiway(layout, "C");
        Assert.NotNull(startNode);

        var hsNode = FindHoldShortForRunway(layout, "30");
        Assert.NotNull(hsNode);

        var routes = TaxiPathfinder.FindRoutes(layout, startNode.Id, hsNode.Id);
        if (routes.Count < 2)
        {
            return; // Can't test ranking with only one route
        }

        // The first route (from penalized A*) should have the fewest taxiway
        // transitions. Alternatives from Yen's K-shortest are sorted by distance,
        // so they may have fewer or more taxiways.
        var firstSeq = GetTaxiwaySequence(routes[0]);
        int minTaxiways = routes.Min(r => GetTaxiwaySequence(r).Count);
        Assert.True(
            firstSeq.Count <= minTaxiways + 1,
            $"First route [{string.Join(", ", firstSeq)}] ({firstSeq.Count} taxiways) should be among the simplest; min across all routes is {minTaxiways}"
        );
    }

    [Fact]
    public void OAK_FindRoutes_FromParking_ToRunway30_FirstRouteIsReasonable()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            return;
        }

        var parking = FindParking(layout, "NEW7");
        Assert.NotNull(parking);

        var hsNode = FindHoldShortForRunway(layout, "30");
        Assert.NotNull(hsNode);

        var routes = TaxiPathfinder.FindRoutes(layout, parking.Id, hsNode.Id);
        Assert.True(routes.Count > 0, "Should find route from NEW7 to RWY 30");

        var bestSeq = GetTaxiwaySequence(routes[0]);

        // The canonical route is D C B W — should not be something convoluted
        // with 6+ taxiway transitions
        Assert.True(
            bestSeq.Count <= 5,
            $"Best route from NEW7 to RWY 30 should have ≤5 taxiways, got {bestSeq.Count}: [{string.Join(", ", bestSeq)}]"
        );
    }

    // -------------------------------------------------------------------------
    // P4.2: SFO E2E
    // -------------------------------------------------------------------------

    [Fact]
    public void SFO_LayoutLoads_HasMultipleRunwayHoldShorts()
    {
        var layout = LoadLayout("SFO", "sfo");
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.Count > 0);
        Assert.True(layout.Edges.Count > 0);

        // SFO has parallel runways — should have hold-shorts for multiple runways
        var holdShortRunways = layout
            .Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort && n.RunwayId is not null)
            .Select(n => n.RunwayId!.ToString())
            .Distinct()
            .ToList();

        Assert.True(holdShortRunways.Count >= 2, $"SFO should have hold-shorts for multiple runways, got: [{string.Join(", ", holdShortRunways)}]");
    }

    [Fact]
    public void SFO_TaxiRoute_HasVariantInference()
    {
        var layout = LoadLayout("SFO", "sfo");
        if (layout is null)
        {
            return;
        }

        // Check that SFO has taxiway variants (e.g., A1, A2, etc.)
        var taxiwayNames = layout.Edges.Select(e => e.TaxiwayName).Where(n => n is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // SFO should have multiple taxiway variants
        bool hasVariants = taxiwayNames.Any(n => n.Length >= 2 && char.IsLetter(n[0]) && char.IsDigit(n[^1]));
        Assert.True(hasVariants, $"SFO should have taxiway variants (e.g., A1). Found: [{string.Join(", ", taxiwayNames.Take(20))}]");
    }

    // -------------------------------------------------------------------------
    // SFO E2E: Issue #13 — SKW5966 pushback + taxi routing
    //
    // SFO T7A spur connects terminal gates (7, 7A, 7B) to taxiway A:
    //   point 0 (south dead end) → ... → point 5 (A junction, shared node)
    //   Gate 7A is a "spot" node near T7A point 4.
    //
    // SKW5966 pushed back to T7A point 2 (mid-spur, heading 284°).
    // "TAXI HERE" on gate 7A sent "TAXI T7A" which walked south to the
    // dead end instead of north toward gate 7A. The correct command should
    // include the destination spot: TAXI @7A or TAXI T7A @7A.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Find the T7A/A junction node and the post-pushback node (T7A point 2).
    /// Returns (pushbackNode, junctionNode, spot7ANode) or nulls if layout unavailable.
    /// </summary>
    private static (GroundNode? pushback, GroundNode? junction, GroundNode? spot7A) FindSfoT7ANodes(AirportGroundLayout layout)
    {
        var spot7A = layout.FindSpotByName("7A");
        if (spot7A is null)
        {
            return (null, null, null);
        }

        var t7aEdges = layout.Edges.Where(e => string.Equals(e.TaxiwayName, "T7A", StringComparison.OrdinalIgnoreCase)).ToList();
        if (t7aEdges.Count == 0)
        {
            return (null, null, null);
        }

        var t7aNodeIds = t7aEdges.SelectMany(e => new[] { e.FromNodeId, e.ToNodeId }).Distinct().ToHashSet();

        // Junction: T7A node that also has an A edge
        var junction = layout.Nodes.Values.FirstOrDefault(n =>
            t7aNodeIds.Contains(n.Id) && n.Edges.Any(e => string.Equals(e.TaxiwayName, "A", StringComparison.OrdinalIgnoreCase))
        );

        // Pushback node: T7A point 2 at (37.620251, -122.385900) — the node SKW5966 pushed back to
        // Find the T7A node closest to that coordinate
        var pushback = layout
            .Nodes.Values.Where(n => t7aNodeIds.Contains(n.Id))
            .OrderBy(n => GeoMath.DistanceNm(n.Latitude, n.Longitude, 37.620251, -122.385900))
            .FirstOrDefault();

        return (pushback, junction, spot7A);
    }

    [Fact]
    public void SFO_TaxiT7A_FromPushback_GoesWrongDirection()
    {
        // Demonstrates the bug: TAXI T7A from the pushback node walks south
        // to the dead end because WalkTaxiway has no directional context.
        var layout = LoadLayout("SFO", "sfo");
        if (layout is null)
        {
            return;
        }

        var (pushback, _, spot7A) = FindSfoT7ANodes(layout);
        Assert.NotNull(pushback);
        Assert.NotNull(spot7A);

        var ac = MakeGroundAircraft("SFO", pushback.Latitude, pushback.Longitude);

        // TAXI T7A — bare taxiway, no destination. This is what the buggy "TAXI HERE" sent.
        var taxi = new TaxiCommand(["T7A"], []);
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);
        Assert.True(result.Success, $"Taxi T7A should succeed: {result.Message}");

        // The route goes south (away from gate 7A and taxiway A).
        // Last segment ends at the southernmost T7A node (dead end).
        var lastSeg = ac.AssignedTaxiRoute!.Segments[^1];
        var lastNode = layout.Nodes[lastSeg.ToNodeId];

        // The dead-end node should be further south (lower latitude) than the pushback node
        Assert.True(
            lastNode.Latitude < pushback.Latitude,
            $"TAXI T7A from pushback should go south (bug): last node lat={lastNode.Latitude:F6} should be < pushback lat={pushback.Latitude:F6}"
        );

        // The dead-end node is further from gate 7A than the pushback node is
        double pushbackToSpot = GeoMath.DistanceNm(pushback.Latitude, pushback.Longitude, spot7A.Latitude, spot7A.Longitude);
        double deadEndToSpot = GeoMath.DistanceNm(lastNode.Latitude, lastNode.Longitude, spot7A.Latitude, spot7A.Longitude);
        Assert.True(
            deadEndToSpot > pushbackToSpot,
            $"Bug: taxi went away from gate 7A (deadEnd→7A={deadEndToSpot:F4}nm > pushback→7A={pushbackToSpot:F4}nm)"
        );
    }

    [Fact]
    public void SFO_TaxiToSpot7A_FromPushback_RoutesToGate()
    {
        // TAXI @7A — A* direct from pushback node to gate 7A.
        // Should route north along T7A toward the gate.
        var layout = LoadLayout("SFO", "sfo");
        if (layout is null)
        {
            return;
        }

        var (pushback, _, spot7A) = FindSfoT7ANodes(layout);
        Assert.NotNull(pushback);
        Assert.NotNull(spot7A);

        var ac = MakeGroundAircraft("SFO", pushback.Latitude, pushback.Longitude);

        // TAXI @7A — no explicit path, A* to spot
        var taxi = new TaxiCommand([], [], DestinationParking: "7A");
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);
        Assert.True(result.Success, $"TAXI @7A should succeed: {result.Message}");

        // Route should end at or very near gate 7A
        var lastSeg = ac.AssignedTaxiRoute!.Segments[^1];
        var lastNode = layout.Nodes[lastSeg.ToNodeId];
        double distToSpot = GeoMath.DistanceNm(lastNode.Latitude, lastNode.Longitude, spot7A.Latitude, spot7A.Longitude);
        Assert.True(distToSpot < 0.02, $"TAXI @7A should end near gate 7A: last node dist={distToSpot:F4}nm");

        // Route should go north (toward gate 7A), not south
        Assert.True(
            lastNode.Latitude > pushback.Latitude,
            $"Route should go north: last lat={lastNode.Latitude:F6} > pushback lat={pushback.Latitude:F6}"
        );
    }

    [Fact]
    public void SFO_TaxiT7A_ToSpot7A_FromPushback_RoutesToGate()
    {
        // TAXI T7A @7A — explicit T7A path extended to gate 7A.
        // ResolveExplicitPath walks T7A (south to dead end), then A* extends to 7A.
        var layout = LoadLayout("SFO", "sfo");
        if (layout is null)
        {
            return;
        }

        var (pushback, _, spot7A) = FindSfoT7ANodes(layout);
        Assert.NotNull(pushback);
        Assert.NotNull(spot7A);

        var ac = MakeGroundAircraft("SFO", pushback.Latitude, pushback.Longitude);

        // TAXI T7A @7A — explicit path + parking destination
        var taxi = new TaxiCommand(["T7A"], [], DestinationParking: "7A");
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);

        if (result.Success)
        {
            // If it succeeds, verify route ends near gate 7A
            var lastSeg = ac.AssignedTaxiRoute!.Segments[^1];
            var lastNode = layout.Nodes[lastSeg.ToNodeId];
            double distToSpot = GeoMath.DistanceNm(lastNode.Latitude, lastNode.Longitude, spot7A.Latitude, spot7A.Longitude);
            Assert.True(distToSpot < 0.02, $"TAXI T7A @7A should end near gate 7A: last node dist={distToSpot:F4}nm");
        }
        else
        {
            // Document the failure — T7A walks to dead end, then A* from dead end to 7A
            // may or may not find a route depending on graph connectivity.
            // This is acceptable: TAXI @7A (no explicit path) is the better command.
            Assert.Contains("7A", result.Message!);
        }
    }

    [Fact]
    public void SFO_TaxiT7A_RouteCompletesWithHoldingInPosition()
    {
        // Verify Bug 2 fix: after TAXI T7A completes (at dead end),
        // aircraft has HoldingInPositionPhase — not phase-less.
        var layout = LoadLayout("SFO", "sfo");
        if (layout is null)
        {
            return;
        }

        var (pushback, _, _) = FindSfoT7ANodes(layout);
        Assert.NotNull(pushback);

        var ac = MakeGroundAircraft("SFO", pushback.Latitude, pushback.Longitude);

        var taxi = new TaxiCommand(["T7A"], []);
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);
        Assert.True(result.Success, $"Taxi T7A should succeed: {result.Message}");

        // Route should be short (only a few segments on the spur)
        Assert.True(ac.AssignedTaxiRoute!.Segments.Count <= 10, $"T7A spur should be short, got {ac.AssignedTaxiRoute.Segments.Count} segments");

        // Run taxi naturally — T7A spur is ~0.05nm.
        // TaxiingPhase adjusts heading/speed but doesn't move position;
        // in the real sim, FlightPhysics.Update does that. We do it manually here.
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            GroundLayout = layout,
            Logger = Logger,
        };

        bool reachedIdle = false;
        for (int i = 0; i < 200; i++)
        {
            if (ac.Phases!.CurrentPhase is HoldingInPositionPhase)
            {
                reachedIdle = true;
                break;
            }

            if (ac.Phases.CurrentPhase is null)
            {
                break;
            }

            PhaseRunner.Tick(ac, ctx);
            AdvancePosition(ac, ctx.DeltaSeconds);
        }

        Assert.True(reachedIdle, $"Taxi should complete with HoldingInPositionPhase, got: {ac.Phases!.CurrentPhase?.Name ?? "null"}");
    }

    [Fact]
    public void SFO_TaxiToSpot7A_ThenRetaxi_Succeeds()
    {
        // End-to-end: TAXI @7A completes, aircraft is in HoldingInPositionPhase,
        // then a second taxi command succeeds.
        var layout = LoadLayout("SFO", "sfo");
        if (layout is null)
        {
            return;
        }

        var (pushback, _, spot7A) = FindSfoT7ANodes(layout);
        Assert.NotNull(pushback);
        Assert.NotNull(spot7A);

        var ac = MakeGroundAircraft("SFO", pushback.Latitude, pushback.Longitude);

        // Step 1: TAXI @7A
        var taxi1 = new TaxiCommand([], [], DestinationParking: "7A");
        var result1 = GroundCommandHandler.TryTaxi(ac, taxi1, layout, null, Logger);
        Assert.True(result1.Success, $"TAXI @7A should succeed: {result1.Message}");

        // Place aircraft at last target node so ArriveAtNode fires
        var lastSeg = ac.AssignedTaxiRoute!.Segments[^1];
        var lastNode = layout.Nodes[lastSeg.ToNodeId];
        ac.AssignedTaxiRoute.CurrentSegmentIndex = ac.AssignedTaxiRoute.Segments.Count - 1;
        ac.Latitude = lastNode.Latitude;
        ac.Longitude = lastNode.Longitude;
        ac.GroundSpeed = 5;

        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            GroundLayout = layout,
            Logger = Logger,
        };

        for (int i = 0; i < 10; i++)
        {
            PhaseRunner.Tick(ac, ctx);
            if (ac.Phases!.CurrentPhase is HoldingInPositionPhase)
            {
                break;
            }
        }

        Assert.IsType<HoldingInPositionPhase>(ac.Phases!.CurrentPhase);

        // Step 2: Issue another taxi — should succeed because HoldingInPositionPhase accepts Taxi
        var taxi2 = new TaxiCommand(["A"], []);
        var result2 = GroundCommandHandler.TryTaxi(ac, taxi2, layout, null, Logger);
        Assert.True(result2.Success, $"Second taxi should succeed: {result2.Message}");
        Assert.IsType<TaxiingPhase>(ac.Phases.CurrentPhase);
    }

    // -------------------------------------------------------------------------
    // SFO E2E: Issue #39 — taxiways not detected as connecting to runway 28R
    //
    // `TAXI C E 28R HS E` fails with "specify connecting taxiway" because
    // taxiway E has no hold-short node for runway 28R. The error lists
    // taxiways that DO connect (C, C2, C3, D, K, L, N, P, Q, R, S1, S2, T)
    // but E is missing.
    // -------------------------------------------------------------------------

    [Fact]
    public void SFO_TaxiCE_ToRunway28R_ShouldSucceed()
    {
        var layout = LoadLayout("SFO", "sfo");
        if (layout is null)
        {
            return;
        }

        // Find a C-only node (not at C/E junction) to simulate a realistic start
        var cOnlyNode = layout.Nodes.Values.FirstOrDefault(n =>
            n.Edges.Any(e => string.Equals(e.TaxiwayName, "C", StringComparison.OrdinalIgnoreCase))
            && !n.Edges.Any(e => string.Equals(e.TaxiwayName, "E", StringComparison.OrdinalIgnoreCase))
            && n.Type == GroundNodeType.TaxiwayIntersection
        );
        Assert.NotNull(cOnlyNode);

        var ac = MakeGroundAircraft("SFO", cOnlyNode.Latitude, cOnlyNode.Longitude);

        // TAXI C E 28R — should find a route from C via E to runway 28R
        var taxi = new TaxiCommand(["C", "E"], [], DestinationRunway: "28R");
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);

        Assert.True(result.Success, $"TAXI C E 28R should succeed: {result.Message}");
    }

    [Fact]
    public void SFO_TaxiwayE_HasHoldShortFor28R()
    {
        var layout = LoadLayout("SFO", "sfo");
        if (layout is null)
        {
            return;
        }

        // Find all hold-short nodes for 28R/10L
        var hs28R = layout
            .Nodes.Values.Where(n =>
                n.Type == GroundNodeType.RunwayHoldShort && n.RunwayId is { } rId && (rId.Contains("28R") || rId.Contains("10L"))
            )
            .ToList();

        Assert.True(hs28R.Count > 0, "SFO should have hold-short nodes for 28R/10L");

        // Collect all taxiways that connect to 28R hold-short nodes
        var connectingTaxiways = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hs in hs28R)
        {
            foreach (var edge in hs.Edges)
            {
                if (!edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                {
                    connectingTaxiways.Add(edge.TaxiwayName);
                }
            }
        }

        // Taxiway E should be among them
        Assert.True(
            connectingTaxiways.Contains("E"),
            $"Taxiway E should connect to a 28R hold-short. Connected taxiways: [{string.Join(", ", connectingTaxiways.Order())}]"
        );
    }

    [Fact]
    public void SFO_TaxiM1_ToRunway1L_ShouldSucceed()
    {
        var layout = LoadLayout("SFO", "sfo");
        if (layout is null)
        {
            return;
        }

        // Find a node on M1 (not at a multi-taxiway junction)
        var m1Node = layout.Nodes.Values.FirstOrDefault(n =>
            n.Edges.Any(e => string.Equals(e.TaxiwayName, "M1", StringComparison.OrdinalIgnoreCase))
            && n.Type == GroundNodeType.TaxiwayIntersection
        );
        Assert.NotNull(m1Node);

        var ac = MakeGroundAircraft("SFO", m1Node.Latitude, m1Node.Longitude);

        // TAXI M1 to runway 1L — M1 connects to 1L holds-short
        var taxi = new TaxiCommand(["M1"], [], DestinationRunway: "1L");
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout, null, Logger);

        Assert.True(result.Success, $"TAXI M1 1L should succeed: {result.Message}");
    }

    [Fact]
    public void SFO_TaxiwayE_WalkMisses28R_FixedByVariantResolver()
    {
        // Documents that WalkTaxiway on E from the C/E junction goes west
        // (crossing 1L/19R and 1R/19L) and misses the eastern 28R hold-shorts.
        // TaxiVariantResolver.ExtendToSameNameHoldShort fixes this by A*
        // extending from the walk endpoint to the 28R hold-short.
        var layout = LoadLayout("SFO", "sfo");
        if (layout is null)
        {
            return;
        }

        var ceJunction = layout.Nodes.Values.FirstOrDefault(n =>
            n.Edges.Any(e => string.Equals(e.TaxiwayName, "C", StringComparison.OrdinalIgnoreCase))
            && n.Edges.Any(e => string.Equals(e.TaxiwayName, "E", StringComparison.OrdinalIgnoreCase))
        );
        Assert.NotNull(ceJunction);

        // Walk E — goes west, misses 28R hold-shorts
        var segments = new List<TaxiRouteSegment>();
        bool walked = TaxiPathfinder.WalkTaxiway(layout, ceJunction.Id, "E", segments, out _);
        Assert.True(walked, "Should be able to walk E from C/E junction");

        // Verify the walk does NOT pass through 28R hold-short (the root cause)
        bool passedHs28R = segments.Any(seg =>
        {
            var toNode = layout.Nodes[seg.ToNodeId];
            return toNode.Type == GroundNodeType.RunwayHoldShort && toNode.RunwayId is { } rId && (rId.Contains("28R") || rId.Contains("10L"));
        });
        Assert.False(passedHs28R, "Walk should miss 28R (goes west instead of east)");

        // But 28R hold-short nodes with E edges DO exist
        var hs28RWithE = layout
            .Nodes.Values.Where(n =>
                n.Type == GroundNodeType.RunwayHoldShort
                && n.RunwayId is { } rId
                && (rId.Contains("28R") || rId.Contains("10L"))
                && n.Edges.Any(e => string.Equals(e.TaxiwayName, "E", StringComparison.OrdinalIgnoreCase))
            )
            .ToList();
        Assert.True(hs28RWithE.Count > 0, "28R hold-shorts with E edges should exist");
    }

    /// <summary>
    /// Move aircraft position based on current heading and ground speed.
    /// Replaces FlightPhysics.Update() for ground-only E2E tests.
    /// </summary>
    private static void AdvancePosition(AircraftState ac, double deltaSeconds)
    {
        if (ac.GroundSpeed <= 0)
        {
            return;
        }

        double distNm = ac.GroundSpeed / 3600.0 * deltaSeconds;
        double hdgRad = ac.Heading * Math.PI / 180.0;
        ac.Latitude += distNm / 60.0 * Math.Cos(hdgRad);
        ac.Longitude += distNm / 60.0 * Math.Sin(hdgRad) / Math.Cos(ac.Latitude * Math.PI / 180.0);
    }
}
