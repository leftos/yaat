using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Phases;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// In-trail compression bug (QXE831 / SWA8154 too close on final): a faster arrival-generator
/// follower behind a slower leader on the same final closed the spawn spacing with no in-trail
/// speed management, collapsing separation below the 3 NM radar floor (the reporter saw 1.3 NM).
///
/// Generator arrivals spawn OnFinal at a fixed distance-based speed (farther out = faster), so a
/// follower is always faster than the closer-in, decelerating leader. The fix makes the arrival
/// generator act as a naive approach controller (TRACON), capping the follower's speed so the
/// stream holds its spacing down the final.
///
/// This test constructs the failing geometry live (a slow turboprop leader and a faster jet
/// follower on the OAK rwy 30 final, ~5 NM apart, both generator arrivals) and ticks the real
/// engine. Without the spacing manager the follower overruns the leader and busts 3 NM; with it
/// the gap holds. A live construction (not recording replay) is used deliberately: replaying a
/// recording re-injects spawns at their recorded positions, which the speed-managed stream no
/// longer matches, so replay cannot fairly exercise the manager.
/// </summary>
public class ArrivalGeneratorInTrailSpacingTests(ITestOutputHelper output)
{
    private const string ScenarioPath = "TestData/issue153-s2-oak-5-2-scenario.json";

    /// <summary>7110.65 §5-5-4 terminal radar separation floor (nm).</summary>
    private const double RadarFloorNm = 3.0;

    [Fact]
    public void FasterFollower_HoldsSpacing_BehindSlowerLeader()
    {
        var engine = LoadOakEngine();
        if (engine is null)
        {
            return;
        }

        var rwy30 = engine.Scenario!.Generators.Single(g => g.Config.Runway == "30").Runway;
        var threshold = new LatLon(rwy30.ThresholdLatitude, rwy30.ThresholdLongitude);

        // Slow turboprop leader at 20 NM; faster jet follower 5 NM behind. Both generator
        // arrivals on the rwy 30 final.
        InjectArrival(engine, rwy30, "DAL1", "DH8D", 20.0, isGeneratorArrival: true);
        InjectArrival(engine, rwy30, "SWA2", "B739", 25.0, isGeneratorArrival: true);

        double minGapWhileBothOut = double.MaxValue;
        int minT = -1;

        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();

            var leader = engine.FindAircraft("DAL1");
            var follower = engine.FindAircraft("SWA2");
            if (leader is null || follower is null)
            {
                break;
            }

            double leaderDist = GeoMath.DistanceNm(leader.Position, threshold);
            double followerDist = GeoMath.DistanceNm(follower.Position, threshold);

            // Assert the radar floor only while both are still well out on the descent — the
            // last ~mile Vref-mismatch residual (a faster-Vref jet cannot fly below its own
            // Vref behind a slower one) is physically unavoidable and excluded.
            if (leaderDist < 5.0 || followerDist < 5.0)
            {
                continue;
            }

            double gap = GeoMath.DistanceNm(leader.Position, follower.Position);
            if (gap < minGapWhileBothOut)
            {
                minGapWhileBothOut = gap;
                minT = t;
            }
        }

        output.WriteLine($"Minimum in-trail separation (both >= 5 NM out): {minGapWhileBothOut:F2} NM at t={minT}s");

