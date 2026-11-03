using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// CROSS on an aircraft with no ground layout: the crossing is planned on the aircraft's own layout, else the one the
/// dispatch context carries (<see cref="DispatchContext.GroundLayout"/>), and with neither the clearance is refused with
/// "No ground layout available". The hold keeps its unsatisfied clearance and no crossing phase is planned — a released
/// hold with no crossing would strand the aircraft and any command chained behind it.
/// </summary>
public class CrossWithNoGroundLayoutTests(ITestOutputHelper output)
{
    private const string Callsign = "UAL1";

    /// <summary>The SFO harness, or null when navdata or the SFO layout is unavailable.</summary>
    private SfoGround? Sfo() => SfoGroundHarness.Build(output, AutoCrossRunwayMode.Off);

    /// <summary>The 1R/19L hold-short bar nearest the 19L threshold.</summary>
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
    /// The aircraft at <paramref name="bar"/> in <paramref name="phases"/>, started on the SFO layout, which is then stripped:
    /// a restored aircraft the snapshot gave no layout, with the dispatch context supplying none either.
    /// </summary>
    private static AircraftState WithoutLayout(SfoGround ground, GroundNode bar, IReadOnlyList<Phase> phases, TaxiRoute? route)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = bar.Position,
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

        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, ground.Layout));
        aircraft.Ground.Layout = null;
        ground.Engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    private static CommandResult Cross(AircraftState aircraft) =>
        GroundCommandHandler.TryCrossRunway(
            aircraft,
            new CrossRunwayCommand(["19L"], [], Expedite: false),
            layout: null,
            findAircraft: null,
            ActiveRunways.Empty
        );

    [Fact]
    public void CrossAtARunwayHoldWithNoGroundLayout_IsRefusedAndTheHoldStaysUnsatisfied()
    {
        if (Sfo() is not { } ground)
        {
            return;
        }

        GroundNode bar = BarNear19L(ground.Layout);
        var holdPhase = new HoldingShortPhase(BarHold(bar, HoldShortReason.RunwayCrossing), HoldShortArrival.HeldShort);
        AircraftState aircraft = WithoutLayout(ground, bar, [holdPhase], route: null);

        CommandResult result = Cross(aircraft);

        Assert.False(result.Success);
        Assert.Equal("No ground layout available", result.Message);
        Assert.DoesNotContain(holdPhase.Requirements, static r => r.IsSatisfied);
        Assert.DoesNotContain(aircraft.Phases!.Phases, static p => p is CrossingRunwayPhase);
    }

    [Fact]
    public void CrossOfTheDestinationRunwayAtTheBarWithNoGroundLayout_IsRefused()
    {
        if (Sfo() is not { } ground)
        {
            return;
        }

        GroundNode bar = BarNear19L(ground.Layout);
        var holdPhase = new HoldingShortPhase(BarHold(bar, HoldShortReason.DestinationRunway), HoldShortArrival.HeldShort);
        AircraftState aircraft = WithoutLayout(ground, bar, [holdPhase], route: null);

        CommandResult result = Cross(aircraft);

        Assert.False(result.Success);
        Assert.Equal("No ground layout available", result.Message);
        Assert.DoesNotContain(holdPhase.Requirements, static r => r.IsSatisfied);
        Assert.DoesNotContain(aircraft.Phases!.Phases, static p => p is CrossingRunwayPhase);
    }

    [Fact]
    public void CrossOfTheDestinationRunwayOnTheRouteWithNoGroundLayout_IsRefused()
    {
        if (Sfo() is not { } ground)
        {
            return;
        }

        GroundNode bar = BarNear19L(ground.Layout);
        HoldShortPoint hold = BarHold(bar, HoldShortReason.DestinationRunway);
        AircraftState aircraft = WithoutLayout(ground, bar, [new TaxiingPhase()], RouteInto(bar, hold));
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        int segmentsBefore = route.Segments.Count;

        CommandResult result = Cross(aircraft);

        Assert.False(result.Success);
        Assert.Equal("No ground layout available", result.Message);
        Assert.Equal(HoldShortReason.DestinationRunway, hold.Reason);
        Assert.False(hold.IsCleared);
        Assert.Equal(segmentsBefore, route.Segments.Count);
    }

    [Fact]
    public void CrossThenDeleteWithNoGroundLayout_AttachesNoAfterCrossingTrigger()
    {
        if (Sfo() is not { } ground)
        {
            return;
        }

        GroundNode bar = BarNear19L(ground.Layout);
        var holdPhase = new HoldingShortPhase(BarHold(bar, HoldShortReason.RunwayCrossing), HoldShortArrival.HeldShort);
        AircraftState aircraft = WithoutLayout(ground, bar, [holdPhase], route: null);

        CommandResult result = ground.Engine.SendCommand(Callsign, "CROSS 19L; DEL");

        Assert.False(result.Success);
        Assert.DoesNotContain(aircraft.Queue.Blocks, static b => b.Trigger?.Type == BlockTriggerType.AfterRunwayCrossing);
    }

    /// <summary>
    /// A refused release leaves nothing behind: with no layout, the 28L crossing the same CROSS names is not pre-cleared
    /// and the route keeps every hold-short it had.
    /// </summary>
    [Fact]
    public void CrossOfTwoRunwaysAtTheHoldWithNoGroundLayout_LeavesTheRouteUnchanged()
    {
        if (Sfo() is not { } ground)
        {
            return;
        }

        GroundNode bar = BarNear19L(ground.Layout);
        HoldShortPoint hold = BarHold(bar, HoldShortReason.RunwayCrossing);
        TaxiRoute route = RouteInto(bar, hold);
        var ahead = new HoldShortPoint
        {
            NodeId = hold.NodeId + 1000,
            Reason = HoldShortReason.RunwayCrossing,
            TargetName = "28L/10R",
        };
        route.HoldShortPoints.Add(ahead);
        AircraftState aircraft = WithoutLayout(ground, bar, [new HoldingShortPhase(hold, HoldShortArrival.HeldShort)], route);

        CommandResult result = GroundCommandHandler.TryCrossRunway(
            aircraft,
            new CrossRunwayCommand(["19L", "28L"], [], Expedite: false),
            layout: null,
            findAircraft: null,
            ActiveRunways.Empty
        );

        Assert.False(result.Success);
        Assert.Equal("No ground layout available", result.Message);
        Assert.False(ahead.IsCleared);
        Assert.False(hold.IsCleared);
        Assert.Equal(2, route.HoldShortPoints.Count);
    }

    /// <summary>
    /// The dispatch context's layout is the crossing's layout when the aircraft carries none: the CROSS succeeds and
    /// plans its crossing, and the layout is never written back into the aircraft.
    /// </summary>
    [Fact]
    public void CrossAtTheHoldWithTheDispatchContextLayout_CrossesAndLeavesTheAircraftLayoutUnset()
    {
        if (Sfo() is not { } ground)
        {
            return;
        }

        GroundNode bar = BarNear19L(ground.Layout);
        var holdPhase = new HoldingShortPhase(BarHold(bar, HoldShortReason.RunwayCrossing), HoldShortArrival.HeldShort);
        AircraftState aircraft = WithoutLayout(ground, bar, [holdPhase], route: null);

        CommandResult result = GroundCommandHandler.TryCrossRunway(
            aircraft,
            new CrossRunwayCommand(["19L"], [], Expedite: false),
            ground.Layout,
            findAircraft: null,
            ActiveRunways.Empty
        );

        Assert.True(result.Success, result.Message);
        Assert.Equal("Cross 19L", result.Message);
        Assert.Contains(aircraft.Phases!.Phases, static p => p is CrossingRunwayPhase);
        Assert.Contains(holdPhase.Requirements, static r => r is { Type: ClearanceType.RunwayCrossing, IsSatisfied: true });
        Assert.Null(aircraft.Ground.Layout);
    }

    /// <summary>RES across an armed hold is a hold release too, and refuses the same way with no layout.</summary>
    [Fact]
    public void ResumeAcrossAnArmedHoldWithNoGroundLayout_IsRefusedAndTheHoldStaysUnsatisfied()
    {
        if (Sfo() is not { } ground)
        {
            return;
        }

        GroundNode bar = BarNear19L(ground.Layout);
        var holdPhase = new HoldingShortPhase(BarHold(bar, HoldShortReason.RunwayCrossing), HoldShortArrival.HeldShort);
        AircraftState aircraft = WithoutLayout(ground, bar, [holdPhase, new FollowingPhase("UAL2")], route: null);

        CommandResult result = ground.Engine.SendCommand(Callsign, "RES CROSS 19L");

        Assert.False(result.Success);
        Assert.Equal("No ground layout available", result.Message);
        Assert.DoesNotContain(holdPhase.Requirements, static r => r.IsSatisfied);
        Assert.DoesNotContain(aircraft.Phases!.Phases, static p => p is CrossingRunwayPhase);
    }
}
