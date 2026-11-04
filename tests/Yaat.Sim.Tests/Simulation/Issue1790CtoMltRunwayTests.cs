using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// YAAT-1790 (KOAK): CAP481 holding short of 28R, its taxi cleared to 28R. <c>MLT 28L</c> was accepted and moved the
/// assigned runway to 28L; <c>CTO, MLT 28L</c> then read back "Cleared for takeoff runway 28R" while the aircraft crossed
/// 28R and departed 28L. A pattern runway on the ground is refused before a takeoff clearance, and a CTO chained with a
/// trailing MLT/MRT is the <c>CTO MLT 28L</c> modifier: the readback and the runway the aircraft departs agree.
/// </summary>
public class Issue1790CtoMltRunwayTests(ITestOutputHelper output)
{
    private const string Callsign = "CAP481";

    // On taxiway B just north of the 28R/10L hold line, nosed south-southeast along B (the fixture
    // MltDuringTaxiKeepsGroundRouteTests uses at the same bar).
    private const double SpawnLat = 37.725690;
    private const double SpawnLon = -122.204525;
    private const double SpawnHeadingTrue = 162.0;

    private static readonly string[] OakRunwayEnds = ["10L", "28R", "10R", "28L", "12", "30", "15", "33"];

    [Theory]
    [InlineData("MLT 28L")]
    [InlineData("MRT 28L")]
    [InlineData("MLT")]
    [InlineData("MRT")]
    public void MltOrMrt_HoldingShortBeforeTakeoffClearance_IsRefused(string command)
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = HoldingShortOf28R("C172");
        if (setup is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        TaxiRoute? routeBefore = aircraft.Ground.AssignedTaxiRoute;
        int holdShortsBefore = routeBefore?.HoldShortPoints.Count ?? -1;
        PatternDirection? directionBefore = aircraft.Pattern.TrafficDirection;
        RunwayInfo? departureRunwayBefore = aircraft.Phases?.DepartureRunway;
        RunwayInfo? patternRunwayBefore = aircraft.Phases?.PatternRunway;

        CommandResult result = engine.SendCommand(Callsign, command);
        output.WriteLine($"{command} -> {result.Success}: {result.Message}");

        Assert.False(result.Success);
        Assert.Equal($"Can't assign a traffic pattern before takeoff clearance; use CTO {command}", result.Message);
        Assert.Equal("28R", aircraft.Phases?.AssignedRunway?.Designator);
        Assert.Same(departureRunwayBefore, aircraft.Phases?.DepartureRunway);
        Assert.Same(patternRunwayBefore, aircraft.Phases?.PatternRunway);
        Assert.Equal(directionBefore, aircraft.Pattern.TrafficDirection);
        Assert.Same(routeBefore, aircraft.Ground.AssignedTaxiRoute);
        Assert.Equal(holdShortsBefore, aircraft.Ground.AssignedTaxiRoute?.HoldShortPoints.Count ?? -1);
        Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
    }

    [Fact]
    public void MltWithRunway_AfterLineUpAndWaitOnly_IsRefused()
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = HoldingShortOf28R("C172");
        if (setup is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        CommandResult luaw = engine.SendCommand(Callsign, "LUAW");
        Assert.True(luaw.Success, luaw.Message);

        CommandResult mlt = engine.SendCommand(Callsign, "MLT 28L");
        output.WriteLine($"MLT 28L after LUAW -> {mlt.Success}: {mlt.Message}");

        Assert.False(mlt.Success);
        Assert.Equal("Can't assign a traffic pattern before takeoff clearance; use CTO MLT 28L", mlt.Message);
        Assert.Equal("28R", aircraft.Phases?.AssignedRunway?.Designator);
        Assert.Null(aircraft.Phases?.DepartureRunway);
    }

    [Fact]
    public void CtoCommaMlt_DepartsTheHoldShortRunway_AsCtoMlt() => AssertChainedMatchesModifier("CTO, MLT 28L", "CTO MLT 28L");

    [Theory]
    [InlineData("CTO, MRT", "CTO MRT")]
    [InlineData("CTO; MRT", "CTO MRT")]
    public void CtoChainedBareTrafficDirection_DepartsTheHoldShortRunway_AsCtoModifier(string chained, string typed) =>
        AssertChainedMatchesModifier(chained, typed);

    [Fact]
    public void CtoSemicolonMlt_DepartsTheHoldShortRunway_AsCtoMlt() => AssertChainedMatchesModifier("CTO; MLT 28L", "CTO MLT 28L");

