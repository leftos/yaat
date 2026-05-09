using System.Text.Json;

namespace Yaat.Sim.Data;

public sealed class InitialContactTransferLoadResult
{
    public List<InitialContactTransferRule> Rules { get; } = [];
    public List<string> Warnings { get; } = [];
}

public static class InitialContactTransferLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Scans <c>{artccsBaseDir}/{ARTCC}/InitialContactTransfers/*.json</c> across every
    /// ARTCC subdirectory and loads facility-specific radio-transfer exceptions.
    /// </summary>
    public static InitialContactTransferLoadResult LoadAll(string artccsBaseDir)
    {
        var result = new InitialContactTransferLoadResult();

        if (!Directory.Exists(artccsBaseDir))
        {
            result.Warnings.Add($"ARTCCs directory not found: {artccsBaseDir}");
            return result;
        }

        foreach (var artccDir in Directory.EnumerateDirectories(artccsBaseDir))
        {
            string categoryDir = Path.Combine(artccDir, "InitialContactTransfers");
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

    private static void LoadFile(string filePath, string artccId, InitialContactTransferLoadResult result)
    {
        List<InitialContactTransferRule>? rules;
        try
        {
            var json = File.ReadAllText(filePath);
            rules = JsonSerializer.Deserialize<List<InitialContactTransferRule>>(json, JsonOptions);
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

        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var location = $"{filePath}[{i}]";

            if (string.IsNullOrWhiteSpace(rule.FromPositionType) && string.IsNullOrWhiteSpace(rule.FromCallsign))
            {
                result.Warnings.Add($"{location}: missing fromPositionType/fromCallsign, skipping");
                continue;
            }

            if (string.IsNullOrWhiteSpace(rule.ToPositionType) && string.IsNullOrWhiteSpace(rule.ToCallsign))
            {
                result.Warnings.Add($"{location}: missing toPositionType/toCallsign, skipping");
                continue;
            }

            if (!TryResolveTiming(rule, out var timing))
            {
                result.Warnings.Add($"{location}: invalid or missing contactAllowedWhen, skipping");
                continue;
            }

            rule.ArtccId = artccId;
            rule.AirportId = !string.IsNullOrWhiteSpace(rule.AirportId) ? NavigationDatabase.NormalizeAirport(rule.AirportId) : null;
            rule.FromCallsign = NormalizeOptionalCallsign(rule.FromCallsign);
            rule.FromPositionType = NormalizeOptionalPositionType(rule.FromPositionType);
            rule.ToCallsign = NormalizeOptionalCallsign(rule.ToCallsign);
            rule.ToPositionType = NormalizeOptionalPositionType(rule.ToPositionType);
            rule.Timing = timing;
            rule.ContactAllowedWhen = NormalizeTimingName(timing);
            result.Rules.Add(rule);
        }
    }

    private static bool TryResolveTiming(InitialContactTransferRule rule, out InitialContactTransferTiming timing)
    {
        if (rule.AllowsWithoutTrackHandoff == true && string.IsNullOrWhiteSpace(rule.ContactAllowedWhen))
        {
            timing = InitialContactTransferTiming.NoHandoffNecessary;
            return true;
        }

        return TryParseTiming(rule.ContactAllowedWhen, out timing);
    }

    private static bool TryParseTiming(string? value, out InitialContactTransferTiming timing)
    {
        var normalized = value?.Trim().Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToUpperInvariant();
        switch (normalized)
        {
            case "HANDOFFINITIATED":
            case "ONHANDOFFINITIATED":
                timing = InitialContactTransferTiming.HandoffInitiated;
                return true;
            case "HANDOFFACCEPTED":
            case "ONHANDOFFACCEPTED":
                timing = InitialContactTransferTiming.HandoffAccepted;
                return true;
            case "NOHANDOFF":
            case "NOHANDOFFNECESSARY":
            case "WITHOUTHANDOFF":
                timing = InitialContactTransferTiming.NoHandoffNecessary;
                return true;
            default:
                timing = default;
                return false;
        }
    }

    private static string NormalizeTimingName(InitialContactTransferTiming timing) =>
        timing switch
        {
            InitialContactTransferTiming.HandoffInitiated => "handoffInitiated",
            InitialContactTransferTiming.HandoffAccepted => "handoffAccepted",
            InitialContactTransferTiming.NoHandoffNecessary => "noHandoffNecessary",
            _ => "",
        };

    private static string? NormalizeOptionalCallsign(string? value) => !string.IsNullOrWhiteSpace(value) ? value.Trim().ToUpperInvariant() : null;

    private static string? NormalizeOptionalPositionType(string? value) => !string.IsNullOrWhiteSpace(value) ? NormalizePositionType(value) : null;

    private static string NormalizePositionType(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "LC" => "TWR",
            "GC" => "GND",
            "DEL" => "GND",
            "DEP" => "APP",
            var normalized => normalized,
        };
}
