using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Yaat.Sim.Tests;

public class MagneticDeclinationTests(ITestOutputHelper output)
{
    [Fact]
    public void Diagnostic_MicroBenchmark()
    {
        // Warm up — JIT + first-call epoch resolve + any static init.
        for (int i = 0; i < 100; i++)
        {
            MagneticDeclination.GetDeclination(37.6, -122.4);
        }

        const int n = 35000;
        var sw = Stopwatch.StartNew();
        double acc = 0;
        for (int i = 0; i < n; i++)
        {
            // Vary position slightly so the Coordinate allocation and evaluation can't be trivially CSE'd.
            acc += MagneticDeclination.GetDeclination(37.6 + (i * 1e-7), -122.4 - (i * 1e-7));
        }
        sw.Stop();
        output.WriteLine(
            $"{n} GetDeclination calls: {sw.Elapsed.TotalMilliseconds:F0}ms total, avg={sw.Elapsed.TotalMilliseconds / n:F4}ms/call (accumulator={acc:F2})"
        );
    }

    [Fact]
    public void GetDeclination_WestCoast_PositiveEast()
    {
        // San Francisco area: WMM 2025 ≈ +13° east declination.
        double decl = MagneticDeclination.GetDeclination(37.6, -122.4);
        Assert.InRange(decl, 11.0, 15.0);
    }

    [Fact]
    public void GetDeclination_EastCoast_NegativeWest()
    {
        // New York area: WMM 2025 ≈ -13° west declination.
        double decl = MagneticDeclination.GetDeclination(40.7, -74.0);
        Assert.InRange(decl, -15.0, -11.0);
    }

    [Fact]
    public void GetDeclination_AgonicBand_SmallMagnitude()
    {
        // Near the WMM 2025 agonic line (through central US, ~lon -94W at 39N): expect |decl| < 4°.
        double decl = MagneticDeclination.GetDeclination(39.0, -94.0);
        Assert.InRange(decl, -4.0, 4.0);
    }

    [Fact]
    public void GetDeclination_Honolulu_NotLinearModel()
    {
        // PHNL (21.3, -157.9): WMM ≈ +10° east. The old linear model gave ~+30.7° — catastrophically
        // wrong for Pacific locations. Bound tight enough that a regression to the linear fit fails.
        double decl = MagneticDeclination.GetDeclination(21.3, -157.9);
        Assert.InRange(decl, 8.0, 12.0);
    }

    [Fact]
    public void GetDeclination_Anchorage_NotLinearModel()
    {
        // PANC (61.2, -149.9): WMM ≈ +15° east. The old linear model gave ~+26.6° — far too large.
        double decl = MagneticDeclination.GetDeclination(61.2, -149.9);
        Assert.InRange(decl, 13.0, 18.0);
    }

    [Fact]
    public void GetDeclination_LatitudeTerm_ChangesResult()
    {
        // The old linear model ignored latitude entirely. A real WMM query at the same longitude
        // but different latitudes must produce meaningfully different values.
        double south = MagneticDeclination.GetDeclination(25.0, -100.0);
        double north = MagneticDeclination.GetDeclination(55.0, -100.0);
        Assert.True(Math.Abs(south - north) > 0.5, $"Expected latitude-dependent variation; got south={south:F2} north={north:F2}");
    }

    [Fact]
    public void TrueToMagnetic_WestCoast()
    {
        // True 270° on West Coast with ~+13° east declination → magnetic ~257°.
        double mag = MagneticDeclination.TrueToMagnetic(270.0, 37.6, -122.4);
        Assert.InRange(mag, 254.0, 260.0);
    }

    [Fact]
    public void TrueToMagnetic_WrapsCorrectly()
    {
        // True 5° with positive east declination → should wrap past 360.
        double mag = MagneticDeclination.TrueToMagnetic(5.0, 37.6, -122.4);
        Assert.InRange(mag, 348.0, 358.0);
    }

    [Fact]
    public void TrueToMagnetic_EastCoast()
    {
        // True 270° on East Coast with ~-13° west declination → magnetic ~283°.
        double mag = MagneticDeclination.TrueToMagnetic(270.0, 40.7, -74.0);
        Assert.InRange(mag, 280.0, 286.0);
    }

    [Fact]
    public void MagneticToTrue_RoundTrips()
    {
        // Any magnetic heading converted to true and back must return the original to float precision.
        const double lat = 37.6;
        const double lon = -122.4;
        foreach (double heading in new[] { 0.0, 90.0, 180.0, 270.0, 359.9 })
        {
            double trueHdg = MagneticDeclination.MagneticToTrue(heading, lat, lon);
            double roundTrip = MagneticDeclination.TrueToMagnetic(trueHdg, lat, lon);
            Assert.Equal(heading, roundTrip, precision: 6);
        }
    }
}
