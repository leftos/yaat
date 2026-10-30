using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// The terminal text a controller sees about a held or upcoming runway names an active runway by its active end. On the SFO
/// layout with <c>ARWY SFO 01L 01R 28L 28R</c>, an aircraft at the 1R/19L bar nearest the 19L threshold names the runway "1R",
/// never "19L" (the nearer end) or the combined "1R/19L"; the aircraft's own runway keeps its assigned end. Commands go through
/// <see cref="SimulationEngine.SendCommand"/>. No-ARWY text is pinned by the existing handler tests.
/// </summary>
public class GroundCommandHandlerActiveEndTests(ITestOutputHelper output)
{
    private const string Callsign = "UAL1";
    private const string Lead = "UAL2";
    private const string AllActive = "ARWY SFO 01L 01R 28L 28R";

    /// <summary>The SFO harness with <paramref name="arwy"/> issued, or null when the SFO layout or navdata is missing.</summary>
    private SfoGround? Sfo(string arwy)
    {
        if (SfoGroundHarness.Build(output, AutoCrossRunwayMode.Off) is not { } ground)
        {
            return null;
        }

        CommandResult set = ground.Engine.Actions.Issue(new ActionInput("", arwy, "conn-1", "XX", Baked: null)).Result;
        Assert.True(set.Success, set.Message);
        return ground;
    }

