using System.Text.Json;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Scenarios;

public static class ScenarioValidator
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static ScenarioValidationResult Validate(Scenario scenario, NavigationDatabase navDb)
    {
        var failures = new List<PresetParseFailure>();
        int totalPresets = 0;
        int parsedOk = 0;

        foreach (var ac in scenario.Aircraft)
        {
            foreach (var preset in ac.PresetCommands)
            {
                if (string.IsNullOrWhiteSpace(preset.Command))
                {
                    continue;
                }

                totalPresets++;
                var compound = CommandParser.ParseCompound(preset.Command, navDb, ac.FlightPlan?.Route);
                if (compound is null)
                {
                    failures.Add(new PresetParseFailure(ac.AircraftId, preset.Command));
                }
                else
                {
                    parsedOk++;
                }
            }
        }

        var procedureIssues = ValidateProcedures(scenario, navDb);

        return new ScenarioValidationResult(scenario.Id, scenario.Name, scenario.Aircraft.Count, totalPresets, parsedOk, failures, procedureIssues);
    }

    public static ScenarioValidationResult Validate(Scenario scenario)
    {
        return Validate(scenario, new NavigationDatabase(null));
    }

    public static ScenarioValidationResult? Validate(string scenarioJson)
    {
        Scenario? scenario;
        try
        {
            scenario = JsonSerializer.Deserialize<Scenario>(scenarioJson, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }

        if (scenario is null)
        {
            return null;
        }

        return Validate(scenario);
    }

    public static ScenarioValidationResult? Validate(string scenarioJson, NavigationDatabase navDb)
    {
        Scenario? scenario;
        try
        {
            scenario = JsonSerializer.Deserialize<Scenario>(scenarioJson, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }

        if (scenario is null)
        {
            return null;
        }

        return Validate(scenario, navDb);
    }

    private static List<ProcedureIssue> ValidateProcedures(Scenario scenario, NavigationDatabase navDb)
    {
        var issues = new List<ProcedureIssue>();

        foreach (var ac in scenario.Aircraft)
        {
            var navPath = ac.StartingConditions.NavigationPath;
            if (string.IsNullOrWhiteSpace(navPath))
            {
                continue;
            }

            var tokens = navPath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                var rawName = token.Split('.')[0];
                string baseName = NavigationDatabase.StripTrailingDigits(rawName);
                if (baseName == rawName)
                {
                    continue;
                }

                // Token has trailing digits — check if it's a SID or STAR
                var resolvedSid = navDb.ResolveSidId(rawName);
                if (resolvedSid is not null)
                {
                    if (!resolvedSid.Equals(rawName, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new ProcedureIssue(ac.AircraftId, rawName, ProcedureIssueKind.VersionChanged, resolvedSid));
                    }

                    continue;
                }

                var resolvedStar = navDb.ResolveStarId(rawName);
                if (resolvedStar is not null)
                {
                    if (!resolvedStar.Equals(rawName, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new ProcedureIssue(ac.AircraftId, rawName, ProcedureIssueKind.VersionChanged, resolvedStar));
                    }

                    continue;
                }

                // Has trailing digits but no SID/STAR match — could be a missing procedure
                // Only flag if base name also doesn't match (avoid false positives for fix names like "ARPT2")
                if (navDb.GetFixPosition(rawName) is null && navDb.GetFixPosition(baseName) is not null)
                {
                    issues.Add(new ProcedureIssue(ac.AircraftId, rawName, ProcedureIssueKind.NotFound, null));
                }
            }
        }

        return issues;
    }
}

public record ScenarioValidationResult(
    string ScenarioId,
    string ScenarioName,
    int AircraftCount,
    int TotalPresets,
    int ParsedOk,
    List<PresetParseFailure> Failures,
    List<ProcedureIssue> ProcedureIssues
);

public record PresetParseFailure(string AircraftId, string Command);

public enum ProcedureIssueKind
{
    VersionChanged,
    NotFound,
}

public record ProcedureIssue(string AircraftId, string ProcedureId, ProcedureIssueKind Kind, string? ResolvedId);
