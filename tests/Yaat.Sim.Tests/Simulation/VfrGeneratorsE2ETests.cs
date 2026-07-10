using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E coverage for the VFR arrival and overflight generators against real OAK navdata and the real Class B/C
/// airspace set. Asserts the spawns land inside their configured ranges, carry the right flight-plan and
/// beacon treatment, respect the airspace and separation gates, and (for overflights) fly a hemispheric
/// cruising altitude toward an exit point.
/// </summary>
public class VfrGeneratorsE2ETests(ITestOutputHelper output)
{
    private const string VfrArrivalScenario = """
        {
          "id": "01TEST00000000000000000001",
          "name": "VfrArrivalGeneratorE2E",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [],
          "vfrArrivalGenerators": [
            {
              "id": "vfr-south",
              "bearingFrom": 120,
              "bearingTo": 200,
              "initialDistance": 12,
              "maxDistance": 20,
              "altitudeMin": 2500,
              "altitudeMax": 3500,
              "intervalTime": 60
            }
          ]
        }
        """;

    private const string OverflightScenario = """
        {
          "id": "01TEST00000000000000000002",
          "name": "OverflightGeneratorE2E",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [],
          "overflightGenerators": [
            {
              "id": "of-eastwest",
              "fromBearingFrom": 80,
              "fromBearingTo": 100,
              "toBearingFrom": 260,
              "toBearingTo": 280,
              "initialDistance": 15,
              "maxDistance": 22,
              "altitudeMin": 4500,
              "altitudeMax": 7500,
              "exitDistance": 30,
              "intervalTime": 60
            }
          ]
        }
        """;

