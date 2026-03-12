using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
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

    /// <summary>
    /// Known typos in scenario data that we can't fix (owned by ARTCC training staff).
    /// CI should not fail on these. If a command is fixed upstream, remove it from this list.
    /// </summary>
    private static readonly HashSet<string> KnownTypos =
    [
        "SPAWNED AT OAR, REQUEST IFR CLEARANCE", // Not a command (text description)
        "WAI T6 DVIA", // Typo: space in WAIT
        "CFIXX STINS AT 200", // Typo: CFIXX instead of CFIX
        "CFIX SCTRR AT 360", // Typo: SCTRR is not a real fix
        "WAIT10 TRACK Q2B", // Typo: missing space in WAIT10
    ];

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
        var fixes = new PermissiveFixLookup();
        var totalPresets = 0;

        foreach (var file in files)
        {
            var json = File.ReadAllText(file);
            var fileName = Path.GetFileNameWithoutExtension(file);

            Scenario? scenario;
            try
            {
                scenario = JsonSerializer.Deserialize<Scenario>(json, JsonOpts);
            }
            catch (JsonException ex)
            {
                newFailures.Add($"[{fileName}] JSON deserialize failed: {ex.Message}");
                continue;
            }

            if (scenario is null)
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(scenario.Name) ? fileName : scenario.Name;
            _output.WriteLine($"  {label} ({scenario.Aircraft.Count} aircraft)");

            foreach (var ac in scenario.Aircraft)
            {
                foreach (var preset in ac.PresetCommands)
                {
                    if (string.IsNullOrWhiteSpace(preset.Command))
                    {
                        continue;
                    }

                    totalPresets++;
                    var compound = CommandParser.ParseCompound(preset.Command, fixes, ac.FlightPlan?.Route);
                    if (compound is null)
                    {
                        var entry = $"[{label}] {ac.AircraftId}: \"{preset.Command}\" → parse failed";
                        if (KnownTypos.Contains(preset.Command))
                        {
                            knownTypoHits.Add(entry);
                        }
                        else
                        {
                            newFailures.Add(entry);
                        }
                    }
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

    /// <summary>
    /// A permissive fix lookup that returns a dummy position for any fix name.
    /// This allows parse tests to succeed even without real nav data — the goal
    /// is to verify command syntax, not fix resolution.
    /// </summary>
    private class PermissiveFixLookup : IFixLookup
    {
        public (double Lat, double Lon)? GetFixPosition(string name) => (37.6, -122.4);

        public double? GetAirportElevation(string code) => 13.0;

        public IReadOnlyList<string> ExpandRoute(string route) => [];

        public IReadOnlyList<string> ExpandRouteForNavigation(string route, string? departureAirport) => [];

        public IReadOnlyList<string>? GetStarBody(string starId) => null;

        public IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? GetStarTransitions(string starId) => null;
    }
}
