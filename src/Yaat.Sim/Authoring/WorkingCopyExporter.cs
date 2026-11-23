using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Authoring;

/// <summary>
/// Turns a working copy's text into the vNAS JSON an author uploads to ATCTrainer. The full export is the copy as the
/// scenario document writes it, every field kept; the clean export drops every field YAAT does not model and the
/// YAAT-only parts (VFR arrival and overflight generators, <c>liveSession</c>, a generator's <c>enabled</c>, an aircraft's
/// <see cref="AircraftTaxiSecondsKey"/> and <see cref="AircraftRpoNoteKey"/>). Both name each YAAT-only part they found, and
/// a copy whose checks report an error is refused.
/// </summary>
public static class WorkingCopyExporter
{
    /// <summary>
    /// The aircraft extension-data key of the Scenario Editor's taxi time, a whole number of seconds: YAAT-only, so vNAS
    /// ignores it and the clean export drops it.
    /// </summary>
    public const string AircraftTaxiSecondsKey = "yaatTaxiSeconds";

    /// <summary>
    /// The aircraft extension-data key of the Scenario Editor's RPO note, a string: YAAT-only, so vNAS ignores it and the
    /// clean export drops it.
    /// </summary>
    public const string AircraftRpoNoteKey = "yaatRpoNote";

    private const string VfrArrivalGeneratorKind = "vfrArrivalGenerator";
    private const string OverflightGeneratorKind = "overflightGenerator";
    private const string LiveSessionKind = "liveSession";
    private const string GeneratorEnabledKind = "generatorEnabled";
    private const string AircraftYaatFieldKind = "aircraftYaatField";

    private static readonly ILogger Log = SimLog.CreateLogger("WorkingCopyExporter");

    // The scenario document's own write options: indented, default escaping.
    private static readonly JsonSerializerOptions NodeWriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Prepares an export of a working copy's text without touching any file: reads it, runs
    /// <paramref name="checks"/> on it, and writes it as the export kind asks.
    /// </summary>
    /// <param name="workingCopyText">The working copy's stored text.</param>
    /// <param name="kind">Which export to make.</param>
    /// <param name="checks">The checks run before the export; an error among their issues refuses it.</param>
    /// <returns>
    /// <see cref="ExportOutcome.Written"/> with the JSON to write, <see cref="ExportOutcome.Refused"/> when a check
    /// reported an error, or <see cref="ExportOutcome.Corrupt"/> when the text is not a readable scenario.
    /// </returns>
    public static ExportResult Prepare(string workingCopyText, ExportKind kind, Func<Scenario, IReadOnlyList<ExportIssue>> checks)
    {
        ArgumentNullException.ThrowIfNull(workingCopyText);
        ArgumentNullException.ThrowIfNull(checks);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an export kind.");
        }

        if (!TryRead(workingCopyText, out ScenarioDocument? document, out string? reason))
        {
            return new ExportResult(ExportOutcome.Corrupt, null, [], [new ExportIssue(ExportIssueSeverity.Error, reason)]);
        }

        IReadOnlyList<YaatOnlyPart> parts = FindYaatOnlyParts(document.Scenario);
        IReadOnlyList<ExportIssue> issues = checks(document.Scenario);
        if (issues.Any(i => i.Severity == ExportIssueSeverity.Error))
        {
            LogRefused(document.Scenario, issues);
            return new ExportResult(ExportOutcome.Refused, null, parts, issues);
        }

