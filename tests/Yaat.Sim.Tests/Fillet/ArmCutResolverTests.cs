using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Fillet;

namespace Yaat.Sim.Tests.Fillet;

/// <summary>Pass-5 step 3d gates for <see cref="ArmCutResolver"/> and plan-time shared-arm scaling.</summary>
public class ArmCutResolverTests
{
    [Fact]
    public void Simple90Degree_SingleCutPerArm()
    {
        var junction = BuildJunction(arms: [(0, 0.0, 800), (1, 90.0, 800)], corners: [(0, 1, 75.0)]);

        var result = Resolve(junction);

        Assert.Equal(2, result.Cuts.Count);
        Assert.All(result.Cuts.Values, c => Assert.Single(c.OwningCornerIds));
        Assert.Single(result.CornerArcs);
        Assert.DoesNotContain(result.Warnings, w => w.Code == PlanWarning.SingleCutRejected);
    }

    [Fact]
    public void SymmetricFourWay_SingleCutPerArm()
    {
        var junction = BuildJunction(
            arms: [(0, 0.0, 800), (1, 90.0, 800), (2, 180.0, 800), (3, 270.0, 800)],
            corners: [(0, 1, 75.0), (1, 2, 75.0), (2, 3, 75.0), (3, 0, 75.0)]
        );

        var result = Resolve(junction);

        Assert.Equal(4, result.Cuts.Count);
        Assert.Equal(4, result.CornerArcs.Count);
        Assert.DoesNotContain(result.Warnings, w => w.Code == PlanWarning.SingleCutRejected);
    }

    [Fact]
    public void ThreeWay0_100_200_MultiCutOnWideAngleArms()
    {
        var junction = BuildJunction(arms: [(0, 0.0, 800), (1, 100.0, 800), (2, 200.0, 800)], corners: [(0, 1, 75.0), (0, 2, 75.0), (1, 2, 75.0)]);

        var result = Resolve(junction);

        var arm0Cuts = CutsOnArm(result, 0).OrderBy(c => c.DistanceAlongArmFt).Select(c => c.DistanceAlongArmFt).ToList();
        var arm2Cuts = CutsOnArm(result, 2).OrderBy(c => c.DistanceAlongArmFt).Select(c => c.DistanceAlongArmFt).ToList();
        var arm1Cuts = CutsOnArm(result, 1).Select(c => c.DistanceAlongArmFt).ToList();

        Assert.Equal(2, arm0Cuts.Count);
        Assert.Equal(2, arm2Cuts.Count);
        Assert.Single(arm1Cuts);

        Assert.Contains(result.Warnings, w => w.Code == PlanWarning.SingleCutRejected);

        Assert.True(arm0Cuts[0] < 20.0);
        Assert.True(arm0Cuts[1] > 50.0);
        Assert.True(arm2Cuts[0] < 20.0);
        Assert.True(arm2Cuts[1] > 50.0);
        Assert.InRange(arm1Cuts[0], 55.0, 70.0);

        Assert.Equal(3, result.CornerArcs.Count);
        Assert.DoesNotContain(result.Warnings, w => w.Code == PlanWarning.DegenerateRadius);

        var acute = junction.Corners.First(c => c.ArmIdA == 0 && c.ArmIdB == 2);
        var arc = result.CornerArcs.First(a => a.CornerId == acute.CornerId);
        var cutA = Assert.IsType<FilletEndpoint.Cut>(arc.EndpointAtArmA);
        var cutB = Assert.IsType<FilletEndpoint.Cut>(arc.EndpointAtArmB);
        double ta = result.Cuts[cutA.Id].DistanceAlongArmFt;
        double tb = result.Cuts[cutB.Id].DistanceAlongArmFt;
        Assert.InRange(ta, 10.0, 20.0);
        Assert.InRange(tb, 10.0, 20.0);
        Assert.NotEqual(cutA.Id, cutB.Id);
    }

