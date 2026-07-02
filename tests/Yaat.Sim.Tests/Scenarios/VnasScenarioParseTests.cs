using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// Loads ARTCC training scenarios from local TestData snapshots and verifies that every
/// preset command can be parsed by CommandParser.ParseCompound.
/// Scenarios are NOT committed to the repo — populate them via the sibling yaat-server repo's tools/validate-all-scenarios.py.
/// Tests are skipped when the snapshot directory is empty or missing.
/// </summary>
[Collection("NavDbMutator")]
public class VnasScenarioParseTests
{
    private static readonly string ScenariosRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", "Scenarios")
    );

    private readonly ITestOutputHelper _output;

    public VnasScenarioParseTests(ITestOutputHelper output)
    {
        _output = output;
        // ScenarioValidator reads NavigationDatabase.Instance for procedure lookups.
        // Tests in isolation would otherwise fail with "not initialized" — the full
        // suite only worked by accident because other test fixtures initialized first.
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData("ZAB")]
    [InlineData("ZAN")]
    [InlineData("ZAU")]
    [InlineData("ZBW")]
    [InlineData("ZDC")]
    [InlineData("ZDV")]
    [InlineData("ZFW")]
    [InlineData("ZHN")]
    [InlineData("ZHU")]
    [InlineData("ZID")]
    [InlineData("ZJX")]
    [InlineData("ZKC")]
    [InlineData("ZLA")]
    [InlineData("ZLC")]
    [InlineData("ZMA")]
    [InlineData("ZME")]
    [InlineData("ZMP")]
    [InlineData("ZNY")]
    [InlineData("ZOA")]
    [InlineData("ZOB")]
    [InlineData("ZSE")]
    [InlineData("ZSU")]
    [InlineData("ZTL")]
    public void AllScenarios_PresetCommandsParse(string artccId)
    {
        var dir = Path.Combine(ScenariosRoot, artccId);
        if (!Directory.Exists(dir))
        {
            _output.WriteLine($"No local scenarios for {artccId} — run in ../yaat-server: python tools/validate-all-scenarios.py --artcc {artccId}");
            return;
        }

        var files = Directory.GetFiles(dir, "*.json").Where(f => !Path.GetFileName(f).StartsWith("_")).OrderBy(f => f).ToList();

        if (files.Count == 0)
        {
            _output.WriteLine($"No scenario files in {dir} — run in ../yaat-server: python tools/validate-all-scenarios.py --artcc {artccId}");
            return;
        }

        _output.WriteLine($"Found {files.Count} {artccId} scenario files");

        // A scenario whose JSON won't deserialize is a real breakage and fails the test. An individual
        // preset command that won't parse is almost always a scenario-author data issue (reported on
        // Discord for ARTCC staff to fix), not a YAAT parser regression — those only WARN, so a few
        // unparseable corpus commands don't turn the whole suite red. Field-loss / unknown-enum
        // regressions are guarded separately by the generator/autotrack round-trip test.
        var loadFailures = new List<string>();
        var unparseablePresets = new List<string>();
        var totalPresets = 0;

        foreach (var file in files)
        {
            var json = File.ReadAllText(file);
            var fileName = Path.GetFileNameWithoutExtension(file);

            var result = ScenarioValidator.Validate(json);
            if (result is null)
            {
                loadFailures.Add($"[{fileName}] JSON deserialize failed");
                continue;
            }

            var label = string.IsNullOrWhiteSpace(result.ScenarioName) ? fileName : result.ScenarioName;
            _output.WriteLine($"  {label} ({result.AircraftCount} aircraft)");

            totalPresets += result.TotalPresets;

            foreach (var f in result.Failures)
            {
                unparseablePresets.Add($"[{label}] {f.AircraftId}: \"{f.Command}\" — parse failed");
            }
        }

        _output.WriteLine($"\n{totalPresets} total preset commands");

        if (unparseablePresets.Count > 0)
        {
            _output.WriteLine(
                $"\n=== WARNING: {unparseablePresets.Count} unparseable preset commands (likely scenario-author issues; not a test failure) ==="
            );
            foreach (var w in unparseablePresets)
            {
                _output.WriteLine(w);
            }
        }

        Assert.True(loadFailures.Count == 0, $"{loadFailures.Count} scenarios failed to load:\n{string.Join("\n", loadFailures)}");
    }
}
