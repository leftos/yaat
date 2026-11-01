using Yaat.Sim.Speech.Context;

namespace Yaat.Client.Services.Speech;

/// <summary>How an STT engine's hypotheses become the context decoder's input.</summary>
public static class SttDecoderInput
{
    /// <summary>
    /// A recognizer hypothesis as the decoder takes it: a sherpa piece's leading space becomes the <c>▁</c> word mark, and an
    /// entry without tokens has none.
    /// </summary>
    /// <exception cref="InvalidDataException">The entry's tokens and log-probabilities differ in count.</exception>
    public static DecoderHypothesis ToDecoderHypothesis(SttHypothesis entry)
    {
        if (entry.Tokens.Count != entry.TokenLogProbs.Count)
        {
            throw new InvalidDataException(
                $"A hypothesis has {entry.Tokens.Count} tokens but {entry.TokenLogProbs.Count} token log-probabilities: '{entry.Text}'."
            );
        }
        if (entry.Tokens.Count == 0)
        {
            return new DecoderHypothesis(entry.Text, null, entry.Score);
        }
        TokenLogProb[] tokens = [.. entry.Tokens.Select((piece, i) => new TokenLogProb(WordMarked(piece), entry.TokenLogProbs[i]))];
        return new DecoderHypothesis(entry.Text, tokens, entry.Score);
    }

    private static string WordMarked(string piece) => piece.StartsWith(' ') ? "▁" + piece[1..] : piece;
}
