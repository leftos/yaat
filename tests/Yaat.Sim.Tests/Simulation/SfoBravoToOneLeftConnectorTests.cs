using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// SFO <c>TAXI A F1 B 1L</c> / <c>TAXI C Z B 1L</c>: taxiway B runs past runway 01L/19R's south end but
/// carries no 1L hold-short bar — the bars sit on the numbered stubs M1 and A2. Real controllers clear
/// "…Bravo, runway 1L" and the pilot takes the ~75 ft M1 stub off the B/M1 fillet corner, so the resolver
/// must thread M1 rather than reject the clearance with "Taxiway B does not reach runway 1L".
///
/// The connector is only free because it is numbered: an unnamed <em>letter</em> taxiway is a deviation
/// the controller must name, and the stub must be short enough to count as "at the runway"
/// (<see cref="TaxiPathfinder.AdjacentRunwayHoldShortMaxFt"/>, issue #393). A runway B genuinely does not
/// serve (1R, 28L) still fails with the same message it does today.
/// </summary>
public class SfoBravoToOneLeftConnectorTests(ITestOutputHelper output)
{
    private const string Callsign = "TEST1";

    /// <summary>
    /// Spot 2 sits on the Terminal 1 ramp lane; the taxi out to 01L/19R's south end is ~2 nm, which a jet
    /// covers in roughly nine minutes of taxi speed and corner slow-downs. The budget bounds the drive so a
    /// route the navigator cannot actually fly fails instead of hanging.
    /// </summary>
    private const int HoldShortBudgetSeconds = 1200;

