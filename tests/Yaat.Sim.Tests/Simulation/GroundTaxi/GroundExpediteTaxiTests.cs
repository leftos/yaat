using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-658 on the committed SFO layout: <c>TAXI … EXP</c> sets the route expedite (cap ×1.3, accel 1.5 kt/s) that a following TAXI
/// without EXP, <c>NORM</c> and a release from a stop without EXP end; <c>CROSS … EXP</c> expedites each crossing it names, carried on
/// the crossing's own hold-short, from issuance through the gap between the runways; a crossing honours <c>SPD</c>; a Super/Heavy gets
/// no speed or acceleration increase.
/// </summary>
public sealed class GroundExpediteTaxiTests
{
    private const string Callsign = "EXP1";
    private const string Route = "TAXI F 28L";
    private const int StageBudgetSeconds = 600;
    private const double JetExpediteCapKts = 39.0;
    private const double JetTaxiKts = 30.0;
    private const string WithoutDelay = "without delay";

    private readonly ITestOutputHelper _output;

    public GroundExpediteTaxiTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void TaxiExp_SetsTheRouteExpedite_WithTheExpediteCapAndAcceleration()
    {
        if (Taxiing("B738", $"{Route} EXP") is not { } taxiing)
        {
            return;
        }

        (SfoGround _, AircraftState aircraft, CommandResult result) = taxiing;
        Assert.Contains(WithoutDelay, result.Message, StringComparison.Ordinal);
        Assert.True(aircraft.Ground.IsExpeditingTaxi);
        TaxiingPhase phase = Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(JetExpediteCapKts, phase.NavMaxSpeedKts, 3);
        Assert.Equal(1.5, aircraft.Targets.DesiredAccelRate);
    }

    /// <summary>The TAXI echo places "without delay" after the route and before the hold-short and destination, as the readback does.</summary>
    [Fact]
    public void TaxiExpEcho_PutsWithoutDelayAfterTheRoute_BeforeTheHoldShort()
    {
        if (Taxiing("B738", "TAXI F HS 1L 28L EXP") is not { } taxiing)
        {
            return;
        }

        (SfoGround _, AircraftState _, CommandResult result) = taxiing;
        Assert.Equal("Taxi via F, without delay, HS 01L/19R at F, HS 10R/28L at F", result.Message);
    }

    /// <summary>A TAXI to the stand the aircraft is already on (no route to drive) still echoes "without delay".</summary>
    [Fact]
    public void TaxiExpToTheStandItIsOn_EchoesWithoutDelay()
    {
        if (SfoGroundHarness.Build(_output, autoCross: false) is not { } ground)
        {
            return;
        }

        GroundNode stand = ground.Layout.Nodes.Values.First(n => (n.Type == GroundNodeType.Parking) && (n.Name is { Length: > 0 }));
        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, Callsign, "B738", stand.Name!);
        aircraft.Ground.ParkingSpot = stand.Name;
        string command = $"TAXI @{stand.Name} EXP";
        CommandResult result = ground.Engine.SendCommand(Callsign, command);
        AssertAccepted(result, command);

