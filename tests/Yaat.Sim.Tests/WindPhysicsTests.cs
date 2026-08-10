using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>Tests for wind physics integration in FlightPhysics.Update.</summary>
public class WindPhysicsTests
{
    private const double Tolerance = 0.5;

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static AircraftState MakeAircraft(double speed = 200, double heading = 090, double altitude = 10_000, bool onGround = false)
    {
        return new AircraftState
        {
            Callsign = "TEST01",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = onGround ? 0 : speed,
            IsOnGround = onGround,
        };
    }

    private static WeatherProfile MakeWind(double fromDeg, double speedKts, double altitude = 5_000)
    {
        return new WeatherProfile
        {
            WindLayers =
            [
                new WindLayer
                {
                    Direction = fromDeg,
                    Speed = speedKts,
                    Altitude = altitude,
                },
            ],
        };
    }

    // -------------------------------------------------------------------------
    // Zero wind: GS == TAS (IAS corrected for altitude), Track == Heading
    // -------------------------------------------------------------------------

    [Fact]
    public void ZeroWind_AtSealevel_GsEqualsIas()
    {
        // At sea level TAS factor = 1.0, so GS should equal IAS with no wind.
        var ac = MakeAircraft(200, 090, 0);
        FlightPhysics.Update(ac, 1.0);
        Assert.Equal(ac.IndicatedAirspeed, ac.GroundSpeed, Tolerance);
    }

    [Fact]
    public void ZeroWind_AtAltitude_GsEqualsExpectedTas()
    {
        // At FL100, TAS factor ≈ 1.165. GS = IAS * 1.165 with no wind.
        var ac = MakeAircraft(200, 090, 10_000);
        FlightPhysics.Update(ac, 1.0);
        double expectedTas = WindInterpolator.IasToTas(200, 10_000);
        Assert.Equal(expectedTas, ac.GroundSpeed, 1.0);
    }

    [Fact]
    public void ZeroWind_EmptyLayers_TrackEqualsHeading()
    {
        var ac = MakeAircraft(200, 090, 10_000);
        var weather = new WeatherProfile(); // no wind layers
        FlightPhysics.Update(ac, 1.0, null, weather, simTimeSeconds: 0);
        Assert.Equal(ac.TrueHeading.Degrees, ac.TrueTrack.Degrees, Tolerance);
    }

    // -------------------------------------------------------------------------
    // Headwind: GS < IAS, Track == Heading
    // -------------------------------------------------------------------------

    [Fact]
    public void Headwind_GroundSpeedLessThanIas()
    {
        // Flying east (090), headwind FROM east (090) at 30 kts
        var ac = MakeAircraft(200, 090, 5_000);
        var weather = MakeWind(fromDeg: 090, speedKts: 30, altitude: 5_000);
        FlightPhysics.Update(ac, 1.0, null, weather, simTimeSeconds: 0);

        // GS should be less than IAS (headwind reduces GS)
        Assert.True(ac.GroundSpeed < 200 - 10, $"Expected GS significantly < 200, got {ac.GroundSpeed}");
        // Track and Heading should be equal (headwind/tailwind → no drift)
        Assert.Equal(090, ac.TrueTrack.Degrees, 2.0);
    }

    // -------------------------------------------------------------------------
    // Tailwind: GS > IAS, Track == Heading
    // -------------------------------------------------------------------------

    [Fact]
    public void Tailwind_GroundSpeedGreaterThanIas()
    {
        // Flying east (090), tailwind FROM west (270) at 30 kts
        var ac = MakeAircraft(200, 090, 5_000);
        var weather = MakeWind(fromDeg: 270, speedKts: 30, altitude: 5_000);
        FlightPhysics.Update(ac, 1.0, null, weather, simTimeSeconds: 0);

        // GS should be greater than IAS (tailwind adds to GS)
        Assert.True(ac.GroundSpeed > 210, $"Expected GS > 210, got {ac.GroundSpeed}");
        // Track should still match heading (pure tailwind, no crosswind)
        Assert.Equal(090, ac.TrueTrack.Degrees, 2.0);
    }

    // -------------------------------------------------------------------------
    // Crosswind: Track != Heading, GS reasonable
    // -------------------------------------------------------------------------

    [Fact]
    public void Crosswind_TrackDiffersFromHeading()
    {
        // Flying north (000), crosswind FROM east (090) at 30 kts
        var ac = MakeAircraft(200, 000, 5_000);
        var weather = MakeWind(fromDeg: 090, speedKts: 30, altitude: 5_000);
        FlightPhysics.Update(ac, 1.0, null, weather, simTimeSeconds: 0);

        // Track should differ from heading due to wind drift
        double trackDiff = Math.Abs(ac.TrueTrack.Degrees - 000);
        if (trackDiff > 180)
        {
            trackDiff = 360 - trackDiff;
        }

        Assert.True(trackDiff > 3, $"Expected track to differ from heading, got track={ac.TrueTrack.Degrees}, hdg={ac.TrueHeading.Degrees}");
    }

