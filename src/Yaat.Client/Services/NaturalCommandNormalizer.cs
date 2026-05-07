using Yaat.Sim.Speech;

namespace Yaat.Client.Services;

internal sealed record NaturalCommandNormalization(string? Callsign, string CanonicalCommand, bool UsedLlmFallback)
{
    public string CommandText => string.IsNullOrWhiteSpace(Callsign) ? CanonicalCommand : $"{Callsign} {CanonicalCommand}";
}

internal static class NaturalCommandNormalizer
{
    public static async Task<NaturalCommandNormalization?> TryNormalizeAsync(
        string transcript,
        SpeechContext context,
        ISpeechCommandMapper ruleMapper,
        ISpeechCommandMapper? llmMapper,
        LocalLlmCallsignResolver? callsignResolver,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return null;
        }

        var result = await SpeechRecognitionService
            .MapTranscriptAsync(transcript, context, ruleMapper, llmMapper, callsignResolver, cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(result.Canonical)
            ? null
            : new NaturalCommandNormalization(result.Callsign, result.Canonical, result.UsedLlmFallback);
    }
}
