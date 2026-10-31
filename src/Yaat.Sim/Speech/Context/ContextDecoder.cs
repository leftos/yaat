using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Speech.Context;

/// <summary>
/// Decodes a transmission against its context: picks the addressed callsign, the entities named around it, and a transcript
/// with the near misses of those matches rewritten. The callsign and the entities are chosen jointly, so what the rest of a
/// transmission says about each candidate aircraft can decide between close callsigns.
/// </summary>
public static partial class ContextDecoder
{
    private const string CorrectionWord = "correction";

    private static readonly ILogger Log = SimLog.CreateLogger("ContextDecoder");

    /// <summary>Decodes each hypothesis on its own and returns the best resolved one, or the first hypothesis's result if none resolved.</summary>
    /// <param name="input">The hypotheses, best first, with where they came from and how strictly to match them.</param>
    /// <param name="context">The room's aircraft and the airport's entities.</param>
    /// <param name="settings">The thresholds and weights to decode under.</param>
    /// <returns>The decoded transmission.</returns>
    /// <exception cref="ArgumentException"><paramref name="input"/> holds no hypothesis.</exception>
    public static DecodeResult Decode(DecoderInput input, TransmissionContext context, DecoderSettings settings)
    {
        if (input.Hypotheses.Count == 0)
        {
            throw new ArgumentException(
                "Decoding needs at least one hypothesis; a recogniser that heard nothing has nothing to decode.",
                nameof(input)
            );
        }

        var call = new DecodeCall(context, settings);
        var outcomes = new List<DecodeResult>(input.Hypotheses.Count);
        for (int i = 0; i < input.Hypotheses.Count; i++)
        {
            outcomes.Add(DecodeHypothesis(input.Hypotheses[i], i, input, call));
        }

        DecodeResult chosen = ChooseHypothesis(input.Hypotheses, outcomes, settings);
        Log.LogDebug(
            "Decoded hypothesis {Index}: {State}, callsign {Callsign}, joint score {Joint}",
            chosen.HypothesisIndex,
            chosen.State,
            chosen.Callsign?.Entity.Value,
            chosen.JointScore
        );
        return chosen;
    }

    private static DecodeResult ChooseHypothesis(IReadOnlyList<DecoderHypothesis> hypotheses, List<DecodeResult> outcomes, DecoderSettings settings)
    {
        bool allAcoustic = hypotheses.All(hypothesis => hypothesis.AcousticScore.HasValue);
        DecodeResult? best = null;
        double bestCombined = double.NegativeInfinity;
        for (int i = 0; i < outcomes.Count; i++)
        {
            if (outcomes[i].State != DecodeState.Resolved)
            {
                continue;
            }

            double combined = Math.Log(outcomes[i].JointScore) + RankTerm(hypotheses, i, allAcoustic, settings);
            if ((best is null) || (combined > bestCombined))
            {
                best = outcomes[i];
                bestCombined = combined;
            }
        }
        return best ?? outcomes[0];
    }

    /// <summary>A hypothesis's acoustic gain on the first one or, when any hypothesis lacks an acoustic score, a penalty per rank.</summary>
    private static double RankTerm(IReadOnlyList<DecoderHypothesis> hypotheses, int index, bool allAcoustic, DecoderSettings settings)
    {
        if (allAcoustic && (hypotheses[index].AcousticScore is { } score) && (hypotheses[0].AcousticScore is { } first))
        {
            return settings.AcousticWeight * (score - first);
        }
        return -settings.MissingAcousticRankPenalty * index;
    }

