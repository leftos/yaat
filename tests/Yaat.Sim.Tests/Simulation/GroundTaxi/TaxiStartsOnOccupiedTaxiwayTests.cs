using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Fillet;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Issue #880: an aircraft resting mid-edge on a long straight taxiway, far from any graph node, had its TAXI start
/// node picked by distance to nodes alone. At KOAK, an aircraft on C's centreline west of H was 333 ft from a node on
/// the parallel D and 376 ft from the nearest C node, so its route began with a free-space leg onto D. At KSFO the
/// same pose mid-B started the route on A and "TAXI B" was refused. A taxi starts on the taxiway the aircraft is on.
/// </summary>
public class TaxiStartsOnOccupiedTaxiwayTests(ITestOutputHelper output)
{
    private const int TaxiTickSeconds = 20;
    private const double MaxTurnFromStartDeg = 30.0;
    private const double MinTaxiSpeedKts = 10.0;

    /// <summary>How far past a straight edge's end, into the fillet arc that continues it, the aircraft stands.</summary>
    private const double IntoFilletFt = 20.0;

    /// <summary>Fillet arcs from this long: the arc's far end is clear of the at-node tolerance of the aircraft 20 ft into it.</summary>
    private const double MinFilletFt = 40.0;

    /// <summary>Fillet arcs up to this long: a junction fillet, not a long high-speed exit curve.</summary>
    private const double MaxFilletFt = 400.0;

    /// <summary>How many C nodes the walk behind the aircraft visits looking for a taxiway off C.</summary>
    private const int MaxWalkNodes = 30;

    [Fact]
    public void TaxiOnC_WestOfH_MidEdge_RouteStartsOnC()
    {
        AirportGroundLayout? layout = LoadLayout("OAK");
        if (layout is null)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(layout);

        LatLon position = Along(tangentCut.Position, farEnd.Position, 0.35);
        AirportGroundLayout.NearestTaxiEdge? onEdge = layout.FindNearestTaxiEdge(position);
        Assert.NotNull(onEdge);
        Assert.True(onEdge.Value.Edge.MatchesTaxiway("C"), $"nearest taxi edge is {onEdge.Value.Edge.TaxiwayName}, not C");

        double headingDeg = GeoMath.BearingTo(farEnd.Position, tangentCut.Position);
        AircraftState aircraft = MakeAircraft(layout, position, headingDeg, "OAK", "C");

        TaxiRoute route = Resolve(layout, aircraft, "TAXI C B RWY 28R HS H");

        GroundNode firstFrom = FirstLayoutSegment(route).Edge.FromNode;
        Assert.True(OnStraightEdgeOf(firstFrom, "C"), $"route joins the graph at node {firstFrom.Id}, not on C");
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.MatchesTaxiway("D"));
        Assert.Equal("C", route.FormatTaxiwaySequence().Split(' ')[0]);

