using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.PathfinderGrid;

/// <summary>
/// Soft latency budget for the V2 auto-router vs V1, over a spread of OAK cross-field pairs. The 200k
/// expansion cap in <see cref="AutoRouter"/> is a footgun (a pathological route could explode the
/// search); this guards against a gross latency regression. The design-doc target is median V2 ≤ 2× V1;
/// the hard assertion is the looser 5× ceiling so normal run-to-run jitter on shared CI runners does not
/// flake the build (a >2× median is logged as a warning). Routes are sub-millisecond, so each is timed in
/// microseconds over repeated iterations after a warm-up.
///
/// <para>
/// Gated under the <c>PathfinderGrid</c> category so it is excluded from the per-PR run (see
/// <c>tools/test-all.ps1</c>) and only runs in the nightly / <c>-Full</c> sweep.
/// </para>
/// </summary>
[Trait("Category", "PathfinderGrid")]
public class PathfinderLatencyBudgetTests(ITestOutputHelper output)
{
    private static readonly ITaxiPathfinder V1 = new TaxiPathfinderV1Adapter();
    private static readonly ITaxiPathfinder V2 = new TaxiPathfinderV2();

    private const int WarmupIterations = 3;
    private const int MeasureIterations = 30;
    private const double TargetRatio = 2.0; // design goal — warn above this
    private const double HardRatio = 5.0; // build fails above this

    [Fact]
    public void V2_AutoRouteLatency_MedianWithinBudgetVsV1()
    {
        TestVnasData.EnsureInitialized();
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            output.WriteLine("SKIP: OAK layout unavailable");
            return;
        }

        var pairs = BuildPairs(layout);
        if (pairs.Count == 0)
        {
            output.WriteLine("SKIP: no routable pairs resolved");
            return;
        }

        var v1Micros = new List<double>();
        var v2Micros = new List<double>();

        foreach (var (label, from, to) in pairs)
        {
            for (int i = 0; i < WarmupIterations; i++)
            {
                V1.FindRoute(layout, from, to, AircraftCategory.Jet);
                V2.FindRoute(layout, from, to, AircraftCategory.Jet);
            }

            double v1 = MeasureMicros(() => V1.FindRoute(layout, from, to, AircraftCategory.Jet));
            double v2 = MeasureMicros(() => V2.FindRoute(layout, from, to, AircraftCategory.Jet));
            v1Micros.Add(v1);
            v2Micros.Add(v2);
            output.WriteLine($"{label}: V1={v1, 7:F1}µs  V2={v2, 7:F1}µs  ratio={(v1 > 0 ? v2 / v1 : 0), 5:F2}");
        }

        double v1Median = Median(v1Micros);
        double v2Median = Median(v2Micros);
        double ratio = v1Median > 0 ? v2Median / v1Median : 0;

        output.WriteLine(
            $"MEDIAN over {pairs.Count} pairs: V1={v1Median:F1}µs  V2={v2Median:F1}µs  ratio={ratio:F2} (target ≤{TargetRatio}, hard ≤{HardRatio})"
        );
        if (ratio > TargetRatio)
        {
            output.WriteLine($"WARN: V2 median latency is {ratio:F2}× V1 — above the design target of {TargetRatio}×.");
        }

        Assert.True(
            ratio <= HardRatio,
            $"V2 median auto-route latency {ratio:F2}× V1 exceeds the {HardRatio}× ceiling (V1={v1Median:F1}µs, V2={v2Median:F1}µs)."
        );
    }

    /// <summary>Parking → runway-hold-short pairs across OAK, skipping any node that is absent from the layout.</summary>
    private static List<(string Label, int From, int To)> BuildPairs(AirportGroundLayout layout)
    {
        var pairs = new List<(string, int, int)>();

        int? Parking(string name) => layout.FindParkingByName(name)?.Id;
        int? RunwayBar(string runway)
        {
            var bars = layout.GetRunwayHoldShortNodes(runway);
            return bars.Count > 0 ? bars[0].Id : null;
        }

        string[] parkings = ["SIG1", "GA3", "SIG4", "KAI1", "4", "22", "FDX5"];
        string[] runways = ["28R", "28L", "30"];

        foreach (string p in parkings)
        {
            int? from = Parking(p);
            if (from is null)
            {
                continue;
            }

            foreach (string r in runways)
            {
                int? to = RunwayBar(r);
                if (to is not null && from.Value != to.Value)
                {
                    pairs.Add(($"OAK {p}→{r}", from.Value, to.Value));
                }
            }
        }

        return pairs;
    }

    private static double MeasureMicros(Action act)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < MeasureIterations; i++)
        {
            act();
        }

        sw.Stop();
        return sw.Elapsed.TotalMicroseconds / MeasureIterations;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int n = sorted.Count;
        return (n % 2) == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2.0;
    }
}