        Assert.Equal($"Taxi via @{stand.Name}, without delay", result.Message);
    }

    [Fact]
    public void FollowingTaxiWithoutExp_EndsTheRouteExpedite()
    {
        if (Taxiing("B738", $"{Route} EXP") is not { } taxiing)
        {
            return;
        }

        (SfoGround ground, AircraftState aircraft, CommandResult _) = taxiing;
        AssertAccepted(ground.Engine.SendCommand(Callsign, Route), Route);
        ground.Engine.TickOneSecond();

        Assert.False(aircraft.Ground.IsExpeditingTaxi);
        TaxiingPhase phase = Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(JetTaxiKts, phase.NavMaxSpeedKts, 3);
        Assert.Null(aircraft.Targets.DesiredAccelRate);
    }

    [Fact]
    public void Norm_EndsTheRouteExpedite()
    {
        if (Taxiing("B738", $"{Route} EXP") is not { } taxiing)
        {
            return;
        }

        (SfoGround ground, AircraftState aircraft, CommandResult _) = taxiing;
        AssertAccepted(ground.Engine.SendCommand(Callsign, "NORM"), "NORM");
        ground.Engine.TickOneSecond();

        Assert.False(aircraft.Ground.IsExpeditingTaxi);
        Assert.Null(aircraft.Targets.DesiredAccelRate);
    }

    [Theory]
    [InlineData("B77W")]
    [InlineData("A388")]
    public void SuperOrHeavy_TaxiExp_GetsNoSpeedOrAccelerationIncrease(string type)
    {
        if (Taxiing(type, $"{Route} EXP") is not { } taxiing)
        {
            return;
        }

        (SfoGround _, AircraftState aircraft, CommandResult _) = taxiing;
        Assert.True(aircraft.Ground.IsExpeditingTaxi);
        TaxiingPhase phase = Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.True(phase.NavMaxSpeedKts <= JetTaxiKts, $"cap {phase.NavMaxSpeedKts} kt above the {JetTaxiKts} kt taxi speed");
        Assert.Equal(1.0, aircraft.Targets.DesiredAccelRate ?? CategoryPerformance.TaxiAccelRate(AircraftCategory.Jet));
    }

    [Theory]
    [InlineData("B77W")]
    [InlineData("A388")]
    public void SuperOrHeavy_StandaloneExp_GetsNoSpeedIncrease(string type)
    {
        if (Taxiing(type, Route) is not { } taxiing)
        {
            return;
        }

        (SfoGround ground, AircraftState aircraft, CommandResult _) = taxiing;
        AssertAccepted(ground.Engine.SendCommand(Callsign, "EXP"), "EXP");
        ground.Engine.TickOneSecond();

        Assert.True(aircraft.Ground.IsExpeditingTaxi);
        TaxiingPhase phase = Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.True(phase.NavMaxSpeedKts <= JetTaxiKts, $"cap {phase.NavMaxSpeedKts} kt above the {JetTaxiKts} kt taxi speed");
    }

    /// <summary>
    /// <c>CROSS … EXP</c> while rolling: echoed once "without delay", expedites the approach to the first bar from issuance, each listed
    /// crossing and the taxi between the runways, and ends clear of the last listed runway.
    /// </summary>
    [Fact]
    public void CrossExp_WhileRolling_ExpeditesFromIssuanceThroughTheLastListedCrossing_AndEndsThere()
    {
        if (Taxiing("B738", Route) is not { } taxiing)
        {
            return;
        }

        (SfoGround ground, AircraftState aircraft, CommandResult _) = taxiing;
        List<HoldShortPoint> crossings = Crossings(aircraft);
        Assert.True(crossings.Count > 1, $"the route crosses {crossings.Count} runway(s); the gap between two crossings is unproved");
        string command = $"CROSS {string.Join(" ", crossings.Select(EndOf))} EXP";
        CommandResult cross = ground.Engine.SendCommand(Callsign, command);
        AssertAccepted(cross, command);
        _output.WriteLine($"{command} -> {cross.Message}");
        Assert.Equal(1, cross.Message!.Split(WithoutDelay).Length - 1);
        Assert.Equal(CommandDescriber.DescribeNatural(CommandParser.Parse(command).Value!), cross.Message);

        Assert.All(crossings, hs => Assert.True(hs.IsExpedited, $"{hs.TargetName} not expedited"));
        Assert.False(aircraft.Ground.IsExpeditingTaxi);

        ground.Engine.TickOneSecond();
        TaxiingPhase approach = Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(JetExpediteCapKts, approach.NavMaxSpeedKts, 3);
        Assert.Equal(1.5, aircraft.Targets.DesiredAccelRate);

        CrossingRunwayPhase crossing = TickIntoCrossing(ground, aircraft);
        Assert.True(crossing.IsExpedited);
        Assert.Equal(JetExpediteCapKts, crossing.NavMaxSpeedKts!.Value, 3);
        Assert.Equal(1.5, aircraft.Targets.DesiredAccelRate);

        int between = SfoGroundHarness.TickUntil(ground.Engine, () => aircraft.Phases?.CurrentPhase is TaxiingPhase, StageBudgetSeconds, null);
        Assert.True(between > 0, $"never taxied between the runways: {aircraft.Phases?.CurrentPhase?.Name}");
        ground.Engine.TickOneSecond();
        TaxiingPhase gap = Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(JetExpediteCapKts, gap.NavMaxSpeedKts, 3);
        Assert.Equal(1.5, aircraft.Targets.DesiredAccelRate);

        HoldShortPoint last = crossings[^1];
        int after = SfoGroundHarness.TickUntil(
            ground.Engine,
            () =>
                (aircraft.Phases?.CurrentPhase is TaxiingPhase)
                && (aircraft.Ground.AssignedTaxiRoute is { } route)
                && (route.SegmentIndexAhead(last.NodeId) == int.MaxValue),
            StageBudgetSeconds,
            null
        );
        Assert.True(after > 0, $"never resumed taxiing past {last.TargetName}: {aircraft.Phases?.CurrentPhase?.Name}");
        ground.Engine.TickOneSecond();
        if (aircraft.Phases?.CurrentPhase is TaxiingPhase onward)
        {
            Assert.Equal(JetTaxiKts, onward.NavMaxSpeedKts, 3);
            Assert.Null(aircraft.Targets.DesiredAccelRate);
        }
    }

    [Fact]
    public void SingleRunwayCrossExp_EchoesWithoutDelayOnce()
    {
        if (Taxiing("B738", Route) is not { } taxiing)
        {
            return;
        }

        (SfoGround ground, AircraftState aircraft, CommandResult _) = taxiing;
        string end = EndOf(Crossings(aircraft)[0]);
        CommandResult cross = ground.Engine.SendCommand(Callsign, $"CROSS {end} EXP");
        AssertAccepted(cross, $"CROSS {end} EXP");

        Assert.Equal($"Cross {end}, without delay", cross.Message);
    }

    [Fact]
    public void Crossing_HonoursTheCommandedTaxiSpeed()
    {
        if (Taxiing("B738", Route) is not { } taxiing)
        {
            return;
        }

        (SfoGround ground, AircraftState aircraft, CommandResult _) = taxiing;
        AssertAccepted(ground.Engine.SendCommand(Callsign, "SPD 35"), "SPD 35");
        string command = $"CROSS {EndOf(Crossings(aircraft)[0])}";
        AssertAccepted(ground.Engine.SendCommand(Callsign, command), command);

        CrossingRunwayPhase crossing = TickIntoCrossing(ground, aircraft);
        Assert.Equal(35.0, crossing.NavMaxSpeedKts!.Value, 3);
    }

    [Fact]
    public void CrossExp_UnderACommandedSpeed_KeepsTheSpeedAndTheNormalAcceleration()
    {
        if (Taxiing("B738", Route) is not { } taxiing)
        {
            return;
        }

        (SfoGround ground, AircraftState aircraft, CommandResult _) = taxiing;
        AssertAccepted(ground.Engine.SendCommand(Callsign, "SPD 20"), "SPD 20");
        string command = $"CROSS {EndOf(Crossings(aircraft)[0])} EXP";
        AssertAccepted(ground.Engine.SendCommand(Callsign, command), command);

        CrossingRunwayPhase crossing = TickIntoCrossing(ground, aircraft);
        Assert.True(crossing.IsExpedited);
        Assert.Equal(20.0, crossing.NavMaxSpeedKts!.Value, 3);
        Assert.Null(aircraft.Targets.DesiredAccelRate);
    }

    [Fact]
    public void CrossExp_IssuedMidCrossing_ExpeditesTheCrossing()
    {
        if (Taxiing("B738", Route) is not { } taxiing)
        {
            return;
        }

        (SfoGround ground, AircraftState aircraft, CommandResult _) = taxiing;
        string end = EndOf(Crossings(aircraft)[0]);
        AssertAccepted(ground.Engine.SendCommand(Callsign, $"CROSS {end}"), $"CROSS {end}");
        CrossingRunwayPhase crossing = TickIntoCrossing(ground, aircraft);
        Assert.Equal(JetTaxiKts, crossing.NavMaxSpeedKts!.Value, 3);

        AssertAccepted(ground.Engine.SendCommand(Callsign, $"CROSS {end} EXP"), $"CROSS {end} EXP");
        ground.Engine.TickOneSecond();

        Assert.Same(crossing, aircraft.Phases?.CurrentPhase);
        Assert.Equal(JetExpediteCapKts, crossing.NavMaxSpeedKts!.Value, 3);
        Assert.Equal(1.5, aircraft.Targets.DesiredAccelRate);
    }

    [Fact]
    public void PlainCross_AfterCrossExp_CrossesAtNormalSpeed()
    {
        if (Taxiing("B738", Route) is not { } taxiing)
        {
            return;
        }

        (SfoGround ground, AircraftState aircraft, CommandResult _) = taxiing;
        HoldShortPoint first = Crossings(aircraft)[0];
        string end = EndOf(first);
        AssertAccepted(ground.Engine.SendCommand(Callsign, $"CROSS {end} EXP"), $"CROSS {end} EXP");
        Assert.True(first.IsExpedited);
        AssertAccepted(ground.Engine.SendCommand(Callsign, $"CROSS {end}"), $"CROSS {end}");
        Assert.False(first.IsExpedited);

        CrossingRunwayPhase crossing = TickIntoCrossing(ground, aircraft);
        Assert.False(crossing.IsExpedited);
        Assert.Equal(JetTaxiKts, crossing.NavMaxSpeedKts!.Value, 3);
        Assert.Null(aircraft.Targets.DesiredAccelRate);
    }

    [Fact]
    public void Norm_DuringAnExpeditedCrossing_EndsItAndClearsEveryCrossingMark()
    {
        if (Taxiing("B738", Route) is not { } taxiing)
        {
            return;
        }

        (SfoGround ground, AircraftState aircraft, CommandResult _) = taxiing;
        List<HoldShortPoint> crossings = Crossings(aircraft);
        string command = $"CROSS {string.Join(" ", crossings.Select(EndOf))} EXP";
        AssertAccepted(ground.Engine.SendCommand(Callsign, command), command);
        CrossingRunwayPhase crossing = TickIntoCrossing(ground, aircraft);
        Assert.True(crossing.IsExpedited);

        AssertAccepted(ground.Engine.SendCommand(Callsign, "NORM"), "NORM");
        ground.Engine.TickOneSecond();

        Assert.False(crossing.IsExpedited);
        Assert.All(crossings, hs => Assert.False(hs.IsExpedited, $"{hs.TargetName} still expedited"));
        Assert.Equal(JetTaxiKts, crossing.NavMaxSpeedKts!.Value, 3);
        Assert.Null(aircraft.Targets.DesiredAccelRate);
    }

    [Fact]
    public void RollingPreClearWithoutExp_KeepsTheRouteExpedite_AndTheCrossingFliesTheExpediteCap()
    {
        if (Taxiing("B738", $"{Route} EXP") is not { } taxiing)
        {
            return;
        }

        (SfoGround ground, AircraftState aircraft, CommandResult _) = taxiing;
        string command = $"CROSS {EndOf(Crossings(aircraft)[0])}";
        AssertAccepted(ground.Engine.SendCommand(Callsign, command), command);
        Assert.True(aircraft.Ground.IsExpeditingTaxi);

        CrossingRunwayPhase crossing = TickIntoCrossing(ground, aircraft);
        Assert.False(crossing.IsExpedited);
        Assert.Equal(JetExpediteCapKts, crossing.NavMaxSpeedKts!.Value, 3);
        Assert.True(aircraft.Ground.IsExpeditingTaxi);
    }

    [Fact]
    public void RollingBareRes_PreClearingATaxiwayBar_KeepsTheRouteExpedite()
    {
        if (Taxiing("B738", Route) is not { } taxiing)
        {
            return;
        }

        (SfoGround ground, AircraftState aircraft, CommandResult _) = taxiing;
        string crossAll = $"CROSS {string.Join(" ", Crossings(aircraft).Select(EndOf))}";
        AssertAccepted(ground.Engine.SendCommand(Callsign, crossAll), crossAll);
        string taxiway = TaxiwayAhead(ground, aircraft);
        AssertAccepted(ground.Engine.SendCommand(Callsign, $"HS {taxiway}"), $"HS {taxiway}");
        AssertAccepted(ground.Engine.SendCommand(Callsign, "EXP"), "EXP");

        CommandResult res = ground.Engine.SendCommand(Callsign, "RES");
        AssertAccepted(res, "RES");
        _output.WriteLine($"RES -> {res.Message}");

        Assert.True(aircraft.Ground.IsExpeditingTaxi);
    }

    [Fact]
    public void CrossFromAStopWithoutExp_EndsTheRouteExpedite()
    {
        if (Taxiing("B738", $"{Route} EXP") is not { } taxiing)
        {
            return;
        }

        (SfoGround ground, AircraftState aircraft, CommandResult _) = taxiing;
        HoldShortPoint first = Crossings(aircraft)[0];
        HoldShortOf(ground, aircraft, first);
        Assert.True(aircraft.Ground.IsExpeditingTaxi, "stopping at the bar is not a cancel");

        string command = $"CROSS {EndOf(first)}";
        AssertAccepted(ground.Engine.SendCommand(Callsign, command), command);

        Assert.False(aircraft.Ground.IsExpeditingTaxi);
    }

    /// <summary>
    /// A RES releasing the aircraft from a runway bar — bare, or with a CROSS list — ends the route expedite and every crossing mark.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResAtTheBar_EndsTheRouteExpedite_AndClearsEveryCrossingMark(bool withCross)
    {
        if (Taxiing("B738", $"{Route} EXP") is not { } taxiing)
        {
            return;
        }

        (SfoGround ground, AircraftState aircraft, CommandResult _) = taxiing;
        List<HoldShortPoint> crossings = Crossings(aircraft);
        string later = $"CROSS {EndOf(crossings[1])} EXP";
        AssertAccepted(ground.Engine.SendCommand(Callsign, later), later);

        HoldShortOf(ground, aircraft, crossings[0]);
        string release = withCross ? $"RES CROSS {EndOf(crossings[^1])}" : "RES";
        AssertAccepted(ground.Engine.SendCommand(Callsign, release), release);

        Assert.False(aircraft.Ground.IsExpeditingTaxi);
        Assert.All(crossings, hs => Assert.False(hs.IsExpedited, $"{hs.TargetName} still expedited"));
    }

    [Fact]
    public void ResBreakingADetectorStall_EndsTheRouteExpedite()
    {
        if (Taxiing("B738", $"{Route} EXP") is not { } taxiing)
        {
            return;
        }

        (SfoGround _, AircraftState aircraft, CommandResult _) = taxiing;
        aircraft.Ground.SpeedLimit = 0.0;

        // The handler's own result: the dispatcher echoes every RES as "Resume taxi", so only here is the stall break visible.
        CommandResult res = GroundCommandHandler.TryResumeTaxi(aircraft);
        AssertAccepted(res, "RES");

        Assert.Equal("Resume taxi — breaking ground conflict", res.Message);
        Assert.False(aircraft.Ground.IsExpeditingTaxi);
    }

    [Fact]
    public void ExpeditedCrossing_RestoredMidCrossing_KeepsTheSameCapAndFlag()
    {
        if (Taxiing("B738", Route) is not { } taxiing)
        {
            return;
        }

        (SfoGround ground, AircraftState aircraft, CommandResult _) = taxiing;
        string command = $"CROSS {EndOf(Crossings(aircraft)[0])} EXP";
        AssertAccepted(ground.Engine.SendCommand(Callsign, command), command);
        CrossingRunwayPhase crossing = TickIntoCrossing(ground, aircraft);
        Assert.True(crossing.IsExpedited);

        StateSnapshotDto snapshot = RoundTrip(ground.Engine.CaptureSnapshot());
        CrossingRunwayPhaseDto dto = Assert.IsType<CrossingRunwayPhaseDto>(crossing.ToSnapshot());
        Assert.True(dto.IsExpedited);

        if (SfoGroundHarness.Build(_output, autoCross: false) is not { } restoredGround)
        {
            return;
        }

        restoredGround.Engine.RestoreFromSnapshot(snapshot);
        AircraftState restored = Assert.IsType<AircraftState>(restoredGround.Engine.FindAircraft(Callsign));
        restored.Ground.AssignedTaxiRoute!.GetHoldShortAt(crossing.ApproachNodeId)!.IsExpedited = false;
        restoredGround.Engine.TickOneSecond();
        ground.Engine.TickOneSecond();

        CrossingRunwayPhase restoredCrossing = Assert.IsType<CrossingRunwayPhase>(restored.Phases?.CurrentPhase);
        Assert.True(restoredCrossing.IsExpedited);
        Assert.Equal(crossing.NavMaxSpeedKts!.Value, restoredCrossing.NavMaxSpeedKts!.Value, 3);
        Assert.Equal(aircraft.Targets.DesiredAccelRate, restored.Targets.DesiredAccelRate);
    }

    [Fact]
    public void HoldShortExpediteFlag_RoundTripsThroughTheRouteSnapshot()
    {
        if (Taxiing("B738", Route) is not { } taxiing)
        {
            return;
        }

        (SfoGround ground, AircraftState aircraft, CommandResult _) = taxiing;
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        HoldShortPoint first = Crossings(aircraft)[0];
        first.IsExpedited = true;

        var restored = TaxiRoute.FromSnapshot(route.ToSnapshot(), ground.Layout);
        Assert.NotNull(restored);

        Assert.True(restored.GetHoldShortAt(first.NodeId)!.IsExpedited);
        Assert.All(restored.HoldShortPoints.Where(h => h.NodeId != first.NodeId), h => Assert.False(h.IsExpedited));
    }

    /// <summary>
    /// An aircraft on SFO taxiway F short of its 01L/19R crossing given <paramref name="command"/> and taxiing for a few seconds; null
    /// without SFO data.
    /// </summary>
    private (SfoGround Ground, AircraftState Aircraft, CommandResult Result)? Taxiing(string type, string command)
    {
        if (SfoGroundHarness.Build(_output, autoCross: false) is not { } ground)
        {
            return null;
        }

        SfoCrossing crossing = SfoGroundHarness.ResolveSfoCrossing(ground.Layout);
        var heading = new TrueHeading(GeoMath.BearingTo(crossing.ApproachNode.Position, crossing.ApproachBar.Position));
        AircraftState aircraft = SfoGroundHarness.SpawnAt(ground, Callsign, type, (crossing.ApproachNode, heading), new HoldingInPositionPhase());
        CommandResult result = ground.Engine.SendCommand(Callsign, command);
        AssertAccepted(result, command);
        for (int second = 0; second < 8; second++)
        {
            ground.Engine.TickOneSecond();
        }

        Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
        return (ground, aircraft, result);
    }

    /// <summary>The uncleared runway crossings ahead on the aircraft's route, in route order; at least one.</summary>
    private List<HoldShortPoint> Crossings(AircraftState aircraft)
    {
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        List<HoldShortPoint> crossings = [.. route.HoldShortPoints.Where(h => (h.Reason == HoldShortReason.RunwayCrossing) && !h.IsCleared)];
        if (crossings.Count == 0)
        {
            SfoGroundHarness.DumpRoute(_output, route);
        }

        Assert.NotEmpty(crossings);
        return crossings;
    }

    /// <summary>
    /// A taxiway meeting the route ahead of the aircraft, two segments on at least and off any runway bar, that the route itself does
    /// not use: the target of a taxiway hold short a bare RES pre-clears once every runway crossing before it is cleared.
    /// </summary>
    private string TaxiwayAhead(SfoGround ground, AircraftState aircraft)
    {
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        HashSet<string> used = [.. route.Segments.Select(s => s.TaxiwayName)];
        foreach (TaxiRouteSegment segment in route.Segments.Skip(route.CurrentSegmentIndex + 2))
        {
            GroundNode node = ground.Layout.Nodes[segment.ToNodeId];
            if ((node.Type == GroundNodeType.RunwayHoldShort) || node.Edges.Any(e => e.IsRunwayCenterline))
            {
                continue;
            }

            if (node.Edges.FirstOrDefault(e => IsPlainTaxiwayName(e.TaxiwayName) && !used.Contains(e.TaxiwayName)) is { } edge)
            {
                return edge.TaxiwayName;
            }
        }

        SfoGroundHarness.DumpRoute(_output, route);
        throw new Xunit.Sdk.XunitException("no taxiway meets the route ahead");
    }

    /// <summary>A taxiway designator an HS can name: letters and digits only, not a runway or a connector ("F - RWY01L/19R").</summary>
    private static bool IsPlainTaxiwayName(string name) =>
        (name.Length > 0) && char.IsLetter(name[0]) && name.All(char.IsLetterOrDigit) && !name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase);

    private static void HoldShortOf(SfoGround ground, AircraftState aircraft, HoldShortPoint bar)
    {
        int held = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => aircraft.Phases?.CurrentPhase is HoldingShortPhase hold && ReferenceEquals(hold.HoldShort, bar),
            StageBudgetSeconds,
            null
        );
        Assert.True(held > 0, $"never held short of {bar.TargetName}: {aircraft.Phases?.CurrentPhase?.Name}");
    }

    private static string EndOf(HoldShortPoint crossing) => RunwayIdentifier.Parse(crossing.TargetName!).End1;

    private static CrossingRunwayPhase TickIntoCrossing(SfoGround ground, AircraftState aircraft)
    {
        int entered = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => aircraft.Phases?.CurrentPhase is CrossingRunwayPhase { NavMaxSpeedKts: not null },
            StageBudgetSeconds,
            null
        );
        Assert.True(entered > 0, $"never entered the crossing: {aircraft.Phases?.CurrentPhase?.Name}");
        ground.Engine.TickOneSecond();
        return Assert.IsType<CrossingRunwayPhase>(aircraft.Phases?.CurrentPhase);
    }

    private static void AssertAccepted(CommandResult result, string command) => Assert.True(result.Success, $"{command}: {result.Message}");

    private static StateSnapshotDto RoundTrip(StateSnapshotDto snapshot) =>
        Assert.IsType<StateSnapshotDto>(
            JsonSerializer.Deserialize<StateSnapshotDto>(
                JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default),
                RecordingJsonOptions.Default
            )
        );
}