    [Fact]
    public void MltWithRunway_AfterTakeoffClearance_KeepsTheTakeoffRunway() =>
        AssertBuiltClearanceRestated("CTO", "MLT 28L", "28L", PatternDirection.Left);

    /// <summary>A circuit an earlier <c>CTO MLT</c> built is re-stated on another runway: takeoff 28R, then the 33 circuit.</summary>
    [Fact]
    public void CtoMlt_ThenMlt33OnTheGround_DepartsTheClearedRunway_FliesThe33Circuit() =>
        AssertBuiltClearanceRestated("CTO MLT", "MLT 33", "33", PatternDirection.Left);

    /// <summary>
    /// A bare traffic direction changes only the side (7110.65 3-10-1.a/b: direction and runway are separate items): the
    /// circuit a built <c>CTO MLT 28L</c> named stays on 28L.
    /// </summary>
    [Fact]
    public void CtoMlt28L_ThenBareMrt_KeepsThe28LCircuit()
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = HoldingShortOf28R("C172");
        if (setup is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        CommandResult cto = engine.SendCommand(Callsign, "CTO MLT 28L");
        Assert.True(cto.Success, cto.Message);

        CommandResult mrt = engine.SendCommand(Callsign, "MRT");
        output.WriteLine($"MRT after CTO MLT 28L -> {mrt.Success}: {mrt.Message}");
        Assert.True(mrt.Success, mrt.Message);
        Assert.Equal("Make right traffic, Runway 28L", mrt.Message);

        AssertDepartsThenFliesCircuit(engine, aircraft, "28L", PatternDirection.Right);
    }

    /// <summary>The same for a <c>CTO MLT 28L</c> stored while taxiing: the stored departure keeps its 28L circuit.</summary>
    [Fact]
    public void StoredCtoMlt28L_ThenBareMrt_KeepsThe28LCircuit()
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = TaxiingTo28R("C172");
        if (setup is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        CommandResult cto = engine.SendCommand(Callsign, "CTO MLT 28L");
        Assert.True(cto.Success, cto.Message);
        Assert.Equal(ClearanceType.ClearedForTakeoff, aircraft.Phases?.DepartureClearance?.Type);

        CommandResult mrt = engine.SendCommand(Callsign, "MRT");
        output.WriteLine($"MRT after stored CTO MLT 28L -> {mrt.Success}: {mrt.Message}");
        Assert.True(mrt.Success, mrt.Message);
        Assert.Equal("Make right traffic, Runway 28L", mrt.Message);

        AssertDepartsThenFliesCircuit(engine, aircraft, "28L", PatternDirection.Right);
    }

    /// <summary>A bare traffic direction on the same runway keeps the pattern altitude the stored clearance named.</summary>
    [Fact]
    public void StoredCtoMlt28L1500_ThenBareMrt_KeepsThePatternAltitude()
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = TaxiingTo28R("C172");
        if (setup is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        CommandResult cto = engine.SendCommand(Callsign, "CTO MLT 28L 1500");
        Assert.True(cto.Success, cto.Message);

        CommandResult mrt = engine.SendCommand(Callsign, "MRT");
        output.WriteLine($"MRT after stored CTO MLT 28L 1500 -> {mrt.Success}: {mrt.Message}");
        Assert.True(mrt.Success, mrt.Message);

        DownwindPhase? downwind = AssertDepartsThenFliesCircuit(engine, aircraft, "28L", PatternDirection.Right);
        Assert.Equal(1500.0, aircraft.Pattern.AltitudeOverrideFt);
        Assert.Equal(1500.0, downwind?.Waypoints?.PatternAltitude);
    }

    /// <summary>
    /// A re-state naming another runway drops the pattern altitude the clearance gave for the old one: <c>CTO MLT 1500</c>
    /// then <c>MLT 33</c> flies 33's own pattern altitude, the one a typed <c>CTO MLT 33</c> flies.
    /// </summary>
    [Fact]
    public void CtoMlt1500_ThenMlt33_FliesThe33PatternAltitude()
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = HoldingShortOf28R("C172");
        (SimulationEngine engine, AircraftState aircraft)? reference = HoldingShortOf28R("C172");
        if ((setup is null) || (reference is null))
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        CommandResult cto = engine.SendCommand(Callsign, "CTO MLT 1500");
        Assert.True(cto.Success, cto.Message);
        Assert.Equal(1500.0, aircraft.Pattern.AltitudeOverrideFt);

        CommandResult mlt = engine.SendCommand(Callsign, "MLT 33");
        output.WriteLine($"MLT 33 after CTO MLT 1500 -> {mlt.Success}: {mlt.Message}");
        Assert.True(mlt.Success, mlt.Message);

