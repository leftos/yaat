using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-1469: a hold-short stop the setback walk places on a fillet arc (<see cref="GroundArc"/>) lies on the painted arc,
/// measured and placed along the curve, not on the chord between the arc's nodes. KCOS <c>TAXI C2 A2 A A3</c> then
/// <c>HS A</c> (the route of <see cref="CrossingRunwayRestoreTests.MidBrakingOntoTheStopPastTheCrossing_RestsWhereTheOriginalDoes"/>):
/// the stop for the hold short of A falls on the fillet arc from A2's 17R/35L bar (#84, the fillet's start) to A's bar node.
/// </summary>
public class FilletStopOnArcTests(ITestOutputHelper output)
{
    private const string AircraftType = "C172";

    /// <summary>
    /// A type whose length setback (L + 30 ft) runs back past where the 17R/35L bar's tail clearance holds the stop, but whose
    /// setback stays shorter than the arc, so the wingtip floor holds its stop to that limit on the arc.
    /// </summary>
    private const string HeldToLimitType = "C560";

    /// <summary>A type more than twice the KCOS fillet arc long, so its just-past-runway setback is the gap to the bar.</summary>
    private const string LongType = "B748";

    /// <summary>How far (ft) the crossed-runway gap may read from the arc's walked length.</summary>
    private const double GapToleranceFt = 0.5;

    /// <summary>How far (ft, along the arc) past the 17R/35L bar the long type stands on the fillet.</summary>
    private const double JustPastBarFt = 3.0;

    /// <summary>How far (ft) off the arc a stop placed along it may lie: the arc is walked as 64 chords.</summary>
    private const double OnArcToleranceFt = 1.0;

    /// <summary>How far (ft) apart two placements of the same stop may lie.</summary>
    private const double SameStopToleranceFt = 0.1;

    /// <summary>How far (ft) past the arc's start a setback longer than the arc is asked to run onto the edge before it.</summary>
    private const double PastArcFt = 20.0;

    /// <summary>
    /// How much shorter (ft) than the arc the runway-clamp case's setback is: well past the on-arc tolerance from the bar, so a
    /// walk that clamped at the bar would miss it.
    /// </summary>
    private const double InsideArcFt = 3.0;

    /// <summary>How far (ft, along the arc) short of its stop the aircraft stands on the fillet.</summary>
    private const double ShortOfStopFt = 2.0;

    /// <summary>
    /// The placed stop for the hold short of A lies on the fillet arc segment ending at A's bar node, within a foot of the
    /// curve, set back along the arc from the bar node by the taxiway setback the walk was asked for.
    /// </summary>
    [Fact]
    public void HoldShortOfA_StopOnTheFilletArc_LiesOnTheArc()
    {
        if (BuildCosRoute(AircraftType) is not { } cos)
        {
            return;
        }

        HoldShortPoint hsA = HoldShortOfA(cos.Route);
        (TaxiRouteSegment arcSeg, GroundArc arc) = ApproachArc(cos.Route, hsA.NodeId);
        Assert.Equal(84, arcSeg.FromNodeId);
        Assert.Equal(129, arcSeg.ToNodeId);

        var stop = new LatLon(Assert.NotNull(hsA.Latitude), Assert.NotNull(hsA.Longitude));
        (double alongFt, double offFt) = arc.Locate(arcSeg.Edge.ToNode, stop);
        double arcFt = ArcFt(arcSeg, arc);
        double intendedFt = TaxiwaySetbackFt(AircraftType);
        output.WriteLine(
            $"stop for A at {stop}: {alongFt:F2} ft along the {arcFt:F2} ft arc {arcSeg.FromNodeId}->{arcSeg.ToNodeId} from "
                + $"#{arcSeg.ToNodeId}, {offFt:F2} ft off it; intended setback {intendedFt:F2} ft"
        );

        Assert.True(offFt <= OnArcToleranceFt, $"the stop lies {offFt:F2} ft off the fillet arc");
        Assert.True(Math.Abs(alongFt - intendedFt) <= OnArcToleranceFt, $"the stop is {alongFt:F2} ft along the arc, not {intendedFt:F2}");
    }