        HoldShortPoint holdH = Assert.Single(route.HoldShortPoints, hs => string.Equals(hs.TargetName, "H", StringComparison.OrdinalIgnoreCase));
        Assert.True(OnStraightEdgeOf(layout.Nodes[holdH.NodeId], "C"), $"HS H is bound at node {holdH.NodeId}, which has no straight C edge");
    }

    /// <summary>
    /// A C172 mid-C at KOAK, facing toward H, cleared to a taxiway that branches off C behind it. The route from the C node
    /// ahead would come straight back over the edge the aircraft stands on, so it turns about on C: segment 0 is the
    /// free-space leg back to the edge's far node, and no segment drives that edge. Ticked through the turn about and the
    /// leg back to the far node, it pivots no faster than ω·r at its turn-about radius, ends facing the far node, and its
    /// centre stays within C's half-width of the centreline.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_TurnsAboutToTheFarNode() => TurnAboutOnC("C172", AircraftCategory.Piston);

    /// <summary>
    /// The same pose and clearance in a C208, a turboprop the FAA aircraft characteristics database puts in TDG 1A: it turns
    /// about on C at its own turn-about radius and pivot speed, its centre within its own TDG 1A half-width of the centreline.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_TurbopropTurnsAboutWithinTheTaxiway() => TurnAboutOnC("C208", AircraftCategory.Turboprop);

    /// <summary>
    /// A <paramref name="type"/> mid-C facing H cleared to the taxiway off C behind it, ticked through the turn about (speed
    /// bound) and then through the leg back to the far node (peak centre offset from C's centreline), which must stay within
    /// the half-width of a TDG 1A taxiway.
    /// </summary>
    private void TurnAboutOnC(string type, AircraftCategory category)
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        AirportGroundLayout layout = ground.Layout;
        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(layout);
        IGroundEdge occupied = tangentCut.Edges.First(e => (e is GroundEdge) && (e.OtherNode(tangentCut) == farEnd));
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, type);
        Assert.Equal(category, AircraftCategorization.Categorize(type));

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine($"{type}: occupied C edge {tangentCut.Id}-{farEnd.Id}; {command}: {result.Success} — {result.Message}");
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");

        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);
        TaxiRouteSegment first = route.Segments[0];
        Assert.True(VirtualNode.IsVirtualEdge(first.Edge.Edge), $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(farEnd.Id, first.ToNodeId);
        Assert.DoesNotContain(route.Segments, s => ReferenceEquals(s.Edge.Edge, occupied));

        double maxTurnSpeedKts = 0.0;
        double peakOffsetFt = 0.0;
        bool FacesFarEnd() =>
            GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, GeoMath.BearingTo(aircraft.Position, farEnd.Position)) <= MaxTurnFromStartDeg;
        void TrackOffset() => peakOffsetFt = Math.Max(peakOffsetFt, CentreOffsetFt(aircraft.Position, tangentCut, farEnd));
        int facedAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            FacesFarEnd,
            TurnAboutTickSeconds,
            _ =>
            {
                maxTurnSpeedKts = FacesFarEnd() ? maxTurnSpeedKts : Math.Max(maxTurnSpeedKts, aircraft.GroundSpeed);
                TrackOffset();
            }
        );
        output.WriteLine($"faced node {farEnd.Id} after {facedAt}s; max {maxTurnSpeedKts:F2} kt while turning, peak {peakOffsetFt:F1} ft off C");
        Assert.True(facedAt > 0, $"the aircraft did not face node {farEnd.Id} within {TurnAboutTickSeconds}s");

        int legDoneAt = SfoGroundHarness.TickUntil(ground.Engine, () => route.CurrentSegmentIndex > 0, TurnAboutLegTickSeconds, _ => TrackOffset());
        output.WriteLine($"left the turn-about leg {legDoneAt}s later; peak {peakOffsetFt:F2} ft off C's centreline");
        Assert.True(legDoneAt > 0, $"the aircraft did not finish the leg back to node {farEnd.Id} within {TurnAboutLegTickSeconds}s");

        double pivotKts = PivotSpeedKts(type, category);
        Assert.True(
            maxTurnSpeedKts <= pivotKts + TurnSpeedOvershootKts,
            $"the aircraft turned about at up to {maxTurnSpeedKts:F2} kt, above the {pivotKts:F2} kt pivot speed (ω·r at its "
                + $"{TurnAboutFit.Evaluate(type, category).RadiusFt:F1} ft turn-about radius) plus {TurnSpeedOvershootKts:F1} kt"
        );
        Assert.True(
            peakOffsetFt <= (Tdg1AHalfWidthFt + CentreTrackingToleranceFt),
            $"the aircraft's centre swung {peakOffsetFt:F2} ft off C's centreline turning about, beyond {Tdg1AHalfWidthFt:F1} ft, "
                + $"{Tdg1AHalfWidthSource}, plus the {CentreTrackingToleranceFt:F1} ft tracking tolerance"
        );
    }

    /// <summary>
    /// A C172 rolling at taxi speed toward H along KOAK C, mid-edge, re-routed to the taxiway off C behind it. It
    /// brakes along C to its pivot speed first and only then turns about, so the arc starts at no more than ω·r on its
    /// turn-about radius (plus the physics overshoot) and its centre stays within C's TDG 1A half-width throughout.
    /// </summary>
    [Fact]
    public void RollingTurnAboutOnC_BrakesToPivotSpeedFirst() => RollingTurnAboutOnC(reRouteNearTheNode: false);

    /// <summary>
    /// The same rolling C172 re-routed so near the C node ahead that braking to pivot speed at the firm rate leaves
    /// no room to turn about before that node (the stopping distance at <see cref="CategoryPerformance.ExpediteExitDecelRate"/>
    /// plus the turn-about radius reaches past it). It cannot reach its pivot speed by that node, so it rolls on past it onto C
    /// beyond (the navigator's roll-past line, never its roll-on-to-the-node line) and turns about there on its own tight radius,
    /// never short of the node: never faster than the pivot speed on the arc, never wider than C's TDG 1A half-width.
    /// </summary>
    [Fact]
    public void RollingTurnAboutOnC_TooShortAtTheFirmRate_TurnsAboutAtOrPastTheNodeAhead() => RollingTurnAboutOnC(reRouteNearTheNode: true);

    /// <summary>
    /// A C172 started near C's far end facing H, cleared along C ahead to the first taxiway off it past H and ticked to taxi
    /// speed, then re-routed to the taxiway off C behind it: mid-edge, or, when <paramref name="reRouteNearTheNode"/>, once
    /// the C node ahead is closer than the firm-rate stopping distance plus the turn-about radius. Ticked through the brake,
    /// the turn about and the leg back to the far node.
    /// </summary>
    private void RollingTurnAboutOnC(bool reRouteNearTheNode)
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        const string Type = "C172";
        const AircraftCategory Category = AircraftCategory.Piston;
        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        double edgeFt = GeoMath.DistanceNm(tangentCut.Position, farEnd.Position) * GeoMath.FeetPerNm;
        LatLon start = Along(tangentCut.Position, farEnd.Position, RollingStartFraction);
        AircraftState aircraft = MakeAircraft(ground.Layout, start, GeoMath.BearingTo(farEnd.Position, tangentCut.Position), "OAK", "C");
        aircraft.AircraftType = Type;
        ground.Engine.World.AddAircraft(aircraft);
        SendAndLog(ground, aircraft, $"TAXI C {FirstTaxiwayOffCBeyond(tangentCut, farEnd)}");

        double radiusFt = TurnAboutFit.Evaluate(Type, Category).RadiusFt;
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(Category);
        double ToNodeAheadFt() => GeoMath.DistanceNm(aircraft.Position, tangentCut.Position) * GeoMath.FeetPerNm;
        bool ReadyToReRoute() =>
            reRouteNearTheNode
                ? ((aircraft.GroundSpeed >= NearTheNodeMinKts) && (ToNodeAheadFt() < (StoppingDistanceFt(aircraft.GroundSpeed, firmRate) + radiusFt)))
                : aircraft.GroundSpeed >= MinTaxiSpeedKts;
        int rollingAt = SfoGroundHarness.TickUntil(ground.Engine, ReadyToReRoute, RollUpTickSeconds, null);
        Assert.True(rollingAt > 0, $"the aircraft was not ready to re-route within {RollUpTickSeconds}s ({aircraft.GroundSpeed:F1} kt)");
        double rollingKts = aircraft.GroundSpeed;
        double rollingBearingDeg = GeoMath.BearingTo(farEnd.Position, tangentCut.Position);
        LatLon reRoutedAt = aircraft.Position;
        double reRoutedToNodeFt = ToNodeAheadFt();
        output.WriteLine(
            $"C edge {tangentCut.Id}-{farEnd.Id} ({edgeFt:F0} ft): rolling at {rollingKts:F1} kt after {rollingAt}s, "
                + $"{ToNodeAheadFt():F0} ft short of node {tangentCut.Id}; firm-rate stop {StoppingDistanceFt(rollingKts, firmRate):F0} ft"
        );

        DebugLogCapture? brakePlan = reRouteNearTheNode
            ? DebugLogCapture.Install(TaxiwayTurnAboutAimTests.RollOnToTheNodeLine, TaxiwayTurnAboutAimTests.RollPastTheNodeLine)
            : null;
        SendAndLog(ground, aircraft, $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);

        double? arcStartKts = null;
        double? arcStartToNodeFt = null;
        double brakeFt = 0.0;
        double maxTurnSpeedKts = 0.0;
        double peakOffsetFt = 0.0;
        bool FacesFarEnd() =>
            GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, GeoMath.BearingTo(aircraft.Position, farEnd.Position)) <= MaxTurnFromStartDeg;
        void Track()
        {
            peakOffsetFt = Math.Max(peakOffsetFt, CentreOffsetFt(aircraft.Position, tangentCut, farEnd));
            bool turning = GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, rollingBearingDeg) > TurnStartDeg;
            if (!turning && (arcStartKts is null))
            {
                brakeFt = GeoMath.DistanceNm(reRoutedAt, aircraft.Position) * GeoMath.FeetPerNm;
                return;
            }

            arcStartKts ??= aircraft.GroundSpeed;
            arcStartToNodeFt ??= ToNodeAheadFt();
            maxTurnSpeedKts = Math.Max(maxTurnSpeedKts, arcStartKts.Value);
            maxTurnSpeedKts = FacesFarEnd() ? maxTurnSpeedKts : Math.Max(maxTurnSpeedKts, aircraft.GroundSpeed);
        }

        int legDoneAt = SfoGroundHarness.TickUntil(ground.Engine, () => route.CurrentSegmentIndex > 0, TurnAboutLegTickSeconds, _ => Track());
        double pivotKts = PivotSpeedKts(Type, Category);
        output.WriteLine(
            $"braked {brakeFt:F1} ft from {rollingKts:F1} kt; arc started at {arcStartKts:F2} kt, {arcStartToNodeFt:F1} ft from node "
                + $"{tangentCut.Id}; max {maxTurnSpeedKts:F2} kt turning (pivot {pivotKts:F2} kt); peak {peakOffsetFt:F2} ft off C; "
                + $"leg done after {legDoneAt}s"
        );

        Assert.True(legDoneAt > 0, $"the aircraft did not finish the leg back to node {farEnd.Id} within {TurnAboutLegTickSeconds}s");
        Assert.True(
            maxTurnSpeedKts <= pivotKts + TurnSpeedOvershootKts,
            $"the aircraft turned about at up to {maxTurnSpeedKts:F2} kt (arc started at {arcStartKts:F2} kt), above the "
                + $"{pivotKts:F2} kt pivot speed (ω·r at its {radiusFt:F1} ft turn-about radius) plus {TurnSpeedOvershootKts:F1} kt"
        );
        Assert.True(
            peakOffsetFt <= (Tdg1AHalfWidthFt + CentreTrackingToleranceFt),
            $"the aircraft's centre swung {peakOffsetFt:F2} ft off C's centreline turning about, beyond {Tdg1AHalfWidthFt:F1} ft, "
                + $"{Tdg1AHalfWidthSource}, plus the {CentreTrackingToleranceFt:F1} ft tracking tolerance"
        );
        if (brakePlan is not null)
        {
            output.WriteLine(string.Join(Environment.NewLine, brakePlan.Lines));
            Assert.Contains(brakePlan.Lines, l => l.Contains(TaxiwayTurnAboutAimTests.RollPastTheNodeLine, StringComparison.Ordinal));
            Assert.DoesNotContain(brakePlan.Lines, l => l.Contains(TaxiwayTurnAboutAimTests.RollOnToTheNodeLine, StringComparison.Ordinal));
            Assert.True(
                brakeFt >= (reRoutedToNodeFt - NodeAheadReachedFt),
                $"the turn about began {arcStartToNodeFt:F1} ft from node {tangentCut.Id} after braking {brakeFt:F1} ft of the "
                    + $"{reRoutedToNodeFt:F1} ft to it: short of the node ahead it rolled on to"
            );
        }
    }

    /// <summary>How far along C, from the tangent cut toward the far end, the rolling C172 starts: room to reach taxi speed mid-edge.</summary>
    private const double RollingStartFraction = 0.85;

    /// <summary>
    /// The least speed (kts) the C172 re-routed near the node ahead must still be rolling at: it slows for the corner its
    /// route ahead turns at that node, so it is re-routed well under taxi speed but still several times its pivot speed.
    /// </summary>
    private const double NearTheNodeMinKts = 3.0;

    /// <summary>How long the rolling C172 may take to reach taxi speed, or the node ahead's firm-rate reach.</summary>
    private const int RollUpTickSeconds = 120;

    /// <summary>How far (deg) the heading turns off the rolling bearing before the turn about counts as begun.</summary>
    private const double TurnStartDeg = 3.0;

    /// <summary>
    /// How far (ft) short of the node ahead a turn about that rolled on to it may begin: a second's roll at its pivot speed and more.
    /// </summary>
    private const double NodeAheadReachedFt = 5.0;

    /// <summary>How far (ft) an aircraft rolls braking from <paramref name="speedKts"/> to a stop at <paramref name="decelKtsPerSec"/>.</summary>
    private static double StoppingDistanceFt(double speedKts, double decelKtsPerSec) =>
        (speedKts * speedKts) / (2.0 * decelKtsPerSec) * GeoMath.FeetPerNm / 3600.0;

    /// <summary>Half the 25 ft width of a TDG 1A taxiway (AC 150/5300-13B).</summary>
    private const double Tdg1AHalfWidthFt = 12.5;

    /// <summary>Where that width comes from, for the failure messages of the turn-about tests.</summary>
    private const string Tdg1AHalfWidthSource = "the TDG 1A taxiway half-width (AC 150/5300-13B: 25 ft wide)";

    /// <summary>
    /// How far past the bound the centre may swing turning about on C: the half-foot the playback's settling onto the reversal
    /// arc and the straight after it adds to the turn-about radius.
    /// </summary>
    private const double CentreTrackingToleranceFt = 0.5;

    /// <summary>
    /// The speed a turn about on a taxiway pivots at: the gear-limited turn rate held on the type's turn-about radius
    /// (<see cref="TurnAboutFit"/>), v = ω·r.
    /// </summary>
    private static double PivotSpeedKts(string type, AircraftCategory category) =>
        CategoryPerformance.GroundTurnRate(category)
        * (Math.PI / 180.0)
        * TurnAboutFit.Evaluate(type, category).RadiusFt
        * 3600.0
        / GeoMath.FeetPerNm;

    /// <summary>How far <paramref name="position"/> lies off the centreline through <paramref name="a"/> and <paramref name="b"/>.</summary>
    private static double CentreOffsetFt(LatLon position, GroundNode a, GroundNode b) =>
        Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position, a.Position, new TrueHeading(GeoMath.BearingTo(a.Position, b.Position))))
        * GeoMath.FeetPerNm;

    /// <summary>
    /// A C172 rolling toward the C node ahead at taxi speed, re-routed to the taxiway off C behind it within its turn-about
    /// radius of that node, where the route starts at the node and drives C back. Too near the edge's end to turn about
    /// where it stands, it still brakes to its pivot speed before it turns: the turn about starts where its braking ends,
    /// never as a wide arc at taxi speed.
    /// </summary>
    [Fact]
    public void RollingTurnAboutOnC_WithinTheTurnRadiusOfTheNodeAhead_BrakesToPivotSpeedFirst()
    {
        const string Type = "C172";
        double radiusFt = TurnAboutFit.Evaluate(Type, AircraftCategory.Piston).RadiusFt;
        if (RollIntoTurnAboutOnC(Type, radiusFt / 2.0, WithinTheRadiusTaxiSpeedKts, isScenarioScripted: false) is not { } run)
        {
            return;
        }

        Assert.True(
            run.MaxTurnKts <= (run.PivotKts + TurnSpeedOvershootKts),
            $"the C172 turned about at up to {run.MaxTurnKts:F2} kt, above its {run.PivotKts:F2} kt pivot speed plus {TurnSpeedOvershootKts:F1} kt"
        );
    }

    /// <summary>
    /// A C172 rolling at N152SP's recorded 20 kt, re-routed to the taxiway off C behind it with less than its firm-rate stop
    /// to the C node ahead: it cannot reach its pivot speed before that node, so it rolls on past it and turns about where
    /// its braking ends. It never turns about from a pose it has already rolled past: its progress along C never runs back
    /// while it still faces the way it rolled (no sideways slide back to the node).
    /// </summary>
    [Fact]
    public void RollingTurnAboutOnC_LessThanItsFirmStopToTheNodeAhead_NeverSlidesBackToIt()
    {
        double firmStopFt = StoppingDistanceFt(OverrunTaxiSpeedKts, CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Piston));
        if (RollIntoTurnAboutOnC("C172", firmStopFt - OverrunFt, OverrunTaxiSpeedKts, isScenarioScripted: false) is not { } run)
        {
            return;
        }

        Assert.True(
            run.MaxSlideBackFt <= SlideBackToleranceFt,
            $"the C172 slid {run.MaxSlideBackFt:F1} ft back along C while still facing the way it rolled: it turned about from a pose it had passed"
        );
        Assert.True(
            run.MaxTurnKts <= (run.PivotKts + TurnSpeedOvershootKts),
            $"the C172 turned about at up to {run.MaxTurnKts:F2} kt, above its {run.PivotKts:F2} kt pivot speed plus {TurnSpeedOvershootKts:F1} kt"
        );
    }

    /// <summary>
    /// A B738 rolling along C at 15 kt, turned about by a scenario preset's TAXI to the taxiway off C behind it so near the C
    /// node ahead that its taxi-rate stop leaves no room to turn about before that node and its firm-rate stop would. A jet
    /// never brakes firmly for a routine turn about: its speed never falls faster than its taxi rate.
    /// </summary>
    [Fact]
    public void RollingTurnAboutOnC_Jet_NeverBrakesAboveItsTaxiRate()
    {
        const string Type = "B738";
        const AircraftCategory Category = AircraftCategory.Jet;
        double radiusFt = TurnAboutFit.Evaluate(Type, Category).RadiusFt;
        double taxiRate = CategoryPerformance.TaxiDecelRate(Category);
        double firmStopFt = StoppingDistanceFt(JetTaxiSpeedKts, CategoryPerformance.ExpediteExitDecelRate(Category));
        double shortOfNodeFt = radiusFt + ((StoppingDistanceFt(JetTaxiSpeedKts, taxiRate) + firmStopFt) / 2.0);
        if (RollIntoTurnAboutOnC(Type, shortOfNodeFt, JetTaxiSpeedKts, isScenarioScripted: true) is not { } run)
        {
            return;
        }

        Assert.True(
            run.MaxDecelKtsPerSec <= (taxiRate + DecelToleranceKtsPerSec),
            $"the B738 slowed by {run.MaxDecelKtsPerSec:F2} kt in one second, faster than its {taxiRate:F1} kt/s taxi rate"
        );
    }

    /// <summary>The C172's speed (kts) when re-routed within its turn-about radius of the node ahead: taxi speed.</summary>
    private const double WithinTheRadiusTaxiSpeedKts = 10.0;

    /// <summary>The C172's speed (kts) when re-routed with less than its firm-rate stop to the node ahead: N152SP's recorded speed.</summary>
    private const double OverrunTaxiSpeedKts = 20.0;

    /// <summary>How far (ft) the C172's firm-rate stop from <see cref="OverrunTaxiSpeedKts"/> reaches past the node ahead.</summary>
    private const double OverrunFt = 10.0;

    /// <summary>How far (ft) its progress along C may run back while it still faces the way it rolled: the playback's settling.</summary>
    private const double SlideBackToleranceFt = 0.5;

    /// <summary>The B738's speed (kts) along C when the preset turns it about.</summary>
    private const double JetTaxiSpeedKts = 15.0;

    /// <summary>How much more (kts) than its taxi rate the jet's speed may fall in one second: the physics' rounding.</summary>
    private const double DecelToleranceKtsPerSec = 0.05;

    /// <summary>What a rolling turn about on C did (<see cref="RollIntoTurnAboutOnC"/>).</summary>
    /// <param name="PivotKts">The type's pivot speed (kts).</param>
    /// <param name="MaxTurnKts">The fastest (kts) it went once its heading turned off the rolling bearing, until it faced the far node.</param>
    /// <param name="MaxDecelKtsPerSec">The most (kts) its speed fell in any one second.</param>
    /// <param name="MaxSlideBackFt">The furthest (ft) its progress along the rolling bearing ran back while it faced within 90° of it.</param>
    private sealed record RollingTurnAboutRun(double PivotKts, double MaxTurnKts, double MaxDecelKtsPerSec, double MaxSlideBackFt);

    /// <summary>
    /// A <paramref name="type"/> placed <paramref name="shortOfNodeFt"/> short of the C node ahead (the tangent cut), facing
    /// it and rolling at <paramref name="speedKts"/>, cleared to the taxiway off C behind it by the controller, or by a
    /// scenario preset when <paramref name="isScenarioScripted"/>, and ticked second by second through its turn about and
    /// the leg back to the far node; null when the layout is unavailable.
    /// </summary>
    private RollingTurnAboutRun? RollIntoTurnAboutOnC(string type, double shortOfNodeFt, double speedKts, bool isScenarioScripted)
    {
        if (BuildOak() is not { } ground)
        {
            return null;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        double rollingBearingDeg = GeoMath.BearingTo(farEnd.Position, tangentCut.Position);
        var backDeg = new TrueHeading((rollingBearingDeg + 180.0) % 360.0);
        LatLon start = GeoMath.ProjectPoint(tangentCut.Position, backDeg, shortOfNodeFt / GeoMath.FeetPerNm);
        AircraftState aircraft = MakeAircraft(ground.Layout, start, rollingBearingDeg, "OAK", "C");
        aircraft.AircraftType = type;
        aircraft.IndicatedAirspeed = speedKts;
        ground.Engine.World.AddAircraft(aircraft);

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.Dispatch(
            Assert.IsType<TaxiCommand>(parsed.Value),
            aircraft,
            ground.Engine.BuildDispatchContext(aircraft, isScenarioScripted, facilityHint: null)
        );
        output.WriteLine(
            $"{type} {shortOfNodeFt:F1} ft short of node {tangentCut.Id} at {speedKts:F1} kt; {command}: {result.Success} — {result.Message}"
        );
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.Equal(farEnd.Id, route.Segments[0].ToNodeId);

        double AlongFt() => GeoMath.AlongTrackDistanceNm(aircraft.Position, farEnd.Position, new TrueHeading(rollingBearingDeg)) * GeoMath.FeetPerNm;
        double previousKts = speedKts;
        double furthestAlongFt = AlongFt();
        bool turning = false;
        bool facedAway = false;
        double maxTurnKts = 0.0;
        double maxDecelKts = 0.0;
        double maxSlideBackFt = 0.0;
        void Track()
        {
            double kts = aircraft.GroundSpeed;
            maxDecelKts = Math.Max(maxDecelKts, previousKts - kts);
            previousKts = kts;
            double offDeg = GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, rollingBearingDeg);
            turning |= offDeg > TurnStartDeg;
            bool facesFarEnd =
                GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, GeoMath.BearingTo(aircraft.Position, farEnd.Position))
                <= MaxTurnFromStartDeg;
            maxTurnKts = (turning && !facesFarEnd) ? Math.Max(maxTurnKts, kts) : maxTurnKts;
            facedAway |= offDeg >= 90.0;
            if (!facedAway)
            {
                double alongFt = AlongFt();
                maxSlideBackFt = Math.Max(maxSlideBackFt, furthestAlongFt - alongFt);
                furthestAlongFt = Math.Max(furthestAlongFt, alongFt);
            }
        }

        int legDoneAt = SfoGroundHarness.TickUntil(ground.Engine, () => route.CurrentSegmentIndex > 0, TurnAboutLegTickSeconds, _ => Track());
        var run = new RollingTurnAboutRun(PivotSpeedKts(type, AircraftCategorization.Categorize(type)), maxTurnKts, maxDecelKts, maxSlideBackFt);
        output.WriteLine($"{run}; leg done after {legDoneAt}s");
        Assert.True(legDoneAt > 0, $"the aircraft did not finish the leg back to node {farEnd.Id} within {TurnAboutLegTickSeconds}s");
        return run;
    }

    /// <summary>
    /// A C172 rolling at <see cref="BarTaxiSpeedKts"/> toward the runway holding position at the end of a long KOAK stub,
    /// re-routed to the taxiway behind it so near the holding position that a turn about where its taxi-rate braking ends
    /// would carry its nose over the hold line. It brakes at the firm rate instead and turns about short of the bar at its
    /// pivot speed, its fuselage nose short of the hold line throughout, and says nothing.
    /// </summary>
    [Fact]
    public void RollingTowardARunwayBar_TaxiRateTooNear_BrakesFirmAndTurnsAboutShortOfTheLine()
    {
        const string Type = "C172";
        double taxiBrakeFt = BrakeToPivotFt(Type, BarTaxiSpeedKts, CategoryPerformance.TaxiDecelRate(AircraftCategory.Piston));
        if (RollTowardARunwayBar(Type, taxiBrakeFt + InsideTheReachFt, BarTaxiSpeedKts, isScenarioScripted: false) is not { } run)
        {
            return;
        }

        AssertTurnedAboutShortOfTheLine(run);
    }

    /// <summary>
    /// A B738 rolling at <see cref="JetBarSpeedKts"/> toward the same runway holding position, turned about by a scenario
    /// preset's TAXI with room to turn about short of the hold line where its taxi-rate braking ends, or, when
    /// <paramref name="taxiRateLeavesRoom"/> is false, only where its firm-rate braking ends. It turns about short of the line
    /// either way, and brakes no harder than its taxi rate unless only the firm rate leaves the room.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RollingTowardARunwayBar_Jet_BrakesFirmOnlyWhenTheTaxiRateLeavesNoRoom(bool taxiRateLeavesRoom)
    {
        const string Type = "B738";
        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet);
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet);
        double taxiBrakeFt = BrakeToPivotFt(Type, JetBarSpeedKts, taxiRate);
        double firmBrakeFt = BrakeToPivotFt(Type, JetBarSpeedKts, firmRate);
        double brakeFt = taxiRateLeavesRoom ? taxiBrakeFt + RoomToSpareFt : (taxiBrakeFt + firmBrakeFt) / 2.0;
        if (RollTowardARunwayBar(Type, BarClearanceFt(Type) + brakeFt, JetBarSpeedKts, isScenarioScripted: true) is not { } run)
        {
            return;
        }

        AssertTurnedAboutShortOfTheLine(run);
        double allowedRate = taxiRateLeavesRoom ? taxiRate : firmRate;
        Assert.True(
            run.MaxDecelKtsPerSec <= (allowedRate + DecelToleranceKtsPerSec),
            $"the B738 slowed by {run.MaxDecelKtsPerSec:F2} kt in one second, faster than the {allowedRate:F1} kt/s it needed"
        );
    }

    /// <summary>
    /// A C172 rolling at <see cref="BarTaxiSpeedKts"/> toward the runway holding position at the end of a long KOAK stub,
    /// re-routed to the taxiway behind it so near the holding position that not even its firm-rate braking leaves room to
    /// turn about short of the hold line. It stops straight ahead with its fuselage nose, half its length ahead, short of the
    /// line, holds there and says unable once; it never turns about.
    /// </summary>
    [Fact]
    public void RollingTowardARunwayBar_NoRoomShortOfTheLine_StopsShortHoldsAndSaysUnable()
    {
        const string Type = "C172";
        if (RollTowardARunwayBar(Type, NoRoomShortOfBarFt(Type), BarTaxiSpeedKts, isScenarioScripted: false) is not { } run)
        {
            return;
        }

        AssertStoppedHeldAndSaidUnable(run);
        Assert.True(
            run.MaxHalfLengthReachFt <= 0.0,
            $"the C172's nose, half its length ahead, reached {run.MaxHalfLengthReachFt:F1} ft past the hold line"
        );
    }

    /// <summary>
    /// The same C172 with no room to turn about short of the runway bar, snapshotted <paramref name="secondsBeforeSnapshot"/>
    /// into its stop and restored into a fresh engine: the restored aircraft goes on stopping and holding exactly as the
    /// uninterrupted one does. Its unable is said on the stop's first tick, once: a restore taken after that says nothing again
    /// (<paramref name="restoredUnableCalls"/> 0), one taken before it says it once (1), as the uninterrupted one does.
    /// </summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void RollingTowardARunwayBar_NoRoomShortOfTheLine_SurvivesSnapshotRoundTripMidStop(int secondsBeforeSnapshot, int restoredUnableCalls)
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        const string Type = "C172";
        (AircraftState aircraft, string command) = PlaceShortOfARunwayBar(ground, Type, NoRoomShortOfBarFt(Type), BarTaxiSpeedKts);
        int unableCalls = 0;
        ground.Engine.WarningEmitted += (_, message) =>
            unableCalls += message.Contains(UnableToTurnAround, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        DispatchTaxi(ground, aircraft, (command, false));
        for (int second = 0; second < secondsBeforeSnapshot; second++)
        {
            ground.Engine.TickOneSecond();
        }

        StateSnapshotDto snapshot = Assert.IsType<StateSnapshotDto>(
            JsonSerializer.Deserialize<StateSnapshotDto>(
                JsonSerializer.Serialize(ground.Engine.CaptureSnapshot(), RecordingJsonOptions.Default),
                RecordingJsonOptions.Default
            )
        );
        Assert.True(aircraft.GroundSpeed > StillKts, "the C172 had stopped when snapshotted: the snapshot is not mid-stop");
        Assert.Contains(
            GroundNavigatorArcRestoreTests.PlaybackObjects(JsonSerializer.SerializeToNode(snapshot, RecordingJsonOptions.Default)),
            p => p[nameof(GroundNavigatorPlaybackDto.TurnAboutHold)] is not null
        );

        SfoGround restored = BuildOak()!.Value;
        restored.Engine.RestoreFromSnapshot(snapshot);
        AircraftState restoredAircraft = Assert.IsType<AircraftState>(restored.Engine.FindAircraft(aircraft.Callsign));
        int restoredCalls = 0;
        restored.Engine.WarningEmitted += (_, message) =>
            restoredCalls += message.Contains(UnableToTurnAround, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        for (int second = 1; second <= RestoreCompareSeconds; second++)
        {
            ground.Engine.TickOneSecond();
            restored.Engine.TickOneSecond();
            Assert.Equal(aircraft.Position, restoredAircraft.Position);
            Assert.Equal(aircraft.TrueHeading.Degrees, restoredAircraft.TrueHeading.Degrees);
        }

        output.WriteLine($"after {RestoreCompareSeconds}s: {restoredAircraft.GroundSpeed:F2} kt at {restoredAircraft.Position}");
        Assert.True(restoredAircraft.GroundSpeed < StillKts, $"the restored C172 was still moving at {restoredAircraft.GroundSpeed:F2} kt");
        Assert.Equal(1, unableCalls);
        Assert.Equal(restoredUnableCalls, restoredCalls);
    }

    /// <summary>How long (s) the restored aircraft is compared with the uninterrupted one.</summary>
    private const int RestoreCompareSeconds = 30;

    /// <summary>
    /// How far (ft) short of the runway bar a <paramref name="type"/> rolling at <see cref="BarTaxiSpeedKts"/> has no room to turn
    /// about short of the hold line even at the firm rate, yet room to stop with its nose, half its length ahead, short of it.
    /// </summary>
    private static double NoRoomShortOfBarFt(string type)
    {
        Assert.True(BarClearanceFt(type) > HalfLengthFt(type), $"the {type}'s turn-about clearance does not exceed its half length");
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategorization.Categorize(type));
        return StoppingDistanceFt(BarTaxiSpeedKts, firmRate) + ((HalfLengthFt(type) + BarClearanceFt(type)) / 2.0);
    }

    /// <summary>
    /// A B744 rolling at <see cref="ShortEdgeSpeedKts"/> mid-way along a KOAK taxiway edge shorter than its turn-about radius,
    /// turned about by a scenario preset's TAXI to the edge's far node, with no straight edge beyond the node ahead long enough
    /// to turn about on (<see cref="FirstShortEdgeWithNoTurnAboutRoom"/>): no route node lies a turning diameter from any turn
    /// about it could fly. It stops straight ahead, holds and says unable once, and never sweeps a wide arc.
    /// </summary>
    [Fact]
    public void RollingTurnAboutOnAShortEdge_NoAimNode_StopsHoldsAndSaysUnable()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        const string Type = "B744";
        double radiusFt = TurnAboutFit.Evaluate(Type, AircraftCategory.Jet).RadiusFt;
        (GroundNode ahead, GroundNode far, GroundEdge edge) =
            FirstShortEdgeWithNoTurnAboutRoom(ground.Layout, radiusFt)
            ?? throw new InvalidOperationException($"no KOAK turn-about taxiway edge is shorter than {radiusFt:F1} ft with no room beyond it");
        output.WriteLine(
            $"{edge.TaxiwayName} edge {far.Id}-{ahead.Id} ({edge.DistanceNm * GeoMath.FeetPerNm:F1} ft) under a {radiusFt:F1} ft turn-about radius"
        );
        LatLon start = Along(far.Position, ahead.Position, 0.5);
        AircraftState aircraft = PlaceRolling(
            ground,
            Type,
            (start, GeoMath.BearingTo(far.Position, ahead.Position)),
            edge.TaxiwayName,
            ShortEdgeSpeedKts
        );
        RollingRun run = TrackRoll(ground, aircraft, ($"TAXI #{far.Id}", true), ahead, untilLegDone: false);
        AssertStoppedHeldAndSaidUnable(run);
        Assert.True(
            run.MaxTurnKts <= (run.PivotKts + TurnSpeedOvershootKts),
            $"the B744 turned at up to {run.MaxTurnKts:F2} kt, above its {run.PivotKts:F2} kt pivot speed plus {TurnSpeedOvershootKts:F1} kt"
        );
        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet);
        Assert.True(
            run.MaxDecelKtsPerSec <= (taxiRate + DecelToleranceKtsPerSec),
            $"the B744 slowed by {run.MaxDecelKtsPerSec:F2} kt in one second, faster than its {taxiRate:F1} kt/s taxi rate"
        );
    }

    /// <summary>
    /// A C172 rolling at <see cref="OverrunTaxiSpeedKts"/> along a straight KOAK taxiway toward a junction with no straight
    /// edge beyond it (<see cref="FirstStraightRollIntoAJunctionWithNoContinuation"/>), re-routed to the taxiway behind it with
    /// less than its firm-rate stop to the junction: it cannot reach its pivot speed by the node and has nowhere past it to turn
    /// about. It stops straight ahead, holds and says unable once, and never slides back toward the node it overran.
    /// </summary>
    [Fact]
    public void RollingTurnAboutIntoAJunctionWithNoContinuation_TooFastToTurnAtTheNode_StopsHoldsAndSaysUnable()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        double radiusFt = TurnAboutFit.Evaluate("C172", AircraftCategory.Piston).RadiusFt;
        double continuationRoomFt = Math.Max(2.0 * radiusFt, OverrunFt) + radiusFt;
        (GroundNode junction, GroundNode behind, GroundEdge edge, GroundNode beyond) =
            FirstStraightRollIntoAJunctionWithNoContinuation(ground.Layout, continuationRoomFt)
            ?? throw new InvalidOperationException(
                $"no straight KOAK taxiway edge ends at a junction with no {continuationRoomFt:F1} ft straight beyond it"
            );
        double firmStopFt = StoppingDistanceFt(OverrunTaxiSpeedKts, CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Piston));
        double bearingDeg = GeoMath.BearingTo(behind.Position, junction.Position);
        var backDeg = new TrueHeading((bearingDeg + 180.0) % 360.0);
        LatLon start = GeoMath.ProjectPoint(junction.Position, backDeg, (firmStopFt - OverrunFt) / GeoMath.FeetPerNm);
        output.WriteLine($"{edge.TaxiwayName} edge {behind.Id}-{junction.Id}: {firmStopFt - OverrunFt:F1} ft short of node {junction.Id}");
        AircraftState aircraft = PlaceRolling(ground, "C172", (start, bearingDeg), edge.TaxiwayName, OverrunTaxiSpeedKts);
        RollingRun run = TrackRoll(ground, aircraft, ($"TAXI #{behind.Id} #{beyond.Id}", false), junction, untilLegDone: false);
        AssertStoppedHeldAndSaidUnable(run);
        Assert.True(
            run.MaxSlideBackFt <= SlideBackToleranceFt,
            $"the C172 slid {run.MaxSlideBackFt:F1} ft back while still facing the way it rolled: it turned about from a pose it had passed"
        );
    }

    /// <summary>
    /// A B738 rolling at <see cref="JetOverrunSpeedKts"/> along a straight KOAK taxiway toward a junction whose straight edge
    /// beyond is too short to turn about on (<see cref="FirstStraightRollIntoAJunctionWithAShortContinuation"/>), turned about
    /// by a scenario preset's TAXI with less than its taxi-rate braking to pivot speed left to the junction: it has no room to
    /// turn about. Away from a runway bar, with pavement straight on past the node, a jet keeps its taxi rate and stops past
    /// the node rather than braking firmly short of it: its speed never falls faster than its taxi rate, it stops beyond the
    /// node, holds and says unable once.
    /// </summary>
    [Fact]
    public void RollingJetIntoAJunctionWithAShortContinuation_NoRoomToTurnAbout_KeepsItsTaxiRateAndStopsPastTheNode()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        const string Type = "B738";
        double radiusFt = TurnAboutFit.Evaluate(Type, AircraftCategory.Jet).RadiusFt;
        (GroundNode junction, GroundNode behind, GroundEdge edge, GroundNode beyond) =
            FirstStraightRollIntoAJunctionWithAShortContinuation(ground.Layout, 3.0 * radiusFt)
            ?? throw new InvalidOperationException(
                $"no straight KOAK taxiway edge ends at a junction with a straight edge beyond under {3.0 * radiusFt:F1} ft"
            );
        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet);
        double shortOfNodeFt = BrakeToPivotFt(Type, JetOverrunSpeedKts, taxiRate) - OverrunFt;
        AircraftState aircraft = PlaceShortOfAJunction(ground, Type, (junction, behind, edge), shortOfNodeFt, JetOverrunSpeedKts);
        double rollingBearingDeg = aircraft.TrueHeading.Degrees;
        RollingRun run = TrackRoll(ground, aircraft, ($"TAXI #{behind.Id} #{beyond.Id}", true), junction, untilLegDone: false);

        AssertStoppedHeldAndSaidUnable(run);
        Assert.True(
            run.MaxDecelKtsPerSec <= (taxiRate + DecelToleranceKtsPerSec),
            $"the B738 slowed by {run.MaxDecelKtsPerSec:F2} kt in one second, faster than its {taxiRate:F1} kt/s taxi rate"
        );
        double pastNodeFt =
            GeoMath.AlongTrackDistanceNm(aircraft.Position, junction.Position, new TrueHeading(rollingBearingDeg)) * GeoMath.FeetPerNm;
        Assert.True(pastNodeFt > 0.0, $"the B738 stopped {-pastNodeFt:F1} ft short of node {junction.Id}: it braked to stop short of it");
    }

    /// <summary>
    /// A B738 rolling at <see cref="JetReachSpeedKts"/> along a straight KOAK taxiway toward a junction with no straight edge
    /// beyond it long enough to turn about on (<see cref="FirstStraightRollIntoAJunctionWithNoContinuation"/>), turned about by
    /// a scenario preset's TAXI where its taxi-rate braking reaches its pivot speed half a turn-about radius short of the node:
    /// too near the node to turn about before it, and its turn about at the node would leave the junction's pavement, the
    /// corner fillets counted. It stops, holds and says unable once; it never turns about off the pavement.
    /// </summary>
    [Fact]
    public void RollingJetIntoAJunction_TurnAboutAtTheNodeLeavesItsPavement_StopsHoldsAndSaysUnable()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        const string Type = "B738";
        double radiusFt = TurnAboutFit.Evaluate(Type, AircraftCategory.Jet).RadiusFt;
        (GroundNode junction, GroundNode behind, GroundEdge edge, GroundNode beyond) =
            FirstStraightRollIntoAJunctionWithNoContinuation(ground.Layout, 3.0 * radiusFt)
            ?? throw new InvalidOperationException(
                $"no straight KOAK taxiway edge ends at a junction with no {3.0 * radiusFt:F1} ft straight beyond it"
            );
        double shortOfNodeFt = BrakeToPivotFt(Type, JetReachSpeedKts, CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet)) + (radiusFt / 2.0);
        AircraftState aircraft = PlaceShortOfAJunction(ground, Type, (junction, behind, edge), shortOfNodeFt, JetReachSpeedKts);
        RollingRun run = TrackRoll(ground, aircraft, ($"TAXI #{behind.Id} #{beyond.Id}", true), junction, untilLegDone: false);
        AssertStoppedHeldAndSaidUnable(run);
    }

    /// <summary>
    /// A C172 rolling along a KOAK taxiway toward a junction whose straight edge beyond ends at a runway holding position
    /// (<see cref="FirstStraightRollIntoAContinuationToARunwayBar"/>), re-routed back with less than its firm-rate braking to
    /// pivot speed left to the junction, and so fast that its taxi-rate braking would end where a turn about clears that
    /// edge's far end by its turning radius but not by its clearance from a runway bar (<see cref="BarClearanceFt"/>). It rolls
    /// on past the junction, brakes at the firm rate instead and turns about short of the bar, its fuselage nose short of the
    /// hold line throughout.
    /// </summary>
    [Fact]
    public void RollingTurnAboutOnAContinuationToARunwayBar_BrakesFirmAndTurnsAboutShortOfTheLine()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        const string Type = "C172";
        (GroundNode junction, GroundNode behind, GroundEdge edge, GroundNode beyond) =
            FirstStraightRollIntoAContinuationToARunwayBar(ground.Layout)
            ?? throw new InvalidOperationException(
                $"no KOAK taxiway edge ends at a junction with a straight edge beyond of {MaxBarContinuationFt:F0} ft or less to a runway bar"
            );
        GroundEdge continuation = StraightContinuation(ground.Layout, edge, junction)!;
        GroundNode bar = continuation.OtherNode(junction);
        double continuationFt = continuation.DistanceNm * GeoMath.FeetPerNm;
        double radiusFt = TurnAboutFit.Evaluate(Type, AircraftCategory.Piston).RadiusFt;
        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategory.Piston);
        double rateRatio = taxiRate / CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Piston);

        // The taxi-rate braking ends half-way between a turning radius and the bar clearance short of the bar; placed short of
        // the junction by half its firm-rate braking to pivot speed, which then ends as far past the junction as it starts short.
        double taxiPastNodeFt = continuationFt - ((BarClearanceFt(Type) + radiusFt) / 2.0);
        double shortOfNodeFt = taxiPastNodeFt * rateRatio / (2.0 - rateRatio);
        double firmPastNodeFt = Math.Max(2.0 * radiusFt, shortOfNodeFt);
        double speedKts = SpeedBrakingToPivotInFt(Type, shortOfNodeFt + taxiPastNodeFt, taxiRate);
        output.WriteLine(
            $"{continuation.TaxiwayName} edge {junction.Id}-{bar.Id} ({continuationFt:F1} ft) to runway bar {bar.Id}; taxi rate ends "
                + $"{taxiPastNodeFt:F1} ft past the junction, firm rate {firmPastNodeFt:F1} ft; bar clearance {BarClearanceFt(Type):F1} ft"
        );
        Assert.True(taxiPastNodeFt >= 2.0 * radiusFt, $"the taxi-rate braking ends {taxiPastNodeFt:F1} ft past the junction, inside two radii");
        Assert.True(
            (firmPastNodeFt + BarClearanceFt(Type)) < continuationFt,
            $"no room for the firm-rate turn about short of the bar: {firmPastNodeFt:F1} + {BarClearanceFt(Type):F1} ft on {continuationFt:F1} ft"
        );

        AircraftState aircraft = PlaceShortOfAJunction(ground, Type, (junction, behind, edge), shortOfNodeFt, speedKts);
        RollingRun run = TrackRoll(ground, aircraft, ($"TAXI #{behind.Id} #{beyond.Id}", false), bar, untilLegDone: true);
        AssertTurnedAboutShortOfTheLine(run);
    }

    /// <summary>
    /// The C172 with no room to turn about short of the runway bar, in a solo training session: its unable is a radio call to
    /// a student working ground or tower (a pilot transmission on the terminal), and a terminal warning otherwise, exactly
    /// once either way.
    /// </summary>
    [Theory]
    [InlineData("GND", true)]
    [InlineData("TWR", true)]
    [InlineData("APP", false)]
    public void RollingTowardARunwayBar_NoRoomShortOfTheLine_SoloUnableCallsGroundAndTower(string studentPosition, bool radioCall)
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        SimScenarioState scenario = ground.Engine.Scenario!;
        scenario.SoloTrainingMode = true;
        scenario.StudentPositionType = studentPosition;
        const string Type = "C172";
        (AircraftState aircraft, string command) = PlaceShortOfARunwayBar(ground, Type, NoRoomShortOfBarFt(Type), BarTaxiSpeedKts);
        int warnings = 0;
        int calls = 0;
        bool Unable(string callsign, string message) =>
            (callsign == aircraft.Callsign) && message.Contains(UnableToTurnAround, StringComparison.OrdinalIgnoreCase);
        ground.Engine.WarningEmitted += (callsign, message) => warnings += Unable(callsign, message) ? 1 : 0;
        ground.Engine.TerminalEntryEmitted += entry => calls += ((entry.Kind == "SayPilot") && Unable(entry.Callsign, entry.Message)) ? 1 : 0;
        DispatchTaxi(ground, aircraft, (command, false));
        for (int second = 0; second < RestoreCompareSeconds; second++)
        {
            ground.Engine.TickOneSecond();
        }

        output.WriteLine($"student {studentPosition}: {calls} radio calls, {warnings} warnings");
        Assert.Equal(radioCall ? 1 : 0, calls);
        Assert.Equal(radioCall ? 0 : 1, warnings);
    }

    /// <summary>
    /// The corner fillets at a real KOAK junction are pavement a turn about there may use
    /// (<see cref="GroundNavigator.StaysOnJunctionPavement"/>): the mid-point of the first fillet (by its lower node id) lying
    /// farther than a B738's pavement bound (<see cref="GroundNavigator.JunctionPavementBoundFt"/>) from every straight
    /// centreline at its first node is on that node's pavement.
    /// </summary>
    [Fact]
    public void JunctionPavement_CountsTheCornerFillets()
    {
        if (LoadLayout("OAK") is not { } layout)
        {
            return;
        }

        double boundFt = GroundNavigator.JunctionPavementBoundFt("B738", AircraftCategory.Jet);
        (GroundArc fillet, LatLon mid) = layout
            .Arcs.OrderBy(a => Math.Min(a.Nodes[0].Id, a.Nodes[1].Id))
            .Select(a => (Fillet: a, Mid: FilletMidPoint(a)))
            .First(f =>
                f.Fillet.Nodes[0]
                    .Edges.OfType<GroundEdge>()
                    .All(e => GeoMath.DistanceToSegmentFt(f.Mid, e.Nodes[0].Position, e.Nodes[1].Position) > boundFt)
            );
        output.WriteLine(
            $"fillet {fillet.TaxiwayName} {fillet.Nodes[0].Id}-{fillet.Nodes[1].Id}: mid-point more than {boundFt:F1} ft off the straights"
        );
        Assert.True(
            GroundNavigator.StaysOnJunctionPavement(fillet.Nodes[0], [mid], boundFt),
            $"the mid-point of fillet {fillet.Nodes[0].Id}-{fillet.Nodes[1].Id} is not on node {fillet.Nodes[0].Id}'s pavement"
        );
    }

    /// <summary>
    /// A brake leg's chord onto the straight edge beyond a junction is held to the junction's pavement
    /// (<see cref="GroundNavigator.ChordStaysOnJunctionPavement"/>), at the first real KOAK node (by id) where two straight
    /// turn-about taxiway edges meet within <see cref="StraightThroughMaxBendDeg"/> of straight on with no corner fillet near:
    /// the chord along their centrelines stays on it, and a chord ending <see cref="OffPavementBounds"/> pavement bounds off the
    /// edge beyond, as onto an edge bent more sharply than any continuation on KOAK or SFO, leaves it. With no fillet at the
    /// node, the chord is judged from one B738 turn-about radius past its start.
    /// </summary>
    [Fact]
    public void BrakeLegChord_OnlyAlongTheJunctionPavement_StaysOnIt()
    {
        if (LoadLayout("OAK") is not { } layout)
        {
            return;
        }

        double boundFt = GroundNavigator.JunctionPavementBoundFt("B738", AircraftCategory.Jet);
        (GroundNode node, GroundEdge from, GroundEdge to) =
            FirstStraightThroughNodeWithNoFillet(layout)
            ?? throw new InvalidOperationException("no KOAK node joins two straight edges with no fillet near");
        double reachFt = Math.Min(from.DistanceNm, to.DistanceNm) * GeoMath.FeetPerNm / 2.0;
        LatLon start = GeoMath.ProjectPoint(
            node.Position,
            new TrueHeading(GeoMath.BearingTo(node.Position, from.OtherNode(node).Position)),
            reachFt / GeoMath.FeetPerNm
        );
        double beyondDeg = GeoMath.BearingTo(node.Position, to.OtherNode(node).Position);
        LatLon onEdge = GeoMath.ProjectPoint(node.Position, new TrueHeading(beyondDeg), reachFt / GeoMath.FeetPerNm);
        LatLon offEdge = GeoMath.ProjectPoint(onEdge, new TrueHeading((beyondDeg + 90.0) % 360.0), OffPavementBounds * boundFt / GeoMath.FeetPerNm);
        output.WriteLine($"node {node.Id} between {from.TaxiwayName} and {to.TaxiwayName}: chords of {2.0 * reachFt:F0} ft, bound {boundFt:F1} ft");

        double radiusFt = TurnAboutFit.Evaluate("B738", AircraftCategory.Jet).RadiusFt;
        Assert.True(
            GroundNavigator.ChordStaysOnJunctionPavement(node, start, onEdge, boundFt, radiusFt),
            "the chord along the centrelines left the pavement"
        );
        Assert.False(
            GroundNavigator.ChordStaysOnJunctionPavement(node, start, offEdge, boundFt, radiusFt),
            "the chord off the edge beyond stayed on the pavement"
        );
    }

    /// <summary>
    /// An E145, a jet short enough that a stop just short of the node keeps its nose short of a KOAK runway bar on the straight
    /// edge beyond (no KOAK fixture fits a B738), rolling along a straight KOAK taxiway toward a junction whose straight edge
    /// beyond ends at a runway holding position (<see cref="ShortestContinuationIntoAJunction"/>, the shortest such edge
    /// beyond longer than its nose reach), turned about by a scenario
    /// preset's TAXI with no room to turn about, so fast that its taxi-rate stop past the node would put its nose, half its
    /// length ahead, over that hold line (<see cref="ContinuationOverrunFt"/>): it brakes at the firm rate instead, stops short
    /// of the node, holds and says unable once, its nose never past the hold line.
    /// </summary>
    [Fact]
    public void RollingJetIntoAJunction_RunwayBarInsideItsTaxiRateStop_BrakesFirmAndStopsShortOfTheNode()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        const string Type = "E145";
        Assert.Equal(AircraftCategory.Jet, AircraftCategorization.Categorize(Type));
        (GroundNode junction, GroundNode behind, GroundEdge edge, GroundNode beyond, GroundEdge continuation) =
            ShortestContinuationIntoAJunction(ground.Layout, HalfLengthFt(Type), barBeyond: true)
            ?? throw new InvalidOperationException(
                $"no straight KOAK taxiway edge ends at a junction with a straight edge beyond to a runway bar for an {Type}"
            );
        GroundNode bar = continuation.OtherNode(junction);
        (RollingRun run, double pastNodeFt) = RollJetPastTheNode(
            ground,
            Type,
            (junction, behind, edge, beyond),
            ContinuationOverrunFt(continuation, HalfLengthFt(Type)),
            bar
        );

        AssertStoppedHeldAndSaidUnable(run);
        AssertBrakedFirmAndStoppedShortOfTheNode(run, pastNodeFt, junction);
        Assert.True(
            run.MaxHalfLengthReachFt <= 0.0,
            $"the {Type}'s nose, half its length ahead, reached {run.MaxHalfLengthReachFt:F1} ft past the hold line at node {bar.Id}"
        );
    }

    /// <summary>
    /// A B738 rolling along a straight KOAK taxiway toward a junction whose straight edge beyond, ending at no runway holding
    /// position, is shorter than its taxi-rate stop past the node (<see cref="ShortestContinuationIntoAJunction"/>, the
    /// shortest such edge beyond), turned about by a scenario preset's TAXI with no room to turn about: it brakes at the firm
    /// rate, stops short of the node, holds and says unable once.
    /// </summary>
    [Fact]
    public void RollingJetIntoAJunction_ContinuationShorterThanItsOverrun_BrakesFirmAndStopsShortOfTheNode()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        const string Type = "B738";
        (GroundNode junction, GroundNode behind, GroundEdge edge, GroundNode beyond, GroundEdge continuation) =
            ShortestContinuationIntoAJunction(ground.Layout, 0.0, barBeyond: false)
            ?? throw new InvalidOperationException("no straight KOAK taxiway edge ends at a junction with a straight edge beyond");
        (RollingRun run, double pastNodeFt) = RollJetPastTheNode(
            ground,
            Type,
            (junction, behind, edge, beyond),
            ContinuationOverrunFt(continuation, 0.0),
            junction
        );

        AssertStoppedHeldAndSaidUnable(run);
        AssertBrakedFirmAndStoppedShortOfTheNode(run, pastNodeFt, junction);
    }

    /// <summary>
    /// A B738 rolling along a straight KOAK taxiway edge of at least <see cref="MinBarApproachEdgeFt"/> toward a junction with
    /// no straight turn-about taxiway beyond it (<see cref="HasStraightRoomBeyond"/>), turned about by a scenario preset's TAXI with its
    /// taxi-rate stop <see cref="OverrunFt"/> past the node: with nowhere straight on to stop, it stops short of the node or
    /// brakes harder than its taxi rate, holds and says unable once.
    /// </summary>
    [Fact]
    public void RollingJetIntoAJunctionWithNoStraightContinuation_NoRoomToTurnAbout_StopsShortOfTheNodeOrBrakesFirm()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        const string Type = "B738";
        (GroundNode junction, GroundNode behind, GroundEdge edge, GroundNode beyond) =
            FirstStraightRollIntoAJunction(
                ground.Layout,
                MinBarApproachEdgeFt,
                (e, j) => (j.Edges.Count > 1) && !HasStraightRoomBeyond(ground.Layout, e, j, 0.0)
            ) ?? throw new InvalidOperationException("no straight KOAK taxiway edge ends at a junction with no straight edge beyond it");
        (RollingRun run, double pastNodeFt) = RollJetPastTheNode(ground, Type, (junction, behind, edge, beyond), OverrunFt, junction);

        AssertStoppedHeldAndSaidUnable(run);
        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet);
        Assert.True(
            (pastNodeFt < 0.0) || (run.MaxDecelKtsPerSec > (taxiRate + DecelToleranceKtsPerSec)),
            $"the B738 stopped {pastNodeFt:F1} ft past node {junction.Id}, slowing by at most {run.MaxDecelKtsPerSec:F2} kt in one second "
                + $"(taxi rate {taxiRate:F1} kt/s), with no straight edge beyond it"
        );
    }

    /// <summary>
    /// A jet's stop straight ahead past a junction node is held to the junction's pavement
    /// (<see cref="GroundNavigator.StopStaysOnJunctionPavement"/>), at the first KOAK junction a straight turn-about taxiway
    /// edge rolls into whose straight edge beyond bends by <see cref="MinStopSeamBendDeg"/> or more, with no other edge there
    /// within <see cref="OtherEdgeClearDeg"/> of straight on (<see cref="IsBentStraightOn"/>): a stop where the edge beyond lies
    /// half a B738 pavement bound aside is on it, one where it lies <see cref="OffPavementBounds"/> bounds aside leaves it.
    /// </summary>
    [Fact]
    public void JetStopStraightAheadPastAJunction_OnlyNearTheBentEdgeBeyond_StaysOnItsPavement()
    {
        if (LoadLayout("OAK") is not { } layout)
        {
            return;
        }

        double boundFt = GroundNavigator.JunctionPavementBoundFt("B738", AircraftCategory.Jet);
        (GroundNode junction, GroundNode behind, GroundEdge edge, _) =
            FirstStraightRollIntoAJunction(layout, 0.0, (e, j) => IsBentStraightOn(layout, e, j, boundFt / 2.0))
            ?? throw new InvalidOperationException("no straight KOAK taxiway edge rolls into a junction with a bent straight edge beyond");
        GroundEdge next = StraightContinuation(layout, edge, junction)!;
        double rollDeg = GeoMath.BearingTo(behind.Position, junction.Position);
        double bendRad =
            GeoMath.AbsBearingDifference(GeoMath.BearingTo(junction.Position, next.OtherNode(junction).Position), rollDeg) * Math.PI / 180.0;
        LatLon from = GeoMath.ProjectPoint(junction.Position, new TrueHeading((rollDeg + 180.0) % 360.0), edge.DistanceNm / 2.0);
        LatLon StopAside(double asideFt) =>
            GeoMath.ProjectPoint(junction.Position, new TrueHeading(rollDeg), asideFt / Math.Sin(bendRad) / GeoMath.FeetPerNm);
        output.WriteLine(
            $"node {junction.Id}: {edge.TaxiwayName} {behind.Id}-{junction.Id} on to {next.TaxiwayName} {junction.Id}-{next.OtherNode(junction).Id} "
                + $"({next.DistanceNm * GeoMath.FeetPerNm:F0} ft), bent {bendRad * 180.0 / Math.PI:F1}°; bound {boundFt:F1} ft"
        );

        double radiusFt = TurnAboutFit.Evaluate("B738", AircraftCategory.Jet).RadiusFt;
        Assert.True(
            GroundNavigator.StopStaysOnJunctionPavement(junction, from, StopAside(boundFt / 2.0), boundFt, radiusFt),
            "the stop half a bound off the edge beyond left the pavement"
        );
        Assert.False(
            GroundNavigator.StopStaysOnJunctionPavement(junction, from, StopAside(OffPavementBounds * boundFt), boundFt, radiusFt),
            $"the stop {OffPavementBounds:F0} bounds off the edge beyond stayed on the pavement"
        );
    }

    /// <summary>
    /// A brake leg's chord is held to a junction's pavement only among that junction's own corner fillets, those whose tangent
    /// nodes lie within <see cref="FilletConstants.MaxTangentDistFt"/> of its node, never a neighbouring junction's
    /// (<see cref="GroundNavigator.ChordStaysOnJunctionPavement"/>): at the first KOAK node with a fillet of its own and a
    /// straight edge to a filleted node beyond that distance (<see cref="FirstJunctionEdgeToAnotherJunctionsFillets"/>), a
    /// chord to the node from half-way along that edge, off its centreline between a B738's pavement bound and its taxiway
    /// half-width, stays on the pavement.
    /// </summary>
    [Fact]
    public void BrakeLegChord_FromOffTheCentrelineBeyondTheJunctionsOwnFillets_StaysOnItsPavement()
    {
        if (LoadLayout("OAK") is not { } layout)
        {
            return;
        }

        TurnAboutFitResult fit = TurnAboutFit.Evaluate("B738", AircraftCategory.Jet);
        double boundFt = GroundNavigator.JunctionPavementBoundFt("B738", AircraftCategory.Jet);
        (GroundNode node, GroundEdge edge, double ownReachFt) =
            FirstJunctionEdgeToAnotherJunctionsFillets(layout)
            ?? throw new InvalidOperationException("no KOAK node with fillets of its own has a straight edge to another junction's fillets");
        double alongDeg = GeoMath.BearingTo(node.Position, edge.OtherNode(node).Position);
        LatLon onCentre = GeoMath.ProjectPoint(node.Position, new TrueHeading(alongDeg), edge.DistanceNm / 2.0);
        double offsetFt = (boundFt + fit.HalfWidthFt) / 2.0;
        LatLon start = GeoMath.ProjectPoint(onCentre, new TrueHeading((alongDeg + 90.0) % 360.0), offsetFt / GeoMath.FeetPerNm);
        output.WriteLine(
            $"node {node.Id}: own fillets reach {ownReachFt:F1} ft; {edge.TaxiwayName} {node.Id}-{edge.OtherNode(node).Id} "
                + $"({edge.DistanceNm * GeoMath.FeetPerNm:F0} ft); start {offsetFt:F1} ft off it "
                + $"(bound {boundFt:F1} ft, half-width {fit.HalfWidthFt:F1} ft)"
        );

        Assert.True(
            GroundNavigator.ChordStaysOnJunctionPavement(node, start, node.Position, boundFt, fit.RadiusFt),
            $"the chord from {offsetFt:F1} ft off {edge.TaxiwayName}, outside node {node.Id}'s own fillets, left its pavement"
        );
    }

    /// <summary>
    /// How sharply (deg) at least the straight edge beyond bends in
    /// <see cref="JetStopStraightAheadPastAJunction_OnlyNearTheBentEdgeBeyond_StaysOnItsPavement"/>.
    /// </summary>
    private const double MinStopSeamBendDeg = 5.0;

    /// <summary>How far (deg) off straight on every other edge at the junction of that seam leaves.</summary>
    private const double OtherEdgeClearDeg = 45.0;

    /// <summary>
    /// Whether the straight edge beyond <paramref name="junction"/> (<see cref="StraightContinuation"/>) bends by
    /// <see cref="MinStopSeamBendDeg"/> or more off <paramref name="edge"/>, is long enough for a stop straight ahead to lie
    /// <paramref name="asideFt"/> off it within its length, and every other edge at the junction leaves more than
    /// <see cref="OtherEdgeClearDeg"/> off straight on.
    /// </summary>
    private static bool IsBentStraightOn(AirportGroundLayout layout, GroundEdge edge, GroundNode junction, double asideFt)
    {
        if (StraightContinuation(layout, edge, junction) is not { } next)
        {
            return false;
        }

        double onDeg = GeoMath.BearingTo(edge.OtherNode(junction).Position, junction.Position);
        double BendDeg(IGroundEdge e) => GeoMath.AbsBearingDifference(GeoMath.BearingTo(junction.Position, e.OtherNode(junction).Position), onDeg);
        double bendDeg = BendDeg(next);
        double stopFt = asideFt / Math.Sin(bendDeg * Math.PI / 180.0);
        return (bendDeg >= MinStopSeamBendDeg)
            && ((next.DistanceNm * GeoMath.FeetPerNm) >= stopFt)
            && junction
                .Edges.OfType<GroundEdge>()
                .Where(e => !ReferenceEquals(e, edge) && !ReferenceEquals(e, next))
                .All(e => BendDeg(e) > OtherEdgeClearDeg);
    }

    /// <summary>
    /// The first KOAK node (by id) with a corner fillet of its own, one at it or at the far end of a straight edge meeting
    /// there with every node within <see cref="FilletConstants.MaxTangentDistFt"/> of it, and a straight edge to a node with
    /// a fillet longer than twice that distance and than four times as far as the farthest of those nodes lies from it: the
    /// node, that edge and how far its own fillets reach; null when there is none.
    /// </summary>
    private static (GroundNode Node, GroundEdge Edge, double OwnReachFt)? FirstJunctionEdgeToAnotherJunctionsFillets(AirportGroundLayout layout)
    {
        foreach (GroundNode node in layout.Nodes.Values.OrderBy(n => n.Id))
        {
            GroundEdge[] straights = [.. node.Edges.OfType<GroundEdge>()];
            double[] ownNodeFt =
            [
                .. straights
                    .Select(e => e.OtherNode(node))
                    .Prepend(node)
                    .SelectMany(n => n.Edges.OfType<GroundArc>())
                    .Select(a => a.Nodes.Max(n => GeoMath.DistanceNm(node.Position, n.Position) * GeoMath.FeetPerNm))
                    .Where(ft => ft <= FilletConstants.MaxTangentDistFt),
            ];
            if (ownNodeFt.Length == 0)
            {
                continue;
            }

            double ownReachFt = ownNodeFt.Max();
            GroundEdge? toFillets = straights.FirstOrDefault(e =>
                ((e.DistanceNm * GeoMath.FeetPerNm) > Math.Max(2.0 * FilletConstants.MaxTangentDistFt, 4.0 * ownReachFt))
                && e.OtherNode(node).Edges.OfType<GroundArc>().Any()
            );
            if (toFillets is not null)
            {
                return (node, toFillets, ownReachFt);
            }
        }

        return null;
    }

    /// <summary>
    /// How far (ft) a jet's firm-rate stop ends short of the node ahead in <see cref="RollJetPastTheNode"/>, and its taxi-rate
    /// stop past the far end of a continuation in <see cref="ContinuationOverrunFt"/>.
    /// </summary>
    private const double StopMarginFt = 10.0;

    /// <summary>
    /// How far (ft) past the junction a jet's taxi-rate stop must end for its nose, <paramref name="noseReachFt"/> ahead, to
    /// reach <see cref="StopMarginFt"/> past the far end of <paramref name="continuation"/>, and at least that margin past the
    /// node.
    /// </summary>
    private static double ContinuationOverrunFt(GroundEdge continuation, double noseReachFt) =>
        Math.Max(StopMarginFt, (continuation.DistanceNm * GeoMath.FeetPerNm) - noseReachFt + StopMarginFt);

    /// <summary>
    /// How far short (ft) of a junction a jet starts for its taxi-rate stop to end <paramref name="overrunFt"/> past the node
    /// and its firm-rate stop <see cref="StopMarginFt"/> short of it: with r the firm rate over the taxi rate and s the start,
    /// r(s − margin) = s + overrun.
    /// </summary>
    private static double JetShortOfNodeFt(double overrunFt)
    {
        double ratio = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet) / CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet);
        return (overrunFt + (ratio * StopMarginFt)) / (ratio - 1.0);
    }

    /// <summary>
    /// A jet of <paramref name="type"/> placed <see cref="JetShortOfNodeFt"/> short of the approach's junction, at the speed from which its firm-rate
    /// stop takes all but <see cref="StopMarginFt"/> of that, turned about by a scenario preset's TAXI back past the node
    /// behind, and watched (<see cref="TrackRoll"/>) against the line through <paramref name="lineAhead"/>: the run, and how
    /// far (ft) past the junction it ended along the way it rolled.
    /// </summary>
    private (RollingRun Run, double PastNodeFt) RollJetPastTheNode(
        SfoGround ground,
        string type,
        (GroundNode Junction, GroundNode Behind, GroundEdge Edge, GroundNode Beyond) approach,
        double overrunFt,
        GroundNode lineAhead
    )
    {
        (GroundNode junction, GroundNode behind, GroundEdge edge, GroundNode beyond) = approach;
        double shortOfNodeFt = JetShortOfNodeFt(overrunFt);
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Jet);
        double speedKts = Math.Sqrt((shortOfNodeFt - StopMarginFt) * 2.0 * firmRate * 3600.0 / GeoMath.FeetPerNm);
        output.WriteLine($"taxi-rate stop {overrunFt:F1} ft past node {junction.Id}");
        AircraftState aircraft = PlaceShortOfAJunction(ground, type, (junction, behind, edge), shortOfNodeFt, speedKts);
        var rolling = new TrueHeading(aircraft.TrueHeading.Degrees);
        RollingRun run = TrackRoll(ground, aircraft, ($"TAXI #{behind.Id} #{beyond.Id}", true), lineAhead, untilLegDone: false);
        return (run, GeoMath.AlongTrackDistanceNm(aircraft.Position, junction.Position, rolling) * GeoMath.FeetPerNm);
    }

    /// <summary>The run braked faster than the jet taxi rate and stopped short of <paramref name="junction"/>.</summary>
    private static void AssertBrakedFirmAndStoppedShortOfTheNode(RollingRun run, double pastNodeFt, GroundNode junction)
    {
        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategory.Jet);
        Assert.True(
            run.MaxDecelKtsPerSec > (taxiRate + DecelToleranceKtsPerSec),
            $"the jet slowed by at most {run.MaxDecelKtsPerSec:F2} kt in one second: it kept its {taxiRate:F1} kt/s taxi rate"
        );
        Assert.True(pastNodeFt < 0.0, $"the jet stopped {pastNodeFt:F1} ft past node {junction.Id}");
    }

    /// <summary>
    /// The straight KOAK turn-about taxiway edge, with no runway holding position at either end, toward a junction whose
    /// straight edge beyond (<see cref="StraightContinuation"/>) ends at a runway holding position when
    /// <paramref name="barBeyond"/> and at none otherwise, and is longer than <paramref name="noseReachFt"/> and
    /// <see cref="StopMarginFt"/> together (a stop that margin short of the node keeps the nose short of its far end), long
    /// enough for a jet to start <see cref="JetShortOfNodeFt"/> short of the junction for its
    /// <see cref="ContinuationOverrunFt"/>, with a node one edge beyond the node behind: the shortest such edge beyond first.
    /// The junction, the node behind, the edge,
    /// that node beyond and the edge beyond; null when there is none.
    /// </summary>
    private static (
        GroundNode Junction,
        GroundNode Behind,
        GroundEdge Edge,
        GroundNode Beyond,
        GroundEdge Continuation
    )? ShortestContinuationIntoAJunction(AirportGroundLayout layout, double noseReachFt, bool barBeyond)
    {
        var fits = new List<(GroundNode Junction, GroundNode Behind, GroundEdge Edge, GroundNode Beyond, GroundEdge Continuation)>();
        IEnumerable<GroundEdge> edges = layout
            .Edges.Where(e => GroundNavigator.IsTurnAboutTaxiway(e, layout))
            .Where(e => e.Nodes.All(n => n.Type != GroundNodeType.RunwayHoldShort));
        foreach (GroundEdge edge in edges)
        {
            foreach ((GroundNode junction, GroundNode behind) in new[] { (edge.Nodes[1], edge.Nodes[0]), (edge.Nodes[0], edge.Nodes[1]) })
            {
                if (
                    (StraightContinuation(layout, edge, junction) is { } next)
                    && ((next.OtherNode(junction).Type == GroundNodeType.RunwayHoldShort) == barBeyond)
                    && ((next.DistanceNm * GeoMath.FeetPerNm) > (noseReachFt + StopMarginFt))
                    && ((edge.DistanceNm * GeoMath.FeetPerNm) > (JetShortOfNodeFt(ContinuationOverrunFt(next, noseReachFt)) + StopMarginFt))
                    && (behind.Edges.FirstOrDefault(e => !ReferenceEquals(e, edge)) is { } back)
                )
                {
                    fits.Add((junction, behind, edge, back.OtherNode(behind), next));
                }
            }
        }

        return (fits.Count == 0) ? null : fits.MinBy(f => f.Continuation.DistanceNm);
    }

    /// <summary>
    /// How sharply (deg) two straight edges may bend at a straight-through node (<see cref="FirstStraightThroughNodeWithNoFillet"/>).
    /// </summary>
    private const double StraightThroughMaxBendDeg = 5.0;

    /// <summary>How many pavement bounds off the edge beyond the off-pavement chord ends.</summary>
    private const double OffPavementBounds = 3.0;

    /// <summary>
    /// The first KOAK node (by id) joining exactly two straight turn-about taxiway edges that continue each other within
    /// <see cref="StraightThroughMaxBendDeg"/>, with no fillet at it or at either edge's far end: the node and its two edges;
    /// null when there is none.
    /// </summary>
    private static (GroundNode Node, GroundEdge From, GroundEdge To)? FirstStraightThroughNodeWithNoFillet(AirportGroundLayout layout)
    {
        foreach (GroundNode node in layout.Nodes.Values.OrderBy(n => n.Id))
        {
            GroundEdge[] straights = [.. node.Edges.OfType<GroundEdge>().Where(e => GroundNavigator.IsTurnAboutTaxiway(e, layout))];
            bool filletNear = straights.Select(e => e.OtherNode(node)).Append(node).Any(n => n.Edges.OfType<GroundArc>().Any());
            if ((node.Edges.Count != 2) || (straights.Length != 2) || filletNear)
            {
                continue;
            }

            double inDeg = GeoMath.BearingTo(straights[0].OtherNode(node).Position, node.Position);
            double outDeg = GeoMath.BearingTo(node.Position, straights[1].OtherNode(node).Position);
            if (GeoMath.AbsBearingDifference(inDeg, outDeg) <= StraightThroughMaxBendDeg)
            {
                return (node, straights[0], straights[1]);
            }
        }

        return null;
    }

    /// <summary>The point half-way along <paramref name="fillet"/>'s curve parameter.</summary>
    private static LatLon FilletMidPoint(GroundArc fillet)
    {
        (double lat, double lon) = fillet.ToBezier().Evaluate(0.5);
        return new LatLon(lat, lon);
    }

    /// <summary>
    /// A <paramref name="type"/> placed <paramref name="shortOfNodeFt"/> short of the approach's junction on its edge, facing
    /// the junction from the node behind and rolling at <paramref name="speedKts"/>.
    /// </summary>
    private AircraftState PlaceShortOfAJunction(
        SfoGround ground,
        string type,
        (GroundNode Junction, GroundNode Behind, GroundEdge Edge) approach,
        double shortOfNodeFt,
        double speedKts
    )
    {
        (GroundNode junction, GroundNode behind, GroundEdge edge) = approach;
        double bearingDeg = GeoMath.BearingTo(behind.Position, junction.Position);
        var backDeg = new TrueHeading((bearingDeg + 180.0) % 360.0);
        LatLon start = GeoMath.ProjectPoint(junction.Position, backDeg, shortOfNodeFt / GeoMath.FeetPerNm);
        output.WriteLine(
            $"{type} at {speedKts:F1} kt, {shortOfNodeFt:F1} ft short of node {junction.Id} on {edge.TaxiwayName} edge "
                + $"{behind.Id}-{junction.Id} ({edge.DistanceNm * GeoMath.FeetPerNm:F0} ft)"
        );
        return PlaceRolling(ground, type, (start, bearingDeg), edge.TaxiwayName, speedKts);
    }

    /// <summary>
    /// The speed (kts) from which a <paramref name="type"/> brakes to its pivot speed in <paramref name="brakeFt"/> at
    /// <paramref name="rateKtsPerSec"/>.
    /// </summary>
    private static double SpeedBrakingToPivotInFt(string type, double brakeFt, double rateKtsPerSec)
    {
        double pivotKts = PivotSpeedKts(type, AircraftCategorization.Categorize(type));
        return Math.Sqrt((pivotKts * pivotKts) + (brakeFt * 2.0 * rateKtsPerSec * 3600.0 / GeoMath.FeetPerNm));
    }

    /// <summary>The B738's speed (kts) rolling into a junction it overruns at its taxi rate.</summary>
    private const double JetOverrunSpeedKts = 20.0;

    /// <summary>The B738's speed (kts) rolling into a junction it reaches its pivot speed short of.</summary>
    private const double JetReachSpeedKts = 15.0;

    /// <summary>The C172's speed (kts) rolling toward the runway holding position.</summary>
    private const double BarTaxiSpeedKts = 15.0;

    /// <summary>The B738's speed (kts) rolling toward the runway holding position.</summary>
    private const double JetBarSpeedKts = 20.0;

    /// <summary>
    /// How far (ft) short of the hold line the C172's taxi-rate braking would end: beyond its turn-about radius, so a turn
    /// about may be solved there, but inside the forward reach of its nose over the turn.
    /// </summary>
    private const double InsideTheReachFt = 8.0;

    /// <summary>How much room (ft) the B738 has to spare beyond its taxi-rate braking and its clearance from the bar.</summary>
    private const double RoomToSpareFt = 20.0;

    /// <summary>The B744's speed (kts) on the short edge: above its pivot speed, its taxi-rate stop well inside the edge.</summary>
    private const double ShortEdgeSpeedKts = 6.0;

    /// <summary>How long (s) a rolling aircraft is watched for.</summary>
    private const int RollWatchSeconds = 300;

    /// <summary>How long (s) a stopped aircraft must stand still for it to count as holding.</summary>
    private const int MinStillSeconds = 20;

    /// <summary>The ground speed (kts) under which an aircraft counts as standing still.</summary>
    private const double StillKts = 0.01;

    /// <summary>How far (deg) a stopping aircraft's heading may wander off the way it rolled.</summary>
    private const double StopHeadingToleranceDeg = 2.0;

    /// <summary>The words of the turn-about refusal (<see cref="GroundCommandHandler.NoRoomToTurnAroundReason"/>).</summary>
    private const string UnableToTurnAround = "no room to turn around";

    /// <summary>What a rolling aircraft did after its TAXI (<see cref="TrackRoll"/>).</summary>
    private sealed record RollingRun
    {
        /// <summary>The type's pivot speed (kts).</summary>
        public required double PivotKts { get; init; }

        /// <summary>The fastest (kts) it went once its heading turned off the rolling bearing, until it first faced back.</summary>
        public required double MaxTurnKts { get; init; }

        /// <summary>The most (kts) its speed fell in any one second.</summary>
        public required double MaxDecelKtsPerSec { get; init; }

        /// <summary>The furthest (ft) its fuselage nose, its cockpit-to-main-gear figure ahead, got past the line through the node ahead.</summary>
        public required double MaxNoseReachFt { get; init; }

        /// <summary>The furthest (ft) the point half its length ahead got past that line.</summary>
        public required double MaxHalfLengthReachFt { get; init; }

        /// <summary>The most (deg) its heading turned off the rolling bearing.</summary>
        public required double MaxHeadingOffDeg { get; init; }

        /// <summary>The furthest (ft) its progress along the rolling bearing ran back while it faced within 90° of it.</summary>
        public required double MaxSlideBackFt { get; init; }

        /// <summary>How long (s) it had stood still when the watch ended.</summary>
        public required int StillSeconds { get; init; }

        /// <summary>How many times it said it had no room to turn around.</summary>
        public required int UnableCalls { get; init; }

        /// <summary>Whether it finished the route's first segment.</summary>
        public required bool LegDone { get; init; }
    }

    /// <summary>
    /// A <paramref name="type"/> placed <paramref name="shortOfBarFt"/> short of the runway holding position at the end of the
    /// first long KOAK stub to a runway crossing (<see cref="FirstStubToARunwayCrossing"/>), facing it and rolling at
    /// <paramref name="speedKts"/>, cleared back to the taxiway behind it (by a scenario preset when
    /// <paramref name="isScenarioScripted"/>) and watched until it finishes the leg back or stands still; null when the
    /// layout is unavailable.
    /// </summary>
    private RollingRun? RollTowardARunwayBar(string type, double shortOfBarFt, double speedKts, bool isScenarioScripted)
    {
        if (BuildOak() is not { } ground)
        {
            return null;
        }

        (AircraftState aircraft, string command) = PlaceShortOfARunwayBar(ground, type, shortOfBarFt, speedKts);
        GroundNode holdShort = FirstStubToARunwayCrossing(ground.Layout)!.Value.HoldShort;
        return TrackRoll(ground, aircraft, (command, isScenarioScripted), holdShort, untilLegDone: true);
    }

    /// <summary>
    /// A <paramref name="type"/> placed <paramref name="shortOfBarFt"/> short of the runway holding position at the end of the
    /// first long KOAK stub to a runway crossing (<see cref="FirstStubToARunwayCrossing"/>), facing it and rolling at
    /// <paramref name="speedKts"/>, and the TAXI back to the taxiway behind it.
    /// </summary>
    private (AircraftState Aircraft, string Command) PlaceShortOfARunwayBar(SfoGround ground, string type, double shortOfBarFt, double speedKts)
    {
        (GroundNode farEnd, GroundNode holdShort, GroundEdge stub, _) =
            FirstStubToARunwayCrossing(ground.Layout)
            ?? throw new InvalidOperationException($"no straight KOAK taxi edge of {MinTurnAboutStubFt:F0} ft or more ends at a runway crossing");
        GroundNode beyond = farEnd.Edges.First(e => !ReferenceEquals(e, stub)).OtherNode(farEnd);
        double bearingDeg = GeoMath.BearingTo(farEnd.Position, holdShort.Position);
        var backDeg = new TrueHeading((bearingDeg + 180.0) % 360.0);
        LatLon start = GeoMath.ProjectPoint(holdShort.Position, backDeg, shortOfBarFt / GeoMath.FeetPerNm);
        output.WriteLine(
            $"{type} {shortOfBarFt:F1} ft short of runway holding position {holdShort.Id} on {stub.TaxiwayName} edge {farEnd.Id}-{holdShort.Id} "
                + $"({stub.DistanceNm * GeoMath.FeetPerNm:F0} ft) at {speedKts:F1} kt; bar clearance {BarClearanceFt(type):F1} ft"
        );
        return (PlaceRolling(ground, type, (start, bearingDeg), stub.TaxiwayName, speedKts), $"TAXI #{farEnd.Id} #{beyond.Id}");
    }

    /// <summary>
    /// Dispatches <paramref name="taxi"/>'s command to the aircraft (as a scenario preset when its flag is set), asserting it is accepted.
    /// </summary>
    private TaxiRoute DispatchTaxi(SfoGround ground, AircraftState aircraft, (string Command, bool IsScenarioScripted) taxi)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(taxi.Command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = CommandDispatcher.Dispatch(
            Assert.IsType<TaxiCommand>(parsed.Value),
            aircraft,
            ground.Engine.BuildDispatchContext(aircraft, taxi.IsScenarioScripted, facilityHint: null)
        );
        output.WriteLine($"{taxi.Command}: {result.Success} — {result.Message}");
        Assert.True(result.Success, $"'{taxi.Command}' was refused: {result.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        return route;
    }

    /// <summary>A <paramref name="type"/> at <paramref name="pose"/> on <paramref name="taxiway"/>, rolling at <paramref name="speedKts"/>.</summary>
    private static AircraftState PlaceRolling(
        SfoGround ground,
        string type,
        (LatLon Position, double HeadingDeg) pose,
        string taxiway,
        double speedKts
    )
    {
        AircraftState aircraft = MakeAircraft(ground.Layout, pose.Position, pose.HeadingDeg, "OAK", taxiway);
        aircraft.AircraftType = type;
        aircraft.IndicatedAirspeed = speedKts;
        ground.Engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    /// <summary>
    /// Clears the rolling aircraft with <paramref name="taxi"/>'s command (by a scenario preset when its flag is set) and
    /// watches it second by second, up to <see cref="RollWatchSeconds"/>, until it has stood still for
    /// <see cref="MinStillSeconds"/> or, when <paramref name="untilLegDone"/>, finished the route's first segment: how fast
    /// it turned and braked, how far its nose got past the line through <paramref name="ahead"/> square to the way it rolled,
    /// how far it turned and slid back, and how many times it said it had no room to turn around.
    /// </summary>
    private RollingRun TrackRoll(
        SfoGround ground,
        AircraftState aircraft,
        (string Command, bool IsScenarioScripted) taxi,
        GroundNode ahead,
        bool untilLegDone
    )
    {
        int unableCalls = 0;
        ground.Engine.WarningEmitted += (callsign, message) =>
            unableCalls += ((callsign == aircraft.Callsign) && message.Contains(UnableToTurnAround, StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
        TaxiRoute route = DispatchTaxi(ground, aircraft, taxi);

        double rollingBearingDeg = aircraft.TrueHeading.Degrees;
        var rolling = new TrueHeading(rollingBearingDeg);
        double noseFt = NoseAheadOfMainGearFt(aircraft.AircraftType);
        double halfLengthFt = HalfLengthFt(aircraft.AircraftType);
        double PastLineFt(double aheadOfCentreFt)
        {
            LatLon point = GeoMath.ProjectPoint(aircraft.Position, aircraft.TrueHeading, aheadOfCentreFt / GeoMath.FeetPerNm);
            return GeoMath.AlongTrackDistanceNm(point, ahead.Position, rolling) * GeoMath.FeetPerNm;
        }

        double previousKts = aircraft.IndicatedAirspeed;
        double furthestFt = PastLineFt(0.0);
        double noseReachFt = PastLineFt(noseFt);
        double halfReachFt = PastLineFt(halfLengthFt);
        double turnKts = 0.0;
        double decelKts = 0.0;
        double offDeg = 0.0;
        double slideBackFt = 0.0;
        bool turning = false;
        bool facedBack = false;
        int still = 0;
        int second = 0;
        while ((second++ < RollWatchSeconds) && (still < MinStillSeconds) && !(untilLegDone && (route.CurrentSegmentIndex > 0)))
        {
            ground.Engine.TickOneSecond();
            double kts = aircraft.GroundSpeed;
            decelKts = Math.Max(decelKts, previousKts - kts);
            previousKts = kts;
            double headingOffDeg = GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, rollingBearingDeg);
            offDeg = Math.Max(offDeg, headingOffDeg);
            turning |= headingOffDeg > TurnStartDeg;
            facedBack |= headingOffDeg >= (180.0 - MaxTurnFromStartDeg);
            turnKts = (turning && !facedBack) ? Math.Max(turnKts, kts) : turnKts;
            noseReachFt = Math.Max(noseReachFt, PastLineFt(noseFt));
            halfReachFt = Math.Max(halfReachFt, PastLineFt(halfLengthFt));
            if (headingOffDeg < 90.0)
            {
                double alongFt = PastLineFt(0.0);
                slideBackFt = Math.Max(slideBackFt, furthestFt - alongFt);
                furthestFt = Math.Max(furthestFt, alongFt);
            }

            still = (kts < StillKts) ? still + 1 : 0;
        }

        var run = new RollingRun
        {
            PivotKts = PivotSpeedKts(aircraft.AircraftType, AircraftCategorization.Categorize(aircraft.AircraftType)),
            MaxTurnKts = turnKts,
            MaxDecelKtsPerSec = decelKts,
            MaxNoseReachFt = noseReachFt,
            MaxHalfLengthReachFt = halfReachFt,
            MaxHeadingOffDeg = offDeg,
            MaxSlideBackFt = slideBackFt,
            StillSeconds = still,
            UnableCalls = unableCalls,
            LegDone = route.CurrentSegmentIndex > 0,
        };
        output.WriteLine($"{run} after {second - 1}s");
        return run;
    }

    /// <summary>
    /// The run turned about and finished the leg back without saying unable, never faster than its pivot speed (plus the
    /// physics overshoot) on the arc, its fuselage nose never past the hold line.
    /// </summary>
    private static void AssertTurnedAboutShortOfTheLine(RollingRun run)
    {
        Assert.True(run.LegDone, "the aircraft did not finish the leg back");
        Assert.Equal(0, run.UnableCalls);
        Assert.True(run.MaxNoseReachFt <= 0.0, $"the aircraft's nose reached {run.MaxNoseReachFt:F1} ft past the hold line");
        Assert.True(
            run.MaxTurnKts <= (run.PivotKts + TurnSpeedOvershootKts),
            $"the aircraft turned about at up to {run.MaxTurnKts:F2} kt, above its {run.PivotKts:F2} kt pivot speed "
                + $"plus {TurnSpeedOvershootKts:F1} kt"
        );
    }

    /// <summary>The run stopped straight ahead without turning about, stood still to the end, and said unable exactly once.</summary>
    private static void AssertStoppedHeldAndSaidUnable(RollingRun run)
    {
        Assert.False(run.LegDone, "the aircraft finished the leg back: it turned about");
        Assert.True(run.MaxHeadingOffDeg <= StopHeadingToleranceDeg, $"the aircraft turned {run.MaxHeadingOffDeg:F1}° off the way it rolled");
        Assert.True(run.StillSeconds >= MinStillSeconds, $"the aircraft stood still only {run.StillSeconds}s at the end of the watch");
        Assert.Equal(1, run.UnableCalls);
    }

    /// <summary>How far (ft) an aircraft of <paramref name="type"/> rolls braking from <paramref name="speedKts"/> to its pivot speed.</summary>
    private static double BrakeToPivotFt(string type, double speedKts, double rateKtsPerSec)
    {
        double pivotKts = PivotSpeedKts(type, AircraftCategorization.Categorize(type));
        return StoppingDistanceFt(speedKts, rateKtsPerSec) - StoppingDistanceFt(pivotKts, rateKtsPerSec);
    }

    /// <summary>
    /// How far (ft) short of a runway holding position a rolling turn about must begin for the whole aircraft but its wings to
    /// stay short of the hold line: 2.73 turn-about radii of main-gear reach over the jog and reversal, plus the fuselage
    /// nose's distance from the turn centre, √(R² + d²), d its cockpit-to-main-gear figure (<see cref="NoseAheadOfMainGearFt"/>).
    /// </summary>
    private static double BarClearanceFt(string type)
    {
        double radiusFt = TurnAboutFit.Evaluate(type, AircraftCategorization.Categorize(type)).RadiusFt;
        double noseFt = NoseAheadOfMainGearFt(type);
        return (GroundNavigator.TurnAboutReachRadii * radiusFt) + Math.Sqrt((radiusFt * radiusFt) + (noseFt * noseFt));
    }

    /// <summary>
    /// How far (ft) the fuselage nose is ahead of the main gear: the FAA record's cockpit-to-main-gear figure, else half the length.
    /// </summary>
    private static double NoseAheadOfMainGearFt(string type) => FaaAircraftDatabase.Get(type)?.CockpitToMainGearFt ?? HalfLengthFt(type);

    /// <summary>Half the length (ft) of <paramref name="type"/>.</summary>
    private static double HalfLengthFt(string type) => AircraftLength.ResolveFt(type) / 2.0;

    /// <summary>
    /// The first straight KOAK turn-about taxiway edge (by its lower node id) shorter than <paramref name="radiusFt"/>, with no
    /// runway holding position at either end, mid-way along which an aircraft stands mid-edge
    /// (<see cref="AirportGroundLayout.FindMidEdgeTaxiStart"/>), and an end beyond which no straight turn-about taxiway has
    /// room for a turn about of that radius (<see cref="HasStraightRoomBeyond"/>, three radii): that end, the other and the
    /// edge; null when there is none.
    /// </summary>
    private static (GroundNode Ahead, GroundNode Far, GroundEdge Edge)? FirstShortEdgeWithNoTurnAboutRoom(AirportGroundLayout layout, double radiusFt)
    {
        IEnumerable<GroundEdge> edges = layout
            .Edges.Where(e => GroundNavigator.IsTurnAboutTaxiway(e, layout) && ((e.DistanceNm * GeoMath.FeetPerNm) < radiusFt))
            .Where(e => e.Nodes.All(n => n.Type != GroundNodeType.RunwayHoldShort))
            .Where(e => layout.FindMidEdgeTaxiStart(Along(e.Nodes[0].Position, e.Nodes[1].Position, 0.5)) == e)
            .OrderBy(e => Math.Min(e.Nodes[0].Id, e.Nodes[1].Id));
        foreach (GroundEdge edge in edges)
        {
            foreach ((GroundNode ahead, GroundNode far) in new[] { (edge.Nodes[1], edge.Nodes[0]), (edge.Nodes[0], edge.Nodes[1]) })
            {
                if (!HasStraightRoomBeyond(layout, edge, ahead, 3.0 * radiusFt))
                {
                    return (ahead, far, edge);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The first straight KOAK turn-about taxiway edge (by edge order) of at least <see cref="MinSharpTurnEdgeFt"/>, with no
    /// runway holding position at either end, ending at a junction beyond which no straight turn-about taxiway continues it
    /// for more than <paramref name="roomFt"/> (<see cref="HasStraightRoomBeyond"/>): the junction, the node behind, the edge
    /// and a node one edge beyond the node behind; null when there is none.
    /// </summary>
    private static (GroundNode Junction, GroundNode Behind, GroundEdge Edge, GroundNode Beyond)? FirstStraightRollIntoAJunctionWithNoContinuation(
        AirportGroundLayout layout,
        double roomFt
    ) =>
        FirstStraightRollIntoAJunction(
            layout,
            MinSharpTurnEdgeFt,
            (edge, junction) => (junction.Edges.Count > 1) && !HasStraightRoomBeyond(layout, edge, junction, roomFt)
        );

    /// <summary>
    /// The first straight KOAK turn-about taxiway edge (by edge order) of at least <see cref="MinSharpTurnEdgeFt"/>, with no
    /// runway holding position at either end, ending at a junction with a straight turn-about taxiway beyond it
    /// (<see cref="StraightContinuation"/>) no longer than <paramref name="roomFt"/>: the junction, the node behind, the edge
    /// and a node one edge beyond the node behind; null when there is none.
    /// </summary>
    private static (GroundNode Junction, GroundNode Behind, GroundEdge Edge, GroundNode Beyond)? FirstStraightRollIntoAJunctionWithAShortContinuation(
        AirportGroundLayout layout,
        double roomFt
    ) =>
        FirstStraightRollIntoAJunction(
            layout,
            MinSharpTurnEdgeFt,
            (edge, junction) => HasStraightRoomBeyond(layout, edge, junction, 0.0) && !HasStraightRoomBeyond(layout, edge, junction, roomFt)
        );

    /// <summary>
    /// The first straight KOAK turn-about taxiway edge (by edge order) of at least <see cref="MinBarApproachEdgeFt"/>, with no
    /// runway holding position at either end, ending at a junction whose straight turn-about taxiway beyond it
    /// (<see cref="StraightContinuation"/>), no longer than <see cref="MaxBarContinuationFt"/>, ends at a runway holding
    /// position: the junction, the node behind, the edge and a node one edge beyond the node behind; null when there is none.
    /// </summary>
    private static (GroundNode Junction, GroundNode Behind, GroundEdge Edge, GroundNode Beyond)? FirstStraightRollIntoAContinuationToARunwayBar(
        AirportGroundLayout layout
    ) =>
        FirstStraightRollIntoAJunction(
            layout,
            MinBarApproachEdgeFt,
            (edge, junction) =>
                (StraightContinuation(layout, edge, junction) is { } next)
                && (next.OtherNode(junction).Type == GroundNodeType.RunwayHoldShort)
                && ((next.DistanceNm * GeoMath.FeetPerNm) <= MaxBarContinuationFt)
        );

    /// <summary>
    /// The first straight KOAK turn-about taxiway edge (by edge order) of at least <paramref name="minEdgeFt"/>, with no runway
    /// holding position at either end, toward an end node that <paramref name="junctionFits"/> (given the edge and that end):
    /// that end, the node behind, the edge and a node one edge beyond the node behind; null when there is none.
    /// </summary>
    private static (GroundNode Junction, GroundNode Behind, GroundEdge Edge, GroundNode Beyond)? FirstStraightRollIntoAJunction(
        AirportGroundLayout layout,
        double minEdgeFt,
        Func<GroundEdge, GroundNode, bool> junctionFits
    )
    {
        IEnumerable<GroundEdge> edges = layout
            .Edges.Where(e => GroundNavigator.IsTurnAboutTaxiway(e, layout) && ((e.DistanceNm * GeoMath.FeetPerNm) >= minEdgeFt))
            .Where(e => e.Nodes.All(n => n.Type != GroundNodeType.RunwayHoldShort));
        foreach (GroundEdge edge in edges)
        {
            foreach ((GroundNode junction, GroundNode behind) in new[] { (edge.Nodes[1], edge.Nodes[0]), (edge.Nodes[0], edge.Nodes[1]) })
            {
                if (junctionFits(edge, junction) && (behind.Edges.FirstOrDefault(e => !ReferenceEquals(e, edge)) is { } back))
                {
                    return (junction, behind, edge, back.OtherNode(behind));
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The straightest turn-about taxiway leaving <paramref name="node"/> other than <paramref name="edge"/>, when it continues
    /// <paramref name="edge"/> within <see cref="GroundNavigator.ContinuationMaxBendDeg"/> of straight on; null otherwise.
    /// </summary>
    private static GroundEdge? StraightContinuation(AirportGroundLayout layout, GroundEdge edge, GroundNode node)
    {
        double onDeg = GeoMath.BearingTo(edge.OtherNode(node).Position, node.Position);
        double BendDeg(GroundEdge e) => GeoMath.AbsBearingDifference(GeoMath.BearingTo(node.Position, e.OtherNode(node).Position), onDeg);
        GroundEdge? next = node
            .Edges.OfType<GroundEdge>()
            .Where(e => !ReferenceEquals(e, edge) && GroundNavigator.IsTurnAboutTaxiway(e, layout))
            .MinBy(BendDeg);
        return ((next is not null) && (BendDeg(next) <= GroundNavigator.ContinuationMaxBendDeg)) ? next : null;
    }

    /// <summary>
    /// Whether the straight turn-about taxiway beyond <paramref name="node"/> (<see cref="StraightContinuation"/>) is longer
    /// than <paramref name="minFt"/>.
    /// </summary>
    private static bool HasStraightRoomBeyond(AirportGroundLayout layout, GroundEdge edge, GroundNode node, double minFt) =>
        (StraightContinuation(layout, edge, node) is { } next) && ((next.DistanceNm * GeoMath.FeetPerNm) > minFt);

    /// <summary>The shortest (ft) taxiway edge a rolling aircraft approaches a continuation to a runway holding position on.</summary>
    private const double MinBarApproachEdgeFt = 120.0;

    /// <summary>
    /// The longest (ft) continuation to a runway holding position the C172 turns about on: longer, its taxi-rate braking must
    /// end so far past the junction that it rolls toward it well above taxi speed.
    /// </summary>
    private const double MaxBarContinuationFt = 150.0;

    /// <summary>
    /// The same pose in a B738: a jet has no room to turn about on a taxiway, so where only the far-node route avoids
    /// coming back over the edge it stands on, it refuses the TAXI and its state is untouched.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_JetRefusesToTurnAround()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "B738");

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, Assert.IsType<TaxiCommand>(parsed.Value), ground.Layout);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");

        Assert.False(result.Success, $"'{command}' was accepted: {result.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("C"), result.Message);
        Assert.EndsWith("no room to turn around on charlie, request a route ahead.", result.PilotUnable?.Tts, StringComparison.Ordinal);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
        Assert.IsType<HoldingInPositionPhase>(aircraft.Phases?.CurrentPhase);
    }

    /// <summary>
    /// The same B738 angled 45° across C: its gear does not fit a turn about on a TDG 3 taxiway at any angle (turning from
    /// off the edge saves little of a full 180's room), so it refuses the controller's TAXI as when lined up.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_JetAngledAcrossCRefusesToTurnAround()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "B738");
        TrueHeading angled = AngleAcrossC(aircraft);

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, Assert.IsType<TaxiCommand>(parsed.Value), ground.Layout);
        output.WriteLine($"{command} at heading {angled.Degrees:F0}: {result.Success} — {result.Message}");

        Assert.False(result.Success, $"'{command}' was accepted: {result.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("C"), result.Message);
        Assert.NotNull(result.PilotUnable);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// The angled B738 given the same TAXI by a scenario preset: no controller is there to re-issue it, so it is not
    /// refused, and since its gear does not fit a turn about it keeps the route from the C node ahead rather than taking
    /// the one from the far node. That route opens by driving straight back over the C edge it stands on, so it turns
    /// about where it stands and reports it in place, toward the far node, as the lined-up preset does.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_ScriptedJetAngledAcrossCKeepsTheRouteAheadAndTurnsAboutInPlace()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "B738");
        TrueHeading angled = AngleAcrossC(aircraft);

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        DispatchContext scripted = ground.Engine.BuildDispatchContext(aircraft, isScenarioScripted: true, facilityHint: null);
        CommandResult result = CommandDispatcher.Dispatch(Assert.IsType<TaxiCommand>(parsed.Value), aircraft, scripted);
        output.WriteLine($"scripted {command} at heading {angled.Degrees:F0}: {result.Success} — {result.Message}");

        Assert.True(result.Success, $"the scripted '{command}' was refused: {result.Message}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);
        Assert.Equal(tangentCut.Id, route.Segments[0].FromNodeId);
        Assert.False(VirtualNode.IsVirtualEdge(route.Segments[0].Edge.Edge), "segment 0 is the far-end route's free-space leg back");
        Assert.NotEqual(TaxiTurnAboutShape.FromFarEnd, route.TurnAboutShape);
        Assert.Equal(TaxiTurnAboutShape.InPlace, route.TurnAboutShape);
        Assert.Equal(farEnd.Id, route.TurnAboutTargetNodeId);
    }

    /// <summary>
    /// The pose of the lined-up B738 in other types, from the controller: a type whose gear fits a turn about on its own
    /// TDG taxiway (a C208, TDG 1A) turns about toward the far node; one whose gear does not (a C25A, TDG 2A, nose gear
    /// 19.6 ft out against 17.5 ft; an AT76, TDG 1B but a 35 ft wheelbase) refuses for want of room to turn around.
    /// </summary>
    [Theory]
    [InlineData("C208", true)]
    [InlineData("C25A", false)]
    [InlineData("AT76", false)]
    public void TaxiOnC_DestinationBehind_LinedUpTypeTurnsAboutOnlyWhenItsGearFits(string type, bool turnsAbout)
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, type);

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, Assert.IsType<TaxiCommand>(parsed.Value), ground.Layout);
        output.WriteLine($"{type} {command}: {result.Success} — {result.Message}");

        if (turnsAbout)
        {
            Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
            TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
            SfoGroundHarness.DumpRoute(output, route);
            Assert.Equal(farEnd.Id, route.Segments[0].ToNodeId);
            Assert.Equal(TaxiTurnAboutShape.FromFarEnd, route.TurnAboutShape);
        }
        else
        {
            Assert.False(result.Success, $"'{command}' was accepted: {result.Message}");
            Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("C"), result.Message);
            Assert.Null(aircraft.Ground.AssignedTaxiRoute);
        }
    }

    /// <summary>Turns <paramref name="aircraft"/>'s heading and track <see cref="AcrossEdgeDeg"/> across C, and returns the new heading.</summary>
    private static TrueHeading AngleAcrossC(AircraftState aircraft)
    {
        var angled = new TrueHeading(aircraft.TrueHeading.Degrees + AcrossEdgeDeg);
        aircraft.TrueHeading = angled;
        aircraft.TrueTrack = angled;
        return angled;
    }

    /// <summary>How far the angled jet's heading is turned off C: well off the taxiway's line, still short of across it.</summary>
    private const double AcrossEdgeDeg = 45.0;

    /// <summary>
    /// The C172 turned about on C by a controller's clearance reports the turn about as pending (the overlay draws it
    /// from that) until it has driven the leg back to the far node, and no longer once that leg is done.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_TurnAboutPendingUntilTheLegCompletes()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "C172");

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        Assert.Equal(farEnd.Id, route.Segments[0].ToNodeId);

        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, aircraft.Ground.TaxiTurnAboutShape);
        Assert.Equal(farEnd.Id, aircraft.Ground.TaxiTurnAboutTargetNodeId);

        int legDoneAt = SfoGroundHarness.TickUntil(ground.Engine, () => route.CurrentSegmentIndex > 0, TurnAboutLegTickSeconds, null);
        output.WriteLine($"left the turn-about leg after {legDoneAt}s");

        Assert.True(legDoneAt > 0, $"the aircraft did not finish the leg back to node {farEnd.Id} within {TurnAboutLegTickSeconds}s");
        Assert.Equal(TaxiTurnAboutShape.None, aircraft.Ground.TaxiTurnAboutShape);
        Assert.Null(aircraft.Ground.TaxiTurnAboutTargetNodeId);
    }

    /// <summary>
    /// The C172 turned about on C, snapshotted right after the clearance and restored into a fresh engine: the turn about is
    /// still pending there, and no longer once the restored aircraft has driven the leg back to the far node.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_TurnAboutPendingSurvivesASnapshotRestore()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "C172");

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, aircraft.Ground.TaxiTurnAboutShape);

        StateSnapshotDto snapshot = ground.Engine.CaptureSnapshot();
        SfoGround restored = BuildOak()!.Value;
        restored.Engine.RestoreFromSnapshot(snapshot);
        AircraftState restoredAircraft = Assert.IsType<AircraftState>(restored.Engine.FindAircraft(aircraft.Callsign));
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, restoredAircraft.Ground.TaxiTurnAboutShape);
        Assert.Equal(farEnd.Id, restoredAircraft.Ground.TaxiTurnAboutTargetNodeId);

        TaxiRoute route = restoredAircraft.Ground.AssignedTaxiRoute!;
        int legDoneAt = SfoGroundHarness.TickUntil(restored.Engine, () => route.CurrentSegmentIndex > 0, TurnAboutLegTickSeconds, null);
        output.WriteLine($"restored: left the turn-about leg after {legDoneAt}s");

        Assert.True(legDoneAt > 0, $"the restored aircraft did not finish the leg back to node {farEnd.Id} within {TurnAboutLegTickSeconds}s");
        Assert.Equal(TaxiTurnAboutShape.None, restoredAircraft.Ground.TaxiTurnAboutShape);
        Assert.Null(restoredAircraft.Ground.TaxiTurnAboutTargetNodeId);
    }

    /// <summary>A C172 mid-C facing H, cleared along C ahead of it: the clearance turns nothing about, so nothing is pending.</summary>
    [Fact]
    public void TaxiOnC_RouteAhead_NoTurnAboutPending()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "C172");

        const string Command = "TAXI C B RWY 28R HS H";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, Command);
        Assert.True(result.Success, $"'{Command}' was refused: {result.Message}");
        Assert.NotEqual(farEnd.Id, aircraft.Ground.AssignedTaxiRoute!.Segments[0].ToNodeId);

        Assert.Equal(TaxiTurnAboutShape.None, aircraft.Ground.TaxiTurnAboutShape);
        Assert.Null(aircraft.Ground.TaxiTurnAboutTargetNodeId);
    }

    /// <summary>How long the leg back to the far C node may take, turn included.</summary>
    private const int TurnAboutLegTickSeconds = 300;

    /// <summary>
    /// A B738 lined up along C toward H, but 20 ft off the centreline and 30 ft short of the C node ahead: the bearing to
    /// that node is 34° off its nose, yet the jet is lined up with the taxiway, so it still refuses to turn about.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_JetLinedUpOffCentrelineNearTheNodeIsRefused()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        double alongDeg = GeoMath.BearingTo(farEnd.Position, tangentCut.Position);
        LatLon onCentreline = GeoMath.ProjectPoint(tangentCut.Position, new TrueHeading(alongDeg + 180.0), ShortOfNodeFt / GeoMath.FeetPerNm);
        LatLon position = GeoMath.ProjectPoint(onCentreline, new TrueHeading(alongDeg + 90.0), OffCentrelineFt / GeoMath.FeetPerNm);
        AircraftState aircraft = MakeAircraft(ground.Layout, position, alongDeg, "OAK", "C");
        ground.Engine.World.AddAircraft(aircraft);
        double offNoseDeg = GeoMath.AbsBearingDifference(alongDeg, GeoMath.BearingTo(position, tangentCut.Position));

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, Assert.IsType<TaxiCommand>(parsed.Value), ground.Layout);
        output.WriteLine($"{command}, node {tangentCut.Id} {offNoseDeg:F0} deg off the nose: {result.Success} — {result.Message}");

        Assert.False(result.Success, $"'{command}' was accepted: {result.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("C"), result.Message);
    }

    /// <summary>How far short of the C node ahead the lined-up, off-centreline jet stands.</summary>
    private const double ShortOfNodeFt = 30.0;

    /// <summary>How far off C's centreline the lined-up jet stands.</summary>
    private const double OffCentrelineFt = 20.0;

    /// <summary>
    /// The same jet pose with the TAXI coming from a scenario preset: no controller is there to hear "unable" and re-issue,
    /// so the jet is not refused and takes the route from the C node ahead, as before the turn about existed.
    /// </summary>
    [Fact]
    public void TaxiOnC_DestinationBehind_ScriptedJetTaxiIsNotRefused()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "B738");

        string command = $"TAXI C {FirstTaxiwayOffCBeyond(farEnd, tangentCut)}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        DispatchContext scripted = ground.Engine.BuildDispatchContext(aircraft, isScenarioScripted: true, facilityHint: null);
        CommandResult result = CommandDispatcher.Dispatch(Assert.IsType<TaxiCommand>(parsed.Value), aircraft, scripted);
        output.WriteLine($"scripted {command}: {result.Success} — {result.Message}");

        Assert.True(result.Success, $"the scripted '{command}' was refused: {result.Message}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);
        Assert.Equal(tangentCut.Id, route.Segments[0].FromNodeId);
        Assert.Equal(TaxiTurnAboutShape.InPlace, route.TurnAboutShape);
        Assert.Equal(farEnd.Id, route.TurnAboutTargetNodeId);
    }

    /// <summary>
    /// A C172 mid-C facing H, cleared along a route drawn to the C node behind it: from that node the drawn route is already
    /// taxied and resolves nothing, so the route from the node ahead is kept, and its first segment drives straight back
    /// over the edge the aircraft stands on. The aircraft turns about where it stands, and the route reports it.
    /// </summary>
    [Fact]
    public void DrawnRouteToTheNodeBehind_KeepsTheRouteAheadAndReportsTheTurnAbout()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        IGroundEdge occupied = tangentCut.Edges.First(e => (e is GroundEdge) && (e.OtherNode(tangentCut) == farEnd));
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "C172");

        string command = $"TAXI #{farEnd.Id}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");

        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);
        TaxiRouteSegment first = route.Segments[0];
        Assert.True(ReferenceEquals(first.Edge.Edge, occupied), $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(farEnd.Id, first.ToNodeId);
        Assert.Equal(TaxiTurnAboutShape.InPlace, route.TurnAboutShape);
        Assert.Equal(farEnd.Id, route.TurnAboutTargetNodeId);
    }

    /// <summary>
    /// The C172 on the drawn route to the C node behind it, ticked: it turns about on the painted C edge it stands on, which
    /// its kept route drives backwards, and its centre stays within C's half-width of the centreline through the turn and
    /// the leg back to the far node.
    /// </summary>
    [Fact]
    public void DrawnRouteToTheNodeBehind_TurnsAboutWithinTheTaxiway()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        IGroundEdge occupied = tangentCut.Edges.First(e => (e is GroundEdge) && (e.OtherNode(tangentCut) == farEnd));
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "C172");

        string command = $"TAXI #{farEnd.Id}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);
        Assert.True(ReferenceEquals(route.Segments[0].Edge.Edge, occupied), "segment 0 is not the occupied C edge driven backwards");

        double peakOffsetFt = 0.0;
        double maxTurnSpeedKts = 0.0;
        bool FacesFarEnd() =>
            GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, GeoMath.BearingTo(aircraft.Position, farEnd.Position)) <= MaxTurnFromStartDeg;
        int legDoneAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (route.CurrentSegmentIndex > 0) || (aircraft.Ground.AssignedTaxiRoute is null),
            TurnAboutLegTickSeconds,
            _ =>
            {
                peakOffsetFt = Math.Max(peakOffsetFt, CentreOffsetFt(aircraft.Position, tangentCut, farEnd));
                maxTurnSpeedKts = FacesFarEnd() ? maxTurnSpeedKts : Math.Max(maxTurnSpeedKts, aircraft.GroundSpeed);
            }
        );
        output.WriteLine(
            $"{command}: leg back to node {farEnd.Id} done after {legDoneAt}s; "
                + $"peak {peakOffsetFt:F1} ft off C, max {maxTurnSpeedKts:F2} kt while turning"
        );

        Assert.True(legDoneAt > 0, $"the aircraft did not finish the leg back to node {farEnd.Id} within {TurnAboutLegTickSeconds}s");
        double pivotKts = PivotSpeedKts("C172", AircraftCategory.Piston);
        Assert.True(
            maxTurnSpeedKts <= pivotKts + TurnSpeedOvershootKts,
            $"the aircraft turned about at up to {maxTurnSpeedKts:F2} kt, above the {pivotKts:F2} kt pivot speed (ω·r at its "
                + $"{TurnAboutFit.Evaluate("C172", AircraftCategory.Piston).RadiusFt:F1} ft turn-about radius) plus {TurnSpeedOvershootKts:F1} kt"
        );
        Assert.True(
            peakOffsetFt <= (Tdg1AHalfWidthFt + CentreTrackingToleranceFt),
            $"the aircraft's centre swung {peakOffsetFt:F2} ft off C's centreline turning about, beyond {Tdg1AHalfWidthFt:F1} ft, "
                + $"{Tdg1AHalfWidthSource}, plus the {CentreTrackingToleranceFt:F1} ft tracking tolerance"
        );
    }

    /// <summary>
    /// The same drawn route from the controller to a B738 lined up along C: the only route is the one from the node ahead,
    /// which turns the jet about where it stands, so it refuses for want of room to turn around.
    /// </summary>
    [Fact]
    public void DrawnRouteToTheNodeBehind_LinedUpJetRefusesToTurnAround()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode tangentCut, GroundNode farEnd) = KoakTaxiwayC.LongEdgeWestOfH(ground.Layout);
        AircraftState aircraft = SpawnMidCFacingH(ground, tangentCut, farEnd, "B738");

        string command = $"TAXI #{farEnd.Id}";
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, Assert.IsType<TaxiCommand>(parsed.Value), ground.Layout);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");

        Assert.False(result.Success, $"'{command}' was accepted: {result.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("C"), result.Message);
        Assert.NotNull(result.PilotUnable);
        Assert.Null(aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// A C172 mid-way along a straight KOAK taxi edge whose end behind it is a runway holding position, facing the other
    /// end, cleared along a route drawn through that holding position: the route from the holding position wins, and no
    /// approach leg is driven up to a holding position, so that route opens with no leg back to it. The aircraft still turns
    /// about toward the holding position, and the route reports it from the far end, with that node as its target.
    /// </summary>
    [Fact]
    public void DrawnRouteBackThroughARunwayHoldShort_NoLegToIt_ReportsTheTurnAboutFromTheFarEnd()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (AircraftState aircraft, GroundNode holdShort) = TaxiBackThroughARunwayHoldShort(ground);

        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        Assert.Equal(holdShort.Id, route.Segments[0].FromNodeId);
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, route.TurnAboutShape);
        Assert.Equal(holdShort.Id, route.TurnAboutTargetNodeId);
    }

    /// <summary>
    /// The C172 cleared back through the runway holding position behind it, ticked: the route opens on the holding position
    /// with no leg back to it, so segment 0 runs on past it. The turn about is pending until the aircraft reaches the holding
    /// position, and no longer once it is past it, though still on segment 0.
    /// </summary>
    [Fact]
    public void NoLegFarEndRoute_PastTheTarget_TurnAboutNoLongerPending()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (AircraftState aircraft, GroundNode holdShort) = TaxiBackThroughARunwayHoldShort(ground);
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, aircraft.Ground.TaxiTurnAboutShape);

        List<(int Second, TaxiTurnAboutShape Shape)> shortOfTarget = [];
        int pastAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => PastTheTargetOnSegment0(aircraft, route, holdShort),
            PastTheTargetTickSeconds,
            second =>
            {
                if (AlongPastTargetFt(aircraft, route, holdShort) < 0.0)
                {
                    shortOfTarget.Add((second, aircraft.Ground.TaxiTurnAboutShape));
                }
            }
        );
        output.WriteLine($"{AlongPastTargetFt(aircraft, route, holdShort):F0} ft past node {holdShort.Id} on segment 0 after {pastAt}s");

        Assert.True(
            pastAt > 0,
            $"the aircraft was not {PastTargetFt:F0} ft past node {holdShort.Id} on segment 0 within {PastTheTargetTickSeconds}s"
        );
        Assert.NotEmpty(shortOfTarget);
        Assert.All(shortOfTarget, s => Assert.Equal(TaxiTurnAboutShape.FromFarEnd, s.Shape));
        Assert.Equal(TaxiTurnAboutShape.None, aircraft.Ground.TaxiTurnAboutShape);
        Assert.Null(aircraft.Ground.TaxiTurnAboutTargetNodeId);
    }

    /// <summary>
    /// The same C172 snapshotted and restored into a fresh engine: a snapshot taken right after the clearance restores the
    /// turn about from the far end as pending, and one taken once the aircraft is past the holding position restores it as
    /// no longer pending.
    /// </summary>
    [Fact]
    public void NoLegFarEndRoute_PastTheTarget_SnapshotRestore_StaysNotPending()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (AircraftState aircraft, GroundNode holdShort) = TaxiBackThroughARunwayHoldShort(ground);
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;

        AircraftState beforeTarget = RestoreIntoAFreshEngine(ground.Engine.CaptureSnapshot(), aircraft.Callsign);
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, beforeTarget.Ground.TaxiTurnAboutShape);
        Assert.Equal(holdShort.Id, beforeTarget.Ground.TaxiTurnAboutTargetNodeId);

        int pastAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => PastTheTargetOnSegment0(aircraft, route, holdShort),
            PastTheTargetTickSeconds,
            null
        );
        Assert.True(
            pastAt > 0,
            $"the aircraft was not {PastTargetFt:F0} ft past node {holdShort.Id} on segment 0 within {PastTheTargetTickSeconds}s"
        );

        AircraftState pastTarget = RestoreIntoAFreshEngine(ground.Engine.CaptureSnapshot(), aircraft.Callsign);
        Assert.Equal(0, pastTarget.Ground.AssignedTaxiRoute!.CurrentSegmentIndex);
        Assert.Equal(TaxiTurnAboutShape.None, pastTarget.Ground.TaxiTurnAboutShape);
        Assert.Null(pastTarget.Ground.TaxiTurnAboutTargetNodeId);
    }

    /// <summary>
    /// A C172 half-way along a long KOAK stub to a runway holding position, on the taxiway side and facing away from it,
    /// cleared along a route drawn back through the holding position across the runway: the route opens on that bar with no
    /// leg back to it. The aircraft turns about and holds at the bar, and the turn about is no longer pending while it holds
    /// there, nor once a <c>CROSS</c> has taken it across the runway past the holding position.
    /// </summary>
    [Fact]
    public void NoLegFarEndRoute_HeldAtTheTargetBar_TurnAboutNoLongerPending()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (AircraftState aircraft, GroundNode holdShort) = TaxiBackAcrossARunway(ground);
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        Assert.True(route.GetHoldShortAt(holdShort.Id) is { IsCleared: false }, $"no uncleared hold short at node {holdShort.Id}");

        int heldAt = TickUntilHeldAt(ground, aircraft, route, holdShort);
        Assert.True(heldAt > 0, $"the aircraft did not hold short at node {holdShort.Id} within {PastTheTargetTickSeconds}s");
        Assert.Equal(TaxiTurnAboutShape.None, aircraft.Ground.TaxiTurnAboutShape);
        Assert.Null(aircraft.Ground.TaxiTurnAboutTargetNodeId);

        SendAndLog(ground, aircraft, $"CROSS {BarRunway(holdShort)}");
        AssertNotPendingOncePast(ground, aircraft, route, holdShort);
    }

    /// <summary>
    /// The same C172 on the long stub, cleared across the runway (<c>CROSS</c>) before it moves: it turns about and crosses
    /// past the holding position without stopping there, and the turn about is no longer pending once it is past it.
    /// </summary>
    [Fact]
    public void NoLegFarEndRoute_BarClearedUpFront_PastTheTarget_TurnAboutNoLongerPending()
    {
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (AircraftState aircraft, GroundNode holdShort) = TaxiBackAcrossARunway(ground);
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SendAndLog(ground, aircraft, $"CROSS {BarRunway(holdShort)}");
        Assert.True(route.GetHoldShortAt(holdShort.Id) is { IsCleared: true }, $"no cleared hold short at node {holdShort.Id}");
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, aircraft.Ground.TaxiTurnAboutShape);

        int pastAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => AlongPastTargetFt(aircraft, route, holdShort) >= PastTargetFt,
            PastTheTargetTickSeconds,
            null
        );
        output.WriteLine(
            $"past node {holdShort.Id} after {pastAt}s, phase {aircraft.Phases?.CurrentPhase?.Name}, segment {route.CurrentSegmentIndex}"
        );

        Assert.True(pastAt > 0, $"the aircraft was not {PastTargetFt:F0} ft past node {holdShort.Id} within {PastTheTargetTickSeconds}s");
        Assert.Equal(TaxiTurnAboutShape.None, aircraft.Ground.TaxiTurnAboutShape);
        Assert.Null(aircraft.Ground.TaxiTurnAboutTargetNodeId);
    }

    /// <summary>
    /// Stubs from this long put the aircraft half-way along them beyond the start-node hold radius (150 ft), so it turns
    /// about before it can take the hold.
    /// </summary>
    private const double MinTurnAboutStubFt = 400.0;

    /// <summary>Ticks until the aircraft holds short and logs where it holds; the second it held, or -1.</summary>
    private int TickUntilHeldAt(SfoGround ground, AircraftState aircraft, TaxiRoute route, GroundNode holdShort)
    {
        int heldAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => aircraft.Phases?.CurrentPhase is HoldingShortPhase,
            PastTheTargetTickSeconds,
            null
        );
        output.WriteLine(
            $"held after {heldAt}s on segment {route.CurrentSegmentIndex}, "
                + $"{AlongPastTargetFt(aircraft, route, holdShort):F0} ft past node {holdShort.Id}, heading "
                + $"{GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, route.Segments[0].Edge.DepartureBearing):F0} deg off segment 0"
        );
        return heldAt;
    }

    /// <summary>
    /// A C172 half-way along the first KOAK stub to a runway holding position whose taxiway crosses that runway
    /// (<see cref="FirstStubToARunwayCrossing"/>), facing away from the holding position, cleared along a route drawn back
    /// through it to the far side's holding position: the aircraft and the near holding position. Asserts the route opens
    /// on that holding position with a turn about from the far end.
    /// </summary>
    private (AircraftState Aircraft, GroundNode HoldShort) TaxiBackAcrossARunway(SfoGround ground)
    {
        (GroundNode ahead, GroundNode holdShort, GroundEdge stub, GroundNode farSide) =
            FirstStubToARunwayCrossing(ground.Layout)
            ?? throw new InvalidOperationException(
                $"no straight KOAK taxi edge of {MinTurnAboutStubFt:F0} ft or more ends at a holding position its taxiway crosses"
            );
        AircraftState aircraft = TaxiBackThrough(ground, (ahead, holdShort, stub), farSide);
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        Assert.Equal(holdShort.Id, route.Segments[0].FromNodeId);
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, aircraft.Ground.TaxiTurnAboutShape);
        return (aircraft, holdShort);
    }

    /// <summary>
    /// The first straight KOAK taxi edge (by its lower node id) of at least <see cref="MinTurnAboutStubFt"/> from a taxi node
    /// to a runway holding position whose taxiway goes on across that runway to its far-side holding position
    /// (<see cref="FarSideBar"/>), half-way along which the aircraft stands mid-edge; null when there is none.
    /// </summary>
    private static (GroundNode Ahead, GroundNode HoldShort, GroundEdge Stub, GroundNode FarSide)? FirstStubToARunwayCrossing(
        AirportGroundLayout layout
    )
    {
        IEnumerable<GroundEdge> taxiEdges = layout
            .Edges.Where(e => !e.IsRamp && !e.IsRunwayCenterline && (e.TaxiwayName.Length > 0))
            .Where(e => (e.DistanceNm * GeoMath.FeetPerNm) >= MinTurnAboutStubFt)
            .OrderBy(e => Math.Min(e.Nodes[0].Id, e.Nodes[1].Id));
        foreach (GroundEdge edge in taxiEdges)
        {
            GroundNode[] holds = [.. edge.Nodes.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is not null))];
            if (holds.Length != 1)
            {
                continue;
            }

            GroundNode ahead = edge.OtherNode(holds[0]);
            bool midEdge = layout.FindMidEdgeTaxiStart(Along(holds[0].Position, ahead.Position, 0.5)) == edge;
            if (midEdge && (FarSideBar(holds[0], edge) is { } farSide))
            {
                return (ahead, holds[0], edge, farSide);
            }
        }

        return null;
    }

    /// <summary>How many nodes the walk across a runway visits looking for the far side's holding position.</summary>
    private const int MaxCrossingWalkNodes = 8;

    /// <summary>
    /// The holding position for the same runway reached by walking on from <paramref name="hold"/> away from
    /// <paramref name="stub"/>, taking the straightest edge at each node; null when the walk meets none within
    /// <see cref="MaxCrossingWalkNodes"/> nodes.
    /// </summary>
    private static GroundNode? FarSideBar(GroundNode hold, IGroundEdge stub)
    {
        GroundNode node = hold;
        IGroundEdge from = stub;
        double bearingDeg = GeoMath.BearingTo(stub.OtherNode(hold).Position, hold.Position);
        for (int i = 0; i < MaxCrossingWalkNodes; i++)
        {
            GroundNode at = node;
            double inDeg = bearingDeg;
            IGroundEdge? next = at
                .Edges.Where(e => e != from)
                .MinBy(e => GeoMath.AbsBearingDifference(e.Directed(at, e.OtherNode(at)).DepartureBearing, inDeg));
            if (next is null)
            {
                return null;
            }

            GroundNode to = next.OtherNode(at);
            if ((to.Type == GroundNodeType.RunwayHoldShort) && Equals(to.RunwayId, hold.RunwayId))
            {
                return to;
            }

            bearingDeg = next.Directed(at, to).ArrivalBearing;
            from = next;
            node = to;
        }

        return null;
    }

    /// <summary>The first designator of the runway <paramref name="holdShort"/> holds short of.</summary>
    private static string BarRunway(GroundNode holdShort) =>
        holdShort.RunwayId is { } runway ? runway.End1 : throw new InvalidOperationException($"node {holdShort.Id} holds short of no runway");

    /// <summary>Sends <paramref name="command"/> to the aircraft, logs the outcome and asserts it was accepted.</summary>
    private void SendAndLog(SfoGround ground, AircraftState aircraft, string command)
    {
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine($"{command}: {result.Success} — {result.Message}; phase {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
    }

    /// <summary>
    /// Ticks until the aircraft is <see cref="PastTargetFt"/> past <paramref name="target"/> along <paramref name="route"/>'s
    /// segment 0, asserting it gets there and that no turn about is pending on any second along the way.
    /// </summary>
    private void AssertNotPendingOncePast(SfoGround ground, AircraftState aircraft, TaxiRoute route, GroundNode target)
    {
        List<(int Second, string? Phase)> pending = [];
        int pastAt = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => AlongPastTargetFt(aircraft, route, target) >= PastTargetFt,
            PastTheTargetTickSeconds,
            second =>
            {
                if (aircraft.Ground.TaxiTurnAboutShape != TaxiTurnAboutShape.None)
                {
                    pending.Add((second, aircraft.Phases?.CurrentPhase?.Name));
                }
            }
        );
        output.WriteLine($"past node {target.Id} after {pastAt}s, phase {aircraft.Phases?.CurrentPhase?.Name}, segment {route.CurrentSegmentIndex}");

        Assert.True(pastAt > 0, $"the aircraft was not {PastTargetFt:F0} ft past node {target.Id} within {PastTheTargetTickSeconds}s");
        Assert.True(pending.Count == 0, $"the turn about was pending at {string.Join(", ", pending.Select(p => $"{p.Second}s ({p.Phase})"))}");
    }

    /// <summary>How far past the target, along segment 0, the aircraft must be to count as past it.</summary>
    private const double PastTargetFt = 20.0;

    /// <summary>How long the turn about and the roll past the holding position may take.</summary>
    private const int PastTheTargetTickSeconds = 180;

    /// <summary>
    /// A C172 half-way along the first KOAK stub to a runway holding position (<see cref="FirstStubToARunwayHoldShort"/>),
    /// facing away from it, cleared along a route drawn back through the holding position to the node beyond it: the
    /// aircraft and the holding position.
    /// </summary>
    private (AircraftState Aircraft, GroundNode HoldShort) TaxiBackThroughARunwayHoldShort(SfoGround ground)
    {
        (GroundNode ahead, GroundNode holdShort, GroundEdge stub) =
            FirstStubToARunwayHoldShort(ground.Layout)
            ?? throw new InvalidOperationException("no straight KOAK taxi edge ends at a runway holding position");
        GroundNode beyond = holdShort.Edges.First(e => !ReferenceEquals(e, stub)).OtherNode(holdShort);
        return (TaxiBackThrough(ground, (ahead, holdShort, stub), beyond), holdShort);
    }

    /// <summary>
    /// A C172 half-way along <paramref name="stub"/>'s edge, facing its node ahead, cleared along a route drawn back through
    /// its holding position to <paramref name="to"/>.
    /// </summary>
    private AircraftState TaxiBackThrough(SfoGround ground, (GroundNode Ahead, GroundNode HoldShort, GroundEdge Edge) stub, GroundNode to)
    {
        (GroundNode ahead, GroundNode holdShort, GroundEdge edge) = stub;
        LatLon position = Along(holdShort.Position, ahead.Position, 0.5);
        AircraftState aircraft = MakeAircraft(
            ground.Layout,
            position,
            GeoMath.BearingTo(holdShort.Position, ahead.Position),
            "OAK",
            edge.TaxiwayName
        );
        aircraft.AircraftType = "C172";
        ground.Engine.World.AddAircraft(aircraft);

        string command = $"TAXI #{holdShort.Id} #{to.Id}";
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine(
            $"{edge.TaxiwayName} edge {ahead.Id}-{holdShort.Id} ({edge.DistanceNm * GeoMath.FeetPerNm:F0} ft), holding position for "
                + $"{holdShort.RunwayId}; {command}: {result.Success} — {result.Message}"
        );
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
        SfoGroundHarness.DumpRoute(output, aircraft.Ground.AssignedTaxiRoute!);
        return aircraft;
    }

    /// <summary>
    /// How far (ft) the aircraft's centre is past <paramref name="target"/> along the departure of <paramref name="route"/>'s
    /// segment 0, measured along that bearing; negative when short of the line through the node square to it.
    /// </summary>
    private static double AlongPastTargetFt(AircraftState aircraft, TaxiRoute route, GroundNode target)
    {
        double bearingDeg = route.Segments[0].Edge.DepartureBearing;
        double distFt = GeoMath.DistanceNm(target.Position, aircraft.Position) * GeoMath.FeetPerNm;
        double offRad = GeoMath.SignedBearingDifference(bearingDeg, GeoMath.BearingTo(target.Position, aircraft.Position)) * Math.PI / 180.0;
        return distFt * Math.Cos(offRad);
    }

    /// <summary>
    /// Whether the aircraft is still on segment 0 of <paramref name="route"/> and <see cref="PastTargetFt"/> past
    /// <paramref name="target"/>.
    /// </summary>
    private static bool PastTheTargetOnSegment0(AircraftState aircraft, TaxiRoute route, GroundNode target) =>
        (route.CurrentSegmentIndex == 0) && (AlongPastTargetFt(aircraft, route, target) >= PastTargetFt);

    /// <summary>The aircraft <paramref name="callsign"/> from <paramref name="snapshot"/>, restored into a fresh KOAK engine.</summary>
    private AircraftState RestoreIntoAFreshEngine(StateSnapshotDto snapshot, string callsign)
    {
        SfoGround restored = BuildOak()!.Value;
        restored.Engine.RestoreFromSnapshot(snapshot);
        return Assert.IsType<AircraftState>(restored.Engine.FindAircraft(callsign));
    }

    /// <summary>
    /// The first straight KOAK taxi edge (by its lower node id) from a taxi node to a runway holding position with another
    /// edge beyond it, half-way along which the aircraft stands mid-edge (<see cref="AirportGroundLayout.FindMidEdgeTaxiStart"/>);
    /// null when there is none.
    /// </summary>
    private static (GroundNode Ahead, GroundNode HoldShort, GroundEdge Stub)? FirstStubToARunwayHoldShort(AirportGroundLayout layout)
    {
        IEnumerable<GroundEdge> taxiEdges = layout
            .Edges.Where(e => !e.IsRamp && !e.IsRunwayCenterline && (e.TaxiwayName.Length > 0))
            .OrderBy(e => Math.Min(e.Nodes[0].Id, e.Nodes[1].Id));
        foreach (GroundEdge edge in taxiEdges)
        {
            GroundNode[] holds = [.. edge.Nodes.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.Edges.Count > 1))];
            if (holds.Length != 1)
            {
                continue;
            }

            GroundNode ahead = edge.OtherNode(holds[0]);
            if (layout.FindMidEdgeTaxiStart(Along(holds[0].Position, ahead.Position, 0.5)) == edge)
            {
                return (ahead, holds[0], edge);
            }
        }

        return null;
    }

    /// <summary>
    /// A C172 on M2 cleared <c>TAXI @B26</c>: the route from the M2 node ahead resolves none, so the clearance is re-planned
    /// from the node behind, and the aircraft turns about on M2 toward it.
    /// </summary>
    [Fact]
    public void TaxiToGateOnM2_RefusedFromTheNodeAhead_ReplansFromTheNodeBehind()
    {
        if (SpawnOnM2("C172") is not { } spawned)
        {
            return;
        }

        CommandResult result = TaxiOnM2(spawned, $"TAXI @{M2Gate}", isScenarioScripted: false);

        Assert.True(result.Success, $"'TAXI @{M2Gate}' was refused: {result.Message}");
        Assert.Equal(spawned.Behind.Id, spawned.Aircraft.Ground.AssignedTaxiRoute!.Segments[0].ToNodeId);
    }

    /// <summary>
    /// A B738 lined up along M2, <c>TAXI @B26</c> from a scenario preset: the route from the M2 node ahead resolves none,
    /// so the scripted jet is not left without a route, and takes the one from the node behind.
    /// </summary>
    [Fact]
    public void TaxiToGateOnM2_RefusedFromTheNodeAhead_ScriptedJetTakesTheRouteFromTheNodeBehind()
    {
        if (SpawnOnM2("B738") is not { } spawned)
        {
            return;
        }

        CommandResult result = TaxiOnM2(spawned, $"TAXI @{M2Gate}", isScenarioScripted: true);

        Assert.True(result.Success, $"the scripted 'TAXI @{M2Gate}' was refused: {result.Message}");
        Assert.Equal(spawned.Behind.Id, spawned.Aircraft.Ground.AssignedTaxiRoute!.Segments[0].ToNodeId);
    }

    /// <summary>
    /// The scripted B738 lined up along M2 whose node ahead resolves no route to <c>@B26</c>: it takes the route from the
    /// node behind, so it reports the turn about from the far end with that node as the target, though it is lined up.
    /// </summary>
    [Fact]
    public void TaxiToGateOnM2_RefusedFromTheNodeAhead_ScriptedJetReportsTheTurnAboutFromTheFarEnd()
    {
        if (SpawnOnM2("B738") is not { } spawned)
        {
            return;
        }

        CommandResult result = TaxiOnM2(spawned, $"TAXI @{M2Gate}", isScenarioScripted: true);

        Assert.True(result.Success, $"the scripted 'TAXI @{M2Gate}' was refused: {result.Message}");
        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, spawned.Aircraft.Ground.TaxiTurnAboutShape);
        Assert.Equal(spawned.Behind.Id, spawned.Aircraft.Ground.TaxiTurnAboutTargetNodeId);
    }

    /// <summary>
    /// A B738 lined up along M2, <c>TAXI @B26</c> from the controller: the route from the M2 node ahead resolves none and
    /// the one from the node behind needs a turn about on M2, so the jet refuses for want of room to turn around.
    /// </summary>
    [Fact]
    public void TaxiToGateOnM2_RefusedFromTheNodeAhead_LinedUpJetRefusesToTurnAround()
    {
        if (SpawnOnM2("B738") is not { } spawned)
        {
            return;
        }

        CommandResult result = TaxiOnM2(spawned, $"TAXI @{M2Gate}", isScenarioScripted: false);

        Assert.False(result.Success, $"'TAXI @{M2Gate}' was accepted: {result.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("M2"), result.Message);
        Assert.NotNull(result.PilotUnable);
        Assert.Null(spawned.Aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// A B738 lined up along M2, cleared to a gate whose ramp-confined route resolves from neither end of M2: the jet
    /// gives the first attempt's own refusal, not the turn-about one.
    /// </summary>
    [Fact]
    public void TaxiToGateOnM2_RefusedFromBothEnds_LinedUpJetGivesTheFirstRefusal()
    {
        if (SpawnOnM2("B738") is not { } spawned)
        {
            return;
        }

        CommandResult result = TaxiOnM2(spawned, $"TAXI @{UnreachableGate}", isScenarioScripted: false);

        Assert.False(result.Success, $"'TAXI @{UnreachableGate}' was accepted: {result.Message}");
        Assert.NotEqual(GroundCommandHandler.NoRoomToTurnAroundReason("M2"), result.Message);
        Assert.StartsWith($"Unable, need a route to gate {UnreachableGate}", result.Message, StringComparison.Ordinal);
        Assert.Null(spawned.Aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// A B738 held after a push on M2, lined up toward <see cref="M2NodeAhead"/>, given the scenario preset
    /// <c>PUSH; TAXI @B26</c>: the push tows it straight back along M2 and it ends still lined up toward the node ahead, so
    /// the TAXI from the controller would be refused for want of room to turn around. A preset is not refused: it takes
    /// the route from the node behind. Snapshotted mid-push and restored, the queued TAXI still fires as the preset it is
    /// and takes the same route as the live run.
    /// </summary>
    [Fact]
    public void ScriptedPushThenTaxi_RestoredMidPush_TaxisAsLive()
    {
        if (SpawnHeldAfterPushOnM2() is not { } controller)
        {
            return;
        }

        // The pose: after the push, the controller's TAXI is refused.
        PushAndHold(controller, isScenarioScripted: false);
        ParseResult<ParsedCommand> parsedTaxi = CommandParser.Parse($"TAXI @{M2Gate}");
        Assert.True(parsedTaxi.IsSuccess, parsedTaxi.Reason);
        CommandResult refused = GroundCommandHandler.TryTaxi(
            controller.Aircraft,
            Assert.IsType<TaxiCommand>(parsedTaxi.Value),
            controller.Ground.Layout
        );
        output.WriteLine($"controller TAXI @{M2Gate} after the push: {refused.Success} — {refused.Message}");
        Assert.Equal(GroundCommandHandler.NoRoomToTurnAroundReason("M2"), refused.Message);

        TaxiRoute live = PushThenTaxiFires(SpawnHeldAfterPushOnM2()!.Value, snapshotMidPush: false);
        TaxiRoute restored = PushThenTaxiFires(SpawnHeldAfterPushOnM2()!.Value, snapshotMidPush: true);

        Assert.Equal(M2NodeBehind, live.Segments[0].ToNodeId);
        Assert.Equal(live.Segments.Select(s => s.ToNodeId), restored.Segments.Select(s => s.ToNodeId));
        Assert.Equal(live.TurnAboutShape, restored.TurnAboutShape);
        Assert.Equal(live.TurnAboutTargetNodeId, restored.TurnAboutTargetNodeId);
    }

    /// <summary>How far along M2 from <see cref="M2NodeBehind"/> to <see cref="M2NodeAhead"/> the aircraft held after a push stands.</summary>
    private const double HeldAfterPushFraction = 0.85;

    /// <summary>How long a push, or a push and the TAXI queued behind it, may take.</summary>
    private const int PushTickSeconds = 240;

    /// <summary>
    /// A stopped B738 in <see cref="HoldingAfterPushbackPhase"/> <see cref="HeldAfterPushFraction"/> of the way along SFO's
    /// M2 edge toward <see cref="M2NodeAhead"/> and lined up toward it, or null when the layout is unavailable.
    /// </summary>
    private (SfoGround Ground, AircraftState Aircraft)? SpawnHeldAfterPushOnM2()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return null;
        }

        GroundNode ahead = ground.Layout.Nodes[M2NodeAhead];
        GroundNode behind = ground.Layout.Nodes[M2NodeBehind];
        LatLon position = Along(behind.Position, ahead.Position, HeldAfterPushFraction);
        AircraftState aircraft = MakeAircraft(ground.Layout, position, GeoMath.BearingTo(behind.Position, ahead.Position), "SFO", "M2");
        aircraft.AircraftType = "B738";
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HoldingAfterPushbackPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ground.Layout));
        ground.Engine.World.AddAircraft(aircraft);
        return (ground, aircraft);
    }

    /// <summary>Dispatches a bare <c>PUSH</c> and ticks until the aircraft is held after it.</summary>
    private void PushAndHold((SfoGround Ground, AircraftState Aircraft) spawned, bool isScenarioScripted)
    {
        AircraftState aircraft = spawned.Aircraft;
        CommandResult pushed = CommandDispatcher.DispatchCompound(
            ParseCompound("PUSH"),
            aircraft,
            spawned.Ground.Engine.BuildDispatchContext(aircraft, isScenarioScripted, facilityHint: null)
        );
        Assert.True(pushed.Success, pushed.Message);
        int heldAt = SfoGroundHarness.TickUntil(
            spawned.Ground.Engine,
            () => aircraft.Phases?.CurrentPhase is HoldingAfterPushbackPhase,
            PushTickSeconds,
            null
        );
        output.WriteLine(
            $"push held after {heldAt}s at ({aircraft.Position.Lat:F6}, {aircraft.Position.Lon:F6}) hdg {aircraft.TrueHeading.Degrees:F0}, "
                + $"mid-edge {spawned.Ground.Layout.FindMidEdgeTaxiStart(aircraft.Position)?.TaxiwayName ?? "none"}"
        );
        Assert.True(heldAt > 0, $"the push did not end within {PushTickSeconds}s");
    }

    /// <summary>
    /// Dispatches the preset <c>PUSH; TAXI @B26</c> and ticks until the queued TAXI has fired, after restoring a snapshot
    /// taken mid-push into a fresh engine when <paramref name="snapshotMidPush"/>; the route it took.
    /// </summary>
    private TaxiRoute PushThenTaxiFires((SfoGround Ground, AircraftState Aircraft) spawned, bool snapshotMidPush)
    {
        (SfoGround ground, AircraftState aircraft) = spawned;
        CommandResult dispatched = CommandDispatcher.DispatchCompound(
            ParseCompound($"PUSH; TAXI @{M2Gate}"),
            aircraft,
            ground.Engine.BuildDispatchContext(aircraft, isScenarioScripted: true, facilityHint: null)
        );
        Assert.True(dispatched.Success, dispatched.Message);

        if (snapshotMidPush)
        {
            int movingAt = SfoGroundHarness.TickUntil(
                ground.Engine,
                () => (aircraft.Phases?.CurrentPhase is PushbackPhase) && (aircraft.GroundSpeed > 1.0),
                PushTickSeconds,
                null
            );
            Assert.True(movingAt > 0, "the push never got moving");
            StateSnapshotDto snapshot = ground.Engine.CaptureSnapshot();
            ground = SfoGroundHarness.Build(output, autoCross: false)!.Value;
            ground.Engine.RestoreFromSnapshot(snapshot);
            aircraft = Assert.IsType<AircraftState>(ground.Engine.FindAircraft(aircraft.Callsign));
            Assert.IsType<PushbackPhase>(aircraft.Phases?.CurrentPhase);
        }

        AircraftState flown = aircraft;
        int firedAt = SfoGroundHarness.TickUntil(ground.Engine, () => flown.Ground.AssignedTaxiRoute is not null, PushTickSeconds, null);
        output.WriteLine($"{(snapshotMidPush ? "restored" : "live")}: TAXI fired after {firedAt}s, phase {flown.Phases?.CurrentPhase?.Name}");
        Assert.True(firedAt > 0, $"the queued TAXI took no route within {PushTickSeconds}s (phase {flown.Phases?.CurrentPhase?.Name})");
        SfoGroundHarness.DumpRoute(output, flown.Ground.AssignedTaxiRoute!);
        return flown.Ground.AssignedTaxiRoute!;
    }

    private static CompoundCommand ParseCompound(string command)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        return parsed.Value!;
    }

    /// <summary>The M2 node ahead of the aircraft on M2: no route inside the ramp to <see cref="M2Gate"/> resolves from it.</summary>
    private const int M2NodeAhead = 14;

    /// <summary>The M2 node behind the aircraft on M2: the route inside the ramp to <see cref="M2Gate"/> resolves from it.</summary>
    private const int M2NodeBehind = 15;

    /// <summary>The gate cleared from M2 with no taxiway named, so the route stays inside the ramp.</summary>
    private const string M2Gate = "B26";

    /// <summary>A gate in another terminal's ramp: no route inside the ramp reaches it from either end of M2.</summary>
    private const string UnreachableGate = "G10";

    /// <summary>
    /// A stopped <paramref name="type"/> 60% of the way along SFO's M2 edge from <see cref="M2NodeBehind"/> to
    /// <see cref="M2NodeAhead"/>, facing the node ahead and lined up along M2, or null when the layout is unavailable.
    /// </summary>
    private (SfoGround Ground, AircraftState Aircraft, GroundNode Behind)? SpawnOnM2(string type)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return null;
        }

        GroundNode ahead = ground.Layout.Nodes[M2NodeAhead];
        GroundNode behind = ground.Layout.Nodes[M2NodeBehind];
        LatLon position = Along(behind.Position, ahead.Position, 0.6);
        GroundEdge? occupied = ground.Layout.FindMidEdgeTaxiStart(position);
        Assert.True(
            (occupied is not null) && occupied.MatchesTaxiway("M2") && occupied.Nodes.Contains(ahead) && occupied.Nodes.Contains(behind),
            $"the aircraft is not mid-way along the M2 edge {behind.Id}-{ahead.Id}"
        );

        AircraftState aircraft = MakeAircraft(ground.Layout, position, GeoMath.BearingTo(behind.Position, ahead.Position), "SFO", "M2");
        aircraft.AircraftType = type;
        ground.Engine.World.AddAircraft(aircraft);
        return (ground, aircraft, behind);
    }

    /// <summary><paramref name="command"/> for the aircraft on M2, from the controller or from a scenario preset.</summary>
    private CommandResult TaxiOnM2((SfoGround Ground, AircraftState Aircraft, GroundNode Behind) spawned, string command, bool isScenarioScripted)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(parsed.Value);
        CommandResult result = isScenarioScripted
            ? CommandDispatcher.Dispatch(
                taxi,
                spawned.Aircraft,
                spawned.Ground.Engine.BuildDispatchContext(spawned.Aircraft, isScenarioScripted: true, facilityHint: null)
            )
            : GroundCommandHandler.TryTaxi(spawned.Aircraft, taxi, spawned.Ground.Layout);
        output.WriteLine(
            $"{(isScenarioScripted ? "scripted " : "")}{command} ({spawned.Aircraft.AircraftType}): {result.Success} — {result.Message}"
        );
        if (spawned.Aircraft.Ground.AssignedTaxiRoute is { } route)
        {
            SfoGroundHarness.DumpRoute(output, route);
        }

        return result;
    }

    /// <summary>How long the turn about at the far C node may take.</summary>
    private const int TurnAboutTickSeconds = 90;

    /// <summary>How far above the slow-turn speed the ground speed may read during the turn about: physics overshoot within one sub-tick.</summary>
    private const double TurnSpeedOvershootKts = 0.2;

    /// <summary>Builds an engine over the committed KOAK layout, or null when the layout or navdata is unavailable.</summary>
    private SfoGround? BuildOak()
    {
        if (LoadLayout("OAK") is not { } layout)
        {
            return null;
        }

        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-c-turn-about",
                ScenarioName = "OAK C Turn About",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunway = false,
            },
        };
        return new SfoGround(engine, layout);
    }

    /// <summary>A stopped <paramref name="type"/> 35% of the way along C edge tangent cut → far end, facing the tangent cut (toward H).</summary>
    private static AircraftState SpawnMidCFacingH(SfoGround ground, GroundNode tangentCut, GroundNode farEnd, string type)
    {
        LatLon position = Along(tangentCut.Position, farEnd.Position, 0.35);
        AircraftState aircraft = MakeAircraft(ground.Layout, position, GeoMath.BearingTo(farEnd.Position, tangentCut.Position), "OAK", "C");
        aircraft.AircraftType = type;
        ground.Engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    /// <summary>
    /// An aircraft mid-C at KOAK, a quarter of the edge short of one end and facing it, whose route leaves that node by
    /// another edge turning more than 90°: it has passed neither end of the edge it is on, so it drives the free-space leg
    /// to the node rather than being read as past it.
    /// </summary>
    [Fact]
    public void ApproachLeg_MidEdge_RouteTurnsSharplyAtTheNodeAhead_DrivesToTheNode()
    {
        AirportGroundLayout? layout = LoadLayout("OAK");
        if (layout is null)
        {
            return;
        }

        (GroundNode node, GroundNode behind, IGroundEdge turn) =
            FirstSharpTurnOffAStraightEdge(layout)
            ?? throw new InvalidOperationException("no straight KOAK taxi edge ends in a turn of more than 90°");
        LatLon position = Along(behind.Position, node.Position, 0.75);
        output.WriteLine($"on edge {behind.Id}-{node.Id}, route leaves node {node.Id} by {turn.TaxiwayName} to {turn.OtherNode(node).Id}");
        var heading = new TrueHeading(GeoMath.BearingTo(behind.Position, node.Position));
        var graphRoute = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = turn.TaxiwayName, Edge = turn.Directed(node, turn.OtherNode(node)) }],
            HoldShortPoints = [],
        };

        TaxiRoute route = TaxiApproachLeg.Prepend(layout, position, heading, graphRoute);
        SfoGroundHarness.DumpRoute(output, route);

        TaxiRouteSegment first = route.Segments[0];
        Assert.True(VirtualNode.IsVirtualEdge(first.Edge.Edge), $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(node.Id, first.ToNodeId);
    }

    /// <summary>Straight taxi edges from this long: three quarters along one, the aircraft is clear of both end nodes.</summary>
    private const double MinSharpTurnEdgeFt = 300.0;

    /// <summary>
    /// The first (by edge order) straight taxi edge of at least <see cref="MinSharpTurnEdgeFt"/>, three quarters along which
    /// the aircraft is on it (<see cref="AirportGroundLayout.FindOccupiedTaxiEdge"/>), with an end that is no holding
    /// position and has an edge leaving it more than 90° off the direction of arrival (<see cref="SharpTurnAt"/>), or null.
    /// </summary>
    private static (GroundNode Node, GroundNode Behind, IGroundEdge Turn)? FirstSharpTurnOffAStraightEdge(AirportGroundLayout layout)
    {
        foreach (GroundEdge edge in layout.Edges.Where(e => !e.IsRamp && !e.IsRunwayCenterline && (e.TaxiwayName.Length > 0)))
        {
            if ((edge.DistanceNm * GeoMath.FeetPerNm) < MinSharpTurnEdgeFt)
            {
                continue;
            }

            foreach ((GroundNode node, GroundNode behind) in new[] { (edge.Nodes[1], edge.Nodes[0]), (edge.Nodes[0], edge.Nodes[1]) })
            {
                bool onEdge = layout.FindOccupiedTaxiEdge(Along(behind.Position, node.Position, 0.75)) == edge;
                if (onEdge && (node.Type != GroundNodeType.RunwayHoldShort) && (SharpTurnAt(node, behind, edge) is { } found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// An edge other than <paramref name="occupied"/> leaving <paramref name="node"/> more than 90° off the direction of
    /// arrival from <paramref name="behind"/> (no ramp connector, no runway centreline), or null.
    /// </summary>
    private static (GroundNode Node, GroundNode Behind, IGroundEdge Turn)? SharpTurnAt(GroundNode node, GroundNode behind, IGroundEdge occupied)
    {
        double arrivalDeg = GeoMath.BearingTo(behind.Position, node.Position);
        IGroundEdge? turn = node.Edges.FirstOrDefault(e =>
            (e != occupied)
            && !e.IsRamp
            && !e.IsRunwayCenterline
            && (GeoMath.AbsBearingDifference(e.Directed(node, e.OtherNode(node)).DepartureBearing, arrivalDeg) > 90.0)
        );
        return turn is null ? null : (node, behind, turn);
    }

    [Fact]
    public void TaxiOnB_MidEdge_Sfo_RouteStartsOnB()
    {
        AirportGroundLayout? layout = LoadLayout("SFO");
        if (layout is null)
        {
            return;
        }

        GroundEdge longestB = LongestStraightB(layout);
        GroundNode from = longestB.Nodes[0];
        GroundNode to = longestB.Nodes[1];
        output.WriteLine($"longest B edge {from.Id}-{to.Id}, {longestB.DistanceNm * GeoMath.FeetPerNm:F0} ft");

        LatLon position = Along(from.Position, to.Position, 0.5);
        AircraftState aircraft = MakeAircraft(layout, position, GeoMath.BearingTo(from.Position, to.Position), "SFO", "B");

        TaxiRoute route = Resolve(layout, aircraft, "TAXI B");

        GroundNode firstFrom = FirstLayoutSegment(route).Edge.FromNode;
        Assert.True(OnStraightEdgeOf(firstFrom, "B"), $"route joins the graph at node {firstFrom.Id}, not on B");
    }

    /// <summary>
    /// A bare <c>TAXI B</c> mid-B at SFO, more than the free-space bound from the B node ahead: the route opens with the
    /// approach leg along B, starting where the aircraft stands.
    /// </summary>
    [Fact]
    public void TaxiOnB_MidEdge_Sfo_ApproachLegStartsAtTheAircraft()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnMidLongestB(ground, out _);

        CommandResult taxi = ground.Engine.SendCommand(aircraft.Callsign, "TAXI B");
        Assert.True(taxi.Success, $"'TAXI B' was refused: {taxi.Message}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        SfoGroundHarness.DumpRoute(output, route);

        TaxiRouteSegment first = route.Segments[0];
        Assert.True(
            VirtualNode.IsVirtualEdge(first.Edge.Edge),
            $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}, not the approach leg"
        );
        double startOffFt = GeoMath.DistanceNm(first.Edge.FromNode.Position, aircraft.Position) * GeoMath.FeetPerNm;
        Assert.True(startOffFt <= 1.0, $"the approach leg starts {startOffFt:F1} ft from the aircraft");
    }

    /// <summary>
    /// A bare <c>TAXI B</c> mid-B at SFO, ticked: the aircraft rolls on along B at taxi speed rather than crawling at the
    /// navigator's reacquire speed toward a route start hundreds of feet ahead, and it does not turn around.
    /// </summary>
    [Fact]
    public void TaxiOnB_MidEdge_Sfo_KeepsTaxiSpeed()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SpawnMidLongestB(ground, out double startHeadingDeg);

        CommandResult taxi = ground.Engine.SendCommand(aircraft.Callsign, "TAXI B");
        Assert.True(taxi.Success, $"'TAXI B' was refused: {taxi.Message}");
        SfoGroundHarness.DumpRoute(output, aircraft.Ground.AssignedTaxiRoute!);

        SfoGroundHarness.TickUntil(ground.Engine, () => false, TaxiTickSeconds, null);

        double offBFt = SfoGroundHarness.DistanceToTaxiwayFt(ground.Layout, "B", aircraft.Position);
        double turnedDeg = GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, startHeadingDeg);
        output.WriteLine($"after {TaxiTickSeconds}s: {offBFt:F0} ft off B, turned {turnedDeg:F0} deg, GS {aircraft.GroundSpeed:F1} kt");
        Assert.True(offBFt <= AirportGroundLayout.OnTaxiEdgeMaxOffsetFt, $"the aircraft is {offBFt:F0} ft off B");
        Assert.True(turnedDeg <= MaxTurnFromStartDeg, $"the aircraft turned {turnedDeg:F0} deg from its start heading");
        Assert.True(aircraft.GroundSpeed > MinTaxiSpeedKts, $"ground speed is {aircraft.GroundSpeed:F1} kt after {TaxiTickSeconds}s");
    }

    /// <summary>
    /// An aircraft 20 ft into a fillet arc past a straight edge's end is not on that edge: the edge's nearest point is its
    /// end node, behind the aircraft. The taxi start must not be that node, nor any other node behind the aircraft: it is
    /// the fillet's own end ahead.
    /// </summary>
    [Fact]
    public void AircraftInAFilletPastAnEdgeEnd_DoesNotStartBehind()
    {
        AirportGroundLayout? layout = LoadLayout("SFO");
        if (layout is null)
        {
            return;
        }

        if (PoseIntoAFillet(layout) is not { } pose)
        {
            Assert.Fail("no fillet arc at SFO runs past the end of the straight taxi edge nearest it");
            return;
        }

        (GroundNode arcEntry, GroundNode arcExit, LatLon position, TrueHeading heading) = pose;
        output.WriteLine($"{IntoFilletFt:F0} ft into the fillet {arcEntry.Id}-{arcExit.Id}, heading {heading.Degrees:F0}");

        // The TAXI command's own start resolution: the heading-biased pick, else the nearest node.
        GroundNode? headingBiased = layout.FindNearestNodeForTaxi(position, heading);
        GroundNode? start = headingBiased ?? layout.FindNearestNode(position);
        output.WriteLine($"heading-biased start: {(headingBiased is null ? "none" : $"node {headingBiased.Id}")}");
        Assert.NotNull(start);
        double startFt = GeoMath.DistanceNm(position, start.Position) * GeoMath.FeetPerNm;
        double offHeadingDeg = GeoMath.AbsBearingDifference(GeoMath.BearingTo(position, start.Position), heading.Degrees);
        output.WriteLine($"taxi start node {start.Id}: {startFt:F0} ft away, {offHeadingDeg:F0} deg off the heading");
        Assert.True(
            (startFt <= AirportGroundLayout.AtNodeToleranceFt) || (offHeadingDeg < 90.0),
            $"the taxi starts at node {start.Id}, {startFt:F0} ft away and {offHeadingDeg:F0} deg off the heading: behind the aircraft"
        );
        Assert.True(start == arcExit, $"the taxi starts at node {start.Id}, not at the fillet's end ahead, node {arcExit.Id}");
    }

    /// <summary>
    /// Spawns a B738 stopped half-way along SFO's longest straight B edge, on its centreline, facing along it. Half that
    /// edge is longer than the free-space bound, so the B node ahead is out of a free-space leg's reach.
    /// </summary>
    private AircraftState SpawnMidLongestB(SfoGround ground, out double startHeadingDeg)
    {
        GroundEdge longestB = LongestStraightB(ground.Layout);
        GroundNode from = longestB.Nodes[0];
        GroundNode to = longestB.Nodes[1];
        double halfFt = longestB.DistanceNm * GeoMath.FeetPerNm / 2.0;
        output.WriteLine($"longest B edge {from.Id}-{to.Id}, {longestB.DistanceNm * GeoMath.FeetPerNm:F0} ft");
        Assert.True(
            halfFt > RampLaneReposition.MaxCrossingFt,
            $"half the longest B edge is {halfFt:F0} ft, inside the {RampLaneReposition.MaxCrossingFt:F0} ft free-space bound"
        );

        startHeadingDeg = GeoMath.BearingTo(from.Position, to.Position);
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            "DAL880",
            "B738",
            (from, new TrueHeading(startHeadingDeg)),
            new HoldingInPositionPhase()
        );
        aircraft.Position = Along(from.Position, to.Position, 0.5);
        aircraft.TrueTrack = new TrueHeading(startHeadingDeg);
        aircraft.Ground.CurrentTaxiway = "B";
        return aircraft;
    }

    /// <summary>SFO's longest straight B edge (no ramp connector, no runway centreline).</summary>
    private static GroundEdge LongestStraightB(AirportGroundLayout layout)
    {
        GroundEdge? longest = layout.Edges.Where(e => e.MatchesTaxiway("B") && !e.IsRamp && !e.IsRunwayCenterline).MaxBy(e => e.DistanceNm);
        Assert.NotNull(longest);
        return longest;
    }

    /// <summary>
    /// Walks C on from <paramref name="start"/>, away from <paramref name="cameFrom"/>, to the first node with an edge on
    /// another taxiway (no ramp connector, no runway centreline), and returns that taxiway's name.
    /// </summary>
    private static string FirstTaxiwayOffCBeyond(GroundNode start, GroundNode cameFrom)
    {
        GroundNode previous = cameFrom;
        GroundNode current = start;
        for (int i = 0; i < MaxWalkNodes; i++)
        {
            if (OtherTaxiwayAt(current, "C") is { } name)
            {
                return name;
            }

            GroundNode next = NextAlong(current, "C", GeoMath.BearingTo(previous.Position, current.Position));
            Assert.True(next != previous, $"C ends at node {current.Id} with no other taxiway off it");
            previous = current;
            current = next;
        }

        Assert.Fail($"no taxiway off C within {MaxWalkNodes} nodes of node {start.Id}");
        return "";
    }

    private static string? OtherTaxiwayAt(GroundNode node, string taxiway)
    {
        foreach (IGroundEdge edge in node.Edges)
        {
            if (edge.IsRamp || edge.IsRunwayCenterline)
            {
                continue;
            }

            string[] names = edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];
            string? other = names.FirstOrDefault(n => !string.Equals(n, taxiway, StringComparison.OrdinalIgnoreCase));
            if (other is not null)
            {
                return other;
            }
        }

        return null;
    }

    /// <summary>
    /// The first (by node id) fillet arc, entered from either end, where an aircraft <see cref="IntoFilletFt"/> into the arc
    /// is past the end of the straight taxi edge nearest it: that edge's nearest point clamps to an end node, and both of
    /// its ends lie behind the aircraft.
    /// </summary>
    private static (GroundNode ArcEntry, GroundNode ArcExit, LatLon Position, TrueHeading Heading)? PoseIntoAFillet(AirportGroundLayout layout)
    {
        foreach (GroundNode node in layout.Nodes.Values.OrderBy(n => n.Id))
        {
            foreach (IGroundEdge edge in node.Edges)
            {
                if ((edge is not GroundArc arc) || arc.IsRamp)
                {
                    continue;
                }

                CubicBezier curve = arc.ToBezier();
                if (arc.Nodes[1] == node)
                {
                    curve = new CubicBezier(curve.P3Lat, curve.P3Lon, curve.P2Lat, curve.P2Lon, curve.P1Lat, curve.P1Lon, curve.P0Lat, curve.P0Lon);
                }

                if (IntoFilletPose(layout, node, arc, curve) is { } pose)
                {
                    return pose;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The pose <see cref="IntoFilletFt"/> along <paramref name="curve"/> (the arc as entered at <paramref name="arcEntry"/>),
    /// heading along its tangent, or null when the arc does not fit the case: too short or too long, a node within the
    /// at-node tolerance, the nearest straight taxi edge beyond the on-taxiway offset, its nearest point inside the edge, or
    /// an end of it ahead of the aircraft.
    /// </summary>
    private static (GroundNode ArcEntry, GroundNode ArcExit, LatLon Position, TrueHeading Heading)? IntoFilletPose(
        AirportGroundLayout layout,
        GroundNode arcEntry,
        GroundArc arc,
        CubicBezier curve
    )
    {
        double arcFt = arc.DistanceNm * GeoMath.FeetPerNm;
        if ((arcFt < MinFilletFt) || (arcFt > MaxFilletFt))
        {
            return null;
        }

        double t = IntoFilletFt / arcFt;
        (double lat, double lon) = curve.Evaluate(t);
        var position = new LatLon(lat, lon);
        var heading = new TrueHeading(curve.TangentBearing(t));
        if (
            layout.Nodes.Values.Any(n =>
                (n.Edges.Count > 0) && ((GeoMath.DistanceNm(position, n.Position) * GeoMath.FeetPerNm) <= AirportGroundLayout.AtNodeToleranceFt)
            )
        )
        {
            return null;
        }

        if (
            (layout.FindNearestTaxiEdge(position) is not { } nearest)
            || ((nearest.DistNm * GeoMath.FeetPerNm) > AirportGroundLayout.OnTaxiEdgeMaxOffsetFt)
        )
        {
            return null;
        }

        GroundEdge straight = nearest.Edge;
        (LatLon _, double _, bool clamped) = GeoMath.FootOfPerpendicular(position, straight.Nodes[0].Position, straight.Nodes[1].Position);
        bool bothEndsBehind = straight.Nodes.All(n => GeoMath.AbsBearingDifference(GeoMath.BearingTo(position, n.Position), heading.Degrees) >= 90.0);
        return (clamped && bothEndsBehind) ? (arcEntry, arc.OtherNode(arcEntry), position, heading) : null;
    }

    private AirportGroundLayout? LoadLayout(string airportId)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new TestAirportGroundData().GetLayout(airportId);
    }

    private static AircraftState MakeAircraft(AirportGroundLayout layout, LatLon position, double headingDeg, string airportId, string taxiway)
    {
        var aircraft = new AircraftState
        {
            Callsign = "DAL880",
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(headingDeg),
            TrueTrack = new TrueHeading(headingDeg),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = airportId, Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.CurrentTaxiway = taxiway;
        return aircraft;
    }

    private TaxiRoute Resolve(AirportGroundLayout layout, AircraftState aircraft, string command)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        TaxiCommand taxi = Assert.IsType<TaxiCommand>(parsed.Value);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);
        output.WriteLine($"route: {route.FormatTaxiwaySequence()} ({route.Segments.Count} segments)");
        for (int i = 0; i < Math.Min(4, route.Segments.Count); i++)
        {
            TaxiRouteSegment seg = route.Segments[i];
            output.WriteLine($"  [{i}] {seg.FromNodeId} -> {seg.ToNodeId} on {seg.TaxiwayName}");
        }

        return route;
    }

    private static TaxiRouteSegment FirstLayoutSegment(TaxiRoute route) => route.Segments.First(s => !VirtualNode.IsVirtualEdge(s.Edge.Edge));

    private static bool OnStraightEdgeOf(GroundNode node, string taxiway) => node.Edges.Any(e => (e is not GroundArc) && e.MatchesTaxiway(taxiway));

    private static LatLon Along(LatLon from, LatLon to, double fraction) =>
        new(from.Lat + ((to.Lat - from.Lat) * fraction), from.Lon + ((to.Lon - from.Lon) * fraction));

    private static GroundNode NextAlong(GroundNode node, string taxiway, double bearingDeg)
    {
        GroundNode? best = null;
        double bestDiff = double.MaxValue;
        foreach (IGroundEdge edge in node.Edges)
        {
            if ((edge is not GroundEdge) || !edge.MatchesTaxiway(taxiway))
            {
                continue;
            }

            GroundNode other = edge.OtherNode(node);
            double diff = GeoMath.AbsBearingDifference(GeoMath.BearingTo(node.Position, other.Position), bearingDeg);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = other;
            }
        }

        Assert.NotNull(best);
        return best;
    }
}
