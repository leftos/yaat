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
/// Coverage test: every OAK parking spot can taxi cleanly to its nearest runway
/// hold-short via <c>TAXIAUTO &lt;RWY&gt;</c> with auto-crossing enabled.
///
/// <para>
/// Catches systemic taxi-resolver bugs that present at specific parking-to-runway
/// combinations — the OAK SIG4 / GA3 north-field spin (Issue 2) was the immediate
/// motivation. By spawning a C172 at every parking node in turn, the test surfaces
/// parallel-fillet ramp problems, A* misrouting, hold-short annotation gaps, and
/// auto-cross handling across the entire field rather than at a single curated spot.
/// </para>
///
/// <para>
/// Pass criterion: aircraft reaches a stable end state within 180 simulated seconds —
/// either <see cref="HoldingShortPhase"/> (route stops at a hold-short before the
/// runway) or <see cref="HoldingInPositionPhase"/> (route completes past an auto-
/// cleared crossing). Spots with no graph path to any runway are reported but
/// not failures — the test asserts on the routable population. Failure means the
/// aircraft is still in <see cref="TaxiingPhase"/> or <see cref="CrossingRunwayPhase"/>
/// after 180 s — i.e. it's orbiting or otherwise stuck.
/// </para>
/// </summary>
public class OakAllParkingTaxiAutoTests(ITestOutputHelper output)
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

    public static IEnumerable<object[]> AllOakParking()
    {
        TestVnasData.EnsureInitialized();
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            yield break;
        }

        foreach (var node in layout.Nodes.Values.Where(n => n.Type == GroundNodeType.Parking).OrderBy(n => n.Id))
        {
            yield return new object[] { node.Name ?? $"node{node.Id}", node.Id };
        }
    }

    [Theory]
    [MemberData(nameof(AllOakParking))]
    public void TaxiAutoFromParking_ReachesHoldShort(string parkingName, int parkingNodeId)
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        Assert.True(layout.Nodes.TryGetValue(parkingNodeId, out var parking));
        Assert.Equal(GroundNodeType.Parking, parking.Type);

        // North-field aircraft taxi to 28R (full-length lineup at east end / taxiway B);
        // south-field aircraft taxi to 30 (full-length lineup at SE end / taxiway W1).
        // Boundary: 10L/28R centerline latitude (~37.7255). All aircraft are C172 so
        // turn radii fit fillet geometry at GA-sized ramps everywhere on the field.
        bool isNorthField = parking.Position.Lat > 37.7255;
        string assignedRunway = isNorthField ? "28R" : "30";
        string aircraftType = "C172";
        var runway = NavigationDatabase.Instance.GetRunway("OAK", assignedRunway);
        if (runway is null)
        {
            output.WriteLine($"SKIP {parkingName} (node {parkingNodeId}): no runway data for OAK {assignedRunway}");
            return;
        }

        var holdShortNodes = layout.GetRunwayHoldShortNodes(runway.Designator);
        if (holdShortNodes.Count == 0)
        {
            output.WriteLine($"SKIP {parkingName} (node {parkingNodeId}): no hold-short nodes for {runway.Designator}");
            return;
        }

        // Verify the pathfinder can find a route at all before spending tick budget.
        // Spots with no graph connectivity to the runway are reported, not asserted.
        if (TaxiPathfinder.FindRoute(layout, parkingNodeId, holdShortNodes[0].Id, AircraftCategory.Jet) is null)
        {
            output.WriteLine($"SKIP {parkingName} (node {parkingNodeId}): no graph route to RWY {runway.Designator}");
            return;
        }

        var callsign = $"N{parkingNodeId:D3}T";
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
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
                CruiseAltitude = 1500,
            },
        };

        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AtParkingPhase());
        var startCtx = CommandDispatcher.BuildMinimalContext(aircraft, layout);
        aircraft.Phases.Start(startCtx);
        aircraft.Ground.Layout = layout;

        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-oak-all-parking",
            ScenarioName = "OAK All Parking Taxi-Auto",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            AutoCrossRunway = true,
        };

        var result = engine.SendCommand(callsign, $"TAXIAUTO {runway.Designator}");
        Assert.True(result.Success, $"{parkingName}: TAXIAUTO {runway.Designator} failed: {result.Message}");

        // C172 nominal taxi speed is 20 kt but fillet-arc corners drop the
        // effective average to ~10 kt across the whole field. The longest
        // cross-field route (PCM2 → 30 = 10082 ft, 79 segments) sustains
        // ~16 ft/s end-to-end. 900 s = 15 min covers the worst case with
        // margin; anything past this is stuck-orbit, not just slow.
        const int maxSeconds = 900;
        bool reached = TickUntil(engine, aircraft, maxSeconds, ac => ac.Phases?.CurrentPhase is HoldingShortPhase or HoldingInPositionPhase);

        var finalPhase = aircraft.Phases?.CurrentPhase?.Name ?? "(none)";
        var finalRoute = aircraft.Ground.AssignedTaxiRoute;
        var segInfo = finalRoute is null ? "no-route" : $"seg {finalRoute.CurrentSegmentIndex}/{finalRoute.Segments.Count}";

        Assert.True(
            reached,
            $"{parkingName} (node {parkingNodeId}, RWY {runway.Designator}): stuck taxiing after {maxSeconds}s — expected to reach "
                + $"HoldingShortPhase or HoldingInPositionPhase. Final phase={finalPhase}, {segInfo}, "
                + $"pos=({aircraft.Position.Lat:F6},{aircraft.Position.Lon:F6}), gs={aircraft.GroundSpeed:F1}"
        );

        output.WriteLine($"OK {parkingName} (node {parkingNodeId}) → RWY {runway.Designator}: {finalPhase}, {segInfo}");
    }

    private bool TickUntil(SimulationEngine engine, AircraftState aircraft, int maxSeconds, Func<AircraftState, bool> condition)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            if (condition(aircraft))
            {
                return true;
            }
        }
        return false;
    }
}