    private SimulationEngine? BuildEngine(string scenarioJson)
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
        foreach (var w in engine.LoadScenario(scenarioJson, rngSeed: 42))
        {
            output.WriteLine($"[load-warn] {w}");
        }
        return engine;
    }

    private static (double Lat, double Lon) Airport() => Yaat.Sim.Data.NavigationDatabase.Instance.GetFixPosition("OAK")!.Value;

    [Fact]
    public void VfrArrivalGenerator_SpawnsInsideItsConfiguredRanges()
    {
        var engine = BuildEngine(VfrArrivalScenario);
        if (engine?.Scenario is null)
        {
            return;
        }

        Assert.Single(engine.Scenario.VfrArrivalGenerators);

        // Sample each aircraft on the tick it appears: a VFR arrival flies straight at the field, so by the
        // end of the run its bearing and distance no longer reflect where the generator placed it.
        var spawns = CollectSpawns(engine, seconds: 600);
        Assert.NotEmpty(spawns);
        output.WriteLine($"spawned {spawns.Count} VFR arrivals");

        var (airportLat, airportLon) = Airport();
        foreach (var spawn in spawns)
        {
            var ac = spawn.Aircraft;
            var distanceNm = GeoMath.DistanceNm(spawn.Position.Lat, spawn.Position.Lon, airportLat, airportLon);
            var bearingTrue = GeoMath.BearingTo(airportLat, airportLon, spawn.Position.Lat, spawn.Position.Lon);
            var bearingMagnetic = MagneticDeclination.TrueToMagnetic(bearingTrue, airportLat, airportLon);

            output.WriteLine(
                $"{ac.Callsign} {ac.AircraftType} {distanceNm:F1}nm brg {bearingMagnetic:F0} at {spawn.Altitude:F0}ft code {ac.Transponder.Code} route={spawn.RouteFixes}"
            );

            // Sampled one tick after the spawn, so the aircraft has already crept a little way inbound.
            Assert.InRange(distanceNm, 11.5, 20.1);
            Assert.InRange(spawn.Altitude, 2500, 3500);
            Assert.InRange(bearingMagnetic, 119, 201);

            // A VFR arrival is receiving service: filed VFR plan to the field, discrete code from the VFR bank.
            Assert.True(ac.FlightPlan.IsVfr);
            Assert.True(ac.FlightPlan.HasFlightPlan);
            Assert.Equal("OAK", ac.FlightPlan.Destination);
            Assert.NotEqual(0u, ac.Transponder.AssignedCode);
            Assert.NotEqual(1200u, ac.Transponder.Code);
            Assert.Equal("C", ac.Transponder.Mode);

            // Proceeding direct the field. Sampled at spawn: an arrival that has since reached OAK has
            // already drained its route.
            Assert.Equal("OAK", spawn.RouteFixes);
        }
    }

    /// <summary>
    /// Ticks the engine, capturing each aircraft's position, altitude and route on the tick it first appears.
    /// The <see cref="AircraftState"/> reference stays live, so anything that changes in flight — position,
    /// altitude, route — must be read from the captured copy, not from the aircraft.
    /// </summary>
    private static List<(AircraftState Aircraft, LatLon Position, double Altitude, string RouteFixes)> CollectSpawns(
        SimulationEngine engine,
        int seconds
    )
    {
        var spawns = new List<(AircraftState, LatLon, double, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var t = 0; t < seconds; t++)
        {
            engine.TickOneSecond();
            foreach (var ac in engine.World.GetSnapshot())
            {
                if (seen.Add(ac.Callsign))
                {
                    spawns.Add((ac, ac.Position, ac.Altitude, string.Join(" ", ac.Targets.NavigationRoute.Select(n => n.Name))));
                }
            }
        }

        return spawns;
    }

    [Fact]
    public void VfrArrivalGenerator_NeverSpawnsInsideClassBOrC()
    {
        var engine = BuildEngine(VfrArrivalScenario);
        if (engine?.Scenario is null)
        {
            return;
        }

        // Guard against a vacuous pass: if the Class B/C set failed to load, every point is "clear" and this
        // test proves nothing. OAK sits under the SFO Bravo, so something must contain a point overhead.
        var (oakLat, oakLon) = Airport();
        Assert.NotEmpty(AirspaceDatabase.Default.FindContaining(new LatLon(oakLat, oakLon), altitudeFtMsl: 5000));

        // A generous band that would otherwise place aircraft inside the SFO Bravo / OAK Charlie.
        engine.Scenario.VfrArrivalGenerators[0].Config.AltitudeMin = 1500;
        engine.Scenario.VfrArrivalGenerators[0].Config.AltitudeMax = 6000;
        engine.Scenario.VfrArrivalGenerators[0].Config.InitialDistance = 3;
        engine.Scenario.VfrArrivalGenerators[0].Config.MaxDistance = 25;
        engine.Scenario.VfrArrivalGenerators[0].Config.BearingFrom = 0;
        engine.Scenario.VfrArrivalGenerators[0].Config.BearingTo = 360;

        var spawnAltitudes = new List<(LatLon Position, double Altitude, string Callsign)>();
        for (var t = 0; t < 600; t++)
        {
            var before = engine.World.GetSnapshot().Select(a => a.Callsign).ToHashSet(StringComparer.Ordinal);
            engine.TickOneSecond();
            foreach (var ac in engine.World.GetSnapshot().Where(a => !before.Contains(a.Callsign)))
            {
                spawnAltitudes.Add((ac.Position, ac.Altitude, ac.Callsign));
            }
        }

        Assert.NotEmpty(spawnAltitudes);
        output.WriteLine($"checked {spawnAltitudes.Count} spawn points");

        foreach (var (position, altitude, callsign) in spawnAltitudes)
        {
            var containing = AirspaceDatabase.Default.FindContaining(position, altitude).ToList();
            Assert.True(
                containing.Count == 0,
                $"{callsign} spawned inside {string.Join(", ", containing.Select(v => $"{v.Class} {v.Name}"))} at {altitude:F0}ft"
            );
        }
    }

    [Fact]
    public void VfrArrivalGenerator_DescendingSpawn_ActuallyDescends()
    {
        var engine = BuildEngine(VfrArrivalScenario);
        if (engine?.Scenario is null)
        {
            return;
        }

        engine.Scenario.VfrArrivalGenerators[0].Config.InitialVsFpm = -500;

        AircraftState? arrival = null;
        for (var t = 0; t < 120 && arrival is null; t++)
        {
            engine.TickOneSecond();
            arrival = engine.World.GetSnapshot().FirstOrDefault();
        }

        Assert.NotNull(arrival);

        // Physics zeroes vertical speed with no target altitude, so the descent target must have been set.
        Assert.NotNull(arrival.Targets.TargetAltitude);
        var fieldElevation = Yaat.Sim.Data.NavigationDatabase.Instance.GetAirportElevation("OAK") ?? 0;
        Assert.Equal(Math.Round((fieldElevation + 1000) / 100.0) * 100.0, arrival.Targets.TargetAltitude!.Value);

        for (var t = 0; t < 10; t++)
        {
            engine.TickOneSecond();
        }

        output.WriteLine($"{arrival.Callsign} vs={arrival.VerticalSpeed:F0} alt={arrival.Altitude:F0} target={arrival.Targets.TargetAltitude}");
        Assert.True(arrival.VerticalSpeed < 0, $"expected a descent, got {arrival.VerticalSpeed:F0} fpm");
    }

    [Fact]
    public void VfrArrivalGenerator_LevelSpawn_HoldsAltitudeForTheController()
    {
        var engine = BuildEngine(VfrArrivalScenario);
        if (engine?.Scenario is null)
        {
            return;
        }

        AircraftState? arrival = null;
        for (var t = 0; t < 120 && arrival is null; t++)
        {
            engine.TickOneSecond();
            arrival = engine.World.GetSnapshot().FirstOrDefault();
        }

        Assert.NotNull(arrival);
        var spawnAltitude = arrival.Altitude;
        Assert.Null(arrival.Targets.TargetAltitude);

        for (var t = 0; t < 30; t++)
        {
            engine.TickOneSecond();
        }

        Assert.Equal(0, arrival.VerticalSpeed);
        Assert.Equal(spawnAltitude, arrival.Altitude, precision: 3);
    }

    [Fact]
    public void OverflightGenerator_SpawnsTransitsWithHemisphericAltitudeAndExitRoute()
    {
        var engine = BuildEngine(OverflightScenario);
        if (engine?.Scenario is null)
        {
            return;
        }

        Assert.Single(engine.Scenario.OverflightGenerators);

        for (var t = 0; t < 600; t++)
        {
            engine.TickOneSecond();
        }

        var spawned = engine.World.GetSnapshot();
        Assert.NotEmpty(spawned);
        output.WriteLine($"spawned {spawned.Count} overflights");

        var (airportLat, airportLon) = Airport();
        foreach (var ac in spawned)
        {
            Assert.True(ac.IsGeneratedOverflight);
            Assert.Equal(30, ac.OverflightExitDistanceNm);

            // A transient not receiving service: cold call on the VFR conspicuity code.
            Assert.True(ac.FlightPlan.IsVfr);
            Assert.False(ac.FlightPlan.HasFlightPlan);
            Assert.Equal(0u, ac.Transponder.AssignedCode);
            Assert.Equal(1200u, ac.Transponder.Code);

            // Routed to an exit point on the "to" arc, at the exit distance.
            var exit = Assert.Single(ac.Targets.NavigationRoute);
            var exitDistance = GeoMath.DistanceNm(exit.Position.Lat, exit.Position.Lon, airportLat, airportLon);
            Assert.Equal(30, exitDistance, precision: 0);

            var exitBearingTrue = GeoMath.BearingTo(airportLat, airportLon, exit.Position.Lat, exit.Position.Lon);
            var exitBearingMagnetic = MagneticDeclination.TrueToMagnetic(exitBearingTrue, airportLat, airportLon);
            Assert.InRange(exitBearingMagnetic, 259, 281);

            // 91.159(a): a level transit above 3000 AGL flies a hemispheric cruising altitude, keyed on its
            // actual course over the ground toward the exit point.
            var courseTrue = GeoMath.BearingTo(ac.Position, exit.Position);
            var courseMagnetic = MagneticDeclination.TrueToMagnetic(courseTrue, ac.Position);
            output.WriteLine($"{ac.Callsign} alt={ac.Altitude:F0} course={courseMagnetic:F0} exitBrg={exitBearingMagnetic:F0}");
            Assert.True(
                HemisphericAltitude.IsConforming(courseMagnetic, ac.Altitude),
                $"{ac.Callsign} at {ac.Altitude:F0}ft is not a VFR cruising altitude for a {courseMagnetic:F0} magnetic course"
            );
        }
    }

    [Fact]
    public void OverflightGenerator_SnapDisabled_KeepsTheRolledAltitude()
    {
        var engine = BuildEngine(OverflightScenario);
        if (engine?.Scenario is null)
        {
            return;
        }

        engine.Scenario.OverflightGenerators[0].Config.SnapHemisphericAltitude = false;
        engine.Scenario.OverflightGenerators[0].Config.AltitudeMin = 5000;
        engine.Scenario.OverflightGenerators[0].Config.AltitudeMax = 5000;

        for (var t = 0; t < 120; t++)
        {
            engine.TickOneSecond();
        }

        var spawned = engine.World.GetSnapshot();
        Assert.NotEmpty(spawned);
        Assert.All(spawned, ac => Assert.Equal(5000, ac.Altitude, precision: 3));
    }

    /// <summary>A scenario carrying neither new array must load and behave exactly as it does today.</summary>
    [Fact]
    public void ScenarioWithoutTheNewArrays_LoadsWithNoVfrOrOverflightGenerators()
    {
        const string plainScenario = """
            {
              "id": "01TEST00000000000000000003",
              "name": "NoNewArrays",
              "artccId": "ZOA",
              "primaryAirportId": "OAK",
              "aircraft": [],
              "aircraftGenerators": []
            }
            """;

        var engine = BuildEngine(plainScenario);
        if (engine?.Scenario is null)
        {
            return;
        }

        Assert.Empty(engine.Scenario.Generators);
        Assert.Empty(engine.Scenario.VfrArrivalGenerators);
        Assert.Empty(engine.Scenario.OverflightGenerators);

        for (var t = 0; t < 120; t++)
        {
            engine.TickOneSecond();
        }

        Assert.Empty(engine.World.GetSnapshot());
    }
}