    /// <summary>
    /// A stop on a straight edge of the same route — the runway crossing's, placed half a fuselage back from its bar — lands
    /// where the straight projection from the bar node toward the approach node puts it.
    /// </summary>
    [Fact]
    public void StraightEdgeStop_IsUnchanged()
    {
        if (BuildCosRoute(AircraftType) is not { } cos)
        {
            return;
        }

        double halfFt = AircraftLength.ResolveFt(AircraftType) / 2.0;
        HoldShortPoint bar = Assert.Single(
            cos.Route.HoldShortPoints,
            h =>
                (h.Reason == HoldShortReason.RunwayCrossing)
                && (cos.Route.Segments.FirstOrDefault(s => s.ToNodeId == h.NodeId) is { } seg)
                && (seg.Edge.Edge is not GroundArc)
                && (Ft(seg.Edge.FromNode.Position, seg.Edge.ToNode.Position) > halfFt)
        );
        TaxiRouteSegment approach = cos.Route.Segments.First(s => s.ToNodeId == bar.NodeId);
        LatLon node = approach.Edge.ToNode.Position;
        (double lat, double lon) = GeoMath.ProjectPointRaw(
            node.Lat,
            node.Lon,
            GeoMath.BearingTo(node, approach.Edge.FromNode.Position),
            halfFt / GeoMath.FeetPerNm
        );

        var stop = new LatLon(Assert.NotNull(bar.Latitude), Assert.NotNull(bar.Longitude));
        double apartFt = Ft(stop, new LatLon(lat, lon));
        output.WriteLine(
            $"{bar.TargetName} bar #{bar.NodeId} on straight {approach.FromNodeId}->{approach.ToNodeId}: "
                + $"stop {apartFt:F4} ft from the chord projection"
        );
        Assert.True(apartFt <= SameStopToleranceFt, $"the straight-edge stop lies {apartFt:F4} ft from the chord projection");
    }

    /// <summary>
    /// A walk from A's bar node over the whole arc and onto the edge before it gives the same stop on the full route as on
    /// one whose prefix is trimmed to that edge.
    /// </summary>
    [Fact]
    public void ArcStop_FromATrimmedRoute_IsTheSame()
    {
        if (BuildCosRoute(AircraftType) is not { } cos)
        {
            return;
        }

        HoldShortPoint hsA = HoldShortOfA(cos.Route);
        (TaxiRouteSegment arcSeg, GroundArc arc) = ApproachArc(cos.Route, hsA.NodeId);
        int beforeArc = cos.Route.Segments.FindIndex(s => s.ToNodeId == arcSeg.FromNodeId);
        Assert.True(beforeArc > 0, "no segment before the arc to trim to");
        var trimmed = new TaxiRoute { Segments = [.. cos.Route.Segments.Skip(beforeArc)], HoldShortPoints = [] };

        double setbackNm = (ArcFt(arcSeg, arc) + PastArcFt) / GeoMath.FeetPerNm;
        GroundNode full = VirtualNode.OffsetBefore(cos.Layout, cos.Route, hsA.NodeId, setbackNm, HoldShortStopKind.Taxiway);
        GroundNode cut = VirtualNode.OffsetBefore(cos.Layout, trimmed, hsA.NodeId, setbackNm, HoldShortStopKind.Taxiway);
        double apartFt = Ft(full.Position, cut.Position);
        output.WriteLine($"full route stop {full.Position}, trimmed route stop {cut.Position}: {apartFt:F4} ft apart");
        Assert.True(apartFt <= SameStopToleranceFt, $"the trimmed route's stop lies {apartFt:F4} ft from the full route's");
    }

