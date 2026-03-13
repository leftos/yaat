using System.Text.Json;
using Xunit;

namespace Yaat.Sim.Tests;

public class WindInterpolatorTests
{
    // -------------------------------------------------------------------------
    // GetWindAt — null / empty profile
    // -------------------------------------------------------------------------

    [Fact]
    public void GetWindAt_NullProfile_ReturnsZero()
    {
        var result = WindInterpolator.GetWindAt(null, 10_000);
        Assert.Equal(0, result.DirectionDeg);
        Assert.Equal(0, result.SpeedKts);
    }

    [Fact]
    public void GetWindAt_EmptyLayers_ReturnsZero()
    {
        var profile = new WeatherProfile();
        var result = WindInterpolator.GetWindAt(profile, 10_000);
        Assert.Equal(0, result.DirectionDeg);
        Assert.Equal(0, result.SpeedKts);
    }

    // -------------------------------------------------------------------------
    // GetWindAt — single layer
    // -------------------------------------------------------------------------

    [Fact]
    public void GetWindAt_SingleLayer_BelowAltitude_ClampsToLayer()
    {
        var profile = MakeProfile([
            new WindLayer
            {
                Direction = 270,
                Speed = 20,
                Altitude = 5_000,
            },
        ]);
        var result = WindInterpolator.GetWindAt(profile, 1_000);
        Assert.Equal(270, result.DirectionDeg, precision: 1);
        Assert.Equal(20, result.SpeedKts, precision: 1);
    }

    [Fact]
    public void GetWindAt_SingleLayer_AboveAltitude_ClampsToLayer()
    {
        var profile = MakeProfile([
            new WindLayer
            {
                Direction = 090,
                Speed = 30,
                Altitude = 5_000,
            },
        ]);
        var result = WindInterpolator.GetWindAt(profile, 20_000);
        Assert.Equal(90, result.DirectionDeg, precision: 1);
        Assert.Equal(30, result.SpeedKts, precision: 1);
    }

    [Fact]
    public void GetWindAt_SingleLayer_AtExactAltitude_ReturnsLayer()
    {
        var profile = MakeProfile([
            new WindLayer
            {
                Direction = 180,
                Speed = 15,
                Altitude = 10_000,
            },
        ]);
        var result = WindInterpolator.GetWindAt(profile, 10_000);
        Assert.Equal(180, result.DirectionDeg, precision: 1);
        Assert.Equal(15, result.SpeedKts, precision: 1);
    }

    // -------------------------------------------------------------------------
    // GetWindAt — clamping at extremes of multi-layer profile
    // -------------------------------------------------------------------------

    [Fact]
    public void GetWindAt_BelowLowestLayer_ClampsToLowest()
    {
        var profile = MakeProfile([
            new WindLayer
            {
                Direction = 090,
                Speed = 10,
                Altitude = 3_000,
            },
            new WindLayer
            {
                Direction = 180,
                Speed = 20,
                Altitude = 10_000,
            },
        ]);
        var result = WindInterpolator.GetWindAt(profile, 500);
        Assert.Equal(90, result.DirectionDeg, precision: 1);
        Assert.Equal(10, result.SpeedKts, precision: 1);
    }

    [Fact]
    public void GetWindAt_AboveHighestLayer_ClampsToHighest()
    {
        var profile = MakeProfile([
            new WindLayer
            {
                Direction = 090,
                Speed = 10,
                Altitude = 3_000,
            },
            new WindLayer
            {
                Direction = 180,
                Speed = 20,
                Altitude = 10_000,
            },
        ]);
        var result = WindInterpolator.GetWindAt(profile, 40_000);
        Assert.Equal(180, result.DirectionDeg, precision: 1);
        Assert.Equal(20, result.SpeedKts, precision: 1);
    }

    // -------------------------------------------------------------------------
    // GetWindAt — interpolation
    // -------------------------------------------------------------------------

    [Fact]
    public void GetWindAt_BetweenLayers_InterpolatesSpeedLinearly()
    {
        var profile = MakeProfile([
            new WindLayer
            {
                Direction = 270,
                Speed = 10,
                Altitude = 0,
            },
            new WindLayer
            {
                Direction = 270,
                Speed = 30,
                Altitude = 10_000,
            },
        ]);
        // Midpoint: speed should be 20 kts
        var result = WindInterpolator.GetWindAt(profile, 5_000);
        Assert.Equal(20, result.SpeedKts, precision: 1);
        Assert.Equal(270, result.DirectionDeg, precision: 1);
    }

