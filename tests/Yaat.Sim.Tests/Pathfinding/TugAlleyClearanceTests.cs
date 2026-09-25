using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// A push whose goal is in the non-movement area — a spot, a stand, a ramp node — keeps the aircraft's outline outside
/// the object-free area of every movement-area taxiway it does not name; the half-width is the taxiway's Airplane Design
/// Group's centreline-to-object separation, the group the airport's widest runway allows capped by the spacing to the
/// nearest parallel taxiway. A push the command sends onto a taxiway is not held to it.
/// </summary>
public class TugAlleyClearanceTests(ITestOutputHelper output)
{
    internal const string Regional = "CRJ7";
    private const string Narrowbody = "B738";

    /// <summary>How far from A's centreline the planner holds an alley push: A's ADG IV half-width, 129.5 ft, plus its 5 ft margin.</summary>
    private const double AHeldClearFt = 134.5;

    /// <summary>
    /// SFO's A runs 218 ft from Y and 236 ft from B, an ADG IV spacing, so its object-free half-width is 129.5 ft, not the
    /// airport's ADG VI 193 ft.
    /// </summary>
    [Fact]
    public void TaxiwayAdg_SfoA_IsGroupIV_129_5Ft()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("AirplaneDesignGroups", LogLevel.Debug).InitializeSimLog();
        AirplaneDesignGroup group = AirplaneDesignGroups.ForTaxiway(layout, "A");

