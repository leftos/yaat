using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Where an <c>ATXI</c> ends, by destination class. An air taxi is a ground movement on the airport
/// (AIM 4-3-17.b), so an air taxi to a <em>runway</em> ends at that runway's holding position — never parked on
/// the pavement (AIM 4-3-18.a.5/6; 7110.65 3-11-1.c "HOLD FOR (takeoff clearance)"). A bare runway destination is
/// the full-length holding position; <c>ATXI 28L@J</c> names the bar on that taxiway, mirroring the located
/// hold-short form. A taxiway spot is not a parking position either — the heli sets down and holds where it is —
/// while a helipad or gate still parks.
/// </summary>
public class AirTaxiRunwayTerminusTests(ITestOutputHelper output)
{
    private const double KoakFieldElevFt = 6.0;
    private const int MaxTicks = 900;
    private const string Runway = "28L";

    /// <summary>
    /// How far the settled heli may sit from its hold-short point. <c>AirTaxiPhase</c> ends the cruise within
    /// <c>ArrivalThresholdNm</c> (0.01 nm = 61 ft) of the target and the landing descends from there, so this is
    /// the phase's own arrival tolerance plus a little slack — not a claim about the setback, which is asserted
    /// exactly on the hold-short point itself.
    /// </summary>
    private const double SettledToleranceFt = 80.0;

