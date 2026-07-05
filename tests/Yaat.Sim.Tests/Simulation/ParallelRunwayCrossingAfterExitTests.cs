using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tests for GitHub issue #175: better handling for aircraft crossing a parallel runway after
/// landing.
///
/// (A) After vacating (e.g. SFO 19L at G), a bare <c>CROSS</c> / <c>CROSS 19R</c> should cross the
/// parallel runway without a prior TAXI.
/// (B) When vacating between parallels with no intervening taxiway intersection, the aircraft
/// auto-pulls-up to the parallel runway's hold-short (OAK 28L exit right on G/H → hold short of 28R).
///
/// This file covers the topology helper <see cref="AirportGroundLayout.FindParallelRunwayCrossing"/>
/// directly against the real OAK/SFO layouts plus synthetic edge cases.
/// </summary>
public class ParallelRunwayCrossingAfterExitTests(ITestOutputHelper output)
{
    private static AirportGroundLayout? LoadLayout(string airportId)
    {
        string path = Path.Combine("TestData", $"{airportId.ToLowerInvariant()}.geojson");
        if (!File.Exists(path))
        {
            return null;
        }

        return GeoJsonParser.Parse(airportId.ToUpperInvariant(), File.ReadAllText(path), null);
    }

    /// <summary>
    /// Find the hold-short an aircraft lands-and-exits at: a <paramref name="landingDesignator"/>
    /// hold-short on <paramref name="taxiway"/> that has a direct same-taxiway edge to a
    /// <paramref name="parallelDesignator"/> hold-short (the between-runways exit). Returns it plus
    /// the node on the other side (toward the landing-runway centerline) as the "come from".
    /// </summary>
    private static (GroundNode LandingHs, GroundNode ComeFrom)? FindLandingExitHoldShort(
        AirportGroundLayout layout,
        string landingDesignator,
        string parallelDesignator,
        string taxiway
    )
    {
        foreach (var node in layout.Nodes.Values)
        {
            if (node.Type != GroundNodeType.RunwayHoldShort || node.RunwayId is not { } rid || !rid.Contains(landingDesignator))
            {
                continue;
            }

            GroundNode? parallelNeighbor = null;
            GroundNode? otherNeighbor = null;
            foreach (var edge in node.Edges)
            {
                if (edge.IsRunwayCenterline || !edge.MatchesTaxiway(taxiway))
                {
                    continue;
                }

                var other = edge.OtherNode(node);
                if (other.Type == GroundNodeType.RunwayHoldShort && other.RunwayId is { } orid && orid.Contains(parallelDesignator))
                {
                    parallelNeighbor = other;
                }
                else
                {
                    otherNeighbor = other;
                }
            }

            if (parallelNeighbor is not null && otherNeighbor is not null)
            {
                return (node, otherNeighbor);
            }
        }

        return null;
    }

    [Theory]
    [InlineData("OAK", "28L", "28R", "G")]
    [InlineData("OAK", "28L", "28R", "H")]
    [InlineData("SFO", "19L", "19R", "G")]
    public void FindParallelRunwayCrossing_BetweenParallels_ReturnsCrossing(string airport, string landing, string parallel, string taxiway)
    {
        var layout = LoadLayout(airport);
        if (layout is null)
        {
            return;
        }

        var setup = FindLandingExitHoldShort(layout, landing, parallel, taxiway);
        Assert.NotNull(setup);
        var (landingHs, comeFrom) = setup.Value;

        var crossing = layout.FindParallelRunwayCrossing(landingHs, comeFrom, taxiway, landing);
        Assert.NotNull(crossing);

        var (nearHs, farHs, parallelRunwayId, pullUp, crossingPath) = crossing.Value;
        output.WriteLine(
            $"{airport} {landing}→{parallel} via {taxiway}: landingHS=#{landingHs.Id} near=#{nearHs.Id} far=#{farHs.Id} "
                + $"rwy={parallelRunwayId} pullUp=[{string.Join("→", pullUp.Select(n => n.Id))}] "
                + $"crossing=[{string.Join("→", crossingPath.Select(n => n.Id))}]"
        );

        Assert.True(nearHs.RunwayId!.Value.Contains(parallel), $"Near HS #{nearHs.Id} should protect {parallel}");
        Assert.True(farHs.RunwayId!.Value.Contains(parallel), $"Far HS #{farHs.Id} should protect {parallel}");
        Assert.NotEqual(nearHs.Id, farHs.Id);
        Assert.Contains(parallel, parallelRunwayId);

        // Path contracts: pull-up runs landingHS → nearHS; crossing runs nearHS → farHS.
        Assert.Equal(landingHs.Id, pullUp[0].Id);
        Assert.Equal(nearHs.Id, pullUp[^1].Id);
        Assert.Equal(nearHs.Id, crossingPath[0].Id);
        Assert.Equal(farHs.Id, crossingPath[^1].Id);

        // The crossing path traverses the parallel runway surface.
        Assert.Contains(crossingPath, n => n.Edges.Any(e => e.MatchesRunway(parallel)));
    }

