using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Diagnostics;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.ControllerAi;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The tick-performance regression guards over the bare engine tick (<see cref="SimulationEngine.TickOneSecond"/>), on the
/// S2-OAK-5-2 room with AI Ground and AI Local observing (ObserverBrain issues no commands); traffic is moved by the
/// scenario's preset push and taxi commands and its arrival generators. It asserts the mean allocations per sim-second against a committed baseline
/// (every commit), and a p95 thread-CPU budget of a tenth of the one-second tick period (Nightly). Neither measures wall time.
/// </summary>
public class TickPerformanceRegressionTests
{
    private const string ScenarioFile = "issue153-s2-oak-5-2-scenario.json";
    private const string ScenarioPath = "TestData/" + ScenarioFile;
    private const string BaselineRelativePath = "TestData/tick-allocation-baseline.json";
    private const string RebaselineVariable = "YAAT_TICK_ALLOC_REBASELINE";
    private const string RebaselineHint =
        "set YAAT_TICK_ALLOC_REBASELINE=1 (on a routed gate run pass --env YAAT_TICK_ALLOC_REBASELINE=1) "
        + "and re-record from a filtered run of TickPerformanceRegressionTests";
    private const int Seed = 42;
    private const int MinActiveAircraft = 20;
    private const int FillBudgetSeconds = 3600;
    private const int WarmUpSeconds = 30;
    private const int WindowSeconds = 300;
    private const double RegressionFactor = 1.10;
    private const double ImprovementFactor = 0.80;

    /// <summary>The hosted loop's tick period (one second) divided by ten, in milliseconds.</summary>
    private const double CpuBudgetMs = 1000.0 / 10;