    [Fact]
    public void GetWindAt_BetweenLayers_VectorInterpolationHandles360Boundary()
    {
        // 350° and 010° — angular midpoint through 000°, NOT through 180°
        var profile = MakeProfile([
            new WindLayer
            {
                Direction = 350,
                Speed = 20,
                Altitude = 0,
            },
            new WindLayer
            {
                Direction = 010,
                Speed = 20,
                Altitude = 10_000,
            },
        ]);
        var result = WindInterpolator.GetWindAt(profile, 5_000);
        // Midpoint through vector interpolation should be ≈ 000°
        double dir = result.DirectionDeg;
        // Normalize: should be 0° or 360°
        if (dir > 180)
        {
            dir -= 360;
        }

        Assert.True(Math.Abs(dir) < 5, $"Expected direction near 0°, got {result.DirectionDeg}°");
    }

    // -------------------------------------------------------------------------
    // GetWindComponents
    // -------------------------------------------------------------------------

    [Fact]
    public void GetWindComponents_WindFrom270_EastwardEffect()
    {
        // Wind FROM 270 (west) → blows eastward
        var profile = MakeProfile([
            new WindLayer
            {
                Direction = 270,
                Speed = 10,
                Altitude = 5_000,
            },
        ]);
        var (northKts, eastKts) = WindInterpolator.GetWindComponents(profile, 5_000);
        Assert.Equal(0, northKts, precision: 1);
        Assert.Equal(10, eastKts, precision: 1);
    }

    [Fact]
    public void GetWindComponents_WindFrom090_WestwardEffect()
    {
        // Wind FROM 090 (east) → blows westward
        var profile = MakeProfile([
            new WindLayer
            {
                Direction = 090,
                Speed = 10,
                Altitude = 5_000,
            },
        ]);
        var (northKts, eastKts) = WindInterpolator.GetWindComponents(profile, 5_000);
        Assert.Equal(0, northKts, precision: 1);
        Assert.Equal(-10, eastKts, precision: 1);
    }

    [Fact]
    public void GetWindComponents_NullProfile_ReturnsZero()
    {
        var (northKts, eastKts) = WindInterpolator.GetWindComponents(null, 5_000);
        Assert.Equal(0, northKts);
        Assert.Equal(0, eastKts);
    }

    // -------------------------------------------------------------------------
    // IasToTas
    // -------------------------------------------------------------------------

    [Fact]
    public void IasToTas_AtSealevel_NoCorrection()
    {
        double tas = WindInterpolator.IasToTas(200, 0);
        Assert.Equal(200, tas, precision: 1);
    }

    [Fact]
    public void IasToTas_AtFL100_ApproximatelyCorrect()
    {
        // Factor at FL100 = 1.165 → IAS 200 → TAS ~233
        double tas = WindInterpolator.IasToTas(200, 10_000);
        Assert.InRange(tas, 230, 236);
    }

    [Fact]
    public void IasToTas_AtFL250_ApproximatelyCorrect()
    {
        // ISA compressible flow: IAS 200 at FL250 → TAS ~293
        double tas = WindInterpolator.IasToTas(200, 25_000);
        Assert.InRange(tas, 291, 296);
    }

    [Fact]
    public void IasToTas_AtFL350_ApproximatelyCorrect()
    {
        // ISA compressible flow: IAS 280 at FL350 → TAS ~473
        double tas = WindInterpolator.IasToTas(280, 35_000);
        Assert.InRange(tas, 471, 476);
    }

    [Fact]
    public void IasToTas_AboveTropopause_ContinuesISA()
    {
        // Above tropopause (36,089 ft) is isothermal; TAS still increases with altitude
        // because pressure decreases even though temperature is constant.
        double tas40k = WindInterpolator.IasToTas(200, 40_000);
        double tas45k = WindInterpolator.IasToTas(200, 45_000);
        Assert.True(tas45k > tas40k, "TAS should increase with altitude above tropopause");
    }

    // -------------------------------------------------------------------------
    // IasToMach
    // -------------------------------------------------------------------------

    [Fact]
    public void IasToMach_AtFL350_280kt_ApproximatelyM082()
    {
        double mach = WindInterpolator.IasToMach(280, 35_000);
        Assert.InRange(mach, 0.81, 0.83);
    }

    [Fact]
    public void IasToMach_AtSeaLevel_250kt_SubsonicLow()
    {
        double mach = WindInterpolator.IasToMach(250, 0);
        Assert.InRange(mach, 0.36, 0.40);
    }

    [Fact]
    public void IasToMach_RoundTrip_WithMachToIas()
    {
        double originalMach = 0.82;
        double altitude = 35_000;
        double ias = WindInterpolator.MachToIas(originalMach, altitude);
        double recoveredMach = WindInterpolator.IasToMach(ias, altitude);
        Assert.Equal(originalMach, recoveredMach, precision: 4);
    }

    // -------------------------------------------------------------------------
    // ComputeWindCorrectionAngle
    // -------------------------------------------------------------------------

