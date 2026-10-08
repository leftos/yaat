using System.Text.Json;
using Xunit;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Reads the runways of every aircraft in the local scenario corpus (<c>TestData/Scenarios</c>, not committed — populate it
/// with the sibling yaat-server repo's <c>tools/validate-all-scenarios.py</c>): the read never throws, whatever a scenario's
/// presets say. Skipped when the corpus is absent, as <c>VnasScenarioParseTests</c> is.
/// </summary>
public class ScenarioRunwaySignalsCorpusTests
{
    private static readonly string ScenariosRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", "Scenarios")
    );

    private readonly ITestOutputHelper _output;

    public ScenarioRunwaySignalsCorpusTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void EveryCorpusScenario_ReadsItsRunwaysWithoutThrowing()
    {
        if (!Directory.Exists(ScenariosRoot))
        {
            _output.WriteLine($"No local scenario corpus at {ScenariosRoot} — run in ../yaat-server: python tools/validate-all-scenarios.py");
            return;
        }

        List<string> files =
        [
            .. Directory
                .GetFiles(ScenariosRoot, "*.json", SearchOption.AllDirectories)
                .Where(file => !Path.GetFileName(file).StartsWith('_'))
                .Order(StringComparer.Ordinal),
        ];
        int scenarios = 0;
        int signals = 0;
        foreach (string file in files)
        {
            if (TryLoad(file) is not { } result)
            {
                continue;
            }

            scenarios++;
            foreach (LoadedAircraft loaded in result.ImmediateAircraft.Concat(result.DelayedAircraft).Concat(result.DeferredAircraft))
            {
                signals += ScenarioRunwaySignals.AircraftRunways(loaded).Count;
            }

            ImpliedActiveRunways.For(result, weather: null, MagneticDeclination.EvaluationDateUtc);
            ScenarioRunwayUse.CountAssigned(result);
        }

        _output.WriteLine($"{scenarios} of {files.Count} corpus files loaded; {signals} runway signals read");
        if (files.Count > 0)
        {
            Assert.True(scenarios > 0, $"none of the {files.Count} corpus files loaded; the sweep read nothing");
            Assert.True(signals > 0, "the corpus loaded but no aircraft named a runway; the reader is reading nothing");
        }
    }

    /// <summary>
    /// The corpus folder also holds files that are not scenarios (no aircraft array) and scenarios the loader rejects; neither
    /// is what this sweep checks, so each is reported and skipped.
    /// </summary>
    private ScenarioLoadResult? TryLoad(string file)
    {
        try
        {
            return ScenarioLoader.Load(File.ReadAllText(file), new NullGroundData(), new Random(7), MagneticDeclination.EvaluationDateUtc);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            _output.WriteLine($"skipped {Path.GetFileName(file)}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