        output.WriteLine(
            $"SFO widest runway {layout.Runways.Max(r => r.WidthFt):F0} ft; taxiway A ADG {group}; taxiway D ADG {AirplaneDesignGroups.ForTaxiway(layout, "D")}"
        );
        Assert.Equal(AirplaneDesignGroup.IV, group);
        Assert.Equal(129.5, AirplaneDesignGroups.TaxiwayObjectFreeHalfWidthFt(group));
    }

    /// <summary>
    /// The airport's group comes from its widest runway in the buckets the taxiway hold-short wingtip floor has always used,
    /// and that floor is still half the group's span ceiling plus 25 ft: 49.5 / 64.5 / 84 / 132 / 156 ft.
    /// </summary>
    [Theory]
    [InlineData(60.0, AirplaneDesignGroup.I, 49.5)]
    [InlineData(74.9, AirplaneDesignGroup.I, 49.5)]
    [InlineData(75.0, AirplaneDesignGroup.II, 64.5)]
    [InlineData(99.9, AirplaneDesignGroup.II, 64.5)]
    [InlineData(100.0, AirplaneDesignGroup.III, 84.0)]
    [InlineData(149.9, AirplaneDesignGroup.III, 84.0)]
    [InlineData(150.0, AirplaneDesignGroup.V, 132.0)]
    [InlineData(199.9, AirplaneDesignGroup.V, 132.0)]
    [InlineData(200.0, AirplaneDesignGroup.VI, 156.0)]
    [InlineData(300.0, AirplaneDesignGroup.VI, 156.0)]
    public void AdgFromRunwayWidth_BucketsMatchWingtipFloor(double widthFt, AirplaneDesignGroup expected, double floorFt)
    {
        AirplaneDesignGroup group = AirplaneDesignGroups.FromRunwayWidth(widthFt);

        Assert.Equal(expected, group);
        Assert.Equal(floorFt, HoldShortAnnotator.WingtipClearanceFloorFt(widthFt));
        Assert.Equal((AirplaneDesignGroups.MaxWingspanFt(group) / 2.0) + 25.0, HoldShortAnnotator.WingtipClearanceFloorFt(widthFt));
    }

    /// <summary>
    /// F8 → 7A under the straight-then-line push brought the right wingtip to 58.7 ft from A's centreline. The push now
    /// pivots onto the lane after a shorter straight and keeps every sampled outline at least A's 129.5 ft half-width
    /// plus the planner's 5 ft margin from A, with no encroachment warning.
    /// </summary>
    [Fact]
    public void AlleyPush_F8To7A_KeepsFootprintOutsideAObjectFreeArea()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        TugPlan plan = PlanOrFail(layout, "F8", Regional, "7A");
        double closestFt = ClosestToFt(layout, plan, Regional, "A");

        output.WriteLine($"F8 → 7A: closest outline approach to A {closestFt:F1} ft; {plan.Warnings.Count} warning(s)");
        Assert.Empty(plan.Warnings);
        Assert.True(
            closestFt >= AHeldClearFt - 0.5,
            $"the outline came {closestFt:F1} ft from A's centreline, inside the {AHeldClearFt:F1} ft the planner holds"
        );
    }

    /// <summary>
    /// E12 → 7B: the approved straight-then-line push came 100.7 ft from A, inside A's ADG IV zone, so the push pivots onto
    /// the lane earlier — still a straight-then-line push, clear of A with no warning. D, which runs up to and across A on
    /// the far side, is clipped to A's zone and no longer reaches into the alley.
    /// </summary>
    [Fact]
    public void AlleyPush_E12To7B_StraightThenLine_ClearOfA()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        TugPlan plan = PlanOrFail(layout, "E12", Narrowbody, "7B");
        double closestFt = ClosestToFt(layout, plan, Narrowbody, "A");

        output.WriteLine($"E12 → 7B: closest outline approach to A {closestFt:F1} ft");
        Assert.Empty(plan.Warnings);
        Assert.Equal(
            [
                (PushbackLegKind.Push, TugMoveShape.Straight),
                (PushbackLegKind.Push, TugMoveShape.Straight),
                (PushbackLegKind.Push, TugMoveShape.ViaLine),
                (PushbackLegKind.Pull, TugMoveShape.ViaLine),
            ],
            [.. plan.Moves.Select(m => (m.Move.Kind, m.Move.Shape))]
        );
        Assert.True(closestFt >= AHeldClearFt - 0.5, $"the outline came {closestFt:F1} ft from A's centreline");
    }

    /// <summary>
    /// D7 <c>PUSH A F1</c> is sent onto A, so it is not held clear of any taxiway's object-free area: it ends lined up on
    /// A with no encroachment warning.
    /// </summary>
    [Fact]
    public void PushNamingMovementAreaTaxiway_NotSubjectToAlleyClearance()
    {
        if (LoadSfo() is not { } layout)
        {
            return;
        }

        GroundNode d7 = layout.FindParkingByName("D7") ?? throw new InvalidOperationException("no D7");
        GroundNode exit = layout.FindExitByTaxiway(d7.Position, "A") ?? throw new InvalidOperationException("no A exit off D7");
        double facingDeg =
            layout.GetEdgeBearingForTaxiway(exit, "A", d7.TrueHeading!.Value.Degrees) ?? throw new InvalidOperationException("no A edge");
        TugRequest request = StandStart(d7, Narrowbody, TugGoal.TaxiwayLine(exit, "A", facingDeg) with { FacingTaxiwayName = "F1" });

        TugPlan? plan = TugMovePlanner.Plan(layout, request, out string refusal);

        Assert.True(plan is not null, $"refused: {refusal}");
        double closestFt = ClosestToFt(layout, plan, Narrowbody, "A");
        output.WriteLine($"D7 PUSH A F1: closest outline approach to A {closestFt:F1} ft; warnings {plan.Warnings.Count}");
        Assert.True(closestFt < 5.0, $"the premise is a push onto A, but it ended {closestFt:F1} ft off it");
        Assert.DoesNotContain(plan.Warnings, w => w.Kind == TugPlanWarningKind.FoulsTaxiway);
    }

    private TugPlan PlanOrFail(AirportGroundLayout layout, string gate, string aircraftType, string spot)
    {
        SimLogBuilder.CreateForTest(output).EnableCategory("TugMovePlanner", LogLevel.Debug).InitializeSimLog();
        GroundNode stand = layout.FindParkingByName(gate) ?? throw new InvalidOperationException($"no gate {gate}");
        GroundNode node = layout.FindSpotNodeByName(spot) ?? throw new InvalidOperationException($"no spot {spot}");
        TugPlan? plan = TugMovePlanner.Plan(layout, StandStart(stand, aircraftType, TugGoal.Spot(node)), out string refusal);
        Assert.True(plan is not null, $"{gate} → {spot} was refused: {refusal}");
        output.WriteLine(
            $"{gate} → {spot} ({aircraftType}): {string.Join(", ", plan.Moves.Select(m => $"{m.Move.Kind} {m.Move.Shape} {m.PathLengthFt:F0} ft"))}"
        );
        return plan;
    }

    internal static TugRequest StandStart(GroundNode stand, string aircraftType, TugGoal goal) =>
        new()
        {
            Start = new TugPose(stand.Position, stand.TrueHeading!.Value.Degrees),
            StartsAtStand = true,
            AircraftType = aircraftType,
            Goals = [goal],
            ParkedNeighbours = [],
            FinalFacingTrueDeg = null,
            PreviousKind = null,
            Forced = false,
        };

    /// <summary>
    /// The closest any point of the aircraft's outline — fuselage, wing and tailplane, sampled every 2 ft along each —
    /// comes over the plan's samples to the taxiway's centreline: its straight edges, and the arcs joining two of its own
    /// edges as chords.
    /// </summary>
    private static double ClosestToFt(AirportGroundLayout layout, TugPlan plan, string aircraftType, string taxiway)
    {
        List<(LatLon A, LatLon B)> centreline = [];
        foreach (GroundEdge edge in layout.Edges.Where(e => e.MatchesTaxiway(taxiway)))
        {
            List<LatLon> points = [edge.Nodes[0].Position, .. edge.IntermediatePoints.Select(q => new LatLon(q.Lat, q.Lon)), edge.Nodes[1].Position];
            centreline.AddRange(points.Zip(points.Skip(1)));
        }

        centreline.AddRange(
            layout.Arcs.Where(a => (a.TaxiwayNames.Length == 1) && a.MatchesTaxiway(taxiway)).Select(a => (a.Nodes[0].Position, a.Nodes[1].Position))
        );
        double halfLengthNm = TugMovePlanner.FuselageLengthFt(aircraftType) / 2.0 / GeoMath.FeetPerNm;
        double halfSpanNm = TugMovePlanner.WingspanFt(aircraftType) / 2.0 / GeoMath.FeetPerNm;
        double best = double.PositiveInfinity;
        foreach (TugPose pose in plan.Moves.SelectMany(m => m.Samples))
        {
            var nose = new TrueHeading(pose.NoseTrueDeg);
            LatLon tail = GeoMath.ProjectPoint(pose.Position, nose.ToReciprocal(), halfLengthNm);
            LatLon noseTip = GeoMath.ProjectPoint(pose.Position, nose, halfLengthNm);
            LatLon left = GeoMath.ProjectPoint(pose.Position, new TrueHeading(pose.NoseTrueDeg - 90.0), halfSpanNm);
            LatLon right = GeoMath.ProjectPoint(pose.Position, new TrueHeading(pose.NoseTrueDeg + 90.0), halfSpanNm);
            LatLon tailLeft = GeoMath.ProjectPoint(tail, new TrueHeading(pose.NoseTrueDeg - 90.0), 0.4 * halfSpanNm);
            LatLon tailRight = GeoMath.ProjectPoint(tail, new TrueHeading(pose.NoseTrueDeg + 90.0), 0.4 * halfSpanNm);
            foreach (LatLon point in Along(tail, noseTip).Concat(Along(left, right)).Concat(Along(tailLeft, tailRight)))
            {
                foreach ((LatLon a, LatLon b) in centreline)
                {
                    best = Math.Min(best, GeoMath.DistanceToSegmentFt(point, a, b));
                }
            }
        }

        return best;
    }

    /// <summary>Points every 2 ft from <paramref name="a"/> to <paramref name="b"/>, both ends included.</summary>
    private static IEnumerable<LatLon> Along(LatLon a, LatLon b)
    {
        double lengthFt = GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;
        int steps = Math.Max(1, (int)Math.Ceiling(lengthFt / 2.0));
        for (int i = 0; i <= steps; i++)
        {
            double f = (double)i / steps;
            yield return new LatLon(a.Lat + (f * (b.Lat - a.Lat)), a.Lon + (f * (b.Lon - a.Lon)));
        }
    }

    internal static AirportGroundLayout? LoadSfo()
    {
        TestVnasData.EnsureInitialized();
        return new TestAirportGroundData().GetLayout("SFO");
    }
}