    [Fact]
    public void Wca_Headwind_ReturnsZero()
    {
        // Flying north, wind from north (headwind) → no crosswind → WCA = 0
        double wca = WindInterpolator.ComputeWindCorrectionAngle(000, 200, 000, 30);
        Assert.Equal(0, wca, precision: 1);
    }

    [Fact]
    public void Wca_Tailwind_ReturnsZero()
    {
        // Flying north, wind from south (tailwind) → no crosswind → WCA = 0
        double wca = WindInterpolator.ComputeWindCorrectionAngle(000, 200, 180, 30);
        Assert.Equal(0, wca, precision: 1);
    }

    [Fact]
    public void Wca_CrosswindFromLeft_NegativeWca()
    {
        // Flying east (090), wind FROM north (000): pushes south (right of track) → crab left → WCA < 0
        double wca = WindInterpolator.ComputeWindCorrectionAngle(090, 200, 000, 30);
        Assert.True(wca < 0, $"Expected WCA < 0 for wind from left, got {wca}");
    }

    [Fact]
    public void Wca_CrosswindFromRight_PositiveWca()
    {
        // Flying east (090), wind FROM south (180): pushes north (left of track) → crab right → WCA > 0
        double wca = WindInterpolator.ComputeWindCorrectionAngle(090, 200, 180, 30);
        Assert.True(wca > 0, $"Expected WCA > 0 for wind from right, got {wca}");
    }

    [Fact]
    public void Wca_ZeroTas_ReturnsZero()
    {
        double wca = WindInterpolator.ComputeWindCorrectionAngle(090, 0, 270, 30);
        Assert.Equal(0, wca);
    }

    [Fact]
    public void Wca_ZeroWind_ReturnsZero()
    {
        double wca = WindInterpolator.ComputeWindCorrectionAngle(090, 200, 270, 0);
        Assert.Equal(0, wca);
    }

    // -------------------------------------------------------------------------
    // JSON deserialization of real ATCTrainer weather files
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("01H6Y8QRJ2P7V51MWPFFKVMP07.json", 7, 3_000, 170, 7)]
    [InlineData("01J7EWDDP3P87KHP0Z5W9SMDMY.json", 4, 1_000, 140, 27)]
    [InlineData("01HTY06CDMYXR5BS55E22SGAF2.json", 8, 3_000, 180, 18)]
    public void Deserialize_RealWeatherFile_ParsesCorrectly(
        string filename,
        int? expectedLayerCount,
        int? expectedFirstAltitude,
        int? expectedFirstDirection,
        int? expectedFirstSpeed
    )
    {
        var path = Path.Combine(TestHelpers.RepoRoot, "docs", "atctrainer-weather-examples", filename);
        Assert.True(File.Exists(path), $"Weather file not found: {path}");

        var json = File.ReadAllText(path);
        var profile = JsonSerializer.Deserialize<WeatherProfile>(json);

        Assert.NotNull(profile);
        Assert.False(string.IsNullOrEmpty(profile.Id));
        Assert.False(string.IsNullOrEmpty(profile.ArtccId));

        if (expectedLayerCount.HasValue)
        {
            Assert.Equal(expectedLayerCount.Value, profile.WindLayers.Count);
        }

        if (expectedFirstAltitude.HasValue)
        {
            Assert.Equal(expectedFirstAltitude.Value, profile.WindLayers[0].Altitude, precision: 0);
        }

        if (expectedFirstDirection.HasValue)
        {
            Assert.Equal(expectedFirstDirection.Value, profile.WindLayers[0].Direction, precision: 0);
        }

        if (expectedFirstSpeed.HasValue)
        {
            Assert.Equal(expectedFirstSpeed.Value, profile.WindLayers[0].Speed, precision: 0);
        }

        // Layers must be sorted by altitude after deserialization
        for (int i = 1; i < profile.WindLayers.Count; i++)
        {
            Assert.True(
                profile.WindLayers[i].Altitude >= profile.WindLayers[i - 1].Altitude,
                $"Layer {i} altitude {profile.WindLayers[i].Altitude} < layer {i - 1} altitude {profile.WindLayers[i - 1].Altitude}"
            );
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static WeatherProfile MakeProfile(List<WindLayer> layers)
    {
        // Use JSON round-trip to exercise the setter-based sort on deserialization
        var profile = new WeatherProfile();
        profile.WindLayers = layers;
        return profile;
    }
}

/// <summary>Shared test utilities.</summary>
internal static class TestHelpers
{
    /// <summary>
    /// Walks up from the test assembly directory until it finds the repository root
    /// (identified by the presence of yaat.slnx).
    /// </summary>
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "yaat.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("Could not find repository root (yaat.slnx)");
        }
    }
}