    /// <summary>
    /// A setback longer than the arc walks the whole arc, measured along the curve, and goes on onto the straight edge
    /// before it by exactly the remainder.
    /// </summary>
    [Fact]
    public void SetbackLongerThanTheArc_ContinuesOntoTheStraightEdgeByTheRemainder()
    {
        if (BuildCosRoute(AircraftType) is not { } cos)
        {
            return;
        }

        HoldShortPoint hsA = HoldShortOfA(cos.Route);
        (TaxiRouteSegment arcSeg, GroundArc arc) = ApproachArc(cos.Route, hsA.NodeId);
        double arcFt = ArcFt(arcSeg, arc);

        GroundNode stop = VirtualNode.OffsetBefore(
            cos.Layout,
            cos.Route,
            hsA.NodeId,
            (arcFt + PastArcFt) / GeoMath.FeetPerNm,
            HoldShortStopKind.Taxiway
        );
        LatLon expected = PastTheArcStart(cos.Route, arcSeg);
        double apartFt = Ft(stop.Position, expected);
        output.WriteLine($"arc {arcFt:F2} ft; stop {stop.Position} is {apartFt:F4} ft from {PastArcFt:F0} ft down the straight edge before it");
        Assert.True(apartFt <= SameStopToleranceFt, $"the stop lies {apartFt:F4} ft from {PastArcFt:F0} ft past the arc's start");
    }

    /// <summary>
    /// A zero-length edge on the walk (a virtual node minted on the arc's start node) is stepped over: the walk past the arc
    /// lands where it does without it.
    /// </summary>
    [Fact]
    public void ZeroLengthEdgeBeforeTheArc_DoesNotMoveTheStop()
    {
        if (BuildCosRoute(AircraftType) is not { } cos)
        {
            return;
        }

        HoldShortPoint hsA = HoldShortOfA(cos.Route);
        (TaxiRouteSegment arcSeg, GroundArc arc) = ApproachArc(cos.Route, hsA.NodeId);
        double arcFt = ArcFt(arcSeg, arc);
        int beforeArc = cos.Route.Segments.FindIndex(s => s.ToNodeId == arcSeg.FromNodeId);
        Assert.True(beforeArc >= 0, "no segment before the arc");

        TaxiRouteSegment straight = cos.Route.Segments[beforeArc];
        GroundNode arcStart = straight.Edge.ToNode;
        GroundNode twin = VirtualNode.Create(arcStart.Position.Lat, arcStart.Position.Lon);
        List<TaxiRouteSegment> segments = [.. cos.Route.Segments];
        segments[beforeArc] = VirtualNode.CreateSegment(straight.Edge.FromNode, twin, straight.TaxiwayName);
        segments.Insert(beforeArc + 1, VirtualNode.CreateSegment(twin, arcStart, straight.TaxiwayName));
        var withZeroLength = new TaxiRoute { Segments = segments, HoldShortPoints = [] };

        GroundNode stop = VirtualNode.OffsetBefore(
            cos.Layout,
            withZeroLength,
            hsA.NodeId,
            (arcFt + PastArcFt) / GeoMath.FeetPerNm,
            HoldShortStopKind.Taxiway
        );
        double apartFt = Ft(stop.Position, PastTheArcStart(cos.Route, arcSeg));
        output.WriteLine($"stop with a zero-length edge at #{arcStart.Id}: {apartFt:F4} ft from the walk without it");
        Assert.True(apartFt <= SameStopToleranceFt, $"the zero-length edge moved the stop {apartFt:F4} ft");
    }

