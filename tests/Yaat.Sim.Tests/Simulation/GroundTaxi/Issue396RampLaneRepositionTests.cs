using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Issue #396: SFO Terminal 1 ramp lanes M3 / M4 / M5 are parallel taxilanes on one ramp with no painted
/// connectors between them, and M4 only joins the graph at M1. A <c>TAXI M4 …</c> (or <c>M5</c>) from gate B20S,
/// or from an aircraft already rolling on M3, is honoured by the pilot cutting across the open apron onto the
/// named lane and continuing on the graph from there — no "unable via M4" drop. The drop-and-warn ladder
/// (<c>TryDropGateLeadOut</c>) stays as the fallback for a gate-adjacent lane that is not a sibling ramp lane,
/// and a taxiway across active runways (41-15 → A) is still rejected outright.
/// </summary>
public class Issue396RampLaneRepositionTests
{
    private const int BehaviourWindowSec = 240;
    private const int MaxZeroProgressSec = 30;
    private const double LaneAcquireToleranceFt = 40.0;

    private readonly ITestOutputHelper _output;

    public Issue396RampLaneRepositionTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? BuildEngine(out AirportGroundLayout? layout) => BuildEngine(out layout, soloTraining: false);

    private SimulationEngine? BuildEngine(out AirportGroundLayout? layout, bool soloTraining)
    {
        layout = null;
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        layout = groundData.GetLayout("SFO");
        if (layout is null)
        {
            return null;
        }

        return BuildEngine(groundData, "SFO", soloTraining);
    }

