using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Phases.Ground;

/// <summary>
/// A <see cref="TaxiingPhase"/> whose route is already complete when it first ticks ends the way a route that runs out
/// does: the terminal phase follows, and a departure clearance stored during the taxi is applied. Ending with nothing
/// after it leaves a grounded aircraft with no phase, where no ground command reaches it.
///
/// <para>The rig: at KOAK a B738 from GA16 via F and C, or via F, C, B to the runway 28R bar (the route
/// <c>ResPreClearTaxiwayHoldShortTests</c> taxis), standing still on the route's last node with every segment already
/// flown.</para>
/// </summary>
public sealed class TaxiingPhaseCompleteOnEntryTests(ITestOutputHelper output)
{
    private const string Stand = "GA16";
    private const string DepartureRunway = "28R";

    [Fact]
    public void RouteCompleteOnEntry_NoDestination_HoldsShortOfTheLastTaxiway()
    {
        AirportGroundLayout? layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        // F C names no destination, so the route ends in a route-incomplete hold short of C; an aircraft already there holds
        // short of C where it stands.
        (AircraftState aircraft, PhaseContext ctx) = StandAtRouteEnd(layout, ["F", "C"], destinationRunway: null);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        HoldShortPoint endHold = Assert.IsType<HoldShortPoint>(route.GetHoldShortAt(route.Segments[^1].ToNodeId));
        Assert.Equal(HoldShortReason.RouteIncomplete, endHold.Reason);
        Assert.Equal("C", endHold.TargetName);

        PhaseRunner.Tick(aircraft, ctx);

        output.WriteLine($"phase after first tick: {aircraft.Phases?.CurrentPhase?.Name ?? "(none)"}");
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Same(endHold, holding.HoldShort);
    }

    [Fact]
    public void RouteCompleteOnEntry_AtTheDestinationRunwayBarWithAStoredTakeoffClearance_LinesUp()
    {
        AirportGroundLayout? layout = LoadOak();
        if (layout is null)
        {
            return;
        }

        (AircraftState aircraft, PhaseContext ctx) = StandAtRouteEnd(layout, ["F", "C", "B"], DepartureRunway);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        HoldShortPoint destinationBar = route.HoldShortPoints[^1];
        Assert.Equal(HoldShortReason.DestinationRunway, destinationBar.Reason);
        Assert.Equal(route.Segments[^1].ToNodeId, destinationBar.NodeId);

        CommandResult stored = DepartureClearanceHandler.StoreDepartureClearanceDuringTaxi(
            aircraft,
            ClearanceType.ClearedForTakeoff,
            new DefaultDeparture(),
            null
        );
        Assert.True(stored.Success, $"test setup: CTO not stored: {stored.Message}");

        PhaseRunner.Tick(aircraft, ctx);

        output.WriteLine($"phase after first tick: {aircraft.Phases?.CurrentPhase?.Name ?? "(none)"}");
        Assert.IsType<LineUpPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Null(aircraft.Phases?.DepartureClearance);
    }

    private static AirportGroundLayout? LoadOak()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb is null ? null : new TestAirportGroundData().GetLayout("OAK");
    }

    /// <summary>
    /// The B738 stopped on the last node of its route from GA16 via <paramref name="taxiways"/>, with the route's cursor
    /// already past its last segment and a <see cref="TaxiingPhase"/> started on it.
    /// </summary>
    private (AircraftState Aircraft, PhaseContext Ctx) StandAtRouteEnd(AirportGroundLayout layout, List<string> taxiways, string? destinationRunway)
    {
        GroundNode stand = Assert.IsType<GroundNode>(layout.FindParkingByName(Stand));
        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: stand.Id,
            taxiwayNames: taxiways,
            out string? failReason,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("B738"),
                DestinationRunway = destinationRunway,
            },
            AircraftCategory.Jet,
            WakeTurbulenceData.WakeClass.Large
        );
        Assert.Null(failReason);
        Assert.NotNull(route);

        TaxiRouteSegment last = route.Segments[^1];
        route.CurrentSegmentIndex = route.Segments.Count;
        Assert.True(route.IsComplete);

        GroundNode end = layout.Nodes[last.ToNodeId];
        output.WriteLine($"route via {string.Join(" ", taxiways)}: {route.Segments.Count} segments, ends at node {end.Id}");

        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = end.Position,
            TrueHeading = new TrueHeading(last.Edge.ArrivalBearing),
            TrueTrack = new TrueHeading(last.Edge.ArrivalBearing),
            IsOnGround = true,
            IndicatedAirspeed = 0,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK", Route = "" },
        };
        aircraft.Ground.AssignedTaxiRoute = route;
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new TaxiingPhase());

        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0.25,
            GroundLayout = layout,
            AircraftLookup = null,
            Logger = NullLogger.Instance,
        };
        aircraft.Phases.Start(ctx);
        return (aircraft, ctx);
    }
}
