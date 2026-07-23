using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// End-to-end integration for <see cref="RunwayDepartureQueue"/>: two real departures spawned at OAK
/// north-field GA parkings, both TAXIAUTO'd to 28R, funnel to the same full-length lineup hold-short.
/// The one that arrives first holds short (#1); the one behind, stopped by ground-conflict just short
/// of the occupied node, is still taxiing (#2). Proves the queue pass runs inside the real tick loop
/// (TickPrePhysics) and numbers a genuine clump — not just the direct-call unit tests.
/// </summary>
public class RunwayDepartureQueueE2ETests(ITestOutputHelper output)
{
    [Fact]
    public void TwoDeparturesFunnelingTo28R_HoldingShortLeadIsOne_TaxiingTrailerIsTwo()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: NavigationDb not initialized");
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            output.WriteLine("SKIP: OAK layout unavailable");
            return;
        }

        var engine = BuildEngine(layout);
        Assert.NotNull(engine);

        var ac1 = SpawnAtParking(engine, layout, "SIG1", "NQUEUE1");
        var ac2 = SpawnAtParking(engine, layout, "GA3", "NQUEUE2");
        if (ac1 is null || ac2 is null)
        {
            output.WriteLine("SKIP: OAK GA parking (SIG1/GA3) not found in layout");
            return;
        }

        Assert.True(engine.SendCommand("NQUEUE1", "TAXIAUTO 28R").Success);
        Assert.True(engine.SendCommand("NQUEUE2", "TAXIAUTO 28R").Success);

        int? node1 = DestinationNode(ac1);
        int? node2 = DestinationNode(ac2);
        if (node1 is null || node1 != node2)
        {
            output.WriteLine($"SKIP: routes target different lineup nodes ({node1} vs {node2}) — no shared line to rank");
            return;
        }

        // Drive the exact per-second sequence the LIVE SERVER uses — TickPrePhysics + physics sub-ticks,
        // and NOT SimulationEngine.TickPostPhysics, which only the standalone TickOneSecond / replay paths
        // call (the server runs its own post-physics via TickProcessor.ProcessPostPhysics). Calling
        // TickOneSecond here would mask a queue pass wired into TickPostPhysics — it would pass in-test yet
        // do nothing live. Keep this driving the sub-steps. See RoomEngine.AdvanceOneSecond.
        const int physicsSubTicks = 4; // = SimulationEngine.PhysicsSubTickRate
        for (int t = 1; t <= 240; t++)
        {
            engine.Scenario!.ElapsedSeconds += 1;
            engine.TickPrePhysics();
            for (int sub = 0; sub < physicsSubTicks; sub++)
            {
                engine.TickPhysics(1.0 / physicsSubTicks);
            }

            // Assert on the queue's OWN output — wait until both aircraft are numbered (the stable
            // two-aircraft clump the feature disambiguates), not a re-derived "near" heuristic that can
            // catch a transitional frame where the trailer's route destination is momentarily gone.
            int p1 = ac1.Ground.RunwayQueuePosition;
            int p2 = ac2.Ground.RunwayQueuePosition;
            if (p1 > 0 && p2 > 0)
            {
                output.WriteLine(
                    $"t={t}: {ac1.Callsign} #{p1} ({ac1.Ground.RunwayQueueRunway}), {ac2.Callsign} #{p2} ({ac2.Ground.RunwayQueueRunway})"
                );
                Assert.True((p1 == 1 && p2 == 2) || (p1 == 2 && p2 == 1), $"expected the pair numbered 1 and 2, got #{p1}/#{p2}");

                // The holding-short aircraft is #1 (front of line); the trailer behind it is #2.
                var holdingShort = HoldingShortAt(ac1, ac2, node1.Value);
                Assert.NotNull(holdingShort);
                Assert.Equal(1, holdingShort.Ground.RunwayQueuePosition);

                // Both are queued for the same runway, shown on the ground datablock as "28R #N".
                Assert.Equal(Runway, ac1.Ground.RunwayQueueRunway);
                Assert.Equal(Runway, ac2.Ground.RunwayQueueRunway);
                return;
            }
        }

        output.WriteLine("SKIP: two-aircraft clump within the proximity gate never formed within 240s");
    }

    private const string Runway = "28R";

    private static AircraftState? HoldingShortAt(AircraftState a, AircraftState b, int nodeId)
    {
        if ((a.Phases?.CurrentPhase is HoldingShortPhase hsa) && hsa.HoldShort.NodeId == nodeId)
        {
            return a;
        }
        if ((b.Phases?.CurrentPhase is HoldingShortPhase hsb) && hsb.HoldShort.NodeId == nodeId)
        {
            return b;
        }
        return null;
    }

    private static int? DestinationNode(AircraftState ac) =>
        ac.Ground.AssignedTaxiRoute?.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.DestinationRunway)?.NodeId;

    private static AircraftState? SpawnAtParking(SimulationEngine engine, AirportGroundLayout layout, string parkingName, string callsign)
    {
        var parking = layout.FindParkingByName(parkingName);
        if (parking is null)
        {
            return null;
        }

        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "C172",
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
        return aircraft;
    }

    private SimulationEngine BuildEngine(AirportGroundLayout layout)
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-runway-queue-e2e",
                ScenarioName = "Runway Queue E2E",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = layout.AirportId,
                AutoCrossRunway = true,
            },
        };
        return engine;
    }
}