    /// <summary>
    /// A stop the wingtip floor holds to its walk-back limit — the tail <see cref="RunwayTailClearance.MarginFt"/> past A2's
    /// 17R/35L bar at the fillet's start — lands on the arc, half a fuselage plus that margin along it from the bar.
    /// </summary>
    [Fact]
    public void StopHeldToTheRunwayTailLimit_OnTheFillet_LiesOnTheArc()
    {
        if (BuildCosRoute(HeldToLimitType) is not { } cos)
        {
            return;
        }

        HoldShortPoint hsA = HoldShortOfA(cos.Route);
        (TaxiRouteSegment arcSeg, GroundArc arc) = ApproachArc(cos.Route, hsA.NodeId);
        GroundNode runwayBar = arcSeg.Edge.FromNode;
        Assert.Equal(GroundNodeType.RunwayHoldShort, runwayBar.Type);

        var stop = new LatLon(Assert.NotNull(hsA.Latitude), Assert.NotNull(hsA.Longitude));
        (double alongFt, double offFt) = arc.Locate(runwayBar, stop);
        double limitFt = (AircraftLength.ResolveFt(HeldToLimitType) / 2.0) + RunwayTailClearance.MarginFt;
        output.WriteLine(
            $"{HeldToLimitType} stop for A: {alongFt:F2} ft along the {ArcFt(arcSeg, arc):F2} ft arc from the runway bar "
                + $"#{runwayBar.Id}, {offFt:F2} ft off it; tail limit {limitFt:F2} ft"
        );

        Assert.True(offFt <= OnArcToleranceFt, $"the held stop lies {offFt:F2} ft off the fillet arc");
        Assert.True(Math.Abs(alongFt - limitFt) <= OnArcToleranceFt, $"the held stop is {alongFt:F2} ft along the arc, not {limitFt:F2}");
    }

    /// <summary>
    /// An aircraft standing on the fillet <see cref="ShortOfStopFt"/> short of the stop for A, the placement re-run from where
    /// it stands: the stop stays where it was, ahead of the aircraft, rather than being pulled back onto it.
    /// </summary>
    [Fact]
    public void AircraftOnTheFilletShortOfTheStop_KeepsTheStopAhead()
    {
        if (BuildCosRoute(AircraftType) is not { } cos)
        {
            return;
        }

        HoldShortPoint hsA = HoldShortOfA(cos.Route);
        (TaxiRouteSegment arcSeg, GroundArc arc) = ApproachArc(cos.Route, hsA.NodeId);
        double setbackFt = TaxiwaySetbackFt(AircraftType);
        LatLon standing = arc.PointAtFt(arcSeg.Edge.ToNode, setbackFt + ShortOfStopFt);
        cos.Route.CurrentSegmentIndex = cos.Route.Segments.IndexOf(arcSeg);

        HoldShortAnnotator.ComputeHoldShortPositions(
            cos.Layout,
            cos.Route,
            AircraftLength.ResolveFt(AircraftType),
            standing,
            HoldShortAnnotator.CombinedName
        );

        var stop = new LatLon(Assert.NotNull(hsA.Latitude), Assert.NotNull(hsA.Longitude));
        (double alongFt, double offFt) = arc.Locate(arcSeg.Edge.ToNode, stop);
        output.WriteLine(
            $"standing {setbackFt + ShortOfStopFt:F2} ft back from #{arcSeg.ToNodeId} on the arc: stop {alongFt:F2} ft back, "
                + $"{offFt:F2} ft off the arc, {Ft(standing, stop):F2} ft from the aircraft"
        );
        Assert.True(offFt <= OnArcToleranceFt, $"the stop lies {offFt:F2} ft off the fillet arc");
        Assert.True(Math.Abs(alongFt - setbackFt) <= OnArcToleranceFt, $"the stop moved to {alongFt:F2} ft back, not {setbackFt:F2}");
    }

