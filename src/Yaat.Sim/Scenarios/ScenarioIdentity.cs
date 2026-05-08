using System.Security.Cryptography;
using System.Text;

namespace Yaat.Sim.Scenarios;

public static class ScenarioIdentity
{
    public static string ResolveScenarioId(string? scenarioId, string scenarioJson) =>
        !string.IsNullOrWhiteSpace(scenarioId) ? scenarioId : ComputeFallbackHash(scenarioJson);

    public static string Normalize(string scenarioId) => scenarioId.Trim().ToUpperInvariant();

    public static string ComputeFallbackHash(string json)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes)[..16];
    }
}