    [Fact]
    public void EffectiveMinRadiusFt_13And63FtOn20DegreeTurn_AboveRadiusFloor()
    {
        double dist13 = 13.0 / GeoMath.FeetPerNm;
        double dist63 = 63.0 / GeoMath.FeetPerNm;
        var posA = GeoMath.ProjectPoint(LatLon.Zero, new TrueHeading(0), dist13);
        var posB = GeoMath.ProjectPoint(LatLon.Zero, new TrueHeading(200), dist63);

        double r = FilletGeometry.EffectiveMinRadiusFt(13, 63, 0, 200, posA, posB);

        Assert.InRange(r, 70.0, 80.0);
        Assert.True(r >= FilletConstants.RadiusFloorFt);
    }

    [Fact]
    public void IdealsWithinCoincidentThreshold_CoalescedSingleCut()
    {
        var junction = BuildJunction(arms: [(0, 0.0, 800), (1, 90.0, 800), (2, 45.0, 800)], corners: [(0, 1, 75.0), (0, 2, 50.0), (1, 2, 52.0)]);

        var result = Resolve(junction);

        Assert.Single(CutsOnArm(result, 0));
        Assert.Single(CutsOnArm(result, 1));
    }

    [Fact]
    public void SharedShortArm_ScalesAndMergesAcrossJunctions()
    {
        var layout = BuildSharedArmLayout(sharedLengthFt: 80);
        var jp1 = PlanAt(layout, 1);
        var jp2 = PlanAt(layout, 2);

        var nextCutId = new CutId(1);
        var r1 = ArmCutResolver.Resolve(jp1, ref nextCutId);
        var r2 = ArmCutResolver.Resolve(jp2, ref nextCutId);
        var plan = FilletPlanBuilder.Build(layout, [jp1, jp2], [r1, r2]);

        Assert.Contains(plan.Warnings, w => w.Code == PlanWarning.SharedArmScaled);
        Assert.NotEmpty(plan.TangentMerges);
    }

    [Fact]
    public void FourWay_OneCornerArcPerArmPair()
    {
        var junction = BuildJunction(
            arms: [(0, 0.0, 800), (1, 90.0, 800), (2, 180.0, 800), (3, 270.0, 800)],
            corners: [(0, 1, 75.0), (1, 2, 75.0), (2, 3, 75.0), (3, 0, 75.0)]
        );

        var result = Resolve(junction);

        Assert.Equal(result.SurvivingCorners.Count, result.CornerArcs.Count);
        Assert.Equal(result.SurvivingCorners.Count, result.CornerArcs.Select(a => a.CornerId).Distinct().Count());
        Assert.DoesNotContain(result.CornerArcs, a => a.EndpointAtArmA == a.EndpointAtArmB);
    }

    [Fact]
    public void MultiCut_EnforceGap_DemotesWithCornerDemotedWarning()
    {
        var junction = BuildJunctionWithExplicitIdeals(
            arms: [(0, 0.0, 800), (1, 90.0, 800), (2, 180.0, 800), (3, 270.0, 800)],
            corners: [(0, 1, 15.01), (0, 2, 10.0), (0, 3, 18.0)]
        );

        var result = Resolve(junction);

        Assert.Contains(result.Warnings, w => w.Code == PlanWarning.CornerDemoted);
        var arm0Cuts = CutsOnArm(result, 0).OrderBy(c => c.DistanceAlongArmFt).Select(c => c.DistanceAlongArmFt).ToList();
        if (arm0Cuts.Count >= 2)
        {
            Assert.True(arm0Cuts[1] - arm0Cuts[0] >= FilletConstants.MinArmSegmentGapFt - 0.01);
        }
    }

    [Fact]
    public void Apply_RepairCountersRemainZero_OnSimple90()
    {
        var stats = new FilletArcGenerator().Apply(BuildSimpleIntersectionLayout());
        AssertRepairCountersZero(stats);
    }