    /// <summary>
    /// The route driven the other way over the same fillet (A back onto A2): a stop set back from the arc's far node lies on
    /// the arc, measured along it from that node.
    /// </summary>
    [Fact]
    public void StopOnTheArc_ReverseTraversal_LiesOnTheArc()
    {
        if (BuildCosRoute(AircraftType) is not { } cos)
        {
            return;
        }

        HoldShortPoint hsA = HoldShortOfA(cos.Route);
        (TaxiRouteSegment arcSeg, GroundArc arc) = ApproachArc(cos.Route, hsA.NodeId);
        var reversed = new TaxiRoute
        {
            Segments =
            [
                .. cos
                    .Route.Segments.AsEnumerable()
                    .Reverse()
                    .Select(s => new TaxiRouteSegment { TaxiwayName = s.TaxiwayName, Edge = s.Edge.Edge.Directed(s.Edge.ToNode, s.Edge.FromNode) }),
            ],
            HoldShortPoints = [],
        };
        double setbackFt = TaxiwaySetbackFt(AircraftType);

        GroundNode stop = VirtualNode.OffsetBefore(cos.Layout, reversed, arcSeg.FromNodeId, setbackFt / GeoMath.FeetPerNm, HoldShortStopKind.Taxiway);
        (double alongFt, double offFt) = arc.Locate(arcSeg.Edge.FromNode, stop.Position);
        output.WriteLine($"reverse: stop {alongFt:F2} ft along the arc from #{arcSeg.FromNodeId}, {offFt:F2} ft off it");
        Assert.True(offFt <= OnArcToleranceFt, $"the reverse stop lies {offFt:F2} ft off the fillet arc");
        Assert.True(Math.Abs(alongFt - setbackFt) <= OnArcToleranceFt, $"the reverse stop is {alongFt:F2} ft along the arc, not {setbackFt:F2}");
    }

    /// <summary>
    /// A stop just past a runway (<see cref="HoldShortStopKind.TaxiwayJustPastRunway"/>) on the fillet that starts at A2's
    /// 17R/35L bar: a setback <see cref="InsideArcFt"/> shorter than the arc — but longer than its chord — stays on the arc, judged against the
    /// arc's length; one longer than the arc is clamped at the runway bar.
    /// </summary>
    [Fact]
    public void RunwayHoldShortClamp_OnTheFillet_IsJudgedOnTheArcLength()
    {
        if (BuildCosRoute(AircraftType) is not { } cos)
        {
            return;
        }

        HoldShortPoint hsA = HoldShortOfA(cos.Route);
        (TaxiRouteSegment arcSeg, GroundArc arc) = ApproachArc(cos.Route, hsA.NodeId);
        GroundNode runwayBar = arcSeg.Edge.FromNode;
        Assert.Equal(GroundNodeType.RunwayHoldShort, runwayBar.Type);
        double arcFt = ArcFt(arcSeg, arc);
        double chordFt = Ft(runwayBar.Position, arcSeg.Edge.ToNode.Position);
        double insideFt = arcFt - InsideArcFt;
        Assert.True(insideFt > chordFt, $"the arc ({arcFt:F2} ft) is not {InsideArcFt:F0} ft longer than its chord ({chordFt:F2} ft)");

        GroundNode inside = VirtualNode.OffsetBefore(
            cos.Layout,
            cos.Route,
            hsA.NodeId,
            insideFt / GeoMath.FeetPerNm,
            HoldShortStopKind.TaxiwayJustPastRunway
        );
        GroundNode clamped = VirtualNode.OffsetBefore(
            cos.Layout,
            cos.Route,
            hsA.NodeId,
            (arcFt + PastArcFt) / GeoMath.FeetPerNm,
            HoldShortStopKind.TaxiwayJustPastRunway
        );
        (double alongFt, double offFt) = arc.Locate(arcSeg.Edge.ToNode, inside.Position);
        double clampedFromBarFt = Ft(clamped.Position, runwayBar.Position);
        output.WriteLine(
            $"arc {arcFt:F2} ft, chord {chordFt:F2} ft: {insideFt:F2} ft setback lands {alongFt:F2} ft along, {offFt:F2} ft off; "
                + $"{arcFt + PastArcFt:F2} ft setback lands {clampedFromBarFt:F4} ft from bar #{runwayBar.Id}"
        );
        Assert.True(offFt <= OnArcToleranceFt, $"the stop lies {offFt:F2} ft off the fillet arc");
        Assert.True(Math.Abs(alongFt - insideFt) <= OnArcToleranceFt, $"the stop is {alongFt:F2} ft along the arc, not {insideFt:F2}");
        Assert.True(clampedFromBarFt <= SameStopToleranceFt, $"the overlong setback stopped {clampedFromBarFt:F4} ft from the runway bar");
    }

