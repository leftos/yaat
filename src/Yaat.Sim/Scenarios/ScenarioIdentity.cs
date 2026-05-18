using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yaat.Sim.Scenarios;

public static class ScenarioIdentity
{
    public static string ResolveScenarioId(string? scenarioId, string scenarioJson) =>
        !string.IsNullOrWhiteSpace(scenarioId) ? scenarioId : ComputeFallbackHash(scenarioJson);

    /// <summary>
    /// Resolves a scenario identity directly from the raw JSON: parses the optional
    /// <c>id</c> field and falls back to a hash of the entire JSON when absent. Used
    /// client-side by the Scenario Setup dialog to key per-scenario preferences without
    /// a server round-trip — produces the same id the server records on load.
    /// </summary>
    public static string ResolveFromJson(string scenarioJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(scenarioJson);
            if (
                doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("id", out var idElement)
                && idElement.ValueKind == JsonValueKind.String
            )
            {
                var id = idElement.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return id;
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to hash.
        }
        return ComputeFallbackHash(scenarioJson);
    }

    public static string Normalize(string scenarioId) => scenarioId.Trim().ToUpperInvariant();

    public static string ComputeFallbackHash(string json)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes)[..16];
    }
}
