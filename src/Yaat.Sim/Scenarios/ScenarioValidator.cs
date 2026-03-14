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

        return new ScenarioValidationResult(scenario.Id, scenario.Name, scenario.Aircraft.Count, totalPresets, parsedOk, failures);
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
}

public record ScenarioValidationResult(
    string ScenarioId,
    string ScenarioName,
    int AircraftCount,
    int TotalPresets,
    int ParsedOk,
    List<PresetParseFailure> Failures
);

public record PresetParseFailure(string AircraftId, string Command);
