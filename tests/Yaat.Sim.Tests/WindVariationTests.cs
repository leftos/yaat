using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// Pins the deterministic wind-perturbation engine: purity, hard bounds (gust ceiling,
/// arc containment), zero-variability passthrough, the altitude taper, amplitude
/// resolution/cross-derivation, VRB wander, and phase decorrelation.
/// </summary>
public class WindVariationTests
{
    private static WindPerturbationInputs GustyInputs =>
        new(MeanDirectionDeg: 210, MeanSpeedKts: 15, GustExcessKts: 10, HalfSpreadDeg: 30, Variable: false, HeightAglFt: 0);

    // -------------------------------------------------------------------------
    // Determinism
    // -------------------------------------------------------------------------

    [Fact]
    public void Compute_SameInputs_SameOutput()
    {
        for (int t = 0; t < 300; t += 7)
        {
            var a = WindVariation.Compute(GustyInputs, t, 123.45);
            var b = WindVariation.Compute(GustyInputs, t, 123.45);
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void Compute_TimeQuantizedToWholeSeconds()
    {
        // Live path evaluates at integer seconds; sub-tick replay at t + 0.25k. Both
        // must see the same wind within a second or replays diverge.
        var atWhole = WindVariation.Compute(GustyInputs, 42.0, 0);
        var atQuarter = WindVariation.Compute(GustyInputs, 42.25, 0);
        var atThreeQuarters = WindVariation.Compute(GustyInputs, 42.75, 0);
        Assert.Equal(atWhole, atQuarter);
        Assert.Equal(atWhole, atThreeQuarters);
    }

    [Fact]
    public void Compute_VariesOverTime()
    {
        var seen = new HashSet<double>();
        for (int t = 0; t < 120; t += 5)
        {
            seen.Add(WindVariation.Compute(GustyInputs, t, 0).SpeedKts);
        }

        Assert.True(seen.Count > 5, $"Expected the wind to vary over two minutes, got {seen.Count} distinct speeds");
    }

    [Fact]
    public void PhaseSecondsFor_StableAndCaseInsensitive()
    {
        double a = WindVariation.PhaseSecondsFor("UAL123");
        double b = WindVariation.PhaseSecondsFor("ual123");
        Assert.Equal(a, b);
        Assert.InRange(a, 0, WindVariation.PerAircraftPhaseSpanSeconds);
        Assert.NotEqual(WindVariation.PhaseSecondsFor("UAL123"), WindVariation.PhaseSecondsFor("UAL124"));
    }

    [Fact]
    public void Compute_DifferentPhases_Decorrelated()
    {
        int differing = 0;
        for (int t = 0; t < 300; t += 10)
        {
            var a = WindVariation.Compute(GustyInputs, t, 0);
            var b = WindVariation.Compute(GustyInputs, t, 1800);
            if (Math.Abs(a.SpeedKts - b.SpeedKts) > 0.1)
            {
                differing++;
            }
        }

        Assert.True(differing > 15, $"Expected phase-shifted samples to differ most of the time, differed {differing}/30");
    }

    // -------------------------------------------------------------------------
    // Hard bounds
    // -------------------------------------------------------------------------

    [Fact]
    public void Compute_SpeedNeverExceedsGustCeiling_AndUsesTheEnvelope()
    {
        // 15G25: the reported gust is a hard ceiling and the lull floor is shallower than
        // symmetric (lull asymmetry). Sampling across many phases must both respect the
        // bounds AND come close to them — an amplitude that is silently scaled up or down
        // fails one side or the other.
        double max = double.MinValue;
        double min = double.MaxValue;
        for (int phase = 0; phase < 3600; phase += 450)
        {
            for (int t = 0; t < 3600; t++)
            {
                double speed = WindVariation.Compute(GustyInputs, t, phase).SpeedKts;
                Assert.InRange(speed, 15 - (0.65 * 10) - 1e-9, 25 + 1e-9);
                max = Math.Max(max, speed);
                min = Math.Min(min, speed);
            }
        }

        Assert.True(max > 15 + 8, $"Expected gusts to approach the 25 kt ceiling, max was {max:F1}");
        Assert.True(min < 15 - 5, $"Expected lulls to approach the floor, min was {min:F1}");
    }

    [Fact]
    public void Compute_DirectionNeverLeavesArc_AndUsesTheEnvelope()
    {
        // Mean 210 half-spread 30 → never outside 180V240, but the wander must genuinely
        // explore the arc.
        double maxDev = 0;
        for (int phase = 0; phase < 3600; phase += 450)
        {
            for (int t = 0; t < 3600; t++)
            {
                var wind = WindVariation.Compute(GustyInputs, t, phase);
                double diff = Math.Abs(((wind.DirectionDeg - 210 + 540) % 360) - 180);
                Assert.True(diff <= 30 + 1e-9, $"t={t} phase={phase}: direction {wind.DirectionDeg} left the 180V240 arc");
                maxDev = Math.Max(maxDev, diff);
            }
        }

        Assert.True(maxDev > 24, $"Expected the wander to approach the ±30° extremes, max deviation was {maxDev:F1}°");
    }

    [Fact]
    public void Compute_ArcContainment_WrapsThroughNorth()
    {
        var inputs = GustyInputs with { MeanDirectionDeg = 10, HalfSpreadDeg = 40 };
        bool crossedNorth = false;
        for (int t = 0; t < 3600; t++)
        {
            var wind = WindVariation.Compute(inputs, t, 0);
            double diff = Math.Abs(((wind.DirectionDeg - 10 + 540) % 360) - 180);
            Assert.True(diff <= 40 + 1e-9, $"t={t}: direction {wind.DirectionDeg} left the 330V050 arc");
            if (wind.DirectionDeg > 180)
            {
                crossedNorth = true;
            }
        }

        Assert.True(crossedNorth, "Expected the wander to cross through north (directions above 330)");
    }

    // -------------------------------------------------------------------------
    // Zero-variability passthrough
    // -------------------------------------------------------------------------

    [Fact]
    public void Compute_NoVariability_BitIdenticalMean()
    {
        var inputs = new WindPerturbationInputs(270, 12, GustExcessKts: 0, HalfSpreadDeg: 0, Variable: false, HeightAglFt: 0);
        for (int t = 0; t < 100; t += 13)
        {
            var wind = WindVariation.Compute(inputs, t, 77);
            Assert.Equal(270, wind.DirectionDeg);
            Assert.Equal(12, wind.SpeedKts);
        }
    }

    [Fact]
    public void ResolveAmplitudes_NothingAuthored_ReturnsZero()
    {
        var (gust, spread) = WindVariation.ResolveAmplitudes(15, null, null, variable: false);
        Assert.Equal(0, gust);
        Assert.Equal(0, spread);
    }

    // -------------------------------------------------------------------------
    // Altitude taper
    // -------------------------------------------------------------------------

    [Fact]
    public void Compute_AboveTaperTop_BitIdenticalMean()
    {
        var inputs = GustyInputs with { HeightAglFt = 3000 };
        for (int t = 0; t < 100; t += 9)
        {
            var wind = WindVariation.Compute(inputs, t, 0);
            Assert.Equal(210, wind.DirectionDeg);
            Assert.Equal(15, wind.SpeedKts);
        }
    }

    [Fact]
    public void AmplitudeTaper_SmoothstepBetweenBands()
    {
        Assert.Equal(1.0, WindVariation.AmplitudeTaper(0));
        Assert.Equal(1.0, WindVariation.AmplitudeTaper(1000));
        Assert.Equal(0.5, WindVariation.AmplitudeTaper(2000), 3);
        Assert.Equal(0.0, WindVariation.AmplitudeTaper(3000));
        Assert.Equal(0.0, WindVariation.AmplitudeTaper(35_000));
    }

    [Fact]
    public void Compute_MidTaper_ReducedButNonZeroPerturbation()
    {
        var surface = GustyInputs;
        var mid = GustyInputs with { HeightAglFt = 2000 };

        double maxSurfaceDev = 0;
        double maxMidDev = 0;
        for (int t = 0; t < 600; t += 5)
        {
            maxSurfaceDev = Math.Max(maxSurfaceDev, Math.Abs(WindVariation.Compute(surface, t, 0).SpeedKts - 15));
            maxMidDev = Math.Max(maxMidDev, Math.Abs(WindVariation.Compute(mid, t, 0).SpeedKts - 15));
        }

        Assert.True(maxMidDev > 0.1, "Expected some perturbation at 2000 ft AGL");
        Assert.True(maxMidDev < maxSurfaceDev, $"Expected tapered amplitude at 2000 ft ({maxMidDev}) below surface amplitude ({maxSurfaceDev})");
    }

    // -------------------------------------------------------------------------
    // Amplitude resolution / cross-derivation
    // -------------------------------------------------------------------------

    [Fact]
    public void ResolveAmplitudes_BothAuthored_UsedVerbatim()
    {
        // Gust and spread measure different components; never cross-derive over an authored value.
        var (gust, spread) = WindVariation.ResolveAmplitudes(15, 25, 30, variable: false);
        Assert.Equal(10, gust, 6);
        Assert.Equal(30, spread, 6);
    }

    [Fact]
    public void ResolveAmplitudes_GustOnly_DerivesSpread()
    {
        var (gust, spread) = WindVariation.ResolveAmplitudes(15, 25, null, variable: false);
        Assert.Equal(10, gust, 6);
        // σ_u = 10/2.75 = 3.64; σ_θ = 0.9·3.64/15 rad = 12.5°; half-spread = 12.5·4/2 = 25°.
        Assert.Equal(25.0, spread, 0.5);
    }

    [Fact]
    public void ResolveAmplitudes_SpreadOnly_DerivesGust()
    {
        var (gust, spread) = WindVariation.ResolveAmplitudes(15, null, 30, variable: false);
        Assert.Equal(30, spread, 6);
        // σ_θ = 60/4 = 15° = 0.262 rad; σ_v = 3.93 kt; σ_u = 4.36; gust excess = 12.0 kt.
        Assert.Equal(12.0, gust, 0.5);
    }

    [Fact]
    public void ResolveAmplitudes_GustClampedToMaxGustFactor()
    {
        // 10G40 would be a 4× gust factor — gust-front territory; clamp to 2.5× (excess 15).
        var (gust, _) = WindVariation.ResolveAmplitudes(10, 40, null, variable: false);
        Assert.Equal(15, gust, 6);
    }

    [Fact]
    public void ResolveAmplitudes_CalmMean_NoPerturbation()
    {
        var (gust, spread) = WindVariation.ResolveAmplitudes(0, 10, 30, variable: false);
        Assert.Equal(0, gust);
        Assert.Equal(0, spread);
    }

    // -------------------------------------------------------------------------
    // VRB
    // -------------------------------------------------------------------------

    [Fact]
    public void Compute_Vrb_DirectionWandersWide()
    {
        var inputs = new WindPerturbationInputs(270, 4, GustExcessKts: 0, HalfSpreadDeg: 0, Variable: true, HeightAglFt: 0);

        var buckets = new bool[8];
        for (int t = 0; t < 1800; t += 5)
        {
            var wind = WindVariation.Compute(inputs, t, 0);
            buckets[(int)(wind.DirectionDeg / 45.0) % 8] = true;
        }

        int covered = buckets.Count(b => b);
        Assert.True(covered >= 6, $"Expected VRB direction to cover most of the circle over 30 min, covered {covered}/8 octants");
    }

    [Fact]
    public void Compute_Vrb_SpeedStaysLightAndAveragesReported()
    {
        // VRB speed is a 2-minute average: instantaneous stays within ±50% of the report
        // and the long-run mean matches it.
        var inputs = new WindPerturbationInputs(270, 4, GustExcessKts: 0, HalfSpreadDeg: 0, Variable: true, HeightAglFt: 0);
        double sum = 0;
        int n = 0;
        for (int t = 0; t < 1800; t += 3)
        {
            var wind = WindVariation.Compute(inputs, t, 0);
            Assert.InRange(wind.SpeedKts, 2 - 1e-9, 6 + 1e-9);
            sum += wind.SpeedKts;
            n++;
        }

        Assert.InRange(sum / n, 3.4, 4.6);
    }

    [Fact]
    public void Compute_Vrb_Deterministic()
    {
        var inputs = new WindPerturbationInputs(180, 5, GustExcessKts: 0, HalfSpreadDeg: 0, Variable: true, HeightAglFt: 0);
        var a = WindVariation.Compute(inputs, 600, 42);
        var b = WindVariation.Compute(inputs, 600, 42);
        Assert.Equal(a, b);
    }
}
