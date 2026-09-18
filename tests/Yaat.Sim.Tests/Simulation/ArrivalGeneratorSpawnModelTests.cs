using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tests for the time-first arrival-generator spawn model. <c>IntervalTime</c> drives the spawn cadence;
/// when an arrival is due it is placed at the back of the stream at
/// <c>D = max(InitialDistance, rearmost + gap)</c>, where <c>gap = max(IntervalDistance, wakeMinimum)</c>,
/// capped at <c>MaxDistance</c> (the generator waits when no room exists rather than exceeding the cap).
/// An empty corridor has no leader, so the arrival spawns exactly at <c>InitialDistance</c>.
/// </summary>
public class ArrivalGeneratorSpawnModelTests(ITestOutputHelper output)
{
    private const int InitialDistance = 15;
    private const int MaxDistance = 50;
    private const int IntervalDistance = 5;

    private static string ScenarioJson(int intervalTime, bool randomizeInterval) =>
        $$"""
            {
              "id": "01TEST00000000000000000000",
              "name": "ArrivalGeneratorSpawnModelTests",
              "artccId": "ZOA",
              "primaryAirportId": "SFO",
              "aircraft": [],
              "initializationTriggers": [],
              "aircraftGenerators": [
                {
                  "id": "gen-28R",
                  "runway": "28R",
                  "engineType": "Jet",
                  "weightCategory": "Large",
                  "initialDistance": {{InitialDistance}},
                  "maxDistance": {{MaxDistance}},
                  "intervalDistance": {{IntervalDistance}},
                  "startTimeOffset": 0,
                  "maxTime": 3600,
                  "intervalTime": {{intervalTime}},
                  "randomizeInterval": {{(randomizeInterval ? "true" : "false")}},
                  "randomizeWeightCategory": false
                }
              ]
            }
            """;