    [Fact]
    public void Crosswind_GroundSpeedReasonable()
    {
        var ac = MakeAircraft(200, 000, 5_000);
        var weather = MakeWind(fromDeg: 090, speedKts: 30, altitude: 5_000);
        FlightPhysics.Update(ac, 1.0, null, weather, simTimeSeconds: 0);

        // At 5000ft, TAS = 200 * 1.077 ≈ 215.4. GS = sqrt(215.4² + 30²) ≈ 217.5
        double tas = WindInterpolator.IasToTas(200, 5_000);
        double expectedGs = Math.Sqrt(tas * tas + 30 * 30);
        Assert.Equal(expectedGs, ac.GroundSpeed, 2.0);
    }

    // -------------------------------------------------------------------------
    // Ground aircraft: unaffected by wind
    // -------------------------------------------------------------------------

    [Fact]
    public void GroundAircraft_WindHasNoEffect()
    {
        var ac = MakeAircraft(20, 090, 0, onGround: true);
        ac.IndicatedAirspeed = 20;
        ac.IsOnGround = true;

        var weather = MakeWind(fromDeg: 270, speedKts: 50, altitude: 0);
        FlightPhysics.Update(ac, 1.0, null, weather, simTimeSeconds: 0);

        // On ground: GS and IAS stay equal; Track follows Heading
        Assert.Equal(ac.GroundSpeed, ac.IndicatedAirspeed, Tolerance);
        Assert.Equal(ac.TrueHeading.Degrees, ac.TrueTrack.Degrees, Tolerance);
    }

    // -------------------------------------------------------------------------
    // TAS at altitude: GS >> IAS even with no wind
    // -------------------------------------------------------------------------

    [Fact]
    public void TasCorrection_AtFL350_GroundSpeedHigherThanIas()
    {
        // At FL350 with no wind, GS ≈ TAS ≈ 473 kts for IAS 280 (ISA compressible flow)
        var ac = MakeAircraft(280, 090, 35_000);
        FlightPhysics.Update(ac, 1.0);

        Assert.True(ac.GroundSpeed > 450, $"Expected GS > 450 at FL350 IAS 280, got {ac.GroundSpeed}");
    }

    // -------------------------------------------------------------------------
    // Backward compat: null weather = existing behavior preserved
    // -------------------------------------------------------------------------

    [Fact]
    public void NullWeather_BackwardCompat_TrackEqualsHeading()
    {
        var ac = MakeAircraft(200, 135, 10_000);
        FlightPhysics.Update(ac, 1.0);
        Assert.Equal(ac.TrueHeading.Degrees, ac.TrueTrack.Degrees, Tolerance);
    }

    // -------------------------------------------------------------------------
    // WCA in navigation: Track matches bearing to fix
    // -------------------------------------------------------------------------

