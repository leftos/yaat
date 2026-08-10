using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// Round-trip calibration acceptance tests: author a wind, observe the simulated field the
/// way the METAR issuer does, and require the observations to reproduce the authored
/// values within tolerance. These pin the engine's constants — when a constant changes,
/// tune the constant until these pass; never widen the tolerances.
/// </summary>
public class WindCalibrationTests
{
    private static WeatherProfile Profile(double dir, double speed, double? gusts, double? halfSpread, bool variable = false) =>
        new()
        {
            WindLayers =
            [
                new WindLayer
                {
                    Altitude = 0,
                    Direction = dir,
                    Speed = speed,
                    Gusts = gusts,
                    DirectionVariabilityDeg = halfSpread,
                    Variable = variable ? true : null,
                },
            ],
        };

    private static List<ObservedWind> ObserveHalfHour(WeatherProfile weather)
    {
        // Fifteen observations across 30 simulated minutes, like a station being read
        // every two minutes.
        var observations = new List<ObservedWind>(15);
        for (int minute = 10; minute < 40; minute += 2)
        {
            var obs = WindObservation.Observe(weather, minute * 60);
            Assert.NotNull(obs);
            observations.Add(obs);
        }

        return observations;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    [Fact]
    public void RoundTrip_SpreadWind_MeansReproduceAuthoredValues()
    {
        // 21015KT 180V240 (mean 210, half-spread 30): observed 2-min mean direction
        // stays near the authored mean and mean speed near the authored speed.
        var weather = Profile(210, 15, gusts: null, halfSpread: 30);
        var observations = ObserveHalfHour(weather);

        double medianDir = Median(observations.Select(o => o.MeanDirectionMagDeg));
        double medianSpeed = Median(observations.Select(o => o.MeanSpeedKts));

        Assert.InRange(medianDir, 200, 220);
        Assert.InRange(medianSpeed, 13.5, 16.5);
    }

    [Fact]
    public void RoundTrip_GustyWind_PeakApproachesReportedGustAndLullStaysShallow()
    {
        // 18G28KT: the 10-minute observed peak must approach the authored gust (it can
        // never exceed it — the gust is a hard ceiling), the observed mean must stay near
        // 18, and the lull must respect the asymmetric floor (18 − 0.65·10 = 11.5).
        var weather = Profile(210, 18, gusts: 28, halfSpread: null);
        var observations = ObserveHalfHour(weather);

        double medianPeak = Median(observations.Select(o => o.PeakSpeedKts));
        double medianMean = Median(observations.Select(o => o.MeanSpeedKts));
        double minLull = observations.Min(o => o.LullSpeedKts);

        Assert.InRange(medianPeak, 26, 28);
        Assert.InRange(medianMean, 16.5, 19.5);
        Assert.True(minLull >= 11.5 - 1e-9, $"Lull {minLull:F1} fell below the asymmetric floor");
    }

    [Fact]
    public void RoundTrip_VrbWind_MeanSpeedMatchesReport_DirectionCoversCircle()
    {
        // VRB04KT: scalar mean 4 ± 0.6 kt; direction distribution covers more than 270°
        // of the circle across 30 minutes.
        var weather = Profile(270, 4, gusts: null, halfSpread: null, variable: true);
        var observations = ObserveHalfHour(weather);

        double medianSpeed = Median(observations.Select(o => o.MeanSpeedKts));
        Assert.InRange(medianSpeed, 3.4, 4.6);

        var buckets = new bool[8];
        for (int t = 10 * 60; t < 40 * 60; t += 5)
        {
            var wind = WindInterpolator.GetWindAt(weather, 0, t, 0);
            buckets[(int)(wind.DirectionDeg / 45.0) % 8] = true;
        }

        Assert.True(buckets.Count(b => b) >= 7, $"VRB direction covered only {buckets.Count(b => b)}/8 octants over 30 min");
    }

    [Fact]
    public void RoundTrip_SteadyWind_ObservationIsExact()
    {
        var weather = Profile(270, 12, gusts: null, halfSpread: null);
        var observations = ObserveHalfHour(weather);

        Assert.All(
            observations,
            o =>
            {
                Assert.Equal(270, o.MeanDirectionMagDeg, 6);
                Assert.Equal(12, o.MeanSpeedKts, 6);
                Assert.Equal(12, o.PeakSpeedKts, 6);
                Assert.Equal(12, o.LullSpeedKts, 6);
            }
        );
    }
}
