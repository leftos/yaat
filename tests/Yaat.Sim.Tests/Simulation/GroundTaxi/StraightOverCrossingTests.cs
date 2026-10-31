using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
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
        output.WriteLine($"{fromTaxiway}→{toTaxiway}: not built: {over.NotBuilt}");
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
        TaxiRoute route = Taxi(layout, "COS", start, a2Bar, "B738", ["A2", "C2", "C"], "Taxi via A2 C2 C");

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
        TaxiRoute route = Taxi(layout, "COS", start, c2Bar, "B738", ["C2", "A2", "A"], "Taxi via C2 A2 A");

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
        TaxiRoute route = Taxi(layout, "CMH", start, e4Bar, aircraftType, ["E4", "F3"], "Taxi via E4 F3");

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
        TaxiRoute route = Taxi(layout, "CMH", start, f3Bar, "C172", ["F3", "E4"], "Taxi via F3 E4");

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
        TaxiRoute route = Taxi(layout, airport, leg.Entry, leg.Landing, "C172", [fromTaxiway, toTaxiway], $"Taxi via {fromTaxiway} {toTaxiway}");
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
            AircraftCategory.Piston,
            WakeTurbulenceData.WakeClass.Small
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
        TaxiRoute cmhRoute = Taxi(cmh, "CMH", e4Bar, f3Bar, "C172", ["E4", "F3"], "Taxi via E4 F3");
        Assert.Equal(e4Bar.Id, Assert.Single(cmhRoute.HoldShortPoints, h => h.Reason == HoldShortReason.RunwayCrossing).NodeId);

        using IDisposable _ = NavigationDatabase.ScopedOverride(Issue899DetourConnectorTests.CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        GroundNode c2Bar = GroundNodeLookup.HoldShortBar(cos, "35L", "C2");
        GroundNode a2Bar = GroundNodeLookup.HoldShortBar(cos, "35L", "A2");
        TaxiRoute cosRoute = Taxi(cos, "COS", c2Bar, a2Bar, "C172", ["C2", "A2", "A"], "Taxi via C2 A2 A");
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
        TaxiRoute route = Taxi(layout, "CMH", start, e4Bar, "C172", ["E4", "F3", "F"], "Taxi via E4 F3 F");

        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
        Assert.Contains(route.Segments, s => s.ToNodeId == f3Bar.Id);
        Assert.Equal(e4Bar.Id, Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.RunwayCrossing).NodeId);
    }

    /// <summary>Lines 50° apart meeting 331 ft along Y, past its bar at 200 ft: no leg can land on Y short of the bar.</summary>
    [Fact]
    public void StraightOverGeometry_LinesMeetPastTheFarBar_IsNoRoom()
    {
        AirportGroundLayout layout = CrossingField(inboundDeg: 155.0, outwardDeg: 205.0, barFt: 200.0);

        StraightOver over = Assert.IsType<StraightOver>(SegmentExpander.FindStraightOver(layout, "X", "Y", []));
        output.WriteLine($"not built: {over.NotBuilt}");

        Assert.Null(over.Leg);
        Assert.True(over.NoRoom, over.NotBuilt);
    }

    /// <summary>The same lines with Y's bar at 400 ft land short of it: the refusal is the bar's, not the angle's.</summary>
    [Fact]
    public void StraightOverGeometry_LinesMeetShortOfTheFarBar_IsALeg()
    {
        AirportGroundLayout layout = CrossingField(inboundDeg: 155.0, outwardDeg: 205.0, barFt: 400.0);

        StraightOver over = Assert.IsType<StraightOver>(SegmentExpander.FindStraightOver(layout, "X", "Y", []));

        Assert.False(over.NoRoom, over.NotBuilt);
        Assert.Equal(StraightOverKind.Continue, over.Leg?.Kind);
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

        Assert.Null(ResolveOverField(layout, AircraftCategory.Jet, out string? jetRefusal));
        Assert.Equal(SegmentExpander.CannotMakeTurnReason("X", "Y"), jetRefusal);

        TaxiRoute? helicopter = ResolveOverField(layout, AircraftCategory.Helicopter, out string? helicopterRefusal);
        Assert.True(helicopter is not null, helicopterRefusal);
        Assert.Equal(leg.Edges.Select(e => e.ToNodeId), VirtualSegments(helicopter).Take(2).Select(s => s.ToNodeId));
    }

    /// <summary>The owner's wording, terminal and spoken, whatever the aircraft: the check comes before any turn is judged.</summary>
    [Theory]
    [InlineData("C172")]
    [InlineData("B738")]
    public void NoRoomToCrossStraight_TerminalAndSpoken(string aircraftType)
    {
        AirportGroundLayout layout = CrossingField(inboundDeg: 155.0, outwardDeg: 205.0, barFt: 200.0);
        GroundNode start = layout.Nodes[FieldXFar];
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(layout, "TST", start, 155.0);
        aircraft.AircraftType = aircraftType;

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, new TaxiCommand(["X", "Y"], [], Expedite: false), layout);
        output.WriteLine($"{result.Success} — {result.Message} — {result.PilotUnable?.Tts}");

        Assert.False(result.Success, "TAXI X Y was accepted");
        Assert.Equal("Unable, no room to cross straight onto Y, request taxi instructions", result.Message);
        PilotSpeechText unable = Assert.IsType<PilotSpeechText>(result.PilotUnable);
        Assert.Equal(result.Message, unable.Terminal);
        Assert.EndsWith(", unable, no room to cross straight over onto yankee, request taxi instructions", unable.Tts);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>The owner's literals: the second taxiway named, spelled when spoken.</summary>
    [Fact]
    public void NoRoomToCrossStraight_OwnerLiterals()
    {
        var aircraft = new AircraftState { Callsign = Issue1334OakDToFAcross33Tests.Callsign, AircraftType = "C172" };

        PilotSpeechText speech = PilotResponder.BuildUnableNoRoomToCrossStraight(aircraft, "C2");

        Assert.Equal("Unable, no room to cross straight onto C2, request taxi instructions", speech.Terminal);
        Assert.EndsWith(", unable, no room to cross straight over onto charlie two, request taxi instructions", speech.Tts);
        Assert.Equal(speech.Terminal, SegmentExpander.NoRoomToCrossStraightReason("C2"));
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
        AssertLegRestores(Taxi(cmh, "CMH", e4Junction, e4Bar, "C172", ["E4", "F3"], "Taxi via E4 F3"), cmh);

        using IDisposable _ = NavigationDatabase.ScopedOverride(Issue899DetourConnectorTests.CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        GroundNode a2Bar = GroundNodeLookup.HoldShortBar(cos, "35L", "A2");
        AssertLegRestores(Taxi(cos, "COS", FarthestOn(cos, "A2", a2Bar), a2Bar, "B738", ["A2", "C2", "C"], "Taxi via A2 C2 C"), cos);
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
            AircraftCategory.Piston,
            WakeTurbulenceData.WakeClass.Small
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
    /// its join and a node 400 ft past that.
    /// </summary>
    private static AirportGroundLayout CrossingField(double inboundDeg, double outwardDeg, double barFt)
    {
        var centre = new LatLon(40.0, -100.0);
        LatLon Along(LatLon from, double bearingDeg, double feet) =>
            GeoMath.ProjectPoint(from, new TrueHeading(bearingDeg), feet / GeoMath.FeetPerNm);

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

    private static TaxiRoute? ResolveOverField(AirportGroundLayout layout, AircraftCategory category, out string? refusal)
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
            },
            category,
            WakeTurbulenceData.WakeClass.Large
        );
    }

    /// <summary>The node on <paramref name="taxiway"/> farthest from <paramref name="point"/>.</summary>
    private static GroundNode FarthestOn(AirportGroundLayout layout, string taxiway, GroundNode point) =>
        layout.GetNodesOnTaxiway(taxiway).OrderByDescending(n => Ft(n.Position, point.Position)).First();

    /// <summary>The route <paramref name="path"/> gives a <paramref name="aircraftType"/> at <paramref name="start"/> nosed at <paramref name="facing"/>, its readback starting <paramref name="readback"/>.</summary>
    private TaxiRoute Taxi(
        AirportGroundLayout layout,
        string airport,
        GroundNode start,
        GroundNode facing,
        string aircraftType,
        List<string> path,
        string readback
    )
    {
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
        StraightOver over = Assert.IsType<StraightOver>(SegmentExpander.FindStraightOver(layout, "L11", "M11", []));
        Assert.Null(over.Leg);
        output.WriteLine($"not built: {over.NotBuilt}");

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
            AircraftCategory.Piston,
            WakeTurbulenceData.WakeClass.Small
        );
        Assert.True(route is not null, failReason);
        Assert.Contains(diag, l => l.StartsWith("[runway-crossing] L11→M11 straight-over not built: ", StringComparison.Ordinal));
        Assert.Contains(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);
        Assert.Empty(VirtualSegments(route));
    }
}
