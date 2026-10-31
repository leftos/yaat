using System.Text.Json;

namespace Yaat.SpeechSandbox;

/// <summary>Reads the JSON sherpa-onnx's C API returns for a decoded offline stream.</summary>
public static class SherpaResultJson
{
    private const string HypothesesKey = "hypotheses";

    /// <summary>
    /// The result's alternatives, best first: one per <c>hypotheses</c> element, or the top-level result alone
    /// where that array is empty or missing, or none when the top-level text is blank too.
    /// </summary>
    /// <param name="json">The result as the C API wrote it.</param>
    /// <exception cref="JsonException">The native wrote something that is not JSON.</exception>
    /// <exception cref="KeyNotFoundException">An entry lacks a key the writer always emits.</exception>
    public static IReadOnlyList<SttHypothesis> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty(HypothesesKey, out JsonElement hypotheses) && (hypotheses.GetArrayLength() > 0))
        {
            return [.. hypotheses.EnumerateArray().Select(h => ReadEntry(h, h.GetProperty("score").GetDouble()))];
        }

        SttHypothesis top = ReadEntry(root, null);
        return (top.Text.Length == 0) ? [] : [top];
    }

    /// <summary>True when the result has a <c>hypotheses</c> key, which only a native with n-best writes.</summary>
    /// <param name="json">The result as the C API wrote it.</param>
    public static bool CarriesNbest(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(HypothesesKey, out _);
    }

    private static SttHypothesis ReadEntry(JsonElement element, double? score)
    {
        string text = (element.GetProperty("text").GetString() ?? string.Empty).Trim();
        string[] tokens = [.. element.GetProperty("tokens").EnumerateArray().Select(t => t.GetString() ?? string.Empty)];
        double[] logProbs = [.. element.GetProperty("ys_log_probs").EnumerateArray().Select(p => p.GetDouble())];
        return new SttHypothesis(text, tokens, logProbs, score);
    }
}