/// <summary>
/// Serialized test collection for the tug planner's wall-clock budget: a timing measured while the rest of the assembly
/// runs its collections in parallel (<c>xunit.runner.json</c>) measures the machine's load, not the planner.
/// </summary>
[CollectionDefinition("TugPlanTiming", DisableParallelization = true)]
public sealed class TugPlanTimingCollection;

/// <summary>What the alley clearance check costs the planner once the layout's work is cached.</summary>
[Collection("TugPlanTiming")]
public class TugAlleyClearanceTimingTests(ITestOutputHelper output)
{
    /// <summary>The most a plan may take once the layout's clearance work is cached, milliseconds.</summary>
    private const double WarmPlanBudgetMs = 100.0;

    /// <summary>
    /// What the clearance check costs at SFO: the first F8 → 7A plan pays for the layout's protected centreline pieces,
    /// their clipping and the taxiways' design groups; planning the same push again reuses all of it and takes under
    /// <see cref="WarmPlanBudgetMs"/>.
    /// </summary>
    [Fact]
    public void AlleyPushPlan_SecondPlanReusesLayoutWork_Under100ms()
    {
        if (TugAlleyClearanceTests.LoadSfo() is not { } layout)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        GroundNode stand = layout.FindParkingByName("F8") ?? throw new InvalidOperationException("no gate F8");
        GroundNode spot = layout.FindSpotNodeByName("7A") ?? throw new InvalidOperationException("no spot 7A");
        TugRequest request = TugAlleyClearanceTests.StandStart(stand, TugAlleyClearanceTests.Regional, TugGoal.Spot(spot));

        var watch = Stopwatch.StartNew();
        TugPlan? cold = TugMovePlanner.Plan(layout, request, out string coldRefusal);
        double coldMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        TugPlan? warm = TugMovePlanner.Plan(layout, request, out string warmRefusal);
        double warmMs = watch.Elapsed.TotalMilliseconds;

        output.WriteLine($"F8 → 7A ({TugAlleyClearanceTests.Regional}) planned in {coldMs:F1} ms cold, {warmMs:F1} ms warm");
        Assert.True(cold is not null, $"the cold plan was refused: {coldRefusal}");
        Assert.True(warm is not null, $"the warm plan was refused: {warmRefusal}");
        Assert.True(warmMs < WarmPlanBudgetMs, $"the warm plan took {warmMs:F1} ms (cold {coldMs:F1} ms)");
    }
}