    [Fact]
    public void WcaNavigation_TrackApproachesBearingToFix()
    {
        // Aircraft heading east, strong crosswind from south
        // With WCA, aircraft heading should crab slightly right, but track should be east
        var ac = MakeAircraft(200, 090, 5_000);
        ac.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "FIX",
                Position = new LatLon(ac.Position.Lat, ac.Position.Lon + 2), // due east
            }
        );

        var weather = MakeWind(fromDeg: 180, speedKts: 40, altitude: 5_000); // wind from south, pushes north

        // Run a few ticks so navigation and WCA are applied
        for (int i = 0; i < 5; i++)
        {
            FlightPhysics.Update(ac, 1.0, null, weather, simTimeSeconds: i);
        }

        // Track should be near 090 (east) because WCA corrects the heading
        double trackDiff = Math.Abs(ac.TrueTrack.Degrees - 090);
        if (trackDiff > 180)
        {
            trackDiff = 360 - trackDiff;
        }

        Assert.True(trackDiff < 5, $"Expected track near 090°, got {ac.TrueTrack.Degrees}°");
    }

    // -------------------------------------------------------------------------
    // Variable wind: GS wobbles with authored variability, steady without
    // -------------------------------------------------------------------------

    private static WeatherProfile MakeGustyWind(double fromDeg, double speedKts, double gustKts, double halfSpreadDeg)
    {
        return new WeatherProfile
        {
            WindLayers =
            [
                new WindLayer
                {
                    Direction = fromDeg,
                    Speed = speedKts,
                    Altitude = 0,
                    Gusts = gustKts,
                    DirectionVariabilityDeg = halfSpreadDeg,
                },
            ],
        };
    }

    [Fact]
    public void VariableWind_GroundSpeedWobblesOverTime()
    {
        // 21015G25 180V240 on final at 500 ft: the GS readout should move around.
        var weather = MakeGustyWind(fromDeg: 210, speedKts: 15, gustKts: 25, halfSpreadDeg: 30);
        var speeds = new HashSet<double>();
        for (int t = 0; t < 120; t += 10)
        {
            var ac = MakeAircraft(140, 210, 500);
            FlightPhysics.Update(ac, 1.0, null, weather, simTimeSeconds: t);
            speeds.Add(Math.Round(ac.GroundSpeed, 1));
        }

        Assert.True(speeds.Count > 3, $"Expected GS to wobble across two minutes, saw {speeds.Count} distinct values");
    }

    [Fact]
    public void SteadyWind_NoVariabilityAuthored_GroundSpeedConstantOverTime()
    {
        var weather = MakeWind(fromDeg: 210, speedKts: 15, altitude: 0);
        double? first = null;
        for (int t = 0; t < 120; t += 10)
        {
            var ac = MakeAircraft(140, 210, 500);
            FlightPhysics.Update(ac, 1.0, null, weather, simTimeSeconds: t);
            first ??= ac.GroundSpeed;
            Assert.Equal(first.Value, ac.GroundSpeed);
        }
    }

    [Fact]
    public void VariableWind_TapersOutAtAltitude()
    {
        // Same gusty surface layer: at 10,000 ft AGL the perturbation is fully tapered,
        // so GS is identical at every sample time.
        var weather = MakeGustyWind(fromDeg: 210, speedKts: 15, gustKts: 25, halfSpreadDeg: 30);
        double? first = null;
        for (int t = 0; t < 120; t += 10)
        {
            var ac = MakeAircraft(250, 210, 10_000);
            FlightPhysics.Update(ac, 1.0, null, weather, simTimeSeconds: t);
            first ??= ac.GroundSpeed;
            Assert.Equal(first.Value, ac.GroundSpeed);
        }
    }

    [Fact]
    public void VariableWind_DifferentCallsigns_Decorrelated()
    {
        // Two aircraft in identical states see different instantaneous winds (callsign
        // phase offset) while a steady wind would give them identical GS.
        var weather = MakeGustyWind(fromDeg: 210, speedKts: 15, gustKts: 25, halfSpreadDeg: 30);
        int differing = 0;
        for (int t = 0; t < 300; t += 20)
        {
            var a = MakeAircraft(140, 210, 500);
            var b = MakeAircraft(140, 210, 500);
            b.Callsign = "OTHER99";
            FlightPhysics.Update(a, 1.0, null, weather, simTimeSeconds: t);
            FlightPhysics.Update(b, 1.0, null, weather, simTimeSeconds: t);
            if (Math.Abs(a.GroundSpeed - b.GroundSpeed) > 0.1)
            {
                differing++;
            }
        }

        Assert.True(differing > 7, $"Expected decorrelated wobble between callsigns, differed {differing}/15 samples");
    }

    [Fact]
    public void VariableWind_WorldTick_ReplaysIdentically()
    {
        // Two identical worlds ticked through the same time sequence must land on
        // identical positions — the perturbation is a pure function of sim time.
        static SimulationWorld BuildWorld()
        {
            var world = new SimulationWorld();
            var ac = new AircraftState
            {
                Callsign = "RPL01",
                AircraftType = "B738",
                TrueHeading = new TrueHeading(90),
                TrueTrack = new TrueHeading(90),
                Altitude = 800,
                IndicatedAirspeed = 140,
                IsOnGround = false,
            };
            world.AddAircraft(ac);
            world.Weather = new WeatherProfile
            {
                WindLayers =
                [
                    new WindLayer
                    {
                        Direction = 210,
                        Speed = 15,
                        Altitude = 0,
                        Gusts = 25,
                        DirectionVariabilityDeg = 30,
                    },
                ],
            };
            return world;
        }

        var world1 = BuildWorld();
        var world2 = BuildWorld();
        for (int second = 0; second < 60; second++)
        {
            for (int sub = 0; sub < 4; sub++)
            {
                world1.Tick(0.25, second);
                world2.Tick(0.25, second);
            }
        }

        var a = world1.GetSnapshot()[0];
        var b = world2.GetSnapshot()[0];
        Assert.Equal(a.Position.Lat, b.Position.Lat);
        Assert.Equal(a.Position.Lon, b.Position.Lon);
        Assert.Equal(a.GroundSpeed, b.GroundSpeed);
        Assert.Equal(a.TrueTrack.Degrees, b.TrueTrack.Degrees);
    }
}
