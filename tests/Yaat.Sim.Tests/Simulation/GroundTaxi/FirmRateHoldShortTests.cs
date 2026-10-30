using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A hold short armed while the aircraft is still rolling is judged against the distance it needs to stop at the
/// <em>firm</em> rate (<see cref="CategoryPerformance.ExpediteExitDecelRate"/>), the rate every other mid-taxi stop at a
/// bar uses (<see cref="GroundStopBraking.ChooseStopBraking"/>), not the routine taxi rate. The firm stop is shorter, so
/// a bar beyond it is one the crew can make by braking firmly — the pilot should not refuse it, since AIM 2-3-5.a.1 has
/// the holding position marking be the boundary no part of the aircraft may extend beyond — and a bar inside it is one
/// nobody can make, whose overrun is braked at the firm rate rather than a gentler one and whose stop is moved forward to
/// the point the aircraft can actually reach.
/// </summary>
public class FirmRateHoldShortTests(ITestOutputHelper output)
{
    /// <summary>The runway the KOAK bars under test hold short of.</summary>
    private const string BarRunway = "33";

    /// <summary>The taxiway the first fixture's bar sits on.</summary>
    private const string BarTaxiway = "C";

    /// <summary>
    /// The speed (kts) the aircraft is rolled at into the hold-short decision, capped at its category's taxi speed: high
    /// enough that an overrun at the firm rate lasts more than a second of braking at that rate, so the rate is read off
    /// a steady second rather than the first one's transient.
    /// </summary>
    private const double RollSpeedKts = 24.0;

    /// <summary>Each type under test: a jet, a turboprop and a piston, whose firm and routine rates differ by 1.5x, 1.2x and 2.25x.</summary>
    public static TheoryData<string> Types => ["B738", "C208", "C172"];

    /// <summary>
    /// A B738/C208/C172 rolling on KOAK taxiway C toward the runway 33 bar, given <c>HS 33</c> with the bar's painted
    /// stop between its firm-rate and its taxi-rate stopping distance: the crew can make it by braking firmly, so the
    /// answer carries no "Unable", the nose stops at the marking, and nothing brakes harder than the firm rate.
    /// </summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void HoldShort_InsideTheTaxiButOutsideTheFirmStoppingDistance_IsMadeFirmly(string type)
    {
        if (PlaceAndHoldShort(type, BetweenTheTwoStops) is not { } run)
        {
            return;
        }

        output.WriteLine($"{type}: {run}");
        Assert.False(run.Unable, $"HS {BarRunway} was answered unable: {run.Message}");
        Assert.DoesNotContain("Unable", run.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(run.NosePastBarFt <= NosePastBarToleranceFt, $"{type}'s nose reached {run.NosePastBarFt:F1} ft past the bar's marking");
        Assert.True(
            run.PeakDecelKtsPerSec <= (run.FirmRate + DecelToleranceKtsPerSec),
            $"{type} slowed by {run.PeakDecelKtsPerSec:F2} kt in one second, above the firm rate {run.FirmRate:F1} kt/s"
        );
        Assert.True(
            run.SpeedBeforeHoldKts <= (run.FirmRate + HoldArmToleranceKts(run.FirmRate)),
            $"{type} was still at {run.SpeedBeforeHoldKts:F2} kt on the tick before it stopped: it was stopped dead from speed"
        );
    }

    /// <summary>
    /// The same bar issued inside the distance only the firm rate reaches: nobody can make the marking, the answer is
    /// unable, and the overrun that follows is braked at the firm rate, not the routine one.
    /// </summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void HoldShort_InsideTheFirmStoppingDistance_IsUnableAndBrakesAtTheFirmRate(string type)
    {
        if (PlaceAndHoldShort(type, InsideTheFirmStop) is not { } run)
        {
            return;
        }

        output.WriteLine($"{type}: {run}");
        Assert.True(run.Unable, $"HS {BarRunway} was answered makeable: {run.Message}");
        Assert.Contains("Unable", run.Message, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(run.PeakDecelKtsPerSec, run.FirmRate - DecelToleranceKtsPerSec, run.FirmRate + DecelToleranceKtsPerSec);
        Assert.True(
            run.SpeedBeforeHoldKts <= (run.FirmRate + HoldArmToleranceKts(run.FirmRate)),
            $"{type} was still at {run.SpeedBeforeHoldKts:F2} kt on the tick before it stopped: it was stopped dead from speed"
        );
    }

    /// <summary>
    /// The unable case watched a sub-tick at a time: the moved stop is placed where the firm rate, flown in the physics'
    /// own sub-ticks, brings the aircraft to rest, so it reaches the stop at a crawl — within the ground snap window, one
    /// sub-tick of firm braking — rather than being zeroed there from several knots.
    /// </summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void HoldShort_InsideTheFirmStoppingDistance_ReachesTheMovedStopAtACrawl(string type)
    {
        if (PlaceAndIssueHoldShort(type, InsideTheFirmStopWithRoomForTheMovedStop) is not { } placed)
        {
            return;
        }

        double firmRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategorization.Categorize(type));
        double snapWindowKts = firmRate / SimulationEngine.PhysicsSubTickRate;
        double arrivalKts = SpeedOnArrivalKts(placed);
        output.WriteLine($"{type}: {arrivalKts:F2} kt on the sub-tick before rest; snap window {snapWindowKts:F3} kt");
        Assert.True(placed.Route.GetHoldShortAt(placed.Bar.Id)?.Unable == true, $"HS {BarRunway} was answered makeable");
        Assert.True(
            arrivalKts <= (snapWindowKts + ArrivalSpeedToleranceKts),
            $"{type} was zeroed from {arrivalKts:F2} kt at its moved stop, above the {snapWindowKts:F3} kt snap window"
        );
    }

    /// <summary>
    /// The unable judgement is the stop that is flown: a B738 at 24 kt whose painted stop lies 66 ft ahead — past its bare
    /// firm stopping distance (64.8 ft) but inside the sub-ticked stop (v²/2a + v·dt/2, 69.9 ft) plus the set-back margin
    /// the curve reaches zero short by — cannot make the marking, so the answer is unable.
    /// </summary>
    [Fact]
    public void HoldShort_PastTheBareFirmStopButInsideTheFlownStop_IsUnable()
    {
        const string Type = "B738";
        if (PlaceAndIssueHoldShort(Type, (_, _, halfLengthFt) => InsideTheFlownStopFt + halfLengthFt) is not { } placed)
        {
            return;
        }

        double speedKts = placed.Aircraft.GroundSpeed;
        double firmStopFt = StoppingDistanceFt(speedKts, CategoryPerformance.ExpediteExitDecelRate(AircraftCategorization.Categorize(Type)));
        double halfSubTickFt = speedKts * GroundStopBraking.FeetPerSecondPerKt / SimulationEngine.PhysicsSubTickRate / 2.0;
        double flownStopFt = firmStopFt + halfSubTickFt + GroundNavigator.SetBackStopMarginFt;
        double paintedFt = AlongFtToStop(placed.Ground.Layout, placed.Route, placed.Aircraft, placed.Bar);
        output.WriteLine(
            $"{Type}: painted stop {paintedFt:F2} ft ahead along the route, bare firm stop {firmStopFt:F2} ft, flown stop plus margin "
                + $"{flownStopFt:F2} ft; {placed.Issued.Message}"
        );
        Assert.True(
            (paintedFt > firmStopFt) && (paintedFt < flownStopFt),
            $"the painted stop lies {paintedFt:F2} ft ahead, outside the window from the bare firm stop {firmStopFt:F2} ft to the flown stop "
                + $"plus margin {flownStopFt:F2} ft"
        );
        Assert.True(placed.Route.GetHoldShortAt(placed.Bar.Id)?.Unable == true, $"HS {BarRunway} was answered makeable: {placed.Issued.Message}");
    }

    /// <summary>Where (ft ahead) the painted stop sits for the flown-stop judgement case: past the bare firm stop, inside the flown one.</summary>
    private const double InsideTheFlownStopFt = 66.0;

    /// <summary>
    /// The control just past the new threshold: a B738 at 24 kt whose painted stop lies a foot beyond the flown stop plus
    /// the set-back margin is made, and brakes onto the marking without being stopped dead. The bound is two sub-ticks of
    /// firm braking (<see cref="GridResidualArrivalKts"/>): the firm braking curve puts the aircraft on its stop off the a·dt
    /// grid, so the arrival threshold takes the hold one sub-tick early — a B738 is zeroed from 2.09 kt against a 1.875 kt
    /// snap window, where a dead stop from taxi speed would be 24 kt.
    /// </summary>
    [Fact]
    public void HoldShort_JustPastTheFlownStop_IsMadeWithoutADeadStop()
    {
        const string Type = "B738";
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategorization.Categorize(Type));
        if (PlaceAndIssueHoldShort(Type, (firmStopFt, _, halfLengthFt) => JustPastTheFlownStopFt(firmStopFt) + halfLengthFt) is not { } placed)
        {
            return;
        }

        double arrivalKts = SpeedOnArrivalKts(placed);
        output.WriteLine($"{Type}: {arrivalKts:F2} kt on the sub-tick before rest; {placed.Issued.Message}");
        Assert.False(placed.Route.GetHoldShortAt(placed.Bar.Id)?.Unable == true, $"HS {BarRunway} was answered unable: {placed.Issued.Message}");
        Assert.True(
            arrivalKts <= GridResidualArrivalKts(firmRate),
            $"{Type} was zeroed from {arrivalKts:F2} kt at the painted stop, "
                + $"above two sub-ticks of firm braking ({GridResidualArrivalKts(firmRate):F3} kt)"
        );
    }

    /// <summary>
    /// A foot past the flown stop from <see cref="RollSpeedKts"/> plus the set-back margin: the flown stop
    /// (<see cref="FlownStopFt"/>), the margin and the foot.
    /// </summary>
    private static double JustPastTheFlownStopFt(double firmStopFt) => FlownStopFt(firmStopFt) + GroundNavigator.SetBackStopMarginFt + 1.0;

    /// <summary>
    /// The stop flown from <see cref="RollSpeedKts"/> on the sub-ticked firm curve: the bare firm stop
    /// <paramref name="firmStopFt"/> plus half a sub-tick of travel.
    /// </summary>
    private static double FlownStopFt(double firmStopFt) =>
        firmStopFt + (RollSpeedKts * GroundStopBraking.FeetPerSecondPerKt / SimulationEngine.PhysicsSubTickRate / 2.0);