    private static DecodeResult DecodeHypothesis(DecoderHypothesis hypothesis, int index, DecoderInput input, DecodeCall call)
    {
        bool typed = input.Source == InputSource.Typed;
        double[]? confidence = typed ? null : WordConfidences(hypothesis);
        var heard = new HeardText(hypothesis.Text, typed ? MatchMode.ExactOnly : input.Mode, confidence, typed, call.Settings);
        var view = new HypothesisView(heard, hypothesis.Text, index, typed);

        CallsignDecision? decision = DecideCallsign(heard, FindCallsignCandidates(heard, call), call);
        DecodeState state = StateOf(decision?.Hit, call.Settings);
        if ((decision is null) || (state == DecodeState.NoCallsign))
        {
            return UnresolvedResult(view, DecodeState.NoCallsign, null, call);
        }

        if (SecondCallsignResult(view, decision, call) is { } second)
        {
            return second;
        }
        return state == DecodeState.Resolved ? ResolvedResult(view, decision, call) : UnresolvedResult(view, state, decision.Hit, call);
    }

    private static DecodeResult ResolvedResult(HypothesisView view, CallsignDecision decision, DecodeCall call)
    {
        CallsignHit hit = decision.Hit;
        return new DecodeResult(
            DecodeState.Resolved,
            view.Heard.Match(hit.Aircraft.Callsign, hit.Full, hit.Weighted, own: true),
            null,
            EntitiesOf(view, decision.Selection, hit, CandidateSources(hit.Aircraft, call)),
            Transcript(view, hit, decision.Selection, call),
            view.Index,
            decision.Joint,
            view.Heard.ConfidenceGated
        );
    }

    /// <summary>A hit that did not resolve claims no tokens unless it is a say-again's best guess.</summary>
    private static DecodeResult UnresolvedResult(HypothesisView view, DecodeState state, CallsignHit? hit, DecodeCall call)
    {
        CallsignHit? bestGuess = state == DecodeState.SayAgain ? hit : null;
        EntitySelection selection = SelectAirportEntities(view.Heard, bestGuess, call);
        return new DecodeResult(
            state,
            null,
            bestGuess is null ? null : view.Heard.Match(bestGuess.Aircraft.Callsign, bestGuess.Full, bestGuess.Weighted, own: true),
            EntitiesOf(view, selection, bestGuess, AirportSources(call)),
            Transcript(view, null, selection, call),
            view.Index,
            0.0,
            view.Heard.ConfidenceGated
        );
    }

    private static List<EntityMatch> EntitiesOf(HypothesisView view, EntitySelection selection, CallsignHit? callsign, List<EntitySource> sources)
    {
        List<EntityMatch> entities =
        [
            .. selection.Chosen.Select(span => view.Heard.Match(span.Source.Entity, span.Range, span.Weighted, span.Source.Own)),
        ];
        return view.Typed ? AddExactValueMatches(view.Heard, entities, callsign, sources) : entities;
    }

    private static string Transcript(HypothesisView view, CallsignHit? callsign, EntitySelection selection, DecodeCall call) =>
        view.Typed ? view.Text : Rewrite(view.Heard, callsign, selection.Chosen, call);

    private static DecodeState StateOf(CallsignHit? hit, DecoderSettings settings)
    {
        if (hit is null)
        {
            return DecodeState.NoCallsign;
        }
        if (hit.Raw >= settings.CallsignAccept)
        {
            return DecodeState.Resolved;
        }
        return hit.Raw >= settings.CallsignSayAgainFloor ? DecodeState.SayAgain : DecodeState.NoCallsign;
    }

    /// <summary>The confidence of each whitespace word of the hypothesis, or null when its pieces do not line up with its words.</summary>
    private static double[]? WordConfidences(DecoderHypothesis hypothesis)
    {
        if (hypothesis.Tokens is null)
        {
            return null;
        }

        var logProbs = new List<double>();
        foreach (TokenLogProb piece in hypothesis.Tokens)
        {
            if ((logProbs.Count == 0) || piece.Piece.StartsWith('▁'))
            {
                logProbs.Add(0.0);
            }
            logProbs[^1] += piece.LogProb;
        }

        int wordCount = hypothesis.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return logProbs.Count == wordCount ? [.. logProbs.Select(Math.Exp)] : null;
    }
}