    /// <summary>
    /// The gap from A's bar node back to the 17R/35L bar the route crossed (#84, the fillet's start) is measured along the
    /// fillet arc, as the setback walk and the route polyline measure it, not along its chord.
    /// </summary>
    [Fact]
    public void CrossedRunwayGap_AcrossTheFillet_IsTheArcLength()
    {
        if (BuildCosRoute(AircraftType) is not { } cos)
        {
            return;
        }

        HoldShortPoint hsA = HoldShortOfA(cos.Route);
        (TaxiRouteSegment arcSeg, GroundArc arc) = ApproachArc(cos.Route, hsA.NodeId);
        double arcFt = ArcFt(arcSeg, arc);

        (int barId, double gapNm) = Assert.NotNull(
            HoldShortAnnotator.FindCrossedRunwayHoldShort(cos.Layout, cos.Route, hsA.NodeId, (arcFt + PastArcFt) / GeoMath.FeetPerNm)
        );
        double gapFt = gapNm * GeoMath.FeetPerNm;
        output.WriteLine($"crossed bar #{barId}, gap {gapFt:F2} ft; arc {arcFt:F2} ft");
        Assert.Equal(arcSeg.FromNodeId, barId);
        Assert.True(Math.Abs(gapFt - arcFt) <= GapToleranceFt, $"the gap reads {gapFt:F2} ft, not the {arcFt:F2} ft arc");
    }

    /// <summary>
    /// A fuselage more than twice the arc long: the stop just past the 17R/35L bar is set back by the gap to that bar
    /// (<c>min(L/2, gap)</c> picks the gap), which is the arc's length. With the aircraft standing on the fillet
    /// <see cref="JustPastBarFt"/> past the bar, that stop lies behind it, so the stop is placed where the aircraft stands.
    /// A gap read on the chord would put the stop several feet ahead of the bar and leave it at the bar, behind the aircraft.
    /// </summary>
    [Fact]
    public void StopSetBackByTheGap_AircraftJustPastTheBar_IsWhereItStands()
    {
        if (BuildCosRoute(LongType) is not { } cos)
        {
            return;
        }

        HoldShortPoint hsA = HoldShortOfA(cos.Route);
        (TaxiRouteSegment arcSeg, GroundArc arc) = ApproachArc(cos.Route, hsA.NodeId);
        double lengthFt = AircraftLength.ResolveFt(LongType);
        Assert.True(lengthFt / 2.0 > ArcFt(arcSeg, arc), $"half the {LongType}'s {lengthFt:F1} ft does not exceed the arc");
        LatLon standing = arc.PointAtFt(arcSeg.Edge.FromNode, JustPastBarFt);
        cos.Route.CurrentSegmentIndex = cos.Route.Segments.IndexOf(arcSeg);

        HoldShortAnnotator.ComputeHoldShortPositions(cos.Layout, cos.Route, lengthFt, standing, HoldShortAnnotator.CombinedName);

        var stop = new LatLon(Assert.NotNull(hsA.Latitude), Assert.NotNull(hsA.Longitude));
        double fromAircraftFt = Ft(standing, stop);
        output.WriteLine(
            $"{LongType} standing {JustPastBarFt:F0} ft past bar #{arcSeg.FromNodeId}: stop {fromAircraftFt:F2} ft from it, "
                + $"{Ft(arcSeg.Edge.FromNode.Position, stop):F2} ft from the bar"
        );
        Assert.True(fromAircraftFt <= OnArcToleranceFt, $"the stop lies {fromAircraftFt:F2} ft from where the aircraft stands");
    }

