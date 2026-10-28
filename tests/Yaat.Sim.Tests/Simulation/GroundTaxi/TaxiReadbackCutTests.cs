using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The TAXI readback is read off the resolved route: the taxiways up to the first hold short that stops the aircraft, the
/// runways crossed before it (implicitly or by auto-cross) as "cross runway X and runway Y", then that hold short, and
/// nothing past it (7110.65 3-7-2.b order: route, crossings, hold short). A runway assignment still leads. At KOAK the
/// aircraft holds short of 28L at the south B bar, facing the runway, so a directed TAXI crosses 28L implicitly.
/// </summary>
public class TaxiReadbackCutTests(ITestOutputHelper output)
{
    private const string SpokenCallsign = "november one two five nine.";

    /// <summary>
    /// The callsign abbreviated as a pilot may speak it once the controller has (AIM 4-2-4.a.2): a seeded draw
    /// (<c>SpokenFormDraws.DrawShortCallsign</c>), so a readback may carry either form.
    /// </summary>
    private const string AbbreviatedSpokenCallsign = "november two five nine.";

    [Fact]
    public void TaxiBC_From28LBar_28RHeld_ReadsBackToThe28RHoldShort()
    {
        string? readback = ReadbackFrom28LBar(AutoCrossRunwayMode.Off, "TAXI B C");
        AssertReadback("taxi via bravo, cross runway two eight left, hold short of runway two eight right", readback);
    }

    /// <summary>
    /// KOAK 30 is on the south field, so from the south B bar at 28L the route runs back down B and ends short of W, which
    /// the clearance left out: that route-incomplete hold is the cut, and the runway assignment still leads.
    /// </summary>
    [Fact]
    public void TaxiB30_From28LBar_KeepsTheRunwayAssignment_CutsAtTheRouteIncompleteHold()
    {
        string? readback = ReadbackFrom28LBar(AutoCrossRunwayMode.Off, "TAXI B 30");
        AssertReadback("runway three zero, taxi via bravo, hold short of taxiway whiskey", readback);
    }

    [Fact]
    public void TaxiBC_From28LBar_28RAutoCrossed_ReadsBothCrossings()
    {
        string? readback = ReadbackFrom28LBar(AutoCrossRunwayMode.All, "TAXI B C");
        AssertReadback("taxi via bravo, charlie, cross runway two eight left and runway two eight right", readback);
    }