        string json = (kind == ExportKind.Clean) ? WriteClean(document) : document.Write();
        return new ExportResult(ExportOutcome.Written, ToLf(json), parts, issues);
    }

    /// <summary>Writes a prepared export to <paramref name="path"/> in one step, UTF-8 without a BOM, LF line ends.</summary>
    /// <param name="result">A <see cref="ExportOutcome.Written"/> result of <see cref="Prepare"/>.</param>
    /// <param name="path">The file to create or replace; its folder is created when it is missing.</param>
    /// <exception cref="InvalidOperationException">The result was refused or corrupt; nothing is written.</exception>
    public static void WriteTo(ExportResult result, string path)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if ((result.Outcome != ExportOutcome.Written) || (result.Json is null))
        {
            throw new InvalidOperationException($"An export that is {result.Outcome} has nothing to write; {path} was not written.");
        }

        StoreFiles.WriteAtomically(path, result.Json);
        if (Log.IsEnabled(LogLevel.Information))
        {
            Log.LogInformation("Exported a scenario to {Path}", path);
        }
    }

    /// <summary>
    /// The checks an export runs until the editor's live checks replace them: <see cref="ScenarioValidator"/>'s preset
    /// command parse failures and procedures not found are errors; a changed procedure version, a substituted transition
    /// fix and an aircraft type that differs from the filed one are warnings.
    /// </summary>
    /// <param name="scenario">The scenario to check.</param>
    /// <returns>One issue per finding.</returns>
    public static IReadOnlyList<ExportIssue> DefaultChecks(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ScenarioValidationResult result = ScenarioValidator.Validate(scenario);
        var issues = new List<ExportIssue>();
        issues.AddRange(
            result.Failures.Select(f => Error($"{f.AircraftId}: the preset command \"{f.Command}\" does not parse ({f.Reason ?? "no reason"})"))
        );
        issues.AddRange(result.ProcedureIssues.Select(ProcedureIssueOf));
        issues.AddRange(result.TransitionFixSubstitutions.Select(TransitionIssueOf));
        issues.AddRange(
            result.AircraftTypeMismatches.Select(m =>
                Warning($"{m.AircraftId}: the aircraft type {m.ActualType} differs from the filed type {m.FiledType}")
            )
        );
        return issues;
    }

    /// <summary>The warning naming each YAAT-only part an export kept or dropped; null when it found none.</summary>
    /// <param name="kind">The export kind: a full export keeps the parts, a clean one drops them.</param>
    /// <param name="parts">The parts the export found.</param>
    /// <returns>The warning text, or null.</returns>
    public static string? DescribeParts(ExportKind kind, IReadOnlyList<YaatOnlyPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
        {
            return null;
        }

        string verb = (kind == ExportKind.Clean) ? "Dropped" : "Kept";
        return $"{verb} YAAT-only parts: {string.Join(", ", parts.Select(DescribePart))}.";
    }

    private static string DescribePart(YaatOnlyPart part) =>
        part.Kind switch
        {
            VfrArrivalGeneratorKind => $"VFR arrival generator {part.Name}",
            OverflightGeneratorKind => $"overflight generator {part.Name}",
            LiveSessionKind => "live session flag",
            GeneratorEnabledKind => $"enabled flag of aircraft generator {part.Name}",
            AircraftYaatFieldKind => part.Name,
            _ => $"{part.Kind} {part.Name}",
        };

    // A copy the store lists corrupt (not an object with a text id and name) is corrupt here too, with the store's reason.
    private static bool TryRead(string text, [NotNullWhen(true)] out ScenarioDocument? document, [NotNullWhen(false)] out string? reason)
    {
        document = null;
        reason = LocalScenarioStore.CheckScenario(text, out _);
        if (reason is not null)
        {
            LogUnreadable(reason);
            return false;
        }

        try
        {
            document = ScenarioDocument.Read(text);
            reason = null;
            return true;
        }
        catch (JsonException ex)
        {
            reason = "The working copy is not a readable scenario: " + ex.Message;
            LogUnreadable(reason);
            return false;
        }
    }

    private static void LogUnreadable(string reason)
    {
        if (Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning("A working copy could not be exported: {Reason}", reason);
        }
    }

    private static List<YaatOnlyPart> FindYaatOnlyParts(Scenario scenario)
    {
        var parts = new List<YaatOnlyPart>();
        AddParts(parts, VfrArrivalGeneratorKind, scenario.VfrArrivalGenerators ?? [], _ => true);
        AddParts(parts, OverflightGeneratorKind, scenario.OverflightGenerators ?? [], _ => true);
        if (scenario.LiveSession)
        {
            parts.Add(new YaatOnlyPart(LiveSessionKind, "liveSession"));
        }

        AddParts(parts, GeneratorEnabledKind, scenario.AircraftGenerators ?? [], g => g.Enabled is not null);
        AddAircraftParts(parts, scenario.Aircraft ?? []);
        return parts;
    }

    // One part per YAAT-only key an aircraft's extension data holds, named by the aircraft's callsign or, when the callsign
    // is empty, by "#" and its 1-based position, then the field.
    private static void AddAircraftParts(List<YaatOnlyPart> parts, List<ScenarioAircraft> aircraft)
    {
        for (int i = 0; i < aircraft.Count; i++)
        {
            ScenarioAircraft? one = aircraft[i];
            if (one?.ExtensionData is not { } extension)
            {
                continue;
            }

            string name = string.IsNullOrEmpty(one.AircraftId) ? $"#{i + 1}" : one.AircraftId;
            if (extension.ContainsKey(AircraftTaxiSecondsKey))
            {
                parts.Add(new YaatOnlyPart(AircraftYaatFieldKind, $"{name} taxi time"));
            }

            if (extension.ContainsKey(AircraftRpoNoteKey))
            {
                parts.Add(new YaatOnlyPart(AircraftYaatFieldKind, $"{name} RPO note"));
            }
        }
    }

    // One part per generator the filter takes, named by its id or, when the id is empty, by "#" and its 1-based position.
    private static void AddParts<T>(List<YaatOnlyPart> parts, string kind, List<T> generators, Func<T, bool> include)
        where T : IGeneratorConfig
    {
        for (int i = 0; i < generators.Count; i++)
        {
            T generator = generators[i];
            if ((generator is not null) && include(generator))
            {
                parts.Add(new YaatOnlyPart(kind, string.IsNullOrEmpty(generator.Id) ? $"#{i + 1}" : generator.Id));
            }
        }
    }

    // Clears every model object's extension data, writes the document, then removes the YAAT-only keys from the JSON:
    // the presence rule still writes a key the source held, an empty array included.
    private static string WriteClean(ScenarioDocument document)
    {
        ClearExtensionData(document.Scenario, new HashSet<object>(ReferenceEqualityComparer.Instance));
        JsonObject root = JsonNode.Parse(document.Write())!.AsObject();
        root.Remove("vfrArrivalGenerators");
        root.Remove("overflightGenerators");
        root.Remove("liveSession");
        if (root["aircraftGenerators"] is JsonArray generators)
        {
            foreach (JsonObject generator in generators.OfType<JsonObject>())
            {
                generator.Remove("enabled");
            }
        }

        return root.ToJsonString(NodeWriteOptions);
    }

    private static void ClearExtensionData(object model, HashSet<object> visited)
    {
        if (!visited.Add(model))
        {
            return;
        }

        foreach (PropertyInfo property in model.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if ((property.GetIndexParameters().Length > 0) || (property.GetGetMethod() is null))
            {
                continue;
            }

            if (property.IsDefined(typeof(JsonExtensionDataAttribute)))
            {
                property.SetValue(model, null);
                continue;
            }

            ClearValue(property.GetValue(model), visited);
        }
    }

    // A model object is cleared; every item of a collection is walked in turn, a dictionary entry by its value, so a
    // model object nested in lists or dictionaries is reached.
    private static void ClearValue(object? value, HashSet<object> visited)
    {
        if (IsModel(value))
        {
            ClearExtensionData(value, visited);
            return;
        }

        if (EntryValue(value) is { } entryValue)
        {
            ClearValue(entryValue, visited);
            return;
        }

        if ((value is IEnumerable items) && (value is not string))
        {
            foreach (object? item in items)
            {
                ClearValue(item, visited);
            }
        }
    }

    // The value of a boxed KeyValuePair (a dictionary's item); null for anything else.
    private static object? EntryValue(object? value)
    {
        Type? type = value?.GetType();
        bool isEntry = (type is not null) && type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>));
        return isEntry ? type!.GetProperty(nameof(KeyValuePair<,>.Value))!.GetValue(value) : null;
    }

    // A scenario model object: a class of the scenario model's namespace.
    private static bool IsModel([NotNullWhen(true)] object? value) =>
        (value is not null) && value.GetType().IsClass && (value.GetType().Namespace == typeof(Scenario).Namespace);

    /// <summary>
    /// The text with every CRLF line end made LF. The writer ends lines with the platform's newline; an exported file
    /// always uses LF. A line end inside a string value is escaped, so only the layout's line ends change.
    /// </summary>
    /// <param name="json">Written JSON.</param>
    /// <returns>The JSON with LF line ends.</returns>
    internal static string ToLf(string json) => json.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static ExportIssue ProcedureIssueOf(ProcedureIssue issue) =>
        (issue.Kind == ProcedureIssueKind.NotFound)
            ? Error($"{issue.AircraftId}: the procedure {issue.ProcedureId} is not in the navigation data")
            : Warning($"{issue.AircraftId}: the procedure {issue.ProcedureId} is now {issue.ResolvedId}");

    private static ExportIssue TransitionIssueOf(TransitionFixSubstitution sub) =>
        Warning(
            (sub.NewFix is null)
                ? $"{sub.AircraftId}: {sub.OldFix} is not on {sub.ProcedureId} and no transition fix replaces it"
                : $"{sub.AircraftId}: {sub.OldFix} is not on {sub.ProcedureId}; the nearest transition fix is {sub.NewFix}"
        );

    private static ExportIssue Error(string message) => new(ExportIssueSeverity.Error, message);

    private static ExportIssue Warning(string message) => new(ExportIssueSeverity.Warning, message);

    private static void LogRefused(Scenario scenario, IReadOnlyList<ExportIssue> issues)
    {
        if (Log.IsEnabled(LogLevel.Information))
        {
            Log.LogInformation(
                "Export of scenario {Id} refused: {Count} error(s)",
                scenario.Id,
                issues.Count(i => i.Severity == ExportIssueSeverity.Error)
            );
        }
    }
}

