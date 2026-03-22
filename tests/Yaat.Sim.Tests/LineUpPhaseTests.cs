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

        return GeoJsonParser.Parse("OAK", File.ReadAllText(path), null);
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
    /// Compute the taxiway approach heading: perpendicular to runway, toward the centerline.
    /// Uses signed cross-track to determine which side the node is on.
    /// </summary>
    private static TrueHeading ComputeApproachHeading(GroundNode node, RunwayInfo runway)
    {
        double signedCross = GeoMath.SignedCrossTrackDistanceNm(
            node.Latitude,
            node.Longitude,
            runway.ThresholdLatitude,
            runway.ThresholdLongitude,
            runway.TrueHeading
        );

        // Positive cross-track = left of track → approach from left (heading = runway - 90°)
        // Negative cross-track = right of track → approach from right (heading = runway + 90°)
        double offset = signedCross >= 0 ? -90 : 90;
        return new TrueHeading(runway.TrueHeading.Degrees + offset);
    }

    /// <summary>
    /// Run LineUpPhase from a specific node and return (backtrackNm, completed, finalHeadingDiff).
    /// </summary>
    private (double BacktrackNm, bool Completed, double HeadingDiff) RunLineUpFromNode(GroundNode node, RunwayInfo runway, AirportGroundLayout layout)
    {
        var perpHeading = ComputeApproachHeading(node, runway);
        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = node.Latitude,
            Longitude = node.Longitude,
            TrueHeading = perpHeading,
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
        double headingDiff = Math.Abs(runway.TrueHeading.SignedAngleTo(aircraft.TrueHeading));
        return (backtrackNm, completed, headingDiff);
    }

    /// <summary>
    /// OAK RWY 28R: aircraft depart heading ~292°.
    /// Threshold at east end (10L threshold), far end at west.
    /// </summary>
    private static RunwayInfo MakeOakRunway28R()
    {
        return TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72152,
            thresholdLon: -122.20065,
            endLat: 37.73089,
            endLon: -122.21926,
            heading: 292,
            elevationFt: 9,
            lengthFt: 6213,
            widthFt: 150
        );
    }

    /// <summary>
    /// Run LineUpPhase from a node and return detailed trajectory metrics.
    /// maxAlongBeforeCross: max along-track increase before cross-track drops below half.
    /// A large value indicates diagonal entry (along-track increases while still far from centerline).
    /// </summary>
    private (double BacktrackNm, bool Completed, double HeadingDiff, double MaxAlongBeforeCross) RunLineUpFromNodeDetailed(
        GroundNode node,
        RunwayInfo runway,
        AirportGroundLayout layout
    )
    {
        var perpHeading = ComputeApproachHeading(node, runway);
        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = node.Latitude,
            Longitude = node.Longitude,
            TrueHeading = perpHeading,
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

        double initialCross = Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(
                aircraft.Latitude,
                aircraft.Longitude,
                runway.ThresholdLatitude,
                runway.ThresholdLongitude,
                runway.TrueHeading
            )
        );

        double minAlongTrack = initialAlong;
        double maxAlongBeforeCross = 0;
        bool crossedHalf = false;
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

            double cross = Math.Abs(
                GeoMath.SignedCrossTrackDistanceNm(
                    aircraft.Latitude,
                    aircraft.Longitude,
                    runway.ThresholdLatitude,
                    runway.ThresholdLongitude,
                    runway.TrueHeading
                )
            );

            if (along < minAlongTrack)
            {
                minAlongTrack = along;
            }

            // Track max along-track increase before cross-track drops below half initial
            if (!crossedHalf)
            {
                double alongIncrease = along - initialAlong;
                if (alongIncrease > maxAlongBeforeCross)
                {
                    maxAlongBeforeCross = alongIncrease;
                }

                if (cross < initialCross * 0.5)
                {
                    crossedHalf = true;
                }
            }
        }

        double backtrackNm = initialAlong - minAlongTrack;
        double headingDiff = Math.Abs(runway.TrueHeading.SignedAngleTo(aircraft.TrueHeading));
        return (backtrackNm, completed, headingDiff, maxAlongBeforeCross);
    }

    [Fact]
    public void OAK_LineUpPhase_28R_NoDiagonalEntry()
    {
        var layout = LoadOak();
        if (layout is null)
        {
            _out.WriteLine("SKIP: oak.geojson not found");
            return;
        }

        var runway = MakeOakRunway28R();
        _out.WriteLine($"RWY 28R: heading={runway.TrueHeading.Degrees:F1}");

        var intersections = FindAllRunwayTaxiwayIntersections(layout, "28R");
        _out.WriteLine($"Found {intersections.Count} intersection nodes for RWY 28R");

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
                    ),
                    CrossTrack: Math.Abs(
                        GeoMath.SignedCrossTrackDistanceNm(
                            n.Latitude,
                            n.Longitude,
                            runway.ThresholdLatitude,
                            runway.ThresholdLongitude,
                            runway.TrueHeading
                        )
                    )
                )
            )
            .OrderBy(x => x.AlongTrack)
            .ToList();

        foreach (var (node, alongTrack, crossTrack) in sorted)
        {
            var edgeNames = string.Join(", ", node.Edges.Select(e => e.TaxiwayName).Distinct());
            _out.WriteLine($"  Node {node.Id}: along={alongTrack:F4}nm cross={crossTrack:F4}nm type={node.Type} edges=[{edgeNames}]");
        }

        bool anyDiagonal = false;
        bool anyFailed = false;
        foreach (var (node, _, crossTrack) in sorted)
        {
            var (backtrack, completed, hdgDiff, maxAlongBeforeCross) = RunLineUpFromNodeDetailed(node, runway, layout);

            // Diagonal threshold: only flag nodes far from centerline (> 0.1nm / 600ft)
            // where along-track increases significantly before crossing halfway.
            // Graph-based Stage 1 (navigating to an on-runway neighbor) naturally
            // produces some forward movement (up to ~0.65× cross-track). The pre-stage
            // perpendicular crossing should produce ~0× ratio.
            bool isDiagonal = (crossTrack > 0.1) && (maxAlongBeforeCross > crossTrack * 0.65);
            var diagonalStatus = isDiagonal ? "DIAGONAL" : "ok";
            var backtrackStatus = backtrack >= 0.01 ? "BACKTRACK" : "ok";

            _out.WriteLine(
                $"  Node {node.Id}: backtrack={backtrack:F4}nm maxAlongBeforeCross={maxAlongBeforeCross:F4}nm "
                    + $"completed={completed} hdgDiff={hdgDiff:F1} [{diagonalStatus}] [{backtrackStatus}]"
            );

            if (isDiagonal)
            {
                anyDiagonal = true;
            }

            if (backtrack >= 0.01)
            {
                anyFailed = true;
            }
        }

        Assert.False(anyDiagonal, "One or more intersection nodes caused diagonal runway entry (see output above)");
        Assert.False(anyFailed, "One or more intersection nodes caused backtracking toward threshold (see output above)");
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
        _out.WriteLine($"RWY 30: heading={runway.TrueHeading.Degrees:F1}");

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
        foreach (var (node, _) in sorted)
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