    /// <summary>The taxiway a hold-short bar sits on — straight edges only, so a junction arc's joined name never wins.</summary>
    private static string? TaxiwayOf(GroundNode node) =>
        node
            .Edges.Select(e => e.TaxiwayName)
            .FirstOrDefault(name => !name.Contains(" - ", StringComparison.Ordinal) && !name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase));

    /// <summary>The runway's full-length entrance: the bar nearest the named end's threshold.</summary>
    private static GroundNode FullLengthBar(AirportGroundLayout layout, string runway)
    {
        var pavement = layout.FindRunway(runway);
        Assert.NotNull(pavement);
        bool isFirstEnd = pavement.Id.End1.Equals(RunwayIdentifier.NormalizeDesignator(runway), StringComparison.OrdinalIgnoreCase);
        var coord = isFirstEnd ? pavement.Coordinates[0] : pavement.Coordinates[^1];
        var threshold = new LatLon(coord.Lat, coord.Lon);
        var bar = layout.GetRunwayHoldShortNodes(runway).OrderBy(n => n.Id).MinBy(n => GeoMath.DistanceNm(threshold, n.Position));
        Assert.NotNull(bar);
        return bar;
    }

    private static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>
    /// Perpendicular distance to the runway centerline, measured to the segments between its vertices. A
    /// nearest-vertex measure is dominated by along-track distance on a runway drawn with few, far-apart points,
    /// which would make "farther from the runway" meaningless here.
    /// </summary>
    private static double DistanceToPavementFt(AirportGroundLayout layout, string runway, LatLon position)
    {
        var pavement = layout.FindRunway(runway);
        Assert.NotNull(pavement);
        var points = pavement.Coordinates.Select(c => new LatLon(c.Lat, c.Lon)).ToList();
        Assert.True(points.Count >= 2, $"runway {runway} has no centerline to measure against");

        double best = double.MaxValue;
        for (int i = 0; i < points.Count - 1; i++)
        {
            best = Math.Min(best, GeoMath.DistanceToSegmentFt(position, points[i], points[i + 1]));
        }

        return best;
    }

    [Fact]
    public void Atxi_ToRunway_EndsHoldingShortAtTheFullLengthBar_NeverOnThePavement()
    {
        if (Setup() is not { } fixture)
        {
            return;
        }

        var (engine, layout, heli) = fixture;
        var runway = CommandDispatcher.ResolveRunway(heli, Runway);
        Assert.NotNull(runway);
        var expectedBar = FullLengthBar(layout, Runway);

        var result = engine.SendCommand("TEST1", $"ATXI {Runway}");
        output.WriteLine($"ATXI {Runway}: success={result.Success} message=\"{result.Message}\"");
        Assert.True(result.Success, result.Message);
        Assert.Equal("Air taxi to runway 28L, holding short", result.Message);

        var holding = TickToHoldShort(engine, heli, runway);

        Assert.Equal(HoldShortReason.DestinationRunway, holding.HoldShort.Reason);
        Assert.Equal(Runway, holding.HoldShort.TargetName);
        Assert.Equal(expectedBar.Id, holding.HoldShort.NodeId);
        Assert.True(heli.IsOnGround, "the heli must be on the ground at the holding position");
        Assert.Equal(0, heli.IndicatedAirspeed, 1.0);
        Assert.Null(heli.Ground.ParkingSpot);
        Assert.Equal(Runway, heli.Phases!.AssignedRunway?.Designator);

        // Nose AT the marking: the stored hold-short point is half a fuselage back from the bar, on the far side
        // of it from the runway — the setback every taxi hold-short gets from ComputeHoldShortPositions.
        Assert.NotNull(holding.HoldShort.Latitude);
        Assert.NotNull(holding.HoldShort.Longitude);
        var stop = new LatLon(holding.HoldShort.Latitude!.Value, holding.HoldShort.Longitude!.Value);
        double halfLengthFt =
            (FaaAircraftDatabase.Get(heli.AircraftType)?.LengthFt ?? HoldShortAnnotator.CwtFallbackLengthFt(heli.AircraftType)) / 2.0;
        double setbackFt = FeetBetween(stop, expectedBar.Position);
        output.WriteLine(
            $"setback={setbackFt:F1} ft (half length {halfLengthFt:F1} ft); "
                + $"stop is {DistanceToPavementFt(layout, Runway, stop):F0} ft from the pavement, "
                + $"bar {DistanceToPavementFt(layout, Runway, expectedBar.Position):F0} ft; "
                + $"settled {FeetBetween(heli.Position, stop):F1} ft from the stop"
        );
        Assert.Equal(halfLengthFt, setbackFt, 1.0);
        Assert.True(
            DistanceToPavementFt(layout, Runway, stop) > DistanceToPavementFt(layout, Runway, expectedBar.Position),
            "the setback must move the stop away from the runway, not toward it"
        );
        Assert.True(
            FeetBetween(heli.Position, stop) < SettledToleranceFt,
            $"heli settled {FeetBetween(heli.Position, stop):F0} ft from its hold-short point"
        );
    }

    [Fact]
    public void AtxiLocatedAtATaxiway_EndsAtThatTaxiwaysBar()
    {
        if (Setup() is not { } fixture)
        {
            return;
        }

        var (engine, layout, heli) = fixture;
        var runway = CommandDispatcher.ResolveRunway(heli, Runway);
        Assert.NotNull(runway);

        // Pick a bar the bare form would NOT choose, named by its own taxiway — node ids are ephemeral, so the
        // target is resolved from the graph rather than hardcoded.
        var fullLength = FullLengthBar(layout, Runway);
        string? fullLengthTaxiway = TaxiwayOf(fullLength);
        var located = layout
            .GetRunwayHoldShortNodes(Runway)
            .OrderBy(n => n.Id)
            .FirstOrDefault(n =>
                (n.Id != fullLength.Id)
                && (TaxiwayOf(n) is { } name)
                && !name.Equals(fullLengthTaxiway, StringComparison.OrdinalIgnoreCase)
                && !n.Edges.Any(e => e.MatchesTaxiway(fullLengthTaxiway ?? ""))
            );
        if (located is null)
        {
            output.WriteLine($"runway {Runway} has no second bar on its own taxiway — nothing to locate");
            return;
        }

        string taxiway = TaxiwayOf(located)!;
        output.WriteLine($"full-length bar {fullLength.Id} on {fullLengthTaxiway}; located bar {located.Id} on {taxiway}");

        var result = engine.SendCommand("TEST1", $"ATXI {Runway}@{taxiway}");
        output.WriteLine($"ATXI {Runway}@{taxiway}: success={result.Success} message=\"{result.Message}\"");
        Assert.True(result.Success, result.Message);
        Assert.Equal($"Air taxi to runway 28L at {taxiway}, holding short", result.Message);

        var holding = TickToHoldShort(engine, heli, runway);

        Assert.Equal(HoldShortReason.DestinationRunway, holding.HoldShort.Reason);
        Assert.NotEqual(fullLength.Id, holding.HoldShort.NodeId);
        Assert.True(layout.Nodes.TryGetValue(holding.HoldShort.NodeId, out var reached));
        Assert.True(reached!.Edges.Any(e => e.MatchesTaxiway(taxiway)), $"the bar reached is not on {taxiway}");
        Assert.Null(heli.Ground.ParkingSpot);
    }

    [Fact]
    public void Atxi_ToATaxiwaySpot_HoldsInPositionWithNoParkingSpot()
    {
        if (Setup() is not { } fixture)
        {
            return;
        }

        var (engine, layout, heli) = fixture;

        // A Spot node whose name no parking or helipad also carries — the ATXI parser strips the $ sigil, so a
        // name shared with a gate would resolve as parking.
        var spot = layout
            .Nodes.Values.OrderBy(n => n.Id)
            .FirstOrDefault(n =>
                (n.Type == GroundNodeType.Spot)
                && (n.Name is { Length: > 0 } name)
                && (layout.FindHelipadByName(name) is null)
                && (layout.FindParkingByName(name) is null)
            );
        Assert.NotNull(spot);

        var result = engine.SendCommand("TEST1", $"ATXI ${spot.Name}");
        output.WriteLine($"ATXI ${spot.Name}: success={result.Success} message=\"{result.Message}\"");
        Assert.True(result.Success, result.Message);

        var phase = TickTo<HoldingInPositionPhase>(engine, heli);
        Assert.NotNull(phase);
        Assert.True(heli.IsOnGround);
        Assert.Null(heli.Ground.ParkingSpot);
        Assert.True(FeetBetween(heli.Position, spot.Position) < SettledToleranceFt, "heli did not settle on the spot");
    }

    [Fact]
    public void Atxi_ToAParkingPosition_StillParks()
    {
        if (Setup() is not { } fixture)
        {
            return;
        }

        var (engine, _, heli) = fixture;

        var result = engine.SendCommand("TEST1", "ATXI FDX1");
        Assert.True(result.Success, result.Message);
        Assert.Equal("Air taxi to FDX1", result.Message);

        var phase = TickTo<AtParkingPhase>(engine, heli);
        Assert.NotNull(phase);
        Assert.Equal("FDX1", heli.Ground.ParkingSpot);
    }

    [Fact]
    public void HoldingShortAfterAnAirTaxi_TakesCtoAndRefusesRes()
    {
        if (Setup() is not { } fixture)
        {
            return;
        }

        var (engine, _, heli) = fixture;
        var runway = CommandDispatcher.ResolveRunway(heli, Runway);
        Assert.NotNull(runway);

        Assert.True(engine.SendCommand("TEST1", $"ATXI {Runway}").Success);
        TickToHoldShort(engine, heli, runway);

        var res = engine.SendCommand("TEST1", "RES");
        output.WriteLine($"RES: success={res.Success} message=\"{res.Message}\"");
        Assert.False(res.Success, "RES must not release a hold short of the destination runway");

        var cto = engine.SendCommand("TEST1", "CTO");
        output.WriteLine($"CTO: success={cto.Success} message=\"{cto.Message}\"");
        Assert.True(cto.Success, cto.Message);

        // The clearance has to be flyable from a bar reached without a taxi route: the line-up is planned, and
        // the heli actually lifts off aligned with the runway it was cleared from.
        var lineup = heli.Phases!.Phases.OfType<LineUpPhase>().FirstOrDefault();
        Assert.True(lineup is not null, "CTO planned no line-up");
        Assert.True(lineup!.Status is PhaseStatus.Pending or PhaseStatus.Active, $"line-up is {lineup.Status}");

        for (int t = 1; (t <= MaxTicks) && heli.IsOnGround; t++)
        {
            engine.TickOneSecond();
        }

        Assert.False(heli.IsOnGround, $"heli never lifted off (phase={heli.Phases?.CurrentPhase?.Name})");
        double offset = Math.Abs(((heli.TrueHeading.Degrees - runway.TrueHeading.Degrees + 540) % 360) - 180);
        output.WriteLine($"airborne at {heli.Altitude:F0} ft heading {heli.TrueHeading.Degrees:F0} vs runway {runway.TrueHeading.Degrees:F0}");
        Assert.True(offset <= 30, $"heli lifted off {offset:F0}° off the {runway.Designator} heading");
    }

    /// <summary>
    /// CROSS at a bar an air taxi put the heli at. The departure-hold refusal ("use LUAW or CTO") applies while
    /// the aircraft is still taxiing to the bar; standing at it — with no taxi route at all, here — CROSS
    /// undesignates the runway and takes the aircraft across (7110.65 3-7-2).
    /// </summary>
    [Fact]
    public void HoldingShortAfterAnAirTaxi_TakesCross()
    {
        if (Setup() is not { } fixture)
        {
            return;
        }

        var (engine, _, heli) = fixture;
        var runway = CommandDispatcher.ResolveRunway(heli, Runway);
        Assert.NotNull(runway);

        Assert.True(engine.SendCommand("TEST1", $"ATXI {Runway}").Success);
        TickToHoldShort(engine, heli, runway);

        var cross = engine.SendCommand("TEST1", $"CROSS {Runway}");
        output.WriteLine($"CROSS {Runway}: success={cross.Success} message=\"{cross.Message}\"");
        Assert.True(cross.Success, cross.Message);

        var crossing = TickTo<CrossingRunwayPhase>(engine, heli);
        Assert.NotNull(crossing);
    }

    /// <summary>
    /// A <c>CROSS</c> chained ahead of another clearance still gates that clearance on the crossing when the bar
    /// was reached by an air taxi. The dispatcher decides that with the same "has the aircraft arrived at the
    /// bar" test the CROSS handler uses, so a route-less terminus must not read as "still taxiing" — the taxi
    /// would otherwise be applied immediately and cancel the crossing.
    /// </summary>
    [Fact]
    public void CrossChainedAfterAnAirTaxi_GatesTheNextBlockOnTheCrossing()
    {
        if (Setup() is not { } fixture)
        {
            return;
        }

        var (engine, _, heli) = fixture;
        var runway = CommandDispatcher.ResolveRunway(heli, Runway);
        Assert.NotNull(runway);

        Assert.True(engine.SendCommand("TEST1", $"ATXI {Runway}").Success);
        TickToHoldShort(engine, heli, runway);

        var chained = engine.SendCommand("TEST1", $"CROSS {Runway}; TAXI @FDX1");
        output.WriteLine($"CROSS {Runway}; TAXI @FDX1: success={chained.Success} message=\"{chained.Message}\"");
        Assert.True(chained.Success, chained.Message);

        output.WriteLine(
            $"route after CROSS: {(heli.Ground.AssignedTaxiRoute is null ? "null" : $"{heli.Ground.AssignedTaxiRoute.Segments.Count} segs, complete={heli.Ground.AssignedTaxiRoute.IsComplete}")}; "
                + $"blocks=[{string.Join(", ", heli.Queue.Blocks.Select(b => b.Trigger?.Type.ToString() ?? "none"))}]"
        );
        Assert.Contains(heli.Queue.Blocks, b => b.Trigger?.Type == BlockTriggerType.AfterRunwayCrossing);
        Assert.IsType<CrossingRunwayPhase>(TickTo<CrossingRunwayPhase>(engine, heli));
        Assert.Null(heli.Ground.ParkingSpot);
    }

    /// <summary>
    /// The air taxi must not leave the superseded taxi route behind: <c>AircraftState.FromSnapshot</c> re-binds a
    /// restored <see cref="HoldingShortPhase"/> to <c>Ground.AssignedTaxiRoute.GetHoldShortAt(NodeId)</c>, so a
    /// stale point at the same node — a crossing the aircraft was already cleared through, say — would replace
    /// the terminus hold on the next rewind and let RES release it.
    /// </summary>
    [Fact]
    public void AirTaxiToARunway_AfterATaxiToTheSameRunway_SurvivesASnapshotRoundTrip()
    {
        if (Setup() is not { } fixture)
        {
            return;
        }

        var (engine, layout, heli) = fixture;
        var runway = CommandDispatcher.ResolveRunway(heli, Runway);
        Assert.NotNull(runway);

        // A taxi clearance to the same runway first: its route ends at a 28L hold short. Clearing that point is
        // the state a takeoff clearance leaves on the route.
        Assert.True(engine.SendCommand("TEST1", $"TAXIAUTO {Runway}").Success);
        var taxiRoute = heli.Ground.AssignedTaxiRoute;
        Assert.NotNull(taxiRoute);
        var destination = taxiRoute.HoldShortPoints.First(h => h.Reason == HoldShortReason.DestinationRunway);
        destination.IsCleared = true;
        output.WriteLine($"taxi route ends at node {destination.NodeId}, cleared");

        Assert.True(engine.SendCommand("TEST1", $"ATXI {Runway}").Success);
        Assert.Null(heli.Ground.AssignedTaxiRoute);

        var holding = TickToHoldShort(engine, heli, runway);
        output.WriteLine($"air taxi holds at node {holding.HoldShort.NodeId}");

        var dto = heli.ToSnapshot();
        var restored = AircraftState.FromSnapshot(dto, layout);
        var restoredHold = Assert.IsType<HoldingShortPhase>(restored.Phases?.CurrentPhase);

        Assert.Equal(HoldShortReason.DestinationRunway, restoredHold.HoldShort.Reason);
        Assert.False(restoredHold.HoldShort.IsCleared);
        Assert.Null(restored.Ground.AssignedTaxiRoute);

        var res = CommandDispatcher.Dispatch(
            CommandParser.Parse("RES").Value!,
            restored,
            TestDispatch.Context(new SerializableRandom(42), groundLayout: layout)
        );
        Assert.False(res.Success, "RES was accepted at the restored destination-runway hold");

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(dto), System.Text.Json.JsonSerializer.Serialize(restored.ToSnapshot()));
    }

    /// <summary>Ticks to the hold short, asserting the heli never sits on or hovers over the runway pavement on the way.</summary>
    private HoldingShortPhase TickToHoldShort(SimulationEngine engine, AircraftState heli, RunwayInfo runway)
    {
        for (int t = 1; t <= MaxTicks; t++)
        {
            engine.TickOneSecond();
            Assert.False(RunwayOccupancy.IsOverOrOnPavement(heli, runway), $"heli was over the {runway.Designator} pavement at t={t}s");
            if (heli.Phases?.CurrentPhase is HoldingShortPhase holding)
            {
                output.WriteLine($"holding short at t={t}s, node={holding.HoldShort.NodeId}");
                return holding;
            }
        }

        Assert.Fail($"heli never reached a hold short within {MaxTicks}s (phase={heli.Phases?.CurrentPhase?.Name})");
        throw new InvalidOperationException("unreachable");
    }

    private T TickTo<T>(SimulationEngine engine, AircraftState heli)
        where T : Phase
    {
        for (int t = 1; t <= MaxTicks; t++)
        {
            engine.TickOneSecond();
            if (heli.Phases?.CurrentPhase is T phase)
            {
                output.WriteLine($"reached {typeof(T).Name} at t={t}s");
                return phase;
            }
        }

        Assert.Fail($"heli never reached {typeof(T).Name} within {MaxTicks}s (phase={heli.Phases?.CurrentPhase?.Name})");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>An EC35 parked on KOAK's HELI pad, in an engine loaded with the real OAK layout.</summary>
    private (SimulationEngine Engine, AirportGroundLayout Layout, AircraftState Heli)? Setup()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var layout = groundData.GetLayout("OAK");
        if (layout is null)
        {
            return null;
        }

        var heliSpot = layout.FindSpotByName("HELI");
        Assert.NotNull(heliSpot);

        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-atxi-runway-terminus",
                ScenarioName = "ATXI runway terminus",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };

        var heli = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "EC35",
            Position = heliSpot.Position,
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            Altitude = KoakFieldElevFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
        };
        // Cached at spawn on a scenario-loaded aircraft; the ground commands read it directly.
        heli.Ground.Layout = layout;
        heli.Phases = new PhaseList();
        heli.Phases.Add(new AtParkingPhase());
        heli.Phases.Start(CommandDispatcher.BuildMinimalContext(heli, layout));
        engine.World.AddAircraft(heli);

        return (engine, layout, heli);
    }
}
