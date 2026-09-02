using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.ControllerAi.Knowledge;

/// <summary>A knowledge file failed validation; the message lists every problem found so the author fixes them in one pass.</summary>
public sealed class FacilityOpsValidationException(string file, IReadOnlyList<string> errors) : Exception($"{file}: {string.Join("; ", errors)}")
{
    public string File { get; } = file;

    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// The process-wide set of facility knowledge files (<c>Data/FacilityOps/*.json</c>), loaded and cross-validated against
/// navdata at startup — an unknown runway, a dangling configuration name or an unknown enum value is a load error, never a
/// silent skip. Static like <see cref="NavigationDatabase"/>: <see cref="Initialize"/> at startup, <see cref="SetInstance"/>
/// in tests, <see cref="For"/> at every consult site (null when the airport has no file — the generic rules stand alone).
/// </summary>
public static class FacilityOpsDatabase
{
    private static readonly ILogger Log = SimLog.CreateLogger("FacilityOpsDatabase");
    private static IReadOnlyList<FacilityOps> _files = [];

    public static bool IsInitialized { get; private set; }

    public static IReadOnlyList<FacilityOps> Files => _files;

    public static void Initialize(string directory, NavigationDatabase navigation)
    {
        SetInstance(LoadDirectory(directory, navigation));
    }

    public static void SetInstance(IReadOnlyList<FacilityOps> files)
    {
        _files = files;
        IsInitialized = true;
    }

    /// <summary>The knowledge for an airport given as FAA or ICAO id, or null when no file covers it.</summary>
    public static FacilityOps? For(string? airportId)
    {
        if (string.IsNullOrWhiteSpace(airportId))
        {
            return null;
        }

        return _files.FirstOrDefault(f => NavigationDatabase.AirportIdsMatch(f.AirportId, airportId));
    }

    /// <summary>
    /// Parses and validates every <c>*.json</c> in <paramref name="directory"/> (missing directory ⇒ no knowledge, not an error).
    /// </summary>
    public static IReadOnlyList<FacilityOps> LoadDirectory(string directory, NavigationDatabase navigation)
    {
        if (!Directory.Exists(directory))
        {
            Log.LogInformation("No facility knowledge directory at {Directory}; brains run their generic rules everywhere", directory);
            return [];
        }

        var files = new List<FacilityOps>();
        foreach (var path in Directory.GetFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            files.Add(Load(path, navigation));
        }

        Log.LogInformation("Loaded facility knowledge for {Airports}", string.Join(", ", files.Select(f => f.AirportId)));
        return files;
    }

    public static FacilityOps Load(string path, NavigationDatabase navigation)
    {
        FacilityOps? ops;
        try
        {
            ops = JsonSerializer.Deserialize<FacilityOps>(File.ReadAllText(path), FacilityOpsJson.Options);
        }
        catch (JsonException ex)
        {
            throw new FacilityOpsValidationException(Path.GetFileName(path), [ex.Message]);
        }

        if (ops is null)
        {
            throw new FacilityOpsValidationException(Path.GetFileName(path), ["the file is empty"]);
        }

        var errors = FacilityOpsValidator.Validate(ops, navigation);
        if (errors.Count > 0)
        {
            throw new FacilityOpsValidationException(Path.GetFileName(path), errors);
        }

        return ops;
    }
}

/// <summary>Cross-checks a knowledge file against navdata and against itself.</summary>
public static class FacilityOpsValidator
{
    public static IReadOnlyList<string> Validate(FacilityOps ops, NavigationDatabase navigation)
    {
        var errors = new List<string>();
        if (ops.SchemaVersion != FacilityOps.CurrentSchemaVersion)
        {
            errors.Add($"schemaVersion {ops.SchemaVersion} is not {FacilityOps.CurrentSchemaVersion}");
        }

        RequireText(errors, ops.FacilityId, "facilityId");
        RequireText(errors, ops.AirportId, "airportId");
        RequireText(errors, ops.SourceDocument, "sourceDocument");
        if (!string.IsNullOrWhiteSpace(ops.AirportId) && !navigation.TryResolveAirport(ops.AirportId, out _))
        {
            errors.Add($"airportId {ops.AirportId} is not in navdata");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var configuration in ops.RunwayConfigurations)
        {
            ValidateConfiguration(errors, names, configuration, navigation);
        }

        if (ops.RunwaySelection is { } selection)
        {
            ValidateSelection(errors, ops, selection, navigation);
        }

        foreach (var rule in ops.RunwayAssignmentPolicy)
        {
            ValidateAssignmentRule(errors, ops, rule, navigation);
        }

        return errors;
    }

