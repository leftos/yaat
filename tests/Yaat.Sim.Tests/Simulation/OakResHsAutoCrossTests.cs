using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A hold-short the controller issues after the route was resolved must stop the aircraft, even when
/// the AutoCrossRunway toggle already pre-cleared the crossing.
///
/// Reported at OAK: a C700 at JSX1 cleared <c>RWY 30 TAXI C B W HS C</c> holds short of taxiway C.
/// The controller then issues <c>RES HS 28R</c> — and the aircraft taxis straight across 28R *and*
/// 28L. Taxiway B out of the JSX ramp crosses both north-field parallels on the way south to 30, and
/// AutoCross had marked both crossings cleared at TAXI time; re-labelling the 28R crossing
/// <see cref="HoldShortReason.ExplicitHoldShort"/> without revoking <see cref="HoldShortPoint.IsCleared"/>
/// left the gate open. Issuing <c>RES</c> and then a standalone <c>HS 28R</c> failed differently: the
/// hold-short landed on the far-side bar, so the aircraft crossed 28R and stopped beyond it.
/// </summary>
public class OakResHsAutoCrossTests(ITestOutputHelper output)
{
    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Information).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    private static GroundNode? FindParking(AirportGroundLayout layout, string name) =>
        layout.Nodes.Values.FirstOrDefault(n =>
            (n.Type == GroundNodeType.Parking || n.Type == GroundNodeType.Spot) && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)
        );

    private static bool TickUntil(SimulationEngine engine, int maxSeconds, Func<bool> condition)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            if (condition())
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsHoldingShortOf(AircraftState aircraft, string designator) =>
        aircraft.Phases?.CurrentPhase is HoldingShortPhase hp
        && hp.HoldShort.TargetName is { } name
        && name.Contains(designator, StringComparison.OrdinalIgnoreCase);

    private static HoldShortPoint HoldShortFor(TaxiRoute route, string designator) =>
        Assert.Single(route.HoldShortPoints, h => h.TargetName is { } n && n.Contains(designator, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Spawns the aircraft at JSX1, clears it <c>RWY 30 TAXI C B W HS C</c> with AutoCross ON, and
    /// ticks until it is holding short of taxiway C — the state the controller was in when they
    /// issued the follow-up hold-short.
    /// </summary>
    private (SimulationEngine Engine, AircraftState Aircraft, AirportGroundLayout Layout)? SetUpHoldingShortOfTaxiwayC()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return null;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);

        var parking = FindParking(layout, "JSX1");
        Assert.NotNull(parking);

        var aircraft = new AircraftState
        {
            Callsign = "JSX28R",
            AircraftType = "C700",
            Position = parking.Position,
            TrueHeading = parking.TrueHeading ?? new TrueHeading(0),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(1500),
            },
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;

        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "oak-res-hs-autocross",
            ScenarioName = "RES HS 28R with AutoCross on",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            AutoCrossRunway = true,
        };

        var taxiResult = engine.SendCommand("JSX28R", "RWY 30 TAXI C B W HS C");
        Assert.True(taxiResult.Success, $"RWY 30 TAXI C B W HS C failed: {taxiResult.Message}");

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine($"Route: {route.ToSummary()} ({route.Segments.Count} segments)");
        foreach (var h in route.HoldShortPoints)
        {
            output.WriteLine(
                $"  HS node={h.NodeId} target={h.TargetName} reason={h.Reason} cleared={h.IsCleared} byAutoCross={h.ClearedByAutoCross}"
            );
        }

        // Precondition: AutoCross pre-cleared both parallels, so nothing stops the aircraft at 28R.
        var hs28R = HoldShortFor(route, "28R");
        Assert.True(hs28R.IsCleared, "AutoCross should have pre-cleared the 28R crossing");
        Assert.True(hs28R.ClearedByAutoCross);
        Assert.Contains(route.HoldShortPoints, h => h.TargetName is { } n && n.Contains("28L", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            TickUntil(engine, 600, () => IsHoldingShortOf(aircraft, "C")),
            $"should hold short of taxiway C within 600s; phase={aircraft.Phases?.CurrentPhase?.GetType().Name ?? "null"}"
        );

        return (engine, aircraft, layout);
    }

    /// <summary>
    /// Resumes and then verifies the aircraft comes to rest holding short at the entry-side 28R bar —
    /// the same node the route already carried — without ever putting a wheel on the runway.
    /// </summary>
    private void AssertStopsAtEntrySideOf28R(SimulationEngine engine, AircraftState aircraft, AirportGroundLayout layout)
    {
        var route = aircraft.Ground.AssignedTaxiRoute!;
        var hs28R = HoldShortFor(route, "28R");
        Assert.Equal(HoldShortReason.ExplicitHoldShort, hs28R.Reason);
        Assert.False(hs28R.IsCleared, "the controller-issued hold-short must revoke the AutoCross clearance");
        Assert.False(hs28R.ClearedByAutoCross);

        int entryNodeId = hs28R.NodeId;
        var entryBar = layout.Nodes[entryNodeId];
        Assert.Equal(GroundNodeType.RunwayHoldShort, entryBar.Type);
        output.WriteLine($"28R entry bar = node {entryNodeId} at ({entryBar.Position.Lat:F6},{entryBar.Position.Lon:F6})");

        // Taxiway B runs north→south across 28R, so the route's far-side 28R bar sits at a strictly
        // lower latitude than the entry bar. Anything at or south of it means the aircraft was on the
        // runway. Resolve it from the route, not the airport at large — 28R has bars at many taxiways.
        var barsOnRoute = Bars28ROnRoute(route, layout);
        Assert.Equal(2, barsOnRoute.Count);
        Assert.Equal(entryNodeId, barsOnRoute[0].Id);
        double farSideLat = barsOnRoute[1].Position.Lat;

        double minLat = aircraft.Position.Lat;
        bool reached = TickUntil(
            engine,
            600,
            () =>
            {
                minLat = Math.Min(minLat, aircraft.Position.Lat);
                return IsHoldingShortOf(aircraft, "28R");
            }
        );

        output.WriteLine($"final phase={aircraft.Phases?.CurrentPhase?.GetType().Name ?? "null"} minLat={minLat:F6} farSideLat={farSideLat:F6}");
        Assert.True(reached, $"should come to rest holding short of 28R; phase={aircraft.Phases?.CurrentPhase?.GetType().Name ?? "null"}");

        var holdPhase = (HoldingShortPhase)aircraft.Phases!.CurrentPhase!;
        Assert.Equal(entryNodeId, holdPhase.HoldShort.NodeId);
        Assert.True(minLat > farSideLat, $"aircraft crossed onto RWY 28R: reached lat {minLat:F6}, far-side bar is at {farSideLat:F6}");
    }

    [Fact]
    public void ResHs28R_WithAutoCrossOn_HoldsShortOnEntrySide()
    {
        var setup = SetUpHoldingShortOfTaxiwayC();
        if (setup is null)
        {
            return;
        }
        var (engine, aircraft, layout) = setup.Value;

        var result = engine.SendCommand("JSX28R", "RES HS 28R");
        Assert.True(result.Success, $"RES HS 28R failed: {result.Message}");

        AssertStopsAtEntrySideOf28R(engine, aircraft, layout);
    }

    [Fact]
    public void ResThenStandaloneHs28R_WithAutoCrossOn_HoldsShortOnEntrySide()
    {
        var setup = SetUpHoldingShortOfTaxiwayC();
        if (setup is null)
        {
            return;
        }
        var (engine, aircraft, layout) = setup.Value;

        var resResult = engine.SendCommand("JSX28R", "RES");
        Assert.True(resResult.Success, $"RES failed: {resResult.Message}");

        var hsResult = engine.SendCommand("JSX28R", "HS 28R");
        Assert.True(hsResult.Success, $"HS 28R failed: {hsResult.Message}");

        AssertStopsAtEntrySideOf28R(engine, aircraft, layout);
    }

    [Fact]
    public void Hs28R_AfterTheAircraftIsAlreadyAcross_IsRejected()
    {
        var setup = SetUpHoldingShortOfTaxiwayC();
        if (setup is null)
        {
            return;
        }
        var (engine, aircraft, layout) = setup.Value;

        // Resume without naming a hold-short: AutoCross carries the aircraft across 28R.
        Assert.True(engine.SendCommand("JSX28R", "RES").Success);
        var route = aircraft.Ground.AssignedTaxiRoute!;
        int entry28R = HoldShortFor(route, "28R").NodeId;
        int exit28R = Bars28ROnRoute(route, layout)[1].Id;

        // Wait until the crossing has completed: CrossingRunwayPhase rejects HS at the phase gate,
        // so the AlreadyEntered rejection is what protects the aircraft once it is back in TaxiingPhase
        // on the far side.
        Assert.True(
            TickUntil(engine, 600, () => aircraft.Phases?.CurrentPhase is TaxiingPhase && route.CurrentSegmentIndex > SegmentIndexOf(route, exit28R)),
            $"aircraft should taxi across 28R with AutoCross on; phase={aircraft.Phases?.CurrentPhase?.GetType().Name ?? "null"}"
        );

        var result = engine.SendCommand("JSX28R", "HS 28R");

        Assert.False(result.Success, "HS 28R must be rejected once the aircraft is on or past the runway");
        Assert.Contains("28R", result.Message!);
        Assert.Equal(entry28R, HoldShortFor(route, "28R").NodeId);
    }

    /// <summary>The route's two 28R hold-short bars in traversal order: entry side, then far side.</summary>
    private static List<GroundNode> Bars28ROnRoute(TaxiRoute route, AirportGroundLayout layout)
    {
        var bars = new List<GroundNode>();
        foreach (var seg in route.Segments)
        {
            if (
                layout.Nodes.TryGetValue(seg.ToNodeId, out var node)
                && node.Type == GroundNodeType.RunwayHoldShort
                && (node.RunwayId?.Contains("28R") ?? false)
                && !bars.Contains(node)
            )
            {
                bars.Add(node);
            }
        }
        return bars;
    }

    private static int SegmentIndexOf(TaxiRoute route, int nodeId)
    {
        for (int i = 0; i < route.Segments.Count; i++)
        {
            if (route.Segments[i].ToNodeId == nodeId)
            {
                return i;
            }
        }
        return int.MaxValue;
    }
}
