using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A taxiing aircraft the ground conflict detector slows for a converging aircraft brakes at its category's taxi brake
/// rate; the convergence slowdown never takes speed off it faster than its brakes can.
///
/// <para>The FOLLOW montage's H2 clip (<c>tools/montage/follow/H2</c>) at KOAK: N738SP taxis from GA16 via F, C, B to
/// runway 28R while N52417 taxis north on K across F. Both routes run through node 439, so the pair classifies as
/// converging once N738SP's route reaches it, about 860 ft out. The recording (seed 20261001) showed N738SP drop from
/// 20.0 to 9.5 kt between t=42 and t=43 and from 16.0 to 8.6 kt between t=51 and t=52, before any controller
/// instruction to give way: the convergence limit appeared for one physics sub-tick and physics set the speed to it.</para>
/// </summary>
public class ConvergenceSlowdownBrakingTests(ITestOutputHelper output)
{
    private const string Lead = "N52417";
    private const string Follower = "N738SP";
    private const double FtPerNm = 6076.12;

    /// <summary>Where the late-convergence fixture starts the yielder from the shared node: close enough in that the
    /// routine rate no longer fits in the room before the stop ring.</summary>
    private const double LateConvergenceStartFt = 230.0;

    /// <summary>The clip's GIVEWAY is sent at t=56; the run stops before it so no controller hold is in play.</summary>
    private const int SecondsBeforeGiveWay = 55;

    private const string Scenario = """
        {
          "id": "01JH02G8F0L1W5T7R9N2K4X6QC",
          "name": "Montage H2 | Give way to traffic crossing ahead on the taxiway",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "studentPositionId": "01GEAMCGAZ418Q26GEPYNXWZ4A",
          "autoDeleteMode": "None",
          "initializationTriggers": [],
          "aircraftGenerators": [],
          "aircraft": [
            {
              "id": "01JH02N52417G8F0L1W5T7R9NB",
              "aircraftId": "N52417",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Coordinates", "coordinates": { "lat": 37.73075, "lon": -122.22539 }, "heading": 9 },
              "onAltitudeProfile": false,
              "flightplan": { "rules": "VFR", "departure": "KLVK", "destination": "KOAK", "cruiseAltitude": 3500, "cruiseSpeed": 0, "route": "", "remarks": "/V/", "aircraftType": "C172" },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            },
            {
              "id": "01JH02N738SPG8F0L1W5T7R9ND",
              "aircraftId": "N738SP",
              "aircraftType": "C172",
              "transponderMode": "Standby",
              "startingConditions": { "type": "Parking", "parking": "GA16" },
              "onAltitudeProfile": false,
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KLVK", "cruiseAltitude": 3500, "cruiseSpeed": 0, "route": "", "remarks": "/V/", "aircraftType": "C172" },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            }
          ],
          "atc": [
            { "id": "01G9CQW3XEE3RW84EBYKEBCPQD", "artccId": "ZOA", "facilityId": "OAK", "positionId": "01GEAMB98RKCPP9HCNPW5AVDA5", "autoConnect": true, "autoTrackAirportIds": [] }
          ],
          "flightStripConfigurations": []
        }
        """;

    private static readonly (int Second, string Callsign, string Command)[] Script =
    [
        (1, Lead, "CAINH"),
        (1, Follower, "CAINH"),
        (2, Follower, "TAXI F C B CROSS 33 RWY 28R"),
        (10, Lead, "TAXI K D @GA7 CROSS 33"),
    ];

