using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// An offset runway pair is crossed straight over and its turn measured on the path flown, not along the centreline: lines meeting
/// at a usable angle continue to their meeting point (KCOS A2/C2, one 59.6° corner), near-parallel lines take a set-back chord
/// (CMH E4/F3 at a 45° larger corner, OAK D/F capped at the 75 ft runway's edge). The crossing's two real bars bracket the leg.
/// </summary>
[Collection("Acceptance")]
public class StraightOverCrossingTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyList<CrossingPair> CosPair = [new CrossingPair("A2", "C2", "17R/35L", "declared by the test")];

    private static AirportGroundLayout? Layout(string airport)
    {
        TestVnasData.EnsureInitialized();
        return new TestAirportGroundData().GetLayout(airport);
    }

    private static double Ft(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    private static List<TaxiRouteSegment> VirtualSegments(TaxiRoute route) => [.. route.Segments.Where(s => VirtualNode.IsVirtualEdge(s.Edge.Edge))];

    private void InitLog() => SimLogBuilder.CreateForTest(output).InitializeSimLog();

    private static double OffCentrelineFt(AirportGroundLayout layout, string runway, GroundNode node) =>
        AirportGroundLayout.CrossTrackFromCenterlineFt(
            RunwayCrossingDetector.BuildRunwayRectangle(Assert.IsType<GroundRunway>(layout.FindRunway(runway))),
            node
        );

    private static void AssertNear(double expected, double actual, double tolerance) =>
        Assert.InRange(actual, expected - tolerance, expected + tolerance);

    /// <summary>The figures of each pair's leg, measured on the real layouts.</summary>
    [Theory]
    [InlineData("COS", "A2", "C2", "Continue", 59.6, -1.0, 0.0)]
    [InlineData("COS", "C2", "A2", "Continue", 59.6, -1.0, 0.0)]
    [InlineData("CMH", "E4", "F3", "Chord", 45.0, 42.2, 55.7)]
    [InlineData("CMH", "F3", "E4", "Chord", 42.2, 45.0, 55.7)]
    [InlineData("OAK", "D", "F", "Chord", 54.28, 52.89, 37.46)]
    [InlineData("OAK", "F", "D", "Chord", 52.89, 54.28, 37.46)]
    public void StraightOverGeometry_MatchesTheMeasuredFigures(
        string airport,
        string fromTaxiway,
        string toTaxiway,
        string kind,
        double firstCorner,
        double secondCorner,
        double setBackFt
    )
    {
        if (Layout(airport) is not { } layout)
        {
            return;
        }

        StraightOver over = Assert.IsType<StraightOver>(SegmentExpander.FindStraightOver(layout, fromTaxiway, toTaxiway, CosPair));
        output.WriteLine($"{fromTaxiway}→{toTaxiway}: not built: {over.Reason}");
        StraightOverLeg leg = Assert.IsType<StraightOverLeg>(over.Leg);
        output.WriteLine($"{leg.Kind} #{leg.Entry.Id}→#{leg.Landing.Id} corners {string.Join("/", leg.CornersDeg)} set back {leg.SetBackFt}");

        Assert.Equal(kind, leg.Kind.ToString());
        AssertNear(firstCorner, leg.CornersDeg[0], 0.1);
        if (secondCorner >= 0.0)
        {
            AssertNear(secondCorner, leg.CornersDeg[1], 0.1);
        }

        AssertNear(setBackFt, leg.SetBackFt, 0.1);
    }

    /// <summary>The table's meeting points: KCOS's P lies 150 ft off the centreline, 454 ft from C2's bar and 491 ft from A2's.</summary>
    [Fact]
    public void StraightOverGeometry_CosMeetingPointLiesBetweenTheBars()
    {
        if (Layout("COS") is not { } layout)
        {
            return;
        }

        StraightOverLeg leg = Assert.IsType<StraightOverLeg>(SegmentExpander.FindStraightOver(layout, "A2", "C2", CosPair)?.Leg);
        GroundNode meet = Assert.Single(leg.Edges.Select(e => e.ToNode), n => n.Id < 0);
        AssertNear(150.0, OffCentrelineFt(layout, "35L", meet), 3.0);
        AssertNear(454.0, Ft(meet.Position, GroundNodeLookup.HoldShortBar(layout, "35L", "C2").Position), 3.0);
        AssertNear(491.0, Ft(meet.Position, GroundNodeLookup.HoldShortBar(layout, "35L", "A2").Position), 3.0);
    }

    /// <summary>A B738 the 150° reversal off the centreline used to refuse continues A2 to its meeting point with C2 and turns once, 59.6°.</summary>
    [Fact]
    public void StraightOver_CosA2C2_B738ContinuesA2ToC2WithOneTurn()
    {
        if ((Layout("COS") is not { } layout) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        InitLog();
        using IDisposable _ = NavigationDatabase.ScopedOverride(Issue899DetourConnectorTests.CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        GroundNode start = FarthestOn(layout, "A2", GroundNodeLookup.HoldShortBar(layout, "35L", "A2"));
        GroundNode a2Bar = GroundNodeLookup.HoldShortBar(layout, "35L", "A2");
        GroundNode c2Bar = GroundNodeLookup.HoldShortBar(layout, "35L", "C2");
        TaxiRoute route = Taxi(new(layout, "COS", start, a2Bar, "B738"), ["A2", "C2", "C"], "Taxi via A2 C2 C");

        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
        List<TaxiRouteSegment> leg = VirtualSegments(route);
        Assert.Equal(2, leg.Count);
        Assert.Equal(c2Bar.Id, leg[1].ToNodeId);
        Assert.Equal("A2", leg[0].TaxiwayName);
        Assert.Equal("C2", leg[1].TaxiwayName);
        double inbound = GeoMath.BearingTo(leg[0].Edge.FromNode.Position, leg[0].Edge.ToNode.Position);
        double outbound = GeoMath.BearingTo(leg[1].Edge.FromNode.Position, leg[1].Edge.ToNode.Position);
        AssertNear(59.6, RouteCostFunction.HeadingDelta(inbound, outbound), 0.2);
        Assert.Equal(a2Bar.Id, Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.RunwayCrossing).NodeId);
    }

    /// <summary>The reverse leaves C2 at the meeting point, 454 ft past C2's bar, and lands on A2's join: C2's join is never reached.</summary>
    [Fact]
    public void StraightOver_CosC2A2_LeavesC2BeforeItsJoin()
    {
        if ((Layout("COS") is not { } layout) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        InitLog();
        using IDisposable _ = NavigationDatabase.ScopedOverride(Issue899DetourConnectorTests.CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        GroundNode c2Bar = GroundNodeLookup.HoldShortBar(layout, "35L", "C2");
        GroundNode start = FarthestOn(layout, "C2", GroundNodeLookup.HoldShortBar(layout, "35L", "A2"));
        TaxiRoute route = Taxi(new(layout, "COS", start, c2Bar, "B738"), ["C2", "A2", "A"], "Taxi via C2 A2 A");

        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
        List<TaxiRouteSegment> leg = VirtualSegments(route);
        Assert.Equal(2, leg.Count);
        Assert.Equal(c2Bar.Id, leg[0].FromNodeId);
        AssertNear(454.0, Ft(c2Bar.Position, leg[0].Edge.ToNode.Position), 3.0);
        Assert.True(leg[1].Edge.ToNode.Edges.Exists(e => e.IsRunwayCenterline), $"#{leg[1].ToNodeId} is not A2's join");
        Assert.Equal(c2Bar.Id, Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.RunwayCrossing).NodeId);
    }

    /// <summary>
    /// CMH E4→F3, 2.8° apart: a chord set back 56 ft, its larger corner 45°, its ends 67 and 65 ft off the centreline, on no
    /// centreline and no link.
    /// </summary>
    [Theory]
    [InlineData("C172")]
    [InlineData("B738")]
    public void StraightOver_CmhE4F3_ChordSetBack56Ft(string aircraftType)
    {
        if (Layout("CMH") is not { } layout)
        {
            return;
        }

        InitLog();
        GroundNode start = GroundNodeLookup.Junction(layout, "E", "E4");
        GroundNode e4Bar = GroundNodeLookup.HoldShortBar(layout, "10L", "E4", start);
        GroundNode f3Bar = GroundNodeLookup.HoldShortBar(layout, "10L", "F3", start);
        TaxiRoute route = Taxi(new(layout, "CMH", start, e4Bar, aircraftType), ["E4", "F3"], "Taxi via E4 F3");

        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline || s.TaxiwayName.Contains(":link", StringComparison.Ordinal));
        List<TaxiRouteSegment> leg = VirtualSegments(route);
        Assert.True(leg.Count >= 3, $"{leg.Count} virtual segments");
        Assert.Equal(e4Bar.Id, leg[0].FromNodeId);
        Assert.Equal(f3Bar.Id, leg[2].ToNodeId);
        AssertNear(66.9, OffCentrelineFt(layout, "10L", leg[0].Edge.ToNode), 0.5);
        AssertNear(65.4, OffCentrelineFt(layout, "10L", leg[1].Edge.ToNode), 0.5);
        AssertNear(179.0, Ft(leg[1].Edge.FromNode.Position, leg[1].Edge.ToNode.Position), 1.0);
        Assert.Equal(["E4", "E4", "F3"], leg.Take(3).Select(s => s.TaxiwayName));
    }

    /// <summary>F3→E4's lines meet 2,500 ft ahead, past E4's bar: near-parallel lines take the chord, never a false "no room".</summary>
    [Fact]
    public void StraightOver_CmhF3E4_ConvergingNearParallel_TakesTheChord()
    {
        if (Layout("CMH") is not { } layout)
        {
            return;
        }

        InitLog();
        GroundNode e4Bar = GroundNodeLookup.HoldShortBar(layout, "10L", "E4", GroundNodeLookup.Junction(layout, "E", "E4"));
        GroundNode f3Bar = GroundNodeLookup.HoldShortBar(layout, "10L", "F3", e4Bar);
        GroundNode start = FarthestOn(layout, "F3", e4Bar);
        TaxiRoute route = Taxi(new(layout, "CMH", start, f3Bar, "C172"), ["F3", "E4"], "Taxi via F3 E4");

        List<TaxiRouteSegment> leg = VirtualSegments(route);
        Assert.Equal(f3Bar.Id, leg[0].FromNodeId);
        Assert.Equal(e4Bar.Id, leg[2].ToNodeId);
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
    }

    /// <summary>
    /// OAK D/F over the 75 ft 15/33: the chord's points stop 37.46 ft out, at the runway's edge, both ways, and each lies inside
    /// the runway's rectangle lengthwise, F's join being the runway's end node.
    /// </summary>
    [Theory]
    [InlineData("D", "F", "J", "D", "F")]
    [InlineData("F", "D", "J", "F", "D")]
    public void StraightOver_OakDF_ChordCappedAtTheRunwayEdge(string fromTaxiway, string toTaxiway, string onward, string firstName, string lastName)
    {
        if (Layout("OAK") is not { } layout)
        {
            return;
        }

        InitLog();
        StraightOverLeg leg = Assert.IsType<StraightOverLeg>(SegmentExpander.FindStraightOver(layout, fromTaxiway, toTaxiway, [])?.Leg);
        Assert.Equal(StraightOverKind.Chord, leg.Kind);
        AssertNear(37.46, leg.SetBackFt, 0.01);
        Assert.Equal([firstName, firstName, lastName], leg.Edges.Select(e => e.TaxiwayName));

        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(Assert.IsType<GroundRunway>(layout.FindRunway("33")));
        double halfWidthFt = rect.HalfWidthNm * GeoMath.FeetPerNm;
        AssertNear(37.5, halfWidthFt, 0.001);
        foreach (GroundNode via in leg.Edges.Select(e => e.FromNode).Where(n => n.Id > 0).Concat([leg.Landing]))
        {
            output.WriteLine($"{onward}: real node #{via.Id} {OffCentrelineFt(layout, "33", via):F2} ft off the centreline");
        }

        foreach (GroundNode point in leg.Edges.Take(2).Select(e => e.ToNode))
        {
            double alongFt =
                GeoMath.AlongTrackDistanceNmRaw(point.Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading.Degrees) * GeoMath.FeetPerNm;
            double lengthFt = rect.LengthNm * GeoMath.FeetPerNm;
            double offFt = OffCentrelineFt(layout, "33", point);
            output.WriteLine($"{onward}: chord point {point.Position} along {alongFt:F1} ft of {lengthFt:F1} ft, {offFt:F2} ft off the centreline");
            Assert.InRange(alongFt, 0.0, lengthFt);
            AssertNear(halfWidthFt, offFt, 0.1);
            Assert.True(offFt <= halfWidthFt + 0.01, $"chord point {offFt} ft off the centreline is past the edge");
        }
    }

    /// <summary>
    /// The other near-parallel pairs in the test airports take the chord too: from the leg's entry each resolves through the leg's
    /// own points and reads back as the two taxiways.
    /// </summary>
    [Theory]
    [InlineData("CMH", "E5", "F4")]
    [InlineData("CMH", "F4", "E5")]
    [InlineData("MSY", "A", "G3")]
    [InlineData("MSY", "G3", "A")]
    public void StraightOver_OtherNearParallelPairs_ResolveAndReadBack(string airport, string fromTaxiway, string toTaxiway)
    {
        if (Layout(airport) is not { } layout)
        {
            return;
        }

        InitLog();
        StraightOverLeg leg = Assert.IsType<StraightOverLeg>(SegmentExpander.FindStraightOver(layout, fromTaxiway, toTaxiway, [])?.Leg);
        Assert.Equal(StraightOverKind.Chord, leg.Kind);
        TaxiRoute route = Taxi(new(layout, airport, leg.Entry, leg.Landing, "C172"), [fromTaxiway, toTaxiway], $"Taxi via {fromTaxiway} {toTaxiway}");
        Assert.Equal(leg.Edges.Select(e => e.ToNodeId), VirtualSegments(route).Take(3).Select(s => s.ToNodeId));
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
    }

    /// <summary>A probe and the top level resolve one leg: every attempt logs the same corners and mints the same virtual nodes.</summary>
    [Fact]
    public void StraightOver_ProbesAndTopLevelAgree()
    {
        if (Layout("OAK") is not { } layout)
        {
            return;
        }

        InitLog();
        GroundNode old1 = Assert.IsType<GroundNode>(layout.FindParkingByName("OLD1"));
        GroundNode bar = Issue1334OakDToFAcross33Tests.OakE28RBar(layout);
        GroundNode start = GroundNodeLookup.OnTaxiway(layout, "E", bar, 1.0, GroundNodeLookup.Junction(layout, "C", "E"));
        var diag = new List<string>();
        TaxiRoute? viaProbe = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            ["C", "D", "F"],
            out string? failReason,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = "E",
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("C172"),
                DestinationHintNode = old1,
                StartHeadingTrue = GeoMath.BearingTo(bar.Position, start.Position),
                DiagnosticLog = diag.Add,
            },
            new TaxiClass(AircraftCategory.Piston, WakeTurbulenceData.WakeClass.Small)
        );
        Assert.True(viaProbe is not null, failReason);

        List<string> legLines = [.. diag.Where(l => l.StartsWith("[runway-crossing] D→F straight-over ", StringComparison.Ordinal)).Distinct()];
        Assert.Equal("[runway-crossing] D→F straight-over Chord", string.Join(" ", Assert.Single(legLines).Split(' ').Take(4)));
        Assert.True(diag.Count(l => l.StartsWith("[runway-crossing] D→F straight-over ", StringComparison.Ordinal)) >= 2, "no probe reached the leg");

        StraightOverLeg leg = Assert.IsType<StraightOverLeg>(SegmentExpander.FindStraightOver(layout, "D", "F", [])?.Leg);
        Assert.Equal(leg.Edges.Select(e => e.ToNodeId), VirtualSegments(viaProbe).Take(3).Select(s => s.ToNodeId));
    }

    /// <summary>
    /// An aircraft standing on the entry bar holds there, the far bar its exit pair: a chord touches no centreline node, so
    /// without the chord counting as a traversal the far bar became the entry and the aircraft crossed unheld.
    /// </summary>
    [Fact]
    public void StraightOver_RouteStartingOnTheEntryBar_PairsTheFarBar()
    {
        if ((Layout("CMH") is not { } cmh) || (Layout("COS") is not { } cos) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        InitLog();
        GroundNode e4Junction = GroundNodeLookup.Junction(cmh, "E", "E4");
        GroundNode e4Bar = GroundNodeLookup.HoldShortBar(cmh, "10L", "E4", e4Junction);
        GroundNode f3Bar = GroundNodeLookup.HoldShortBar(cmh, "10L", "F3", e4Junction);
        TaxiRoute cmhRoute = Taxi(new(cmh, "CMH", e4Bar, f3Bar, "C172"), ["E4", "F3"], "Taxi via E4 F3");
        Assert.Equal(e4Bar.Id, Assert.Single(cmhRoute.HoldShortPoints, h => h.Reason == HoldShortReason.RunwayCrossing).NodeId);

        using IDisposable _ = NavigationDatabase.ScopedOverride(Issue899DetourConnectorTests.CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        GroundNode c2Bar = GroundNodeLookup.HoldShortBar(cos, "35L", "C2");
        GroundNode a2Bar = GroundNodeLookup.HoldShortBar(cos, "35L", "A2");
        TaxiRoute cosRoute = Taxi(new(cos, "COS", c2Bar, a2Bar, "C172"), ["C2", "A2", "A"], "Taxi via C2 A2 A");
        Assert.Equal(c2Bar.Id, Assert.Single(cosRoute.HoldShortPoints, h => h.Reason == HoldShortReason.RunwayCrossing).NodeId);
    }

    /// <summary>
    /// A clearance with no destination is cut short of its last taxiway. Past the far bar of a chord that cut stands: it never
    /// moves back to the entry bar, though the chord touches no centreline node.
    /// </summary>
    [Fact]
    public void StraightOver_NoDestinationCutPastTheFarBar_CrossesTheChord()
    {
        if (Layout("CMH") is not { } layout)
        {
            return;
        }

        InitLog();
        GroundNode start = GroundNodeLookup.Junction(layout, "E", "E4");
        GroundNode e4Bar = GroundNodeLookup.HoldShortBar(layout, "10L", "E4", start);
        GroundNode f3Bar = GroundNodeLookup.HoldShortBar(layout, "10L", "F3", start);
        TaxiRoute route = Taxi(new(layout, "CMH", start, e4Bar, "C172"), ["E4", "F3", "F"], "Taxi via E4 F3 F");

        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
        Assert.Contains(route.Segments, s => s.ToNodeId == f3Bar.Id);
        Assert.Equal(e4Bar.Id, Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.RunwayCrossing).NodeId);
    }

    /// <summary>
    /// Lines 50° apart meeting 331 ft along Y, past its bar at 200 ft: no leg continues to the meeting point, so the crossing takes
    /// the chord, set back to the runway's edge.
    /// </summary>
    [Fact]
    public void StraightOverGeometry_LinesMeetPastTheFarBar_TakesTheChord()
    {
        AirportGroundLayout layout = CrossingField(inboundDeg: 155.0, outwardDeg: 205.0, barFt: 200.0);

        StraightOver over = Assert.IsType<StraightOver>(SegmentExpander.FindStraightOver(layout, "X", "Y", []));
        output.WriteLine($"{over.Leg?.Kind} corners {string.Join("/", over.Leg?.CornersDeg ?? [])} set back {over.Leg?.SetBackFt}: {over.Reason}");

        StraightOverLeg leg = Assert.IsType<StraightOverLeg>(over.Leg);
        Assert.Equal(StraightOverKind.Chord, leg.Kind);
        Assert.Equal(2, leg.CornersDeg.Count);
        AssertNear(82.8, leg.SetBackFt, 0.5);
    }

    /// <summary>The same lines with Y's bar at 400 ft land short of it: the chord is the bar's doing, not the angle's.</summary>
    [Fact]
    public void StraightOverGeometry_LinesMeetShortOfTheFarBar_IsALeg()
    {
        AirportGroundLayout layout = CrossingField(inboundDeg: 155.0, outwardDeg: 205.0, barFt: 400.0);

        StraightOver over = Assert.IsType<StraightOver>(SegmentExpander.FindStraightOver(layout, "X", "Y", []));

        Assert.Null(over.Reason);
        Assert.Equal(StraightOverKind.Continue, over.Leg?.Kind);
    }

    /// <summary>
    /// X and Y both leave the runway 30° off it, so the corners between their joins are already 30°: the chord runs between the
    /// joins themselves: a zero set-back chord, one virtual edge named for Y between the two joins.
    /// </summary>
    [Fact]
    public void StraightOverGeometry_JoinsWithinTheChordCorner_TakeOneChordBetweenThem()
    {
        AirportGroundLayout layout = CrossingField(inboundDeg: 120.0, outwardDeg: 120.0, barFt: 400.0);
        CrossingPair declared = new("X", "Y", "09/27", "declared by the test");

        StraightOver over = Assert.IsType<StraightOver>(SegmentExpander.FindStraightOver(layout, "X", "Y", [declared]));
        output.WriteLine($"not built: {over.Reason}");

        StraightOverLeg leg = Assert.IsType<StraightOverLeg>(over.Leg);
        Assert.Equal(StraightOverKind.Chord, leg.Kind);
        Assert.Equal(0.0, leg.SetBackFt);
        DirectionalEdge chord = Assert.Single(leg.Edges);
        Assert.Equal((FieldXJoin, FieldYJoin, "Y"), (chord.FromNode.Id, chord.ToNode.Id, chord.TaxiwayName));
        Assert.Equal(2, leg.CornersDeg.Count);
        AssertNear(30.0, leg.CornersDeg[0], 0.1);
        AssertNear(30.0, leg.CornersDeg[1], 0.1);
    }

    /// <summary>
    /// X forks at its join, square to the runway toward its bar and 20° off the runway's line along a spur with no bar: the line is
    /// the square edge, not the one leaning away from Y's join.
    /// </summary>
    [Fact]
    public void StraightOverGeometry_FirstLineIsTheEdgeSquareToTheRunway()
    {
        AirportGroundLayout layout = CrossingField(inboundDeg: 180.0, outwardDeg: 180.0, barFt: 400.0);
        GroundNode xJoin = layout.Nodes[FieldXJoin];
        var spurEnd = new GroundNode
        {
            Id = 9,
            Position = Along(xJoin.Position, 290.0, 300.0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        layout.Nodes[spurEnd.Id] = spurEnd;
        layout.Edges.Add(
            new GroundEdge
            {
                Nodes = [xJoin, spurEnd],
                TaxiwayName = "X",
                DistanceNm = GeoMath.DistanceNm(xJoin.Position, spurEnd.Position),
            }
        );
        layout.RebuildAdjacencyLists();
        CrossingPair declared = new("X", "Y", "09/27", "declared by the test");

        StraightOver over = Assert.IsType<StraightOver>(SegmentExpander.FindStraightOver(layout, "X", "Y", [declared]));
        output.WriteLine($"not built: {over.Reason}");

        StraightOverLeg leg = Assert.IsType<StraightOverLeg>(over.Leg);
        Assert.Equal(FieldXBar, leg.Entry.Id);
    }

    /// <summary>
    /// X flown on 255° and Y leaving square on 180°, their lines meeting 290 ft out along X, past its bar at 250 ft: the chord can
    /// set back only 75 ft, to the runway's edge on Y, leaving a 141° corner off X. Past a jet's 135°, the jet's crossing falls back
    /// to today's route along the centreline, never a turn refusal of the chord; here that route's 165° turn onto the centreline
    /// leaves the jet no route at all, since a chord's corners are never wider than the centreline's turns. A helicopter's 175°
    /// admits the chord.
    /// </summary>
    [Fact]
    public void StraightOver_ChordCornerBeyondTheTurnLimit_FallsBackFromTheChord()
    {
        if (TestVnasData.NavigationDb is not { } navDb)
        {
            return;
        }

        InitLog();
        AirportGroundLayout layout = CrossingField(inboundDeg: 255.0, outwardDeg: 180.0, barFt: 250.0);
        CrossingPair declared = new("X", "Y", "09/27", "declared by the test");
        var sidecars = new AirportSidecarCatalog([new AirportSidecar("KTST") { CrossingPairs = [declared] }]);
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb.WithAirportSidecars(sidecars));
        StraightOverLeg leg = Assert.IsType<StraightOverLeg>(SegmentExpander.FindStraightOver(layout, "X", "Y", [declared])?.Leg);
        output.WriteLine($"{leg.Kind} corners {string.Join("/", leg.CornersDeg)} set back {leg.SetBackFt}");
        Assert.Equal(StraightOverKind.Chord, leg.Kind);
        Assert.InRange(leg.MaxCornerDeg, CategoryLimits.MaxHeadingChangeDeg(AircraftCategory.Jet), 175.0);

        var diag = new List<string>();
        TaxiRoute? jet = ResolveOverField(layout, AircraftCategory.Jet, diag, out string? jetRefusal);
        output.WriteLine($"jet: {jetRefusal}");
        Assert.Contains(diag, l => l.StartsWith("[runway-crossing] X→Y straight-over not built: the chord's", StringComparison.Ordinal));
        Assert.DoesNotContain(diag, l => l.StartsWith("[runway-crossing] X→Y refused:", StringComparison.Ordinal));
        Assert.NotEqual(SegmentExpander.CannotMakeTurnReason("X", "Y"), jetRefusal);
        Assert.Empty(jet is null ? [] : VirtualSegments(jet));

        TaxiRoute? helicopter = ResolveOverField(layout, AircraftCategory.Helicopter, [], out string? helicopterRefusal);
        Assert.True(helicopter is not null, helicopterRefusal);
        Assert.Equal(leg.Edges.Select(e => e.ToNodeId), VirtualSegments(helicopter).Take(3).Select(s => s.ToNodeId));
    }

    /// <summary>
    /// The lines meet on the runway at a 150° corner: past a jet's 135°, inside a helicopter's 175°. The turn is judged per
    /// category on the geometry every aircraft shares.
    /// </summary>
    [Fact]
    public void StraightOverGeometry_MeetsAheadBeyondTheTurnLimit_IsCannotMakeTurn()
    {
        if (TestVnasData.NavigationDb is not { } navDb)
        {
            return;
        }

        AirportGroundLayout layout = CrossingField(inboundDeg: 100.0, outwardDeg: 250.0, barFt: 1000.0);
        CrossingPair declared = new("X", "Y", "09/27", "declared by the test");
        var sidecars = new AirportSidecarCatalog([new AirportSidecar("KTST") { CrossingPairs = [declared] }]);
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb.WithAirportSidecars(sidecars));
        StraightOverLeg leg = Assert.IsType<StraightOverLeg>(SegmentExpander.FindStraightOver(layout, "X", "Y", [declared])?.Leg);
        Assert.Equal(StraightOverKind.Continue, leg.Kind);
        AssertNear(150.0, leg.MaxCornerDeg, 0.1);

        Assert.Null(ResolveOverField(layout, AircraftCategory.Jet, [], out string? jetRefusal));
        Assert.Equal(SegmentExpander.CannotMakeTurnReason("X", "Y"), jetRefusal);

        TaxiRoute? helicopter = ResolveOverField(layout, AircraftCategory.Helicopter, [], out string? helicopterRefusal);
        Assert.True(helicopter is not null, helicopterRefusal);
        Assert.Equal(leg.Edges.Select(e => e.ToNodeId), VirtualSegments(helicopter).Take(2).Select(s => s.ToNodeId));
    }

    /// <summary>
    /// TAXI X Y where the lines meet past Y's bar is no refusal, whatever the aircraft: it crosses on the chord, the leg's own points,
    /// and touches no centreline.
    /// </summary>
    [Theory]
    [InlineData("C172")]
    [InlineData("B738")]
    public void LinesMeetPastTheFarBar_TaxiCrossesOnTheChord(string aircraftType)
    {
        InitLog();
        AirportGroundLayout layout = CrossingField(inboundDeg: 155.0, outwardDeg: 205.0, barFt: 200.0);
        StraightOverLeg leg = Assert.IsType<StraightOverLeg>(SegmentExpander.FindStraightOver(layout, "X", "Y", [])?.Leg);

        TaxiRoute route = Taxi(new(layout, "TST", layout.Nodes[FieldXFar], layout.Nodes[FieldXBar], aircraftType), ["X", "Y"], "Taxi via X Y");

        Assert.Equal(leg.Edges.Select(e => e.ToNodeId), VirtualSegments(route).Take(3).Select(s => s.ToNodeId));
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
    }

    /// <summary>
    /// A snapshot taken on the leg's second virtual segment, a chord at CMH and the corner's far side at KCOS, restores the same
    /// virtual nodes, ids and positions, and snapshots again byte for byte.
    /// </summary>
    [Fact]
    public void StraightOver_SnapshotMidLeg_RestoresByteForByte()
    {
        if ((Layout("CMH") is not { } cmh) || (Layout("COS") is not { } cos) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        InitLog();
        GroundNode e4Junction = GroundNodeLookup.Junction(cmh, "E", "E4");
        GroundNode e4Bar = GroundNodeLookup.HoldShortBar(cmh, "10L", "E4", e4Junction);
        AssertLegRestores(Taxi(new(cmh, "CMH", e4Junction, e4Bar, "C172"), ["E4", "F3"], "Taxi via E4 F3"), cmh);

        using IDisposable _ = NavigationDatabase.ScopedOverride(Issue899DetourConnectorTests.CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        GroundNode a2Bar = GroundNodeLookup.HoldShortBar(cos, "35L", "A2");
        AssertLegRestores(Taxi(new(cos, "COS", FarthestOn(cos, "A2", a2Bar), a2Bar, "B738"), ["A2", "C2", "C"], "Taxi via A2 C2 C"), cos);
    }

    private static void AssertLegRestores(TaxiRoute route, AirportGroundLayout layout)
    {
        TaxiRouteSegment onLeg = VirtualSegments(route)[1];
        int index = route.Segments.IndexOf(onLeg);
        route.CurrentSegmentIndex = index;

        TaxiRouteDto dto = route.ToSnapshot();
        TaxiRoute restored = Assert.IsType<TaxiRoute>(TaxiRoute.FromSnapshot(dto, layout));

        Assert.Equal(JsonSerializer.Serialize(dto), JsonSerializer.Serialize(restored.ToSnapshot()));
        Assert.Equal(route.Segments.Select(s => s.ToNodeId), restored.Segments.Select(s => s.ToNodeId));
        Assert.Equal(onLeg.Edge.FromNode.Position, restored.Segments[index].Edge.FromNode.Position);
        Assert.Equal(onLeg.Edge.ToNode.Position, restored.Segments[index].Edge.ToNode.Position);
        Assert.Equal(index, restored.CurrentSegmentIndex);
    }

    /// <summary>On the leg the aircraft is across the runway's axis, on its pavement: a crossing, not a taxi along the runway.</summary>
    [Theory]
    [InlineData("CMH", "E4", "F3", "10L", 1, 0.5)]
    [InlineData("OAK", "D", "F", "33", 1, 0.5)]
    [InlineData("COS", "A2", "C2", "35L", 0, 0.25)]
    public void StraightOver_OnTheLeg_ClassifiesAsCrossing(
        string airport,
        string fromTaxiway,
        string toTaxiway,
        string end,
        int edge,
        double fraction
    )
    {
        if (Layout(airport) is not { } layout)
        {
            return;
        }

        StraightOverLeg leg = Assert.IsType<StraightOverLeg>(SegmentExpander.FindStraightOver(layout, fromTaxiway, toTaxiway, CosPair)?.Leg);
        (LatLon from, LatLon to) = (leg.Edges[edge].FromNode.Position, leg.Edges[edge].ToNode.Position);
        double headingDeg = GeoMath.BearingTo(from, to);
        var aircraft = new AircraftState
        {
            Callsign = Issue1334OakDToFAcross33Tests.Callsign,
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(from, new TrueHeading(headingDeg), GeoMath.DistanceNm(from, to) * fraction),
            TrueHeading = new TrueHeading(headingDeg),
            IsOnGround = true,
        };
        RunwayInfo runway = Assert.Single(RunwayOccupancy.AirportRunways(airport), r => r.Id.Contains(end));

        Assert.Equal(RunwayUseKind.Crossing, RunwayOccupancy.ClassifyByGeometry(aircraft, runway, layout));
    }

    /// <summary>
    /// KCOS's leg turns at P, 150 ft off the centreline and off the runway's pavement, and runs on along C2's line to its bar. A
    /// B738 cleared TAXI A2 C2 and CROSS 35L meets both "clear of the runway" definitions there (docs/runway-fouling.md): while
    /// its position is on the runway's pavement it is on the runway (Crossing or OnSurface); once off the pavement and it
    /// moves on unrestricted it is clear for the controller (P/CG b, AIM 4-3-21.b), yet still vacating for the pilot's rule
    /// (AIM 2-3-5.a.1) until its tail passes C2's bar.
    /// </summary>
    [Fact]
    public void StraightOver_KcosOffPavementStretch_FollowsBothClearDefinitions()
    {
        if ((Layout("COS") is not { } layout) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        InitLog();
        using IDisposable _ = NavigationDatabase.ScopedOverride(Issue899DetourConnectorTests.CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        GroundNode a2Bar = GroundNodeLookup.HoldShortBar(layout, "35L", "A2");
        GroundNode c2Bar = GroundNodeLookup.HoldShortBar(layout, "35L", "C2");
        GroundNode start = FarthestOn(layout, "A2", a2Bar);
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(
            layout,
            "COS",
            start,
            GeoMath.BearingTo(start.Position, a2Bar.Position)
        );
        aircraft.AircraftType = "B738";
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-yaat-943-cos-off-pavement",
            ScenarioName = "COS A2/C2 straight over 17R/35L",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "COS",
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
        };

        CommandResult taxi = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "TAXI A2 C2");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        int stretch = route.Segments.FindIndex(s => (s.FromNodeId < 0) && (s.ToNodeId == c2Bar.Id));
        Assert.True(stretch >= 0, "the route has no P→C2 bar stretch");
        GroundNode meet = route.Segments[stretch].Edge.FromNode;
        AssertNear(150.0, OffCentrelineFt(layout, "35L", meet), 3.0);

        Assert.IsType<HoldingShortPhase>(
            Issue1334OakDToFAcross33Tests.TickUntil(output, engine, ac => ac.Phases?.CurrentPhase is HoldingShortPhase, 900)?.Phases?.CurrentPhase
        );
        CommandResult cross = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "CROSS 35L");
        Assert.True(cross.Success, cross.Message);

        RunwayInfo runway = Assert.Single(RunwayOccupancy.AirportRunways("COS"), r => r.Id.Contains("35L"));
        var body = AircraftBody.Of("B738");
        double halfLengthFt = AircraftLength.ResolveFt("B738") / 2.0;
        double c2Bearing = GeoMath.BearingTo(meet.Position, c2Bar.Position);
        double barAlongFt = Ft(meet.Position, c2Bar.Position);
        (int onPavement, int clearForController, int vacatingForPilot) = (0, 0, 0);
        bool enteredRunway = false;
        string? wrong = null;
        bool tailPastBar = false;

        string Where(AircraftState ac, RunwayUseKind? kind, bool vacating) =>
            $"Classify {kind?.ToString() ?? "null"}, IsVacating {vacating} in {ac.Phases?.CurrentPhase?.GetType().Name ?? "no phase"}, "
            + $"{Ft(meet.Position, ac.Position):F0} ft from P, {Ft(c2Bar.Position, ac.Position):F0} ft from C2's bar, {ac.GroundSpeed:F1} kt";

        SubTickDriver.TickSubTicksUntil(
            engine,
            Issue1334OakDToFAcross33Tests.Callsign,
            _ => tailPastBar || (wrong is not null),
            300,
            ac =>
            {
                double alongFt = GeoMath.AlongTrackDistanceNmRaw(ac.Position, meet.Position, c2Bearing) * GeoMath.FeetPerNm;
                double offFt = Math.Abs(GeoMath.SignedCrossTrackDistanceNmRaw(ac.Position, meet.Position, c2Bearing)) * GeoMath.FeetPerNm;
                // Vacating ends inside the same tolerance of the bar as a square crossing's.
                tailPastBar = (alongFt > (barAlongFt + halfLengthFt - RunwayOccupancy.HoldLineFoulToleranceFt)) && (offFt < 30.0);
                bool onRunwayPavement = RunwayOccupancy.IsOnPavement(ac, runway);
                enteredRunway |= onRunwayPavement;
                if (!enteredRunway || tailPastBar)
                {
                    return;
                }

                RunwayUseKind? kind = RunwayOccupancy.Classify(ac, runway, layout)?.Kind;
                bool vacating = RunwayOccupancy.IsVacating(ac, body, runway, layout);
                if (onRunwayPavement)
                {
                    onPavement++;
                    wrong ??= (kind is RunwayUseKind.Crossing or RunwayUseKind.OnSurface) ? null : $"on the pavement: {Where(ac, kind, vacating)}";
                    return;
                }

                if (ac.GroundSpeed < 0.5)
                {
                    wrong ??= $"stopped off the pavement: {Where(ac, kind, vacating)}";
                    return;
                }

                clearForController += kind is null ? 1 : 0;
                vacatingForPilot += vacating ? 1 : 0;
                wrong ??= ((kind is null) && vacating) ? null : $"off the pavement, moving: {Where(ac, kind, vacating)}";
            }
        );

        output.WriteLine(
            $"on the pavement {onPavement}, clear for the controller {clearForController}, vacating {vacatingForPilot}: {wrong ?? "as defined"}"
        );
        Assert.Null(wrong);
        Assert.True(tailPastBar, "the tail never passed C2's bar");
        Assert.True(onPavement > 0, "no sample on the pavement");
        Assert.True(clearForController > 0, "no sample past the edge clear for the controller");
        Assert.True(vacatingForPilot > 0, "no sample past the edge still vacating for the pilot");
    }

    /// <summary>
    /// The controller rule on KCOS's P→C2-bar stretch (docs/runway-fouling.md): a B738 crossing 35L straight over, given HOLD
    /// 40 ft past P along C2's line, stops off the pavement with its tail still inside C2's marking. Leaving and stopped with its
    /// tail inside the far marking, it fouls the runway, although C2's bar lies more than 200 ft along the runway from it.
    /// </summary>
    [Fact]
    public void StraightOver_KcosHeldOnTheStretch_TailInsideC2sMarking_Fouls()
    {
        if ((Layout("COS") is not { } layout) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        InitLog();
        using IDisposable _ = NavigationDatabase.ScopedOverride(Issue899DetourConnectorTests.CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        GroundNode a2Bar = GroundNodeLookup.HoldShortBar(layout, "35L", "A2");
        GroundNode c2Bar = GroundNodeLookup.HoldShortBar(layout, "35L", "C2");
        GroundNode start = FarthestOn(layout, "A2", a2Bar);
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(
            layout,
            "COS",
            start,
            GeoMath.BearingTo(start.Position, a2Bar.Position)
        );
        aircraft.AircraftType = "B738";
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-yaat-943-cos-held-on-stretch",
            ScenarioName = "COS A2/C2 straight over 17R/35L, held on the stretch",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "COS",
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
        };

        CommandResult taxi = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "TAXI A2 C2");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        int stretch = route.Segments.FindIndex(s => (s.FromNodeId < 0) && (s.ToNodeId == c2Bar.Id));
        Assert.True(stretch >= 0, "the route has no P→C2 bar stretch");
        GroundNode meet = route.Segments[stretch].Edge.FromNode;
        Assert.IsType<HoldingShortPhase>(
            Issue1334OakDToFAcross33Tests.TickUntil(output, engine, ac => ac.Phases?.CurrentPhase is HoldingShortPhase, 900)?.Phases?.CurrentPhase
        );
        CommandResult cross = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "CROSS 35L");
        Assert.True(cross.Success, cross.Message);

        double c2Bearing = GeoMath.BearingTo(meet.Position, c2Bar.Position);
        double AlongFt(AircraftState ac) => GeoMath.AlongTrackDistanceNmRaw(ac.Position, meet.Position, c2Bearing) * GeoMath.FeetPerNm;
        Assert.NotNull(SubTickDriver.TickSubTicksUntil(engine, Issue1334OakDToFAcross33Tests.Callsign, ac => AlongFt(ac) > 40.0, 300, _ => { }));
        CommandResult hold = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "HOLD");
        Assert.True(hold.Success, hold.Message);
        Assert.NotNull(
            SubTickDriver.TickSubTicksUntil(
                engine,
                Issue1334OakDToFAcross33Tests.Callsign,
                ac => ac.GroundSpeed <= RunwayOccupancy.StoppedGroundSpeedKts,
                60,
                _ => { }
            )
        );

        RunwayInfo runway = Assert.Single(RunwayOccupancy.AirportRunways("COS"), r => r.Id.Contains("35L"));
        double tailAlongFt = AlongFt(aircraft) - (AircraftLength.ResolveFt("B738") / 2.0);
        string where =
            $"{AlongFt(aircraft):F0} ft past P, tail {Ft(meet.Position, c2Bar.Position) - tailAlongFt:F0} ft short of C2's bar, "
            + $"{aircraft.GroundSpeed:F1} kt in {aircraft.Phases?.CurrentPhase?.GetType().Name}";
        Assert.IsType<CrossingRunwayPhase>(aircraft.Phases?.CurrentPhase);
        Assert.False(RunwayOccupancy.IsOnPavement(aircraft, runway), where);
        Assert.True(tailAlongFt < (Ft(meet.Position, c2Bar.Position) - (2.0 * RunwayOccupancy.HoldLineFoulToleranceFt)), where);
        RunwayUseKind? kind = RunwayOccupancy.Classify(aircraft, runway, layout)?.Kind;
        Assert.True(kind == RunwayUseKind.Fouling, $"Classify {kind?.ToString() ?? "null"}, {where}");
    }

    /// <summary>
    /// A B738 cleared TAXI A2 C2 at KCOS, held at A2's 35L bar with a <c>FOLLOWG</c> armed behind it, crosses 35L straight over
    /// (<see cref="StageCosFollowgCrossing"/>); a HOLD 40 ft past P on C2's line breaks the follow off and a RES lets the crossing
    /// end at rest past C2's bar. Every second after the break-off the test records the tail's distance past C2's bar along the leg
    /// flown (C2's line from P) and along the near-bar → far-bar bearing, and the crossing's not-clear reading
    /// (<see cref="CrossingRunwayPhase.IsNotClearOfFarMarking"/>); at rest the answer is the break-off hold's
    /// <see cref="HoldingInPositionPhase.TailInsideRunway"/>. With the tail short of the bar along the leg flown the crossing reads
    /// not clear; once past it, both read clear: the crossing judges its tail against C2's own marking (YAAT-1637). Measured red
    /// with the bar bearing: not clear for 2 s with the tail 5.15 ft past the bar on the leg (−4.89 ft on the bearing, 10.05 ft
    /// apart), and at rest 9.28 ft past it, the tail inside 17R.
    /// </summary>
    [Fact]
    public void StraightOverCrossing_AfterFollowgBreakOff_ClearOfFarMarkingOnceTailPassesTheBar()
    {
        if ((Layout("COS") is not { } layout) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        InitLog();
        using IDisposable _ = NavigationDatabase.ScopedOverride(Issue899DetourConnectorTests.CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        const string follower = Issue1334OakDToFAcross33Tests.Callsign;
        CosFollowgCrossing stage = StageCosFollowgCrossing(layout, null);
        SimulationEngine engine = stage.Engine;
        AircraftState aircraft = stage.Aircraft;
        GroundNode a2Bar = stage.A2Bar;
        GroundNode c2Bar = stage.C2Bar;
        double barAlongFt = stage.BarAlongFt;
        double AlongFt(AircraftState ac) => stage.AlongFt(ac);
        Assert.NotNull(
            Issue1334OakDToFAcross33Tests.TickUntil(
                output,
                engine,
                ac => (ac.Phases?.CurrentPhase is CrossingRunwayPhase) && (AlongFt(ac) > 40.0),
                300
            )
        );
        Assert.Contains(aircraft.Phases!.Phases.Skip(aircraft.Phases.CurrentIndex + 1), p => p is FollowingPhase);

        CommandResult hold = engine.SendCommand(follower, "HOLD");
        Assert.True(hold.Success, hold.Message);
        CrossingRunwayPhase crossing = Assert.IsType<CrossingRunwayPhase>(aircraft.Phases.CurrentPhase);
        Assert.DoesNotContain(aircraft.Phases.Phases.Skip(aircraft.Phases.CurrentIndex + 1), p => p is FollowingPhase);
        Assert.Equal(a2Bar.Id, crossing.ApproachNodeId);
        Assert.Equal(c2Bar.Id, crossing.TargetNodeId);
        output.WriteLine($"HOLD at {AlongFt(aircraft):F1} ft past P on C2's line; C2's bar is {barAlongFt:F1} ft past P");
        output.WriteLine($"crossing slice: {SliceText(crossing)}");

        double halfLengthFt = AircraftLength.ResolveFt("B738") / 2.0;
        var acrossHeading = new TrueHeading(GeoMath.BearingTo(a2Bar.Position, c2Bar.Position));
        output.WriteLine($"leg flown {stage.C2Bearing:F1}°, near→far bar bearing {acrossHeading.Degrees:F1}°");
        var samples = new List<(int Second, double FlownFt, double BearingFt, bool NotClear)>();
        int second = 0;

        // Always true, so a tick predicate can record and then judge.
        bool Record(AircraftState ac)
        {
            second++;
            if (ac.Phases?.CurrentPhase != crossing)
            {
                return true;
            }

            double flownFt = AlongFt(ac) - barAlongFt - halfLengthFt;
            double bearingFt = (GeoMath.AlongTrackDistanceNm(ac.Position, c2Bar.Position, acrossHeading) * GeoMath.FeetPerNm) - halfLengthFt;
            bool notClear = crossing.IsNotClearOfFarMarking(ac, layout);
            samples.Add((second, flownFt, bearingFt, notClear));
            output.WriteLine(
                $"t+{second}s: tail past C2's bar {flownFt:F2} ft flown, {bearingFt:F2} ft on the bar bearing, gap {flownFt - bearingFt:F2} ft, "
                    + $"{(notClear ? "NOT clear" : "clear")}, short of clearing the bar (tail and wingtips) "
                    + $"{FollowingPhase.ShortOfBarFt(layout, c2Bar.Id, ac):F2} ft, {ac.GroundSpeed:F1} kt"
            );
            return true;
        }

        AircraftState? TickRecording(Func<AircraftState, bool> until, int budgetSeconds) =>
            Issue1334OakDToFAcross33Tests.TickUntil(output, engine, ac => Record(ac) && until(ac), budgetSeconds);

        Assert.NotNull(TickRecording(ac => ac.GroundSpeed < 0.5, 60));
        CommandResult res = engine.SendCommand(follower, "RES");
        Assert.True(res.Success, res.Message);
        AircraftState rest = Assert.IsType<AircraftState>(
            TickRecording(ac => (ac.Phases?.CurrentPhase is HoldingInPositionPhase) && (ac.GroundSpeed < 0.5), 300)
        );

        HoldingInPositionPhase held = Assert.IsType<HoldingInPositionPhase>(rest.Phases?.CurrentPhase);
        Assert.True(held.EndsBrokenOffFollow);
        double restFlownFt = AlongFt(rest) - barAlongFt - halfLengthFt;
        output.WriteLine(
            $"at rest: tail {restFlownFt:F2} ft past C2's bar flown, tail inside {held.TailInsideRunway ?? "no runway"}; largest gap "
                + $"{(samples.Count == 0 ? 0.0 : samples.Max(s => s.FlownFt - s.BearingFt)):F2} ft"
        );

        Assert.True(samples.Exists(s => (s.FlownFt < 0.0) && s.NotClear), "no sample with the tail short of C2's bar read not clear");
        List<(int Second, double FlownFt, double BearingFt, bool NotClear)> wrong = [.. samples.Where(s => (s.FlownFt >= 0.0) && s.NotClear)];
        Assert.True(
            wrong.Count == 0,
            $"not clear with the tail past C2's bar on {wrong.Count} ticks; first t+{wrong.FirstOrDefault().Second}s, "
                + $"{wrong.FirstOrDefault().FlownFt:F2} ft flown, {wrong.FirstOrDefault().BearingFt:F2} ft on the bar bearing"
        );
        Assert.True(
            (restFlownFt < 0.0) || (held.TailInsideRunway is null),
            $"at rest with the tail {restFlownFt:F2} ft past C2's bar along the leg, the hold says the tail is inside {held.TailInsideRunway}"
        );
    }

    /// <summary>
    /// The KCOS A2 → C2 crossing (<see cref="StageCosFollowgCrossing"/>) held by a HOLD as soon as the nose passes A2's 35L marking:
    /// it stops with its tail still behind A2's marking, on the near side of the centreline and off C2's paint. Part of it is still
    /// inside the runway's markings, so the crossing reads not clear of its far marking
    /// (<see cref="CrossingRunwayPhase.IsNotClearOfFarMarking"/>), though C2's marking judged off its paint alone reads the tail far
    /// outside.
    /// </summary>
    [Fact]
    public void StraightOverCrossing_HeldWithTheTailBehindTheNearMarking_IsNotClearOfTheFarMarking()
    {
        if ((Layout("COS") is not { } layout) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        InitLog();
        using IDisposable _ = NavigationDatabase.ScopedOverride(Issue899DetourConnectorTests.CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        CosFollowgCrossing stage = StageCosFollowgCrossing(layout, null);
        AircraftState aircraft = stage.Aircraft;
        Assert.NotNull(
            Issue1334OakDToFAcross33Tests.TickUntil(
                output,
                stage.Engine,
                ac => (ac.Phases?.CurrentPhase is CrossingRunwayPhase crossing) && (crossing.NosePastNearMarkingFt(ac, layout) > 0.0),
                300
            )
        );
        CommandResult hold = stage.Engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "HOLD");
        Assert.True(hold.Success, hold.Message);
        Assert.NotNull(Issue1334OakDToFAcross33Tests.TickUntil(output, stage.Engine, ac => ac.GroundSpeed < 0.5, 60));

        CrossingRunwayPhase held = Assert.IsType<CrossingRunwayPhase>(aircraft.Phases?.CurrentPhase);
        double nosePastFt = held.NosePastNearMarkingFt(aircraft, layout) ?? double.NaN;
        double tailPastFt = nosePastFt - AircraftLength.ResolveFt("B738");
        output.WriteLine($"at rest: nose {nosePastFt:F1} ft past A2's marking, tail {tailPastFt:F1} ft; slice {SliceText(held)}");
        Assert.True(nosePastFt > 0.0, $"the nose stopped {nosePastFt:F1} ft past A2's marking");
        Assert.True(tailPastFt < 0.0, $"the tail stopped {tailPastFt:F1} ft past A2's marking");
        Assert.True(held.IsNotClearOfFarMarking(aircraft, layout), "a crossing held with its tail behind the near marking read clear");
    }

    /// <summary>
    /// YAAT-1466: under a commanded <c>SPD</c> below the crossing floor a stop cap below the SPD still wins. The KCOS A2 → C2 crossing
    /// (<see cref="StageCosFollowgCrossing"/>) at SPD 8, its FOLLOWG broken off by a HOLD and resumed by a RES, ends at rest and
    /// brakes onto its slice end: the floor is never above the SPD, and the curve onto the slice end lowers it below the SPD.
    /// </summary>
    [Fact]
    public void StraightOverCrossing_EndingAtRestUnderASlowSpd_StopCapStillWins()
    {
        if ((Layout("COS") is not { } layout) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        InitLog();
        using IDisposable _ = NavigationDatabase.ScopedOverride(Issue899DetourConnectorTests.CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        const string follower = Issue1334OakDToFAcross33Tests.Callsign;
        CosFollowgCrossing stage = StageCosFollowgCrossing(layout, "SPD 8");
        AircraftState aircraft = stage.Aircraft;
        foreach (string command in new[] { "HOLD", "RES" })
        {
            Assert.NotNull(
                Issue1334OakDToFAcross33Tests.TickUntil(
                    output,
                    stage.Engine,
                    ac => (ac.Phases?.CurrentPhase is CrossingRunwayPhase) && ((command != "RES") || (ac.GroundSpeed < 0.5)),
                    300
                )
            );
            CommandResult result = stage.Engine.SendCommand(follower, command);
            Assert.True(result.Success, $"{command}: {result.Message}");
        }

        CrossingRunwayPhase crossing = Assert.IsType<CrossingRunwayPhase>(aircraft.Phases?.CurrentPhase);
        int capped = 0;
        for (int second = 0; (second < 300) && ReferenceEquals(aircraft.Phases?.CurrentPhase, crossing); second++)
        {
            stage.Engine.TickOneSecond();
            double floorKts = crossing.NavMinSpeedKts ?? double.MaxValue;
            output.WriteLine($"t+{second}s: {aircraft.GroundSpeed:F2} kt, floor {floorKts:F2} kt, SPD {aircraft.Ground.CommandedTaxiSpeedKts}");
            Assert.Equal(8.0, aircraft.Ground.CommandedTaxiSpeedKts);
            Assert.True(floorKts <= 8.0, $"t+{second}s: floor {floorKts:F2} kt above SPD 8");
            Assert.True(aircraft.GroundSpeed <= 8.5, $"t+{second}s: {aircraft.GroundSpeed:F2} kt under SPD 8");
            capped += (floorKts < 8.0) ? 1 : 0;
        }

        Assert.True(capped > 0, "the curve onto the slice end never lowered the floor below the SPD");
    }

    /// <summary>
    /// The KCOS A2 → C2 straight-over crossing of 35L with a FOLLOWG armed on it: a B738 cleared TAXI A2 C2 held at A2's 35L bar,
    /// a B738 leader on C2 more than 300 ft past C2's bar, then FOLLOWG and CROSS 35L given. The caller holds the navigation
    /// database override that declares the pair (<see cref="Issue899DetourConnectorTests.CosDeclaring"/>). A
    /// <paramref name="taxiCommand"/> (a <c>SPD</c>) is given right after the TAXI, while the aircraft still taxis.
    /// </summary>
    private CosFollowgCrossing StageCosFollowgCrossing(AirportGroundLayout layout, string? taxiCommand)
    {
        const string follower = Issue1334OakDToFAcross33Tests.Callsign;
        const string leader = "SWA943";
        GroundNode a2Bar = GroundNodeLookup.HoldShortBar(layout, "35L", "A2");
        GroundNode c2Bar = GroundNodeLookup.HoldShortBar(layout, "35L", "C2");
        GroundNode start = FarthestOn(layout, "A2", a2Bar);
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        double startHeading = GeoMath.BearingTo(start.Position, a2Bar.Position);
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(layout, "COS", start, startHeading);
        aircraft.AircraftType = "B738";
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-yaat-943-cos-followg-break-off",
            ScenarioName = "COS A2/C2 straight over 17R/35L, FOLLOWG broken off on the leg",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "COS",
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
        };

        CommandResult taxi = engine.SendCommand(follower, "TAXI A2 C2");
        Assert.True(taxi.Success, taxi.Message);
        if (taxiCommand is not null)
        {
            CommandResult then = engine.SendCommand(follower, taxiCommand);
            Assert.True(then.Success, $"{taxiCommand}: {then.Message}");
        }

        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        int stretch = route.Segments.FindIndex(s => (s.FromNodeId < 0) && (s.ToNodeId == c2Bar.Id));
        Assert.True(stretch >= 0, "the route has no P→C2 bar stretch");
        GroundNode meet = route.Segments[stretch].Edge.FromNode;
        var stage = new CosFollowgCrossing(engine, aircraft, a2Bar, c2Bar, meet, GeoMath.BearingTo(meet.Position, c2Bar.Position));

        GroundNode leadAt = Assert.IsType<GroundNode>(
            layout
                .GetNodesOnTaxiway("C2")
                .Select(n => (Node: n, Past: stage.AlongFt(n.Position) - stage.BarAlongFt))
                .Where(c => c.Past > 300.0)
                .OrderBy(c => c.Past)
                .Select(c => c.Node)
                .FirstOrDefault()
        );
        AircraftState lead = Issue1334OakDToFAcross33Tests.MakeAircraftAt(layout, "COS", leadAt, stage.C2Bearing);
        lead.Callsign = leader;
        lead.AircraftType = "B738";
        engine.World.AddAircraft(lead);

        Assert.IsType<HoldingShortPhase>(
            Issue1334OakDToFAcross33Tests.TickUntil(output, engine, ac => ac.Phases?.CurrentPhase is HoldingShortPhase, 900)?.Phases?.CurrentPhase
        );
        CommandResult follow = engine.SendCommand(follower, $"FOLLOWG {leader}");
        Assert.True(follow.Success, follow.Message);
        CommandResult cross = engine.SendCommand(follower, "CROSS 35L");
        Assert.True(cross.Success, cross.Message);
        return stage;
    }

    /// <summary>
    /// The staged KCOS A2 → C2 crossing (<see cref="StageCosFollowgCrossing"/>): <paramref name="Meet"/> is P, where the virtual
    /// stretch to C2's bar starts, and <paramref name="C2Bearing"/> that stretch's bearing, the leg flown.
    /// </summary>
    private sealed record CosFollowgCrossing(
        SimulationEngine Engine,
        AircraftState Aircraft,
        GroundNode A2Bar,
        GroundNode C2Bar,
        GroundNode Meet,
        double C2Bearing
    )
    {
        /// <summary>How far (ft) C2's bar lies past P along the leg flown.</summary>
        public double BarAlongFt => AlongFt(C2Bar.Position);

        /// <summary>How far (ft) <paramref name="point"/> lies past P along the leg flown.</summary>
        public double AlongFt(LatLon point) => GeoMath.AlongTrackDistanceNmRaw(point, Meet.Position, C2Bearing) * GeoMath.FeetPerNm;

        /// <summary>How far (ft) <paramref name="aircraft"/>'s centre lies past P along the leg flown.</summary>
        public double AlongFt(AircraftState aircraft) => AlongFt(aircraft.Position);
    }

    /// <summary>The crossing's slice as node ids, a virtual edge marked "(v)".</summary>
    private static string SliceText(CrossingRunwayPhase crossing) =>
        string.Join(
            " ",
            crossing.CrossingRoute?.Segments.Select(s => $"#{s.FromNodeId}→#{s.ToNodeId}{(VirtualNode.IsVirtualEdge(s.Edge.Edge) ? "(v)" : "")}")
                ?? []
        );

    /// <summary>
    /// OAK D→F from the leg's entry, already on the runway side of D's bar: the chord touches no centreline node and the leg passes
    /// no bar with a hold, so the held-short leg's scan has only the chord itself to find the crossing by.
    /// </summary>
    [Fact]
    public void RunwayOnLeg_ChordCrossing_ReportsTheRunway()
    {
        if (Layout("OAK") is not { } layout)
        {
            return;
        }

        InitLog();
        StraightOverLeg straightOver = Assert.IsType<StraightOverLeg>(SegmentExpander.FindStraightOver(layout, "D", "F", [])?.Leg);
        GroundNode start = straightOver.Entry;
        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            ["D", "F"],
            out string? failReason,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = "D",
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("C172"),
                StartHeadingTrue = GeoMath.BearingTo(start.Position, straightOver.Edges[0].ToNode.Position),
            },
            new TaxiClass(AircraftCategory.Piston, WakeTurbulenceData.WakeClass.Small)
        );
        Assert.True(route is not null, failReason);
        List<TaxiRouteSegment> leg = [.. route.Segments.TakeWhile(s => !s.Edge.Edge.MatchesTaxiway("F"))];
        Assert.Contains(leg, s => VirtualNode.IsVirtualEdge(s.Edge.Edge));
        Assert.DoesNotContain(route.HoldShortPoints, h => leg.Exists(s => (s.FromNodeId == h.NodeId) || (s.ToNodeId == h.NodeId)));

        string? rejection = GroundCommandHandler.RunwayOnLeg(layout, route, leg);

        Assert.NotNull(rejection);
        Assert.StartsWith("crosses a runway", rejection);
    }

    private const int FieldXJoin = 3;
    private const int FieldYJoin = 4;
    private const int FieldXBar = 5;
    private const int FieldXFar = 6;
    private const int FieldYBar = 7;
    private const int FieldYFar = 8;

    /// <summary>
    /// A field no real layout gives: runway 09/27 with X joining it from the north and Y leaving south, joins 280 ft apart, X flown on
    /// <paramref name="inboundDeg"/> and Y on <paramref name="outwardDeg"/>, each bar <paramref name="barFt"/> along its taxiway from
    /// its join and a node 400 ft past that. Hand-built because the geometry these tests need is in no real layout: no test airport
    /// has two lines meeting past the far bar, joins already within the chord's corner, or a chord whose corners are wide.
    /// </summary>
    private static AirportGroundLayout CrossingField(double inboundDeg, double outwardDeg, double barFt)
    {
        var centre = new LatLon(40.0, -100.0);
        var layout = new AirportGroundLayout { AirportId = "TST" };
        LatLon west = Along(centre, 270.0, 4000.0);
        LatLon east = Along(centre, 90.0, 4000.0);
        layout.Runways.Add(
            new GroundRunway
            {
                Name = "09 - 27",
                Coordinates = [(west.Lat, west.Lon), (east.Lat, east.Lon)],
                WidthFt = 150.0,
            }
        );

        var runway = RunwayIdentifier.Parse("09/27");
        LatLon xJoin = Along(centre, 270.0, 140.0);
        LatLon yJoin = Along(centre, 90.0, 140.0);
        double xOutward = (inboundDeg + 180.0) % 360.0;
        GroundNode Node(int id, LatLon position, bool bar)
        {
            var node = new GroundNode
            {
                Id = id,
                Position = position,
                Type = bar ? GroundNodeType.RunwayHoldShort : GroundNodeType.TaxiwayIntersection,
                RunwayId = bar ? runway : null,
            };
            layout.Nodes[id] = node;
            return node;
        }

        GroundNode[] centreline = [Node(1, west, false), Node(FieldXJoin, xJoin, false), Node(FieldYJoin, yJoin, false), Node(2, east, false)];
        GroundNode[] x =
        [
            centreline[1],
            Node(FieldXBar, Along(xJoin, xOutward, barFt), true),
            Node(FieldXFar, Along(xJoin, xOutward, barFt + 400.0), false),
        ];
        GroundNode[] y =
        [
            centreline[2],
            Node(FieldYBar, Along(yJoin, outwardDeg, barFt), true),
            Node(FieldYFar, Along(yJoin, outwardDeg, barFt + 400.0), false),
        ];
        void Chain(GroundNode[] nodes, string name)
        {
            foreach ((GroundNode a, GroundNode b) in nodes.Zip(nodes.Skip(1)))
            {
                layout.Edges.Add(
                    new GroundEdge
                    {
                        Nodes = [a, b],
                        TaxiwayName = name,
                        DistanceNm = GeoMath.DistanceNm(a.Position, b.Position),
                    }
                );
            }
        }

        Chain(centreline, "RWY09/27");
        Chain(x, "X");
        Chain(y, "Y");
        layout.RebuildAdjacencyLists();
        return layout;
    }

    private static LatLon Along(LatLon from, double bearingDeg, double feet) =>
        GeoMath.ProjectPoint(from, new TrueHeading(bearingDeg), feet / GeoMath.FeetPerNm);

    private static TaxiRoute? ResolveOverField(AirportGroundLayout layout, AircraftCategory category, List<string> diag, out string? refusal)
    {
        GroundNode start = layout.Nodes[FieldXFar];
        return TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            ["X", "Y"],
            out refusal,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("B738"),
                StartHeadingTrue = GeoMath.BearingTo(start.Position, layout.Nodes[FieldXBar].Position),
                DiagnosticLog = diag.Add,
            },
            new TaxiClass(category, WakeTurbulenceData.WakeClass.Large)
        );
    }

    /// <summary>The node on <paramref name="taxiway"/> farthest from <paramref name="point"/>.</summary>
    private static GroundNode FarthestOn(AirportGroundLayout layout, string taxiway, GroundNode point) =>
        layout.GetNodesOnTaxiway(taxiway).OrderByDescending(n => Ft(n.Position, point.Position)).First();

    /// <summary>
    /// A <see cref="AircraftType"/> standing at <see cref="Start"/> on <see cref="Airport"/>'s layout, nosed at
    /// <see cref="Facing"/>.
    /// </summary>
    private sealed record TaxiFrom(AirportGroundLayout Layout, string Airport, GroundNode Start, GroundNode Facing, string AircraftType);

    /// <summary>
    /// The route <paramref name="path"/> gives the aircraft <paramref name="from"/> stands as, its readback starting
    /// <paramref name="readback"/>.
    /// </summary>
    private TaxiRoute Taxi(TaxiFrom from, List<string> path, string readback)
    {
        (AirportGroundLayout layout, string airport, GroundNode start, GroundNode facing, string aircraftType) = from;
        double heading = start.Id == facing.Id ? 0.0 : GeoMath.BearingTo(start.Position, facing.Position);
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(layout, airport, start, heading);
        aircraft.AircraftType = aircraftType;
        var taxi = new TaxiCommand(Path: path, HoldShorts: [], Expedite: false, DestinationParking: null);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"{aircraftType} {string.Join(" ", path)}: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"segments: {string.Join(" ", route.Segments.Select(s => $"#{s.FromNodeId}→#{s.ToNodeId}({s.TaxiwayName})"))}");
        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            output.WriteLine($"hold short: #{hs.NodeId} {hs.TargetName} ({hs.Reason})");
        }

        Assert.StartsWith(readback, result.Message);
        return route;
    }

    [Fact]
    public void StraightOver_NoFarBar_KeepsTodaysCentrelineRoute()
    {
        if (Layout("MIA") is not { } layout)
        {
            return;
        }

        InitLog();
        var capture = WarningLogCapture.Install();
        StraightOver over = Assert.IsType<StraightOver>(SegmentExpander.FindStraightOver(layout, "L11", "M11", []));
        Assert.Null(over.Leg);
        output.WriteLine($"not built: {over.Reason}");
        Assert.True(over.MissingLayoutData, over.Reason);
        Assert.Contains($"[runway-crossing] L11→M11 straight-over not built: {over.Reason}", capture.Warnings);

        List<GroundNode> onL11 = layout.GetNodesOnTaxiway("L11");
        GroundNode join = onL11.First(n => n.Edges.Exists(e => e.IsRunwayCenterline));
        GroundNode start = onL11.Where(n => n.Type == GroundNodeType.RunwayHoldShort).OrderBy(n => Ft(n.Position, join.Position)).First();
        var diag = new List<string>();
        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            ["L11", "M11"],
            out string? failReason,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("C172"),
                StartHeadingTrue = GeoMath.BearingTo(start.Position, join.Position),
                DiagnosticLog = diag.Add,
            },
            new TaxiClass(AircraftCategory.Piston, WakeTurbulenceData.WakeClass.Small)
        );
        Assert.True(route is not null, failReason);
        Assert.Contains(diag, l => l.StartsWith("[runway-crossing] L11→M11 straight-over not built: ", StringComparison.Ordinal));
        Assert.Contains(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
        Assert.Empty(VirtualSegments(route));
    }
}
