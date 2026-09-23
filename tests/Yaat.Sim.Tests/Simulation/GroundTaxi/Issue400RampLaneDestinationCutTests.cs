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
/// unable via TE …]"). The pilot can simply cross the apron from TE, so the clearance is honoured with free-space legs
/// at the destination end — across to a point on the stand's centreline, then in on the stand heading — the
/// destination-side twin of the issue #396 start-side cut.
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
        GroundNode? spot = layout.FindSpotNodeByName("7");
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
            Phases = new PhaseList(),
        };
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

    /// <summary>
    /// A free-space leg: a virtual segment (the crossing to a roll-in point, or the roll-in itself), or a segment between
    /// two layout nodes no layout edge joins.
    /// </summary>
    private static bool IsFreeSpaceLeg(TaxiRouteSegment seg) =>
        VirtualNode.IsVirtualEdge(seg.Edge.Edge)
        || ((seg.FromNodeId >= 0) && (seg.ToNodeId >= 0) && !seg.Edge.FromNode.Edges.Any(e => e.HasNode(seg.ToNodeId)));

    /// <summary>
    /// The route ends in the two free-space legs of a roll-in: a crossing to a point on the stand's centreline, then a
    /// leg from that point into the stand on the stand heading, together within the crossing cap.
    /// </summary>
    private static void AssertRollsInToStand(TaxiRoute route, GroundNode stand)
    {
        TaxiRouteSegment crossing = route.Segments[^2];
        TaxiRouteSegment rollIn = route.Segments[^1];
        Assert.True(VirtualNode.IsVirtualEdge(crossing.Edge.Edge), $"the second-last segment should be the apron crossing: {crossing.TaxiwayName}");
        Assert.True(VirtualNode.IsVirtualEdge(rollIn.Edge.Edge), $"the last segment should be the roll-in: {rollIn.TaxiwayName}");
        Assert.Equal(crossing.ToNodeId, rollIn.FromNodeId);
        Assert.Equal(stand.Id, rollIn.ToNodeId);
        double standHeading = Assert.IsType<TrueHeading>(stand.TrueHeading).Degrees;
        Assert.True(
            GeoMath.AbsBearingDifference(rollIn.Edge.ArrivalBearing, standHeading) <= 1.0,
            $"the roll-in runs {rollIn.Edge.ArrivalBearing:F1}°, not on the {standHeading:F1}° stand heading"
        );
        double driveFt = LengthFt(crossing) + LengthFt(rollIn);
        Assert.True(driveFt <= RampLaneReposition.MaxCrossingFt, $"the drive of {driveFt:F0} ft exceeds the cap");
    }

    private static double LengthFt(TaxiRouteSegment seg) =>
        GeoMath.DistanceNm(seg.Edge.FromNode.Position, seg.Edge.ToNode.Position) * GeoMath.FeetPerNm;

    private static bool OnTaxiway(GroundNode node, string twy) => node.Edges.Any(e => e.MatchesTaxiway(twy));

    [Fact]
    public void TaxiVTTeToSpot22_CutsAcrossTheApronOntoTc()
    {
        SimulationEngine? engine = BuildEngine(out AirportGroundLayout? layout);
        if (engine is null || layout is null)
        {
            return;
        }

        AircraftState aircraft = AddStoppedOnV(engine, layout, "SWA690");
        CommandResult result = engine.SendCommand("SWA690", "TAXI V T TE @22");
        _output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        _output.WriteLine("route: " + string.Join(" ", route.Segments.Select(s => $"{s.FromNodeId}-{s.ToNodeId}({s.TaxiwayName})")));

        Assert.True(Traverses(route, "V"), "route must taxi along V");
        Assert.True(Traverses(route, "T"), "route must taxi along T");
        Assert.True(Traverses(route, "TE"), "route must taxi along TE as cleared");
        Assert.Equal("22", route.DestinationParking);

        GroundNode spot22 = layout.FindParkingByName("22")!;
        Assert.Equal(spot22.Id, route.Segments[^1].ToNodeId);

        var cuts = route.Segments.Where(IsFreeSpaceLeg).ToList();
        Assert.True(cuts.Count == 2, $"expected the crossing and the roll-in, found {cuts.Count} free-space legs");
        AssertRollsInToStand(route, spot22);
        TaxiRouteSegment cut = cuts[0];
        Assert.True(OnTaxiway(cut.Edge.FromNode, "TE"), $"the cut must leave from a TE node, not #{cut.FromNodeId}");
        int cutIndex = route.Segments.IndexOf(cut);
        Assert.False(
            Traverses(new TaxiRoute { Segments = [.. route.Segments.Take(cutIndex)], HoldShortPoints = [] }, "TC"),
            "TC is only used after the cut"
        );

        Assert.DoesNotContain(route.Warnings, w => w.Contains("unable", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(route.Warnings, w => w.Contains("not in the route issued", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("Taxi via V T TE", result.Message);
        Assert.DoesNotContain("unable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same cut with V left out of the clearance (<c>TAXI T TE @22</c>) by an aircraft known to be on V: the route
    /// still bridges along V onto T, and V is the taxiway it stands on, not a deviation, so no note names it.
    /// </summary>
    [Fact]
    public void TaxiTTeToSpot22_OccupyingV_NoNoteForV()
    {
        SimulationEngine? engine = BuildEngine(out AirportGroundLayout? layout);
        if (engine is null || layout is null)
        {
            return;
        }

        AircraftState aircraft = AddStoppedOnV(engine, layout, "SWA690");
        aircraft.Ground.CurrentTaxiway = "V";
        CommandResult result = engine.SendCommand("SWA690", "TAXI T TE @22");
        _output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        _output.WriteLine("route: " + string.Join(" ", route.Segments.Select(s => $"{s.FromNodeId}-{s.ToNodeId}({s.TaxiwayName})")));

        Assert.True(Traverses(route, "V"), "route must bridge along V");
        Assert.Contains(route.Segments, IsFreeSpaceLeg);
        AssertRollsInToStand(route, layout.FindParkingByName("22")!);
        Assert.Equal("22", route.DestinationParking);
        Assert.DoesNotContain(route.Warnings, w => w.Contains("not in the route issued", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("not in the route issued", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TaxiVTTeToSpot22_AircraftReachesTheSpot()
    {
        SimulationEngine? engine = BuildEngine(out AirportGroundLayout? layout);
        if (engine is null || layout is null)
        {
            return;
        }

        AircraftState aircraft = AddStoppedOnV(engine, layout, "SWA690");
        CommandResult result = engine.SendCommand("SWA690", "TAXI V T TE @22");
        Assert.True(result.Success, result.Message);

        GroundNode spot22 = layout.FindParkingByName("22")!;
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
        SimulationEngine? engine = BuildEngine(out AirportGroundLayout? layout);
        if (engine is null || layout is null)
        {
            return;
        }

        AircraftState aircraft = AddStoppedOnV(engine, layout, "SWA690");
        CommandResult result = engine.SendCommand("SWA690", "TAXI V T TC @22");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.DoesNotContain(route.Segments, IsFreeSpaceLeg);
        Assert.True(Traverses(route, "TC"));
        Assert.Empty(route.Warnings);
    }

    [Fact]
    public void TaxiVTTeToSpot23_OwnLaneNeedsNoCut()
    {
        SimulationEngine? engine = BuildEngine(out AirportGroundLayout? layout);
        if (engine is null || layout is null)
        {
            return;
        }

        AircraftState aircraft = AddStoppedOnV(engine, layout, "SWA690");
        CommandResult result = engine.SendCommand("SWA690", "TAXI V T TE @23");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.DoesNotContain(route.Segments, IsFreeSpaceLeg);
        Assert.True(Traverses(route, "TE"));
        Assert.Empty(route.Warnings);
    }

    [Fact]
    public void CutRoute_SurvivesSnapshotRoundTrip()
    {
        SimulationEngine? engine = BuildEngine(out AirportGroundLayout? layout);
        if (engine is null || layout is null)
        {
            return;
        }

        AircraftState aircraft = AddStoppedOnV(engine, layout, "SWA690");
        Assert.True(engine.SendCommand("SWA690", "TAXI V T TE @22").Success);
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute!;
        Assert.Contains(route.Segments, IsFreeSpaceLeg);

        var restored = TaxiRoute.FromSnapshot(route.ToSnapshot(), layout);
        Assert.NotNull(restored);
        Assert.Equal(route.Segments.Count, restored.Segments.Count);
        Assert.Equal(route.Segments.Select(s => (s.FromNodeId, s.ToNodeId)), restored.Segments.Select(s => (s.FromNodeId, s.ToNodeId)));
        Assert.Equal(route.Segments.Count(IsFreeSpaceLeg), restored.Segments.Count(IsFreeSpaceLeg));
        GroundNode spot22 = layout.FindParkingByName("22")!;
        AssertRollsInToStand(route, spot22);
        AssertRollsInToStand(restored, spot22);
        Assert.Equal(route.Segments[^2].Edge.ToNode.Position, restored.Segments[^2].Edge.ToNode.Position);
        Assert.Equal("22", restored.DestinationParking);
    }
}
