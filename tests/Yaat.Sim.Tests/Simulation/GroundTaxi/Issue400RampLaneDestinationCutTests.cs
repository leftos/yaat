using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Issue #400: OAK ramp taxilanes TE and TC run parallel off taxiway T and only join each other at their southern
/// end. Spot 22 hangs off TC's northern ramp cluster; TE's northern cluster is a separate graph island ~330 ft
/// away across open apron. <c>TAXI V T TE @22</c> therefore had no graph route from TE's end to the spot, and the
/// handler dropped TE and quietly substituted TC ("Taxi via V T TC @22 [taxiing via TC — not in the route issued;
/// unable via TE …]"). The pilot can simply cross the apron from TE onto TC, so the clearance is honoured with a
/// free-space leg at the destination end — the destination-side twin of the issue #396 start-side cut.
/// </summary>
public class Issue400RampLaneDestinationCutTests
{
    private const int BehaviourWindowSec = 420;
    private const int MaxZeroProgressSec = 30;
    private const double ArrivalToleranceFt = 40.0;

    private readonly ITestOutputHelper _output;

    public Issue400RampLaneDestinationCutTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? BuildEngine(out AirportGroundLayout? layout)
    {
        layout = null;
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        layout = groundData.GetLayout("OAK");
        if (layout is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(_output)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .EnableCategory("RampLaneReposition", LogLevel.Debug)
            .InitializeSimLog();
        return new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 0,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                SoloTrainingMode = false,
            },
        };
    }

    /// <summary>A jet stopped on taxiway V at spot 7 (its north-west end), nose along V toward T.</summary>
    private static AircraftState AddStoppedOnV(SimulationEngine engine, AirportGroundLayout layout, string callsign)
    {
        var spot = layout.FindSpotNodeByName("7");
        Assert.True(spot is not null, "spot 7 on taxiway V not found in the OAK layout");

        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = spot.Position,
            TrueHeading = new TrueHeading(110),
            Altitude = 9,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            AirportId = "OAK",
            FlightPlan = new AircraftFlightPlan { Departure = "KLAX", Destination = "KOAK" },
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    private static bool Traverses(TaxiRoute route, string twy) =>
        route.Segments.Any(s =>
            s.TaxiwayName.Split([' ', '-', '/', ','], StringSplitOptions.RemoveEmptyEntries)
                .Any(tok => string.Equals(tok, twy, StringComparison.OrdinalIgnoreCase))
        );

    /// <summary>A free-space leg: both endpoints are layout nodes but no layout edge joins them.</summary>
    private static bool IsFreeSpaceLeg(TaxiRouteSegment seg) =>
        (seg.FromNodeId >= 0) && (seg.ToNodeId >= 0) && !seg.Edge.FromNode.Edges.Any(e => e.HasNode(seg.ToNodeId));

    private static double LengthFt(TaxiRouteSegment seg) =>
        GeoMath.DistanceNm(seg.Edge.FromNode.Position, seg.Edge.ToNode.Position) * GeoMath.FeetPerNm;

    private static bool OnTaxiway(GroundNode node, string twy) => node.Edges.Any(e => e.MatchesTaxiway(twy));

    [Fact]
    public void TaxiVTTeToSpot22_CutsAcrossTheApronOntoTc()
    {
        var engine = BuildEngine(out var layout);
        if (engine is null || layout is null)
        {
            return;
        }

        var aircraft = AddStoppedOnV(engine, layout, "SWA690");
        var result = engine.SendCommand("SWA690", "TAXI V T TE @22");
        _output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        _output.WriteLine("route: " + string.Join(" ", route.Segments.Select(s => $"{s.FromNodeId}-{s.ToNodeId}({s.TaxiwayName})")));

        Assert.True(Traverses(route, "V"), "route must taxi along V");
        Assert.True(Traverses(route, "T"), "route must taxi along T");
        Assert.True(Traverses(route, "TE"), "route must taxi along TE as cleared");
        Assert.Equal("22", route.DestinationParking);

        var spot22 = layout.FindParkingByName("22")!;
        Assert.Equal(spot22.Id, route.Segments[^1].ToNodeId);

        var cuts = route.Segments.Where(IsFreeSpaceLeg).ToList();
        Assert.True(cuts.Count == 1, $"expected exactly one free-space leg, found {cuts.Count}");
        var cut = cuts[0];
        Assert.True(OnTaxiway(cut.Edge.FromNode, "TE"), $"the cut must leave from a TE node, not #{cut.FromNodeId}");
        Assert.True(LengthFt(cut) <= RampLaneReposition.MaxCrossingFt, $"crossing {LengthFt(cut):F0} ft exceeds the cap");
        int cutIndex = route.Segments.IndexOf(cut);
        Assert.False(
            Traverses(new TaxiRoute { Segments = route.Segments.Take(cutIndex).ToList(), HoldShortPoints = [] }, "TC"),
            "TC is only used after the cut"
        );

        Assert.DoesNotContain(route.Warnings, w => w.Contains("unable", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(route.Warnings, w => w.Contains("not in the route issued", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("Taxi via V T TE", result.Message);
        Assert.DoesNotContain("unable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TaxiVTTeToSpot22_AircraftReachesTheSpot()
    {
        var engine = BuildEngine(out var layout);
        if (engine is null || layout is null)
        {
            return;
        }

        var aircraft = AddStoppedOnV(engine, layout, "SWA690");
        var result = engine.SendCommand("SWA690", "TAXI V T TE @22");
        Assert.True(result.Success, result.Message);

        var spot22 = layout.FindParkingByName("22")!;
        var evaluator = new TaxiBudgetEvaluator();
        for (int t = 1; t <= BehaviourWindowSec; t++)
        {
            engine.TickOneSecond();
            evaluator.Observe(aircraft);
            double toSpotFt = GeoMath.DistanceNm(aircraft.Position, spot22.Position) * GeoMath.FeetPerNm;
            if ((aircraft.Phases?.CurrentPhase is AtParkingPhase) && (toSpotFt <= ArrivalToleranceFt))
            {
                _output.WriteLine($"t={t}s: parked at 22; {evaluator.DiagnosticSummary()}");
                Assert.True(
                    evaluator.MaxConsecutiveZeroProgressSec <= MaxZeroProgressSec,
                    $"{aircraft.Callsign} sat unmoving for {evaluator.MaxConsecutiveZeroProgressSec}s. {evaluator.DiagnosticSummary()}"
                );
                return;
            }
        }

        Assert.Fail(
            $"{aircraft.Callsign} never parked at 22 within {BehaviourWindowSec}s; now on {aircraft.Ground.CurrentTaxiway ?? "(none)"} "
                + $"phase {aircraft.Phases?.CurrentPhase?.Name}. {evaluator.DiagnosticSummary()}"
        );
    }

    [Fact]
    public void TaxiVTTcToSpot22_ConnectedLaneNeedsNoCut()
    {
        var engine = BuildEngine(out var layout);
        if (engine is null || layout is null)
        {
            return;
        }

        var aircraft = AddStoppedOnV(engine, layout, "SWA690");
        var result = engine.SendCommand("SWA690", "TAXI V T TC @22");
        Assert.True(result.Success, result.Message);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.DoesNotContain(route.Segments, IsFreeSpaceLeg);
        Assert.True(Traverses(route, "TC"));
        Assert.Empty(route.Warnings);
    }

    [Fact]
    public void TaxiVTTeToSpot23_OwnLaneNeedsNoCut()
    {
        var engine = BuildEngine(out var layout);
        if (engine is null || layout is null)
        {
            return;
        }

        var aircraft = AddStoppedOnV(engine, layout, "SWA690");
        var result = engine.SendCommand("SWA690", "TAXI V T TE @23");
        Assert.True(result.Success, result.Message);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.DoesNotContain(route.Segments, IsFreeSpaceLeg);
        Assert.True(Traverses(route, "TE"));
        Assert.Empty(route.Warnings);
    }

    [Fact]
    public void CutRoute_SurvivesSnapshotRoundTrip()
    {
        var engine = BuildEngine(out var layout);
        if (engine is null || layout is null)
        {
            return;
        }

        var aircraft = AddStoppedOnV(engine, layout, "SWA690");
        Assert.True(engine.SendCommand("SWA690", "TAXI V T TE @22").Success);
        var route = aircraft.Ground.AssignedTaxiRoute!;
        Assert.Contains(route.Segments, IsFreeSpaceLeg);

        var restored = TaxiRoute.FromSnapshot(route.ToSnapshot(), layout);
        Assert.NotNull(restored);
        Assert.Equal(route.Segments.Count, restored.Segments.Count);
        Assert.Equal(route.Segments.Select(s => (s.FromNodeId, s.ToNodeId)), restored.Segments.Select(s => (s.FromNodeId, s.ToNodeId)));
        Assert.Equal(route.Segments.Count(IsFreeSpaceLeg), restored.Segments.Count(IsFreeSpaceLeg));
        Assert.Equal("22", restored.DestinationParking);
    }
}
