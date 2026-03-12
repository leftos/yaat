using System.Text.Json;
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

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly ITestOutputHelper _output = output;

    [Theory]
    [InlineData("ZOA")]
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

        var knownTypoHits = new List<string>();
        var newFailures = new List<string>();
        var totalPresets = 0;

        foreach (var file in files)
        {
            var json = File.ReadAllText(file);
            var fileName = Path.GetFileNameWithoutExtension(file);

            var result = ScenarioValidator.Validate(json);
            if (result is null)
            {
                newFailures.Add($"[{fileName}] JSON deserialize failed");
                continue;
            }

            var label = string.IsNullOrWhiteSpace(result.ScenarioName) ? fileName : result.ScenarioName;
            _output.WriteLine($"  {label} ({result.AircraftCount} aircraft)");

            totalPresets += result.TotalPresets;

            foreach (var f in result.Failures)
            {
                var entry = $"[{label}] {f.AircraftId}: \"{f.Command}\" — parse failed";
                if (f.IsKnownTypo)
                {
                    knownTypoHits.Add(entry);
                }
                else
                {
                    newFailures.Add(entry);
                }
            }
        }

        _output.WriteLine($"\n{totalPresets} total preset commands");

        if (knownTypoHits.Count > 0)
        {
            _output.WriteLine($"\n--- {knownTypoHits.Count} known typos (expected) ---");
            foreach (var f in knownTypoHits)
            {
                _output.WriteLine(f);
            }
        }

        if (newFailures.Count > 0)
        {
            _output.WriteLine($"\n=== {newFailures.Count} NEW PARSE FAILURES ===");
            foreach (var f in newFailures)
            {
                _output.WriteLine(f);
            }
        }

        Assert.True(
            newFailures.Count == 0,
            $"{newFailures.Count} new preset commands failed to parse "
                + $"({knownTypoHits.Count} known typos excluded):\n{string.Join("\n", newFailures)}"
        );
    }
}