    /// <summary>
    /// Until the GIVEWAY, N738SP never loses more speed in a second than a piston's taxi brake rate over the second plus
    /// the one sub-tick of change the ground snap window allows.
    /// </summary>
    [Fact]
    public void ConvergenceSlowdown_BeforeGiveWay_BrakesAtThePistonTaxiBrakeRate()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: navdata unavailable");
            return;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            output.WriteLine("SKIP: KOAK layout unavailable");
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData);
        engine.LoadScenario(Scenario, 20261001, MagneticDeclination.EvaluationDateUtc);
        AircraftState follower = engine.FindAircraft(Follower)!;
        Assert.NotNull(engine.FindAircraft(Lead));

        double maxLossPerSecondKts = CategoryPerformance.TaxiDecelRate(AircraftCategory.Piston) * (1.0 + (1.0 / SimulationEngine.PhysicsSubTickRate));
        (double LossKts, int Second) worst = (0.0, -1);
        double previousKts = follower.GroundSpeed;
        for (int second = 0; second < SecondsBeforeGiveWay; second++)
        {
            foreach ((int at, string callsign, string command) in Script.Where(line => line.Second == second))
            {
                CommandResult result = engine.SendCommand(callsign, command);
                Assert.True(result.Success, $"t={at}: {callsign} {command} was rejected: {result.Message}");
            }

            engine.TickOneSecond();
            double lossKts = previousKts - follower.GroundSpeed;
            output.WriteLine(
                $"t={second + 1} gs={follower.GroundSpeed:F2} loss={lossKts:F2} limit={follower.Ground.SpeedLimit?.ToString("F2") ?? "-"}"
            );
            if (lossKts > worst.LossKts)
            {
                worst = (lossKts, second + 1);
            }

            previousKts = follower.GroundSpeed;
        }

        Assert.True(follower.Ground.Hold is null, $"{Follower} was held before the GIVEWAY: {follower.Ground.Hold?.Kind}");
        Assert.True(
            worst.LossKts <= maxLossPerSecondKts + 1e-6,
            $"{Follower} lost {worst.LossKts:F2} kt in the second ending t={worst.Second}; a piston brakes at most {maxLossPerSecondKts:F2} kt/s"
        );
    }

    /// <summary>
    /// The convergence limit's sawtooth is brakeable. The ETA gate still re-clears the limit once the yielder has
    /// slowed to it — the arrival estimate is raised only to the 12 kt floor, so a yielder capped by this rule feeds
    /// its reduced speed back in — but with the limit floored at what the yielder can shed in one detector pass this
    /// is a sawtooth rather than the recording's cliff: no second loses more than the piston taxi brake rate plus the
    /// one sub-tick of change the ground snap window allows. The set/clear counts are reported for the record.
    /// </summary>
    [Fact]
    public void ConvergenceSlowdown_LimitSawtooth_BrakesAtThePistonRate()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: navdata unavailable");
            return;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            output.WriteLine("SKIP: KOAK layout unavailable");
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData);
        engine.LoadScenario(Scenario, 20261001, MagneticDeclination.EvaluationDateUtc);
        AircraftState follower = engine.FindAircraft(Follower)!;
        Assert.NotNull(engine.FindAircraft(Lead));

        double maxLossPerSecondKts = CategoryPerformance.TaxiDecelRate(AircraftCategory.Piston) * (1.0 + (1.0 / SimulationEngine.PhysicsSubTickRate));
        var limitedBySecond = new List<(int Second, bool Limited)>();
        double worstLossKts = 0.0;
        int worstSecond = -1;
        double previousKts = follower.GroundSpeed;
        for (int second = 0; second < SecondsBeforeGiveWay; second++)
        {
            foreach ((int at, string callsign, string command) in Script.Where(line => line.Second == second))
            {
                CommandResult result = engine.SendCommand(callsign, command);
                Assert.True(result.Success, $"t={at}: {callsign} {command} was rejected: {result.Message}");
            }

            engine.TickOneSecond();
            limitedBySecond.Add((second + 1, follower.Ground.SpeedLimit is not null));
            double lossKts = previousKts - follower.GroundSpeed;
            if (lossKts > worstLossKts)
            {
                worstLossKts = lossKts;
                worstSecond = second + 1;
            }

            previousKts = follower.GroundSpeed;
        }

        int firstLimitedSecond = limitedBySecond.FirstOrDefault(s => s.Limited).Second;
        Assert.True(firstLimitedSecond > 0, $"{Follower} never got a convergence limit before t={SecondsBeforeGiveWay}");

        int switches = limitedBySecond.Skip(1).Zip(limitedBySecond).Count(pair => pair.First.Limited != pair.Second.Limited);
        output.WriteLine(
            $"limit present on {limitedBySecond.Count(s => s.Limited)}/{SecondsBeforeGiveWay} seconds, {switches} set/clear switches, first set at t={firstLimitedSecond}"
        );
        output.WriteLine("limited seconds: " + string.Join(",", limitedBySecond.Where(s => s.Limited).Select(s => s.Second)));

        Assert.True(
            worstLossKts <= maxLossPerSecondKts + 1e-6,
            $"the convergence sawtooth took {worstLossKts:F2} kt off {Follower} in the second ending t={worstSecond}; a piston brakes at most {maxLossPerSecondKts:F2} kt/s"
        );
    }

    /// <summary>
    /// A convergence that only turns into a conflict close in must brake firmly to reach the stop ring at a walking
    /// pace. A C172 at 20 kt needs 168 ft to stop at its routine 2 kt/s, more than the ~150 ft of room left before
    /// <see cref="GroundConflictDetector.DefaultStopDistanceFt"/>, so the floor has to switch to the category's firm
    /// rate (<see cref="CategoryPerformance.ExpediteExitDecelRate"/>). Measured on the real SFO M1×M3 crossing:
    /// the routine rate leaves the yielder at ~6.7 kt when it reaches the ring, the firm rate at the ramp's 5 kt crawl.
    /// </summary>
    [Fact]
    public void LateConvergence_BrakesFirmlyToReachTheStopRingSlow()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: navdata unavailable");
            return;
        }

        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            output.WriteLine("SKIP: KSFO layout unavailable");
            return;
        }

        GroundNode? crossing = layout.FindIntersectionNode("M1", "M3");
        Assert.NotNull(crossing);
        GroundNode? yielderStart = NearestNodeOnTaxiwayInRange(layout, "M1", crossing.Position, 180.0, 320.0);
        GroundNode? winnerStart = NearestNodeOnTaxiwayInRange(layout, "M3", crossing.Position, 120.0, 450.0);
        Assert.NotNull(yielderStart);
        Assert.NotNull(winnerStart);

        TaxiRoute? yielderRoute = TaxiPathfinder.FindRoute(
            layout,
            yielderStart.Id,
            crossing.Id,
            AircraftCategory.Piston,
            WakeTurbulenceData.WakeClass.Small
        );
        TaxiRoute? winnerRoute = TaxiPathfinder.FindRoute(
            layout,
            winnerStart.Id,
            crossing.Id,
            AircraftCategory.Piston,
            WakeTurbulenceData.WakeClass.Small
        );
        Assert.NotNull(yielderRoute);
        Assert.NotNull(winnerRoute);
        Assert.Equal(crossing.Id, GroundConflictDetector.FindSharedUpcomingNode(yielderRoute, winnerRoute));

        AircraftState yielder = MakeTaxiing("N738SP", "C172", yielderStart, crossing.Position, 20.0, yielderRoute);
        // Pin the approach distance: at just over 200 ft the piston's routine stopping distance (0.42 v² ft = 169 ft
        // at 20 kt) no longer fits before the stop ring (room = separation − DefaultStopDistanceFt), and the geometry's
        // M1 node spacing is finer than the fixture needs.
        double approachFt = GeoMath.DistanceNm(yielder.Position, crossing.Position) * FtPerNm;
        Assert.True(approachFt > LateConvergenceStartFt, "the M1 start node is closer to the crossing than the fixture needs");
        AdvanceToward(yielder, crossing.Position, (approachFt - LateConvergenceStartFt) / FtPerNm);
        // The winner reaches the shared node first: park it there so the pair separation tracks the yielder's own
        // distance to the node, and hold its 20 kt so the closing-proximity trail cap (max(winner speed, 5 kt)) stays
        // above the convergence floor and does not mask it. Both are C172s, so GetSeparation's stop ring is exactly
        // DefaultStopDistanceFt and the ring in the assertion below is reachable.
        AircraftState winner = MakeTaxiing("N52417", "C172", winnerStart, crossing.Position, 20.0, winnerRoute);
        winner.Position = crossing.Position;

        double maxLossPerSecondKts =
            CategoryPerformance.ExpediteExitDecelRate(AircraftCategory.Piston) * (1.0 + (1.0 / SimulationEngine.PhysicsSubTickRate));
        const double delta = 1.0 / SimulationEngine.PhysicsSubTickRate;
        double speedAtRingKts = -1.0;
        double worstLossKts = 0.0;
        int worstSecond = -1;
        bool limited = false;
        double secondStartKts = yielder.GroundSpeed;
        for (int step = 0; step < 4 * 60; step++)
        {
            double distFt = GeoMath.DistanceNm(yielder.Position, crossing.Position) * FtPerNm;
            if (distFt <= GroundConflictDetector.DefaultStopDistanceFt)
            {
                // The speed the yielder carries into the ring, read before this sub-tick's clamp: inside the ring the
                // convergence stop branch (and the closing stop that fires on the same sub-tick) is the backstop.
                speedAtRingKts = yielder.GroundSpeed;
                break;
            }

            GroundConflictDetector.ApplySpeedLimits([yielder, winner], layout, delta);
            if (yielder.Ground.SpeedLimit is { } limit)
            {
                limited = true;
                if (yielder.IndicatedAirspeed > limit)
                {
                    yielder.IndicatedAirspeed = limit;
                }
            }

            AdvanceToward(yielder, crossing.Position, yielder.IndicatedAirspeed / 3600.0 * delta);
            if (step % SimulationEngine.PhysicsSubTickRate == SimulationEngine.PhysicsSubTickRate - 1)
            {
                int second = (step + 1) / SimulationEngine.PhysicsSubTickRate;
                output.WriteLine(
                    $"t={second} gs={yielder.GroundSpeed:F2} dist={distFt:F0}ft limit={yielder.Ground.SpeedLimit?.ToString("F2") ?? "-"}"
                );
                double lossKts = secondStartKts - yielder.GroundSpeed;
                if (lossKts > worstLossKts)
                {
                    worstLossKts = lossKts;
                    worstSecond = second;
                }

                secondStartKts = yielder.GroundSpeed;
            }
        }

        Assert.True(limited, "the convergence limit never applied; the test's premise (a late convergence) did not hold");
        Assert.True(speedAtRingKts >= 0, "the yielder never reached DefaultStopDistanceFt from the shared node");
        output.WriteLine($"speed entering the stop ring: {speedAtRingKts:F2} kt; worst per-second loss {worstLossKts:F2} kt at t={worstSecond}");
        Assert.True(
            speedAtRingKts <= GroundConflictDetector.SlowTaxiSpeedKts + 1e-6,
            $"the yielder entered the stop ring at {speedAtRingKts:F2} kt; firm braking must leave a walking pace (at most {GroundConflictDetector.SlowTaxiSpeedKts:F1} kt)"
        );
        Assert.True(
            worstLossKts <= maxLossPerSecondKts + 1e-6,
            $"the firm braking took {worstLossKts:F2} kt off in the second ending t={worstSecond}; the firm rate allows at most {maxLossPerSecondKts:F2} kt/s"
        );
    }

    private static void AdvanceToward(AircraftState aircraft, LatLon target, double moveNm)
    {
        double totalNm = GeoMath.DistanceNm(aircraft.Position, target);
        if ((totalNm <= 0) || (moveNm <= 0))
        {
            return;
        }

        double fraction = Math.Min(1.0, moveNm / totalNm);
        aircraft.Position = new LatLon(
            aircraft.Position.Lat + ((target.Lat - aircraft.Position.Lat) * fraction),
            aircraft.Position.Lon + ((target.Lon - aircraft.Position.Lon) * fraction)
        );
    }

    private static GroundNode? NearestNodeOnTaxiwayInRange(AirportGroundLayout layout, string taxiway, LatLon reference, double minFt, double maxFt)
    {
        double midFt = (minFt + maxFt) / 2.0;
        GroundNode? best = null;
        double bestErr = double.MaxValue;
        foreach (GroundNode node in layout.GetNodesOnTaxiway(taxiway))
        {
            double distFt = GeoMath.DistanceNm(node.Position, reference) * FtPerNm;
            if (distFt < minFt || distFt > maxFt)
            {
                continue;
            }

            double err = Math.Abs(distFt - midFt);
            if (err < bestErr)
            {
                bestErr = err;
                best = node;
            }
        }

        return best;
    }

    private static AircraftState MakeTaxiing(string callsign, string aircraftType, GroundNode start, LatLon toward, double ias, TaxiRoute route)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            Position = start.Position,
            TrueHeading = new TrueHeading(GeoMath.BearingTo(start.Position, toward)),
            IsOnGround = true,
            IndicatedAirspeed = ias,
            Ground = new AircraftGroundOps { AssignedTaxiRoute = route },
            Phases = new PhaseList(),
        };
        ac.Phases.Add(new TaxiingPhase());
        ac.Phases.CurrentPhase!.Status = PhaseStatus.Active;
        return ac;
    }
}