    /// <summary>
    /// A B738 at 24 kt whose painted stop lies inside the margin window — past the flown stop (v²/2a + v·dt/2) but within
    /// the set-back margin of it — is answered unable, since the firm curve onto a painted stop reaches zero that margin
    /// short; but its stop is never moved backward to the flown stop. It stays at the painted point, and the aircraft,
    /// braked on the sub-ticked firm curve, comes to rest there rather than short of the marking.
    /// </summary>
    [Fact]
    public void HoldShort_InsideTheMarginWindow_IsUnableAndKeepsThePaintedStop()
    {
        const string Type = "B738";
        if (PlaceAndIssueHoldShort(Type, (firmStopFt, _, halfLengthFt) => InsideTheMarginWindowFt(firmStopFt) + halfLengthFt) is not { } placed)
        {
            return;
        }

        HoldShortRun run = Watch(placed);
        double restPastPaintedFt =
            GeoMath.AlongTrackDistanceNm(placed.Aircraft.Position, placed.PaintedStop, new TrueHeading(placed.BarBearingDeg)) * GeoMath.FeetPerNm;
        output.WriteLine($"{Type}: {run}; at rest {restPastPaintedFt:F2} ft past the painted stop (negative = short of it)");
        Assert.True(run.Unable, $"HS {BarRunway} was answered makeable: {run.Message}");
        Assert.Contains("Unable", run.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(run.StopMovedFt <= StopMovedToleranceFt, $"the bar's stop was moved {run.StopMovedFt:F2} ft off the painted point");
        Assert.True(
            (restPastPaintedFt >= -RestsShortOfThePaintedStopFt) && (restPastPaintedFt <= RestsAtTheStopFt),
            $"{Type} came to rest {restPastPaintedFt:F2} ft past the painted stop (negative = short of it); "
                + $"allowed {RestsShortOfThePaintedStopFt:F1} ft short to {RestsAtTheStopFt:F1} ft past"
        );
    }

    /// <summary>
    /// How far (ft) short of the painted stop the margin-window case may come to rest. Measured, a B738 rests 0.06 ft past
    /// it; with the stop moved back to the flown stop it rested 1.37 ft short.
    /// </summary>
    private const double RestsShortOfThePaintedStopFt = 0.5;

    /// <summary>
    /// Where (ft ahead) the margin-window case's painted stop sits: <see cref="MarginWindowDepthFt"/> past the flown stop
    /// (<see cref="FlownStopFt"/>), inside the set-back margin.
    /// </summary>
    private static double InsideTheMarginWindowFt(double firmStopFt) => FlownStopFt(firmStopFt) + MarginWindowDepthFt;

    /// <summary>How far (ft) past the flown stop the margin-window case's painted stop sits, short of the 2 ft margin.</summary>
    private const double MarginWindowDepthFt = 1.5;

    /// <summary>
    /// Slack (kts) on the arrival speed against the snap window: the navigator measures the moved stop about 0.1 ft off where
    /// the placement put it (a C208 holds 24.02 kt, not 24.00, on the sub-tick the stop moves), which carries to the last sub-tick.
    /// </summary>
    private const double ArrivalSpeedToleranceKts = 0.05;

    /// <summary>
    /// How far (ft) from the bar's node the crawl case is issued: inside the firm-stop distance of the painted stop, as
    /// <see cref="InsideTheFirmStop"/>, but far enough out that the moved stop — the firm stop plus half a sub-tick of
    /// travel — still falls short of the node. Nearer, the stop is clamped at the node and the aircraft runs onto it at
    /// speed by design (a C172 issued at <see cref="InsideTheFirmStopFraction"/> needs 79.2 ft and has 77).
    /// </summary>
    private static double InsideTheFirmStopWithRoomForTheMovedStop(double firmStopFt, double taxiStopFt, double halfLengthFt) =>
        (firmStopFt * CrawlInsideTheFirmStopFraction) + halfLengthFt;

    /// <summary>How far into the firm-rate stopping distance the crawl case's bar sits.</summary>
    private const double CrawlInsideTheFirmStopFraction = 0.95;

    /// <summary>
    /// A bar a segment past the one in progress, inside the firm stopping distance: the bar is ahead of the node the
    /// navigator is currently targeting, so nothing re-aims at it and its painted stop has to be slid forward along the
    /// route by the phase itself. The route reaches it over a fillet arc, so the projection has to be measured and placed
    /// on that arc — a chord measurement would carry the stop off the pavement and past the bar's node.
    /// </summary>
    [Fact]
    public void HoldShort_BeyondTheSegmentInProgress_MovesTheStopAlongTheRoute()
    {
        const string Type = "B738";
        if (HoldShortBeyondTheJunction(Type) is not { } run)
        {
            return;
        }

        output.WriteLine($"{Type}: {run}");
        Assert.True(run.Unable, $"the hold short was answered makeable: {run.Message}");
        Assert.True(run.StopMovedFt >= StopMovedToleranceFt, $"the bar's stop was not moved forward: {run.StopMovedFt:F1} ft");
        Assert.True(
            (run.RestPastStopFt <= RestsAtTheStopFt) && (run.RestPastStopFt >= -StopsShortOfTheStopFt),
            $"{Type} came to rest {run.RestPastStopFt:F1} ft past its moved stop (negative = short of it); "
                + $"allowed {StopsShortOfTheStopFt:F1} ft short to {RestsAtTheStopFt:F1} ft past"
        );
    }

    /// <summary>
    /// The across-a-fillet case watched a sub-tick at a time: the moved stop is reached after the aircraft crosses the
    /// junction node, and on the sub-tick the navigator arrives at that node it publishes no speed of its own, so the
    /// unable bar's own sub-ticked curve has to hold the published target. The S→S1 corner limit puts the aircraft above
    /// the curve before the node, so the arrival threshold takes the hold one sub-tick early: a B738 is zeroed from 2.10 kt
    /// against a 1.875 kt snap window, within <see cref="CrawlPastANodeArrivalKts"/> (a plain regression bound here; the
    /// straight-node case is the one the cap decides).
    /// </summary>
    [Fact]
    public void HoldShort_BeyondTheSegmentInProgress_ReachesTheMovedStopAtACrawl()
    {
        const string Type = "B738";
        if (PlaceBeyondTheJunction(Type, MovedStopPastTheJunction) is not { } placed)
        {
            return;
        }

        double firmRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategorization.Categorize(Type));
        double snapWindowKts = firmRate / SimulationEngine.PhysicsSubTickRate;
        int startSegment = placed.Route.CurrentSegmentIndex;
        double arrivalKts = SpeedOnArrivalKts(placed);
        output.WriteLine($"{Type}: {arrivalKts:F2} kt on the sub-tick before rest; snap window {snapWindowKts:F3} kt");
        Assert.True(placed.Route.GetHoldShortAt(placed.Bar.Id)?.Unable == true, $"the hold short was answered makeable: {placed.Issued.Message}");
        Assert.True(
            placed.Route.CurrentSegmentIndex > startSegment,
            $"{Type} came to rest on segment {placed.Route.CurrentSegmentIndex}, never crossing the junction past segment {startSegment}"
        );
        double boundKts = CrawlPastANodeArrivalKts(firmRate);
        Assert.True(arrivalKts <= boundKts, $"{Type} was zeroed from {arrivalKts:F2} kt at its moved stop, above {boundKts:F2} kt");
    }

    /// <summary>
    /// A straight run with a taxiway node between the aircraft and an unable bar's moved stop, crossed while riding the firm
    /// sub-ticked curve: on the sub-tick the navigator arrives at that node it publishes no speed of its own.
    /// </summary>
    [Fact]
    public void HoldShort_MovedStopPastAStraightNode_ReachesTheMovedStopAtACrawl()
    {
        const string Type = "B738";
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return;
        }

        (GroundNode bar, GroundNode node, GroundEdge approach) =
            FirstBarPastAStraightNode(ground.Layout)
            ?? throw new InvalidOperationException("no SFO runway bar lies a straight node past a long edge");
        GroundNode far = approach.OtherNode(node);
        string target = bar.RunwayId?.End1 ?? throw new InvalidOperationException($"node {bar.Id} holds short of no runway");
        double bearingDeg = GeoMath.BearingTo(far.Position, node.Position);
        AircraftCategory category = AircraftCategorization.Categorize(Type);
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(category);
        AircraftState aircraft = PlaceRolling(
            ground,
            Type,
            (far.Position, bearingDeg),
            approach.TaxiwayName,
            CategoryPerformance.TaxiSpeed(category)
        );
        Send(ground, aircraft, $"TAXI #{far.Id} #{bar.Id}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException($"{Type} has no taxi route");
        HoldShortPoint barPoint = HoldShortAt(route, bar);
        LatLon paintedStop = new(barPoint.Latitude!.Value, barPoint.Longitude!.Value);
        int barSegmentIndex = BarSegmentIndex(route, bar);
        output.WriteLine(
            $"bar {bar.Id} {GeoMath.DistanceNm(node.Position, bar.Position) * GeoMath.FeetPerNm:F1} ft past node {node.Id}, approach "
                + $"{approach.TaxiwayName} {approach.DistanceNm * GeoMath.FeetPerNm:F0} ft from {far.Id}; painted on the bar's segment "
                + $"{route.StopLiesOnSegment(barSegmentIndex, barPoint)}"
        );
        RollOnUntil(
            ground,
            aircraft,
            route,
            (a, r) =>
            {
                double flownFt =
                    StoppingDistanceFt(a.GroundSpeed, firmRate)
                    + (a.GroundSpeed * GroundStopBraking.FeetPerSecondPerKt / SimulationEngine.PhysicsSubTickRate / 2.0);
                double toNodeFt = r.RemainingDistanceFt(a.Position, r.CurrentSegmentIndex + 1);
                return ((flownFt - toNodeFt) >= MovedStopPastTheJunctionFt) && (AlongFtToStop(ground.Layout, r, a, bar) < (flownFt - 1.0));
            }
        );
        CommandResult issued = ground.Engine.SendCommand(aircraft.Callsign, $"HS {target}");
        var placed = new HoldShortCase
        {
            Ground = ground,
            Aircraft = aircraft,
            Route = route,
            Bar = bar,
            BarBearingDeg = bearingDeg,
            PaintedStop = paintedStop,
            Issued = issued,
            IssuedAt = aircraft.Position,
        };
        int startSegment = route.CurrentSegmentIndex;
        output.WriteLine($"{Type} at {aircraft.GroundSpeed:F2} kt on segment {startSegment}; {issued.Message}");
        double arrivalKts = SpeedOnArrivalKts(placed);
        output.WriteLine($"{Type}: {arrivalKts:F2} kt on the sub-tick before rest; on segment {route.CurrentSegmentIndex}");
        Assert.True(route.GetHoldShortAt(bar.Id)?.Unable == true, $"the hold short was answered makeable: {issued.Message}");
        Assert.True(route.CurrentSegmentIndex > startSegment, $"{Type} never crossed node {node.Id}");
        double boundKts = CrawlPastANodeArrivalKts(firmRate);
        Assert.True(arrivalKts <= boundKts, $"{Type} was zeroed from {arrivalKts:F2} kt at its moved stop, above {boundKts:F2} kt");
    }