        CommandResult typed = reference.Value.engine.SendCommand(Callsign, "CTO MLT 33");
        Assert.True(typed.Success, typed.Message);

        Assert.Null(aircraft.Pattern.AltitudeOverrideFt);
        double restatedAltitude = FirstDownwind(aircraft).Waypoints!.PatternAltitude;
        double typedAltitude = FirstDownwind(reference.Value.aircraft).Waypoints!.PatternAltitude;
        output.WriteLine($"downwind altitude: restated {restatedAltitude:F0}, typed CTO MLT 33 {typedAltitude:F0}");
        Assert.Equal(typedAltitude, restatedAltitude);
        Assert.NotEqual(1500.0, restatedAltitude);
    }

    /// <summary>
    /// A built <c>CTO MLT 28L 1500</c> then a bare <c>MRT</c> keeps the 1,500 ft pattern altitude on 28L, and the re-stated
    /// departure keeps the altitude as typed.
    /// </summary>
    [Fact]
    public void CtoMlt28L1500_ThenBareMrt_KeepsThePatternAltitude()
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = HoldingShortOf28R("C172");
        if (setup is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        CommandResult cto = engine.SendCommand(Callsign, "CTO MLT 28L 1500");
        Assert.True(cto.Success, cto.Message);
        TakeoffPhase takeoff = Assert.IsType<TakeoffPhase>(aircraft.Phases?.Phases.FirstOrDefault(p => p is TakeoffPhase));
        ClosedTrafficDeparture original = Assert.IsType<ClosedTrafficDeparture>(takeoff.Departure);
        Assert.NotNull(original.PatternAltitudeText);

        CommandResult mrt = engine.SendCommand(Callsign, "MRT");
        output.WriteLine($"MRT after CTO MLT 28L 1500 -> {mrt.Success}: {mrt.Message}");
        Assert.True(mrt.Success, mrt.Message);
        Assert.Equal(original.PatternAltitudeText, Assert.IsType<ClosedTrafficDeparture>(takeoff.Departure).PatternAltitudeText);

        DownwindPhase? downwind = AssertDepartsThenFliesCircuit(engine, aircraft, "28L", PatternDirection.Right);
        Assert.Equal(1500.0, aircraft.Pattern.AltitudeOverrideFt);
        Assert.Equal(1500.0, downwind?.Waypoints?.PatternAltitude);
    }

    /// <summary>
    /// The re-stated stored departure keeps the pattern altitude as typed, so it describes as it did: after a bare <c>MRT</c>,
    /// a stored <c>CTO MLT 28L 1500</c> reads <c>CTO MRT 28L 1500</c>, not the altitude re-rendered from its feet.
    /// </summary>
    [Fact]
    public void StoredCtoMlt28L1500_ThenBareMrt_KeepsThePatternAltitudeText()
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = TaxiingTo28R("C172");
        if (setup is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        CommandResult cto = engine.SendCommand(Callsign, "CTO MLT 28L 1500");
        Assert.True(cto.Success, cto.Message);
        ClosedTrafficDeparture original = Assert.IsType<ClosedTrafficDeparture>(aircraft.Phases?.DepartureClearance?.Departure);
        Assert.NotNull(original.PatternAltitudeText);
        string describedBefore = CommandDescriber.DescribeCommand(new ClearedForTakeoffCommand(original));

        CommandResult mrt = engine.SendCommand(Callsign, "MRT");
        Assert.True(mrt.Success, mrt.Message);
        ClosedTrafficDeparture restated = Assert.IsType<ClosedTrafficDeparture>(aircraft.Phases?.DepartureClearance?.Departure);
        string describedAfter = CommandDescriber.DescribeCommand(new ClearedForTakeoffCommand(restated));
        output.WriteLine($"stored departure: before MRT '{describedBefore}', after '{describedAfter}'");

        Assert.Equal(original.PatternAltitudeText, restated.PatternAltitudeText);
        Assert.Equal(describedBefore.Replace("MLT", "MRT", StringComparison.Ordinal), describedAfter);
    }

    /// <summary>
    /// A stored <c>CTO MLT 28L 1500</c> re-stated on another runway drops the altitude given for 28L: <c>MLT 33</c> flies 33's
    /// own pattern altitude, the one a typed <c>CTO MLT 33</c> flies.
    /// </summary>
    [Fact]
    public void StoredCtoMlt28L1500_ThenMlt33_FliesThe33PatternAltitude()
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = TaxiingTo28R("C172");
        (SimulationEngine engine, AircraftState aircraft)? reference = HoldingShortOf28R("C172");
        if ((setup is null) || (reference is null))
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        CommandResult cto = engine.SendCommand(Callsign, "CTO MLT 28L 1500");
        Assert.True(cto.Success, cto.Message);

        CommandResult mlt = engine.SendCommand(Callsign, "MLT 33");
        output.WriteLine($"MLT 33 after stored CTO MLT 28L 1500 -> {mlt.Success}: {mlt.Message}");
        Assert.True(mlt.Success, mlt.Message);
        ClosedTrafficDeparture restated = Assert.IsType<ClosedTrafficDeparture>(aircraft.Phases?.DepartureClearance?.Departure);
        Assert.Null(restated.PatternAltitude);
        Assert.Null(restated.PatternAltitudeText);

        CommandResult typed = reference.Value.engine.SendCommand(Callsign, "CTO MLT 33");
        Assert.True(typed.Success, typed.Message);

        DownwindPhase? downwind = AssertDepartsThenFliesCircuit(engine, aircraft, "33", PatternDirection.Left);
        Assert.Null(aircraft.Pattern.AltitudeOverrideFt);
        double typedAltitude = FirstDownwind(reference.Value.aircraft).Waypoints!.PatternAltitude;
        output.WriteLine($"downwind altitude: restated {downwind?.Waypoints?.PatternAltitude:F0}, typed CTO MLT 33 {typedAltitude:F0}");
        Assert.Equal(typedAltitude, downwind?.Waypoints?.PatternAltitude);
        Assert.NotEqual(1500.0, typedAltitude);
    }

    /// <summary>With no pattern runway before it (a plain CTO), a bare traffic direction flies the takeoff runway's circuit.</summary>
    [Fact]
    public void Cto_ThenBareMlt_FliesTheTakeoffRunwayCircuit_NoAltitudeOverride()
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = HoldingShortOf28R("C172");
        if (setup is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        CommandResult cto = engine.SendCommand(Callsign, "CTO");
        Assert.True(cto.Success, cto.Message);

        CommandResult mlt = engine.SendCommand(Callsign, "MLT");
        output.WriteLine($"MLT after CTO -> {mlt.Success}: {mlt.Message}");
        Assert.True(mlt.Success, mlt.Message);
        Assert.Equal("Make left traffic, Runway 28R", mlt.Message);

        AssertDepartsThenFliesCircuit(engine, aircraft, "28R", PatternDirection.Left);
        Assert.Null(aircraft.Pattern.AltitudeOverrideFt);
    }

    /// <summary>
    /// A helicopter's takeoff is not re-stated: MLT with a runway keeps the plain pattern-direction path, which only records the
    /// pattern, so no runway-transition circuit is built after its vertical takeoff from the runway it holds short of.
    /// </summary>
    [Fact]
    public void HelicopterCto_ThenMltWithRunway_BuildsNoCircuit()
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = HoldingShortOf28R("EC35");
        if (setup is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        CommandResult cto = engine.SendCommand(Callsign, "CTO");
        output.WriteLine($"CTO -> {cto.Success}: {cto.Message}");
        Assert.True(cto.Success, cto.Message);
        Assert.True(aircraft.IsOnGround);
        PhaseList phases = aircraft.Phases!;
        Assert.Contains(phases.Phases, p => p is HelicopterTakeoffPhase { Status: PhaseStatus.Pending });
        Assert.Equal("28R", phases.AssignedRunway?.Designator);
        List<Phase> before = [.. phases.Phases];
        output.WriteLine($"phases before: {string.Join(" > ", before.Select(p => p.Name))}");

        CommandResult mlt = engine.SendCommand(Callsign, "MLT 28L");
        output.WriteLine($"MLT 28L after helicopter CTO -> {mlt.Success}: {mlt.Message}");
        output.WriteLine($"phases: {string.Join(" > ", aircraft.Phases!.Phases.Select(p => p.Name))}");

        Assert.True(mlt.Success, mlt.Message);
        Assert.Same(phases, aircraft.Phases);
        Assert.Equal(before, aircraft.Phases.Phases);
        Assert.DoesNotContain(aircraft.Phases.Phases, p => p is UpwindPhase or CrosswindPhase or DownwindPhase);
    }

    /// <summary>
    /// A helicopter's takeoff clearance stored while it taxis is not re-stated either: MLT with a runway leaves the stored
    /// clearance as it was.
    /// </summary>
    [Fact]
    public void HelicopterStoredCto_ThenMltWithRunway_KeepsTheStoredClearance()
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = TaxiingTo28R("EC35");
        if (setup is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        CommandResult cto = engine.SendCommand(Callsign, "CTO");
        output.WriteLine($"CTO while taxiing -> {cto.Success}: {cto.Message}");
        Assert.True(cto.Success, cto.Message);
        DepartureClearanceInfo? storedBefore = aircraft.Phases?.DepartureClearance;
        Assert.Equal(ClearanceType.ClearedForTakeoff, storedBefore?.Type);

        CommandResult mlt = engine.SendCommand(Callsign, "MLT 28L");
        output.WriteLine($"MLT 28L after stored helicopter CTO -> {mlt.Success}: {mlt.Message}");

        Assert.Same(storedBefore, aircraft.Phases?.DepartureClearance);
    }

    private static DownwindPhase FirstDownwind(AircraftState aircraft) =>
        Assert.IsType<DownwindPhase>(aircraft.Phases?.Phases.FirstOrDefault(p => p is DownwindPhase));

    /// <summary>A stored line-up-and-wait clearance is not a takeoff clearance (7110.65 3-9-4).</summary>
    [Fact]
    public void MltWithRunway_AfterStoredLineUpAndWait_IsRefused()
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = TaxiingTo28R("C172");
        if (setup is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        CommandResult luaw = engine.SendCommand(Callsign, "LUAW");
        Assert.True(luaw.Success, luaw.Message);
        Assert.Equal(ClearanceType.LineUpAndWait, aircraft.Phases?.DepartureClearance?.Type);

        CommandResult mlt = engine.SendCommand(Callsign, "MLT 28L");
        output.WriteLine($"MLT 28L after stored LUAW -> {mlt.Success}: {mlt.Message}");

        Assert.False(mlt.Success);
        Assert.Equal("Can't assign a traffic pattern before takeoff clearance; use CTO MLT 28L", mlt.Message);
        Assert.Equal("28R", aircraft.Phases?.AssignedRunway?.Designator);
        Assert.Null(aircraft.Phases?.DepartureRunway);
        Assert.Null(aircraft.Phases?.PatternRunway);
    }

    [Theory]
    [InlineData("MLT 28L", "28L", PatternDirection.Left)]
    [InlineData("MRT", "28R", PatternDirection.Right)]
    public void StoredCtoMlt_ThenTrafficDirection_DepartsTheClearedRunway_FliesTheNewCircuit(
        string restatement,
        string patternRunway,
        PatternDirection direction
    )
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = TaxiingTo28R("C172");
        if (setup is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        CommandResult cto = engine.SendCommand(Callsign, "CTO MLT");
        Assert.True(cto.Success, cto.Message);
        Assert.Equal(ClearanceType.ClearedForTakeoff, aircraft.Phases?.DepartureClearance?.Type);

        CommandResult restated = engine.SendCommand(Callsign, restatement);
        output.WriteLine($"{restatement} after stored CTO MLT -> {restated.Success}: {restated.Message}");
        Assert.True(restated.Success, restated.Message);
        Assert.Equal("28R", aircraft.Phases?.AssignedRunway?.Designator);

        AssertDepartsThenFliesCircuit(engine, aircraft, patternRunway, direction);
    }

    [Theory]
    [InlineData("CTO H270, MLT 28L", "Conflicting departure instructions: CTO H270 and MLT 28L; use CTO MLT 28L")]
    [InlineData("CTO H270, MLT 28L, MRT", "Conflicting departure instructions: CTO H270 and MLT 28L; use CTO MLT 28L")]
    [InlineData("CTO RH; MRT", "Conflicting departure instructions: CTO MRH and MRT; use CTO MRT")]
    public void CtoWithDepartureInstruction_ChainedWithTrafficDirection_IsRefusedWhole(string command, string expected)
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = HoldingShortOf28R("C172");
        if (setup is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        var hold = aircraft.Phases?.CurrentPhase as HoldingShortPhase;
        Assert.NotNull(hold);
        int phaseCount = aircraft.Phases!.Phases.Count;

        CommandResult result = engine.SendCommand(Callsign, command);
        output.WriteLine($"{command} -> {result.Success}: {result.Message}");

        Assert.False(result.Success);
        Assert.Equal(expected, result.Message);
        Assert.Same(hold, aircraft.Phases.CurrentPhase);
        Assert.Equal(phaseCount, aircraft.Phases.Phases.Count);
        Assert.Empty(aircraft.Queue.Blocks);
        Assert.Equal("28R", aircraft.Phases.AssignedRunway?.Designator);
        Assert.Null(aircraft.Phases.PatternRunway);
        Assert.Null(aircraft.Pattern.TrafficDirection);
    }

    /// <summary>
    /// Two traffic directions in the CTO's block are not folded: the CTO applies as typed, then each MLT/MRT re-states the
    /// built clearance's circuit in turn, so the last one (right traffic) is the one flown.
    /// </summary>
    [Fact]
    public void CtoWithTwoTrafficDirectionsInOneBlock_IsNotFolded_TheLastOneIsFlown()
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = HoldingShortOf28R("C172");
        if (setup is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        CommandResult result = engine.SendCommand(Callsign, "CTO, MLT, MRT");
        output.WriteLine($"CTO, MLT, MRT -> {result.Success}: {result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.Equal("Cleared for takeoff runway 28R, Make left traffic, Runway 28R, Make right traffic, Runway 28R", result.Message);
        AssertDepartsThenFliesCircuit(engine, aircraft, "28R", PatternDirection.Right);
    }

    /// <summary>A conditioned block after <c>;</c> is not folded: the CTO keeps its own departure and the MLT stays queued.</summary>
    [Fact]
    public void CtoThenConditionedTrafficDirection_IsNotFolded()
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = HoldingShortOf28R("C172");
        if (setup is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        CommandResult result = engine.SendCommand(Callsign, "CTO; LV 1000 MLT");
        output.WriteLine($"CTO; LV 1000 MLT -> {result.Success}: {result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.Contains(aircraft.Phases!.Phases, p => p is InitialClimbPhase);
        Assert.Null(aircraft.Phases.PatternRunway);
        CommandBlock queued = Assert.Single(aircraft.Queue.Blocks);
        Assert.NotNull(queued.Trigger);
    }

    /// <summary>On the runway in a touch-and-go roll the aircraft is flying the pattern, so a traffic direction is taken.</summary>
    [Fact]
    public void Mrt_DuringTouchAndGoRoll_IsAccepted()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        if (runway is null)
        {
            return;
        }

        var roll = TouchAndGoPhase.FromSnapshot(
            new TouchAndGoPhaseDto
            {
                Status = (int)PhaseStatus.Active,
                ElapsedSeconds = 5,
                FieldElevation = runway.ElevationFt,
                RunwayHeadingDeg = runway.TrueHeading.Degrees,
                ThresholdLat = runway.ThresholdLatitude,
                ThresholdLon = runway.ThresholdLongitude,
                RolloutDuration = 5,
                RolloutElapsed = 2,
                Reaccelerating = false,
                Airborne = false,
            }
        );
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "C172",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 50,
            IsOnGround = true,
            Phases = new PhaseList { AssignedRunway = runway, TrafficDirection = PatternDirection.Left },
        };
        aircraft.Phases.Add(roll);
        Assert.Same(roll, aircraft.Phases.CurrentPhase);

        CommandResult result = PatternCommandHandler.TryChangePatternDirection(
            aircraft,
            PatternDirection.Right,
            null,
            null,
            ActiveRunways.Empty,
            null
        );

        Assert.True(result.Success, result.Message);
        Assert.Equal(PatternDirection.Right, aircraft.Pattern.TrafficDirection);
    }

    /// <summary>
    /// A takeoff ahead with no takeoff runway (neither the departure nor the assigned runway set) refuses the re-state instead
    /// of falling through to the plain pattern-direction path, which writes the pattern runway into the assigned runway the
    /// takeoff rolls on (the YAAT-1790 symptom).
    /// </summary>
    [Fact]
    public void MltWithRunway_TakeoffAheadWithNoTakeoffRunway_IsRefused()
    {
        TestVnasData.EnsureInitialized();
        Assert.NotNull(NavigationDatabase.Instance.GetRunway("OAK", "28L"));
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "C172",
            AirportId = "OAK",
            Position = new LatLon(SpawnLat, SpawnLon),
            TrueHeading = new TrueHeading(SpawnHeadingTrue),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new TakeoffPhase());
        Assert.Null(aircraft.Phases.DepartureRunway);
        Assert.Null(aircraft.Phases.AssignedRunway);

        CommandResult result = PatternCommandHandler.TryChangePatternDirection(
            aircraft,
            PatternDirection.Left,
            "28L",
            null,
            ActiveRunways.Empty,
            null
        );
        output.WriteLine($"MLT 28L with no takeoff runway -> {result.Success}: {result.Message}");

        Assert.False(result.Success);
        Assert.Equal("Can't change the traffic pattern: no takeoff runway for the cleared departure", result.Message);
        Assert.Null(aircraft.Phases.AssignedRunway);
    }

    /// <summary>
    /// YAAT-1791: a takeoff clearance built at the hold short, then MLT/MRT on the ground re-states its departure. The aircraft
    /// rolls on 28R, the clearance's runway, and a circuit on <paramref name="patternRunway"/> follows the liftoff.
    /// </summary>
    private void AssertBuiltClearanceRestated(string clearance, string restatement, string patternRunway, PatternDirection direction)
    {
        (SimulationEngine engine, AircraftState aircraft)? setup = HoldingShortOf28R("C172");
        if (setup is null)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft) = setup.Value;
        CommandResult cto = engine.SendCommand(Callsign, clearance);
        Assert.True(cto.Success, cto.Message);
        Assert.True(aircraft.IsOnGround);

        CommandResult restated = engine.SendCommand(Callsign, restatement);
        output.WriteLine($"{restatement} after {clearance} -> {restated.Success}: {restated.Message}");
        Assert.True(restated.Success, restated.Message);
        Assert.True(aircraft.IsOnGround);

        AssertDepartsThenFliesCircuit(engine, aircraft, patternRunway, direction);
    }

    /// <summary>
    /// Ticks to liftoff, asserting the runway the takeoff phase reads (<c>DepartureRunway ?? AssignedRunway</c>) is 28R on every
    /// tick it runs, then that the chain after the liftoff is a <paramref name="direction"/> circuit whose downwind belongs to
    /// <paramref name="patternRunway"/>.
    /// </summary>
    private DownwindPhase? AssertDepartsThenFliesCircuit(
        SimulationEngine engine,
        AircraftState aircraft,
        string patternRunway,
        PatternDirection direction
    )
    {
        bool rolled = false;
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            PhaseList? phases = aircraft.Phases;
            Assert.NotNull(phases);
            if (phases.CurrentPhase is TakeoffPhase)
            {
                rolled = true;
                Assert.Equal("28R", (phases.DepartureRunway ?? phases.AssignedRunway)?.Designator);
            }

            if (!aircraft.IsOnGround)
            {
                output.WriteLine($"airborne {t}s; phases: {string.Join(" > ", phases.Phases.Select(p => p.Name))}");
                Assert.True(rolled, "lifted off without a takeoff phase");
                Assert.DoesNotContain(phases.Phases, p => p is InitialClimbPhase);
                Assert.Contains(phases.Phases, p => p is UpwindPhase or CrosswindPhase);
                Assert.Equal(patternRunway, phases.PatternRunway?.Designator);
                Assert.Equal(direction, phases.TrafficDirection);

                // The downwind's threshold is the authored (possibly displaced) one, so the circuit's runway is the OAK runway
                // end whose threshold lies nearest it.
                DownwindPhase downwind = Assert.IsType<DownwindPhase>(phases.Phases.First(p => p is DownwindPhase));
                Assert.NotNull(downwind.Waypoints);
                Assert.Equal(direction, downwind.Waypoints.Direction);
                var downwindThreshold = new LatLon(downwind.Waypoints.ThresholdLat, downwind.Waypoints.ThresholdLon);
                string nearest = OakRunwayEnds.MinBy(id =>
                    NavigationDatabase.Instance.GetRunway("OAK", id) is { } end
                        ? GeoMath.DistanceNm(downwindThreshold, new LatLon(end.ThresholdLatitude, end.ThresholdLongitude))
                        : double.MaxValue
                )!;
                Assert.Equal(patternRunway, nearest);
                return downwind;
            }
        }

        Assert.Fail($"never lifted off within 300s; phase={aircraft.Phases?.CurrentPhase?.Name} ias={aircraft.IndicatedAirspeed:F1}");
        return null;
    }

    /// <summary>
    /// A <paramref name="aircraftType"/> taxiing toward the 28R hold short on its first taxiing tick, so a clearance issued now is
    /// stored for the route's end.
    /// </summary>
    private (SimulationEngine engine, AircraftState aircraft)? TaxiingTo28R(string aircraftType)
    {
        if (BuildEngine(aircraftType) is not { } built)
        {
            return null;
        }

        (SimulationEngine engine, AircraftState aircraft) = built;
        for (int t = 1; t <= 60; t++)
        {
            engine.TickOneSecond();
            if (aircraft.Phases?.CurrentPhase is TaxiingPhase)
            {
                output.WriteLine($"t=+{t}: taxiing to 28R");
                return (engine, aircraft);
            }
        }

        Assert.Fail($"never started taxiing: phase={aircraft.Phases?.CurrentPhase?.Name}");
        return null;
    }

    private void AssertChainedMatchesModifier(string chained, string typed)
    {
        (SimulationEngine engine, AircraftState aircraft)? chainedSetup = HoldingShortOf28R("C172");
        (SimulationEngine engine, AircraftState aircraft)? typedSetup = HoldingShortOf28R("C172");
        if ((chainedSetup is null) || (typedSetup is null))
        {
            return;
        }

        CommandResult chainedResult = chainedSetup.Value.engine.SendCommand(Callsign, chained);
        CommandResult typedResult = typedSetup.Value.engine.SendCommand(Callsign, typed);
        output.WriteLine($"{chained} -> {chainedResult.Success}: {chainedResult.Message}");
        output.WriteLine($"{typed} -> {typedResult.Success}: {typedResult.Message}");

        Assert.True(typedResult.Success, typedResult.Message);
        Assert.True(chainedResult.Success, chainedResult.Message);
        Assert.Contains("runway 28R", chainedResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(typedResult.Message, chainedResult.Message);

        PhaseList? chainedPhases = chainedSetup.Value.aircraft.Phases;
        PhaseList? typedPhases = typedSetup.Value.aircraft.Phases;
        Assert.NotNull(chainedPhases);
        Assert.NotNull(typedPhases);
        Assert.Equal("28R", (chainedPhases.DepartureRunway ?? chainedPhases.AssignedRunway)?.Designator);
        Assert.Equal(typedPhases.DepartureRunway?.Designator, chainedPhases.DepartureRunway?.Designator);
        Assert.Equal(typedPhases.AssignedRunway?.Designator, chainedPhases.AssignedRunway?.Designator);
        Assert.Equal(typedPhases.PatternRunway?.Designator, chainedPhases.PatternRunway?.Designator);
        Assert.Equal(typedPhases.TrafficDirection, chainedPhases.TrafficDirection);
        Assert.Equal(typedSetup.Value.aircraft.Pattern.TrafficDirection, chainedSetup.Value.aircraft.Pattern.TrafficDirection);
        Assert.Equal(PhaseTypes(typedPhases), PhaseTypes(chainedPhases));
        Assert.Equal(typedSetup.Value.aircraft.Queue.Blocks.Count, chainedSetup.Value.aircraft.Queue.Blocks.Count);
    }

    private static List<string> PhaseTypes(PhaseList phases) => [.. phases.Phases.Select(p => p.GetType().Name)];

    private (SimulationEngine engine, AircraftState aircraft)? HoldingShortOf28R(string aircraftType)
    {
        if (BuildEngine(aircraftType) is not { } built)
        {
            return null;
        }

        (SimulationEngine engine, AircraftState aircraft) = built;
        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();
            if (
                aircraft.Phases?.CurrentPhase is HoldingShortPhase hold
                && (hold.HoldShort.Reason == HoldShortReason.DestinationRunway)
                && hold.HoldShort.TargetName is { } target
                && RunwayIdentifier.Parse(target).Contains("28R")
            )
            {
                output.WriteLine($"t=+{t}: holding short of {target} at node #{hold.HoldShort.NodeId}");
                Assert.Equal("28R", aircraft.Phases.AssignedRunway?.Designator);
                return (engine, aircraft);
            }
        }

        Assert.Fail($"never held short of 28R: phase={aircraft.Phases?.CurrentPhase?.Name} gs={aircraft.GroundSpeed:F1}");
        return null;
    }

    /// <summary>
    /// An engine with <paramref name="aircraftType"/> spawned on taxiway B and cleared <c>TAXI B 28R</c>, not yet ticked.
    /// </summary>
    private (SimulationEngine engine, AircraftState aircraft)? BuildEngine(string aircraftType)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout("OAK");
        if (layout is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("PatternCommandHandler", LogLevel.Debug)
            .EnableCategory("CommandDispatcher", LogLevel.Debug)
            .InitializeSimLog();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        AircraftState aircraft = Spawn(engine, layout, aircraftType);

        CommandResult taxi = engine.SendCommand(Callsign, "TAXI B 28R");
        Assert.True(taxi.Success, taxi.Message);
        return (engine, aircraft);
    }

    private static AircraftState Spawn(SimulationEngine engine, AirportGroundLayout layout, string aircraftType)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = aircraftType,
            Position = new LatLon(SpawnLat, SpawnLon),
            TrueHeading = new TrueHeading(SpawnHeadingTrue),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KOAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(4500),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-issue-1790",
            ScenarioName = "CTO chained with MLT departs the hold-short runway",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
        };
        return aircraft;
    }
}