    private static readonly JsonSerializerOptions BaselineJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
    };

    private readonly ITestOutputHelper _output;

    public TickPerformanceRegressionTests(ITestOutputHelper output)
    {
        _output = output;
        Yaat.Sim.Testing.TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void TickAllocations_MeanBytesPerSimSecond_StayWithinTenPercentOfBaseline()
    {
        SimulationEngine? engine = BuildWarmRoom();
        if (engine is null)
        {
            return;
        }

        double[] bytes = MeasureWindow(engine, static () => GC.GetAllocatedBytesForCurrentThread());
        long mean = (long)Math.Round(bytes.Average());
        _output.WriteLine($"mean allocations: {mean:N0} bytes per sim-second over {WindowSeconds} s");

        if (Environment.GetEnvironmentVariable(RebaselineVariable) == "1")
        {
            WriteBaseline(mean);
            return;
        }

        TickAllocationBaseline baseline = ReadBaseline();
        double limit = baseline.MeanBytesPerSecond * RegressionFactor;
        Assert.True(
            mean <= limit,
            $"tick allocations measured {mean:N0} bytes per sim-second; baseline {baseline.MeanBytesPerSecond:N0} "
                + $"(baseline recorded on {baseline.RecordedOn}), +10% limit {limit:N0}; {RebaselineHint}"
        );

        if (mean < (baseline.MeanBytesPerSecond * ImprovementFactor))
        {
            double improvedPct = 100.0 * (baseline.MeanBytesPerSecond - mean) / baseline.MeanBytesPerSecond;
            _output.WriteLine($"allocations improved {improvedPct:F1}%: re-record with {RebaselineVariable}=1");
        }
    }

    [Fact]
    [Trait("Category", "Nightly")]
    public void TickCpu_P95PerSimSecond_StaysWithinTenthOfTickPeriod()
    {
        SimulationEngine? engine = BuildWarmRoom();
        if (engine is null)
        {
            return;
        }

        double[] cpuMs = MeasureWindow(engine, static () => (ThreadCpuTime.Current() + GC.GetTotalPauseDuration()).TotalMilliseconds);
        Array.Sort(cpuMs);
        double p95 = NearestRank(cpuMs, 0.95);
        double p50 = NearestRank(cpuMs, 0.50);
        double max = cpuMs[^1];
        _output.WriteLine($"tick CPU per sim-second (thread CPU time plus GC pause): p95 {p95:F1} ms, p50 {p50:F1} ms, max {max:F1} ms");

        Assert.True(
            p95 <= CpuBudgetMs,
            $"tick p95 is {p95:F1} ms of thread CPU time plus GC pause per sim-second, over the {CpuBudgetMs:F0} ms budget "
                + $"(tick period / 10); p50 {p50:F1} ms, max {max:F1} ms"
        );
    }

    /// <summary>
    /// Loads the room with AI Ground and AI Local on, ticks until at least <see cref="MinActiveAircraft"/> aircraft are in the
    /// world, then <see cref="WarmUpSeconds"/> more for the JIT. Null when the ZOA config or the scenario is missing.
    /// </summary>
    private static SimulationEngine? BuildWarmRoom()
    {
        ArtccConfigRoot? zoa = TestArtccConfig.LoadZoa();
        if (zoa is null || !File.Exists(ScenarioPath))
        {
            return null;
        }

        AiPositionConfig[] positions = [TestAiPositions.OakGround(zoa), TestAiPositions.OakTower(zoa)];
        SimulationEngine engine = AiTestFixture.Load(File.ReadAllText(ScenarioPath), zoa, Seed, positions);
        int maxSeen = 0;
        for (int t = 0; (t < FillBudgetSeconds) && (maxSeen < MinActiveAircraft); t++)
        {
            AiTestFixture.Tick(engine, 1);
            maxSeen = Math.Max(maxSeen, engine.World.GetSnapshot().Count);
        }

        if (maxSeen < MinActiveAircraft)
        {
            Assert.Fail($"room never reached {MinActiveAircraft} aircraft within {FillBudgetSeconds} s (max {maxSeen})");
        }

        AiTestFixture.Tick(engine, WarmUpSeconds);
        return engine;
    }

    /// <summary>
    /// Ticks <see cref="WindowSeconds"/> sim-seconds, one at a time, and returns the <paramref name="read"/> delta around each
    /// <see cref="SimulationEngine.TickOneSecond"/>; the aircraft count is read outside the measured span.
    /// </summary>
    private double[] MeasureWindow(SimulationEngine engine, Func<double> read)
    {
        double[] samples = new double[WindowSeconds];
        int min = int.MaxValue;
        int max = 0;
        for (int i = 0; i < WindowSeconds; i++)
        {
            double before = read();
            engine.TickOneSecond();
            samples[i] = read() - before;
            int count = engine.World.GetSnapshot().Count;
            min = Math.Min(min, count);
            max = Math.Max(max, count);
        }

        _output.WriteLine($"aircraft over the {WindowSeconds} s window: min {min}, max {max}");
        Assert.True(min >= MinActiveAircraft, $"the measured window fell to {min} aircraft; the room must keep at least {MinActiveAircraft} active");
        return samples;
    }

    private static double NearestRank(double[] sortedAscending, double fraction) =>
        sortedAscending[(int)Math.Ceiling(fraction * sortedAscending.Length) - 1];

    private void WriteBaseline(long meanBytesPerSecond)
    {
        string recordedOn = RuntimeInformation.OSDescription + " " + RuntimeInformation.ProcessArchitecture + " .NET " + Environment.Version;
        TickAllocationBaseline baseline = new(ScenarioFile, Seed, WindowSeconds, meanBytesPerSecond, recordedOn);
        string path = Path.Combine(SourceTestProjectDirectory(), BaselineRelativePath);
        File.WriteAllText(path, JsonSerializer.Serialize(baseline, BaselineJsonOptions) + "\n");
        _output.WriteLine($"re-recorded the tick allocation baseline at {path}");
    }

    /// <summary>Reads the committed baseline from the source tree and checks it was recorded on this room and window.</summary>
    private static TickAllocationBaseline ReadBaseline()
    {
        string path = Path.Combine(SourceTestProjectDirectory(), BaselineRelativePath);
        Assert.True(File.Exists(path), $"no tick allocation baseline at {path}; {RebaselineHint}");
        TickAllocationBaseline baseline =
            JsonSerializer.Deserialize<TickAllocationBaseline>(File.ReadAllText(path), BaselineJsonOptions)
            ?? throw new InvalidOperationException($"{path} deserialized to null; {RebaselineHint}");
        Assert.True(baseline.Scenario == ScenarioFile, $"the baseline's scenario is {baseline.Scenario}, not {ScenarioFile}; {RebaselineHint}");
        Assert.True(baseline.Seed == Seed, $"the baseline's seed is {baseline.Seed}, not {Seed}; {RebaselineHint}");
        Assert.True(
            baseline.WindowSeconds == WindowSeconds,
            $"the baseline's windowSeconds is {baseline.WindowSeconds}, not {WindowSeconds}; {RebaselineHint}"
        );
        return baseline;
    }

    private static string SourceTestProjectDirectory()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Yaat.Sim.Tests.csproj")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"no Yaat.Sim.Tests.csproj above {AppContext.BaseDirectory}");
    }

    private sealed record TickAllocationBaseline(string Scenario, int Seed, int WindowSeconds, long MeanBytesPerSecond, string RecordedOn);
}