    /// <summary>The 1R/19L hold-short bar nearest the 19L threshold, so the nearer end of the runway is 19L.</summary>
    private static GroundNode BarNear19L(AirportGroundLayout layout)
    {
        RunwayInfo threshold = NavigationDatabase.Instance.GetRunway("SFO", "19L") ?? throw new InvalidOperationException("SFO 19L missing");
        var at = new LatLon(threshold.ThresholdLatitude, threshold.ThresholdLongitude);
        var runway = RunwayIdentifier.Parse("01R/19L");
        return layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is { } id) && id.Overlaps(runway))
            .MinBy(n => GeoMath.DistanceNm(n.Position, at))!;
    }

    private static HoldShortPoint BarHold(GroundNode bar, HoldShortReason reason) =>
        new()
        {
            NodeId = bar.Id,
            Reason = reason,
            TargetName = bar.RunwayId!.Value.ToString(),
        };

    /// <summary>A taxi route still running into <paramref name="hold"/>'s bar along its taxiway edge, the bar its one hold short.</summary>
    private static TaxiRoute RouteInto(GroundNode bar, HoldShortPoint hold)
    {
        IGroundEdge edge = bar.Edges.First(e => !e.TaxiwayName.StartsWith("RWY", StringComparison.Ordinal));
        GroundNode from = edge.OtherNode(bar)!;
        return new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = edge.TaxiwayName, Edge = edge.Directed(from, bar) }],
            HoldShortPoints = [hold],
            CurrentSegmentIndex = 0,
        };
    }

    /// <summary>
    /// Adds <paramref name="callsign"/> at <paramref name="at"/> in <paramref name="phases"/> (the first started with the room's
    /// active runways), on <paramref name="route"/> when one is given.
    /// </summary>
    private static AircraftState Spawn(SfoGround ground, string callsign, LatLon at, IReadOnlyList<Phase> phases, TaxiRoute? route)
    {
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = at,
            TrueHeading = new TrueHeading(0),
            IsOnGround = true,
            Phases = new PhaseList(),
        };
        aircraft.Ground.Layout = ground.Layout;
        aircraft.Ground.AssignedTaxiRoute = route;
        foreach (Phase phase in phases)
        {
            aircraft.Phases.Add(phase);
        }

        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ground.Engine.Scenario!.ActiveRunways, ground.Layout));
        ground.Engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    /// <summary>Holds <see cref="Callsign"/> at <paramref name="bar"/>, the taxi route to it still running.</summary>
    private static AircraftState HeldMidRoute(SfoGround ground, GroundNode bar, HoldShortReason reason)
    {
        HoldShortPoint hold = BarHold(bar, reason);
        return Spawn(ground, Callsign, bar.Position, [new HoldingShortPhase(hold)], RouteInto(bar, hold));
    }

    [Fact]
    public void CrossOfTheDestinationRunwayMidRoute_IsRefusedNamingTheActiveEnd()
    {
        if (Sfo(AllActive) is not { } ground)
        {
            return;
        }

        HeldMidRoute(ground, BarNear19L(ground.Layout), HoldShortReason.DestinationRunway);

        CommandResult result = ground.Engine.SendCommand(Callsign, "CROSS 19L");

        Assert.False(result.Success);
        Assert.Equal("Cannot cross destination runway 1R; use LUAW or CTO", result.Message);
    }

    /// <summary>
    /// Red proof: with the handler's list replaced by <c>ActiveRunways.Empty</c>, the refusal read
    /// "holding short of departure runway 19L; issue LUAW or CTO" (the nearer end).
    /// </summary>
    [Fact]
    public void FollowAtTheDepartureBarMidRoute_IsRefusedNamingTheActiveEnd()
    {
        if (Sfo(AllActive) is not { } ground)
        {
            return;
        }

        SfoGroundHarness.SpawnAtJunction(ground, Lead, "B738", "B", "H");
        HeldMidRoute(ground, BarNear19L(ground.Layout), HoldShortReason.DestinationRunway);

        CommandResult result = ground.Engine.SendCommand(Callsign, $"FOLLOWG {Lead}");

        Assert.False(result.Success);
        Assert.Equal("holding short of departure runway 1R; issue LUAW or CTO", result.Message);
    }

    /// <summary>
    /// Red proof: with the handler's list replaced by <c>ActiveRunways.Empty</c>, the refusal read
    /// "unable, holding short of 19L — issue CROSS 19L" (the nearer end).
    /// </summary>
    [Fact]
    public void BareResumeWithAFollowArmed_IsRefusedNamingTheActiveEnd()
    {
        if (Sfo(AllActive) is not { } ground)
        {
            return;
        }

        SfoGroundHarness.SpawnAtJunction(ground, Lead, "B738", "B", "H");
        GroundNode bar = BarNear19L(ground.Layout);
        Spawn(ground, Callsign, bar.Position, [new HoldingShortPhase(BarHold(bar, HoldShortReason.RunwayCrossing)), new FollowingPhase(Lead)], null);

        CommandResult result = ground.Engine.SendCommand(Callsign, "RES");

        Assert.False(result.Success);
        Assert.Equal("unable, holding short of 1R — issue CROSS 1R", result.Message);
    }

    [Fact]
    public void NextHoldShortIsARunway_RefusalNamesTheActiveEnd()
    {
        if (Sfo(AllActive) is not { } ground)
        {
            return;
        }

        GroundNode bar = BarNear19L(ground.Layout);
        TaxiRoute route = RouteInto(bar, BarHold(bar, HoldShortReason.RunwayCrossing));
        Spawn(ground, Callsign, route.Segments[0].Edge.FromNode.Position, [new TaxiingPhase()], route);

        CommandResult result = ground.Engine.SendCommand(Callsign, "RES");

        Assert.False(result.Success);
        Assert.Equal("Next hold short is runway 1R; use CROSS", result.Message);
    }

    [Fact]
    public void HoldAtTheBar_NamesTheActiveEnd()
    {
        if (Sfo(AllActive) is not { } ground)
        {
            return;
        }

        GroundNode bar = BarNear19L(ground.Layout);
        Spawn(ground, Callsign, bar.Position, [new HoldingShortPhase(BarHold(bar, HoldShortReason.RunwayCrossing))], null);

        CommandResult result = ground.Engine.SendCommand(Callsign, "HOLD");

        Assert.Equal("Hold position (already short of 1R)", result.Message);
    }

    /// <summary>A bare CROSS names no end, so its echo names the active end.</summary>
    [Fact]
    public void BareCross_EchoesTheActiveEnd()
    {
        if (Sfo(AllActive) is not { } ground)
        {
            return;
        }

        GroundNode bar = BarNear19L(ground.Layout);
        Spawn(ground, Callsign, bar.Position, [new HoldingShortPhase(BarHold(bar, HoldShortReason.RunwayCrossing))], null);

        CommandResult result = ground.Engine.SendCommand(Callsign, "CROSS");

        Assert.True(result.Success, result.Message);
        Assert.Equal("Cross 1R", result.Message);
    }

    /// <summary>
    /// "No crossing route found" is reachable only when the layout carries no way across the runway, which no real SFO bar
    /// lacks, so this case keeps a layout holding nothing but the 1R/19L bar node, at the 19L threshold.
    /// </summary>
    [Fact]
    public void CrossWithNoCrossingRoute_IsRefusedNamingTheActiveEnd()
    {
        TestVnasData.EnsureInitialized();
        ActiveRunways active = ActiveRunways.Empty.With(
            "SFO",
            [.. new[] { "01L", "01R", "28L", "28R" }.Select(end => new ActiveRunway(end, ActiveRunwayUse.Both))]
        );
        RunwayInfo threshold = NavigationDatabase.Instance.GetRunway("SFO", "19L") ?? throw new InvalidOperationException("SFO 19L missing");
        var position = new LatLon(threshold.ThresholdLatitude, threshold.ThresholdLongitude);
        var layout = new AirportGroundLayout { AirportId = "SFO" };
        var bar = new GroundNode
        {
            Id = 3,
            Position = position,
            Type = GroundNodeType.RunwayHoldShort,
            RunwayId = RunwayIdentifier.Parse("01R/19L"),
        };
        layout.Nodes[bar.Id] = bar;
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(0),
            IsOnGround = true,
            Phases = new PhaseList(),
        };
        aircraft.Ground.Layout = layout;
        aircraft.Phases.Add(new HoldingShortPhase(BarHold(bar, HoldShortReason.RunwayCrossing)));
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, active, layout));

        CommandResult result = GroundCommandHandler.TryCrossRunway(aircraft, new CrossRunwayCommand([], [], Expedite: false), layout, null, active);

        Assert.False(result.Success);
        Assert.Equal("No crossing route found for 1R", result.Message);
    }

    [Fact]
    public void CommandTheHoldDoesNotTake_IsRefusedNamingTheActiveEnd()
    {
        if (Sfo(AllActive) is not { } ground)
        {
            return;
        }

        GroundNode bar = BarNear19L(ground.Layout);
        var hold = new HoldingShortPhase(BarHold(bar, HoldShortReason.RunwayCrossing));
        Spawn(ground, Callsign, bar.Position, [hold], null);

        CommandAcceptance acceptance = hold.CanAcceptCommand(CanonicalCommandType.Speed);

        Assert.True(acceptance.IsRejected);
        Assert.StartsWith("aircraft is holding short of 1R;", acceptance.Reason);
    }

    [Fact]
    public void TailOverARunway_WarningNamesItsActiveEnd()
    {
        if (Sfo(AllActive) is not { } ground)
        {
            return;
        }

        GroundNode bar = BarNear19L(ground.Layout);
        IGroundEdge edge = bar.Edges.First(e => !e.TaxiwayName.StartsWith("RWY", StringComparison.Ordinal));
        GroundNode beyond = edge.OtherNode(bar)!;
        var hold = new HoldShortPoint
        {
            NodeId = beyond.Id,
            Reason = HoldShortReason.ExplicitHoldShort,
            TargetName = edge.TaxiwayName,
            TailOverRunwayNodeId = bar.Id,
        };
        AircraftState aircraft = Spawn(ground, Callsign, beyond.Position, [new HoldingShortPhase(hold)], null);

        Assert.Contains(aircraft.PendingWarnings, w => w.Contains("not clear of RWY 1R —", StringComparison.Ordinal));
    }

    /// <summary>
    /// The aircraft's own runway keeps its assigned end: assigned 19L with only 01R active, the hold at its departure bar is
    /// "Holding Short 19L" and a CROSS of it is refused naming 19L.
    /// </summary>
    [Fact]
    public void OwnRunway_NamedByItsAssignedEnd()
    {
        if (Sfo("ARWY SFO 01R") is not { } ground)
        {
            return;
        }

        GroundNode bar = BarNear19L(ground.Layout);
        HoldShortPoint hold = BarHold(bar, HoldShortReason.DestinationRunway);
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = bar.Position,
            TrueHeading = new TrueHeading(0),
            IsOnGround = true,
            Phases = new PhaseList { AssignedRunway = NavigationDatabase.Instance.GetRunway("SFO", "19L") },
        };
        aircraft.Ground.Layout = ground.Layout;
        aircraft.Ground.AssignedTaxiRoute = RouteInto(bar, hold);
        aircraft.Phases.Add(new HoldingShortPhase(hold));
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ground.Engine.Scenario!.ActiveRunways, ground.Layout));
        ground.Engine.World.AddAircraft(aircraft);

        CommandResult result = ground.Engine.SendCommand(Callsign, "CROSS 1R");

        Assert.Equal("Holding Short 19L", aircraft.Phases.CurrentPhase!.Name);
        Assert.False(result.Success);
        Assert.Equal("Cannot cross destination runway 19L; use LUAW or CTO", result.Message);
    }
}
