using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A bare <c>TAXI B</c> (only the taxiway the aircraft is already on, at most <c>EXP</c> / <c>NODEL</c>) names no destination
/// and no further taxiway, so it does nothing: it is refused with a reason, never spoken, and the aircraft stays put, wherever
/// it stands on B. At KOAK, on B at the south 28L/10R bar (facing either way, on it or just short of it, or holding short of
/// 28L), the refusal keeps it from taking the bare taxi as a route along B, across 28L and 28R.
/// </summary>
public class BareTaxiFirstStepHeadingTests(ITestOutputHelper output)
{
    /// <summary>How long an aircraft is run after the refused taxi.</summary>
    private const int HoldingShortTickSeconds = 60;

    /// <summary>
    /// How far from its start a refused aircraft may sit: it starts still, and taxiing anywhere would carry it hundreds of
    /// feet.
    /// </summary>
    private const double MaxHoldingShortCreepFt = 10.0;

    private const string RefusedTaxiB = "TAXI B rejected: already on B; give a destination or a further taxiway";

    /// <summary>
    /// <paramref name="pastBarFt"/> feet from the bar toward 28L (0 = on the bar), facing away from the runway along B.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(10.0)]
    public void BareTaxiB_At28LBarFacingAway_IsRefused(double pastBarFt)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        (GroundNode bar, _, double towardRunwayDeg) = Bars28LOnB(layout);
        var heading = new TrueHeading(MagneticDeclination.NormalizeDeg(towardRunwayDeg + 180.0));
        LatLon position = GeoMath.ProjectPoint(bar.Position, new TrueHeading(towardRunwayDeg), pastBarFt / GeoMath.FeetPerNm);
        output.WriteLine($"bar {bar.Id}, toward 28L {towardRunwayDeg:F1}, start {pastBarFt:F0} ft past it, heading {heading.Degrees:F1}");

