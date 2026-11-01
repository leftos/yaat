namespace Yaat.Client.Services.Speech;

/// <summary>The sherpa transducer's two decoders.</summary>
public enum SherpaDecodingMethod
{
    Greedy,
    Beam,
}

/// <summary>How a sherpa transducer decodes a run.</summary>
/// <param name="Method">Greedy search, or modified beam search.</param>
/// <param name="NumPaths">Hypotheses a beam stream returns, 1 through <see cref="MaxBeamPaths"/>; 1 for greedy.</param>
/// <param name="HotwordScore">The recognizer's hotword score; one per run, since the decoder reads it from the config.</param>
public sealed record SherpaDecoding(SherpaDecodingMethod Method, int NumPaths, float HotwordScore)
{
    public const int MaxBeamPaths = 4;

    /// <summary>The hotword score a run uses when it names none.</summary>
    public const float DefaultHotwordScore = 1.5f;

    public static readonly SherpaDecoding Greedy = new(SherpaDecodingMethod.Greedy, 1, DefaultHotwordScore);
}

/// <summary>One decoded alternative of a clip.</summary>
/// <param name="Text">The transcript, trimmed.</param>
/// <param name="Tokens">The model's tokens; empty for an engine that reports none.</param>
/// <param name="TokenLogProbs">The tokens' log probabilities; empty for an engine that reports none.</param>
/// <param name="Score">The hypothesis's search score; null outside a beam search.</param>
public sealed record SttHypothesis(string Text, IReadOnlyList<string> Tokens, IReadOnlyList<double> TokenLogProbs, double? Score);

/// <summary>One engine's alternatives for a clip, best first, with the latency it measured and whether it came from a cache.</summary>
/// <param name="Entries">The alternatives; empty when the engine returned nothing.</param>
/// <param name="LatencyMs">
/// The engine's own latency in milliseconds; null means the caller's stopwatch around the call is the latency.
/// The streaming engine reports its own finalization time here.
/// </param>
/// <param name="Cached">True only for a cloud cache hit.</param>
public sealed record SttHypotheses(IReadOnlyList<SttHypothesis> Entries, double? LatencyMs, bool Cached)
{
    /// <summary>The best alternative's transcript; null when there is none or it is empty.</summary>
    public string? Text => ((Entries.Count > 0) && (Entries[0].Text.Length > 0)) ? Entries[0].Text : null;

    /// <summary>The result of an engine that returns one plain transcript: no entry for null or empty text.</summary>
    public static SttHypotheses Single(string? text, double? latencyMs, bool cached) =>
        new(string.IsNullOrEmpty(text) ? [] : [new SttHypothesis(text, [], [], null)], latencyMs, cached);
}
