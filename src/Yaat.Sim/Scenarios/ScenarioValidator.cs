using System.Text.Json;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Scenarios;

public static class ScenarioValidator
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static ScenarioValidationResult Validate(Scenario scenario)
    {
        var navDb = NavigationDatabase.Instance;
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
                var result = CommandParser.ParseCompound(preset.Command, ac.FlightPlan?.Route);
                if (!result.IsSuccess)
                {
                    failures.Add(new PresetParseFailure(ac.AircraftId, preset.Command, result.Reason));
                }
                else
                {
                    parsedOk++;
                }
            }
        }

        var (procedureIssues, transitionFixSubs) = ValidateProcedures(scenario);

        return new ScenarioValidationResult(
            scenario.Id,
            scenario.Name,
            scenario.Aircraft.Count,
            totalPresets,
            parsedOk,
            failures,
            procedureIssues,
            transitionFixSubs
        );
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

    private static (List<ProcedureIssue>, List<TransitionFixSubstitution>) ValidateProcedures(Scenario scenario)
    {
        var navDb = NavigationDatabase.Instance;
        var issues = new List<ProcedureIssue>();
        var substitutions = new List<TransitionFixSubstitution>();

        foreach (var ac in scenario.Aircraft)
        {
            var navPath = ac.StartingConditions.NavigationPath;
            if (string.IsNullOrWhiteSpace(navPath))
            {
                continue;
            }

            var tokens = navPath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                var rawName = tokens[i].Split('.')[0];
                string baseName = NavigationDatabase.StripTrailingDigits(rawName);
                if (baseName == rawName)
                {
                    continue;
                }

                // SID/STAR versions are a single trailing digit (e.g., BDEGA4, COKTL3).
                // Multiple trailing digits (e.g., SEA147021) indicate an FRD — skip those.
                int trailingDigits = rawName.Length - baseName.Length;
                if (trailingDigits > 1)
                {
                    continue;
                }

                // Token has a single trailing digit — check if it's a SID or STAR
                var resolvedSid = navDb.ResolveSidId(rawName);
                if (resolvedSid is not null)
                {
                    if (!resolvedSid.Equals(rawName, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new ProcedureIssue(ac.AircraftId, rawName, ProcedureIssueKind.VersionChanged, resolvedSid));
                        CheckSidTransitionFix(tokens, i, resolvedSid, ac.AircraftId, ac.FlightPlan?.Departure ?? "", navDb, substitutions);
                    }

                    continue;
                }

                var resolvedStar = navDb.ResolveStarId(rawName);
                if (resolvedStar is not null)
                {
                    if (!resolvedStar.Equals(rawName, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new ProcedureIssue(ac.AircraftId, rawName, ProcedureIssueKind.VersionChanged, resolvedStar));
                        CheckStarTransitionFix(tokens, i, resolvedStar, ac.AircraftId, ac.FlightPlan?.Destination ?? "", navDb, substitutions);
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

        return (issues, substitutions);
    }

    private static void CheckSidTransitionFix(
        string[] tokens,
        int sidIndex,
        string resolvedSidId,
        string aircraftId,
        string departureAirport,
        NavigationDatabase navDb,
        List<TransitionFixSubstitution> substitutions
    )
    {
        int nextIdx = FindNextNonNumericTokenIndex(tokens, sidIndex + 1);
        if (nextIdx < 0)
        {
            return;
        }

        var nextFixName = tokens[nextIdx].Split('.')[0];

        // Only flag if the fix doesn't appear anywhere on the new SID (body, enroute
        // transitions, or CIFP runway transition legs). Fixes that are still on the
        // procedure will be handled correctly by RouteExpander.
        if (ScenarioLoader.IsFixOnSid(nextFixName, resolvedSidId, departureAirport, navDb))
        {
            return;
        }

        var transitions = navDb.GetSidTransitions(resolvedSidId);
        if (transitions is null || transitions.Count == 0)
        {
            return;
        }

        var closest = ScenarioLoader.FindClosestTransitionFix(nextFixName, transitions, navDb);

        // Fallback: old fix not in navdb — use the fix after it as geographic reference
        if (closest is null)
        {
            int beyondIdx = FindNextNonNumericTokenIndex(tokens, nextIdx + 1);
            if (beyondIdx >= 0)
            {
                var beyondPos = navDb.GetFixPosition(tokens[beyondIdx].Split('.')[0]);
                if (beyondPos is not null)
                {
                    closest = ScenarioLoader.FindClosestTransitionFixToPosition(beyondPos.Value, transitions, navDb);
                }
            }
        }

        substitutions.Add(new TransitionFixSubstitution(aircraftId, resolvedSidId, nextFixName, closest));
    }

    private static void CheckStarTransitionFix(
        string[] tokens,
        int starIndex,
        string resolvedStarId,
        string aircraftId,
        string destinationAirport,
        NavigationDatabase navDb,
        List<TransitionFixSubstitution> substitutions
    )
    {
        int prevIdx = FindPrecedingNonNumericTokenIndex(tokens, starIndex - 1);
        if (prevIdx < 0)
        {
            return;
        }

        var prevFixName = tokens[prevIdx].Split('.')[0];

        // Only flag if the fix doesn't appear anywhere on the new STAR (body, enroute
        // transitions, or CIFP runway transition legs). Fixes that are still on the
        // procedure will be handled correctly by RouteExpander.
        if (ScenarioLoader.IsFixOnStar(prevFixName, resolvedStarId, destinationAirport, navDb))
        {
            return;
        }

        var transitions = navDb.GetStarTransitions(resolvedStarId);
        if (transitions is null || transitions.Count == 0)
        {
            return;
        }

        var closest = ScenarioLoader.FindClosestTransitionFix(prevFixName, transitions, navDb);

        // Fallback: old fix not in navdb — use the fix before it as geographic reference
        if (closest is null)
        {
            int beforeIdx = FindPrecedingNonNumericTokenIndex(tokens, prevIdx - 1);
            if (beforeIdx >= 0)
            {
                var beforePos = navDb.GetFixPosition(tokens[beforeIdx].Split('.')[0]);
                if (beforePos is not null)
                {
                    closest = ScenarioLoader.FindClosestTransitionFixToPosition(beforePos.Value, transitions, navDb);
                }
            }
        }

        substitutions.Add(new TransitionFixSubstitution(aircraftId, resolvedStarId, prevFixName, closest));
    }

    private static int FindNextNonNumericTokenIndex(string[] tokens, int startIndex)
    {
        for (int j = startIndex; j < tokens.Length; j++)
        {
            if (!double.TryParse(tokens[j].Split('.')[0], out _))
            {
                return j;
            }
        }

        return -1;
    }

    private static int FindPrecedingNonNumericTokenIndex(string[] tokens, int startIndex)
    {
        for (int j = startIndex; j >= 0; j--)
        {
            if (!double.TryParse(tokens[j].Split('.')[0], out _))
            {
                return j;
            }
        }

        return -1;
    }
}

public record ScenarioValidationResult(
    string ScenarioId,
    string ScenarioName,
    int AircraftCount,
    int TotalPresets,
    int ParsedOk,
    List<PresetParseFailure> Failures,
    List<ProcedureIssue> ProcedureIssues,
    List<TransitionFixSubstitution> TransitionFixSubstitutions
);

public record PresetParseFailure(string AircraftId, string Command, string? Reason);

public enum ProcedureIssueKind
{
    VersionChanged,
    NotFound,
}

public record ProcedureIssue(string AircraftId, string ProcedureId, ProcedureIssueKind Kind, string? ResolvedId);

/// <summary>
/// Records a stale transition fix detected during procedure version upgrade.
/// OldFix is the fix from the scenario; NewFix is the closest valid transition on the new procedure (null if none found).
/// </summary>
public record TransitionFixSubstitution(string AircraftId, string ProcedureId, string OldFix, string? NewFix);
