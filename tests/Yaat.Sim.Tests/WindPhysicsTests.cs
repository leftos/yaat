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
        FlightPhysics.Update(ac, 1.0, null, null);
        Assert.Equal(ac.IndicatedAirspeed, ac.GroundSpeed, Tolerance);
    }

    [Fact]
    public void ZeroWind_AtAltitude_GsEqualsExpectedTas()
    {
        // At FL100, TAS factor ≈ 1.165. GS = IAS * 1.165 with no wind.
        var ac = MakeAircraft(200, 090, 10_000);
        FlightPhysics.Update(ac, 1.0, null, null);
        double expectedTas = WindInterpolator.IasToTas(200, 10_000);
        Assert.Equal(expectedTas, ac.GroundSpeed, 1.0);
    }

    [Fact]
    public void ZeroWind_EmptyLayers_TrackEqualsHeading()
    {
        var ac = MakeAircraft(200, 090, 10_000);
        var weather = new WeatherProfile(); // no wind layers
        FlightPhysics.Update(ac, 1.0, null, weather);
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
        FlightPhysics.Update(ac, 1.0, null, weather);

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
        FlightPhysics.Update(ac, 1.0, null, weather);

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
        FlightPhysics.Update(ac, 1.0, null, weather);

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
        FlightPhysics.Update(ac, 1.0, null, weather);

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
        FlightPhysics.Update(ac, 1.0, null, weather);

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
        FlightPhysics.Update(ac, 1.0, null, null);

        Assert.True(ac.GroundSpeed > 450, $"Expected GS > 450 at FL350 IAS 280, got {ac.GroundSpeed}");
    }

    // -------------------------------------------------------------------------
    // Backward compat: null weather = existing behavior preserved
    // -------------------------------------------------------------------------

    [Fact]
    public void NullWeather_BackwardCompat_TrackEqualsHeading()
    {
        var ac = MakeAircraft(200, 135, 10_000);
        FlightPhysics.Update(ac, 1.0, null, null);
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
                Latitude = ac.Latitude,
                Longitude = ac.Longitude + 2, // due east
            }
        );

        var weather = MakeWind(fromDeg: 180, speedKts: 40, altitude: 5_000); // wind from south, pushes north

        // Run a few ticks so navigation and WCA are applied
        for (int i = 0; i < 5; i++)
        {
            FlightPhysics.Update(ac, 1.0, null, weather);
        }

        // Track should be near 090 (east) because WCA corrects the heading
        double trackDiff = Math.Abs(ac.TrueTrack.Degrees - 090);
        if (trackDiff > 180)
        {
            trackDiff = 360 - trackDiff;
        }

        Assert.True(trackDiff < 5, $"Expected track near 090°, got {ac.TrueTrack.Degrees}°");
    }
}