    /// <summary>
    /// An aircraft that lands and exits toward its OWN runway's far-side hold-short (not a parallel)
    /// gets no crossing — the next hold-short belongs to the same runway.
    /// </summary>
    [Fact]
    public void FindParallelRunwayCrossing_SameRunwayFarSide_ReturnsNull()
    {
        var layout = LoadLayout("OAK");
        if (layout is null)
        {
            return;
        }

        // Reuse the 28L→28R setup to get the 28R near hold-short and the 28L hold-short next to it.
        var setup = FindLandingExitHoldShort(layout, "28L", "28R", "G");
        Assert.NotNull(setup);
        var (oak28LHoldShort, _) = setup.Value;

        // The 28R hold-short adjacent to the 28L exit.
        var near28R = oak28LHoldShort
            .Edges.Select(e => e.OtherNode(oak28LHoldShort))
            .First(n => n.Type == GroundNodeType.RunwayHoldShort && n.RunwayId is { } r && r.Contains("28R"));

        // Pretend we landed 28R and exited toward its centerline: the next hold-short along G is
        // 28R's own far side, not a parallel.
        var crossing = layout.FindParallelRunwayCrossing(near28R, oak28LHoldShort, "G", "28R");
        Assert.Null(crossing);
    }

    [Fact]
    public void FindParallelRunwayCrossing_InterveningIntersection_ReturnsNull()
    {
        var layout = BuildSyntheticParallelLayout(withInterveningBranch: true);
        var landingHs = layout.Nodes[1];
        var comeFrom = layout.Nodes[0];

        var crossing = layout.FindParallelRunwayCrossing(landingHs, comeFrom, "G", "28L");
        Assert.Null(crossing);
    }

    [Fact]
    public void FindParallelRunwayCrossing_NoInterveningIntersection_ReturnsCrossing()
    {
        var layout = BuildSyntheticParallelLayout(withInterveningBranch: false);
        var landingHs = layout.Nodes[1];
        var comeFrom = layout.Nodes[0];

        var crossing = layout.FindParallelRunwayCrossing(landingHs, comeFrom, "G", "28L");
        Assert.NotNull(crossing);

        var (nearHs, farHs, _, _, _) = crossing.Value;
        Assert.Equal(3, nearHs.Id);
        Assert.Equal(5, farHs.Id);
    }

    /// <summary>
    /// Minimal two-runway parallel layout along taxiway G:
    /// 0(comeFrom) -G- 1(HS 28L) -G- 2(mid) -G- 3(HS 28R near) -G- 4 -G- 5(HS 28R far).
    /// When <paramref name="withInterveningBranch"/> is true, node 2 also has a foreign taxiway "X"
    /// edge, simulating an intersecting taxiway between the landing hold-short and the parallel.
    /// </summary>
    private static AirportGroundLayout BuildSyntheticParallelLayout(bool withInterveningBranch)
    {
        var rwy28L = RunwayIdentifier.Parse("28L/10R");
        var rwy28R = RunwayIdentifier.Parse("28R/10L");
        var layout = new AirportGroundLayout { AirportId = "KTST" };

        GroundNode Twy(int id, double lat) =>
            new()
            {
                Id = id,
                Position = new LatLon(lat, -122.0),
                Type = GroundNodeType.TaxiwayIntersection,
            };
        GroundNode Hs(int id, double lat, RunwayIdentifier rwy) =>
            new()
            {
                Id = id,
                Position = new LatLon(lat, -122.0),
                Type = GroundNodeType.RunwayHoldShort,
                RunwayId = rwy,
            };

        var n0 = Twy(0, 37.700);
        var n1 = Hs(1, 37.701, rwy28L);
        var n2 = Twy(2, 37.702);
        var n3 = Hs(3, 37.703, rwy28R);
        var n4 = Twy(4, 37.704);
        var n5 = Hs(5, 37.705, rwy28R);
        var nodes = new[] { n0, n1, n2, n3, n4, n5 };

        GroundEdge G(GroundNode a, GroundNode b, string name = "G") =>
            new()
            {
                Nodes = [a, b],
                TaxiwayName = name,
                DistanceNm = 0.05,
            };

        var edges = new List<GroundEdge> { G(n0, n1), G(n1, n2), G(n2, n3), G(n3, n4), G(n4, n5) };

        if (withInterveningBranch)
        {
            var nSide = Twy(6, 37.7025);
            nodes = [.. nodes, nSide];
            edges.Add(G(n2, nSide, "X"));
        }

        foreach (var node in nodes)
        {
            layout.Nodes[node.Id] = node;
        }

        foreach (var edge in edges)
        {
            edge.Nodes[0].Edges.Add(edge);
            edge.Nodes[1].Edges.Add(edge);
            layout.Edges.Add(edge);
        }

        layout.RebuildAdjacencyLists();
        return layout;
    }