/// <summary>Which export of a working copy to make.</summary>
public enum ExportKind
{
    /// <summary>The vNAS JSON with every field kept, YAAT-only parts included.</summary>
    Full,

    /// <summary>The vNAS JSON without the fields YAAT does not model and without the YAAT-only parts.</summary>
    Clean,
}

/// <summary>What preparing an export found.</summary>
public enum ExportOutcome
{
    /// <summary>The export is ready to write; any issues are warnings.</summary>
    Written,

    /// <summary>A check reported an error; there is nothing to write.</summary>
    Refused,

    /// <summary>The working copy's text is not a readable scenario; there is nothing to write.</summary>
    Corrupt,
}

/// <summary>How serious an export issue is.</summary>
public enum ExportIssueSeverity
{
    /// <summary>Refuses the export.</summary>
    Error,

    /// <summary>Shown with the export, which is still written.</summary>
    Warning,
}

/// <summary>One finding of an export's checks.</summary>
/// <param name="Severity">Whether it refuses the export.</param>
/// <param name="Message">What is wrong, naming the aircraft where there is one.</param>
public sealed record ExportIssue(ExportIssueSeverity Severity, string Message);

/// <summary>One part of a scenario only YAAT reads, found by an export.</summary>
/// <param name="Kind">
/// <c>vfrArrivalGenerator</c>, <c>overflightGenerator</c>, <c>liveSession</c>, <c>generatorEnabled</c> (an aircraft
/// generator's <c>enabled</c> flag) or <c>aircraftYaatField</c> (an aircraft's taxi time or RPO note).
/// </param>
/// <param name="Name">
/// The generator's id, or <c>#</c> and its 1-based position in its list when the id is empty (<c>#1</c>);
/// <c>liveSession</c>; or the aircraft's callsign, or <c>#</c> and its 1-based position when the callsign is empty, then
/// its field (<c>AAL1 taxi time</c>, <c>#2 RPO note</c>).
/// </param>
public sealed record YaatOnlyPart(string Kind, string Name);

/// <summary>The outcome of <see cref="WorkingCopyExporter.Prepare"/>.</summary>
/// <param name="Outcome">Whether the export can be written.</param>
/// <param name="Json">The JSON to write when <see cref="ExportOutcome.Written"/>; null otherwise.</param>
/// <param name="Parts">Each YAAT-only part found: kept by a full export, dropped by a clean one.</param>
/// <param name="Issues">The checks' findings, or the one reason a corrupt copy could not be read.</param>
public sealed record ExportResult(ExportOutcome Outcome, string? Json, IReadOnlyList<YaatOnlyPart> Parts, IReadOnlyList<ExportIssue> Issues);