    /// <summary>The KCOS layout and the aircraft's route after <c>TAXI C2 A2 A A3</c> then <c>HS A</c>.</summary>
    private sealed record CosRoute(AirportGroundLayout Layout, TaxiRoute Route);

    /// <summary>The KCOS route for an aircraft of <paramref name="aircraftType"/>; null when navdata or the layout is unavailable.</summary>
    private CosRoute? BuildCosRoute(string aircraftType)
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if ((TestVnasData.NavigationDb is not { } navDb) || (groundData.GetLayout("COS") is not { } layout))
        {
            output.WriteLine("SKIP: navdata or KCOS layout unavailable");
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        using IDisposable _ = NavigationDatabase.ScopedOverride(Issue899DetourConnectorTests.CosDeclaring(navDb, "A2", "C2", "17R/35L"));
        string runway = Issue1334OakDToFAcross33Tests.RunwayCenterline(layout, "35L");
        (GroundNode start, GroundNode join) = Issue1334OakDToFAcross33Tests.StartAwayFromRunwayJoin(output, layout, "COS", "C2", runway);
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-yaat-1469-cos-fillet-stop",
                ScenarioName = "COS C2/A2/A/A3, HS A on a fillet arc",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "COS",
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(layout, "COS", start, GeoMath.BearingTo(start.Position, join.Position));
        aircraft.AircraftType = aircraftType;
        engine.World.AddAircraft(aircraft);
        CommandResult taxi = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "TAXI C2 A2 A A3");
        Assert.True(taxi.Success, taxi.Message);
        CommandResult hs = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "HS A");
        Assert.True(hs.Success, hs.Message);

        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        return new CosRoute(layout, route);
    }

    private static HoldShortPoint HoldShortOfA(TaxiRoute route) => Assert.Single(route.HoldShortPoints, h => h.TargetName == "A");

    /// <summary>The route segment that ends at <paramref name="barNodeId"/>, which must be a fillet arc, and its arc.</summary>
    private static (TaxiRouteSegment Segment, GroundArc Arc) ApproachArc(TaxiRoute route, int barNodeId)
    {
        TaxiRouteSegment seg = route.Segments.First(s => s.ToNodeId == barNodeId);
        return (seg, Assert.IsType<GroundArc>(seg.Edge.Edge));
    }

    /// <summary>The arc's length (ft) walked along the curve.</summary>
    private static double ArcFt(TaxiRouteSegment arcSeg, GroundArc arc) => arc.Locate(arcSeg.Edge.ToNode, arcSeg.Edge.FromNode.Position).AlongFt;

    /// <summary><see cref="PastArcFt"/> down the straight edge before <paramref name="arcSeg"/>, from the arc's start node.</summary>
    private static LatLon PastTheArcStart(TaxiRoute route, TaxiRouteSegment arcSeg)
    {
        TaxiRouteSegment straight = route.Segments.First(s => s.ToNodeId == arcSeg.FromNodeId);
        Assert.IsNotType<GroundArc>(straight.Edge.Edge);
        LatLon arcStart = straight.Edge.ToNode.Position;
        Assert.True(Ft(straight.Edge.FromNode.Position, arcStart) > PastArcFt, "the straight edge before the arc is shorter than the overrun");
        (double lat, double lon) = GeoMath.ProjectPointRaw(
            arcStart.Lat,
            arcStart.Lon,
            GeoMath.BearingTo(arcStart, straight.Edge.FromNode.Position),
            PastArcFt / GeoMath.FeetPerNm
        );
        return new LatLon(lat, lon);
    }

    /// <summary>The taxiway hold-short setback (ft) the walk is asked for: the aircraft's length plus the taxiway buffer.</summary>
    private static double TaxiwaySetbackFt(string aircraftType) =>
        AircraftLength.ResolveFt(aircraftType) + HoldShortAnnotator.TaxiwayHoldShortBufferFt;

    private static double Ft(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;
}