    private static AirportGroundLayout? LoadSfo()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb is null ? null : new TestAirportGroundData().GetLayout("SFO");
    }

    private TaxiRoute? Resolve(AirportGroundLayout layout, GroundNode start, string[] path, string runway, out string? failReason)
    {
        var diag = new List<string>();
        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            start.Id,
            [.. path],
            out failReason,
            new ExplicitPathOptions
            {
                OccupiedTaxiway = null,
                DestinationRunway = runway,
                DiagnosticLog = diag.Add,
            },
            AircraftCategory.Jet
        );

        foreach (string line in diag)
        {
            if (line.StartsWith("[connector]", StringComparison.Ordinal) || line.StartsWith("[variant]", StringComparison.Ordinal))
            {
                output.WriteLine(line);
            }
        }

        string outcome = route is null ? "<null>" : $"{route.Segments.Count} segments";
        output.WriteLine($"TAXI {string.Join(' ', path)} {runway}: route={outcome} fail={failReason ?? "-"}");
        if (route is not null)
        {
            output.WriteLine("  taxiways: " + string.Join(' ', route.Segments.Select(s => s.TaxiwayName)));
            output.WriteLine("  warnings: " + string.Join(" | ", route.Warnings));
        }

        return route;
    }

    /// <summary>Index of the first segment travelling exactly <paramref name="taxiway"/> (a joined fillet-arc name never matches).</summary>
    private static int FirstSegmentOn(TaxiRoute route, string taxiway)
    {
        for (int i = 0; i < route.Segments.Count; i++)
        {
            if (route.Segments[i].TaxiwayName.Equals(taxiway, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Index of the first segment that <em>reaches</em> <paramref name="taxiway"/> — travels it, or passes
    /// through a node incident to it. This is the pathfinder's own "reached" test: two cleared taxiways that
    /// meet at one junction are both honored by the turn through that node, without a labeled edge of either.
    /// </summary>
    private static int FirstSegmentReaching(TaxiRoute route, string taxiway)
    {
        for (int i = 0; i < route.Segments.Count; i++)
        {
            DirectionalEdge edge = route.Segments[i].Edge;
            bool reached =
                edge.Edge.MatchesTaxiway(taxiway)
                || edge.FromNode.Edges.Any(e => e.MatchesTaxiway(taxiway))
                || edge.ToNode.Edges.Any(e => e.MatchesTaxiway(taxiway));
            if (reached)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Each taxiway is travelled, in the order issued.</summary>
    private static void AssertOrderedTaxiways(TaxiRoute route, string[] ordered)
    {
        int previous = -1;
        foreach (string taxiway in ordered)
        {
            int index = FirstSegmentOn(route, taxiway);
            Assert.True(index >= 0, $"route never travels {taxiway}: {string.Join(' ', route.Segments.Select(s => s.TaxiwayName))}");
            Assert.True(index > previous, $"route travels {taxiway} at segment {index}, out of order (previous token started at {previous})");
            previous = index;
        }
    }

    /// <summary>The route travels B, then travels the M1 stub — the connector is threaded after the cleared taxiway, never instead of it.</summary>
    private static void AssertTravelsBravoThenM1(TaxiRoute route)
    {
        int bravo = FirstSegmentOn(route, "B");
        int mike1 = FirstSegmentOn(route, "M1");
        Assert.True(bravo >= 0, $"route never travels B: {string.Join(' ', route.Segments.Select(s => s.TaxiwayName))}");
        Assert.True(mike1 >= 0, $"route never travels M1: {string.Join(' ', route.Segments.Select(s => s.TaxiwayName))}");
        Assert.True(bravo < mike1, $"route travels M1 at segment {mike1}, before B at segment {bravo}");
    }

    /// <summary>The shared shape assertions: one 1L destination stop on the M1 bar, a contiguous, flyable route ending there.</summary>
    private static void AssertThreadsM1ToOneLeft(TaxiRoute route, GroundNode m1Bar)
    {
        HoldShortPoint destination = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.DestinationRunway);
        Assert.NotNull(destination.TargetName);
        Assert.True(
            RunwayIdentifier.Parse(destination.TargetName).Contains("1L"),
            $"destination hold-short targets '{destination.TargetName}', expected the 1L bar"
        );
        Assert.Equal(m1Bar.Id, destination.NodeId);
        Assert.Equal(m1Bar.Id, route.Segments[^1].ToNodeId);

        for (int i = 0; i + 1 < route.Segments.Count; i++)
        {
            Assert.True(
                route.Segments[i].ToNodeId == route.Segments[i + 1].FromNodeId,
                $"segments {i}/{i + 1} are not contiguous ({route.Segments[i].ToNodeId} != {route.Segments[i + 1].FromNodeId})"
            );
        }

        double limit = CategoryLimits.MaxHeadingChangeDeg(AircraftCategory.Jet);
        for (int i = 1; i < route.Segments.Count; i++)
        {
            DirectionalEdge previous = route.Segments[i - 1].Edge;
            DirectionalEdge current = route.Segments[i].Edge;
            if (GeometricAdmissibility.IsNoOpEdge(previous.Edge) || GeometricAdmissibility.IsNoOpEdge(current.Edge))
            {
                continue;
            }

            double delta = RouteCostFunction.HeadingDelta(previous.ArrivalBearing, current.DepartureBearing);
            string where = $"segment {i} ({current.TaxiwayName} {current.FromNodeId}->{current.ToNodeId})";
            Assert.True(delta <= limit, $"{where} turns {delta:F0}° > {limit:F0}°");
        }

        Assert.Contains(route.Warnings, w => w.Contains("via M1", StringComparison.Ordinal));
        Assert.DoesNotContain(route.Warnings, w => w.Contains("not in the route issued", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AF1B_To1L_FromSpot2_ThreadsM1()
    {
        AirportGroundLayout? layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        GroundNode? start = layout.FindSpotNodeByName("2");
        GroundNode? m1Bar = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "1L", "M1");
        if (start is null || m1Bar is null)
        {
            return;
        }

        TaxiRoute? route = Resolve(layout, start, ["A", "F1", "B"], "1L", out string? failReason);

        Assert.True(route is not null, $"TAXI A F1 B 1L from spot 2 must resolve via the M1 stub: {failReason}");
        AssertOrderedTaxiways(route, ["A", "F1", "B", "M1"]);
        AssertTravelsBravoThenM1(route);
        AssertThreadsM1ToOneLeft(route, m1Bar);
    }

    [Fact]
    public void CZB_To1L_FromGate50_1_ThreadsM1()
    {
        AirportGroundLayout? layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        GroundNode? start = layout.FindParkingByName("50-1");
        GroundNode? m1Bar = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "1L", "M1");
        if (start is null || m1Bar is null)
        {
            return;
        }

        TaxiRoute? route = Resolve(layout, start, ["C", "Z", "B"], "1L", out string? failReason);

        Assert.True(route is not null, $"TAXI C Z B 1L from cargo gate 50-1 must resolve via the M1 stub: {failReason}");
        AssertOrderedTaxiways(route, ["Z", "B", "M1"]);

        // C is honored by being reached, not travelled: the cargo ramp lead-out joins Z, which meets C at a
        // junction the route passes through. That is the resolver's documented "touched, not traversed" case.
        Assert.True(FirstSegmentReaching(route, "C") >= 0, "route never reaches C");
        AssertTravelsBravoThenM1(route);
        AssertThreadsM1ToOneLeft(route, m1Bar);
    }

    [Fact]
    public void TryTaxi_AF1B_1L_NoticeNamesM1()
    {
        AirportGroundLayout? layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        GroundNode? start = layout.FindSpotNodeByName("2");
        GroundNode? m1Bar = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "1L", "M1");
        if (start is null || m1Bar is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.LoadScenario(
            BuildScenarioJson(start.Position.Lat, start.Position.Lon, HeadingTowardOneLeft(layout, start)),
            rngSeed: 42,
            sessionStartUtc: MagneticDeclination.EvaluationDateUtc
        );

        CommandResult result = engine.SendCommand(Callsign, "TAXI A F1 B 1L");
        output.WriteLine($"echo: {result.Message}");
        Assert.True(result.Success, $"TAXI A F1 B 1L from spot 2 must be accepted: {result.Message}");
        Assert.Equal("Taxi via A F1 B RWY 1L [via M1 — B reaches 1L through M1]", result.Message);

        AircraftState? aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        AssertTravelsBravoThenM1(route);
        AssertThreadsM1ToOneLeft(route, m1Bar);
        output.WriteLine($"route: {route.Segments.Count} segments, {route.TotalDistanceNm:F2} nm");

        HoldingShortPhase? holding = null;
        for (int t = 1; t <= HoldShortBudgetSeconds; t++)
        {
            engine.TickOneSecond();
            AircraftState? live = engine.FindAircraft(Callsign);
            if (live?.Phases?.CurrentPhase is HoldingShortPhase phase)
            {
                holding = phase;
                output.WriteLine($"t={t}s holding short of {phase.HoldShort.TargetName}");
                break;
            }

            if (t % 120 == 0)
            {
                output.WriteLine(
                    $"t={t}s phase={live?.Phases?.CurrentPhase?.Name} ias={live?.IndicatedAirspeed:F1} "
                        + $"segIdx={live?.Ground.AssignedTaxiRoute?.CurrentSegmentIndex}/{route.Segments.Count}"
                );
            }
        }

        Assert.True(holding is not null, $"TEST1 never reached a hold-short within {HoldShortBudgetSeconds}s");
        Assert.NotNull(holding.HoldShort.TargetName);
        Assert.Contains("1L", holding.HoldShort.TargetName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AF1B_To1R_StillFails_SameMessage() => AssertUnreachableRunway("1R");

    [Fact]
    public void AF1B_To28L_StillFails_SameMessage() => AssertUnreachableRunway("28L");

    private void AssertUnreachableRunway(string runway)
    {
        AirportGroundLayout? layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        GroundNode? start = layout.FindSpotNodeByName("2");
        if (start is null)
        {
            return;
        }

        TaxiRoute? route = Resolve(layout, start, ["A", "F1", "B"], runway, out string? failReason);

        Assert.Null(route);
        Assert.Equal($"Taxiway B does not reach runway {runway} — specify a connecting taxiway.", failReason);
    }

    [Fact]
    public void FindRunwayConnectorsOffTaxiway_B_1L_YieldsM1()
    {
        AirportGroundLayout? layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        List<SegmentExpander.RunwayConnectorCandidate> toOneLeft = SegmentExpander.FindRunwayConnectorsOffTaxiway(
            layout,
            "B",
            "1L",
            ["A", "F1", "B"]
        );
        foreach (SegmentExpander.RunwayConnectorCandidate candidate in toOneLeft)
        {
            output.WriteLine(
                $"1L connector off B: {candidate.Connector} via node {candidate.JunctionNodeId} to bar {candidate.BarNodeId} ({candidate.StubFt:F0} ft)"
            );
        }

        SegmentExpander.RunwayConnectorCandidate only = Assert.Single(toOneLeft);
        Assert.Equal("M1", only.Connector);
        Assert.True(only.StubFt < 200.0, $"the M1 stub to the 1L bar measured {only.StubFt:F0} ft, expected the short lead-in off the B/M1 corner");

        // 1R's bars hang off F1, but the F1 run to them passes the AF and A junctions and is far longer
        // than a stub — B does not serve 1R, with or without F1 already named in the clearance.
        Assert.Empty(SegmentExpander.FindRunwayConnectorsOffTaxiway(layout, "B", "1R", ["A", "F1", "B"]));
        Assert.Empty(SegmentExpander.FindRunwayConnectorsOffTaxiway(layout, "B", "1R", ["B"]));
    }

    /// <summary>A heading along the first leg of the clearance, so the spawned aircraft is not asked to start with a pirouette.</summary>
    private static int HeadingTowardOneLeft(AirportGroundLayout layout, GroundNode start)
    {
        GroundNode? junction = layout.FindIntersectionNode("A", "F1");
        return junction is null ? 180 : (int)Math.Round(GeoMath.BearingTo(start.Position, junction.Position));
    }

    private static string BuildScenarioJson(double lat, double lon, int headingMag)
    {
        return $$"""
            {
              "id": "test-sfo-b-1l-connector",
              "name": "SFO B to 1L connector",
              "artccId": "ZOA",
              "primaryAirportId": "SFO",
              "initializationTriggers": [],
              "aircraftGenerators": [],
              "aircraft": [
                {
                  "id": "test-ac-1",
                  "aircraftId": "TEST1",
                  "aircraftType": "B738",
                  "transponderMode": "Standby",
                  "startingConditions": {
                    "type": "Coordinates",
                    "coordinates": {"lat": {{lat}}, "lon": {{lon}}},
                    "heading": {{headingMag}}
                  },
                  "onAltitudeProfile": false,
                  "flightplan": {
                    "rules": "IFR",
                    "departure": "KSFO",
                    "destination": "KLAX",
                    "cruiseAltitude": 35000,
                    "cruiseSpeed": 450,
                    "route": "",
                    "remarks": "",
                    "aircraftType": "B738/L"
                  },
                  "presetCommands": [],
                  "spawnDelay": 0,
                  "airportId": "SFO",
                  "difficulty": "Easy"
                }
              ],
              "atc": [],
              "studentPositionId": "",
              "autoDeleteMode": "Parked",
              "flightStripConfigurations": []
            }
            """;
    }
}