        Assert.True(
            minGapWhileBothOut >= RadarFloorNm,
            $"Faster follower busted the {RadarFloorNm:F0} NM radar floor behind a slower leader: {minGapWhileBothOut:F2} NM at t={minT}s"
        );
    }

    [Fact]
    public void ManualSpeedCommand_ReleasesAutoSpacing()
    {
        var engine = LoadOakEngine();
        if (engine is null)
        {
            return;
        }
        var rwy30 = engine.Scenario!.Generators.Single(g => g.Config.Runway == "30").Runway;

        InjectArrival(engine, rwy30, "DAL1", "DH8D", 10.0, isGeneratorArrival: true);
        InjectArrival(engine, rwy30, "SWA2", "B739", 15.0, isGeneratorArrival: true);

        for (int t = 1; t <= 5; t++)
        {
            engine.TickOneSecond();
        }

        var follower = engine.FindAircraft("SWA2");
        Assert.NotNull(follower);
        Assert.NotNull(follower.Targets.SpeedCeiling); // manager engaged

        var result = engine.SendCommand("SWA2", "SPD 200");
        Assert.True(result.Success, result.Message);

        engine.TickOneSecond();
        follower = engine.FindAircraft("SWA2");
        Assert.NotNull(follower);

        Assert.True(follower.Approach.AutoSpacingReleased, "manual speed command should release auto-spacing");
        // The manager must not re-impose a ceiling once released; SPD cleared it.
        Assert.Null(follower.Targets.SpeedCeiling);
    }

    [Fact]
    public void StudentTrackOwnership_ReleasesAutoSpacing()
    {
        var engine = LoadOakEngine();
        if (engine is null)
        {
            return;
        }
        var rwy30 = engine.Scenario!.Generators.Single(g => g.Config.Runway == "30").Runway;

        InjectArrival(engine, rwy30, "DAL1", "DH8D", 10.0, isGeneratorArrival: true);
        var follower = InjectArrival(engine, rwy30, "SWA2", "B739", 15.0, isGeneratorArrival: true);

        engine.TickOneSecond();
        Assert.NotNull(engine.FindAircraft("SWA2")!.Targets.SpeedCeiling); // manager engaged

        // The student controller takes the track — the simulated TRACON hands off speed authority.
        var student = TrackOwner.CreateNonNas("OAK_TWR");
        engine.Scenario.StudentPosition = student;
        engine.FindAircraft("SWA2")!.Track.Owner = TrackOwner.CreateNonNas("OAK_TWR");

        engine.TickOneSecond();
        follower = engine.FindAircraft("SWA2");
        Assert.NotNull(follower);

        Assert.True(follower.Approach.AutoSpacingReleased, "student ownership should release auto-spacing");
        Assert.Null(follower.Targets.SpeedCeiling);
    }

    [Fact]
    public void NonGeneratorArrival_IsNotManaged_ButServesAsLeader()
    {
        var engine = LoadOakEngine();
        if (engine is null)
        {
            return;
        }
        var rwy30 = engine.Scenario!.Generators.Single(g => g.Config.Runway == "30").Runway;

        // A manual (non-generator) leader and a generator follower: the follower IS managed
        // (a leader can be any origin), but a non-generator aircraft is never managed itself.
        InjectArrival(engine, rwy30, "MANUAL1", "DH8D", 10.0, isGeneratorArrival: false);
        InjectArrival(engine, rwy30, "GEN2", "B739", 15.0, isGeneratorArrival: true);
        InjectArrival(engine, rwy30, "MANUAL3", "B739", 20.0, isGeneratorArrival: false);

        for (int t = 1; t <= 5; t++)
        {
            engine.TickOneSecond();
        }

        var manualLeader = engine.FindAircraft("MANUAL1");
        var genFollower = engine.FindAircraft("GEN2");
        var manualFollower = engine.FindAircraft("MANUAL3");
        Assert.NotNull(manualLeader);
        Assert.NotNull(genFollower);
        Assert.NotNull(manualFollower);

        Assert.NotNull(genFollower.Targets.SpeedCeiling); // generator follower managed behind a manual leader
        Assert.Null(manualLeader.Targets.SpeedCeiling); // manual aircraft never managed
        Assert.Null(manualFollower.Targets.SpeedCeiling); // manual aircraft never managed
    }

    [Fact]
    public void NewMarkerFields_SurviveSnapshotRoundTrip()
    {
        var aircraft = new AircraftState
        {
            Callsign = "SWA2",
            AircraftType = "B739",
            IsGeneratorArrival = true,
        };
        aircraft.Approach.AutoSpacingReleased = true;

        var restored = AircraftState.FromSnapshot(aircraft.ToSnapshot(), groundLayout: null);

        Assert.True(restored.IsGeneratorArrival);
        Assert.True(restored.Approach.AutoSpacingReleased);
    }

    private SimulationEngine? LoadOakEngine()
    {
        if (!File.Exists(ScenarioPath))
        {
            return null;
        }
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
        engine.LoadScenario(File.ReadAllText(ScenarioPath), rngSeed: 1);
        return engine.Scenario is null ? null : engine;
    }

    private static AircraftState InjectArrival(
        SimulationEngine engine,
        RunwayInfo runway,
        string callsign,
        string type,
        double distanceNm,
        bool isGeneratorArrival
    )
    {
        var category = AircraftCategorization.Categorize(type);
        var init = AircraftInitializer.InitializeOnFinal(runway, category, requestedDistanceNm: distanceNm, aircraftType: type);

        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = init.Position,
            TrueHeading = init.TrueHeading,
            Altitude = init.Altitude,
            IndicatedAirspeed = init.Speed,
            IsOnGround = false,
            IsGeneratorArrival = isGeneratorArrival,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
            Phases = init.Phases,
        };

        engine.World.AddAircraft(aircraft);
        return aircraft;
    }
}