        AssertBareTaxiRefused(groundData, layout, new BareTaxiStart(position, heading, new HoldingInPositionPhase()), "TAXI B");
    }

    /// <summary>
    /// <paramref name="shortOfBarFt"/> feet from the bar away from the runway (0 = on the bar), facing 28L along B.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(10.0)]
    public void BareTaxiB_AtOrJustShortOfTheBarFacingTheRunway_IsRefused(double shortOfBarFt)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        (GroundNode bar, _, double towardRunwayDeg) = Bars28LOnB(layout);
        var heading = new TrueHeading(towardRunwayDeg);
        double awayFromRunwayDeg = MagneticDeclination.NormalizeDeg(towardRunwayDeg + 180.0);
        LatLon position = GeoMath.ProjectPoint(bar.Position, new TrueHeading(awayFromRunwayDeg), shortOfBarFt / GeoMath.FeetPerNm);
        output.WriteLine($"bar {bar.Id}, toward 28L {towardRunwayDeg:F1}, start {shortOfBarFt:F0} ft short of it, heading {heading.Degrees:F1}");

        AssertBareTaxiRefused(groundData, layout, new BareTaxiStart(position, heading, new HoldingInPositionPhase()), "TAXI B");
    }

    /// <summary>
    /// Holding short of 28L at the south B bar, facing the runway, the aircraft is given a bare <c>TAXI B</c>: it must not
    /// take that as a clearance across 28L, so it is refused and stays on the bar.
    /// </summary>
    [Fact]
    public void BareTaxiB_HoldingShortOf28LFacingIt_DoesNotCross()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        AssertBareTaxiRefused(groundData, layout, HoldingShortOf28LOnB(layout, output), "TAXI B");
    }

    /// <summary>40 ft short of the same bar on the side away from the runway, facing the runway along B.</summary>
    [Fact]
    public void BareTaxiB_ShortOfTheBarFacingTheRunway_IsRefused()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        (GroundNode bar, _, double towardRunwayDeg) = Bars28LOnB(layout);
        var heading = new TrueHeading(towardRunwayDeg);
        double awayFromRunwayDeg = MagneticDeclination.NormalizeDeg(towardRunwayDeg + 180.0);
        LatLon position = GeoMath.ProjectPoint(bar.Position, new TrueHeading(awayFromRunwayDeg), 40.0 / GeoMath.FeetPerNm);
        output.WriteLine($"bar {bar.Id}, toward 28L {towardRunwayDeg:F1}, start 40 ft short of it, heading {heading.Degrees:F1}");

        AssertBareTaxiRefused(groundData, layout, new BareTaxiStart(position, heading, new HoldingInPositionPhase()), "TAXI B");
    }

    /// <summary>"Without delay" adds no destination: <c>TAXI B EXP</c> from holding short of 28L on B is refused as bare.</summary>
    [Fact]
    public void TaxiBExp_OnB_IsRefused()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        AssertBareTaxiRefused(groundData, layout, HoldingShortOf28LOnB(layout, output), "TAXI B EXP");
    }

    /// <summary>
    /// A hold short or a crossing is more than the own taxiway: <c>TAXI B HS C</c> and <c>TAXI B CROSS 28R</c> from holding
    /// short of 28L on B are not bare, so the aircraft is given a route and taxis.
    /// </summary>
    [Theory]
    [InlineData("TAXI B HS C")]
    [InlineData("TAXI B CROSS 28R")]
    public void TaxiBWithAHoldShortOrACrossing_OnB_Taxis(string command)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        SimulationEngine engine = BuildOakEngine(groundData, AutoCrossRunwayMode.Off);
        BareTaxiStart start = HoldingShortOf28LOnB(layout, output);
        AircraftState aircraft = SpawnAt(layout, start.Position, start.Heading, start.Phase);
        engine.World.AddAircraft(aircraft);

        CommandResult taxi = engine.SendCommand(aircraft.Callsign, command);

        Assert.True(taxi.Success, $"'{command}' was refused: {taxi.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.NotEmpty(route.Segments);
    }

    /// <summary>
    /// A bare <c>TAXI B</c> from another taxiway is no refusal: on C, far enough back from where C meets B that its nose is
    /// past the taxiway setback, facing that junction, the aircraft taxis along C and holds short of B where an explicit
    /// <c>HS B</c> would stop it, never on the B/C junction node: an aircraft never stops inside an intersection.
    /// </summary>
    [Fact]
    public void BareTaxiB_FromC_HoldsShortOfB_NotRefused()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        HeldShortOfBOnC(groundData, layout);
    }

    /// <summary>
    /// The pilot reads back a bare <c>TAXI B</c> given on C (the <see cref="StartOnC.PastTheSetback"/> start) as it does
    /// today: pinned so a change to what the cut route says is seen.
    /// </summary>
    [Fact]
    public void BareTaxiB_FromC_ReadsBackAsPinned()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        (SimulationEngine engine, AircraftState aircraft, _) = OnCFacingB(groundData, layout, StartOnC.PastTheSetback);
        CommandResult taxi = engine.SendCommand(aircraft.Callsign, "TAXI B");
        Assert.True(taxi.Success, $"'TAXI B' was refused: {taxi.Message}");
        HandlerReadback readback = Assert.IsType<HandlerReadback>(taxi.PilotReadback);
        output.WriteLine($"readback terminal '{readback.Speech.Terminal}', tts '{readback.Speech.Tts}'");
        Assert.Equal("taxi, hold short of taxiway B", readback.Speech.Terminal);
        Assert.Equal("taxi, hold short of taxiway bravo", readback.Speech.Tts);
    }

    /// <summary>
    /// An explicit two-taxiway clearance with no destination ends the same way: <c>TAXI C B</c> from C holds short of B with
    /// the route-incomplete hold and the same advisory, stopped where an explicit <c>HS B</c> would stop it.
    /// </summary>
    [Fact]
    public void ExplicitTaxiCB_FromC_HoldsShortOfB()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        (SimulationEngine engine, AircraftState aircraft, GroundNode junction) = OnCFacingB(groundData, layout, StartOnC.PastTheSetback);

        CommandResult taxi = engine.SendCommand(aircraft.Callsign, "TAXI C B");
        TaxiRoute route = AssertHeldShortOfBRoute(taxi, aircraft, junction, "TAXI C B");
        TickUntilHoldingShortOfB(engine, aircraft, junction);
        AssertHeldAtThePlacedStop(layout, aircraft, route, junction);
    }

    /// <summary>
    /// An aircraft whose nose is already inside B's setback when given a bare <c>TAXI B</c> on C (two nodes from the junction,
    /// nose about 65 ft from B's centreline) is not refused and holds short of B where it stands: it never moves on into the
    /// junction.
    /// </summary>
    [Fact]
    public void BareTaxiB_FromCInsideTheSetback_HoldsWhereItStands()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        (SimulationEngine engine, AircraftState aircraft, GroundNode junction) = OnCFacingB(groundData, layout, StartOnC.TwoNodesFromB);
        LatLon start = aircraft.Position;
        Assert.True(
            NoseToCentrelineFt(layout, aircraft, "B") < TaxiwayNoseSetbackFt(layout, aircraft.AircraftType),
            "the start is not inside B's setback"
        );

        CommandResult taxi = engine.SendCommand(aircraft.Callsign, "TAXI B");
        AssertHeldShortOfBRoute(taxi, aircraft, junction, "TAXI B");
        TickUntilHoldingShortOfB(engine, aircraft, junction);
        for (int second = 0; second < HoldingShortTickSeconds; second++)
        {
            engine.TickOneSecond();
        }

        double movedFt = GeoMath.DistanceNm(aircraft.Position, start) * GeoMath.FeetPerNm;
        output.WriteLine($"after the hold and {HoldingShortTickSeconds} s more: {movedFt:F1} ft from the start");
        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.True(movedFt <= MaxHoldingShortCreepFt, $"the aircraft moved {movedFt:F1} ft from where it stood");
    }

    /// <summary>
    /// An explicit taxiway hold short has no backstop: an aircraft eastbound on D told <c>TAXI D HS C</c> stops however far
    /// back it takes for its nose to clear C's centreline by the taxiway setback, toward or into the G/D intersection behind
    /// the D/C merge, rather than at an earlier intersection short of that. Keeping G/D clear is the controller's to do.
    /// </summary>
    [Fact]
    public void TaxiDHoldShortC_EastboundOnD_HoldsWithItsNosePastTheSetback()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        const string AircraftType = "C172";
        GroundNode merge = Assert.IsType<GroundNode>(layout.FindIntersectionNode("C", "D"));
        GroundNode gd = Assert.IsType<GroundNode>(layout.FindIntersectionNode("D", "G"));
        GroundNode onD = merge.Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway("D")).Select(e => e.OtherNode(merge)).MinBy(n => n.Id)!;
        List<GroundNode> back = [merge, .. TaxiStartsOnOccupiedTaxiwayTests.NodesAhead(onD, GeoMath.BearingTo(merge.Position, onD.Position), "D")];
        int index = FirstPastTheSetback(layout, back, AircraftType, "C");
        Assert.True((index > 0) && (index < back.Count), $"no start on D back from the D/C merge {merge.Id} with its nose past C's setback");
        GroundNode startNode = back[index];
        var heading = new TrueHeading(GeoMath.BearingTo(startNode.Position, back[index - 1].Position));
        SimulationEngine engine = BuildOakEngine(groundData, AutoCrossRunwayMode.Off);
        AircraftState aircraft = SpawnAt(layout, startNode.Position, heading, new HoldingInPositionPhase());
        aircraft.Ground.CurrentTaxiway = "D";
        engine.World.AddAircraft(aircraft);
        output.WriteLine(
            $"D/C merge {merge.Id}, G/D {gd.Id} {GeoMath.DistanceNm(gd.Position, merge.Position) * GeoMath.FeetPerNm:F1} ft from it, start on D "
                + $"node {startNode.Id}, heading {heading.Degrees:F1}, nose {NoseToCentrelineFt(layout, aircraft, "C"):F1} ft from C's centreline"
        );

        CommandResult taxi = engine.SendCommand(aircraft.Callsign, "TAXI D HS C");
        Assert.True(taxi.Success, $"'TAXI D HS C' was refused: {taxi.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        HoldShortPoint hold = Assert.Single(route.HoldShortPoints, h => h.TargetName == "C");
        Assert.DoesNotContain(route.Warnings, w => w.StartsWith(WingtipWarningPrefix("C"), StringComparison.Ordinal));
        int held = SfoGroundHarness.TickUntil(
            engine,
            () => aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.TargetName: "C" },
            300,
            null
        );
        Assert.True(held > 0, $"not holding short of C after 300 s: {aircraft.Phases?.CurrentPhase?.Name}");

        double noseFt = NoseToCentrelineFt(layout, aircraft, "C");
        double setbackFt = TaxiwayNoseSetbackFt(layout, AircraftType);
        output.WriteLine(
            $"holding short of C: nose {noseFt:F1} ft from C's centreline (setback {setbackFt:F1}), "
                + $"{GeoMath.DistanceNm(aircraft.Position, gd.Position) * GeoMath.FeetPerNm:F1} ft from G/D"
        );
        Assert.True(noseFt >= setbackFt - NoseSetbackToleranceFt, $"the nose holds {noseFt:F1} ft from C's centreline, inside {setbackFt:F1}");
    }

    /// <summary>
    /// A stopped aircraft whose setback point lies behind it holds where it stands: on C two nodes from where C meets B, its
    /// nose already inside B's setback, <c>TAXI C HS B</c> holds it short of B without moving, and the route keeps the
    /// wingtip-clearance warning since the nose stays short of the floor.
    /// </summary>
    [Fact]
    public void TaxiCHoldShortB_FromCInsideTheSetback_HoldsWhereItStands()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        (SimulationEngine engine, AircraftState aircraft, _) = OnCFacingB(groundData, layout, StartOnC.TwoNodesFromB);
        LatLon start = aircraft.Position;

        CommandResult taxi = engine.SendCommand(aircraft.Callsign, "TAXI C HS B");
        Assert.True(taxi.Success, $"'TAXI C HS B' was refused: {taxi.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.Contains(route.Warnings, w => w.StartsWith(WingtipWarningPrefix("B"), StringComparison.Ordinal));
        int held = SfoGroundHarness.TickUntil(
            engine,
            () => aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.TargetName: "B" },
            300,
            null
        );
        Assert.True(held > 0, $"not holding short of B after 300 s: {aircraft.Phases?.CurrentPhase?.Name}");
        for (int second = 0; second < HoldingShortTickSeconds; second++)
        {
            engine.TickOneSecond();
        }

        double movedFt = GeoMath.DistanceNm(aircraft.Position, start) * GeoMath.FeetPerNm;
        output.WriteLine($"holding short of B {movedFt:F1} ft from the start");
        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.True(movedFt <= MaxHoldingShortCreepFt, $"the aircraft moved {movedFt:F1} ft from where it stood");
    }

    /// <summary>
    /// Holding short of B where the bare <c>TAXI B</c> ended, a second bare <c>TAXI B</c> names no way along B, so it is
    /// refused like the bare taxi on the aircraft's own taxiway, nothing is spoken, and the aircraft stays held. B leaves the
    /// C/B junction only behind the aircraft (no direction onto it is a turn it can make), so the reason says B lies behind
    /// and asks for a destination, as it does for <c>TAXI &gt;B</c> and <c>TAXI &lt;B</c>.
    /// </summary>
    [Fact]
    public void BareTaxiB_FromTheHoldShortOfB_IsRefused()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = HeldShortOfBOnC(groundData, layout);
        LatLon held = aircraft.Position;
        TaxiRoute? routeBefore = aircraft.Ground.AssignedTaxiRoute;

        CommandResult again = engine.SendCommand(aircraft.Callsign, "TAXI B");
        output.WriteLine($"TAXI B: success={again.Success} '{again.Message}'");
        Assert.False(again.Success, $"'TAXI B' from holding short of B was accepted: {again.Message}");
        Assert.Equal("holding short of B: B lies behind, give a destination", again.Message);
        Assert.Same(routeBefore, aircraft.Ground.AssignedTaxiRoute);
        Assert.Null(again.PilotReadback);
        for (int second = 0; second < HoldingShortTickSeconds; second++)
        {
            engine.TickOneSecond();
        }

        double movedFt = GeoMath.DistanceNm(aircraft.Position, held) * GeoMath.FeetPerNm;
        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.True(movedFt <= MaxHoldingShortCreepFt, $"the aircraft moved {movedFt:F1} ft after the refused TAXI B");
    }

    /// <summary>
    /// Holding short of B where the bare <c>TAXI B</c> ended, a <c>TAXI >B</c> or <c>TAXI &lt;B</c> whose route would only
    /// end at the same hold is refused: B leaves the C/B junction only behind the aircraft (a turn the aircraft cannot make
    /// there), so the reason asks for a destination, and the aircraft keeps its route.
    /// </summary>
    [Theory]
    [InlineData("TAXI >B")]
    [InlineData("TAXI <B")]
    public void TaxiBWithADirection_FromTheHoldShortOfB_IsRefusedAsBehind(string command)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = HeldShortOfBOnC(groundData, layout);
        TaxiRoute? routeBefore = aircraft.Ground.AssignedTaxiRoute;

        CommandResult again = engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine($"{command}: success={again.Success} '{again.Message}'");
        Assert.False(again.Success, $"'{command}' from holding short of B was accepted: {again.Message}");
        Assert.Same(routeBefore, aircraft.Ground.AssignedTaxiRoute);
        Assert.Equal("holding short of B: B lies behind, give a destination", again.Message);
    }

    /// <summary>
    /// Holding short of B where the bare <c>TAXI B</c> ended, <c>TAXI B RWY 28L</c> names a destination, so it is accepted by
    /// whatever route reaches it: the route holds short of 28L at its bar on B (OAK node 184).
    /// </summary>
    [Fact]
    public void TaxiBToARunway_FromTheHoldShortOfB_IsAccepted()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = HeldShortOfBOnC(groundData, layout);

        CommandResult again = engine.SendCommand(aircraft.Callsign, "TAXI B RWY 28L");
        output.WriteLine($"TAXI B RWY 28L: success={again.Success} '{again.Message}'");
        Assert.True(again.Success, $"'TAXI B RWY 28L' from holding short of B was refused: {again.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.Contains(route.HoldShortPoints, h => (h.Reason == HoldShortReason.DestinationRunway) && (h.NodeId == Oak28LBarOnBNodeId));
    }

    /// <summary>The OAK node of runway 28L's hold-short bar on B.</summary>
    private const int Oak28LBarOnBNodeId = 184;

    /// <summary>
    /// <c>RES</c> does not release the hold a bare <c>TAXI B</c> ended in: the route has nowhere to go past it, so it is
    /// refused with the route-incomplete reason and the aircraft stays put.
    /// </summary>
    [Fact]
    public void Res_AtTheHoldShortOfB_IsRejectedAndTheAircraftStays()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = HeldShortOfBOnC(groundData, layout);
        LatLon held = aircraft.Position;

        CommandResult res = engine.SendCommand(aircraft.Callsign, "RES");
        output.WriteLine($"RES: success={res.Success} '{res.Message}'");
        Assert.False(res.Success, $"RES was accepted: {res.Message}");
        Assert.Equal(HoldingShortPhase.RouteIncompleteMessage("B"), res.Message);
        for (int second = 0; second < HoldingShortTickSeconds; second++)
        {
            engine.TickOneSecond();
        }

        double movedFt = GeoMath.DistanceNm(aircraft.Position, held) * GeoMath.FeetPerNm;
        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.True(movedFt <= MaxHoldingShortCreepFt, $"the aircraft moved {movedFt:F1} ft after RES");
    }

    /// <summary>
    /// An explicit <c>HS B</c> folded onto the no-destination cut (<c>TAXI C HS B</c> from the
    /// <see cref="StartOnC.PastTheSetback"/> start on C) ends the route at B just as a bare <c>TAXI B</c> does: <c>RES</c>
    /// at that hold is refused with the route-incomplete reason, since the route has nowhere to go past it, and the aircraft
    /// stays held.
    /// </summary>
    [Fact]
    public void TaxiCHoldShortB_ThenRes_IsRefusedWithTheRouteIncompleteMessage() => AssertRefusedAtTheTaxiCHoldShortB("RES");

    /// <summary>
    /// <c>CROSS</c>, bare or naming B, at the hold a <c>TAXI C HS B</c> folded onto the no-destination cut ends in is
    /// refused with the route-incomplete reason as <c>RES</c> is, and the aircraft stays held: crossing B leads nowhere.
    /// </summary>
    [Theory]
    [InlineData("CROSS")]
    [InlineData("CROSS B")]
    public void TaxiCHoldShortB_ThenCross_IsRefusedWithTheRouteIncompleteMessage(string command) => AssertRefusedAtTheTaxiCHoldShortB(command);

    /// <summary>
    /// <c>TAXI C HS B</c> from the <see cref="StartOnC.PastTheSetback"/> start on C, taken to its hold, then
    /// <paramref name="command"/>: refused with the route-incomplete reason, and the aircraft stays held.
    /// </summary>
    private void AssertRefusedAtTheTaxiCHoldShortB(string command)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        (SimulationEngine engine, AircraftState aircraft, GroundNode junction) = OnCFacingB(groundData, layout, StartOnC.PastTheSetback);
        CommandResult taxi = engine.SendCommand(aircraft.Callsign, "TAXI C HS B");
        Assert.True(taxi.Success, $"'TAXI C HS B' was refused: {taxi.Message}");
        SfoGroundHarness.DumpRoute(output, Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute));
        TickUntilHoldingShortOfB(engine, aircraft, junction);
        LatLon held = aircraft.Position;

        CommandResult refused = engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine($"{command}: success={refused.Success} '{refused.Message}'");
        Assert.False(refused.Success, $"{command} was accepted: {refused.Message}");
        Assert.Equal("holding short of B where the route issued ends — issue a TAXI that includes B", refused.Message);
        for (int second = 0; second < HoldingShortTickSeconds; second++)
        {
            engine.TickOneSecond();
        }

        double movedFt = GeoMath.DistanceNm(aircraft.Position, held) * GeoMath.FeetPerNm;
        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.True(movedFt <= MaxHoldingShortCreepFt, $"the aircraft moved {movedFt:F1} ft after {command}");
    }

    /// <summary>
    /// The advisory a no-destination route ending at B carries in place of the old "holding at the C/B intersection" one.
    /// </summary>
    private const string HoldingShortOfBAdvisory = "holding short of B: route ends at B, no destination given";

    /// <summary>
    /// How far from the hold's placed stop the stopped aircraft may sit: the navigator brakes onto a painted stop to zero
    /// the navigator's 2 ft set-back stop margin short of it, plus a foot of rounding.
    /// </summary>
    private const double StopToleranceFt = 3.0;

    /// <summary>How far past the setback the nose of the <see cref="StartOnC.PastTheSetback"/> start is, at least.</summary>
    private const double PastTheSetbackMarginFt = 10.0;

    /// <summary>Where on C, facing the C/B junction, an aircraft starts.</summary>
    private enum StartOnC
    {
        /// <summary>Two nodes from the junction (OAK node 4), the nose already inside B's setback.</summary>
        TwoNodesFromB,

        /// <summary>The first node back from the junction where the nose is past B's setback by <see cref="PastTheSetbackMarginFt"/>.</summary>
        PastTheSetback,
    }

    /// <summary>
    /// A bare <c>TAXI B</c> from the <see cref="StartOnC.PastTheSetback"/> start on C, taken to the hold short of B it ends
    /// in: accepted, the route held short of B, the aircraft holding at the hold's placed stop.
    /// </summary>
    private (SimulationEngine Engine, AircraftState Aircraft) HeldShortOfBOnC(TestAirportGroundData groundData, AirportGroundLayout layout)
    {
        (SimulationEngine engine, AircraftState aircraft, GroundNode junction) = OnCFacingB(groundData, layout, StartOnC.PastTheSetback);

        CommandResult taxi = engine.SendCommand(aircraft.Callsign, "TAXI B");
        TaxiRoute route = AssertHeldShortOfBRoute(taxi, aircraft, junction, "TAXI B");
        HoldingShortPhase holding = TickUntilHoldingShortOfB(engine, aircraft, junction);
        Assert.Equal(HoldShortReason.RouteIncomplete, holding.HoldShort.Reason);
        AssertHeldAtThePlacedStop(layout, aircraft, route, junction);
        return (engine, aircraft);
    }

    /// <summary>
    /// A KOAK engine with a C172 on C at <paramref name="where"/>, facing the C/B junction and holding in position, and the
    /// junction node.
    /// </summary>
    private (SimulationEngine Engine, AircraftState Aircraft, GroundNode Junction) OnCFacingB(
        TestAirportGroundData groundData,
        AirportGroundLayout layout,
        StartOnC where
    )
    {
        const string AircraftType = "C172";
        GroundNode junction = Assert.IsType<GroundNode>(layout.FindIntersectionNode("B", "C"));
        GroundNode onC = junction.Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway("C")).Select(e => e.OtherNode(junction)).MinBy(n => n.Id)!;
        List<GroundNode> back = [.. TaxiStartsOnOccupiedTaxiwayTests.NodesAhead(onC, GeoMath.BearingTo(junction.Position, onC.Position), "C")];
        int index = where == StartOnC.TwoNodesFromB ? 1 : FirstPastTheSetback(layout, back, AircraftType, "B");
        Assert.True((index > 0) && (index < back.Count), $"no {where} start on C back from the B junction {junction.Id}");
        GroundNode startNode = back[index];
        var heading = new TrueHeading(GeoMath.BearingTo(startNode.Position, back[index - 1].Position));

        SimulationEngine engine = BuildOakEngine(groundData, AutoCrossRunwayMode.Off);
        AircraftState aircraft = SpawnAt(layout, startNode.Position, heading, new HoldingInPositionPhase());
        Assert.Equal(AircraftType, aircraft.AircraftType);
        aircraft.Ground.CurrentTaxiway = "C";
        engine.World.AddAircraft(aircraft);
        double toJunctionFt = GeoMath.DistanceNm(startNode.Position, junction.Position) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"B/C junction {junction.Id}, start on C node {startNode.Id} {toJunctionFt:F1} ft from it, heading {heading.Degrees:F1}, "
                + $"nose {NoseToCentrelineFt(layout, aircraft, "B"):F1} ft from B's centreline"
        );
        return (engine, aircraft, junction);
    }

    /// <summary>
    /// The index in <paramref name="back"/> (a taxiway's nodes back from where it meets <paramref name="target"/>) of the first
    /// node where an aircraft facing that junction has its nose past the target's setback by
    /// <see cref="PastTheSetbackMarginFt"/>; -1 when there is none.
    /// </summary>
    private static int FirstPastTheSetback(AirportGroundLayout layout, List<GroundNode> back, string aircraftType, string target)
    {
        double wantFt = TaxiwayNoseSetbackFt(layout, aircraftType) + PastTheSetbackMarginFt;
        for (int i = 1; i < back.Count; i++)
        {
            var heading = new TrueHeading(GeoMath.BearingTo(back[i].Position, back[i - 1].Position));
            if (NoseToCentrelineFt(layout, back[i].Position, heading, aircraftType, target) >= wantFt)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// <paramref name="command"/> was accepted with a route along C held short of B by a route-incomplete hold, bound like an
    /// explicit <c>HS B</c> at the junction node with its stop set back from it, and carries the no-destination advisory.
    /// </summary>
    private TaxiRoute AssertHeldShortOfBRoute(CommandResult taxi, AircraftState aircraft, GroundNode junction, string command)
    {
        Assert.True(taxi.Success, $"'{command}' from C was refused: {taxi.Message}");
        output.WriteLine($"response: {taxi.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.NotEmpty(route.Segments);
        Assert.All(route.Segments, s => Assert.False(s.Edge.Edge.MatchesTaxiway("B"), $"segment {s.FromNodeId}->{s.ToNodeId} drives B"));
        HoldShortPoint hold = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.RouteIncomplete);
        Assert.Equal("B", hold.TargetName);
        Assert.Equal(junction.Id, hold.NodeId);
        Assert.Equal(junction.Id, route.Segments[^1].ToNodeId);
        var stop = new LatLon(Assert.NotNull(hold.Latitude), Assert.NotNull(hold.Longitude));
        double stopFt = GeoMath.DistanceNm(stop, junction.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"hold stop {stopFt:F1} ft from the junction");
        Assert.True(stopFt > AirportGroundLayout.OnTaxiEdgeMaxOffsetFt, $"the hold's stop is {stopFt:F1} ft from the junction node");
        Assert.Contains(HoldingShortOfBAdvisory, route.Warnings);
        Assert.DoesNotContain(route.Warnings, w => w.Contains("intersection", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(route.Warnings, w => w.Contains("needs B", StringComparison.OrdinalIgnoreCase));
        return route;
    }

    /// <summary>
    /// Ticks until the aircraft holds short of B, at most 300 s, never coming within
    /// <see cref="AirportGroundLayout.OnTaxiEdgeMaxOffsetFt"/> of <paramref name="junction"/>, and returns that hold.
    /// </summary>
    private static HoldingShortPhase TickUntilHoldingShortOfB(SimulationEngine engine, AircraftState aircraft, GroundNode junction)
    {
        double nearestFt = double.PositiveInfinity;
        int held = SfoGroundHarness.TickUntil(
            engine,
            () => aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.TargetName: "B" },
            300,
            _ => nearestFt = Math.Min(nearestFt, GeoMath.DistanceNm(aircraft.Position, junction.Position) * GeoMath.FeetPerNm)
        );
        Assert.True(held > 0, $"not holding short of B after 300 s: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.True(nearestFt > AirportGroundLayout.OnTaxiEdgeMaxOffsetFt, $"the aircraft came {nearestFt:F1} ft from the junction node");
        return Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
    }

    /// <summary>
    /// The aircraft holds where an explicit <c>HS B</c> would stop it: at the hold's placed stop on <paramref name="route"/>,
    /// which is at least the length setback (<c>L + buffer</c>) back from <paramref name="junction"/>, with its nose at least
    /// the taxiway setback from B's centreline however many intersections lie behind (no junction caps the walk back), so
    /// the route carries no wingtip-clearance warning for B.
    /// </summary>
    private void AssertHeldAtThePlacedStop(AirportGroundLayout layout, AircraftState aircraft, TaxiRoute route, GroundNode junction)
    {
        HoldShortPoint hold = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.RouteIncomplete);
        var stop = new LatLon(Assert.NotNull(hold.Latitude), Assert.NotNull(hold.Longitude));
        double offStopFt = GeoMath.DistanceNm(aircraft.Position, stop) * GeoMath.FeetPerNm;
        double stopBackFt = GeoMath.DistanceNm(stop, junction.Position) * GeoMath.FeetPerNm;
        double lengthSetbackFt = AircraftLength.ResolveFt(aircraft.AircraftType) + HoldShortAnnotator.TaxiwayHoldShortBufferFt;
        double noseFt = NoseToCentrelineFt(layout, aircraft, "B");
        double setbackFt = TaxiwayNoseSetbackFt(layout, aircraft.AircraftType);
        output.WriteLine(
            $"holding short of B: {offStopFt:F1} ft off the stop, stop {stopBackFt:F1} ft back from the junction (length setback "
                + $"{lengthSetbackFt:F1}), nose {noseFt:F1} ft from B's centreline (setback {setbackFt:F1})"
        );
        Assert.True(offStopFt <= StopToleranceFt, $"the aircraft holds {offStopFt:F1} ft from the hold's placed stop");
        Assert.True(stopBackFt >= lengthSetbackFt - StopToleranceFt, $"the stop is {stopBackFt:F1} ft back from the junction");
        Assert.True(noseFt >= setbackFt - NoseSetbackToleranceFt, $"the nose holds {noseFt:F1} ft from B's centreline, inside {setbackFt:F1}");
        Assert.DoesNotContain(route.Warnings, w => w.StartsWith(WingtipWarningPrefix("B"), StringComparison.Ordinal));
    }

    /// <summary>The annotator's wingtip-clearance warning for a hold short of <paramref name="taxiway"/>, up to the distance.</summary>
    private static string WingtipWarningPrefix(string taxiway) => $"holding short of TWY {taxiway} — wingtip clearance from {taxiway} not assured";

    /// <summary>How far inside the setback a held nose may sit: the annotator walks the nose back in 1 ft steps.</summary>
    private const double NoseSetbackToleranceFt = 1.0;

    /// <summary>
    /// The taxiway hold-short setback <see cref="HoldShortAnnotator.ComputeHoldShortPositions"/> holds the nose to:
    /// <c>max(L/2 + buffer, wingtip floor)</c> from the crossed taxiway's centreline.
    /// </summary>
    private static double TaxiwayNoseSetbackFt(AirportGroundLayout layout, string aircraftType)
    {
        double halfFt = AircraftLength.ResolveFt(aircraftType) / 2.0;
        double floorFt = HoldShortAnnotator.WingtipClearanceFloorFt(layout.Runways.Max(r => r.WidthFt));
        return Math.Max(halfFt + HoldShortAnnotator.TaxiwayHoldShortBufferFt, floorFt);
    }

    /// <summary>How far the aircraft's nose is from <paramref name="taxiway"/>'s centreline, in feet.</summary>
    private static double NoseToCentrelineFt(AirportGroundLayout layout, AircraftState aircraft, string taxiway) =>
        NoseToCentrelineFt(layout, aircraft.Position, aircraft.TrueHeading, aircraft.AircraftType, taxiway);

    /// <summary>
    /// How far the nose of an <paramref name="aircraftType"/> centred at <paramref name="position"/> and facing
    /// <paramref name="heading"/> is from <paramref name="taxiway"/>'s centreline, in feet, measured as the hold-short setback
    /// is (<see cref="HoldShortAnnotator.DistanceToCentrelineFt"/>).
    /// </summary>
    private static double NoseToCentrelineFt(AirportGroundLayout layout, LatLon position, TrueHeading heading, string aircraftType, string taxiway)
    {
        double halfNm = AircraftLength.ResolveFt(aircraftType) / 2.0 / GeoMath.FeetPerNm;
        LatLon nose = GeoMath.ProjectPoint(position, heading, halfNm);
        return HoldShortAnnotator.DistanceToCentrelineFt(nose, layout.Edges.Where(e => e.MatchesTaxiway(taxiway)), nose);
    }

    /// <summary>
    /// Issues <paramref name="command"/> to an aircraft on B at <paramref name="start"/> and asserts the bare-taxi refusal: a
    /// failed result with the reason, no route change, nothing for the pilot to say, and the aircraft still where it started
    /// after <see cref="HoldingShortTickSeconds"/>.
    /// </summary>
    private void AssertBareTaxiRefused(TestAirportGroundData groundData, AirportGroundLayout layout, BareTaxiStart start, string command)
    {
        SimulationEngine engine = BuildOakEngine(groundData, AutoCrossRunwayMode.Off);
        AircraftState aircraft = SpawnAt(layout, start.Position, start.Heading, start.Phase);
        engine.World.AddAircraft(aircraft);
        TaxiRoute? routeBefore = aircraft.Ground.AssignedTaxiRoute;

        CommandResult taxi = engine.SendCommand(aircraft.Callsign, command);

        Assert.False(taxi.Success, $"'{command}' was accepted: {taxi.Message}");
        Assert.Equal(RefusedTaxiB, taxi.Message);
        Assert.Same(routeBefore, aircraft.Ground.AssignedTaxiRoute);
        Assert.Null(taxi.PilotUnable);
        Assert.Null(taxi.PilotReadback);
        Assert.Empty(aircraft.PendingPilotTransmissions);

        for (int second = 0; second < HoldingShortTickSeconds; second++)
        {
            engine.TickOneSecond();
        }

        double movedFt = GeoMath.DistanceNm(aircraft.Position, start.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"after {HoldingShortTickSeconds} s: {movedFt:F1} ft from the start, phase {aircraft.Phases?.CurrentPhase?.GetType().Name}");
        Assert.True(movedFt <= MaxHoldingShortCreepFt, $"the aircraft is {movedFt:F1} ft from its start after {HoldingShortTickSeconds} s");
    }

    /// <summary>Where and how an aircraft stands on B before the taxi.</summary>
    internal sealed record BareTaxiStart(LatLon Position, TrueHeading Heading, Phase Phase);

    /// <summary>Holding short of 28L on the south B bar, uncleared, facing the runway.</summary>
    internal static BareTaxiStart HoldingShortOf28LOnB(AirportGroundLayout layout, ITestOutputHelper output)
    {
        (GroundNode bar, _, double towardRunwayDeg) = Bars28LOnB(layout);
        var heading = new TrueHeading(towardRunwayDeg);
        var holding = new HoldingShortPhase(
            new HoldShortPoint
            {
                NodeId = bar.Id,
                Reason = HoldShortReason.RunwayCrossing,
                TargetName = bar.RunwayId?.ToString(),
            }
        );
        output.WriteLine($"bar {bar.Id} ({bar.RunwayId}), toward 28L {towardRunwayDeg:F1}, holding short on it, heading {heading.Degrees:F1}");
        return new BareTaxiStart(bar.Position, heading, holding);
    }

    /// <summary>A KOAK engine in solo training, so every pilot readback is queued as a transmission.</summary>
    internal static SimulationEngine BuildOakEngine(IAirportGroundData groundData, AutoCrossRunwayMode autoCross) =>
        new(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-bare-taxi-b",
                ScenarioName = "OAK bare TAXI B",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunwayMode = autoCross,
                SoloTrainingMode = true,
            },
        };

    /// <summary>
    /// B's 28L/10R bar south of the runway (the one farther from 28R/10L), the bar across 28L from it, and the bearing of
    /// the south bar's B edge toward 28L.
    /// </summary>
    internal static (GroundNode Bar, GroundNode FarBar, double TowardRunwayDeg) Bars28LOnB(AirportGroundLayout layout)
    {
        List<GroundNode> bars =
        [
            .. layout
                .Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } rwy) && rwy.Contains("28L"))
                .Where(n => n.Edges.Any(e => e.MatchesTaxiway("B"))),
        ];
        Assert.True(bars.Count == 2, $"B has {bars.Count} 28L/10R bars, expected two (one each side)");
        GroundNode south = bars.MinBy(n => n.Position.Lat)!;
        GroundNode north = bars.MaxBy(n => n.Position.Lat)!;
        double towardNorthBar = GeoMath.BearingTo(south.Position, north.Position);
        double towardRunwayDeg = Assert.Single(
            south
                .Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway("B"))
                .Select(e => GeoMath.BearingTo(south.Position, e.OtherNode(south).Position)),
            b => GeoMath.AbsBearingDifference(b, towardNorthBar) < 90.0
        );
        return (south, north, towardRunwayDeg);
    }

    /// <summary>A C172 on B at <paramref name="position"/>, running <paramref name="startPhase"/>.</summary>
    internal static AircraftState SpawnAt(AirportGroundLayout layout, LatLon position, TrueHeading heading, Phase startPhase)
    {
        var aircraft = new AircraftState
        {
            Callsign = "N1259",
            AircraftType = "C172",
            Position = position,
            TrueHeading = heading,
            TrueTrack = heading,
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(startPhase);
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.CurrentTaxiway = "B";
        return aircraft;
    }
}
