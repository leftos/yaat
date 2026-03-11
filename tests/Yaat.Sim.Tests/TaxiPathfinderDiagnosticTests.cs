using System.IO;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests;

/// <summary>
/// Verbose diagnostic tests for TaxiPathfinder. Enable these temporarily when debugging
/// unexpected route decisions. They emit a full step-by-step trace of every path decision.
/// </summary>
public class TaxiPathfinderDiagnosticTests(ITestOutputHelper output)
{
    private const string TestDataDir = "TestData";

    private AirportGroundLayout? LoadLayout(string airportId, string subdir)
    {
        string path = Path.Combine(TestDataDir, $"{subdir}.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse(airportId, File.ReadAllText(path)) : null;
    }

    /// <summary>
    /// Diagnostic trace for issue #53: AAL2839 "TAXI B M1 1L" at SFO produces 47 segments.
    /// Tries every node on taxiway B as a starting point and emits the full pathfinder trace.
    /// </summary>
    [Fact]
    public void Diag_SFO_TaxiBM1_To1L_VerboseTrace()
    {
        var layout = LoadLayout("SFO", "sfo");
        if (layout is null)
        {
            output.WriteLine("sfo.geojson not found — skipping");
            return;
        }

        // Find nodes near AAL2839's starting position (lat ~37.609, lon ~-122.3837)
        // to use as realistic starting points.
        const double StartLat = 37.609046;
        const double StartLon = -122.383669;

        var bNodes = layout
            .Nodes.Values.Where(n => n.Edges.Any(e => string.Equals(e.TaxiwayName, "B", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => Math.Abs(n.Latitude - StartLat) + Math.Abs(n.Longitude - StartLon))
            .Take(5)
            .ToList();

        output.WriteLine($"=== SFO B/M1/1L diagnostic — {bNodes.Count} candidate start nodes near ({StartLat}, {StartLon}) ===");
        output.WriteLine("");

        foreach (var startNode in bNodes)
        {
            output.WriteLine(
                $"--- Start node {startNode.Id}: lat={startNode.Latitude:F6} lon={startNode.Longitude:F6} edges=[{string.Join(",", startNode.Edges.Select(e => e.TaxiwayName))}] ---"
            );

            var log = new List<string>();
            var route = TaxiPathfinder.ResolveExplicitPath(
                layout,
                startNode.Id,
                ["B", "M1"],
                out string? failReason,
                destinationRunway: "1L",
                diagnosticLog: msg => log.Add(msg)
            );

            foreach (var line in log)
            {
                output.WriteLine(line);
            }

            if (route is null)
            {
                output.WriteLine($"  RESULT: null (failReason={failReason ?? "null"})");
            }
            else
            {
                output.WriteLine(
                    $"  RESULT: {route.Segments.Count} segments, taxiways=[{string.Join(",", route.Segments.Select(s => s.TaxiwayName).Distinct())}]"
                );
                output.WriteLine($"  Last segment: {route.Segments[^1].FromNodeId} → {route.Segments[^1].ToNodeId}");
                if (layout.Nodes.TryGetValue(route.Segments[^1].ToNodeId, out var endNode))
                {
                    output.WriteLine(
                        $"  End node: lat={endNode.Latitude:F6} lon={endNode.Longitude:F6} type={endNode.Type} runwayId={endNode.RunwayId}"
                    );
                }
            }

            output.WriteLine("");
        }

        // Also dump all runway hold-short nodes for runway 1L so we can see what exists
        var hs1L = layout.Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort && n.RunwayId is { } id && id.Contains("1L")).ToList();
        output.WriteLine($"=== Runway 1L hold-short nodes in SFO layout: {hs1L.Count} ===");
        foreach (var hs in hs1L.OrderBy(n => n.Latitude))
        {
            output.WriteLine(
                $"  Node {hs.Id}: lat={hs.Latitude:F6} lon={hs.Longitude:F6} runwayId={hs.RunwayId} edges=[{string.Join(",", hs.Edges.Select(e => e.TaxiwayName))}]"
            );
        }
    }

    /// <summary>
    /// Diagnostic trace for OAK W3→W rwy30 regression: destinationHint passed on last taxiway
    /// of a multi-taxiway route breaks TaxiVariantResolver for W1/W2 inference.
    /// </summary>
    [Fact]
    public void Diag_OAK_TaxiW3W_ToRunway30_VerboseTrace()
    {
        var layout = LoadLayout("OAK", "oak");
        if (layout is null)
        {
            output.WriteLine("oak.geojson not found — skipping");
            return;
        }

        // Find W3 edges and use the nodes from the first edge as start candidates
        var w3Edges = layout
            .Nodes.Values.SelectMany(n => n.Edges)
            .Where(e => string.Equals(e.TaxiwayName, "W3", StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToList();
        var tried = new HashSet<int>();
        var startNodes = w3Edges.SelectMany(e => new[] { e.FromNodeId, e.ToNodeId }).Where(tried.Add).Take(4).ToList();

        output.WriteLine($"=== OAK W3/W/30 diagnostic — {startNodes.Count} candidate start nodes ===");
        output.WriteLine("");

        foreach (var startId in startNodes)
        {
            if (!layout.Nodes.TryGetValue(startId, out var startNode))
            {
                continue;
            }

            output.WriteLine(
                $"--- Start node {startId}: lat={startNode.Latitude:F6} lon={startNode.Longitude:F6} edges=[{string.Join(",", startNode.Edges.Select(e => e.TaxiwayName))}] ---"
            );

            var log = new List<string>();
            var route = TaxiPathfinder.ResolveExplicitPath(
                layout,
                startId,
                ["W3", "W"],
                out string? failReason,
                destinationRunway: "30",
                diagnosticLog: msg => log.Add(msg)
            );

            foreach (var line in log)
            {
                output.WriteLine(line);
            }

            if (route is null)
            {
                output.WriteLine($"  RESULT: null (failReason={failReason ?? "null"})");
            }
            else
            {
                output.WriteLine(
                    $"  RESULT: {route.Segments.Count} segments, taxiways=[{string.Join(",", route.Segments.Select(s => s.TaxiwayName).Distinct())}]"
                );
                output.WriteLine($"  Last segment: {route.Segments[^1].FromNodeId} → {route.Segments[^1].ToNodeId}");
                if (layout.Nodes.TryGetValue(route.Segments[^1].ToNodeId, out var endNode))
                {
                    output.WriteLine(
                        $"  End node: lat={endNode.Latitude:F6} lon={endNode.Longitude:F6} type={endNode.Type} runwayId={endNode.RunwayId}"
                    );
                }

                bool hasVariant = route.Segments.Any(s => TaxiVariantResolver.IsNumberedVariant(s.TaxiwayName, "W"));
                bool passesHoldShort = route.Segments.Any(s =>
                    layout.Nodes.TryGetValue(s.ToNodeId, out var n)
                    && n.Type == GroundNodeType.RunwayHoldShort
                    && n.RunwayId is { } rId
                    && TaxiPathfinder.RunwayIdMatches(rId, "30")
                );
                output.WriteLine($"  hasVariant={hasVariant} passesHoldShort={passesHoldShort}");
            }

            output.WriteLine("");
        }

        // Dump runway 30 hold-short nodes
        var hs30 = layout
            .Nodes.Values.Where(n => n.Type == GroundNodeType.RunwayHoldShort && n.RunwayId is { } id && TaxiPathfinder.RunwayIdMatches(id, "30"))
            .ToList();
        output.WriteLine($"=== Runway 30 hold-short nodes in OAK layout: {hs30.Count} ===");
        foreach (var hs in hs30.OrderBy(n => n.Latitude))
        {
            output.WriteLine(
                $"  Node {hs.Id}: lat={hs.Latitude:F6} lon={hs.Longitude:F6} runwayId={hs.RunwayId} edges=[{string.Join(",", hs.Edges.Select(e => e.TaxiwayName))}]"
            );
        }
    }
}