    // --- E2E: land, vacate between parallels, pull up, hold short, cross ---

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    /// <summary>
    /// Spawns a B738 on 1nm final, places it through landing + the named exit, sends CLAND and the
    /// exit command, and returns the live engine + aircraft. Returns null (silent skip) when navdata
    /// or layout is unavailable.
    /// </summary>
    private (SimulationEngine Engine, AircraftState Aircraft)? SetupLanding(
        string airport,
        string runwayId,
        string exitCommand,
        bool autoPullUp,
        string aircraftType = "B738"
    )
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return null;
        }

        var runway = NavigationDatabase.Instance.GetRunway(airport, runwayId);
        if (runway is null)
        {
            return null;
        }

        var layout = new TestAirportGroundData().GetLayout(airport);
        if (layout is null)
        {
            return null;
        }

        double reciprocal = (runway.TrueHeading.Degrees + 180) % 360;
        var (acLat, acLon) = GeoMath.ProjectPointRaw(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, 1.0);
        double approachIas = aircraftType is "B738" or "A320" ? 145 : 75;

        var aircraft = new AircraftState
        {
            Callsign = "TST738",
            AircraftType = aircraftType,
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + 318,
            IndicatedAirspeed = approachIas,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = airport,
                Destination = airport,
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
        };

        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Ground.Layout = layout;

        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));

        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-175",
            ScenarioName = "Parallel crossing after exit",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = airport,
            AutoPullUpToParallel = autoPullUp,
        };

        Assert.True(engine.SendCommand("TST738", "CLAND").Success);
        Assert.True(engine.SendCommand("TST738", exitCommand).Success);
        return (engine, aircraft);
    }

    [Theory]
    [InlineData("SFO", "19L", "19R", "EXIT G", "B738")]
    [InlineData("OAK", "28L", "28R", "EXIT G", "C172")]
    public void AutoPullUp_LandsAndHoldsShortOfParallel_ThenCrossesOnCross(
        string airport,
        string landing,
        string parallel,
        string exitCommand,
        string aircraftType
    )
    {
        var setup = SetupLanding(airport, landing, exitCommand, autoPullUp: true, aircraftType);
        if (setup is null)
        {
            return;
        }

        var (engine, ac) = setup.Value;

        // (B) auto-pull-up: drive until the aircraft is holding short of the parallel runway.
        HoldingShortPhase? holding = null;
        for (int t = 1; t <= 500; t++)
        {
            engine.TickOneSecond();
            if (ac.Phases?.CurrentPhase is HoldingShortPhase h)
            {
                holding = h;
                break;
            }
        }

        Assert.NotNull(holding);
        output.WriteLine($"{airport} {landing}: holding short of {holding.HoldShort.TargetName} at gs={ac.GroundSpeed:F2}");
        Assert.Contains(parallel, holding.HoldShort.TargetName ?? "");
        Assert.Equal(HoldShortReason.RunwayCrossing, holding.HoldShort.Reason);
        Assert.True(ac.GroundSpeed < 1.0, $"Aircraft should be stopped short of {parallel}, gs={ac.GroundSpeed:F2}");

        var route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.Contains(route.HoldShortPoints, hs => (hs.TargetName ?? "").Contains(parallel) && hs.Reason == HoldShortReason.RunwayCrossing);

        // (A) bare CROSS crosses the parallel without a prior TAXI.
        var crossResult = engine.SendCommand("TST738", "CROSS");
        Assert.True(crossResult.Success, $"CROSS should succeed, got: {crossResult.Message}");

        bool sawCrossing = false;
        bool finishedClear = false;
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            var phase = ac.Phases?.CurrentPhase;
            if (phase is CrossingRunwayPhase)
            {
                sawCrossing = true;
            }

            if (sawCrossing && (phase is HoldingInPositionPhase or HoldingAfterExitPhase) && ac.GroundSpeed < 1.0)
            {
                finishedClear = true;
                output.WriteLine($"  crossed {parallel}, now {phase.Name} at ({ac.Position.Lat:F6},{ac.Position.Lon:F6})");
                break;
            }
        }

        Assert.True(sawCrossing, "Aircraft should enter CrossingRunwayPhase after CROSS");
        Assert.True(finishedClear, "Aircraft should finish holding clear on the far side of the parallel");
    }

    [Fact]
    public void AutoPullUpDisabled_HoldsAtLandingExit_AndCrossIsRejected()
    {
        var setup = SetupLanding("SFO", "19L", "EXIT G", autoPullUp: false);
        if (setup is null)
        {
            return;
        }

        var (engine, ac) = setup.Value;

        HoldingAfterExitPhase? holding = null;
        for (int t = 1; t <= 500; t++)
        {
            engine.TickOneSecond();
            if (ac.Phases?.CurrentPhase is HoldingAfterExitPhase h)
            {
                holding = h;
                break;
            }

            // It must never auto-advance into a HoldingShortPhase when the setting is off.
            Assert.IsNotType<HoldingShortPhase>(ac.Phases?.CurrentPhase);
        }

        Assert.NotNull(holding);
        Assert.Null(ac.Ground.AssignedTaxiRoute);

        // Bare CROSS without a prior TAXI is rejected (current behavior preserved).
        var crossResult = engine.SendCommand("TST738", "CROSS");
        Assert.False(crossResult.Success, "CROSS should be rejected when holding after exit with no taxi route");
    }
}