    /// <summary>
    /// KOAK spot E on TE, facing the TE/T junction: <c>TAXI TE T U HS T</c> holds short of T at the junction, where the route
    /// ends at the T bar. The readback stops at that hold short.
    /// </summary>
    [Fact]
    public void TaxiTeTUHsT_FromSpotE_ReadsBackToTheTaxiwayHoldShort()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        SimulationEngine engine = BareTaxiFirstStepHeadingTests.BuildOakEngine(groundData, AutoCrossRunwayMode.All);
        GroundNode spot = layout.FindSpotNodeByName("E")!;
        GroundNode junction = layout.FindIntersectionNode("TE", "T")!;
        GroundNode toward = spot.Edges.Select(e => e.OtherNode(spot)).MinBy(n => GeoMath.DistanceNm(n.Position, junction.Position))!;
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            new SfoGround(engine, layout),
            "N1259",
            "B738",
            (spot, new TrueHeading(GeoMath.BearingTo(spot.Position, toward.Position))),
            new HoldingInPositionPhase()
        );

        string? readback = IssueAndReadBack(engine, aircraft, "TAXI TE T U HS T");
        AssertReadback("taxi via tango echo, hold short of taxiway tango", readback);
    }

    /// <summary>
    /// Released at the 28R bar after <c>TAXI B C</c> from the 28L bar, the aircraft crosses and goes on along its queued
    /// route, so the readback is the crossing and the route past it, up to the next stop (none on B C).
    /// </summary>
    [Fact]
    public void CrossAt28RBar_AfterTaxiBC_ReadsTheQueuedPath()
    {
        (SimulationEngine engine, AircraftState aircraft) = HoldingShortOf28RAfter("TAXI B C");
        string? readback = IssueAndReadBack(engine, aircraft, "CROSS 28R");
        AssertReadback("cross runway two eight right, continue taxi via bravo, charlie", readback);
    }

    /// <summary>
    /// The clearance's taxiways survive a snapshot round trip: restored while holding short of 28R, the release still
    /// names only B and the issued C past the route's end.
    /// </summary>
    [Fact]
    public void CrossAt28RBar_AfterSnapshotRestore_StillNamesOnlyTheClearanceTaxiways()
    {
        (SimulationEngine engine, AircraftState aircraft) = HoldingShortOf28RAfter("TAXI B C");
        string snapshotJson = JsonSerializer.Serialize(engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        StateSnapshotDto snapshot = Assert.IsType<StateSnapshotDto>(
            JsonSerializer.Deserialize<StateSnapshotDto>(snapshotJson, RecordingJsonOptions.Default)
        );

        SimulationEngine restoredEngine = BareTaxiFirstStepHeadingTests.BuildOakEngine(new TestAirportGroundData(), AutoCrossRunwayMode.Off);
        restoredEngine.RestoreFromSnapshot(snapshot);
        AircraftState restored = Assert.IsType<AircraftState>(restoredEngine.FindAircraft(aircraft.Callsign));
        Assert.Equal(["B", "C"], Assert.IsType<TaxiRoute>(restored.Ground.AssignedTaxiRoute).ClearanceTaxiways);

        string? readback = IssueAndReadBack(restoredEngine, restored, "CROSS 28R");
        AssertReadback("cross runway two eight right, continue taxi via bravo, charlie", readback);
    }

    /// <summary>
    /// A route that does not know its clearance's taxiways (restored from a snapshot written without them) reads every leg
    /// it drives past the bar, and no issued taxiways past its end.
    /// </summary>
    [Fact]
    public void RouteRestoredWithoutClearanceTaxiways_ReadsEveryLeg()
    {
        (SimulationEngine engine, AircraftState aircraft) = HoldingShortOf28RAfter("TAXI B C");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        route.ClearanceTaxiways = null;
        string? readback = IssueAndReadBack(engine, aircraft, "CROSS 28R");
        AssertReadback("cross runway two eight right, continue taxi via bravo", readback);
    }

    /// <summary>
    /// The route's clearance taxiways are left out of its snapshot while unknown, so every snapshot without them serializes
    /// as before, and a known list round-trips in clearance order.
    /// </summary>
    [Fact]
    public void TaxiRouteDto_NullClearanceTaxiways_WritesNoProperty()
    {
        TestVnasData.EnsureInitialized();
        if (new TestAirportGroundData().GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        var unknown = new TaxiRoute { Segments = [], HoldShortPoints = [] };
        string unknownJson = JsonSerializer.Serialize(unknown.ToSnapshot(), RecordingJsonOptions.Default);
        Assert.DoesNotContain(nameof(TaxiRouteDto.ClearanceTaxiways), unknownJson, StringComparison.OrdinalIgnoreCase);
        TaxiRouteDto unknownDto = Assert.IsType<TaxiRouteDto>(JsonSerializer.Deserialize<TaxiRouteDto>(unknownJson, RecordingJsonOptions.Default));
        Assert.Null(Assert.IsType<TaxiRoute>(TaxiRoute.FromSnapshot(unknownDto, layout)).ClearanceTaxiways);

        var named = new TaxiRoute
        {
            Segments = [],
            HoldShortPoints = [],
            ClearanceTaxiways = ["B", "C"],
        };
        string namedJson = JsonSerializer.Serialize(named.ToSnapshot(), RecordingJsonOptions.Default);
        TaxiRouteDto namedDto = Assert.IsType<TaxiRouteDto>(JsonSerializer.Deserialize<TaxiRouteDto>(namedJson, RecordingJsonOptions.Default));
        Assert.Equal(["B", "C"], Assert.IsType<TaxiRoute>(TaxiRoute.FromSnapshot(namedDto, layout)).ClearanceTaxiways);
    }

    [Fact]
    public void ResAt28RBar_AfterTaxiBC_ReadsTheCrossingAndTheQueuedPath()
    {
        (SimulationEngine engine, AircraftState aircraft) = HoldingShortOf28RAfter("TAXI B C");
        string? readback = IssueAndReadBack(engine, aircraft, "RES");
        AssertReadback("cross runway two eight right, continue taxi via bravo, charlie", readback);
    }

    [Fact]
    public void CrossExpAt28RBar_AfterTaxiBC_ReadsWithoutDelayAfterTheCrossing()
    {
        (SimulationEngine engine, AircraftState aircraft) = HoldingShortOf28RAfter("TAXI B C");
        string? readback = IssueAndReadBack(engine, aircraft, "CROSS 28R EXP");
        AssertReadback("cross runway two eight right, without delay, continue taxi via bravo, charlie", readback);
    }

    /// <summary>A <c>CROSS 28R</c> while still rolling toward the 28R bar pre-clears it, and reads the route past it.</summary>
    [Fact]
    public void Cross28RWhileRolling_AfterTaxiBC_ReadsTheQueuedPathPastThe28RBar()
    {
        (SimulationEngine engine, AircraftState aircraft) = StageAt28LBar(AutoCrossRunwayMode.Off);
        IssueAndReadBack(engine, aircraft, "TAXI B C");
        for (int t = 0; t < 5; t++)
        {
            engine.TickOneSecond();
        }

        Assert.IsNotType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.True(aircraft.GroundSpeed > 0, $"not rolling after 5 s: {aircraft.GroundSpeed} kt in {aircraft.Phases?.CurrentPhase?.Name}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        Assert.Contains(route.HoldShortPoints, hs => HoldShortAnnotator.TargetMatches(hs.TargetName, "28R") && !hs.IsCleared);

        string? readback = IssueAndReadBack(engine, aircraft, "CROSS 28R");
        AssertReadback("cross runway two eight right, continue taxi via bravo, charlie", readback);
    }

    /// <summary>
    /// Rolling on <c>TAXI B C</c> toward the 28L bar, still uncleared, a <c>CROSS 28R</c> pre-clears the 28R bar beyond it: 28L
    /// stops the aircraft first, so nothing is read on past 28R and the readback is the command's own.
    /// </summary>
    [Fact]
    public void Cross28RWhileRollingTowardAnUnclearedBar_KeepsTheCommandsReadback()
    {
        (SimulationEngine engine, AircraftState aircraft) = StageShortOf28LBar();
        IssueAndReadBack(engine, aircraft, "TAXI B C");
        for (int t = 0; t < 5; t++)
        {
            engine.TickOneSecond();
        }

        Assert.True(aircraft.GroundSpeed > 0, $"not rolling after 5 s: {aircraft.GroundSpeed} kt in {aircraft.Phases?.CurrentPhase?.Name}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        HoldShortPoint? next = route.NextUnclearedHoldShortAhead();
        Assert.True(HoldShortAnnotator.TargetMatches(next?.TargetName, "28L"), $"the next uncleared hold short ahead is {next?.TargetName}, not 28L");

        aircraft.PendingPilotTransmissions.Clear();
        CommandResult cross = engine.SendCommand(aircraft.Callsign, "CROSS 28R");
        Assert.True(cross.Success, cross.Message);
        Assert.Null(cross.PilotReadback);
        string? readback = aircraft.PendingPilotTransmissions.FirstOrDefault(t => t.Kind == PilotTransmissionKind.Readback)?.SpeechText;
        output.WriteLine($"readback: {readback}");
        Assert.NotNull(readback);
        Assert.DoesNotContain("continue taxi", readback);
    }

    /// <summary>
    /// <c>TAXI B C, CROSS 28R</c> in one transmission from the 28L bar: the 28R crossing the CROSS pre-clears is read in the
    /// TAXI's route readback, rebuilt once the whole block has run, and the CROSS adds no clause of its own.
    /// </summary>
    [Fact]
    public void TaxiBCCross28R_InOneBlock_ReadsTheCrossingInTheRouteReadback()
    {
        string? readback = ReadbackFrom28LBar(AutoCrossRunwayMode.Off, "TAXI B C, CROSS 28R");
        AssertReadback("taxi via bravo, charlie, cross runway two eight left and runway two eight right", readback);
    }

    /// <summary>
    /// <c>CROSS 28L</c> beside <c>TAXI B C</c> from the 28L bar names the crossing the TAXI already reads (implicit from the
    /// hold), so it is covered and 28L is read once.
    /// </summary>
    [Fact]
    public void TaxiBCCross28L_FromThe28LBar_ReadsThe28LCrossingOnce()
    {
        string? readback = ReadbackFrom28LBar(AutoCrossRunwayMode.Off, "TAXI B C, CROSS 28L");
        AssertReadback("taxi via bravo, cross runway two eight left, hold short of runway two eight right", readback);
    }

    /// <summary>With auto-cross All the TAXI already reads the 28R crossing, so a <c>CROSS 28R</c> beside it is covered.</summary>
    [Fact]
    public void TaxiBCCross28R_AutoCrossAll_ReadsThe28RCrossingOnce()
    {
        string? readback = ReadbackFrom28LBar(AutoCrossRunwayMode.All, "TAXI B C, CROSS 28R");
        AssertReadback("taxi via bravo, charlie, cross runway two eight left and runway two eight right", readback);
    }

    /// <summary>
    /// Short of the uncleared 28L bar, the TAXI readback stops at 28L, before the 28R bar the <c>CROSS</c> pre-clears: the
    /// readback does not read that crossing, so the <c>CROSS</c> keeps its own clause.
    /// </summary>
    [Fact]
    public void TaxiBCCross28R_ShortOfThe28LBar_KeepsThe28RCrossingClause()
    {
        (SimulationEngine engine, AircraftState aircraft) = StageShortOf28LBar();
        string? readback = IssueAndReadBack(engine, aircraft, "TAXI B C, CROSS 28R");
        AssertReadback("taxi via bravo, hold short of runway two eight left, cross runway two eight right", readback);
    }

    /// <summary>
    /// An expedited <c>CROSS</c> is covered like any other: the rebuilt TAXI readback reads the 28R crossing once, in its own
    /// clause with its "without delay" (7110.65 §3-7-2.j), and never the stale hold short of 28R.
    /// </summary>
    [Fact]
    public void TaxiBCCross28RExp_FromThe28LBar_ReadsTheExpeditedCrossingOnce()
    {
        string? readback = ReadbackFrom28LBar(AutoCrossRunwayMode.Off, "TAXI B C, CROSS 28R EXP");
        AssertReadback("taxi via bravo, charlie, cross runway two eight left, cross runway two eight right without delay", readback);
    }

    /// <summary>
    /// Short of the uncleared 28L bar, the TAXI readback stops at 28L, before the 28R bar the expedited <c>CROSS</c>
    /// pre-clears, so the <c>CROSS</c> keeps its own clause, "without delay" and all.
    /// </summary>
    [Fact]
    public void TaxiBCCross28RExp_ShortOfThe28LBar_KeepsItsOwnClause()
    {
        (SimulationEngine engine, AircraftState aircraft) = StageShortOf28LBar();
        string? readback = IssueAndReadBack(engine, aircraft, "TAXI B C, CROSS 28R EXP");
        AssertReadback("taxi via bravo, hold short of runway two eight left, cross runway two eight right without delay", readback);
    }

    /// <summary>
    /// With auto-cross All, a covered <c>CROSS 28L EXP</c> reads the expedited 28L crossing in its own clause, in route order
    /// before the auto-crossed 28R.
    /// </summary>
    [Fact]
    public void TaxiBCCross28LExp_AutoCrossAll_ReadsTheExpeditedRunwayInRouteOrder()
    {
        string? readback = ReadbackFrom28LBar(AutoCrossRunwayMode.All, "TAXI B C, CROSS 28L EXP");
        AssertReadback("taxi via bravo, charlie, cross runway two eight left without delay, cross runway two eight right", readback);
    }

    /// <summary>
    /// The rebuild reads a bar an uncovered command of the block cleared as uncleared: on the <c>TAXI B C</c> route from the
    /// 28L bar, with 28L cleared as by a covered command and the 28R bar cleared as by an uncovered one, the rebuilt readback
    /// crosses 28L and ends silently at 28R, with no hold short of it, leaving the 28R crossing to the uncovered command's own
    /// clause.
    /// </summary>
    [Fact]
    public void UncoveredSiblingCross_ItsBarReadsAsUncleared_InTheRebuild()
    {
        (SimulationEngine engine, AircraftState aircraft) = StageAt28LBar(AutoCrossRunwayMode.Off);
        CommandResult taxi = engine.SendCommand(aircraft.Callsign, "TAXI B C");
        Assert.True(taxi.Success, taxi.Message);
        GroundCommandHandler.TaxiRouteReadbackSource source = Assert.IsType<GroundCommandHandler.TaxiRouteReadbackSource>(taxi.TaxiRouteReadback);
        HoldShortPoint bar28L = Assert.Single(source.Route.HoldShortPoints, hs => HoldShortAnnotator.TargetMatches(hs.TargetName, "28L"));
        HoldShortPoint bar28R = Assert.Single(source.Route.HoldShortPoints, hs => HoldShortAnnotator.TargetMatches(hs.TargetName, "28R"));
        bar28L.IsCleared = true;
        bar28R.IsCleared = true;

        HandlerReadback rebuilt = Assert.IsType<HandlerReadback>(GroundCommandHandler.RebuildRouteReadback(source, ["28L"], [], [bar28R]));

        output.WriteLine($"rebuilt: {rebuilt.Speech.Tts}");
        Assert.Equal("taxi via bravo, cross runway two eight left", rebuilt.Speech.Tts);
    }

    /// <summary>A covered <c>CROSS 10L</c> picks the end the 28R/10L crossing is read as.</summary>
    [Fact]
    public void TaxiBCCross10L_ReadsTheNamedRunwayEnd()
    {
        string? readback = ReadbackFrom28LBar(AutoCrossRunwayMode.Off, "TAXI B C, CROSS 10L");
        AssertReadback("taxi via bravo, charlie, cross runway two eight left and runway one zero left", readback);
    }

    /// <summary>
    /// Two <c>CROSS</c> beside the TAXI, the expedited one first: both are covered, and the rebuilt TAXI readback reads each
    /// crossing once, in route order, the expedited 28R in its own "without delay" clause.
    /// </summary>
    [Fact]
    public void TaxiBCCross28RExpCross28L_FromThe28LBar_ReadsEachCrossingOnce()
    {
        string? readback = ReadbackFrom28LBar(AutoCrossRunwayMode.Off, "TAXI B C, CROSS 28R EXP, CROSS 28L");
        AssertReadback("taxi via bravo, charlie, cross runway two eight left, cross runway two eight right without delay", readback);
    }

    /// <summary>
    /// An expedited TAXI with a covered expedited <c>CROSS</c>: the crossings join as plain ones and the TAXI's single trailing
    /// "without delay" covers them.
    /// </summary>
    [Fact]
    public void TaxiBCExpCross28RExp_FromThe28LBar_SaysWithoutDelayOnce()
    {
        string? readback = ReadbackFrom28LBar(AutoCrossRunwayMode.Off, "TAXI B C EXP, CROSS 28R EXP");
        AssertReadback("taxi via bravo, charlie, cross runway two eight left and runway two eight right, without delay", readback);
    }

    /// <summary>An expedited TAXI with a covered plain <c>CROSS 28L</c> says "without delay" once and reads 28L once.</summary>
    [Fact]
    public void TaxiBCExpCross28L_FromThe28LBar_SaysWithoutDelayOnce()
    {
        string? readback = ReadbackFrom28LBar(AutoCrossRunwayMode.Off, "TAXI B C EXP, CROSS 28L");
        AssertReadback("taxi via bravo, cross runway two eight left, without delay, hold short of runway two eight right", readback);
    }

    /// <summary>
    /// A <c>CROSS</c> naming a taxiway releases a taxiway hold short ("continue taxi"), which no route readback reads, so it is
    /// never covered, alone or beside a runway, and keeps its own clause. The <c>TAXI B C</c> route from the 28L bar holds short
    /// of no taxiway, so this reads what <c>CROSS C</c> and <c>CROSS 28L C</c> name directly.
    /// </summary>
    [Fact]
    public void TaxiBCCrossTaxiwayRelease_IsNeverCovered()
    {
        TestVnasData.EnsureInitialized();
        CrossRunwayCommand taxiwayOnly = ParseCross("CROSS C");
        Assert.Equal(["C"], taxiwayOnly.RunwayIds);
        Assert.Empty(GroundCommandHandler.RunwaysNamed(taxiwayOnly.RunwayIds, taxiwayOnly.HoldShorts));
        Assert.Null(GroundCommandHandler.CoverableRunways(taxiwayOnly));

        CrossRunwayCommand mixed = ParseCross("CROSS 28L C");
        Assert.Equal(["28L", "C"], mixed.RunwayIds);
        Assert.Equal(["28L"], GroundCommandHandler.RunwaysNamed(mixed.RunwayIds, mixed.HoldShorts));
        Assert.Null(GroundCommandHandler.CoverableRunways(mixed));

        static CrossRunwayCommand ParseCross(string text)
        {
            CompoundCommand compound = Assert.IsType<CompoundCommand>(CommandParser.ParseCompound(text).Value);
            return Assert.IsType<CrossRunwayCommand>(Assert.Single(Assert.Single(compound.Blocks).Commands));
        }
    }

    /// <summary>
    /// <c>CROSS 28L, TAXI B C</c> from the 28L bar: the CROSS before the TAXI follows the same rule. 28L is cleared on the TAXI's
    /// route and read before its first stop, so the CROSS is covered and the readback is the TAXI's route readback alone.
    /// </summary>
    [Fact]
    public void CrossBeforeTaxi_InOneBlock_FollowsTheCoverRule()
    {
        string? readback = ReadbackFrom28LBar(AutoCrossRunwayMode.Off, "CROSS 28L, TAXI B C");
        AssertReadback("taxi via bravo, cross runway two eight left, hold short of runway two eight right", readback);
    }

    /// <summary>
    /// A covered <c>TAXI B C, CROSS 28R</c> reads exactly as a lone <c>TAXI B C</c> giving the same route readback, in every
    /// band, personality, seed and second swept: the dropped CROSS counts as no clause, so the readback is neither chained nor
    /// more than one command, and takes the lone TAXI's flavour.
    /// </summary>
    [Fact]
    public void TaxiBCCross28R_Covered_ReadsAsALoneTaxi()
    {
        (_, AircraftState aircraft) = StageAt28LBar(AutoCrossRunwayMode.Off);
        CompoundCommand coveredCompound = Assert.IsType<CompoundCommand>(CommandParser.ParseCompound("TAXI B C, CROSS 28R").Value);
        CompoundCommand loneCompound = Assert.IsType<CompoundCommand>(CommandParser.ParseCompound("TAXI B C").Value);
        var route = new HandlerReadback(
            CanonicalCommandType.Taxi,
            new PilotSpeechText(
                "taxi via B C, cross runway 28L and runway 28R",
                "taxi via bravo, charlie, cross runway two eight left and runway two eight right"
            )
        );
        var covered = new CommandResult(true) { PilotReadback = route with { Covers = [coveredCompound.Blocks[0].Commands[1]] } };
        var lone = new CommandResult(true) { PilotReadback = route };

        foreach (FrequencyLoadBand band in Enum.GetValues<FrequencyLoadBand>())
        {
            foreach (PilotPersonality personality in Enum.GetValues<PilotPersonality>())
            {
                for (int seed = 0; seed < 40; seed++)
                {
                    var context = new PilotSpeechContext(personality, band, seed, seed % 7)
                    {
                        ShortenCallsign = false,
                        GroupContactFrequency = false,
                        CurrentIndicatedAirspeed = null,
                    };
                    PilotSpeechText? fromLone = PilotResponder.BuildReadbackAsApplied(loneCompound, lone, aircraft, context);
                    PilotSpeechText? fromCovered = PilotResponder.BuildReadbackAsApplied(coveredCompound, covered, aircraft, context);
                    Assert.NotNull(fromLone);
                    Assert.Equal(fromLone, fromCovered);
                }
            }
        }
    }

    /// <summary>
    /// The general block path (<see cref="CommandDispatcher.BuildApplyAction"/>, a block applied by the queue rather than the
    /// tower driver), driven directly on the real KOAK layout: <c>TAXI B C, CROSS 28L</c> from the 28L bar gives the TAXI's
    /// route readback covering the CROSS instance.
    /// </summary>
    [Fact]
    public void BlockApplyAction_TaxiBCCross28L_CoversTheCrossInstance()
    {
        (_, AircraftState aircraft) = StageAt28LBar(AutoCrossRunwayMode.Off);
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(aircraft.Ground.Layout);
        List<ParsedCommand> commands = Assert.IsType<CompoundCommand>(CommandParser.ParseCompound("TAXI B C, CROSS 28L").Value).Blocks[0].Commands;
        Func<AircraftState, bool, CommandResult> apply = CommandDispatcher.BuildApplyAction(
            commands,
            TestDispatch.Context(new SerializableRandom(42), groundLayout: layout),
            new CommandBlock()
        );

        CommandResult result = apply(aircraft, false);

        Assert.True(result.Success, result.Message);
        HandlerReadback readback = Assert.IsType<HandlerReadback>(result.PilotReadback);
        output.WriteLine($"readback: {readback.Speech.Tts}");
        Assert.Same(commands[1], Assert.Single(readback.Covers));
        Assert.Equal("taxi via bravo, cross runway two eight left, hold short of runway two eight right", readback.Speech.Tts);
    }

    /// <summary>A TAXIAUTO names no taxiways of its own, so its route leaves the clearance's taxiways unknown.</summary>
    [Fact]
    public void TaxiAuto_LeavesTheClearanceTaxiwaysUnknown()
    {
        (SimulationEngine engine, AircraftState aircraft) = StageAt28LBar(AutoCrossRunwayMode.Off);
        CommandResult taxi = engine.SendCommand(aircraft.Callsign, "TAXIAUTO 30");
        Assert.True(taxi.Success, $"'TAXIAUTO 30' was refused: {taxi.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.Null(route.ClearanceTaxiways);
    }

    /// <summary>
    /// Spot E staging: <c>TAXI TE T U HS T</c> ends at the T bar, so a bare <c>RES</c> there has no queued route past the
    /// bar to read, and the readback is the command's own, as before.
    /// </summary>
    [Fact]
    public void ResAtTaxiwayBar_RouteEndsAtTheBar_KeepsTodaysReadback()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return;
        }

        SimulationEngine engine = BareTaxiFirstStepHeadingTests.BuildOakEngine(groundData, AutoCrossRunwayMode.All);
        GroundNode spot = layout.FindSpotNodeByName("E")!;
        GroundNode junction = layout.FindIntersectionNode("TE", "T")!;
        GroundNode toward = spot.Edges.Select(e => e.OtherNode(spot)).MinBy(n => GeoMath.DistanceNm(n.Position, junction.Position))!;
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            new SfoGround(engine, layout),
            "N1259",
            "B738",
            (spot, new TrueHeading(GeoMath.BearingTo(spot.Position, toward.Position))),
            new HoldingInPositionPhase()
        );

        IssueAndReadBack(engine, aircraft, "TAXI TE T U HS T");
        TickUntilHoldingShortOf(engine, aircraft, "T");
        string? readback = IssueAndReadBack(engine, aircraft, "RES");
        AssertReadback("resume taxi", readback);
    }

    /// <summary>Holding short of 28L at the south B bar, facing it, the aircraft is given <paramref name="command"/>.</summary>
    private string? ReadbackFrom28LBar(AutoCrossRunwayMode autoCross, string command)
    {
        (SimulationEngine engine, AircraftState aircraft) = StageAt28LBar(autoCross);
        return IssueAndReadBack(engine, aircraft, command);
    }

    /// <summary>From the 28L bar with auto-cross off, <paramref name="taxi"/> is issued and the aircraft taxis to the 28R bar.</summary>
    private (SimulationEngine Engine, AircraftState Aircraft) HoldingShortOf28RAfter(string taxi)
    {
        (SimulationEngine engine, AircraftState aircraft) = StageAt28LBar(AutoCrossRunwayMode.Off);
        IssueAndReadBack(engine, aircraft, taxi);
        TickUntilHoldingShortOf(engine, aircraft, "28R");
        return (engine, aircraft);
    }

    /// <summary>An engine with the aircraft holding short of 28L at the south B bar, facing it.</summary>
    private (SimulationEngine Engine, AircraftState Aircraft) StageAt28LBar(AutoCrossRunwayMode autoCross)
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return default;
        }

        SimulationEngine engine = BareTaxiFirstStepHeadingTests.BuildOakEngine(groundData, autoCross);
        BareTaxiFirstStepHeadingTests.BareTaxiStart start = BareTaxiFirstStepHeadingTests.HoldingShortOf28LOnB(layout, output);
        AircraftState aircraft = BareTaxiFirstStepHeadingTests.SpawnAt(layout, start.Position, start.Heading, start.Phase);
        engine.World.AddAircraft(aircraft);
        return (engine, aircraft);
    }

    /// <summary>
    /// An engine with auto-cross off and the aircraft stopped on B two nodes short of the south 28L bar, away from the runway,
    /// facing it.
    /// </summary>
    private (SimulationEngine Engine, AircraftState Aircraft) StageShortOf28LBar()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            Assert.Skip("no KOAK layout");
            return default;
        }

        SimulationEngine engine = BareTaxiFirstStepHeadingTests.BuildOakEngine(groundData, AutoCrossRunwayMode.Off);
        (GroundNode bar, _, double towardRunwayDeg) = BareTaxiFirstStepHeadingTests.Bars28LOnB(layout);
        double awayDeg = MagneticDeclination.NormalizeDeg(towardRunwayDeg + 180.0);
        List<GroundNode> behind = [.. TaxiStartsOnOccupiedTaxiwayTests.NodesAhead(bar, awayDeg, "B").Take(3)];
        Assert.True(behind.Count == 3, $"B runs {behind.Count - 1} nodes back from the 28L bar {bar.Id}, expected at least two");
        GroundNode start = behind[2];
        var heading = new TrueHeading(GeoMath.BearingTo(start.Position, behind[1].Position));
        output.WriteLine($"bar {bar.Id}, start on B node {start.Id}, heading {heading.Degrees:F1}");
        AircraftState aircraft = BareTaxiFirstStepHeadingTests.SpawnAt(layout, start.Position, heading, new HoldingInPositionPhase());
        engine.World.AddAircraft(aircraft);
        return (engine, aircraft);
    }

    /// <summary>
    /// Asserts the readback is <paramref name="body"/> and the callsign, spoken in full or abbreviated (a seeded draw,
    /// AIM 4-2-4.a.2).
    /// </summary>
    private static void AssertReadback(string body, string? readback) =>
        Assert.True(
            (readback == $"{body}, {SpokenCallsign}") || (readback == $"{body}, {AbbreviatedSpokenCallsign}"),
            $"expected '{body}, ' and the callsign in either form; got '{readback}'"
        );

    /// <summary>
    /// The continuation reads on to the departure runway's own bar: after <c>TAXI B C 33</c> from the 28L bar, a <c>CROSS
    /// 28R</c> at the 28R bar reads the route on C to runway 33, which it holds short of, and nothing past it.
    /// </summary>
    [Fact]
    public void CrossAt28RBar_AfterTaxiBC33_HoldsShortOfTheDepartureRunway()
    {
        (SimulationEngine engine, AircraftState aircraft) = HoldingShortOf28RAfter("TAXI B C 33");
        string? readback = IssueAndReadBack(engine, aircraft, "CROSS 28R");
        AssertReadback("cross runway two eight right, continue taxi via bravo, charlie, hold short of runway three three", readback);
    }

    /// <summary>Ticks until the aircraft holds short of <paramref name="target"/>, failing after 300 s.</summary>
    private static void TickUntilHoldingShortOf(SimulationEngine engine, AircraftState aircraft, string target)
    {
        for (int t = 0; t < 300; t++)
        {
            if (aircraft.Phases?.CurrentPhase is HoldingShortPhase hold && HoldShortAnnotator.TargetMatches(hold.HoldShort.TargetName, target))
            {
                return;
            }

            engine.TickOneSecond();
        }

        Assert.Fail($"not holding short of {target} after 300 s; phase {aircraft.Phases?.CurrentPhase?.Name}");
    }

    /// <summary>Issues <paramref name="command"/>, dumps the route and returns the spoken readback the solo pilot queued for it.</summary>
    private string? IssueAndReadBack(SimulationEngine engine, AircraftState aircraft, string command)
    {
        aircraft.PendingPilotTransmissions.Clear();
        CommandResult taxi = engine.SendCommand(aircraft.Callsign, command);
        Assert.True(taxi.Success, $"'{command}' was refused: {taxi.Message}");
        output.WriteLine($"message: {taxi.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        output.WriteLine(
            $"at segment {route.CurrentSegmentIndex}: {string.Join(" ", route.Segments.Select(s => $"{s.TaxiwayName}:{s.FromNodeId}>{s.ToNodeId}"))}"
        );
        PilotTransmission? readback = aircraft.PendingPilotTransmissions.FirstOrDefault(t => t.Kind == PilotTransmissionKind.Readback);
        output.WriteLine($"readback: {readback?.Text} / {readback?.SpeechText}");
        return readback?.SpeechText;
    }
}
