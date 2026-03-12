using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class LineUpPhaseTests
{
    private const string TestDataDir = "TestData";
    private readonly ITestOutputHelper _out;

    public LineUpPhaseTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static AirportGroundLayout? LoadOak()
    {
        var path = Path.Combine(TestDataDir, "oak.geojson");
        if (!File.Exists(path))
        {
            return null;
        }

        return GeoJsonParser.Parse("OAK", File.ReadAllText(path), null, null);
    }

    /// <summary>
    /// OAK RWY 30: aircraft depart heading ~310°.
    /// RWY 30 threshold at SE end (start of roll), far end at NW.
    /// </summary>
    private static RunwayInfo MakeOakRunway30()
    {
        double seLat = 37.701486;
        double seLon = -122.214273;
        double nwLat = 37.720057;
        double nwLon = -122.242128;

        return TestRunwayFactory.Make(
            designator: "30",
            airportId: "OAK",
            thresholdLat: seLat,
            thresholdLon: seLon,
            endLat: nwLat,
            endLon: nwLon,
            heading: GeoMath.BearingTo(seLat, seLon, nwLat, nwLon),
            elevationFt: 6,
            lengthFt: 10520,
            widthFt: 150
        );
    }

    /// <summary>
    /// Find ALL nodes that sit at the intersection of a runway edge and a taxiway edge.
    /// </summary>
    private static List<GroundNode> FindAllRunwayTaxiwayIntersections(AirportGroundLayout layout, string rwyName)
    {
        var result = new List<GroundNode>();
        var rwyEdgeNodeIds = new HashSet<int>();

        foreach (var edge in layout.Edges)
        {
            if (edge.TaxiwayName.Contains(rwyName, StringComparison.OrdinalIgnoreCase))
            {
                rwyEdgeNodeIds.Add(edge.FromNodeId);
                rwyEdgeNodeIds.Add(edge.ToNodeId);
            }
        }

        foreach (var nodeId in rwyEdgeNodeIds)
        {
            if (!layout.Nodes.TryGetValue(nodeId, out var node))
            {
                continue;
            }

            bool hasRwyEdge = false;
            bool hasTaxiEdge = false;
            foreach (var edge in node.Edges)
            {
                if (edge.TaxiwayName.StartsWith("RWY", StringComparison.OrdinalIgnoreCase))
                {
                    hasRwyEdge = true;
                }
                else
                {
                    hasTaxiEdge = true;
                }
            }

            if (hasRwyEdge && hasTaxiEdge)
            {
                result.Add(node);
            }
        }

        // Also include annotated hold-short nodes
        foreach (var hsNode in layout.GetRunwayHoldShortNodes(rwyName))
        {
            if (!result.Any(n => n.Id == hsNode.Id))
            {
                result.Add(hsNode);
            }
        }

        return result;
    }

    /// <summary>
    /// Run LineUpPhase from a specific node and return (backtrackNm, completed, finalHeadingDiff).
    /// </summary>
    private (double BacktrackNm, bool Completed, double HeadingDiff) RunLineUpFromNode(GroundNode node, RunwayInfo runway, AirportGroundLayout layout)
    {
        double perpHeading = (runway.TrueHeading + 90) % 360;
        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = node.Latitude,
            Longitude = node.Longitude,
            Heading = perpHeading,
            IsOnGround = true,
            Departure = "OAK",
        };
        aircraft.Phases = new PhaseList();

        var lineUpPhase = new LineUpPhase(node.Id);
        aircraft.Phases.Add(lineUpPhase);

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0.5,
            Runway = runway,
            FieldElevation = 6,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
        };

        aircraft.Phases.Start(ctx);

        double initialAlong = GeoMath.AlongTrackDistanceNm(
            aircraft.Latitude,
            aircraft.Longitude,
            runway.ThresholdLatitude,
            runway.ThresholdLongitude,
            runway.TrueHeading
        );

        double minAlongTrack = initialAlong;
        bool completed = false;

        for (int i = 0; i < 600; i++)
        {
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            if (lineUpPhase.OnTick(ctx))
            {
                completed = true;
                break;
            }

            double along = GeoMath.AlongTrackDistanceNm(
                aircraft.Latitude,
                aircraft.Longitude,
                runway.ThresholdLatitude,
                runway.ThresholdLongitude,
                runway.TrueHeading
            );

            if (along < minAlongTrack)
            {
                minAlongTrack = along;
            }
        }

        double backtrackNm = initialAlong - minAlongTrack;
        double headingDiff = Math.Abs(FlightPhysics.NormalizeAngle(runway.TrueHeading - aircraft.Heading));
        return (backtrackNm, completed, headingDiff);
    }

    [Fact]
    public void OAK_LineUpPhase_AllIntersections_NeverBacktrackTowardThreshold()
    {
        var layout = LoadOak();
        if (layout is null)
        {
            _out.WriteLine("SKIP: oak.geojson not found");
            return;
        }

        var runway = MakeOakRunway30();
        _out.WriteLine($"RWY 30: heading={runway.TrueHeading:F1}");

        var intersections = FindAllRunwayTaxiwayIntersections(layout, "30");
        _out.WriteLine($"Found {intersections.Count} intersection nodes for RWY 30");

        // Sort by along-track distance (threshold → far end)
        var sorted = intersections
            .Select(n =>
                (
                    Node: n,
                    AlongTrack: GeoMath.AlongTrackDistanceNm(
                        n.Latitude,
                        n.Longitude,
                        runway.ThresholdLatitude,
                        runway.ThresholdLongitude,
                        runway.TrueHeading
                    )
                )
            )
            .OrderBy(x => x.AlongTrack)
            .ToList();

        foreach (var (node, alongTrack) in sorted)
        {
            var edgeNames = string.Join(", ", node.Edges.Select(e => e.TaxiwayName).Distinct());
            _out.WriteLine($"  Node {node.Id}: along={alongTrack:F4}nm ({alongTrack * 6076:F0}ft), type={node.Type}, edges=[{edgeNames}]");
        }

        // Test each intersection node
        bool anyFailed = false;
        foreach (var (node, alongTrack) in sorted)
        {
            var (backtrack, completed, hdgDiff) = RunLineUpFromNode(node, runway, layout);
            var status = backtrack >= 0.01 ? "FAIL" : "ok";
            _out.WriteLine(
                $"  Node {node.Id}: backtrack={backtrack:F4}nm ({backtrack * 6076:F0}ft), completed={completed}, hdgDiff={hdgDiff:F1}, [{status}]"
            );

            if (backtrack >= 0.01)
            {
                anyFailed = true;
            }
        }

        Assert.False(anyFailed, "One or more intersection nodes caused backtracking toward threshold (see output above)");
    }
}