    /// <summary>
    /// The first SFO runway bar (by node id) whose last edge in is straight, 70–150 ft long, and continues straight back
    /// (within 3°) over a straight edge of 200 ft or more: that bar, the node between the edges and the long edge.
    /// </summary>
    private static (GroundNode Bar, GroundNode Node, GroundEdge Approach)? FirstBarPastAStraightNode(AirportGroundLayout layout)
    {
        foreach (
            GroundNode bar in layout
                .Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is not null))
                .OrderBy(n => n.Id)
        )
        {
            foreach (GroundEdge last in bar.Edges.OfType<GroundEdge>().Where(e => (e.DistanceNm * GeoMath.FeetPerNm) is >= 40.0 and <= 170.0))
            {
                GroundNode node = last.OtherNode(bar);
                double inDeg = GeoMath.BearingTo(node.Position, bar.Position);
                GroundEdge? approach = node
                    .Edges.OfType<GroundEdge>()
                    .Where(e => !ReferenceEquals(e, last) && ((e.DistanceNm * GeoMath.FeetPerNm) >= 150.0))
                    .FirstOrDefault(e => GeoMath.AbsBearingDifference(GeoMath.BearingTo(e.OtherNode(node).Position, node.Position), inDeg) <= 6.0);
                if (approach is not null)
                {
                    return (bar, node, approach);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The across-a-fillet fixture with the hold short issued as the aircraft starts down the fillet: the stop it can fly
    /// lies on the fillet in progress, before the segment that ends at the bar's node, so it is a set-back stop and the
    /// hold is taken by <c>TaxiingPhase.TryHoldAtSetBackStop</c> rather than by arriving at a node. The aircraft still
    /// reaches it at a crawl, within the bound of the junction case.
    /// </summary>
    [Fact]
    public void HoldShort_MovedStopOnTheFilletInProgress_ReachesTheMovedStopAtACrawl()
    {
        const string Type = "B738";
        if (PlaceBeyondTheJunction(Type, (_, _) => true) is not { } placed)
        {
            return;
        }

        double firmRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategorization.Categorize(Type));
        int barSegmentIndex = BarSegmentIndex(placed.Route, placed.Bar);
        double arrivalKts = SpeedOnArrivalKts(placed);
        bool stopOnBarSegment = placed.Route.StopLiesOnSegment(barSegmentIndex, HoldShortAt(placed.Route, placed.Bar));
        output.WriteLine($"{Type}: {arrivalKts:F2} kt on the sub-tick before rest; stop on the bar's segment {stopOnBarSegment}");
        Assert.True(placed.Route.GetHoldShortAt(placed.Bar.Id)?.Unable == true, $"the hold short was answered makeable: {placed.Issued.Message}");
        Assert.True(placed.Route.GetHoldShortAt(placed.Bar.Id)?.StopMoved == true, "the bar's stop was not moved");
        Assert.False(stopOnBarSegment, $"the moved stop lies on the bar's segment {barSegmentIndex}, not on the fillet before it");
        double boundKts = CrawlPastANodeArrivalKts(firmRate);
        Assert.True(arrivalKts <= boundKts, $"{Type} was zeroed from {arrivalKts:F2} kt at its moved stop on the fillet, above {boundKts:F2} kt");
    }

    /// <summary>
    /// The arrival-speed bound (kts) for a stop the aircraft joins the sub-ticked curve of off its a·dt grid: two sub-ticks of
    /// firm braking plus <see cref="ArrivalSpeedToleranceKts"/>. Joined off the grid, the last sub-tick on the curve ends
    /// inside the arrival threshold whenever the speed left is under about 0.4·a·dt, and the hold is taken one sub-tick early.
    /// </summary>
    private static double GridResidualArrivalKts(double firmRate) =>
        (2.0 * firmRate / SimulationEngine.PhysicsSubTickRate) + ArrivalSpeedToleranceKts;

    /// <summary>
    /// True once the aircraft has rolled far enough along the fillet that the stop it can fly at the firm rate — the bare
    /// firm stop plus half a sub-tick of travel — lies <see cref="MovedStopPastTheJunctionFt"/> or more past the junction
    /// the fillet ends at.
    /// </summary>
    private static bool MovedStopPastTheJunction(AircraftState aircraft, TaxiRoute route)
    {
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(AircraftCategorization.Categorize(aircraft.AircraftType));
        double flownStopFt =
            StoppingDistanceFt(aircraft.GroundSpeed, firmRate)
            + (aircraft.GroundSpeed * GroundStopBraking.FeetPerSecondPerKt / SimulationEngine.PhysicsSubTickRate / 2.0);
        double toJunctionFt = route.RemainingDistanceFt(aircraft.Position, route.CurrentSegmentIndex + 1);
        return (flownStopFt - toJunctionFt) >= MovedStopPastTheJunctionFt;
    }

    /// <summary>
    /// The arrival-speed bound (kts) of an unable bar's moved stop reached past a route node: a quarter sub-tick of firm
    /// braking above one sub-tick, plus <see cref="ArrivalSpeedToleranceKts"/> — 2.39 kt for a jet. It guards the cap the
    /// taxi phase holds the published target to on the sub-tick the navigator arrives at a node and publishes no speed
    /// (<c>TaxiingPhase.UnableStopCapKts</c>): measured, a B738 crossing a straight SFO node on the firm curve arrives at
    /// 1.88 kt with the cap and 7.49 kt without it. On the fillet fixtures, where the corner limit has put the aircraft off
    /// the curve before the node, it is a plain regression bound: 2.10 kt across the junction and 1.88 kt at a set-back
    /// stop on the fillet, the same with the cap removed.
    /// </summary>
    private static double CrawlPastANodeArrivalKts(double firmRate) =>
        (1.25 * firmRate / SimulationEngine.PhysicsSubTickRate) + ArrivalSpeedToleranceKts;

    /// <summary>How far (ft) past the junction the crawl-across-a-junction case's moved stop lies, at least.</summary>
    private const double MovedStopPastTheJunctionFt = 10.0;

    /// <summary>
    /// The path a controller actually uses: the aircraft is cleared across runway 33 to a node past it, the crossing is
    /// pre-cleared with <c>CROSS</c>, and <c>HS 33</c> arrives while it is still rolling. Outside the firm stop the crew
    /// makes it; inside it the stop is moved forward and the overrun is braked at the firm rate.
    /// </summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void HoldShort_IssuedWhileRollingAcrossTheRunway_IsMadeFirmly(string type)
    {
        if (RollAcrossTheRunway(type, madeFirmly: true) is not { } run)
        {
            return;
        }

        output.WriteLine($"{type}: {run}");
        Assert.False(run.Unable, $"HS {BarRunway} was answered unable: {run.Message}");
        Assert.True(run.NosePastBarFt <= NosePastBarToleranceFt, $"{type}'s nose reached {run.NosePastBarFt:F1} ft past the bar's marking");
        Assert.True(
            run.PeakDecelKtsPerSec <= (run.FirmRate + DecelToleranceKtsPerSec),
            $"{type} slowed by {run.PeakDecelKtsPerSec:F2} kt in one second, above the firm rate {run.FirmRate:F1} kt/s"
        );
    }

    /// <summary>
    /// The same rolling approach with <c>HS 33</c> arriving inside the firm stop: the bar is refused, its stop moved
    /// forward, and the overrun braked at the firm rate onto that stop, which the aircraft reaches at a crawl. Here the
    /// navigator was already aimed at the painted stop, so moving the stop moves its target further on: read against the
    /// distance to the old target, that once tripped the overshoot check and took the hold on the spot, from taxi speed.
    /// </summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void HoldShort_IssuedWhileRollingAcrossTheRunway_InsideTheFirmStop_IsUnable(string type)
    {
        if (RollAcrossTheRunway(type, madeFirmly: false) is not { } run)
        {
            return;
        }

        output.WriteLine($"{type}: {run}");
        Assert.True(run.Unable, $"HS {BarRunway} was answered makeable: {run.Message}");
        Assert.True(run.StopMovedFt >= StopMovedToleranceFt, $"the bar's stop was not moved forward: {run.StopMovedFt:F1} ft");
        Assert.InRange(run.PeakDecelKtsPerSec, run.FirmRate - DecelToleranceKtsPerSec, run.FirmRate + DecelToleranceKtsPerSec);
        Assert.True(
            run.SpeedBeforeHoldKts <= (run.FirmRate + HoldArmToleranceKts(run.FirmRate)),
            $"{type} was still at {run.SpeedBeforeHoldKts:F2} kt on the tick before it stopped: it was stopped dead from speed"
        );
    }

    /// <summary>
    /// The rolling unable case watched a sub-tick at a time: the aircraft reaches its moved stop within two sub-ticks of
    /// firm braking (<see cref="GridResidualArrivalKts"/>), never zeroed from taxi speed.
    /// </summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void HoldShort_IssuedWhileRollingAcrossTheRunway_InsideTheFirmStop_ReachesTheMovedStopAtACrawl(string type)
    {
        AircraftCategory category = AircraftCategorization.Categorize(type);
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(category);
        RollUp? rollUp = SolveRollUp(type, madeFirmly: false, firmRate, CategoryPerformance.TaxiDecelRate(category), RollKts(category));
        if (rollUp is null)
        {
            return;
        }

        HoldShortPoint barPoint = HoldShortAt(rollUp.Route, rollUp.Bar);
        CommandResult issued = rollUp.Ground.Engine.SendCommand(rollUp.Aircraft.Callsign, $"HS {BarRunway}");
        var placed = new HoldShortCase
        {
            Ground = rollUp.Ground,
            Aircraft = rollUp.Aircraft,
            Route = rollUp.Route,
            Bar = rollUp.Bar,
            BarBearingDeg = rollUp.BearingDeg,
            PaintedStop = new LatLon(barPoint.Latitude!.Value, barPoint.Longitude!.Value),
            Issued = issued,
            IssuedAt = rollUp.Aircraft.Position,
        };
        double arrivalKts = SpeedOnArrivalKts(placed);
        output.WriteLine($"{type}: {arrivalKts:F2} kt on the sub-tick before rest; {issued.Message}");
        Assert.True(rollUp.Route.GetHoldShortAt(rollUp.Bar.Id)?.Unable == true, $"HS {BarRunway} was answered makeable: {issued.Message}");
        Assert.True(
            arrivalKts <= GridResidualArrivalKts(firmRate),
            $"{type} was zeroed from {arrivalKts:F2} kt at its moved stop, "
                + $"above two sub-ticks of firm braking ({GridResidualArrivalKts(firmRate):F3} kt)"
        );
    }

    /// <summary>
    /// A hold short armed on a bar whose stop has to be slid forward, placed half the firm stop short of the painted bar:
    /// the stop moves twice as far as the painted bar sits, so an aircraft that brakes to the painted bar and never
    /// reaches the moved point shows the aim and the stop disagreeing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void HoldShort_MovedStop_IsThePointTheNavigatorStopsAt(string type)
    {
        if (HoldShortAtHalfTheFirmStop(type, [$"HS {BarRunway}"]) is not { } run)
        {
            return;
        }

        output.WriteLine($"{type}: {run}");
        Assert.True(run.Unable, $"HS {BarRunway} was answered makeable: {run.Message}");
        Assert.True(run.StopMovedFt >= StopMovedToleranceFt, $"the bar's stop was not moved forward: {run.StopMovedFt:F1} ft");
        Assert.True(
            run.RolledFt >= (run.MovedStopFt - MovedStopShortFt),
            $"{type} stopped {run.RolledFt:F1} ft from where the hold short arrived, short of the {run.MovedStopFt:F1} ft moved stop: "
                + "it braked to the painted bar and never reached the moved point"
        );
    }

    /// <summary>
    /// The same bar armed by <c>RES HS 33</c> instead. A resume arms its hold-shorts without the dispatch's unable check, so
    /// the bar is flagged by the taxi phase's re-aim on its next tick, and the re-aim has to slide the stop before it aims:
    /// aiming first leaves the navigator flying at the painted bar while the stop it brakes onto sits further on.
    /// </summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void HoldShort_ArmedByResume_MovedStop_IsThePointTheNavigatorStopsAt(string type)
    {
        if (HoldShortAtHalfTheFirmStop(type, ["HOLD", $"RES HS {BarRunway}"]) is not { } run)
        {
            return;
        }

        output.WriteLine($"{type}: {run}");
        Assert.True(run.Unable, $"HOLD then RES HS {BarRunway} left the bar makeable: {run.Message}");
        Assert.True(run.StopMovedFt >= StopMovedToleranceFt, $"the bar's stop was not moved forward: {run.StopMovedFt:F1} ft");
    }

    /// <summary>
    /// A B738 braking onto its moved stop is told <c>HOLD</c>, comes to rest 100 ft short of that stop, and is told
    /// <c>RES</c>: from rest it has the room to stop at the routine taxi rate, so it does — the firm rate is chosen as for any
    /// makeable stop (<see cref="GroundStopBraking.ChooseStopBraking"/>), only while the taxi rate cannot make the stop.
    /// </summary>
    [Fact]
    public void HoldShort_MovedStop_ResumedFromRestWellShort_BrakesAtTheTaxiRate()
    {
        const string Type = "B738";
        if (ArmAtHalfTheFirmStop(Type, [$"HS {BarRunway}"]) is not { } armed)
        {
            return;
        }

        AircraftState aircraft = armed.Aircraft;
        Send(armed.Ground, aircraft, "HOLD");
        for (int second = 0; (second < StopWatchSeconds) && (aircraft.GroundSpeed > StillKts); second++)
        {
            armed.Ground.Engine.TickOneSecond();
        }

        LatLon movedStop = StopPosition(armed.Route, armed.Bar);
        var backDeg = new TrueHeading((armed.BarBearingDeg + 180.0) % 360.0);
        aircraft.Position = GeoMath.ProjectPoint(movedStop, backDeg, RestShortOfTheMovedStopFt / GeoMath.FeetPerNm);
        CommandResult resumed = Send(armed.Ground, aircraft, "RES");
        LatLon restedAt = aircraft.Position;
        // One second rolled off from rest first: the watch runs while the aircraft moves.
        armed.Ground.Engine.TickOneSecond();
        HoldShortRun run = Watch(armed with { Issued = resumed, IssuedAt = restedAt });

        output.WriteLine($"{Type}: {run}");
        Assert.True(run.Unable, "the bar lost its unable flag");
        Assert.True(
            run.PeakDecelKtsPerSec <= (run.TaxiRate + DecelToleranceKtsPerSec),
            $"{Type} slowed by {run.PeakDecelKtsPerSec:F2} kt in one second from rest {RestShortOfTheMovedStopFt:F0} ft short of its "
                + $"moved stop, above the taxi rate {run.TaxiRate:F1} kt/s"
        );
    }

    /// <summary>
    /// A bar flagged unable at dispatch while no taxi phase was flying the aircraft — as an <c>HS</c> given during a follow
    /// with an assigned route — whose stop no taxi phase has moved yet, handed to a fresh <see cref="TaxiingPhase"/> with the
    /// aircraft at rest, as the follow resuming its assigned route does: from rest the bar is makeable, so the taxi keeps
    /// the painted stop and holds there, rather than moving the stop to where the aircraft stands and halting mid-taxiway.
    /// </summary>
    [Fact]
    public void HoldShort_FlaggedUnableBeforeAFreshTaxiAtRest_HoldsAtThePaintedBar()
    {
        const string Type = "B738";
        if (PlaceAndIssueHoldShort(Type, InsideTheFirmStop) is not { } placed)
        {
            return;
        }

        AircraftState aircraft = placed.Aircraft;
        Assert.True(placed.Route.GetHoldShortAt(placed.Bar.Id)?.Unable == true, $"HS {BarRunway} was answered makeable: {placed.Issued.Message}");
        double toPaintedFt = AlongFtToStop(placed.Ground.Layout, placed.Route, aircraft, placed.Bar);
        aircraft.IndicatedAirspeed = 0;
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new TaxiingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, placed.Ground.Layout));
        bool rolled = false;
        for (int second = 0; (second < StopWatchSeconds) && !(rolled && (aircraft.GroundSpeed <= StillKts)); second++)
        {
            placed.Ground.Engine.TickOneSecond();
            rolled |= aircraft.GroundSpeed > StillKts;
        }

        double stopShiftFt = FeetBetween(StopPosition(placed.Route, placed.Bar), placed.PaintedStop);
        double restFromPaintedFt = FeetBetween(aircraft.Position, placed.PaintedStop);
        output.WriteLine(
            $"{Type}: {toPaintedFt:F1} ft from the painted stop at rest; rolled {rolled}; stop shifted {stopShiftFt:F1} ft; "
                + $"at rest {restFromPaintedFt:F1} ft from the painted stop; unable {placed.Route.GetHoldShortAt(placed.Bar.Id)?.Unable}"
        );
        Assert.True(rolled, $"{Type} never moved off from rest {toPaintedFt:F1} ft short of the painted stop: it halted mid-taxiway");
        Assert.True(stopShiftFt <= StopMovedToleranceFt, $"the fresh taxi phase moved the painted stop {stopShiftFt:F1} ft");
        Assert.True(restFromPaintedFt <= RestsAtTheStopFt, $"{Type} came to rest {restFromPaintedFt:F1} ft from the painted stop, not at the bar");
    }

    /// <summary>How far (ft) short of the moved stop the held aircraft is put at rest before it is resumed.</summary>
    private const double RestShortOfTheMovedStopFt = 100.0;

    /// <summary>
    /// A bar whose stop the taxi phase has moved, handed to a fresh <see cref="TaxiingPhase"/> over the same route before the
    /// aircraft reaches it, at rest — as a follow whose lead has gone resumes its assigned route, sharing that route's
    /// hold-short points: the new phase must not slide the stop again from where the aircraft now stands.
    /// </summary>
    [Fact]
    public void HoldShort_MovedStop_IsNotMovedAgainByAFreshTaxiPhase()
    {
        const string Type = "B738";
        if (ArmAtHalfTheFirmStop(Type, [$"HS {BarRunway}"]) is not { } armed)
        {
            return;
        }

        AircraftState aircraft = armed.Aircraft;
        LatLon movedStop = StopPosition(armed.Route, armed.Bar);
        aircraft.IndicatedAirspeed = 0;
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new TaxiingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, armed.Ground.Layout));
        armed.Ground.Engine.TickOneSecond();

        double movedAgainFt = FeetBetween(StopPosition(armed.Route, armed.Bar), movedStop);
        output.WriteLine($"{Type}: the fresh taxi phase moved the stop {movedAgainFt:F1} ft");
        Assert.True(movedAgainFt <= StopMovedToleranceFt, $"the fresh taxi phase slid the bar's moved stop {movedAgainFt:F1} ft again");
    }

    /// <summary>
    /// A B738 rolling toward a KOAK runway 33 bar on a route that goes on across the runway, handed <c>HS 33</c> with the
    /// bar's node closer than its firm stop: the stop it can make lies past the node, and is clamped there rather than
    /// carried on along the route toward the runway.
    /// </summary>
    [Fact]
    public void HoldShort_FirmStopPastTheBarNode_IsClampedAtTheNode()
    {
        const string Type = "B738";
        if (BuildOak() is not { } ground)
        {
            return;
        }

        (GroundNode bar, GroundEdge approach, GroundNode behind, GroundNode wellPast) =
            FirstBarACrossingReaches(ground.Layout)
            ?? throw new InvalidOperationException($"no KOAK runway {BarRunway} bar lies on a taxiway that crosses to a node past it");
        double bearingDeg = GeoMath.BearingTo(behind.Position, bar.Position);
        AircraftCategory category = AircraftCategorization.Categorize(Type);
        double rollKts = RollKts(category);
        double firmStopFt = StoppingDistanceFt(rollKts, CategoryPerformance.ExpediteExitDecelRate(category));
        AircraftState aircraft = PlaceRolling(ground, Type, (behind.Position, bearingDeg), approach.TaxiwayName, rollKts);
        Send(ground, aircraft, $"TAXI #{behind.Id} #{wellPast.Id}");
        TrySend(ground, aircraft, "CROSS");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException($"{Type} has no taxi route");
        double toNodeFt = firmStopFt * NodeInsideTheFirmStopFraction;
        aircraft.Position = GeoMath.ProjectPoint(bar.Position, new TrueHeading((bearingDeg + 180.0) % 360.0), toNodeFt / GeoMath.FeetPerNm);
        CommandResult issued = Send(ground, aircraft, $"HS {BarRunway}");
        ground.Engine.TickOneSecond();

        LatLon stop = StopPosition(route, bar);
        double stopPastNodeFt = GeoMath.AlongTrackDistanceNm(stop, bar.Position, new TrueHeading(bearingDeg)) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"{Type} at {rollKts:F1} kt {toNodeFt:F1} ft from node {bar.Id}, firm stop {firmStopFt:F1} ft; {issued.Message}; "
                + $"stop {FeetBetween(stop, bar.Position):F1} ft from the node, {stopPastNodeFt:F1} ft past it"
        );
        Assert.True(route.GetHoldShortAt(bar.Id)?.Unable == true, $"HS {BarRunway} was answered makeable: {issued.Message}");
        Assert.True(
            FeetBetween(stop, bar.Position) <= StopPastNodeToleranceFt,
            $"{Type}'s moved stop lies {FeetBetween(stop, bar.Position):F1} ft from the bar's node {bar.Id} ({stopPastNodeFt:F1} ft past it)"
        );
    }

    /// <summary>How far from the bar's node the clamped case is issued, as a fraction of the firm stop.</summary>
    private const double NodeInsideTheFirmStopFraction = 0.6;

    /// <summary>
    /// How far (ft) short of the moved stop the aircraft may come to rest. On the bar's own segment the sub-ticked curve
    /// brings it to the stop itself; measured, a B738 rests 1.2 ft, a C208 1.4 ft and a C172 1.3 ft short of it.
    /// </summary>
    private const double MovedStopShortFt = 2.0;

    /// <summary>Where the painted stop sits for the moved-stop case, as a fraction of the firm stop.</summary>
    private const double ArmedStopFraction = 0.5;

    /// <summary>
    /// <see cref="ArmAtHalfTheFirmStop"/>, with the navigator asserted to be aimed at the moved stop one tick in, then
    /// watched to a stop. Null when the fixture is unavailable.
    /// </summary>
    private HoldShortRun? HoldShortAtHalfTheFirmStop(string type, IReadOnlyList<string> commands)
    {
        if (ArmAtHalfTheFirmStop(type, commands) is not { } armed)
        {
            return null;
        }

        LatLon aimed = AimPoint(armed.Aircraft);
        LatLon movedStop = StopPosition(armed.Route, armed.Bar);
        output.WriteLine(
            $"aimed at {aimed}: {FeetBetween(aimed, movedStop):F1} ft from the moved stop, "
                + $"{FeetBetween(aimed, armed.PaintedStop):F1} ft from the painted one"
        );
        Assert.True(
            FeetBetween(aimed, movedStop) <= AimedAtTheStopFt,
            $"{type}: the navigator is aimed {FeetBetween(aimed, movedStop):F1} ft from the moved stop it has to brake onto and "
                + $"{FeetBetween(aimed, armed.PaintedStop):F1} ft from the painted bar"
        );
        return Watch(armed);
    }

    /// <summary>
    /// A <paramref name="type"/> rolling on the runway 33 bar's KOAK taxiway-C edge, handed <paramref name="commands"/> in
    /// one breath, the last a hold short of runway 33, with the painted stop half the firm stop ahead so the moved one is twice that far on, and
    /// ticked once. The case as that tick leaves it, measured from where the aircraft then stands. Null when the fixture is
    /// unavailable.
    /// </summary>
    private HoldShortCase? ArmAtHalfTheFirmStop(string type, IReadOnlyList<string> commands)
    {
        if (BuildOak() is not { } ground)
        {
            return null;
        }

        AirportGroundLayout layout = ground.Layout;
        GroundNode bar =
            TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, BarRunway, BarTaxiway)
            ?? throw new InvalidOperationException($"KOAK has no runway {BarRunway} hold-short on taxiway {BarTaxiway}");
        List<GroundEdge> barEdges = [.. bar.Edges.OfType<GroundEdge>().Where(e => e.MatchesTaxiway(BarTaxiway)).OrderByDescending(e => e.DistanceNm)];
        Assert.True(
            barEdges.Count >= 2,
            $"KOAK node {bar.Id} (runway {bar.RunwayId}) has {barEdges.Count} {BarTaxiway} edges; edges: {Describe(bar)}"
        );
        GroundNode behind = barEdges[0].OtherNode(bar);
        GroundNode pastTheBar = barEdges[^1].OtherNode(bar);
        double bearingDeg = GeoMath.BearingTo(behind.Position, bar.Position);
        var backDeg = new TrueHeading((bearingDeg + 180.0) % 360.0);

        AircraftCategory category = AircraftCategorization.Categorize(type);
        double rollKts = RollKts(category);
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(category);
        double taxiRate = CategoryPerformance.TaxiDecelRate(category);
        double alongFt = StoppingDistanceFt(rollKts, firmRate) * ArmedStopFraction;
        AircraftState aircraft = PlaceRolling(ground, type, (behind.Position, bearingDeg), BarTaxiway, rollKts);
        Send(ground, aircraft, $"TAXI #{behind.Id} #{pastTheBar.Id}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException($"{type} has no taxi route");
        ground.Engine.TickOneSecond();
        aircraft.Position = GeoMath.ProjectPoint(bar.Position, backDeg, (alongFt + (AircraftLength.ResolveFt(type) / 2.0)) / GeoMath.FeetPerNm);

        HoldShortPoint barPoint = HoldShortAt(route, bar);
        LatLon paintedStop = new(barPoint.Latitude!.Value, barPoint.Longitude!.Value);

        // A standalone HS flags the bar at dispatch; a RES HS arms it unflagged and the phase's re-aim flags it on its first
        // tick. Either way, one tick in, the phase has slid the stop forward: the navigator has to be aimed at that point,
        // not at the painted bar it can no longer reach.
        foreach (string earlier in commands.SkipLast(1))
        {
            Send(ground, aircraft, earlier);
        }

        CommandResult issued = Send(ground, aircraft, commands[^1]);
        ground.Engine.TickOneSecond();
        output.WriteLine(
            $"{type} at {rollKts:F1} kt {alongFt:F1} ft from the painted stop, firm stop {StoppingDistanceFt(rollKts, firmRate):F1} ft; "
                + $"{issued.Success} — {issued.Message}"
        );

        return new HoldShortCase
        {
            Ground = ground,
            Aircraft = aircraft,
            Route = route,
            Bar = bar,
            BarBearingDeg = bearingDeg,
            PaintedStop = paintedStop,
            Issued = issued,
            IssuedAt = aircraft.Position,
        };
    }

    /// <summary>How far (ft) the navigator may be aimed from the stop the phase moved the bar to.</summary>
    private const double AimedAtTheStopFt = 5.0;

    /// <summary>A hold-short case ready to be watched: the engine, the aircraft, the route, the bar it was issued on and
    /// the pose and painted stop the run is measured from.</summary>
    private sealed record HoldShortCase
    {
        public required SfoGround Ground { get; init; }
        public required AircraftState Aircraft { get; init; }
        public required TaxiRoute Route { get; init; }
        public required GroundNode Bar { get; init; }
        public required double BarBearingDeg { get; init; }
        public required LatLon PaintedStop { get; init; }
        public required CommandResult Issued { get; init; }
        public required LatLon IssuedAt { get; init; }
    }

    /// <summary>The distance (ft) between two positions.</summary>
    private static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>
    /// The point the taxi phase's navigator is aimed at, from the phase's own snapshot
    /// (<see cref="TaxiingPhaseDto.Navigator"/>), which is where <see cref="TaxiingPhase"/>'s re-aim writes.
    /// </summary>
    private static LatLon AimPoint(AircraftState aircraft)
    {
        TaxiingPhase phase =
            aircraft.Phases?.CurrentPhase as TaxiingPhase ?? throw new InvalidOperationException($"{aircraft.Callsign} is not taxiing");
        GroundNavigatorDto navigator =
            (phase.ToSnapshot() as TaxiingPhaseDto)?.Navigator
            ?? throw new InvalidOperationException($"{aircraft.Callsign}'s taxi snapshot carries no navigator");
        return new LatLon(navigator.TargetLat, navigator.TargetLon);
    }

    /// <summary>
    /// A piston HOLD met in the stretch before a cleared runway crossing is judged against the stopping distance at the
    /// rate that HOLD brakes at — 4.5 kt/s for a piston, not its 2 kt/s taxi rate — so a painted stop the HOLD's own rate
    /// would not reach does not pin the HOLD onto the crossing's braking curve. The control at a distance both rates
    /// reach shows the pin does apply there.
    /// </summary>
    [Fact]
    public void Hold_BeforeAClearedCrossing_IsJudgedAtTheHoldBrakeRate()
    {
        const string Type = "C172";
        AircraftCategory category = AircraftCategorization.Categorize(Type);
        double rollKts = RollKts(category);
        double holdStopFt = StoppingDistanceFt(rollKts, HoldBrakeRateKtsPerSec);
        double taxiStopFt = StoppingDistanceFt(rollKts, CategoryPerformance.TaxiDecelRate(category));
        Assert.True(holdStopFt < taxiStopFt, $"the piston's {holdStopFt:F1} ft hold-rate stop is not inside its {taxiStopFt:F1} ft taxi-rate one");

        CrossingHoldRun? beyondBoth = HoldBeforeACrossing(Type, (holdStopFt + taxiStopFt) / 2.0);
        CrossingHoldRun? insideBoth = HoldBeforeACrossing(Type, holdStopFt * TncHoldControlFraction);
        if ((beyondBoth is null) || (insideBoth is null))
        {
            output.WriteLine($"the {Type} crossing fixture is unavailable: the case is skipped");
            return;
        }

        output.WriteLine($"outside the hold rate's stop: {beyondBoth}; inside it: {insideBoth}");
        Assert.NotEqual(HoldBrakeRateKtsPerSec, beyondBoth.PinnedRate);
        Assert.Equal(HoldBrakeRateKtsPerSec, insideBoth.PinnedRate);
    }

    /// <summary>Where the control HOLD is placed, as a fraction of the hold rate's own stopping distance.</summary>
    private const double TncHoldControlFraction = 0.5;

    /// <summary>The rate (kt/s) a piston HOLD on the runway brakes at (<c>CrossingRunwayPhase.HoldBrakeRateKtsPerSec</c>).</summary>
    private const double HoldBrakeRateKtsPerSec = 4.5;

    /// <summary>
    /// What a HOLD given before a cleared crossing left the aircraft pinned to: where it was issued, the deceleration
    /// rate its HOLD pinned (null when it pinned nothing, which is the answer a crossing out of the HOLD's own rate's
    /// reach leaves) and the runway end it was read back as holding on.
    /// </summary>
    private sealed record CrossingHoldRun(double AlongFt, double? PinnedRate, string? HeldOnRunwayEnd)
    {
        public override string ToString() =>
            $"along {AlongFt:F1} ft: pinned {(PinnedRate is { } rate ? $"{rate:F1} kt/s" : "no rate")}, held on {HeldOnRunwayEnd ?? "no runway"}";
    }

    /// <summary>
    /// A <paramref name="type"/> placed <paramref name="alongFt"/> from the painted stop of a KOAK runway 33 bar whose
    /// crossing is cleared, given <c>HOLD</c> and ticked once. Null when the layout is unavailable — distinct from a run
    /// whose <see cref="CrossingHoldRun.PinnedRate"/> is null, which is the HOLD pinning nothing of its own.
    /// </summary>
    private CrossingHoldRun? HoldBeforeACrossing(string type, double alongFt)
    {
        if (BuildOak() is not { } ground)
        {
            return null;
        }

        AirportGroundLayout layout = ground.Layout;
        (GroundNode bar, GroundEdge approach, GroundNode behind, GroundNode wellPast) =
            FirstBarACrossingReaches(layout)
            ?? throw new InvalidOperationException($"no KOAK runway {BarRunway} bar lies on a taxiway that crosses to a node past it");
        double bearingDeg = GeoMath.BearingTo(behind.Position, bar.Position);
        AircraftCategory category = AircraftCategorization.Categorize(type);
        double rollKts = RollKts(category);
        AircraftState aircraft = PlaceRolling(ground, type, (behind.Position, bearingDeg), approach.TaxiwayName, rollKts);
        Send(ground, aircraft, $"TAXI #{behind.Id} #{wellPast.Id} HS {BarRunway}");
        TrySend(ground, aircraft, "CROSS");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException($"{type} has no taxi route");
        aircraft.Position = GeoMath.ProjectPoint(
            bar.Position,
            new TrueHeading((bearingDeg + 180.0) % 360.0),
            (alongFt + (AircraftLength.ResolveFt(type) / 2.0)) / GeoMath.FeetPerNm
        );
        HoldShortPoint barPoint = HoldShortAt(route, bar);
        double reachedFt = TaxiingPhase.AlongRouteDistanceToHoldShortFt(layout, route, aircraft.Position, barPoint);
        output.WriteLine(
            $"{type} at {rollKts:F1} kt: along-route {reachedFt:F1} ft of the hold's "
                + $"{StoppingDistanceFt(rollKts, HoldBrakeRateKtsPerSec):F1} ft and taxi's "
                + $"{StoppingDistanceFt(rollKts, CategoryPerformance.TaxiDecelRate(category)):F1} ft stops"
        );
        Send(ground, aircraft, "HOLD");
        ground.Engine.TickOneSecond();
        return new CrossingHoldRun(reachedFt, aircraft.Targets.DesiredDecelRate, HeldOnRunwayEnd(aircraft));
    }

    /// <summary>
    /// The runway end a HOLD given now is read back as holding on, or null when it is not one
    /// (<see cref="TaxiingPhase.HeldOnRunwayEnd"/>).
    /// </summary>
    private static string? HeldOnRunwayEnd(AircraftState aircraft) =>
        (aircraft.Phases?.CurrentPhase as TaxiingPhase)?.HeldOnRunwayEnd(aircraft, ActiveRunways.Empty);

    /// <summary>
    /// How far (ft) from the bar's node the makeable case is issued: the painted stop at the middle of the window
    /// between the firm and the taxi stopping distance, which for a runway bar sits half the aircraft back from it.
    /// </summary>
    private static double BetweenTheTwoStops(double firmStopFt, double taxiStopFt, double halfLengthFt) =>
        ((firmStopFt + taxiStopFt) / 2.0) + halfLengthFt;

    /// <summary>
    /// How far (ft) from the bar's node the unable case is issued: inside the firm-stop distance of the painted stop,
    /// which the runway bar's half-length setback sets back from the node.
    /// </summary>
    private static double InsideTheFirmStop(double firmStopFt, double taxiStopFt, double halfLengthFt) =>
        (firmStopFt * InsideTheFirmStopFraction) + halfLengthFt;

    /// <summary>How far into the firm-rate stopping distance the unable case's bar sits.</summary>
    private const double InsideTheFirmStopFraction = 0.85;

    /// <summary>How far (kts/s) a measured deceleration may differ from the rate published for it.</summary>
    private const double DecelToleranceKtsPerSec = 0.2;

    /// <summary>
    /// How far (kts) above the firm rate the speed on the tick before the hold may be. The hold on a bar whose stop lies on
    /// its own segment is not armed by speed at all: the navigator is aimed at the stop, and the phase's arrival there
    /// (<c>TaxiingPhase.ArriveAtNode</c>) takes the hold and zeroes whatever speed the braking curve has left. Measured
    /// as the speed on the whole-second tick before rest on the placed cases: made firmly, a B738 2.09 kt, a C208 3.40 kt
    /// and a C172 2.56 kt; unable, where the stop is moved to the sub-ticked firm stop and the aircraft brakes onto it at the
    /// firm rate from the sub-tick it moves, 3.39, 1.52 and 2.89 kt. The bound is one second of firm braking above the
    /// crawl a start-node hold arms at (<see cref="TaxiingPhase.StartNodeHoldArmSpeedKts"/>) plus one sub-tick of firm
    /// braking: loose enough for those snaps, tight enough that a hold that stops the aircraft dead from taxi speed still
    /// fails — 24 kt against this bound's 12.4 kt for a jet.
    /// </summary>
    private static double HoldArmToleranceKts(double firmRate) =>
        TaxiingPhase.StartNodeHoldArmSpeedKts + (firmRate / SimulationEngine.PhysicsSubTickRate);

    /// <summary>How far (ft) past the bar's marking the nose may come to at a stop, for rounding.</summary>
    private const double NosePastBarToleranceFt = 2.0;

    /// <summary>How far (ft) a bar's moved stop must sit from its painted one to count as moved.</summary>
    private const double StopMovedToleranceFt = 1.0;

    /// <summary>Shortest approach edge worth placing on, so the bar can be reached at taxi speed.</summary>
    private const double MinApproachFt = 150.0;

    /// <summary>Longest hop (ft) from a junction to the bar past it that still counts as "the next node on the route".</summary>
    private const double MaxBarHopFt = 40.0;

    /// <summary>
    /// Shortest fillet arc (ft) worth building the across-a-fillet fixture on. A minimum only: the arc it picks may run
    /// past the firm stop, so the node clamp need not bind there (<see cref="HoldShort_FirmStopPastTheBarNode_IsClampedAtTheNode"/>
    /// is the case where it does).
    /// </summary>
    private const double MinArcApproachFt = 20.0;

    /// <summary>How long (s) the aircraft is watched for after the hold short.</summary>
    private const int StopWatchSeconds = 120;

    /// <summary>How many ticks' worth of taxiing behind its hold-short window the rolling case starts.</summary>
    private const double RollUpStartTicks = 2.0;

    /// <summary>
    /// The along-route distance (ft) at which the rolling case's <c>HS</c> is issued, from the speed it is rolling at
    /// then: the middle of the window between the firm and the taxi stopping distance when <paramref name="madeFirmly"/>,
    /// else <see cref="RollingInsideTheFirmStopFraction"/> of the firm stop — inside it, so only the firm rate reaches the
    /// bar, yet far enough that the aircraft has a second of braking left to read that rate off before its stop.
    /// </summary>
    private static double RollUpTargetFt(double speedKts, bool madeFirmly, double firmRate, double taxiRate) =>
        madeFirmly
            ? (StoppingDistanceFt(speedKts, firmRate) + StoppingDistanceFt(speedKts, taxiRate)) / 2.0
            : StoppingDistanceFt(speedKts, firmRate) * RollingInsideTheFirmStopFraction;

    /// <summary>How far into the firm stop the rolling unable case is issued: inside it, with a second of braking to spare.</summary>
    private const double RollingInsideTheFirmStopFraction = 0.7;

    /// <summary>The along-route distance (ft) from <paramref name="aircraft"/> to <paramref name="bar"/>'s painted stop.</summary>
    private static double AlongFtToStop(AirportGroundLayout layout, TaxiRoute route, AircraftState aircraft, GroundNode bar) =>
        TaxiingPhase.AlongRouteDistanceToHoldShortFt(layout, route, aircraft.Position, HoldShortAt(route, bar));

    /// <summary>The ground speed (kts) under which the aircraft counts as stopped.</summary>
    private const double StillKts = 0.05;

    /// <summary>The ground speed (kts) below which a tick's speed drop is the hold being taken, not braking.</summary>
    private const double StopFloorKts = 1.0;

    /// <summary>What the aircraft did after the hold short was issued.</summary>
    private sealed record HoldShortRun(
        string Type,
        string Message,
        bool Unable,
        double PeakDecelKtsPerSec,
        double NosePastBarFt,
        double SpeedBeforeHoldKts,
        double StopMovedFt,
        double RolledFt,
        double MovedStopFt,
        double FirmStopFt,
        double StopPastNodeFt,
        double StopOffRouteFt,
        double RestToNodeFt,
        double StopToNodeFt,
        double RestPastStopFt,
        double FirmRate,
        double TaxiRate
    )
    {
        public override string ToString() =>
            $"{Type}: {Message}; peak decel {PeakDecelKtsPerSec:F2} kt/s (firm {FirmRate:F1}, taxi {TaxiRate:F1}); "
            + $"nose {NosePastBarFt:F1} ft past the bar; {SpeedBeforeHoldKts:F2} kt before the stop; stop moved {StopMovedFt:F1} ft, "
            + $"{StopPastNodeFt:F1} ft from the node, {StopOffRouteFt:F2} ft off the route; "
            + $"rolled {RolledFt:F1} ft of the {MovedStopFt:F1} ft moved stop; at rest {RestPastStopFt:F1} ft past the stop";
    }

    /// <summary>
    /// Places a <paramref name="type"/> on the runway 33 bar's KOAK taxiway-C edge at the along-route distance to the
    /// painted stop <paramref name="targetNodeFt"/> picks, cleared to the bar, issues <c>HS 33</c> and watches it stop:
    /// the answer, the hardest braking and where the nose ended up. Null when the layout or the bar is unavailable.
    /// </summary>
    private HoldShortRun? PlaceAndHoldShort(string type, Func<double, double, double, double> targetNodeFt) =>
        PlaceAndIssueHoldShort(type, targetNodeFt) is { } placed ? Watch(placed) : null;

    /// <summary>
    /// <see cref="PlaceAndHoldShort"/>'s set-up: the aircraft placed and handed <c>HS 33</c>, not yet ticked. Null when
    /// the layout or the bar is unavailable.
    /// </summary>
    private HoldShortCase? PlaceAndIssueHoldShort(string type, Func<double, double, double, double> targetNodeFt)
    {
        if (BuildOak() is not { } ground)
        {
            return null;
        }

        AirportGroundLayout layout = ground.Layout;
        GroundNode bar =
            TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, BarRunway, BarTaxiway)
            ?? throw new InvalidOperationException($"KOAK has no runway {BarRunway} hold-short on taxiway {BarTaxiway}");
        List<GroundEdge> barEdges = [.. bar.Edges.OfType<GroundEdge>().Where(e => e.MatchesTaxiway(BarTaxiway)).OrderByDescending(e => e.DistanceNm)];
        Assert.True(
            barEdges.Count >= 2,
            $"KOAK node {bar.Id} (runway {bar.RunwayId}) has {barEdges.Count} {BarTaxiway} edges; edges: {Describe(bar)}"
        );
        GroundEdge edge = barEdges[0];
        GroundNode behind = edge.OtherNode(bar);
        GroundNode pastTheBar = barEdges[^1].OtherNode(bar);
        double bearingDeg = GeoMath.BearingTo(behind.Position, bar.Position);
        var backDeg = new TrueHeading((bearingDeg + 180.0) % 360.0);

        AircraftCategory category = AircraftCategorization.Categorize(type);
        double rollKts = RollKts(category);
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(category);
        double taxiRate = CategoryPerformance.TaxiDecelRate(category);
        double halfLengthFt = AircraftLength.ResolveFt(type) / 2.0;
        double approachFt = targetNodeFt(StoppingDistanceFt(rollKts, firmRate), StoppingDistanceFt(rollKts, taxiRate), halfLengthFt);

        // Cleared from the node behind the bar on to the node past it, so the bar's node is on the route but is not the
        // route's own start — a bar there is bound by HS 33 below, which is what the case is issued as.
        AircraftState aircraft = PlaceRolling(ground, type, (behind.Position, bearingDeg), BarTaxiway, rollKts);
        Send(ground, aircraft, $"TAXI #{behind.Id} #{pastTheBar.Id}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException($"{type} has no taxi route");

        // Rolled on to the distance the case is issued at, without ticking: the navigator has planned nothing for the bar.
        aircraft.Position = GeoMath.ProjectPoint(bar.Position, backDeg, approachFt / GeoMath.FeetPerNm);
        HoldShortPoint barPoint = HoldShortAt(route, bar);
        LatLon paintedStop = new(barPoint.Latitude!.Value, barPoint.Longitude!.Value);
        CommandResult issued = ground.Engine.SendCommand(aircraft.Callsign, $"HS {BarRunway}");
        barPoint = HoldShortAt(route, bar);
        output.WriteLine(
            $"{type} at {rollKts:F1} kt on {edge.TaxiwayName} edge {behind.Id}-{bar.Id} "
                + $"({edge.DistanceNm * GeoMath.FeetPerNm:F0} ft); along-route to the stop "
                + $"{TaxiingPhase.AlongRouteDistanceToHoldShortFt(layout, route, aircraft.Position, barPoint):F1} ft; firm stop "
                + $"{StoppingDistanceFt(rollKts, firmRate):F1}, taxi stop {StoppingDistanceFt(rollKts, taxiRate):F1} ft; "
                + $"{issued.Success} — {issued.Message}"
        );

        return new HoldShortCase
        {
            Ground = ground,
            Aircraft = aircraft,
            Route = route,
            Bar = bar,
            BarBearingDeg = bearingDeg,
            PaintedStop = paintedStop,
            Issued = issued,
            IssuedAt = aircraft.Position,
        };
    }

    /// <summary>
    /// Runs the case a physics sub-tick at a time until the aircraft is at rest, and returns the speed (kts) it had on the
    /// sub-tick before: what the hold zeroed when it was taken, or the last sub-tick of braking when physics brought it to
    /// rest itself. The phase ticks before every sub-tick's physics, so a hold taken at speed shows here, where a
    /// whole-second sample would hide it inside the second.
    /// </summary>
    private double SpeedOnArrivalKts(HoldShortCase c)
    {
        const double SubTickSeconds = 1.0 / SimulationEngine.PhysicsSubTickRate;
        double previousKts = c.Aircraft.GroundSpeed;
        List<string> trace = [];
        for (int sub = 0; (sub < StopWatchSeconds * SimulationEngine.PhysicsSubTickRate) && (c.Aircraft.GroundSpeed > StillKts); sub++)
        {
            previousKts = c.Aircraft.GroundSpeed;
            trace.Add($"{previousKts:F2}@{FeetBetween(c.Aircraft.Position, StopPosition(c.Route, c.Bar)):F1}ft");
            c.Ground.Engine.TickPhysics(SubTickSeconds);
            if (((sub + 1) % SimulationEngine.PhysicsSubTickRate) == 0)
            {
                c.Ground.Engine.TickPostPhysics();
            }
        }

        output.WriteLine($"sub-tick speeds: {string.Join(" ", trace)}");
        return previousKts;
    }

    /// <summary>
    /// A <paramref name="type"/> cleared from the edge before a junction on to the bar a short hop past it, issued
    /// <c>HS 33</c> inside the firm stopping distance of its painted stop: the bar's node is a whole segment past the one
    /// in progress. Null when the layout or the bar is unavailable.
    /// </summary>
    private HoldShortRun? HoldShortBeyondTheJunction(string type)
    {
        if (PlaceBeyondTheJunction(type, (_, _) => true) is not { } placed)
        {
            return null;
        }

        HoldShortRun beyond = Watch(placed);
        Assert.True(
            beyond.StopPastNodeFt <= StopPastNodeToleranceFt,
            $"{type}'s moved stop was placed {beyond.StopPastNodeFt:F1} ft past the bar's node {placed.Bar.Id}, toward the runway"
        );
        Assert.True(
            beyond.StopOffRouteFt <= StopOffRouteToleranceFt,
            $"{type}'s moved stop lies {beyond.StopOffRouteFt:F2} ft off the route, past {StopOffRouteToleranceFt:F1} ft"
        );
        return beyond;
    }

    /// <summary>
    /// <see cref="HoldShortBeyondTheJunction"/>'s set-up: the aircraft placed on the fillet, rolled on a physics sub-tick at
    /// a time until <paramref name="issueWhen"/> holds, and handed the hold short there, not yet ticked after it. Null when
    /// the layout or the bar is unavailable.
    /// </summary>
    private HoldShortCase? PlaceBeyondTheJunction(string type, Func<AircraftState, TaxiRoute, bool> issueWhen)
    {
        // SFO, not KOAK: no KOAK runway bar sits a short hop past a junction a fillet arc leads to, and it is the arc that
        // makes the projection's measurement matter.
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.All) is not { } ground)
        {
            return null;
        }

        AirportGroundLayout layout = ground.Layout;
        (GroundNode bar, GroundNode junction, IGroundEdge approach) =
            FirstBarPastAJunction(layout)
            ?? throw new InvalidOperationException($"no SFO runway bar sits a short hop past a junction a long fillet arc leads to");
        GroundNode approachFar = approach.OtherNode(junction);
        string target = bar.RunwayId?.End1 ?? throw new InvalidOperationException($"node {bar.Id} holds short of no runway");
        double hopFt = GeoMath.DistanceNm(junction.Position, bar.Position) * GeoMath.FeetPerNm;
        double bearingDeg = GeoMath.BearingTo(approachFar.Position, junction.Position);

        AircraftCategory category = AircraftCategorization.Categorize(type);
        // At the category's own taxi cap, not RollKts's fixed 24 kt: the fillet is far longer than the settled roll
        // speed's firm stop, so only the cap's reaches past it and leaves the bar unmakeable.
        double rollKts = CategoryPerformance.TaxiSpeed(category);
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(category);
        double taxiRate = CategoryPerformance.TaxiDecelRate(category);

        // Rolled from the arc's own far node: the route drives the fillet, and the bar's node is one segment past the
        // junction the aircraft is heading for, so nothing re-aims at the bar.
        // Pointed along the arc's own departure tangent, not the chord to the junction: an aircraft placed off the arc's
        // heading first drives an entry alignment, whose travel re-projected onto the arc lands about 6 ft further along it
        // than it rolled, and that distance would be read as an overrun of the moved stop. Placed rather than ticked in: an
        // aircraft taxied up to the fillet brakes at the taxi rate for the arc's corner speed and meets the arc start near it,
        // and from there the bar is never inside its firm stopping distance, so no natural approach makes it unable.
        double tangentDeg = approach.Directed(approachFar, junction).DepartureBearing;
        AircraftState aircraft = PlaceRolling(ground, type, (approachFar.Position, tangentDeg), approach.TaxiwayName, rollKts);
        Send(ground, aircraft, $"TAXI #{approachFar.Id} #{bar.Id}");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException($"{type} has no taxi route");
        HoldShortPoint barPoint = HoldShortAt(route, bar);
        LatLon paintedStop = new(barPoint.Latitude!.Value, barPoint.Longitude!.Value);
        RollOnUntil(ground, aircraft, route, issueWhen);
        CommandResult issued = ground.Engine.SendCommand(aircraft.Callsign, $"HS {target}");
        barPoint = HoldShortAt(route, bar);
        output.WriteLine(
            $"{type} at {aircraft.GroundSpeed:F1} kt {FeetBetween(approachFar.Position, aircraft.Position):F1} ft from node {approachFar.Id} "
                + $"on {approach.TaxiwayName} ({approach.DistanceNm * GeoMath.FeetPerNm:F0} ft "
                + $"fillet to junction {junction.Id}) with bar {bar.Id} {hopFt:F1} ft past it; along-route to the stop "
                + $"{TaxiingPhase.AlongRouteDistanceToHoldShortFt(layout, route, aircraft.Position, barPoint):F1} ft; firm stop "
                + $"{StoppingDistanceFt(rollKts, firmRate):F1} ft; {issued.Success} — {issued.Message}"
        );
        output.WriteLine($"route: {string.Join(", ", route.Segments.Skip(route.CurrentSegmentIndex).Select(Describe))}");

        return new HoldShortCase
        {
            Ground = ground,
            Aircraft = aircraft,
            Route = route,
            Bar = bar,
            BarBearingDeg = bearingDeg,
            PaintedStop = paintedStop,
            Issued = issued,
            IssuedAt = aircraft.Position,
        };
    }

    /// <summary>
    /// Ticks the engine a physics sub-tick at a time (the post-physics pass once a second, as the engine does) until
    /// <paramref name="ready"/> holds, at most <see cref="RollOnLimitSeconds"/>; zero sub-ticks when it already does.
    /// </summary>
    private static void RollOnUntil(SfoGround ground, AircraftState aircraft, TaxiRoute route, Func<AircraftState, TaxiRoute, bool> ready)
    {
        const double SubTickSeconds = 1.0 / SimulationEngine.PhysicsSubTickRate;
        for (int sub = 0; !ready(aircraft, route); sub++)
        {
            if (sub >= RollOnLimitSeconds * SimulationEngine.PhysicsSubTickRate)
            {
                throw new InvalidOperationException($"{aircraft.Callsign} never reached the point the hold short is issued at");
            }

            ground.Engine.TickPhysics(SubTickSeconds);
            if (((sub + 1) % SimulationEngine.PhysicsSubTickRate) == 0)
            {
                ground.Engine.TickPostPhysics();
            }
        }
    }

    /// <summary>How long (s) the aircraft may roll on before the hold short is issued.</summary>
    private const int RollOnLimitSeconds = 10;

    /// <summary>How far (ft) past its moved stop the aircraft may come to rest: the reviewer's route tolerance.</summary>
    private const double RestsAtTheStopFt = 3.0;

    /// <summary>How far (ft) short of its moved stop the aircraft may come to rest: the navigator's
    /// <c>SetBackStopMarginFt</c> plus the distance the crawl before the hold covers.</summary>
    private const double StopsShortOfTheStopFt = 8.0;

    /// <summary>How far (ft) past the bar's node a moved stop may be placed before it counts as carried past it.</summary>
    private const double StopPastNodeToleranceFt = 0.5;

    /// <summary>How far (ft) off the route a moved stop may lie: its line, or its fillet arc.</summary>
    private const double StopOffRouteToleranceFt = 1.0;

    /// <summary>A route segment for a failure message: its taxiway and whether it is a straight line or a fillet arc.</summary>
    private static string Describe(TaxiRouteSegment seg) => $"{seg.TaxiwayName}{(seg.Edge.Edge is GroundArc ? "/arc" : "/line")}";

    /// <summary>
    /// A <paramref name="type"/> cleared across runway 33 to a node past its far side, the crossing pre-cleared with
    /// <c>CROSS</c>, rolled up to the hold-short window and then issued <c>HS 33</c>: the made-firmly window when
    /// <paramref name="madeFirmly"/>, the inside-the-firm-stop one otherwise. Null when the layout is unavailable.
    /// </summary>
    private HoldShortRun? RollAcrossTheRunway(string type, bool madeFirmly)
    {
        AircraftCategory category = AircraftCategorization.Categorize(type);
        double rollKts = RollKts(category);
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(category);
        double taxiRate = CategoryPerformance.TaxiDecelRate(category);
        RollUp? rollUp = SolveRollUp(type, madeFirmly, firmRate, taxiRate, rollKts);
        if (rollUp is null)
        {
            return null;
        }

        SfoGround ground = rollUp.Ground;
        AircraftState aircraft = rollUp.Aircraft;
        TaxiRoute route = rollUp.Route;
        GroundNode bar = rollUp.Bar;
        HoldShortPoint barPoint = HoldShortAt(route, bar);
        LatLon paintedStop = new(barPoint.Latitude!.Value, barPoint.Longitude!.Value);
        CommandResult issued = ground.Engine.SendCommand(aircraft.Callsign, $"HS {BarRunway}");
        output.WriteLine(
            $"{type} rolling at {aircraft.GroundSpeed:F1} kt {rollUp.ReachedFt:F1} ft from the stop of the window's {rollUp.TargetFt:F1} ft "
                + $"(firm {StoppingDistanceFt(rollKts, firmRate):F1}, taxi {StoppingDistanceFt(rollKts, taxiRate):F1} ft); "
                + $"{issued.Success} — {issued.Message}"
        );
        return Watch(
            new HoldShortCase
            {
                Ground = ground,
                Aircraft = aircraft,
                Route = route,
                Bar = bar,
                BarBearingDeg = rollUp.BearingDeg,
                PaintedStop = paintedStop,
                Issued = issued,
                IssuedAt = aircraft.Position,
            }
        );
    }

    /// <summary>How many passes the roll-up's placement is solved over, and how close to the window it must land.</summary>
    private const int WindowSolvePasses = 4;
    private const double WindowSolveToleranceFt = 2.0;

    /// <summary>A rolling case as the roll-up left it: the engine, the aircraft, its route and bar, and where the aircraft
    /// stood when the hold short would have been issued.</summary>
    private sealed record RollUp(
        SfoGround Ground,
        AircraftState Aircraft,
        TaxiRoute Route,
        GroundNode Bar,
        double BearingDeg,
        double ReachedFt,
        double TargetFt
    );

    /// <summary>
    /// <see cref="RollUpOnce"/> solved for the window: the taxi's first tick has no target to brake for, so it sheds a
    /// little speed and moves the window under a placement aimed from the taxiing speed; the miss corrects the placement
    /// and the pass repeats on a fresh engine.
    /// </summary>
    private RollUp? SolveRollUp(string type, bool madeFirmly, double firmRate, double taxiRate, double rollKts)
    {
        double placementFt = RollUpTargetFt(rollKts, madeFirmly, firmRate, taxiRate) + (RollUpStartTicks * rollKts * GeoMath.FeetPerNm / 3600.0);
        RollUp? run = null;
        for (int pass = 0; pass < WindowSolvePasses; pass++)
        {
            run = RollUpOnce(type, placementFt, rollKts, madeFirmly, firmRate, taxiRate);
            if (run is null)
            {
                return null;
            }

            double missFt = run.ReachedFt - run.TargetFt;
            if (Math.Abs(missFt) <= WindowSolveToleranceFt)
            {
                break;
            }

            placementFt -= missFt;
        }

        return run;
    }

    /// <summary>
    /// A rolling case's aircraft cleared across the bar's runway to a node past its far side with the crossing
    /// pre-cleared, placed <paramref name="placementFt"/> from the bar's node and ticked one second, so the hold short
    /// reaches an aircraft that has taxied there: where it then stands along the route from the stop, and the window its
    /// speed gives it. Null when the layout is unavailable.
    /// </summary>
    private RollUp? RollUpOnce(string type, double placementFt, double rollKts, bool madeFirmly, double firmRate, double taxiRate)
    {
        if (BuildOak() is not { } ground)
        {
            return null;
        }

        AirportGroundLayout layout = ground.Layout;
        (GroundNode bar, GroundEdge approach, GroundNode behind, GroundNode wellPast) =
            FirstBarACrossingReaches(layout)
            ?? throw new InvalidOperationException($"no KOAK runway {BarRunway} bar lies on a taxiway that crosses to a node past it");
        double bearingDeg = GeoMath.BearingTo(behind.Position, bar.Position);
        AircraftState aircraft = PlaceRolling(ground, type, (behind.Position, bearingDeg), approach.TaxiwayName, rollKts);
        Send(ground, aircraft, $"TAXI #{behind.Id} #{wellPast.Id}");
        TrySend(ground, aircraft, "CROSS");
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException($"{type} has no taxi route");
        aircraft.Position = GeoMath.ProjectPoint(
            bar.Position,
            new TrueHeading((bearingDeg + 180.0) % 360.0),
            (placementFt + (AircraftLength.ResolveFt(type) / 2.0)) / GeoMath.FeetPerNm
        );
        ground.Engine.TickOneSecond();
        return new RollUp(
            ground,
            aircraft,
            route,
            bar,
            bearingDeg,
            AlongFtToStop(layout, route, aircraft, bar),
            RollUpTargetFt(aircraft.GroundSpeed, madeFirmly, firmRate, taxiRate)
        );
    }

    /// <summary>
    /// Watches the aircraft to a stop and reports the answer, the hardest braking, where the nose ended up, the speed on
    /// the tick before it stopped and how far the bar's stop was moved from where the annotator painted it.
    /// </summary>
    private HoldShortRun Watch(HoldShortCase c)
    {
        SfoGround ground = c.Ground;
        AircraftState aircraft = c.Aircraft;
        TaxiRoute route = c.Route;
        GroundNode bar = c.Bar;
        double bearingDeg = c.BarBearingDeg;
        LatLon paintedStop = c.PaintedStop;
        CommandResult issued = c.Issued;
        LatLon issuedAt = c.IssuedAt;
        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(category);
        double taxiRate = CategoryPerformance.TaxiDecelRate(category);
        bool unable = route.HoldShortPoints.Any(h => h.Unable);
        double firmStopFt = StoppingDistanceFt(aircraft.GroundSpeed, firmRate);
        int barSegmentIndex = BarSegmentIndex(route, bar);
        double restToNodeFt = double.NaN;
        double peakDecelKts = 0.0;
        double previousKts = aircraft.GroundSpeed;
        double speedBeforeHoldKts = previousKts;
        double nosePastFt = 0.0;
        List<double> speeds = [aircraft.GroundSpeed];
        int second = 0;
        while ((second++ < StopWatchSeconds) && (aircraft.GroundSpeed > StillKts))
        {
            ground.Engine.TickOneSecond();
            // The tick that takes the hold drops the speed to zero in one go; the braking measured here is the one before it.
            if (aircraft.GroundSpeed > StopFloorKts)
            {
                peakDecelKts = Math.Max(peakDecelKts, previousKts - aircraft.GroundSpeed);
            }
            else
            {
                speedBeforeHoldKts = previousKts;
            }

            previousKts = aircraft.GroundSpeed;
            speeds.Add(aircraft.GroundSpeed);
            nosePastFt = Math.Max(nosePastFt, PastTheBarFt(aircraft, bar, bearingDeg));
            // Read while the aircraft is still on the bar's own segment: a stop at or past the node completes the route, and
            // the remaining-distance walk needs a segment in progress.
            if ((barSegmentIndex >= 0) && (route.CurrentSegmentIndex <= barSegmentIndex))
            {
                restToNodeFt = route.RemainingDistanceFt(aircraft.Position, barSegmentIndex + 1);
            }
        }

        double stopMovedFt = StopMovedFt(route, bar, paintedStop);
        double rolledFt = GeoMath.DistanceNm(issuedAt, aircraft.Position) * GeoMath.FeetPerNm;
        LatLon stop = StopPosition(route, bar);
        double movedStopFt = GeoMath.DistanceNm(issuedAt, stop) * GeoMath.FeetPerNm;
        double stopPastNodeFt = GeoMath.AlongTrackDistanceNm(stop, bar.Position, new TrueHeading(bearingDeg)) * GeoMath.FeetPerNm;
        double stopOffRouteFt = OffRouteFt(route, barSegmentIndex, stop);
        // The route's own measure of the same pair, so a stop placed on a fillet arc is compared with where the aircraft
        // came to rest along the route rather than by the straight line between two points of a curve.
        HoldShortPoint barPoint = HoldShortAt(route, bar);
        double stopToNodeFt = barSegmentIndex >= 0 ? route.HoldShortSetbackNm(barSegmentIndex, barPoint) * GeoMath.FeetPerNm : double.NaN;
        // Read off the aircraft's own heading at rest, independent of how the route measures the stop: positive = past it.
        double restPastStopFt = GeoMath.AlongTrackDistanceNm(aircraft.Position, stop, aircraft.TrueHeading) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"speeds: {string.Join(" ", speeds.Select(s => s.ToString("F2")))}; stop moved {stopMovedFt:F1} ft from the painted "
                + $"{paintedStop}; {movedStopFt:F1} ft ahead of where the hold short arrived (firm stop {firmStopFt:F1} ft); "
                + $"{stopPastNodeFt:F1} ft from the bar's node (positive = past); {stopOffRouteFt:F2} ft off the route; rolled {rolledFt:F1} ft; "
                + $"rest {restToNodeFt:F1} ft from the node, stop {stopToNodeFt:F1} ft"
        );
        return new HoldShortRun(
            aircraft.AircraftType,
            issued.Message ?? string.Empty,
            unable,
            peakDecelKts,
            nosePastFt,
            speedBeforeHoldKts,
            stopMovedFt,
            rolledFt,
            movedStopFt,
            firmStopFt,
            stopPastNodeFt,
            stopOffRouteFt,
            restToNodeFt,
            stopToNodeFt,
            restPastStopFt,
            firmRate,
            taxiRate
        );
    }

    /// <summary>The index of the route's segment ending at <paramref name="bar"/>'s node, or -1 when it ends at none.</summary>
    private static int BarSegmentIndex(TaxiRoute route, GroundNode bar)
    {
        for (int i = 0; i < route.Segments.Count; i++)
        {
            if (route.Segments[i].ToNodeId == bar.Id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The bar's stop position, asserted to have been computed.</summary>
    private static LatLon StopPosition(TaxiRoute route, GroundNode bar) =>
        route.GetHoldShortAt(bar.Id) is { Latitude: { } lat, Longitude: { } lon }
            ? new LatLon(lat, lon)
            : throw new InvalidOperationException($"node {bar.Id}'s hold-short has no computed position");

    /// <summary>How far (ft) <paramref name="stop"/> lies off the route's geometry, over the segments up to the bar's node.</summary>
    private static double OffRouteFt(TaxiRoute route, int barSegmentIndex, LatLon stop)
    {
        double best = double.MaxValue;
        for (int i = 0; i <= Math.Min(barSegmentIndex, route.Segments.Count - 1); i++)
        {
            best = Math.Min(best, OffSegmentFt(route.Segments[i], stop));
        }

        return best;
    }

    /// <summary>How far (ft) <paramref name="point"/> lies off <paramref name="seg"/>'s own line or fillet arc.</summary>
    private static double OffSegmentFt(TaxiRouteSegment seg, LatLon point)
    {
        if (seg.Edge.Edge is not GroundArc arc)
        {
            return GeoMath.DistanceToSegmentFt(
                point.Lat,
                point.Lon,
                seg.Edge.FromNode.Position.Lat,
                seg.Edge.FromNode.Position.Lon,
                seg.Edge.ToNode.Position.Lat,
                seg.Edge.ToNode.Position.Lon
            );
        }

        CubicBezier curve = arc.ToBezier();
        bool forward = arc.Nodes[0].Id == seg.Edge.FromNode.Id;
        double best = double.MaxValue;
        (double Lat, double Lon) previous = curve.Evaluate(forward ? 0.0 : 1.0);
        for (int i = 1; i <= ArcSampleCount; i++)
        {
            double t = forward ? (double)i / ArcSampleCount : 1.0 - ((double)i / ArcSampleCount);
            (double lat, double lon) = curve.Evaluate(t);
            best = Math.Min(best, GeoMath.DistanceToSegmentFt(point.Lat, point.Lon, previous.Lat, previous.Lon, lat, lon));
            previous = (lat, lon);
        }

        return best;
    }

    /// <summary>How finely a fillet arc is sampled to measure a stop's distance from it.</summary>
    private const int ArcSampleCount = 32;

    /// <summary>The bar's stop as it stands now, asserted to be on the route and positioned.</summary>
    private static HoldShortPoint HoldShortAt(TaxiRoute route, GroundNode bar)
    {
        HoldShortPoint? point = route.GetHoldShortAt(bar.Id);
        Assert.True(
            point is not null,
            $"the route carries no hold-short at node {bar.Id} ({bar.RunwayId}); holds: {SfoGroundHarness.FormatActual(route.HoldShortPoints)}"
        );
        Assert.True((point!.Latitude is not null) && (point.Longitude is not null), $"the hold-short at node {bar.Id} has no computed position");
        return point;
    }

    /// <summary>How far (ft) the bar's stop has moved from <paramref name="paintedStop"/>.</summary>
    private static double StopMovedFt(TaxiRoute route, GroundNode bar, LatLon paintedStop)
    {
        if (route.GetHoldShortAt(bar.Id) is not { Latitude: { } lat, Longitude: { } lon })
        {
            return 0.0;
        }

        return GeoMath.DistanceNm(paintedStop, new LatLon(lat, lon)) * GeoMath.FeetPerNm;
    }

    /// <summary>
    /// The speed (kts) a <paramref name="category"/> is rolled at: <see cref="RollSpeedKts"/>, capped at the category's
    /// taxi speed (<see cref="CategoryPerformance.TaxiSpeed"/>) so the navigator has no reason to slow the aircraft itself.
    /// </summary>
    private static double RollKts(AircraftCategory category) => Math.Min(RollSpeedKts, CategoryPerformance.TaxiSpeed(category));

    /// <summary>
    /// The first SFO runway bar (by node id) a short hop past a junction a long fillet arc leads to, so the route to the
    /// bar runs over that arc: a stop projected along the route has to be measured and placed on it, not on the chord
    /// between its ends. With that bar, the junction and the approach arc.
    /// </summary>
    private static (GroundNode Bar, GroundNode Junction, IGroundEdge Approach)? FirstBarPastAJunction(AirportGroundLayout layout)
    {
        foreach (
            GroundNode bar in layout
                .Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is not null))
                .OrderBy(n => n.Id)
        )
        {
            foreach (GroundEdge hop in bar.Edges.OfType<GroundEdge>().OrderBy(e => e.DistanceNm))
            {
                GroundNode junction = hop.OtherNode(bar);
                if ((hop.DistanceNm * GeoMath.FeetPerNm) > MaxBarHopFt)
                {
                    continue;
                }

                IGroundEdge? approach = junction
                    .Edges.Where(e => e is GroundArc)
                    .Where(e => !ReferenceEquals(e.OtherNode(junction), bar) && (e.TaxiwayName.Length > 0))
                    .Where(e => (e.DistanceNm * GeoMath.FeetPerNm) >= MinArcApproachFt)
                    .OrderBy(e => e.DistanceNm)
                    .FirstOrDefault();
                if (approach is not null)
                {
                    return (bar, junction, approach);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The first KOAK runway <see cref="BarRunway"/> bar (by node id) on a long straight taxiway edge with the taxiway
    /// continuing past it to a node past the runway's far-side bar: that bar, the edge leading to it, its near node and
    /// the node well past it. Null when there is none.
    /// </summary>
    private static (GroundNode Bar, GroundEdge Approach, GroundNode Behind, GroundNode WellPast)? FirstBarACrossingReaches(AirportGroundLayout layout)
    {
        foreach (GroundNode bar in BarsOnTheRunway(layout))
        {
            GroundEdge? approach = bar
                .Edges.OfType<GroundEdge>()
                .Where(e => (e.DistanceNm * GeoMath.FeetPerNm) >= MinApproachFt)
                .OrderByDescending(e => e.DistanceNm)
                .FirstOrDefault();
            if (approach is null)
            {
                continue;
            }

            GroundNode behind = approach.OtherNode(bar);
            if (NodePastTheRunway(bar, approach) is { } wellPast)
            {
                return (bar, approach, behind, wellPast);
            }
        }

        return null;
    }

    /// <summary>The KOAK bars of <see cref="BarRunway"/>, by node id.</summary>
    private static IEnumerable<GroundNode> BarsOnTheRunway(AirportGroundLayout layout) =>
        layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } id) && id.Contains(BarRunway))
            .OrderBy(n => n.Id);

    /// <summary>How many nodes the walk across the runway visits looking for the far side's holding position.</summary>
    private const int MaxCrossingWalkNodes = 8;

    /// <summary>How many nodes on past that holding position the route's own end sits, so its stop is not braking the aircraft
    /// before it reaches the hold-short window.</summary>
    private const int NodesPastTheFarBar = 4;

    /// <summary>
    /// <paramref name="bar"/>'s far-side holding position for its runway, reached by taking the straightest edge at each
    /// node away from <paramref name="approach"/>, and then <see cref="NodesPastTheFarBar"/> node on: the end a route
    /// across the runway is drawn to, so the crossing is one the annotator binds. Null when the walk finds no far bar.
    /// </summary>
    private static GroundNode? NodePastTheRunway(GroundNode bar, GroundEdge approach)
    {
        GroundNode at = bar;
        IGroundEdge from = approach;
        double bearingDeg = GeoMath.BearingTo(approach.OtherNode(bar).Position, bar.Position);
        for (int i = 0; i < MaxCrossingWalkNodes; i++)
        {
            GroundNode here = at;
            double inDeg = bearingDeg;
            IGroundEdge? next = here
                .Edges.Where(e => !ReferenceEquals(e, from))
                .MinBy(e => GeoMath.AbsBearingDifference(e.Directed(here, e.OtherNode(here)).DepartureBearing, inDeg));
            if (next is null)
            {
                return null;
            }

            GroundNode to = next.OtherNode(here);
            if ((to.Type == GroundNodeType.RunwayHoldShort) && Equals(to.RunwayId, bar.RunwayId))
            {
                return WalkOn(to, next, NodesPastTheFarBar);
            }

            bearingDeg = next.Directed(here, to).ArrivalBearing;
            from = next;
            at = to;
        }

        return null;
    }

    /// <summary><paramref name="steps"/> nodes on from <paramref name="from"/> along the straightest edge each time.</summary>
    private static GroundNode WalkOn(GroundNode from, IGroundEdge cameFrom, int steps)
    {
        GroundNode at = from;
        IGroundEdge previous = cameFrom;
        for (int i = 0; i < steps; i++)
        {
            GroundNode here = at;
            IGroundEdge? next = here
                .Edges.Where(e => !ReferenceEquals(e, previous))
                .MinBy(e =>
                    GeoMath.AbsBearingDifference(
                        e.Directed(here, e.OtherNode(here)).DepartureBearing,
                        previous.Directed(here, previous.OtherNode(here)).ArrivalBearing
                    )
                );
            if (next is null)
            {
                return at;
            }

            previous = next;
            at = next.OtherNode(here);
        }

        return at;
    }

    /// <summary>A layout node's edges, for a failure message.</summary>
    private static string Describe(GroundNode node) =>
        string.Join(
            ", ",
            node.Edges.Select(e => $"{e.TaxiwayName}/{(e.DistanceNm * GeoMath.FeetPerNm):F0}ft/{(e is GroundEdge ? "straight" : "arc")}")
        );

    /// <summary>
    /// How far (ft) the aircraft's nose — its centre projected half its length ahead — is past the line through the bar
    /// square to its departure.
    /// </summary>
    private static double PastTheBarFt(AircraftState aircraft, GroundNode bar, double bearingDeg)
    {
        LatLon nose = GeoMath.ProjectPoint(
            aircraft.Position,
            aircraft.TrueHeading,
            AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0 / GeoMath.FeetPerNm
        );
        return GeoMath.AlongTrackDistanceNm(nose, bar.Position, new TrueHeading(bearingDeg)) * GeoMath.FeetPerNm;
    }

    /// <summary>How far (ft) an aircraft rolls braking from <paramref name="speedKts"/> to a stop at <paramref name="decelKtsPerSec"/>.</summary>
    private static double StoppingDistanceFt(double speedKts, double decelKtsPerSec) =>
        (speedKts * speedKts) / (2.0 * decelKtsPerSec) * GeoMath.FeetPerNm / 3600.0;

    /// <summary>
    /// Sends <paramref name="command"/> and logs the outcome without asserting it: for a fixture step the case does not
    /// depend on, such as clearing a crossing the drawn route bound only when it crosses.
    /// </summary>
    private void TrySend(SfoGround ground, AircraftState aircraft, string command)
    {
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");
    }

    /// <summary>Sends <paramref name="command"/> and asserts it was accepted.</summary>
    private CommandResult Send(SfoGround ground, AircraftState aircraft, string command)
    {
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine($"{command}: {result.Success} — {result.Message}");
        Assert.True(result.Success, $"'{command}' was refused: {result.Message}");
        return result;
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
        var aircraft = new AircraftState
        {
            Callsign = "N401YA",
            AircraftType = type,
            Position = pose.Position,
            TrueHeading = new TrueHeading(pose.HeadingDeg),
            TrueTrack = new TrueHeading(pose.HeadingDeg),
            Altitude = 6,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, ground.Layout));
        aircraft.IndicatedAirspeed = speedKts;
        aircraft.Ground.Layout = ground.Layout;
        aircraft.Ground.CurrentTaxiway = taxiway;
        ground.Engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    /// <summary>An engine over the committed KOAK layout, as <see cref="SfoGroundHarness.Build"/> builds one over SFO's.</summary>
    private SfoGround? BuildOak()
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if ((TestVnasData.NavigationDb is null) || (groundData.GetLayout("OAK") is not { } layout))
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-firm-rate-hold-short",
                ScenarioName = "KOAK Firm-Rate Hold Short",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
        return new SfoGround(engine, layout);
    }
}
