using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// Loads ARTCC training scenarios from local TestData snapshots and verifies that every
/// preset command can be parsed by CommandParser.ParseCompound.
/// Scenarios are NOT committed to the repo — download them locally with tools/refresh-scenarios.py.
/// Tests are skipped when the snapshot directory is empty or missing.
/// </summary>
public class VnasScenarioParseTests(ITestOutputHelper output)
{
    private static readonly string ScenariosRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData", "Scenarios")
    );

    private readonly ITestOutputHelper _output = output;

    [Theory]
    [InlineData("ZAB")]
    [InlineData("ZAU")]
    [InlineData("ZBW")]
    [InlineData("ZDC")]
    [InlineData("ZDV")]
    [InlineData("ZFW")]
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
    [InlineData("ZTL")]
    public void AllScenarios_PresetCommandsParse(string artccId)
    {
        var dir = Path.Combine(ScenariosRoot, artccId);
        if (!Directory.Exists(dir))
        {
            _output.WriteLine($"No local scenarios for {artccId} — run: python tools/refresh-scenarios.py {artccId}");
            return;
        }

        var files = Directory.GetFiles(dir, "*.json").Where(f => !Path.GetFileName(f).StartsWith("_")).OrderBy(f => f).ToList();

        if (files.Count == 0)
        {
            _output.WriteLine($"No scenario files in {dir} — run: python tools/refresh-scenarios.py {artccId}");
            return;
        }

        _output.WriteLine($"Found {files.Count} {artccId} scenario files");

        var failures = new List<string>();
        var totalPresets = 0;

        foreach (var file in files)
        {
            var json = File.ReadAllText(file);
            var fileName = Path.GetFileNameWithoutExtension(file);

            var result = ScenarioValidator.Validate(json);
            if (result is null)
            {
                failures.Add($"[{fileName}] JSON deserialize failed");
                continue;
            }

            var label = string.IsNullOrWhiteSpace(result.ScenarioName) ? fileName : result.ScenarioName;
            _output.WriteLine($"  {label} ({result.AircraftCount} aircraft)");

            totalPresets += result.TotalPresets;

            foreach (var f in result.Failures)
            {
                failures.Add($"[{label}] {f.AircraftId}: \"{f.Command}\" — parse failed");
            }
        }

        _output.WriteLine($"\n{totalPresets} total preset commands");

        if (failures.Count > 0)
        {
            _output.WriteLine($"\n=== {failures.Count} PARSE FAILURES ===");
            foreach (var f in failures)
            {
                _output.WriteLine(f);
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} preset commands failed to parse:\n{string.Join("\n", failures)}");
    }
}
