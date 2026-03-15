using System.Text.Json;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Proto;
using Yaat.Sim.Scenarios;

namespace Yaat.ScenarioValidator;

public static class Program
{
    private static readonly string[] AllArtccs =
    [
        "ZAB",
        "ZAU",
        "ZBW",
        "ZDC",
        "ZDV",
        "ZFW",
        "ZHU",
        "ZID",
        "ZJX",
        "ZKC",
        "ZLA",
        "ZLC",
        "ZMA",
        "ZME",
        "ZMP",
        "ZNY",
        "ZOA",
        "ZOB",
        "ZSE",
        "ZTL",
    ];

    private static readonly JsonSerializerOptions JsonOutputOpts = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        var artccIds = new List<string>();
        string? filePath = null;
        string? dirPath = null;
        bool allMode = false;
        bool jsonOutput = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--artcc" when i + 1 < args.Length:
                    // Consume all following non-flag arguments as ARTCC IDs
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    {
                        artccIds.Add(args[++i].ToUpperInvariant());
                    }

                    break;
                case "--file" when i + 1 < args.Length:
                    filePath = args[++i];
                    break;
                case "--dir" when i + 1 < args.Length:
                    dirPath = args[++i];
                    break;
                case "--all":
                    allMode = true;
                    break;
                case "--json":
                    jsonOutput = true;
                    break;
                default:
                    PrintUsage();
                    return 1;
            }
        }

        int modeCount = (artccIds.Count > 0 ? 1 : 0) + (filePath is not null ? 1 : 0) + (dirPath is not null ? 1 : 0) + (allMode ? 1 : 0);
        if (modeCount != 1)
        {
            PrintUsage();
            return 1;
        }

        // Download NavData + CIFP for procedure validation
        await InitializeNavDataAsync();

        if (allMode)
        {
            return await ValidateMultipleAsync(AllArtccs, jsonOutput);
        }

        if (artccIds.Count > 1)
        {
            return await ValidateMultipleAsync([.. artccIds], jsonOutput);
        }

        List<ScenarioValidationResult> results;

        if (artccIds.Count == 1)
        {
            results = await ValidateArtccAsync(artccIds[0], jsonOutput);
        }
        else if (filePath is not null)
        {
            results = ValidateFile(filePath);
        }
        else
        {
            results = ValidateDirectory(dirPath!);
        }

        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(results, JsonOutputOpts));
            return results.Any(r => r.Failures.Count > 0) ? 1 : 0;
        }

        return PrintTextReport(results);
    }

    private static async Task InitializeNavDataAsync()
    {
        var client = new VnasClient();

        NavDataSet? navData = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Console.Error.Write(attempt == 1 ? "Downloading NavData..." : $" retry {attempt}/3...");
                navData = await client.DownloadNavDataAsync();
                if (navData is not null)
                {
                    Console.Error.WriteLine($" {navData.Fixes.Count} fixes");
                    break;
                }

                Console.Error.Write(" null response");
            }
            catch (Exception ex)
            {
                Console.Error.Write($" failed ({ex.Message})");
            }
        }

        if (navData is null)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("ERROR: NavData download failed after 3 attempts. Aborting.");
            Environment.Exit(1);
        }

        Console.Error.Write("Downloading CIFP...");
        using var cifpService = new CifpDataService();
        await cifpService.InitializeAsync();

        if (cifpService.CifpFilePath is null)
        {
            Console.Error.WriteLine(" failed");
            Console.Error.WriteLine("ERROR: CIFP download failed. Aborting.");
            Environment.Exit(1);
        }

        Console.Error.WriteLine(" OK");
        NavigationDatabase.Initialize(navData!, cifpService.CifpFilePath!, customFixesBaseDir: "");
    }

    private static async Task<int> ValidateMultipleAsync(string[] artccs, bool jsonOutput)
    {
        var allResults = new Dictionary<string, List<ScenarioValidationResult>>();

        var tasks = artccs.Select(async artcc =>
        {
            var results = await ValidateArtccAsync(artcc, quiet: true);
            lock (allResults)
            {
                allResults[artcc] = results;
            }

            int failures = results.Sum(r => r.Failures.Count);
            int procIssues = results.Sum(r => r.ProcedureIssues.Count);
            int presets = results.Sum(r => r.TotalPresets);
            Console.Error.WriteLine($"  [{artcc}] {results.Count} scenarios, {presets} presets, {failures} failures, {procIssues} procedure issues");
        });
        await Task.WhenAll(tasks);

        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(allResults, JsonOutputOpts));
            return allResults.Values.Any(list => list.Any(r => r.Failures.Count > 0)) ? 1 : 0;
        }

        Console.WriteLine("\n=== SUMMARY ===\n");
        bool anyFailures = false;
        foreach (var artcc in artccs)
        {
            var results = allResults[artcc];
            int scenarios = results.Count;
            int presets = results.Sum(r => r.TotalPresets);
            int failures = results.Sum(r => r.Failures.Count);
            int procIssues = results.Sum(r => r.ProcedureIssues.Count);
            var status = failures == 0 && procIssues == 0 ? "PASS" : $"FAIL ({failures} parse, {procIssues} procedure)";
            if (failures > 0 || procIssues > 0)
            {
                anyFailures = true;
            }

            Console.WriteLine($"  {artcc}: {scenarios} scenarios, {presets} presets — {status}");
        }

        if (anyFailures)
        {
            Console.WriteLine();
            foreach (var artcc in artccs)
            {
                var failed = allResults[artcc].Where(r => r.Failures.Count > 0).ToList();
                if (failed.Count > 0)
                {
                    Console.WriteLine($"\n=== {artcc} PARSE FAILURES ===\n");
                    foreach (var scenario in failed)
                    {
                        Console.WriteLine($"  {scenario.ScenarioName}");
                        var byAircraft = scenario.Failures.GroupBy(f => f.AircraftId);
                        foreach (var group in byAircraft)
                        {
                            Console.WriteLine($"    {group.Key}:");
                            foreach (var f in group)
                            {
                                var reason = f.Reason is not null ? $" — {f.Reason}" : "";
                                Console.WriteLine($"      \"{f.Command}\"{reason}");
                            }
                        }

                        Console.WriteLine();
                    }
                }

                var withProcIssues = allResults[artcc].Where(r => r.ProcedureIssues.Count > 0).ToList();
                if (withProcIssues.Count > 0)
                {
                    Console.WriteLine($"\n=== {artcc} PROCEDURE ISSUES ===\n");
                    foreach (var scenario in withProcIssues)
                    {
                        Console.WriteLine($"  {scenario.ScenarioName}");
                        PrintProcedureIssuesByProcedure(scenario.ProcedureIssues, "    ");
                        Console.WriteLine();
                    }
                }
            }
        }

        return anyFailures ? 1 : 0;
    }

    private static async Task<List<ScenarioValidationResult>> ValidateArtccAsync(string artccId, bool quiet)
    {
        var client = new VnasClient();

        if (!quiet)
        {
            Console.Error.Write($"Fetching scenario list for {artccId}...");
        }

        var summaries = await client.GetScenarioSummariesAsync(artccId);

        if (summaries.Count == 0)
        {
            if (!quiet)
            {
                Console.Error.WriteLine($" no scenarios found for {artccId}");
            }

            return [];
        }

        if (!quiet)
        {
            Console.Error.WriteLine($" {summaries.Count} scenarios");
            Console.Error.WriteLine($"Validating {summaries.Count} {artccId} scenarios...\n");
        }

        // Download and validate scenarios in parallel (throttled to 8 concurrent)
        using var throttle = new SemaphoreSlim(8);
        int completed = 0;
        var tasks = summaries.Select(async summary =>
        {
            await throttle.WaitAsync();
            try
            {
                var json = await client.GetScenarioJsonAsync(summary.Id);
                if (json is null)
                {
                    Console.Error.WriteLine($"  [{artccId}] {summary.Name} — FETCH FAILED");
                    return null;
                }

                var result = Yaat.Sim.Scenarios.ScenarioValidator.Validate(json);
                if (result is null)
                {
                    Console.Error.WriteLine($"  [{artccId}] {summary.Name} — JSON PARSE FAILED");
                    return null;
                }

                if (!quiet)
                {
                    int n = Interlocked.Increment(ref completed);
                    var issues = new List<string>();
                    if (result.Failures.Count > 0)
                    {
                        issues.Add($"{result.Failures.Count} failure{(result.Failures.Count != 1 ? "s" : "")}");
                    }

                    if (result.ProcedureIssues.Count > 0)
                    {
                        issues.Add($"{result.ProcedureIssues.Count} procedure issue{(result.ProcedureIssues.Count != 1 ? "s" : "")}");
                    }

                    var status = issues.Count == 0 ? "OK" : string.Join(", ", issues);
                    Console.Error.WriteLine(
                        $"  [{n}/{summaries.Count}] {result.ScenarioName} ({result.AircraftCount} aircraft, {result.TotalPresets} presets) — {status}"
                    );
                }

                return result;
            }
            finally
            {
                throttle.Release();
            }
        });

        var all = await Task.WhenAll(tasks);
        return all.Where(r => r is not null).ToList()!;
    }

    private static List<ScenarioValidationResult> ValidateFile(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return [];
        }

        var json = File.ReadAllText(path);
        var result = Yaat.Sim.Scenarios.ScenarioValidator.Validate(json);
        if (result is null)
        {
            Console.Error.WriteLine($"Failed to parse: {path}");
            return [];
        }

        return [result];
    }

    private static List<ScenarioValidationResult> ValidateDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            Console.Error.WriteLine($"Directory not found: {path}");
            return [];
        }

        var files = Directory.GetFiles(path, "*.json").Where(f => !Path.GetFileName(f).StartsWith("_")).OrderBy(f => f).ToList();

        if (files.Count == 0)
        {
            Console.Error.WriteLine($"No .json files in {path}");
            return [];
        }

        Console.Error.WriteLine($"Validating {files.Count} scenario files...\n");
        var results = new List<ScenarioValidationResult>();

        foreach (var file in files)
        {
            var json = File.ReadAllText(file);
            var result = Yaat.Sim.Scenarios.ScenarioValidator.Validate(json);
            if (result is null)
            {
                Console.Error.WriteLine($"  {Path.GetFileName(file)} — JSON PARSE FAILED");
                continue;
            }

            results.Add(result);
            var status = result.Failures.Count == 0 ? "OK" : $"{result.Failures.Count} failure{(result.Failures.Count != 1 ? "s" : "")}";
            Console.Error.WriteLine($"  {result.ScenarioName} ({result.AircraftCount} aircraft, {result.TotalPresets} presets) — {status}");
        }

        return results;
    }

    private static int PrintTextReport(List<ScenarioValidationResult> results)
    {
        if (results.Count == 0)
        {
            return 0;
        }

        int totalPresets = results.Sum(r => r.TotalPresets);
        int totalFailures = results.Sum(r => r.Failures.Count);
        int totalProcIssues = results.Sum(r => r.ProcedureIssues.Count);

        Console.WriteLine($"\n=== RESULTS ===");
        Console.WriteLine(
            $"{results.Count} scenarios, {totalPresets} presets, {totalFailures} failure{(totalFailures != 1 ? "s" : "")}, {totalProcIssues} procedure issue{(totalProcIssues != 1 ? "s" : "")}"
        );

        var failedScenarios = results.Where(r => r.Failures.Count > 0).ToList();
        if (failedScenarios.Count > 0)
        {
            Console.WriteLine($"\n=== FAILURES ===\n");
            foreach (var scenario in failedScenarios)
            {
                Console.WriteLine($"  {scenario.ScenarioName}");
                var byAircraft = scenario.Failures.GroupBy(f => f.AircraftId);
                foreach (var group in byAircraft)
                {
                    Console.WriteLine($"    {group.Key}:");
                    foreach (var f in group)
                    {
                        var reason = f.Reason is not null ? $" — {f.Reason}" : "";
                        Console.WriteLine($"      \"{f.Command}\"{reason}");
                    }
                }
                Console.WriteLine();
            }
        }

        var procScenarios = results.Where(r => r.ProcedureIssues.Count > 0).ToList();
        if (procScenarios.Count > 0)
        {
            Console.WriteLine($"\n=== PROCEDURE ISSUES ===\n");
            foreach (var scenario in procScenarios)
            {
                Console.WriteLine($"  {scenario.ScenarioName}");
                PrintProcedureIssuesByProcedure(scenario.ProcedureIssues, "    ");
                Console.WriteLine();
            }
        }

        return totalFailures > 0 || totalProcIssues > 0 ? 1 : 0;
    }

    private static void PrintProcedureIssuesByProcedure(List<ProcedureIssue> issues, string indent)
    {
        // Group by (ProcedureId, Kind, ResolvedId) so each unique procedure line appears once with all callsigns
        var grouped = issues.GroupBy(i => (i.ProcedureId, i.Kind, i.ResolvedId)).OrderBy(g => g.Key.ProcedureId);

        foreach (var group in grouped)
        {
            var callsigns = string.Join(", ", group.Select(i => i.AircraftId).OrderBy(id => id));
            string detail =
                group.Key.Kind == ProcedureIssueKind.VersionChanged
                    ? $"{indent}{group.Key.ProcedureId} → {group.Key.ResolvedId}: {callsigns}"
                    : $"{indent}{group.Key.ProcedureId} not found: {callsigns}";
            Console.WriteLine(detail);
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  Yaat.ScenarioValidator --artcc <ID> [<ID>...]");
        Console.Error.WriteLine("  Yaat.ScenarioValidator --all");
        Console.Error.WriteLine("  Yaat.ScenarioValidator --file <scenario.json>");
        Console.Error.WriteLine("  Yaat.ScenarioValidator --dir <path/to/scenarios/>");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  --json    Output results as JSON");
    }
}