    private SimulationEngine? BuildLoadedEngine(int intervalTime, bool randomizeInterval)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        var engine = new SimulationEngine(groundData);
        var warnings = engine.LoadScenario(
            ScenarioJson(intervalTime, randomizeInterval),
            rngSeed: 42,
            sessionStartUtc: MagneticDeclination.EvaluationDateUtc
        );
        foreach (var w in warnings)
        {
            output.WriteLine($"[load-warn] {w}");
        }
        return engine;
    }

    private void Dump(SimulationEngine engine)
    {
        foreach (var s in engine.GeneratorSpawnLog.OrderBy(s => s.ElapsedSeconds))
        {
            output.WriteLine(
                $"t={s.ElapsedSeconds} d={s.SpawnDistanceNm:F1} rearmost={s.RearmostAtSpawnNm?.ToString("F1") ?? "none"} gap={s.RequiredGapNm:F1}"
            );
        }
    }

    [Fact]
    public void LongInterval_KeepsArrivalsNearInitialDistance_PacedByTime()
    {
        var engine = BuildLoadedEngine(intervalTime: 180, randomizeInterval: false);
        if (engine is null)
        {
            return;
        }

        for (int t = 0; t < 600; t++)
        {
            engine.TickOneSecond();
        }
        Dump(engine);

        var spawns = engine.GeneratorSpawnLog.OrderBy(s => s.ElapsedSeconds).ToList();
        Assert.True(spawns.Count >= 3, $"expected a sustained stream, got {spawns.Count} spawns");

        // Empty corridor at activation -> first arrival at InitialDistance, with no leader.
        Assert.Null(spawns[0].RearmostAtSpawnNm);
        Assert.Equal(InitialDistance, spawns[0].SpawnDistanceNm, precision: 1);

        // A long interval drains the corridor between spawns, so every arrival keeps entering near
        // InitialDistance -- NOT at the back of the corridor (MaxDistance). This is the time-first signature.
        foreach (var s in spawns)
        {
            Assert.InRange(s.SpawnDistanceNm, InitialDistance - 0.5, InitialDistance + IntervalDistance);
            Assert.True(s.SpawnDistanceNm <= MaxDistance + 1e-6, "no spawn may exceed MaxDistance");
            if (s.RearmostAtSpawnNm is double rear)
            {
                Assert.True(
                    s.SpawnDistanceNm - rear >= s.RequiredGapNm - 1e-6,
                    $"spawn at {s.SpawnDistanceNm:F2} is closer than the {s.RequiredGapNm:F2}nm gap behind rearmost {rear:F2}"
                );
            }
        }

        // IntervalTime (180s @ 100%) sets the cadence between consecutive arrivals; with the corridor
        // never backed up, randomize off gives an exact, defer-free cadence.
        var expected = ScenarioPacing.EffectiveArrivalGeneratorIntervalSeconds(180, 100);
        for (int i = 1; i < spawns.Count; i++)
        {
            Assert.Equal(expected, spawns[i].ElapsedSeconds - spawns[i - 1].ElapsedSeconds, precision: 6);
        }
    }

    [Fact]
    public void ShortInterval_PacksStreamTowardMaxDistance_ThenWaits()
    {
        var engine = BuildLoadedEngine(intervalTime: 15, randomizeInterval: false);
        if (engine is null)
        {
            return;
        }

        for (int t = 0; t < 600; t++)
        {
            engine.TickOneSecond();
        }
        Dump(engine);

        var spawns = engine.GeneratorSpawnLog.OrderBy(s => s.ElapsedSeconds).ToList();
        Assert.NotEmpty(spawns);

        // The cap is hard: no arrival is ever placed beyond MaxDistance.
        foreach (var s in spawns)
        {
            Assert.True(s.SpawnDistanceNm <= MaxDistance + 1e-6, $"spawn at {s.SpawnDistanceNm:F2} exceeds MaxDistance {MaxDistance}");
        }

        // First arrival enters an empty corridor at InitialDistance; a short interval then fills the
        // corridor, stacking subsequent arrivals back from InitialDistance toward the MaxDistance cap.
        Assert.Equal(InitialDistance, spawns[0].SpawnDistanceNm, precision: 1);
        Assert.True(
            spawns.Max(s => s.SpawnDistanceNm) >= MaxDistance - IntervalDistance,
            $"stream did not pack toward the cap; deepest spawn was {spawns.Max(s => s.SpawnDistanceNm):F1}nm"
        );

        // Once packed, the generator throttles on 'no room' rather than firing every IntervalTime, so the
        // realised count is far below the naive 600/15 the timer alone would produce.
        Assert.True(spawns.Count < 40, $"expected throttling near the cap, got {spawns.Count} spawns");
    }

    [Fact]
    public void RandomizeInterval_JittersCadence_AroundTheBaseInterval()
    {
        var engine = BuildLoadedEngine(intervalTime: 180, randomizeInterval: true);
        if (engine is null)
        {
            return;
        }

        for (int t = 0; t < 1800; t++)
        {
            engine.TickOneSecond();
        }
        Dump(engine);

        var spawns = engine.GeneratorSpawnLog.OrderBy(s => s.ElapsedSeconds).ToList();
        Assert.True(spawns.Count >= 6, $"expected several spawns over 1800s, got {spawns.Count}");

        // The long interval keeps the corridor drained, so each cadence equals the jittered interval
        // (plus up to ~1s tick rounding). Jitter is +-25% of the 180s base, so every gap stays bounded.
        var diffs = new List<double>();
        for (int i = 1; i < spawns.Count; i++)
        {
            var diff = spawns[i].ElapsedSeconds - spawns[i - 1].ElapsedSeconds;
            diffs.Add(diff);
            Assert.InRange(diff, (180 * 0.75) - 1.5, (180 * 1.25) + 1.5);
        }

        // Randomize must actually vary the cadence -- not a metronome.
        Assert.True(diffs.Distinct().Count() > 1, "randomizeInterval produced a constant cadence");

        // ...but it stays centred on the base interval rather than drifting.
        Assert.InRange(diffs.Average(), 150, 210);
    }

    // The OAK RWY 30 generator from the S2-OAK-P practical-exam bundle, started at t=0 with no scripted traffic,
    // so the only aircraft in the corridor on the first spawn is the one a test injects.
    private const string Oak30GeneratorId = "gen-30";
    private const int OakInitialDistance = 10;

    private static string OakScenarioJson() =>
        $$"""
            {
              "id": "01TEST00000000000000000001",
              "name": "ArrivalGeneratorSpawnModelTests-OAK",
              "artccId": "ZOA",
              "primaryAirportId": "OAK",
              "aircraft": [],
              "initializationTriggers": [],
              "aircraftGenerators": [
                {
                  "id": "{{Oak30GeneratorId}}",
                  "runway": "30",
                  "engineType": "Jet",
                  "weightCategory": "Large",
                  "initialDistance": {{OakInitialDistance}},
                  "maxDistance": {{MaxDistance}},
                  "intervalDistance": {{IntervalDistance}},
                  "startTimeOffset": 0,
                  "maxTime": 3600,
                  "intervalTime": 180,
                  "randomizeInterval": false,
                  "randomizeWeightCategory": false
                }
              ]
            }
            """;

    private (SimulationEngine Engine, RunwayInfo Runway)? BuildOakEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            return null;
        }

        var engine = new SimulationEngine(groundData);
        var warnings = engine.LoadScenario(OakScenarioJson(), rngSeed: 42, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        foreach (var w in warnings)
        {
            output.WriteLine($"[load-warn] {w}");
        }

        var runway = engine.Scenario!.Generators.Single(g => g.Config.Id == Oak30GeneratorId).Runway;
        return (engine, runway);
    }

    /// <summary>Ticks until the RWY 30 generator logs its first spawn and returns that record.</summary>
    private GeneratorSpawnRecord FirstOak30Spawn(SimulationEngine engine)
    {
        for (int t = 0; (t < 10) && !engine.GeneratorSpawnLog.Any(s => s.GeneratorId == Oak30GeneratorId); t++)
        {
            engine.TickOneSecond();
        }
        Dump(engine);
        return engine.GeneratorSpawnLog.First(s => s.GeneratorId == Oak30GeneratorId);
    }

    /// <summary>
    /// An airborne aircraft on the RWY 30 extended centreline, on the approach side of the threshold, tracking
    /// straight away from the runway and climbing — the RPO's <c>FH 110</c> VFR departure from the bundle.
    /// </summary>
    private static AircraftState OutboundClimber(RunwayInfo runway, double distanceNm, PhaseList? phases)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var outbound = new TrueHeading((runway.TrueHeading.Degrees + 180.0) % 360.0);
        return new AircraftState
        {
            Callsign = "N25313",
            AircraftType = "C172",
            Position = GeoMath.ProjectPoint(threshold, outbound, distanceNm),
            TrueHeading = outbound,
            TrueTrack = outbound,
            Altitude = 3500,
            IndicatedAirspeed = 100,
            VerticalSpeed = 500,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
            Phases = phases,
        };
    }

    [Fact]
    public void OutboundAircraftOnCentreline_IsNotCountedAsRearmostInbound()
    {
        var setup = BuildOakEngine();
        if (setup is null)
        {
            return;
        }
        var (engine, runway) = setup.Value;

        engine.World.AddAircraft(OutboundClimber(runway, distanceNm: 23, phases: null));

        var spawn = FirstOak30Spawn(engine);
        Assert.Equal(OakInitialDistance, spawn.SpawnDistanceNm, precision: 1);
        Assert.Null(spawn.RearmostAtSpawnNm);
    }

    [Fact]
    public void DepartureRunwayAssignment_IsNotLandingIntent()
    {
        var setup = BuildOakEngine();
        if (setup is null)
        {
            return;
        }
        var (engine, runway) = setup.Value;

        // A departure carries AssignedRunway = its departure runway; with no phase, approach or landing clearance
        // that is not an intent to land, so the outbound aircraft still stays out of the stream.
        engine.World.AddAircraft(OutboundClimber(runway, distanceNm: 23, phases: new PhaseList { AssignedRunway = runway }));

        var spawn = FirstOak30Spawn(engine);
        Assert.Equal(OakInitialDistance, spawn.SpawnDistanceNm, precision: 1);
        Assert.Null(spawn.RearmostAtSpawnNm);
    }

    [Fact]
    public void ArrivalOnFinal_IsRearmostInbound_SpawnSitsBehindIt()
    {
        var setup = BuildOakEngine();
        if (setup is null)
        {
            return;
        }
        var (engine, runway) = setup.Value;

        engine.World.AddAircraft(GeneratorArrivalOnFinal(runway, "SWA999", distanceNm: 23));

        var spawn = FirstOak30Spawn(engine);
        Assert.NotNull(spawn.RearmostAtSpawnNm);
        Assert.InRange(spawn.RearmostAtSpawnNm.Value, 22.5, 23.5);
        Assert.Equal(spawn.RearmostAtSpawnNm.Value + spawn.RequiredGapNm, spawn.SpawnDistanceNm, precision: 6);
        Assert.InRange(spawn.SpawnDistanceNm, 27.5, 28.5);
    }

    [Fact]
    public void LandingIntentAcrossTheBand_IsRearmostInbound_SpawnSitsBehindIt()
    {
        var setup = BuildOakEngine();
        if (setup is null)
        {
            return;
        }
        var (engine, runway) = setup.Value;

        // A base leg inside the 2 nm band: 1 nm right of the extended centreline 15 nm out, tracking 90 degrees off
        // the landing course back toward it, already cleared to land on 30.
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var outbound = new TrueHeading((runway.TrueHeading.Degrees + 180.0) % 360.0);
        var rightOfCourse = new TrueHeading((runway.TrueHeading.Degrees + 90.0) % 360.0);
        var baseHeading = new TrueHeading((runway.TrueHeading.Degrees + 270.0) % 360.0);
        engine.World.AddAircraft(
            new AircraftState
            {
                Callsign = "SWA998",
                AircraftType = "B738",
                Position = GeoMath.ProjectPoint(GeoMath.ProjectPoint(threshold, outbound, 15), rightOfCourse, 1),
                TrueHeading = baseHeading,
                TrueTrack = baseHeading,
                Altitude = 4000,
                IndicatedAirspeed = 210,
                IsOnGround = false,
                FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
                Phases = new PhaseList { AssignedRunway = runway, LandingClearance = ClearanceType.ClearedToLand },
            }
        );

        var spawn = FirstOak30Spawn(engine);
        Assert.NotNull(spawn.RearmostAtSpawnNm);
        Assert.InRange(spawn.RearmostAtSpawnNm.Value, 14.5, 15.5);
        Assert.Equal(spawn.RearmostAtSpawnNm.Value + spawn.RequiredGapNm, spawn.SpawnDistanceNm, precision: 6);
    }

    /// <summary>
    /// An outbound aircraft (no phase, no clearance) on the RWY 30 centreline at the spawn point, level with the spawn
    /// altitude, is inside standard separation (3 nm / 1,000 ft, the check the VFR and overflight generators use): the
    /// due spawn is held, and it happens once the outbound aircraft has moved clear.
    /// </summary>
    [Fact]
    public void OutboundTrafficAtTheSpawnPoint_HoldsTheSpawn_UntilItHasMovedClear()
    {
        var setup = BuildOakEngine();
        if (setup is null)
        {
            return;
        }
        var (engine, runway) = setup.Value;

        var (_, spawnAltitudeFt) = AircraftInitializer.FinalApproachPoint(runway, AircraftCategory.Jet, OakInitialDistance);
        var outbound = OutboundClimber(runway, distanceNm: OakInitialDistance, phases: null);
        outbound.Altitude = spawnAltitudeFt;
        outbound.VerticalSpeed = 0;
        engine.World.AddAircraft(outbound);

        engine.TickOneSecond();
        Assert.Empty(engine.GeneratorSpawnLog);

        for (int t = 0; (t < 300) && engine.GeneratorSpawnLog.Count == 0; t++)
        {
            engine.TickOneSecond();
        }
        Dump(engine);

        var spawn = Assert.Single(engine.GeneratorSpawnLog);
        Assert.Equal(OakInitialDistance, spawn.SpawnDistanceNm, precision: 1);
        var arrival = engine.FindAircraft(spawn.Callsign)!;
        var passing = engine.FindAircraft(outbound.Callsign)!;
        double lateralNm = GeoMath.DistanceNm(arrival.Position, passing.Position);
        double verticalFt = Math.Abs(arrival.Altitude - passing.Altitude);
        output.WriteLine($"spawned at t={spawn.ElapsedSeconds}s: {lateralNm:F2} nm / {verticalFt:F0} ft from {passing.Callsign}");
        Assert.True(
            (lateralNm >= VfrSpawnSiting.MinLateralSeparationNm) || (verticalFt >= VfrSpawnSiting.MinVerticalSeparationFt),
            $"the arrival spawned {lateralNm:F2} nm / {verticalFt:F0} ft from the outbound aircraft"
        );
    }

    /// <summary>The same outbound aircraft 1,800 ft below the spawn altitude is vertically separated, so the spawn is on time.</summary>
    [Fact]
    public void OutboundTrafficWellBelowTheSpawnAltitude_DoesNotHoldTheSpawn()
    {
        var setup = BuildOakEngine();
        if (setup is null)
        {
            return;
        }
        var (engine, runway) = setup.Value;

        var (_, spawnAltitudeFt) = AircraftInitializer.FinalApproachPoint(runway, AircraftCategory.Jet, OakInitialDistance);
        var outbound = OutboundClimber(runway, distanceNm: OakInitialDistance, phases: null);
        outbound.Altitude = spawnAltitudeFt - 1800;
        outbound.VerticalSpeed = 0;
        engine.World.AddAircraft(outbound);

        engine.TickOneSecond();

        var spawn = Assert.Single(engine.GeneratorSpawnLog);
        Assert.Equal(OakInitialDistance, spawn.SpawnDistanceNm, precision: 1);
    }

    /// <summary>
    /// The arrival the spawn is placed behind is never separation traffic for it: the corridor arrivals placement spaced
    /// behind are left out of the check, so the in-trail spawn at rearmost + gap goes out on time.
    /// </summary>
    [Fact]
    public void ArrivalAtTheRearmost_DoesNotHoldTheSpawnBehindIt()
    {
        var setup = BuildOakEngine();
        if (setup is null)
        {
            return;
        }
        var (engine, runway) = setup.Value;

        engine.World.AddAircraft(GeneratorArrivalOnFinal(runway, "SWA997", distanceNm: 12));

        engine.TickOneSecond();

        var spawn = Assert.Single(engine.GeneratorSpawnLog);
        Assert.NotNull(spawn.RearmostAtSpawnNm);
        Assert.InRange(spawn.RearmostAtSpawnNm.Value, 11.5, 12.5);
        Assert.Equal(spawn.RearmostAtSpawnNm.Value + spawn.RequiredGapNm, spawn.SpawnDistanceNm, precision: 6);
    }

    /// <summary>
    /// An arrival inbound to land that placement did not space behind is still separation traffic for the spawn. Here it
    /// is cleared to land on 30 but joining on a 30° intercept 2.5 nm right of the extended centreline, outside the 2 nm
    /// corridor band, abeam the 10 nm spawn point at the spawn altitude — inside 3 nm / 1,000 ft, so the due spawn is held.
    /// </summary>
    [Fact]
    public void InboundArrivalOutsideTheCorridor_NearTheSpawnPoint_HoldsTheSpawn()
    {
        var setup = BuildOakEngine();
        if (setup is null)
        {
            return;
        }
        var (engine, runway) = setup.Value;

        var (spawnPoint, spawnAltitudeFt) = AircraftInitializer.FinalApproachPoint(runway, AircraftCategory.Jet, OakInitialDistance);
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var outbound = new TrueHeading((runway.TrueHeading.Degrees + 180.0) % 360.0);
        var rightOfCourse = new TrueHeading((runway.TrueHeading.Degrees + 90.0) % 360.0);
        var intercept = new TrueHeading((runway.TrueHeading.Degrees + 330.0) % 360.0);
        var joining = new AircraftState
        {
            Callsign = "SWA996",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(GeoMath.ProjectPoint(threshold, outbound, OakInitialDistance), rightOfCourse, 2.5),
            TrueHeading = intercept,
            TrueTrack = intercept,
            Altitude = spawnAltitudeFt,
            IndicatedAirspeed = 210,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
            Phases = new PhaseList { AssignedRunway = runway, LandingClearance = ClearanceType.ClearedToLand },
        };
        engine.World.AddAircraft(joining);
        double lateralNm = GeoMath.DistanceNm(spawnPoint, joining.Position);
        Assert.True(lateralNm < VfrSpawnSiting.MinLateralSeparationNm, $"premise: the joining arrival is {lateralNm:F2} nm from the spawn point");

        engine.TickOneSecond();

        Assert.Empty(engine.GeneratorSpawnLog);
    }

    /// <summary>A B738 generator arrival in <c>FinalApproachPhase</c> on the runway's final, placed the way the generator places one.</summary>
    private static AircraftState GeneratorArrivalOnFinal(RunwayInfo runway, string callsign, double distanceNm)
    {
        var init = AircraftInitializer.InitializeOnFinal(
            runway,
            AircraftCategory.Jet,
            callsign,
            requestedDistanceNm: distanceNm,
            aircraftType: "B738"
        );
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = init.Position,
            TrueHeading = init.TrueHeading,
            TrueTrack = init.TrueHeading,
            Altitude = init.Altitude,
            IndicatedAirspeed = init.Speed,
            IsOnGround = false,
            IsGeneratorArrival = true,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
            Phases = init.Phases,
        };
    }
}
