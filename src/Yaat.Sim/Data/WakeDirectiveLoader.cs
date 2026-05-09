using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yaat.Sim.Data;

public sealed class WakeDirectiveLoadResult
{
    public List<WakeDirectiveRule> Rules { get; } = [];
    public List<string> Warnings { get; } = [];
}

public static class WakeDirectiveLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Scans <c>{artccsBaseDir}/{ARTCC}/WakeDirectives/*.json</c> across every
    /// ARTCC subdirectory and loads facility-specific wake scoring directives.
    /// </summary>
    public static WakeDirectiveLoadResult LoadAll(string artccsBaseDir)
    {
        var result = new WakeDirectiveLoadResult();

        if (!Directory.Exists(artccsBaseDir))
        {
            result.Warnings.Add($"ARTCCs directory not found: {artccsBaseDir}");
            return result;
        }

        foreach (var artccDir in Directory.EnumerateDirectories(artccsBaseDir))
        {
            string categoryDir = Path.Combine(artccDir, "WakeDirectives");
            if (!Directory.Exists(categoryDir))
            {
                continue;
            }

            var artccId = Path.GetFileName(artccDir).Trim().ToUpperInvariant();
            foreach (var file in Directory.GetFiles(categoryDir, "*.json"))
            {
                LoadFile(file, artccId, result);
            }
        }

        return result;
    }

    private static void LoadFile(string filePath, string artccId, WakeDirectiveLoadResult result)
    {
        List<WakeDirectiveRuleDto>? rules;
        try
        {
            var json = File.ReadAllText(filePath);
            rules = JsonSerializer.Deserialize<List<WakeDirectiveRuleDto>>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Failed to parse {filePath}: {ex.Message}");
            return;
        }

        if (rules is null)
        {
            result.Warnings.Add($"Null result deserializing {filePath}");
            return;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rules.Count; i++)
        {
            var dto = rules[i];
            var location = $"{filePath}[{i}]";
            if (!TryNormalizeRule(dto, artccId, location, result.Warnings, out var rule))
            {
                continue;
            }

            if (!seenIds.Add(rule.Id))
            {
                result.Warnings.Add($"{location}: duplicate id '{rule.Id}', skipping");
                continue;
            }

            result.Rules.Add(rule);
        }
    }

    private static bool TryNormalizeRule(WakeDirectiveRuleDto dto, string artccId, string location, List<string> warnings, out WakeDirectiveRule rule)
    {
        rule = new WakeDirectiveRule();
        if (string.IsNullOrWhiteSpace(dto.Id))
        {
            warnings.Add($"{location}: missing id, skipping");
            return false;
        }

        if (!TryParseOperation(dto.Operation, location, warnings, out var operation))
        {
            return false;
        }

        if (!TryParseRelation(dto.Relation, location, warnings, out var relation))
        {
            return false;
        }

        if (!TryParseEffects(dto.Effects, location, warnings, out var effects))
        {
            return false;
        }

        if (!TryParseCwt(dto.PrecedingCwt, "precedingCwt", location, warnings, out var precedingCwt))
        {
            return false;
        }

        if (!TryParseCwt(dto.SucceedingCwt, "succeedingCwt", location, warnings, out var succeedingCwt))
        {
            return false;
        }

        rule = new WakeDirectiveRule
        {
            ArtccId = artccId,
            Id = dto.Id.Trim(),
            AirportId = !string.IsNullOrWhiteSpace(dto.AirportId) ? NavigationDatabase.NormalizeAirport(dto.AirportId) : null,
            Runways = dto
                .Runways.Select(NormalizeRunway)
                .Where(static runway => runway.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Operation = operation,
            Relation = relation,
            PrecedingCwt = precedingCwt,
            SucceedingCwt = succeedingCwt,
            SourceRuleReferences = dto.SourceRuleReferences.Select(static value => value.Trim()).Where(static value => value.Length > 0).ToList(),
            Effects = effects,
            RuleReference = !string.IsNullOrWhiteSpace(dto.RuleReference) ? dto.RuleReference.Trim() : null,
            Notes = !string.IsNullOrWhiteSpace(dto.Notes) ? dto.Notes.Trim() : null,
        };
        return true;
    }

    private static bool TryParseOperation(string? value, string location, List<string> warnings, out WakeDirectiveOperation operation)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            operation = WakeDirectiveOperation.Any;
            return true;
        }

        if (TryParseNormalizedEnum(value, out operation))
        {
            return true;
        }

        warnings.Add($"{location}: invalid operation '{value}', skipping");
        return false;
    }

    private static bool TryParseRelation(string? value, string location, List<string> warnings, out WakeDirectiveRelation relation)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            relation = WakeDirectiveRelation.Any;
            return true;
        }

        if (TryParseNormalizedEnum(value, out relation))
        {
            return true;
        }

        warnings.Add($"{location}: invalid relation '{value}', skipping");
        return false;
    }

    private static bool TryParseEffects(IReadOnlyList<string> values, string location, List<string> warnings, out List<WakeDirectiveEffect> effects)
    {
        effects = [];
        if (values.Count == 0)
        {
            warnings.Add($"{location}: missing effects, skipping");
            return false;
        }

        foreach (var value in values)
        {
            if (!TryParseNormalizedEnum(value, out WakeDirectiveEffect effect))
            {
                warnings.Add($"{location}: invalid effect '{value}', skipping");
                return false;
            }

            if (!effects.Contains(effect))
            {
                effects.Add(effect);
            }
        }

        return true;
    }

    private static bool TryParseCwt(IReadOnlyList<string> values, string fieldName, string location, List<string> warnings, out List<char> categories)
    {
        categories = [];
        foreach (var value in values)
        {
            string normalized = value.Trim().ToUpperInvariant();
            if (normalized.Length != 1 || normalized[0] is < 'A' or > 'I')
            {
                warnings.Add($"{location}: invalid {fieldName} '{value}', skipping");
                return false;
            }

            if (!categories.Contains(normalized[0]))
            {
                categories.Add(normalized[0]);
            }
        }

        return true;
    }

    private static bool TryParseNormalizedEnum<TEnum>(string value, out TEnum result)
        where TEnum : struct, Enum
    {
        string normalized = value.Trim().Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (name.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                result = Enum.Parse<TEnum>(name);
                return true;
            }
        }

        result = default;
        return false;
    }

    private static string NormalizeRunway(string value) => value.Trim().ToUpperInvariant().Replace("RWY", "", StringComparison.Ordinal).Trim();

    private sealed class WakeDirectiveRuleDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("airportId")]
        public string? AirportId { get; set; }

        [JsonPropertyName("runways")]
        public List<string> Runways { get; set; } = [];

        [JsonPropertyName("operation")]
        public string? Operation { get; set; }

        [JsonPropertyName("relation")]
        public string? Relation { get; set; }

        [JsonPropertyName("precedingCwt")]
        public List<string> PrecedingCwt { get; set; } = [];

        [JsonPropertyName("succeedingCwt")]
        public List<string> SucceedingCwt { get; set; } = [];

        [JsonPropertyName("sourceRuleReferences")]
        public List<string> SourceRuleReferences { get; set; } = [];

        [JsonPropertyName("effects")]
        public List<string> Effects { get; set; } = [];

        [JsonPropertyName("ruleReference")]
        public string? RuleReference { get; set; }

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }
    }
}