    [Fact]
    public void Apply_RepairCountersRemainZero_OnThreeWayMultiCut()
    {
        var stats = new FilletArcGenerator().Apply(BuildThreeWayLayout());
        AssertRepairCountersZero(stats);
        Assert.True(stats.ArcsCreated >= 3);
    }

    [Fact]
    public void Apply_YPattern_CollinearThroughConnectsStraightArmTangents()
    {
        var layout = BuildYPatternLayout();
        new FilletArcGenerator().Apply(layout);

        // The collinear straight-through arms stay connected end-to-end after filleting.
        int straightNorth = 10;
        int straightSouth = 12;
        Assert.True(HasPath(layout, straightNorth, straightSouth, allowedTaxiways: ["STRAIGHT"]));
    }

    private static void AssertRepairCountersZero(FilletStatistics stats)
    {
        Assert.Equal(0, stats.OrphansRescued);
        Assert.Equal(0, stats.RedundantPreserveEdgesRemoved);
        Assert.Equal(0, stats.DuplicateCornerArcsRemoved);
        Assert.Equal(0, stats.ParallelBypassEdgesRemoved);
        Assert.Equal(0, stats.DirectShortensAdded);
    }

    private static bool HasPath(AirportGroundLayout layout, int startId, int goalId, IReadOnlyList<string> allowedTaxiways)
    {
        var allowed = allowedTaxiways.ToHashSet(StringComparer.Ordinal);
        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(startId);
        visited.Add(startId);

        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            if (id == goalId)
            {
                return true;
            }

            if (!layout.Nodes.TryGetValue(id, out var node))
            {
                continue;
            }

            foreach (var e in node.Edges)
            {
                if (e is not GroundEdge ge || !allowed.Contains(ge.TaxiwayName))
                {
                    continue;
                }

                int next = ge.OtherNode(node).Id;
                if (visited.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return false;
    }

    private static ArmCutResolver.JunctionCutResult Resolve(JunctionPlan junction)
    {
        var nextCutId = new CutId(1);
        return ArmCutResolver.Resolve(junction, ref nextCutId);
    }

    private static IEnumerable<ResolvedArmCut> CutsOnArm(ArmCutResolver.JunctionCutResult result, int armId) =>
        result.Cuts.Values.Where(c => c.ArmId == armId);

    private static JunctionPlan PlanAt(AirportGroundLayout layout, int nodeId)
    {
        var node = layout.Nodes[nodeId];
        var arms = TaxiwayArmBuilder.BuildArms(node, []);
        var (corners, collinear) = CornerPlanner.PlanCorners(node, arms);
        return new JunctionPlan(nodeId, node, JunctionKind.MultiCorner, collinear.Count > 0, arms, corners, collinear);
    }

    private static JunctionPlan BuildJunction(
        (int Id, double BearingDeg, double LengthFt)[] arms,
        (int ArmA, int ArmB, double RequestedRadiusFt)[] corners
    )
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        var junction = new GroundNode
        {
            Id = 0,
            Position = LatLon.Zero,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[0] = junction;

        var builtArms = new List<TaxiwayArm>();
        foreach (var (id, bearing, lengthFt) in arms)
        {
            double distNm = lengthFt / GeoMath.FeetPerNm;
            var terminal = new GroundNode
            {
                Id = id + 10,
                Position = GeoMath.ProjectPoint(LatLon.Zero, new TrueHeading(bearing), distNm),
                Type = GroundNodeType.TaxiwayIntersection,
            };
            layout.Nodes[terminal.Id] = terminal;
            var edge = new GroundEdge
            {
                Nodes = [junction, terminal],
                TaxiwayName = $"A{id}",
                DistanceNm = distNm,
            };
            layout.Edges.Add(edge);

            var walk = TaxiwayWalk.Walk(edge, junction, []);
            builtArms.Add(
                new TaxiwayArm(
                    id,
                    0,
                    edge,
                    edge.TaxiwayName,
                    bearing,
                    walk.AvailableLengthFt,
                    FilletConstants.MaxTangentDistFt,
                    TaxiwayArmTerminus.OtherIntersection,
                    terminal,
                    false,
                    walk
                )
            );
        }

        layout.RebuildAdjacencyLists();

        var cornerSpecs = new List<CornerSpec>();
        int cornerId = 0;
        foreach (var (armA, armB, radius) in corners)
        {
            var a = builtArms.First(x => x.Id == armA);
            var b = builtArms.First(x => x.Id == armB);
            double turn = FilletGeometry.ComputeTurnAngle(a.BearingFromJunctionDeg, b.BearingFromJunctionDeg);
            double ideal = FilletGeometry.ComputeIdealTangentFt(
                turn,
                radius,
                a.LengthFt,
                b.LengthFt,
                capA: false,
                capB: false,
                a.IntersectionCapFt,
                b.IntersectionCapFt
            );
            cornerSpecs.Add(
                new CornerSpec(
                    cornerId++,
                    0,
                    armA,
                    armB,
                    a.RootEdge,
                    b.RootEdge,
                    turn,
                    radius,
                    ideal,
                    a.BearingFromJunctionDeg,
                    b.BearingFromJunctionDeg
                )
            );
        }

        return new JunctionPlan(0, junction, JunctionKind.MultiCorner, false, builtArms, cornerSpecs, []);
    }

    private static JunctionPlan BuildJunctionWithExplicitIdeals(
        (int Id, double BearingDeg, double LengthFt)[] arms,
        (int ArmA, int ArmB, double IdealTangentFt)[] corners
    )
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        var junction = new GroundNode
        {
            Id = 0,
            Position = LatLon.Zero,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[0] = junction;

        var builtArms = new List<TaxiwayArm>();
        foreach (var (id, bearing, lengthFt) in arms)
        {
            double distNm = lengthFt / GeoMath.FeetPerNm;
            var terminal = new GroundNode
            {
                Id = id + 10,
                Position = GeoMath.ProjectPoint(LatLon.Zero, new TrueHeading(bearing), distNm),
                Type = GroundNodeType.TaxiwayIntersection,
            };
            layout.Nodes[terminal.Id] = terminal;
            var edge = new GroundEdge
            {
                Nodes = [junction, terminal],
                TaxiwayName = $"A{id}",
                DistanceNm = distNm,
            };
            layout.Edges.Add(edge);

            var walk = TaxiwayWalk.Walk(edge, junction, []);
            builtArms.Add(
                new TaxiwayArm(
                    id,
                    0,
                    edge,
                    edge.TaxiwayName,
                    bearing,
                    walk.AvailableLengthFt,
                    FilletConstants.MaxTangentDistFt,
                    TaxiwayArmTerminus.OtherIntersection,
                    terminal,
                    false,
                    walk
                )
            );
        }

        layout.RebuildAdjacencyLists();

        var cornerSpecs = new List<CornerSpec>();
        int cornerId = 0;
        foreach (var (armA, armB, ideal) in corners)
        {
            var a = builtArms.First(x => x.Id == armA);
            var b = builtArms.First(x => x.Id == armB);
            double turn = FilletGeometry.ComputeTurnAngle(a.BearingFromJunctionDeg, b.BearingFromJunctionDeg);
            cornerSpecs.Add(
                new CornerSpec(
                    cornerId++,
                    0,
                    armA,
                    armB,
                    a.RootEdge,
                    b.RootEdge,
                    turn,
                    75.0,
                    ideal,
                    a.BearingFromJunctionDeg,
                    b.BearingFromJunctionDeg
                )
            );
        }

        return new JunctionPlan(0, junction, JunctionKind.MultiCorner, false, builtArms, cornerSpecs, []);
    }

    private static AirportGroundLayout BuildSharedArmLayout(double sharedLengthFt)
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        double sharedNm = sharedLengthFt / GeoMath.FeetPerNm;
        double legNm = 400.0 / GeoMath.FeetPerNm;

        var j1 = new GroundNode
        {
            Id = 1,
            Position = LatLon.Zero,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var j2 = new GroundNode
        {
            Id = 2,
            Position = GeoMath.ProjectPoint(LatLon.Zero, new TrueHeading(0), sharedNm),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var north = new GroundNode
        {
            Id = 11,
            Position = GeoMath.ProjectPoint(j1.Position, new TrueHeading(90), legNm),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var south = new GroundNode
        {
            Id = 12,
            Position = GeoMath.ProjectPoint(j2.Position, new TrueHeading(270), legNm),
            Type = GroundNodeType.TaxiwayIntersection,
        };

        foreach (var n in new[] { j1, j2, north, south })
        {
            layout.Nodes[n.Id] = n;
        }

        void Add(GroundNode a, GroundNode b, string twy)
        {
            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [a, b],
                    TaxiwayName = twy,
                    DistanceNm = GeoMath.DistanceNm(a.Position, b.Position),
                }
            );
        }

        Add(j1, j2, "SHARED");
        Add(j1, north, "N");
        Add(j2, south, "S");

        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static AirportGroundLayout BuildThreeWayLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        var junction = new GroundNode
        {
            Id = 0,
            Position = LatLon.Zero,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[0] = junction;

        double distNm = 800.0 / GeoMath.FeetPerNm;
        foreach (var (id, bearing) in new[] { (1, 0.0), (2, 100.0), (3, 200.0) })
        {
            var pos = GeoMath.ProjectPoint(LatLon.Zero, new TrueHeading(bearing), distNm);
            var node = new GroundNode
            {
                Id = id,
                Position = pos,
                Type = GroundNodeType.TaxiwayIntersection,
            };
            layout.Nodes[id] = node;
            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [junction, node],
                    TaxiwayName = $"T{id}",
                    DistanceNm = distNm,
                }
            );
        }

        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static AirportGroundLayout BuildYPatternLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        var junction = new GroundNode
        {
            Id = 0,
            Position = LatLon.Zero,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[0] = junction;

        double distNm = 800.0 / GeoMath.FeetPerNm;
        void AddArm(int nodeId, double bearingDeg, string taxiway)
        {
            var pos = GeoMath.ProjectPoint(LatLon.Zero, new TrueHeading(bearingDeg), distNm);
            var node = new GroundNode
            {
                Id = nodeId,
                Position = pos,
                Type = GroundNodeType.TaxiwayIntersection,
            };
            layout.Nodes[nodeId] = node;
            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [junction, node],
                    TaxiwayName = taxiway,
                    DistanceNm = distNm,
                }
            );
        }

        AddArm(10, 0.0, "STRAIGHT");
        AddArm(11, 89.0, "BRANCH");
        AddArm(12, 179.0, "STRAIGHT");

        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static AirportGroundLayout BuildSimpleIntersectionLayout()
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };
        var intersection = new GroundNode
        {
            Id = 0,
            Position = LatLon.Zero,
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[0] = intersection;

        for (int i = 0; i < 2; i++)
        {
            int id = i + 1;
            var node = new GroundNode
            {
                Id = id,
                Position = new LatLon(i == 0 ? 0.01 : 0, i == 0 ? 0 : 0.01),
                Type = GroundNodeType.TaxiwayIntersection,
            };
            layout.Nodes[id] = node;
            layout.Edges.Add(
                new GroundEdge
                {
                    Nodes = [intersection, node],
                    TaxiwayName = $"T{id}",
                    DistanceNm = GeoMath.DistanceNm(LatLon.Zero, node.Position),
                }
            );
        }

        layout.RebuildAdjacencyLists();
        return layout;
    }
}