    private static void ValidateConfiguration(
        List<string> errors,
        HashSet<string> names,
        RunwayConfiguration configuration,
        NavigationDatabase navigation
    )
    {
        RequireText(errors, configuration.Name, "runwayConfigurations[].name");
        RequireText(errors, configuration.Source, $"runwayConfigurations[{configuration.Name}].source");
        if (!names.Add(configuration.Name))
        {
            errors.Add($"runway configuration {configuration.Name} is declared twice");
        }

        if (configuration.Runways.Count == 0)
        {
            errors.Add($"runway configuration {configuration.Name} names no airport");
        }

        var airports = configuration.Runways.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        for (int i = 0; i < airports.Count; i++)
        {
            for (int j = i + 1; j < airports.Count; j++)
            {
                if (NavigationDatabase.AirportIdsMatch(airports[i], airports[j]))
                {
                    errors.Add($"runway configuration {configuration.Name} names the same airport as both {airports[i]} and {airports[j]}");
                }
            }
        }

        foreach (var (airport, sets) in configuration.Runways)
        {
            if (!navigation.TryResolveAirport(airport, out _))
            {
                errors.Add($"runway configuration {configuration.Name}: airport {airport} is not in navdata");
                continue;
            }

            foreach (var runway in sets.Departure.Concat(sets.Arrival))
            {
                if (navigation.GetRunway(airport, runway) is null)
                {
                    errors.Add($"runway configuration {configuration.Name}: {airport} has no runway {runway}");
                }
            }
        }
    }

    private static void ValidateSelection(List<string> errors, FacilityOps ops, RunwaySelectionPolicy selection, NavigationDatabase navigation)
    {
        RequireText(errors, selection.Source, "runwaySelection.source");
        if (selection.CalmWindBelowKt <= 0)
        {
            errors.Add("runwaySelection.calmWindBelowKt must be positive");
        }

        RequireConfiguration(errors, ops, selection.CalmConfiguration, "runwaySelection.calmConfiguration");
        if (selection.WindAlignedCandidates.Count == 0)
        {
            errors.Add("runwaySelection.windAlignedCandidates is empty");
        }

        foreach (var candidate in selection.WindAlignedCandidates)
        {
            RequireConfiguration(errors, ops, candidate, "runwaySelection.windAlignedCandidates[]");
        }

        foreach (var coupling in selection.PartnerCouplings)
        {
            RequireText(errors, coupling.Source, "runwaySelection.partnerCouplings[].source");
            if (!navigation.TryResolveAirport(coupling.PartnerAirportId, out _))
            {
                errors.Add($"partner coupling: airport {coupling.PartnerAirportId} is not in navdata");
            }

            RequireConfiguration(errors, ops, coupling.UseConfiguration, "runwaySelection.partnerCouplings[].useConfiguration");
            if (ops.Configuration(coupling.UseConfiguration)?.RunwaysAt(coupling.PartnerAirportId) is null)
            {
                errors.Add($"partner coupling: configuration {coupling.UseConfiguration} names no runways at {coupling.PartnerAirportId}");
            }
        }
    }

    private static void ValidateAssignmentRule(List<string> errors, FacilityOps ops, RunwayAssignmentRule rule, NavigationDatabase navigation)
    {
        RequireText(errors, rule.Id, "runwayAssignmentPolicy[].id");
        RequireText(errors, rule.Source, $"runwayAssignmentPolicy[{rule.Id}].source");
        if (rule.Runways.Count == 0)
        {
            errors.Add($"runway assignment rule {rule.Id} names no runway");
        }

        foreach (var runway in rule.Runways)
        {
            if (navigation.GetRunway(ops.AirportId, runway) is null)
            {
                errors.Add($"runway assignment rule {rule.Id}: {ops.AirportId} has no runway {runway}");
            }
        }

        if (rule.Applies.IsEmpty)
        {
            errors.Add($"runway assignment rule {rule.Id} applies to nothing (empty predicate)");
        }
    }

    private static void RequireConfiguration(List<string> errors, FacilityOps ops, string name, string where)
    {
        if (ops.Configuration(name) is null)
        {
            errors.Add($"{where}: configuration {name} is not declared");
        }
    }

    private static void RequireText(List<string> errors, string? value, string where)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{where} is missing");
        }
    }
}