    private SimulationEngine BuildEngine(IAirportGroundData groundData, string airportId, bool soloTraining)
    {
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
                PrimaryAirportId = airportId,
                SoloTrainingMode = soloTraining,
            },
        };
    }

    private static AircraftState AddParkedAt(
        SimulationEngine engine,
        AirportGroundLayout layout,
        string callsign,
        string type,
        string parking,
        string airportId
    )
    {
        var gate = layout.FindParkingByName(parking);
        Assert.True(gate is not null, $"parking {parking} not found in the {airportId} layout");

        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = gate.Position,
            TrueHeading = gate.TrueHeading ?? new TrueHeading(0),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            AirportId = airportId,
            FlightPlan = new AircraftFlightPlan { Departure = "K" + airportId, Destination = "KLAX" },
        };
        // A scenario spawn at a gate installs AtParkingPhase; without it a TAXI never reaches the tower path.
        var init = AircraftInitializer.InitializeAtParking(gate, 13);
        aircraft.Phases = init.Phases;
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    private static bool Traverses(TaxiRoute route, string twy) =>
        route.Segments.Any(s =>
            s.TaxiwayName.Split([' ', '-', '/', ','], StringSplitOptions.RemoveEmptyEntries)
                .Any(tok => string.Equals(tok, twy, StringComparison.OrdinalIgnoreCase))
        );

    private static void AssertRepositionsOnto(TaxiRoute route, string lane, CommandResult result)
    {
        var first = route.Segments[0];
        Assert.True(first.FromNodeId < 0, $"the route must begin with a free-space leg from the aircraft, not graph node {first.FromNodeId}");
        Assert.Equal(lane, first.TaxiwayName);
        Assert.True(Traverses(route, lane), $"route must taxi along {lane}");
        Assert.DoesNotContain(route.Warnings, w => w.Contains("unable", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("unable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TaxiM4FromB20S_RepositionsOntoM4()
    {
        var engine = BuildEngine(out var layout);
        if (engine is null || layout is null)
        {
            return;
        }

        var aircraft = AddParkedAt(engine, layout, "AAL436", "B77W", "B20S", "SFO");
        var result = engine.SendCommand("AAL436", "TAXI M4 M1 A H GL L LF F 28L HS 1L");
        _output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        _output.WriteLine("route: " + string.Join(" ", route.Segments.Select(s => s.TaxiwayName)));
        AssertRepositionsOnto(route, "M4", result);
        Assert.True(Traverses(route, "M1"), "route must reach M1");
        Assert.True(Traverses(route, "A"), "route must reach A");

        // The rest of the clearance is honored: hold short of 1L en route, line-up stop at 28L.
        Assert.Contains(
            route.HoldShortPoints,
            h => h.Reason == HoldShortReason.ExplicitHoldShort && h.TargetName is { } name && RunwayIdentifier.Parse(name).Contains("1L")
        );
        Assert.Contains(
            route.HoldShortPoints,
            h => h.Reason == HoldShortReason.DestinationRunway && h.TargetName is { } name && RunwayIdentifier.Parse(name).Contains("28L")
        );
    }

    [Fact]
    public void TaxiM5FromB20S_RepositionsOntoM5()
    {
        var engine = BuildEngine(out var layout);
        if (engine is null || layout is null)
        {
            return;
        }

        var aircraft = AddParkedAt(engine, layout, "AAL436", "B77W", "B20S", "SFO");
        var result = engine.SendCommand("AAL436", "TAXI M5 M1 A A1 1R");
        Assert.True(result.Success, result.Message);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        _output.WriteLine("route: " + string.Join(" ", route.Segments.Select(s => s.TaxiwayName)));
        AssertRepositionsOnto(route, "M5", result);
        Assert.False(Traverses(route, "M4"), "M4 is crossed in free space, never taxied along");
        Assert.True(Traverses(route, "M1"), "route must reach M1");
    }

    [Fact]
    public void TaxiM4FromB20S_SoloReadbackIsTheNormalClearance()
    {
        var engine = BuildEngine(out var layout, soloTraining: true);
        if (engine is null || layout is null)
        {
            return;
        }

        var aircraft = AddParkedAt(engine, layout, "AAL436", "B77W", "B20S", "SFO");
        var result = engine.SendCommand("AAL436", "TAXI M4 M1 A A1 1R");
        Assert.True(result.Success, result.Message);

        var readback = aircraft.PendingPilotTransmissions.FirstOrDefault(t => t.Kind == Yaat.Sim.Pilot.PilotTransmissionKind.Readback);
        Assert.True(readback is not null, "the solo pilot must read the taxi clearance back");
        _output.WriteLine($"readback: {readback.Text} / {readback.SpeechText}");
        Assert.DoesNotContain("unable", readback.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("M4 M1 A A1", readback.Text);
        Assert.DoesNotContain("unable", readback.SpeechText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TaxiM4FromB20S_AircraftActuallyCrossesOntoM4AndReachesM1()
    {
        var engine = BuildEngine(out var layout);
        if (engine is null || layout is null)
        {
            return;
        }

        var aircraft = AddParkedAt(engine, layout, "AAL436", "B77W", "B20S", "SFO");
        var result = engine.SendCommand("AAL436", "TAXI M4 M1 A A1 1R");
        Assert.True(result.Success, result.Message);
        var target = aircraft.Ground.AssignedTaxiRoute!.Segments[0].Edge.ToNode;

        TickUntilOnLane(engine, aircraft, target, "M1");
    }

    [Fact]
    public void TaxiM4WhileRollingOnM3_CrossesOntoM4()
    {
        var engine = BuildEngine(out var layout);
        if (engine is null || layout is null)
        {
            return;
        }

        var aircraft = AddParkedAt(engine, layout, "UAL512", "B739", "B20S", "SFO");
        Assert.True(engine.SendCommand("UAL512", "TAXI M3 M1 A A1 1R").Success);

        // Roll down M3 for a while, then re-clear via M4 mid-lane.
        for (int t = 0; t < 45; t++)
        {
            engine.TickOneSecond();
        }

        Assert.Equal("M3", aircraft.Ground.CurrentTaxiway);
        Assert.True(aircraft.GroundSpeed > 1, "aircraft should be rolling on M3 before the re-clearance");

        var result = engine.SendCommand("UAL512", "TAXI M4 M1 A A1 1R");
        _output.WriteLine($"re-clearance: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        AssertRepositionsOnto(route, "M4", result);
        double crossingFt = GeoMath.DistanceNm(route.Segments[0].Edge.FromNode.Position, route.Segments[0].Edge.ToNode.Position) * GeoMath.FeetPerNm;
        Assert.True(crossingFt <= 300, $"a mid-lane switch is a short cut across the alley, not {crossingFt:F0} ft");

        TickUntilOnLane(engine, aircraft, route.Segments[0].Edge.ToNode, "M1");
    }

    /// <summary>
    /// Ticks until the aircraft has passed within <see cref="LaneAcquireToleranceFt"/> of the lane-acquire node and
    /// then reports <paramref name="laterTaxiway"/> as its current taxiway, failing on a stall or the time cap.
    /// </summary>
    private void TickUntilOnLane(SimulationEngine engine, AircraftState aircraft, GroundNode target, string laterTaxiway)
    {
        var evaluator = new TaxiBudgetEvaluator();
        bool acquired = false;
        for (int t = 1; t <= BehaviourWindowSec; t++)
        {
            engine.TickOneSecond();
            evaluator.Observe(aircraft);
            double toTargetFt = GeoMath.DistanceNm(aircraft.Position, target.Position) * GeoMath.FeetPerNm;
            acquired |= toTargetFt <= LaneAcquireToleranceFt;
            if (acquired && string.Equals(aircraft.Ground.CurrentTaxiway, laterTaxiway, StringComparison.OrdinalIgnoreCase))
            {
                _output.WriteLine($"t={t}s: on {laterTaxiway} after acquiring #{target.Id}; {evaluator.DiagnosticSummary()}");
                Assert.True(
                    evaluator.MaxConsecutiveZeroProgressSec <= MaxZeroProgressSec,
                    $"{aircraft.Callsign} sat unmoving for {evaluator.MaxConsecutiveZeroProgressSec}s. {evaluator.DiagnosticSummary()}"
                );
                return;
            }
        }

        Assert.Fail(
            $"{aircraft.Callsign} never {(acquired ? $"reached {laterTaxiway} after" : "came within " + LaneAcquireToleranceFt + " ft of")} "
                + $"node #{target.Id} in {BehaviourWindowSec}s; now on {aircraft.Ground.CurrentTaxiway ?? "(none)"} "
                + $"phase {aircraft.Phases?.CurrentPhase?.Name}. {evaluator.DiagnosticSummary()}"
        );
    }

    [Fact]
    public void TaxiAcrossRunwaysFromGate_StillRejected()
    {
        var engine = BuildEngine(out var layout);
        if (engine is null || layout is null)
        {
            return;
        }

        AddParkedAt(engine, layout, "N70234", "C182", "41-15", "SFO");
        var result = engine.SendCommand("N70234", "TAXI A E 28R HS E");
        _output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.False(
            result.Success,
            $"taxiway A lies across active runways from 41-15 — the clearance must be rejected, not rerouted: {result.Message}"
        );
        Assert.Contains("via A", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The gate-adjacent drop-and-warn ladder remains the fallback for a lane next to the gate that is neither
    /// graph-connected nor a sibling ramp lane of the gate's own lane: here lettered taxiway K, parallel to lane
    /// M3 but with no ramp connection. The reposition declines (K is not an M-lane), so K is dropped with a
    /// warning and the rest taxis.
    /// </summary>
    [Fact]
    public void NonSiblingGateAdjacentTaxiway_StillDroppedWithWarning()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var layout = GeoJsonParser.Parse("TST", MiniRampGeoJson(), "TST");
        var engine = BuildEngine(new FixedGroundData(layout), "TST", soloTraining: false);
        var aircraft = AddParkedAt(engine, layout, "TST1", "B738", "G", "TST");

        var result = engine.SendCommand("TST1", "TAXI K M3");
        _output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.False(Traverses(route, "K"), "K is unreachable and not a sibling lane: dropped, not cut across to");
        Assert.Contains(route.Warnings, w => w.Contains("unable via K", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("taxiing via M3", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gate G 250 ft west of north–south lane M3 (an SFO-like alley); lettered taxiway K parallel 150 ft east of M3, unconnected.
    /// </summary>
    private static string MiniRampGeoJson()
    {
        const double lat0 = 37.60;
        const double lon0 = -122.38;
        const double degPerFtLat = 1.0 / 364000.0;
        double degPerFtLon = degPerFtLat / Math.Cos(lat0 * Math.PI / 180.0);
        string Lon(double ft) => (lon0 + (ft * degPerFtLon)).ToString("F7", System.Globalization.CultureInfo.InvariantCulture);
        string Lat(double ft) => (lat0 + (ft * degPerFtLat)).ToString("F7", System.Globalization.CultureInfo.InvariantCulture);
        string Line(string name, double lonFt) =>
            $$"""
                { "type": "Feature", "properties": { "type": "taxiway", "name": "{{name}}" },
                  "geometry": { "type": "LineString",
                    "coordinates": [[{{Lon(lonFt)}}, {{Lat(-600)}}], [{{Lon(lonFt)}}, {{Lat(0)}}], [{{Lon(lonFt)}}, {{Lat(600)}}]] } }
                """;
        return $$"""
            { "type": "FeatureCollection", "features": [
              { "type": "Feature", "properties": { "type": "parking", "name": "G", "heading": 270 },
                "geometry": { "type": "Point", "coordinates": [{{Lon(-250)}}, {{Lat(0)}}] } },
              {{Line("M3", 0)}},
              {{Line("K", 150)}}
            ] }
            """;
    }

    private sealed class FixedGroundData(AirportGroundLayout layout) : IAirportGroundData
    {
        public AirportGroundLayout? GetLayout(string airportId) => layout;

        public string? GetSourceGeoJson(string airportId) => null;
    }
}
