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
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-905: a bare taxi whose two taxiways meet only across a runway (KCOS A2/C2 across 17R/35L, declared in-test as a
/// sidecar <c>crossingPairs</c> entry; KOAK D/F across 15/33, YAAT-850's offset crossing) ends on the far taxiway with the
/// whole aircraft past its holding-position bar: half the aircraft's length plus
/// <see cref="RunwayTailClearance.MarginFt"/> past the far bar, straight ahead, never on the runway centreline. The
/// readback names the crossing, never "on 17R", "RWY" or "intersection".
/// </summary>
[Collection("Acceptance")]
public class Issue905BareCrossingEndTests(ITestOutputHelper output)
{
    private const string CosBareRsp = "Taxi via A2 C2 [holding short of 17R/35L at A2 — route ends on C2 clear of 17R/35L, no destination given]";
    private const string CosReverseRsp = "Taxi via C2 A2 [holding short of 17R/35L at C2 — route ends on A2 clear of 17R/35L, no destination given]";
    private const string CosCrossRsp = "Taxi via A2 C2 [crossing 17R/35L at A2 — route ends on C2 clear of 17R/35L, no destination given]";
    private const string CosHoldShortRsp = "Taxi via A2 C2 HS 17R/35L at A2 [route ends on C2 clear of 17R/35L, no destination given]";
    private const string OakBareRsp = "Taxi via D F [holding short of 15/33 at D — route ends on F clear of 15/33, no destination given]";

    /// <summary>KOAK's D/J junction (<see cref="Issue1334OakDToFAcross33Tests"/>), nosed north up D.</summary>
    private static GroundNode OakDJJunctionNode(AirportGroundLayout layout) => GroundNodeLookup.Junction(layout, "D", "J");

    /// <summary>KOAK's F join on the 15/33 centreline, where the D→F crossing leaves the runway.</summary>
    private static GroundNode OakFJoinOn33Node(AirportGroundLayout layout) => GroundNodeLookup.Junction(layout, "RWY15/33", "F");

    /// <summary>KOAK F's 15/33 bar on the far side of the D→F crossing.</summary>
    private static GroundNode OakFBar33Node(AirportGroundLayout layout) => GroundNodeLookup.HoldShortBar(layout, "33", "F");

    /// <summary>KOAK's next F node past <see cref="OakFBar33Node"/>, away from 15/33.</summary>
    private static GroundNode OakFPastItsBarNode(AirportGroundLayout layout) =>
        GroundNodeLookup.OnTaxiway(layout, "F", OakFBar33Node(layout), 1.0, GroundNodeLookup.Parking(layout, "GA20"));

    /// <summary>The arrival landing KOAK 28R in <see cref="OakRolloutTaxiCDF_ReadsBackTheCrossingBeforeTheEnd"/>.</summary>
    private const string RolloutArrival = "ARR1";

    private static double ClearFt(string aircraftType) => (AircraftLength.ResolveFt(aircraftType) / 2.0) + RunwayTailClearance.MarginFt;

    /// <summary>
    /// R1/R2: KCOS <c>TAXI A2 C2</c> and <c>TAXI C2 A2</c> for a C172, the pair declared: the route ends on the far taxiway at
    /// least half a length plus the margin past its 17R/35L bar, not on the centreline join, and the readback names the
    /// crossing (R5).
    /// </summary>
    [Theory]
    [InlineData("A2", "C2", CosBareRsp)]
    [InlineData("C2", "A2", CosReverseRsp)]
    public void CosBare_EndsOnTheFarTaxiwayPastItsBar(string fromTaxiway, string toTaxiway, string expectedRsp)
    {
        if ((CosStart(fromTaxiway) is not { } start) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        using IDisposable _ = NavigationDatabase.ScopedOverride(CosDeclaring(navDb));
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        var taxi = new TaxiCommand(Path: [fromTaxiway, toTaxiway], HoldShorts: [], Expedite: false);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, start.Layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        AssertEndsClearPast(output, route, start.Layout, ("35L", toTaxiway), "C172");
        AssertNoRunwayLeg(result.Message);
        Assert.Equal(expectedRsp, result.Message);
    }

    /// <summary>
    /// The same bare <c>TAXI A2 C2</c> in a scenario that pre-clears crossings: the route ends the same way, and the readback
    /// says the aircraft crosses 17R/35L at A2 rather than holding short of it (7110.65 3-7-2.c).
    /// </summary>
    [Fact]
    public void CosBareAutoCross_ReadsBackTheCrossing()
    {
        if ((CosStart("A2") is not { } start) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        using IDisposable _ = NavigationDatabase.ScopedOverride(CosDeclaring(navDb));
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        var taxi = new TaxiCommand(Path: ["A2", "C2"], HoldShorts: [], Expedite: false);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, start.Layout, autoCrossRunwayMode: AutoCrossRunwayMode.All);
        output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        AssertEndsClearPast(output, route, start.Layout, ("35L", "C2"), "C172");
        Assert.Equal(CosCrossRsp, result.Message);
    }

    /// <summary>
    /// R7, unchanged: <c>TAXI A2 17R C2</c> (along the runway) and <c>TAXI A2 C2 HS A2</c> (a hold short that is not of the
    /// runway crossed) are not bare crossings, so neither carries the clear-of-runway end.
    /// </summary>
    [Theory]
    [InlineData("A2 17R C2", "")]
    [InlineData("A2 C2", "A2")]
    public void CosNotBare_KeepsTheOrdinaryEnd(string path, string holdShort)
    {
        if ((CosStart("A2") is not { } start) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        using IDisposable _ = NavigationDatabase.ScopedOverride(CosDeclaring(navDb));
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        List<HoldShortTarget> holdShorts = holdShort.Length == 0 ? [] : [HoldShortTarget.Parse(holdShort)];
        var taxi = new TaxiCommand(Path: [.. path.Split(' ')], HoldShorts: holdShorts, Expedite: false);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, start.Layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.DoesNotContain("clear of 17R/35L", result.Message);
        if (aircraft.Ground.AssignedTaxiRoute is { } route)
        {
            Assert.True(route.Segments[^1].ToNodeId >= 0, $"the route ends on a cut node #{route.Segments[^1].ToNodeId}");
        }
    }

    /// <summary>
    /// The cut end is a virtual node the layout never held: a snapshot taken mid-taxi on the cut leg records its position,
    /// and the restored route rebuilds the same node, id and edge, so it snapshots again byte for byte.
    /// </summary>
    [Fact]
    public void CosBareCutRoute_RestoresFromSnapshotByteForByte()
    {
        if ((CosStart("A2") is not { } start) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        using IDisposable _ = NavigationDatabase.ScopedOverride(CosDeclaring(navDb));
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, new TaxiCommand(["A2", "C2"], [], Expedite: false), start.Layout);
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        GroundNode end = route.Segments[^1].Edge.ToNode;
        Assert.True(VirtualNode.IsVirtualNode(end), $"the route ends on layout node #{end.Id}, not a cut");
        route.CurrentSegmentIndex = route.Segments.Count - 1;

        TaxiRouteDto dto = route.ToSnapshot();
        TaxiRoute restored = Assert.IsType<TaxiRoute>(TaxiRoute.FromSnapshot(dto, start.Layout));

        Assert.Equal(JsonSerializer.Serialize(dto), JsonSerializer.Serialize(restored.ToSnapshot()));
        Assert.Equal(end.Id, restored.Segments[^1].ToNodeId);
        Assert.Equal(end.Position, restored.Segments[^1].Edge.ToNode.Position);
        Assert.Equal(route.Segments[^1].Edge.DistanceNm, restored.Segments[^1].Edge.DistanceNm);
        Assert.Equal(route.CurrentSegmentIndex, restored.CurrentSegmentIndex);
    }

    /// <summary>
    /// R5/R7: <c>TAXI A2 C2 HS 35L</c> is bare, the only hold short on the runway being crossed: the same end past C2's bar,
    /// and the readback names the hold.
    /// </summary>
    [Fact]
    public void CosHoldShortOfTheCrossedRunway_EndsTheSame()
    {
        if ((CosStart("A2") is not { } start) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        using IDisposable _ = NavigationDatabase.ScopedOverride(CosDeclaring(navDb));
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        var taxi = new TaxiCommand(Path: ["A2", "C2"], HoldShorts: [HoldShortTarget.Parse("35L")], Expedite: false);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, start.Layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        AssertEndsClearPast(output, route, start.Layout, ("35L", "C2"), "C172");
        Assert.Equal(CosHoldShortRsp, result.Message);
    }

    /// <summary>An explicit <c>TAXI A2 C2 CROSS 35L</c> with no onward taxiway ends the same way, past C2's bar.</summary>
    [Fact]
    public void CosCrossWithNoOnwardTaxiway_EndsTheSame()
    {
        if ((CosStart("A2") is not { } start) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        using IDisposable _ = NavigationDatabase.ScopedOverride(CosDeclaring(navDb));
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        var taxi = new TaxiCommand(Path: ["A2", "C2"], HoldShorts: [], Expedite: false, CrossRunways: ["35L"]);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, start.Layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        AssertEndsClearPast(output, route, start.Layout, ("35L", "C2"), "C172");
        AssertNoRunwayLeg(result.Message);
        Assert.Equal(CosCrossRsp, result.Message);
    }

    /// <summary>
    /// R4 driven: the aircraft holds at A2's 17R/35L bar, <c>CROSS 35L</c> carries it over without stopping between the bars,
    /// and it comes to rest on C2 with the whole aircraft past C2's bar.
    /// </summary>
    [Fact]
    public void CosBareDriven_Cross35L_EndsClearOfTheRunway()
    {
        if ((CosStart("A2") is not { } start) || (TestVnasData.NavigationDb is not { } navDb))
        {
            return;
        }

        using IDisposable _ = NavigationDatabase.ScopedOverride(CosDeclaring(navDb));
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(start.Layout, "COS", start.Node, start.HeadingDeg);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-yaat-905-cos-a2-c2",
            ScenarioName = "COS A2/C2 bare across 17R/35L",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "COS",
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
        };

        CommandResult taxi = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "TAXI A2 C2");
        output.WriteLine($"taxi: {taxi.Success} — {taxi.Message}");
        Assert.True(taxi.Success, taxi.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        int farBarSegment = route.Segments.FindIndex(s => IsCosBarOnTaxiway(start.Layout, s.ToNodeId, "C2"));
        Assert.True(farBarSegment >= 0, "the route never reaches C2's 17R/35L bar");
        GroundNode farBar = start.Layout.Nodes[route.Segments[farBarSegment].ToNodeId];

        AircraftState? holding = Issue1334OakDToFAcross33Tests.TickUntil(output, engine, ac => ac.Phases?.CurrentPhase is HoldingShortPhase, 900);
        HoldingShortPhase hold = Assert.IsType<HoldingShortPhase>(holding?.Phases?.CurrentPhase);
        Assert.True(IsCosBarOnTaxiway(start.Layout, hold.HoldShort.NodeId, "A2"), $"holding at #{hold.HoldShort.NodeId}, not A2's bar");

        CommandResult cross = engine.SendCommand(Issue1334OakDToFAcross33Tests.Callsign, "CROSS 35L");
        output.WriteLine($"cross: {cross.Success} — {cross.Message}");
        Assert.True(cross.Success, cross.Message);

        bool stoppedBetweenBars = false;
        bool pastFarBar = false;
        var losses = new SpeedLossWatch(aircraft);
        AircraftState? atRest = SubTickDriver.TickSubTicksUntil(
            engine,
            Issue1334OakDToFAcross33Tests.Callsign,
            ac => pastFarBar && (ac.GroundSpeed < 0.1),
            300,
            ac =>
            {
                losses.Sample();
                pastFarBar |= (ac.Ground.AssignedTaxiRoute is null) || (ac.Ground.AssignedTaxiRoute.CurrentSegmentIndex > farBarSegment);
                stoppedBetweenBars |= (!pastFarBar) && (ac.Phases?.CurrentPhase is HoldingShortPhase);
            }
        );

        Assert.False(stoppedBetweenBars, "the aircraft held short again between A2's bar and C2's bar");
        Assert.NotNull(atRest);
        double pastFt = GeoMath.DistanceNm(farBar.Position, atRest.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"at rest {pastFt:F0} ft past C2's bar #{farBar.Id}, twy={atRest.Ground.CurrentTaxiway}");
        Assert.False(AirportGroundLayout.HasRunwayCenterlineEdge(route.Segments[^1].Edge.ToNode));
        Assert.True(pastFt >= AircraftLength.ResolveFt("C172") / 2.0, $"at rest only {pastFt:F0} ft past C2's bar");
        Assert.Equal("C2", atRest.Ground.CurrentTaxiway);

        // The bare crossing ends the route, and the hold that follows stops the aircraft where it stands, so the crossing hands
        // back within the set-back margin of its end: the tail rests clear of C2's bar by the tail clearance less that margin.
        double tailPastFt = pastFt - (AircraftLength.ResolveFt(atRest.AircraftType) / 2.0);
        double minTailFt = RunwayTailClearance.MarginFt - GroundNavigator.SetBackStopMarginFt;
        output.WriteLine($"tail at rest {tailPastFt:F1} ft past C2's bar");
        Assert.True(tailPastFt >= minTailFt, $"the tail rests {tailPastFt:F1} ft past C2's bar, inside {minTailFt:F0} ft");

        // The bare crossing's end is a stop, which it brakes onto at the taxi rate rather than stopping dead from its crossing
        // speed: no physics sub-tick loses more than that rate, the take of the stop from a crawl aside.
        double maxLossKts =
            (CategoryPerformance.TaxiDecelRate(AircraftCategorization.Categorize(atRest.AircraftType)) / SimulationEngine.PhysicsSubTickRate) + 0.01;
        output.WriteLine($"largest speed loss in one sub-tick: {losses.WorstKts:F2} kt at {losses.WorstAt}");
        Assert.True(
            losses.WorstKts <= maxLossKts,
            $"the aircraft lost {losses.WorstKts:F2} kt in one sub-tick at {losses.WorstAt}, more than the taxi rate's {maxLossKts:F2} kt"
        );
    }

    /// <summary>
    /// R5: the terminal and spoken readbacks come from the clearance as issued, and none says "RWY" or "intersection".
    /// </summary>
    [Fact]
    public void Readbacks_TerminalAndSpoken_AreVerbatim()
    {
        TestVnasData.EnsureInitialized();
        var bare = new TaxiCommand(["A2", "C2"], [], Expedite: false);
        var held = new TaxiCommand(["A2", "C2"], [HoldShortTarget.Parse("35L")], Expedite: false);
        var oak = new TaxiCommand(["D", "F"], [], Expedite: false);

        (string? Terminal, string? Spoken)[] readbacks =
        [
            (PhraseologyVerbalizer.VerbalizeTerminal(bare), PhraseologyVerbalizer.Verbalize(bare)),
            (PhraseologyVerbalizer.VerbalizeTerminal(held), PhraseologyVerbalizer.Verbalize(held)),
            (PhraseologyVerbalizer.VerbalizeTerminal(oak), PhraseologyVerbalizer.Verbalize(oak)),
        ];
        foreach ((string? terminal, string? spoken) in readbacks)
        {
            output.WriteLine($"terminal: {terminal} — spoken: {spoken}");
        }

        Assert.Equal("taxi via A2 C2", readbacks[0].Terminal);
        Assert.Equal("taxi via alpha two, charlie two", readbacks[0].Spoken);
        Assert.Equal("taxi via A2 C2, hold short of runway 35L", readbacks[1].Terminal);
        Assert.Equal("taxi via alpha two, charlie two, hold short of runway three five left", readbacks[1].Spoken);
        Assert.Equal("taxi via D F", readbacks[2].Terminal);
        Assert.Equal("taxi via delta, foxtrot", readbacks[2].Spoken);
        foreach (string text in readbacks.SelectMany(r => new[] { r.Terminal ?? "", r.Spoken ?? "" }))
        {
            AssertNoRunwayLeg(text);
        }
    }

    /// <summary>
    /// R6: KOAK <c>TAXI D F</c> from the D/J junction (YAAT-850's offset crossing of 15/33): the route ends on F past its 15/33
    /// bar, and reads back the crossing.
    /// </summary>
    [Fact]
    public void OakBareDF_EndsOnFPastItsBar()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(layout, "OAK", OakDJJunctionNode(layout), 330.0);
        var taxi = new TaxiCommand(Path: ["D", "F"], HoldShorts: [], Expedite: false);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        AssertEndsClearPast(output, route, layout, ("33", "F"), "C172");
        Assert.Equal(OakBareRsp, result.Message);
    }

    /// <summary>
    /// KOAK <c>TAXI D F HS J@D</c> from the D/J junction: the J hold short binds on D before the 15/33 crossing, so the
    /// clearance is still bare. The J hold short is kept, and the route ends on F clear of 15/33. (A bare <c>HS J</c> with no
    /// destination folds J into the path as <c>D F J</c>, so the located form is the one that keeps J an earlier hold.)
    /// </summary>
    [Fact]
    public void OakBareDF_HoldShortJBeforeTheCrossing_KeepsItAndEndsPastFsBar()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(layout, "OAK", OakDJJunctionNode(layout), 330.0);
        var taxi = new TaxiCommand(Path: ["D", "F"], HoldShorts: [HoldShortTarget.Parse("J@D")], Expedite: false);

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        foreach (HoldShortPoint hs in route.HoldShortPoints)
        {
            output.WriteLine($"hold short: #{hs.NodeId} {hs.TargetName} ({hs.Reason})");
        }

        HoldShortPoint j = Assert.Single(route.HoldShortPoints, hs => hs.Reason == HoldShortReason.ExplicitHoldShort);
        Assert.Equal("J", j.TargetName);
        HoldShortPoint crossing = Assert.Single(route.HoldShortPoints, hs => hs.Reason == HoldShortReason.RunwayCrossing);
        int jAt = route.Segments.FindIndex(s => s.FromNodeId == j.NodeId);
        int crossingAt = route.Segments.FindIndex(s => s.FromNodeId == crossing.NodeId);
        Assert.True(
            (jAt >= 0) && (jAt < crossingAt),
            $"the J hold short #{j.NodeId} (segment {jAt}) does not bind before the 15/33 crossing bar #{crossing.NodeId} (segment {crossingAt})"
        );
        AssertEndsClearPast(output, route, layout, ("33", "F"), "C172");
        Assert.Contains("route ends on F clear of 15/33", result.Message);
    }

    /// <summary>
    /// The same bare KOAK <c>TAXI D F</c> from the D/J junction for a B738 and a C172 ends at different points, each clear of
    /// 15/33 by its own length: the B738's end lies farther along F by half the difference in length. (KCOS's A2/C2 turn is
    /// too tight for a B738, which refuses it.)
    /// </summary>
    [Fact]
    public void OakBareDF_B738AndC172_EachEndsClearByItsOwnLength()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var alongFt = new Dictionary<string, double>();
        foreach (string type in new[] { "C172", "B738" })
        {
            AircraftState aircraft = Issue1334OakDToFAcross33Tests.MakeAircraftAt(layout, "OAK", OakDJJunctionNode(layout), 330.0);
            aircraft.AircraftType = type;
            CommandResult result = GroundCommandHandler.TryTaxi(aircraft, new TaxiCommand(["D", "F"], [], Expedite: false), layout);
            output.WriteLine($"{type}: {result.Success} — {result.Message}");
            Assert.True(result.Success, result.Message);
            TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
            alongFt[type] = AssertEndsClearPast(output, route, layout, ("33", "F"), type);
        }

        double expectedGapFt = ClearFt("B738") - ClearFt("C172");
        Assert.True(expectedGapFt > 1.0, $"a B738 and a C172 need the same clearance ({expectedGapFt:F1} ft apart)");
        Assert.Equal(expectedGapFt, alongFt["B738"] - alongFt["C172"], 1.0);
    }

    /// <summary>
    /// The same KOAK <c>TAXI C D F</c> sent on the 28R landing rollout and stored for after the exit: the readback says how the
    /// 15/33 crossing at D stands before the clear-of-runway end, as it does on the ground.
    /// </summary>
    [Fact]
    public void OakRolloutTaxiCDF_ReadsBackTheCrossingBeforeTheEnd()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        if (ShortFinalArrival.SpawnClearedToLand("OAK", "28R", "C172", RolloutArrival, 1.0) is not { } spawned)
        {
            return;
        }

        int touchdown = SfoGroundHarness.TickUntil(
            spawned.Engine,
            () => spawned.Aircraft.IsOnGround && (spawned.Aircraft.Phases?.CurrentPhase is LandingPhase),
            300,
            null
        );
        Assert.True(touchdown > 0, $"never touched down: {spawned.Aircraft.Phases?.CurrentPhase?.Name}");

        CommandResult? accepted = null;
        for (int second = 0; (second < 60) && (accepted is null); second++)
        {
            CommandResult result = spawned.Engine.SendCommand(RolloutArrival, "TAXI C D F");
            output.WriteLine($"t+{second} TAXI C D F at {spawned.Aircraft.GroundSpeed:F1} kt -> {result.Success} {result.Message}");
            accepted = result.Success ? result : null;
            spawned.Engine.TickOneSecond();
        }

        Assert.NotNull(accepted);
        Assert.NotNull(spawned.Aircraft.Phases?.TaxiRoute);
        Assert.Contains("holding short of 15/33 at D — route ends on F clear of 15/33", accepted.Message);
    }

    /// <summary>
    /// A bare crossing never ends at another runway's bar (auto-cross could clear that hold short with the nose past the
    /// other runway's holding position) nor on a runway centreline, but may end on the crossed runway's own bar side or on a
    /// plain taxiway node. No real layout we carry reaches the other-runway end, so the predicate is pinned on KOAK's nodes.
    /// </summary>
    [Fact]
    public void ClearOfRunwayEnd_IsRefusedAtAnotherRunwaysBar()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var crossed = RunwayIdentifier.Parse("15/33");
        GroundNode bar28L = layout.Nodes.Values.First(n =>
            (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId?.Contains("28L") == true) && !AirportGroundLayout.HasRunwayCenterlineEdge(n)
        );
        GroundNode fBar33 = layout.Nodes.Values.First(n => IsBarOnTaxiway(layout, n.Id, "33", "F"));
        GroundNode fNode = layout.Nodes.Values.First(n =>
            (n.Type != GroundNodeType.RunwayHoldShort) && n.Edges.All(e => e.MatchesTaxiway("F")) && !AirportGroundLayout.HasRunwayCenterlineEdge(n)
        );
        output.WriteLine($"28L bar #{bar28L.Id}, F's 15/33 bar #{fBar33.Id}, F node #{fNode.Id}");

        Assert.True(SegmentExpander.RefusesClearOfRunwayEndAt(bar28L, crossed), $"a 15/33 crossing may end at 28L's bar #{bar28L.Id}");
        Assert.False(SegmentExpander.RefusesClearOfRunwayEndAt(fBar33, crossed));
        Assert.False(SegmentExpander.RefusesClearOfRunwayEndAt(fNode, crossed));

        GroundNode centreline33 = OakFJoinOn33Node(layout);
        Assert.True(AirportGroundLayout.HasRunwayCenterlineEdge(centreline33), $"#{centreline33.Id} is not on a runway centreline");
        Assert.True(
            SegmentExpander.RefusesClearOfRunwayEndAt(centreline33, crossed),
            $"a 15/33 crossing may end on its centreline at #{centreline33.Id}"
        );
    }

    /// <summary>
    /// A walk's end on a layout node clears only where the end may stand at all: well past the line, another runway's bar or
    /// the crossed runway's centreline is still not a clear end; a plain F node is, once past the margin and not before.
    /// </summary>
    [Fact]
    public void ClearOfRunwayEnd_AtANode_ClearsOnlyWhereTheEndMayStand()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var crossed = RunwayIdentifier.Parse("15/33");
        GroundNode bar28L = layout.Nodes.Values.First(n =>
            (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId?.Contains("28L") == true) && !AirportGroundLayout.HasRunwayCenterlineEdge(n)
        );
        GroundNode fNode = OakFPastItsBarNode(layout);
        double wellPastFt = 1000.0;

        Assert.False(SegmentExpander.EndClearsAt(bar28L, crossed, wellPastFt), $"a 15/33 crossing clears at 28L's bar #{bar28L.Id}");
        Assert.False(SegmentExpander.EndClearsAt(OakFJoinOn33Node(layout), crossed, wellPastFt));
        Assert.True(SegmentExpander.EndClearsAt(fNode, crossed, RunwayTailClearance.MarginFt));
        Assert.False(SegmentExpander.EndClearsAt(fNode, crossed, RunwayTailClearance.MarginFt - 0.01));
    }

    /// <summary>
    /// The nose counts as well as the tail: a B738 centred 20 ft past F's 15/33 hold line but nosed 120° off the taxiway's axis
    /// has its tail far past the line and its nose back over it, so it is not clear. No layout we carry bends that far past a
    /// far bar (KCOS and KOAK at most 7°), so the measure is pinned on KOAK's far bar.
    /// </summary>
    [Fact]
    public void FuselagePastLine_CountsTheNoseWhenTheHeadingTurnsBack()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        GroundNode fBar33 = OakFBar33Node(layout);
        GroundNode fNode = OakFPastItsBarNode(layout);
        double axisDeg = GeoMath.BearingTo(fBar33.Position, fNode.Position);
        double lengthFt = AircraftLength.ResolveFt("B738");
        LatLon centre = GeoMath.ProjectPointRaw(fBar33.Position, axisDeg, 20.0 / GeoMath.FeetPerNm);

        double straightFt = SegmentExpander.FuselagePastLineFt(fBar33.Position, axisDeg, centre, axisDeg, lengthFt);
        double turnedBackFt = SegmentExpander.FuselagePastLineFt(fBar33.Position, axisDeg, centre, axisDeg + 120.0, lengthFt);
        output.WriteLine($"B738 centred 20 ft past: straight {straightFt:F1} ft, nosed 120° off {turnedBackFt:F1} ft");

        Assert.Equal(20.0 - (lengthFt / 2.0), straightFt, 0.1);
        Assert.Equal(20.0 - (lengthFt / 4.0), turnedBackFt, 0.1);
        Assert.True(turnedBackFt < RunwayTailClearance.MarginFt, $"a B738 nosed back over the line counts as {turnedBackFt:F1} ft past it");
    }

    /// <summary>
    /// R3's wording: the pilot asks for taxi instructions when the far taxiway has no room to clear the runway. No real layout
    /// we carry has a too-short far taxiway, so only the builder's text is pinned.
    /// </summary>
    [Fact]
    public void NoRoomToClear_TerminalAndSpoken()
    {
        var aircraft = new AircraftState { Callsign = Issue1334OakDToFAcross33Tests.Callsign, AircraftType = "C172" };
        PilotSpeechText speech = PilotResponder.BuildUnableNoRoomToClearRunway(aircraft, "C2", "17R/35L");
        output.WriteLine($"terminal: {speech.Terminal} — spoken: {speech.Tts}");

        Assert.Equal("Unable, no room on C2 to clear 17R/35L, request taxi instructions", speech.Terminal);
        Assert.EndsWith(", unable, no room on charlie two to clear the runway, request taxi instructions", speech.Tts);
        Assert.Equal(speech.Terminal, SegmentExpander.NoRoomToClearRunwayReason("C2", "17R/35L"));
    }

    /// <summary>
    /// Asserts the route ends on <paramref name="far"/>'s taxiway with the <paramref name="aircraftType"/>'s tail at least the
    /// margin past <paramref name="far"/>'s runway's far hold line (<see cref="TailPastHoldLineFt"/>), and the end no more than
    /// that plus the longest arc after the bar (an arc is taken whole), and within 1 ft of half the length plus the margin
    /// along the path on the near-straight taxiways pinned here. Returns how far along the path past the far bar the end lies.
    /// </summary>
    internal static double AssertEndsClearPast(
        ITestOutputHelper output,
        TaxiRoute route,
        AirportGroundLayout layout,
        (string Runway, string Taxiway) far,
        string aircraftType
    )
    {
        (string runway, string farTaxiway) = far;
        output.WriteLine(
            $"route: {route.FormatTaxiwaySequence()} nodes "
                + string.Join(",", route.Segments.Select(s => s.FromNodeId).Append(route.Segments[^1].ToNodeId))
                + $" warnings [{string.Join("; ", route.Warnings)}]"
        );
        int farBarIdx = route.Segments.FindIndex(s => IsBarOnTaxiway(layout, s.ToNodeId, runway, farTaxiway));
        Assert.True(farBarIdx >= 0, $"the route never reaches {farTaxiway}'s bar");
        GroundNode farBar = layout.Nodes[route.Segments[farBarIdx].ToNodeId];
        GroundNode end = route.Segments[^1].Edge.ToNode;
        double needFt = ClearFt(aircraftType);
        double alongFt = route.Segments.Skip(farBarIdx + 1).Sum(s => s.Edge.DistanceNm) * GeoMath.FeetPerNm;
        double tailFt = TailPastHoldLineFt(route.Segments[farBarIdx].Edge, route.Segments[^1].Edge, AircraftLength.ResolveFt(aircraftType));
        output.WriteLine(
            $"{aircraftType} ends at #{end.Id}: tail {tailFt:F2} ft past the hold line at the far bar #{farBar.Id}, "
                + $"{alongFt:F2} ft past it along the path ({needFt:F2} ft along a straight taxiway)"
        );

        Assert.False(AirportGroundLayout.HasRunwayCenterlineEdge(end), $"the route ends on the runway at #{end.Id}");
        Assert.True(tailFt >= RunwayTailClearance.MarginFt - 0.01, $"the tail ends only {tailFt:F2} ft past {farTaxiway}'s {runway} hold line");
        double arcAllowanceFt = route
            .Segments.Skip(farBarIdx + 1)
            .Where(s => s.Edge.Edge is GroundArc)
            .Select(s => s.Edge.DistanceNm * GeoMath.FeetPerNm)
            .DefaultIfEmpty(0.0)
            .Max();
        Assert.True(
            tailFt <= RunwayTailClearance.MarginFt + arcAllowanceFt + 1.0,
            $"the tail ends {tailFt:F2} ft past {farTaxiway}'s {runway} hold line, beyond the margin (plus {arcAllowanceFt:F0} ft of arc)"
        );
        double bendDeg = Math.Abs(
            GeoMath.SignedBearingDifference(route.Segments[farBarIdx].Edge.ArrivalBearing, route.Segments[^1].Edge.ArrivalBearing)
        );
        output.WriteLine($"heading at the end is {bendDeg:F2}° off the taxiway's axis at the bar");
        Assert.True(
            Math.Abs(alongFt - needFt) <= 1.0,
            $"the end lies {alongFt:F2} ft past {farTaxiway}'s bar along the path, not within 1 ft of {needFt:F2} ft (bend {bendDeg:F1}°)"
        );
        if (VirtualNode.IsVirtualNode(end))
        {
            Assert.InRange(tailFt, RunwayTailClearance.MarginFt - 0.01, RunwayTailClearance.MarginFt + 0.01);
        }

        Assert.Equal(farTaxiway, route.Segments[^1].TaxiwayName);
        return alongFt;
    }

    /// <summary>
    /// How far, in feet, the tail (half <paramref name="lengthFt"/> behind the end, along the heading the route arrives at it)
    /// lies past the hold line through the far bar, the line across the taxiway's axis as the route arrives at the bar,
    /// measured perpendicular to that line.
    /// </summary>
    private static double TailPastHoldLineFt(DirectionalEdge toFarBar, DirectionalEdge toEnd, double lengthFt)
    {
        LatLon bar = toFarBar.ToNode.Position;
        double axisDeg = toFarBar.ArrivalBearing;
        LatLon endPos = toEnd.ToNode.Position;
        LatLon tail = SegmentExpander.ProjectAlong(endPos, toEnd.ArrivalBearing + 180.0, lengthFt / 2.0);
        double distanceFt = GeoMath.DistanceNm(bar, tail) * GeoMath.FeetPerNm;
        return distanceFt * Math.Cos((GeoMath.BearingTo(bar, tail) - axisDeg) * Math.PI / 180.0);
    }

    private static void AssertNoRunwayLeg(string? text)
    {
        Assert.DoesNotContain(" on 17R", text);
        Assert.DoesNotContain(" on 35L", text);
        Assert.DoesNotContain(" on 33", text);
        Assert.DoesNotContain("RWY", text);
        Assert.DoesNotContain("intersection", text);
    }

    private sealed record TaxiStart(AirportGroundLayout Layout, GroundNode Node, double HeadingDeg);

    /// <summary>The far node of COS <paramref name="fromTaxiway"/> from its 17R/35L join, nosed toward the join.</summary>
    private TaxiStart? CosStart(string fromTaxiway)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("COS");
        if (layout is null)
        {
            return null;
        }

        string runway = Issue1334OakDToFAcross33Tests.RunwayCenterline(layout, "35L");
        (GroundNode start, GroundNode join) = Issue1334OakDToFAcross33Tests.StartAwayFromRunwayJoin(output, layout, "COS", fromTaxiway, runway);
        return new TaxiStart(layout, start, GeoMath.BearingTo(start.Position, join.Position));
    }

    /// <summary><paramref name="navDb"/> reading a KCOS sidecar that declares A2/C2 a crossing of 17R/35L.</summary>
    private static NavigationDatabase CosDeclaring(NavigationDatabase navDb) =>
        navDb.WithAirportSidecars(
            new AirportSidecarCatalog([
                new AirportSidecar("KCOS") { CrossingPairs = [new CrossingPair("A2", "C2", "17R/35L", "declared by the test")] },
            ])
        );

    private static bool IsCosBarOnTaxiway(AirportGroundLayout layout, int nodeId, string taxiway) => IsBarOnTaxiway(layout, nodeId, "35L", taxiway);

    private static bool IsBarOnTaxiway(AirportGroundLayout layout, int nodeId, string runway, string taxiway) =>
        layout.Nodes.TryGetValue(nodeId, out GroundNode? node)
        && (node.Type == GroundNodeType.RunwayHoldShort)
        && (node.RunwayId?.Contains(runway) == true)
        && node.Edges.Any(e => e.MatchesTaxiway(taxiway));
}
