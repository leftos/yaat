namespace Yaat.Sim.Speech.Context;

public static partial class ContextDecoder
{
    private const int MinFlightDigitsBeforeTail = 3;

    private const int MinValueTailDigits = 3;

    private const int MinFlightDigitsBeforeLoneDigits = 4;

    /// <summary>A callsign candidate and where it was heard; <paramref name="Form"/> is null for a typed ICAO.</summary>
    private sealed record CallsignHit(RoomAircraft Aircraft, SpanPlace Place, SpokenForm? Form, SpanScore Score)
    {
        public TokenRange Core => Place.Core;

        public TokenRange Full => Place.Full;

        public double Raw => Score.Raw;

        public double Weighted => Score.Weighted;
    }

    private sealed record CallsignDecision(CallsignHit Hit, EntitySelection Selection, double Joint);

    /// <summary>
    /// One callsign pass; <paramref name="SeparatorOpens"/> marks a scan after a second-callsign word directly after the winner,
    /// which acts as an opener.
    /// </summary>
    private sealed record CallsignScan(HeardText Heard, DecodeCall Call, bool SeparatorOpens);

    private static List<CallsignHit> FindCallsignCandidates(HeardText heard, DecodeCall call)
    {
        if (heard.Typed && (ExactCallsignWord(heard, call) is { } exact))
        {
            return [exact];
        }

        DecoderSettings settings = call.Settings;
        List<CallsignHit> hits = HitsByAircraft(CallsignRanges(heard, settings), new CallsignScan(heard, call, SeparatorOpens: false));
        List<CallsignHit> leading = [.. hits.Where(hit => Leads(hit, settings) && !FollowsTrafficLeadIn(heard, hit))];
        return Ranked(leading.Count > 0 ? leading : TrailingCandidates(heard, hits, settings), settings);
    }

    /// <summary>
    /// The candidates left when none leads. An opening no candidate matched is another aircraft's call, so nothing later can be
    /// the addressee; a candidate refused as traffic still matched its opening.
    /// </summary>
    private static IEnumerable<CallsignHit> TrailingCandidates(HeardText heard, List<CallsignHit> hits, DecoderSettings settings)
    {
        bool startUnclaimed = !hits.Any(hit => Leads(hit, settings)) && OpensWithUnmatchedCall(heard, settings);
        return startUnclaimed ? [] : hits.Where(hit => !FollowsTrafficLeadIn(heard, hit));
    }

    private static bool OpensWithUnmatchedCall(HeardText heard, DecoderSettings settings) =>
        Enumerable.Range(0, settings.MaxStartOffset + 1).Any(index => OpensCallAt(heard.Tokens, index));

    private static bool OpensCallAt(IReadOnlyList<SpokenToken> tokens, int index) =>
        IsUnknownCallAt(tokens, index) && !IsCorrectionValueWord(tokens, index) && !((index > 0) && IsCorrectionValueWord(tokens, index - 1));

    private static List<CallsignHit> HitsByAircraft(List<TokenRange> ranges, CallsignScan scan)
    {
        var hits = new List<CallsignHit>();
        foreach (RoomAircraft aircraft in scan.Call.Context.Aircraft)
        {
            if (BestCallsignSpan(aircraft, ranges, scan) is { } best)
            {
                hits.Add(best);
            }
        }
        return hits;
    }

    /// <summary>Best first; the sort is stable, so a tie on score and length keeps room order.</summary>
    private static List<CallsignHit> Ranked(IEnumerable<CallsignHit> hits, DecoderSettings settings) =>
        [.. hits.OrderByDescending(hit => hit.Weighted).ThenByDescending(hit => hit.Core.Length).Take(settings.CallsignTopK)];

    /// <summary>The addressee is named first, so once a candidate leads, a later match can only be a value or traffic.</summary>
    private static bool Leads(CallsignHit hit, DecoderSettings settings) =>
        (hit.Core.Start <= settings.MaxStartOffset) && (hit.Raw >= settings.CallsignSayAgainFloor);

    private static bool FollowsTrafficLeadIn(HeardText heard, CallsignHit hit)
    {
        int before = hit.Core.Start - 1;
        while ((before >= 0) && heard.IsArticle(before))
        {
            before--;
        }
        return (before >= 0) && ContextPhraseWords.TrafficLeadIns.Contains(heard.Tokens[before].Text);
    }

    private static CallsignHit? ExactCallsignWord(HeardText heard, DecodeCall call)
    {
        for (int word = 0; word < heard.Words.Length; word++)
        {
            string upper = heard.Words[word].ToUpperInvariant();
            RoomAircraft? aircraft = call.Context.Aircraft.FirstOrDefault(candidate => candidate.Callsign.Value == upper);
            if ((aircraft is not null) && (heard.RangeOfWord(word) is { } range))
            {
                return new CallsignHit(aircraft, new SpanPlace(range, range), null, new SpanScore(1.0, 1.0));
            }
        }
        return null;
    }

    private static List<TokenRange> CallsignRanges(HeardText heard, DecoderSettings settings)
    {
        int tokenCount = heard.Tokens.Count;
        var ranges = new List<TokenRange>();
        for (int start = 0; start < tokenCount; start++)
        {
            int lastEnd = Math.Min(tokenCount, start + settings.MaxCallsignSpan);
            for (int end = start + 1; end <= lastEnd; end++)
            {
                var range = new TokenRange(start, end);
                if (((start <= settings.MaxStartOffset) || ReachesLastToken(heard, end)) && !heard.StartsInsideDigits(range, heard.Whole))
                {
                    ranges.Add(range);
                }
            }
        }
        return ranges;
    }

    private static bool ReachesLastToken(HeardText heard, int end)
    {
        int tokenCount = heard.Tokens.Count;
        return (end == tokenCount) || ((end == tokenCount - 1) && IsWeightClassToken(heard.Tokens[end]));
    }

    private static bool IsWeightClassToken(SpokenToken token) => (token.Kind == TokenKind.Word) && (token.Text is "heavy" or "super");

    private static CallsignHit? BestCallsignSpan(RoomAircraft aircraft, List<TokenRange> ranges, CallsignScan scan)
    {
        CallsignHit? best = null;
        foreach (SpokenForm form in aircraft.Callsign.Forms)
        {
            foreach (TokenRange range in ranges)
            {
                if (HitAt(aircraft, range, form, scan) is { } hit)
                {
                    best = IsBetterSpan(hit, best, scan.Call.Settings) ? hit : best;
                }
            }
        }
        return best;
    }

    private static CallsignHit? HitAt(RoomAircraft aircraft, TokenRange range, SpokenForm form, CallsignScan scan) =>
        IsPrefixlessRefused(range, scan.Call.ShapeOf(form), scan)
            ? null
            : FormHit(aircraft, new CandidateRange(range, scan.Heard.Whole), form, scan.Heard, scan.Call);

    /// <summary>The span rules every callsign match obeys, in the callsign stage and as traffic: its shape, a score, and where it may end.</summary>
    private static CallsignHit? FormHit(RoomAircraft aircraft, CandidateRange candidate, SpokenForm form, HeardText heard, DecodeCall call)
    {
        FormShape shape = call.ShapeOf(form);
        TokenRange range = candidate.Range;
        if (!heard.Fits(range, shape))
        {
            return null;
        }

        SpanScore score = heard.Score(range, form, shape);
        if (score.Weighted <= 0.0)
        {
            return null;
        }

        bool endsCleanly =
            !heard.EndsInsideDigits(range, candidate.Run)
            || (shape.IsPrefixed && (score.Raw == 1.0) && IsValueTail(aircraft, range, shape, heard, call));
        return endsCleanly ? new CallsignHit(aircraft, new SpanPlace(range, WithWeightClass(heard, range)), form, score) : null;
    }

    /// <summary>A form with no telephony could be any number, so it only counts where a callsign is expected and not where a value is.</summary>
    private static bool IsPrefixlessRefused(TokenRange range, FormShape shape, CallsignScan scan)
    {
        if (shape.IsPrefixed)
        {
            return false;
        }

        HeardText heard = scan.Heard;
        bool beforeValue = (range.End < heard.Tokens.Count) && ContextPhraseWords.PrefixlessValueFollowers.Contains(heard.Tokens[range.End].Text);
        return beforeValue || !(scan.SeparatorOpens || OpensPrefixless(heard, range.Start, scan.Call.Settings));
    }

    private static bool OpensPrefixless(HeardText heard, int start, DecoderSettings settings)
    {
        if (start == 0)
        {
            return true;
        }
        if (start > settings.MaxStartOffset)
        {
            return false;
        }

        string before = heard.Tokens[start - 1].Text;
        bool frequencyCall =
            (start >= 2)
            && ContextPhraseWords.FacilityWords.Contains(before)
            && ContextPhraseWords.FrequencyVerbs.Contains(heard.Tokens[start - 2].Text);
        return ContextPhraseWords.Openers.Contains(before) && !frequencyCall;
    }

    /// <summary>Whether the digits after a range ending inside a digit run are a value of their own, not more of the flight number.</summary>
    private static bool IsValueTail(RoomAircraft aircraft, TokenRange range, FormShape shape, HeardText heard, DecodeCall call)
    {
        if (!CanEndBeforeTail(range, shape, heard))
        {
            return false;
        }

        int tailEnd = heard.DigitRunEnd(range.End);
        bool longTail = tailEnd - range.End >= MinValueTailDigits;
        bool followed = (tailEnd < heard.Tokens.Count) && ContextPhraseWords.NumberValueFollowers.Contains(heard.Tokens[tailEnd].Text);
        return longTail || followed || IsRunwayCode(heard.Slice(new TokenRange(range.End, tailEnd)), aircraft, call);
    }

    /// <summary>
    /// Whether the flight can stop at this boundary: it splits no word or spoken pair, and a short flight said digit by digit
    /// is not simply carrying on in the digits after it.
    /// </summary>
    private static bool CanEndBeforeTail(TokenRange range, FormShape shape, HeardText heard)
    {
        int flightDigits = shape.Tokens.Count(token => token.Kind == TokenKind.Digit);
        bool cleanBoundary = heard.OpensSourceWord(range.End) && !heard.SplitsSpokenPair(range.End);
        bool loneDigits = heard.IsLoneDigitWord(range.End - 1) && heard.IsLoneDigitWord(range.End);
        return cleanBoundary && (flightDigits >= MinFlightDigitsBeforeTail) && ((flightDigits >= MinFlightDigitsBeforeLoneDigits) || !loneDigits);
    }

    private static bool IsRunwayCode(IReadOnlyList<SpokenToken> tail, RoomAircraft aircraft, DecodeCall call)
    {
        IEnumerable<SpokenToken[]> codeForms = aircraft
            .OwnEntities.Concat(call.Context.AirportEntities)
            .Where(entity => entity.Kind == EntityKind.Runway)
            .SelectMany(entity => entity.Forms)
            .Select(call.ShapeOf)
            .Where(shape => shape.NameCount == 0)
            .Select(shape => shape.Tokens.ToArray());
        return codeForms.Any(form => form.Select(token => token.Text).SequenceEqual(tail.Select(token => token.Text)));
    }

    private static TokenRange WithWeightClass(HeardText heard, TokenRange core)
    {
        bool absorbs = (core.End < heard.Tokens.Count) && IsWeightClassToken(heard.Tokens[core.End]);
        return absorbs ? new TokenRange(core.Start, core.End + 1) : core;
    }

    private static bool IsBetterSpan(CallsignHit hit, CallsignHit? best, DecoderSettings settings)
    {
        if (best is null)
        {
            return true;
        }

        (bool, double, double, int, int) hitRank = (Leads(hit, settings), hit.Weighted, hit.Raw, hit.Full.Length, -hit.Full.Start);
        (bool, double, double, int, int) bestRank = (Leads(best, settings), best.Weighted, best.Raw, best.Full.Length, -best.Full.Start);
        return hitRank.CompareTo(bestRank) > 0;
    }

    /// <summary>
    /// The candidate with the highest joint score among those close enough to the best; the list is best first, so a tie keeps
    /// the better one.
    /// </summary>
    private static CallsignDecision? DecideCallsign(HeardText heard, List<CallsignHit> candidates, DecodeCall call)
    {
        CallsignDecision? winner = null;
        foreach (CallsignHit hit in candidates)
        {
            if (hit.Weighted < candidates[0].Weighted - call.Settings.CallsignJointMargin)
            {
                continue;
            }

            CallsignDecision decision = DecisionFor(heard, hit, RemainderOf(hit, heard.Tokens.Count), call);
            if ((winner is null) || (decision.Joint > winner.Joint))
            {
                winner = decision;
            }
        }
        return winner;
    }

    private static CallsignDecision DecisionFor(HeardText heard, CallsignHit hit, Remainder remainder, DecodeCall call)
    {
        EntitySelection selection = SelectEntities(heard, remainder, CandidateSources(hit.Aircraft, call), 0.0, call);
        DecoderSettings settings = call.Settings;
        double coverage = Math.Max(selection.Coverage, settings.CoverageFloor);
        return new CallsignDecision(hit, selection, hit.Weighted * selection.MeanScore * Math.Pow(coverage, settings.CoverageExponent));
    }

    /// <summary>What the first second-callsign word with an outcome makes of the transmission, or null when the winner stands.</summary>
    private static DecodeResult? SecondCallsignResult(HypothesisView view, CallsignDecision decision, DecodeCall call)
    {
        IReadOnlyList<SpokenToken> tokens = view.Heard.Tokens;
        for (int i = decision.Hit.Full.End; i < tokens.Count; i++)
        {
            if (ContextPhraseWords.SecondCallsignWords.Contains(tokens[i].Text) && (SeparatorResult(view, decision, i, call) is { } result))
            {
                return result;
            }
        }
        return null;
    }

    private static DecodeResult? SeparatorResult(HypothesisView view, CallsignDecision decision, int separator, DecodeCall call)
    {
        HeardText heard = view.Heard;
        CallsignHit? second = HitAfterSeparator(heard, separator, separator == decision.Hit.Full.End, call);
        DecodeState state = StateOf(second, call.Settings);
        if (heard.Tokens[separator].Text == CorrectionWord)
        {
            return CorrectionResult(view, decision, new SeparatedHit(separator, second, state), call);
        }
        return state == DecodeState.NoCallsign ? null : UnresolvedResult(view, DecodeState.SayAgain, decision.Hit, call);
    }

    private sealed record SeparatedHit(int Separator, CallsignHit? Hit, DecodeState State);

    private static DecodeResult? CorrectionResult(HypothesisView view, CallsignDecision decision, SeparatedHit second, DecodeCall call)
    {
        if (AmendsTraffic(decision, second.Separator))
        {
            return null;
        }

        HeardText heard = view.Heard;
        if ((second.Hit is not { } hit) || (second.State == DecodeState.NoCallsign))
        {
            return NamesUnknownCallsign(heard, second.Separator) ? UnresolvedResult(view, DecodeState.SayAgain, decision.Hit, call) : null;
        }
        return DescribesTraffic(heard, decision, second.Separator)
            ? UnresolvedResult(view, DecodeState.SayAgain, decision.Hit, call)
            : SwitchedResult(view, hit, second.State, call);
    }

    /// <summary>A correction's hit takes the address: a resolved one rereads the tokens after it, a say-again one is only the guess.</summary>
    private static DecodeResult SwitchedResult(HypothesisView view, CallsignHit hit, DecodeState state, DecodeCall call)
    {
        if (state == DecodeState.SayAgain)
        {
            return UnresolvedResult(view, DecodeState.SayAgain, hit, call);
        }

        HeardText heard = view.Heard;
        var remainder = new Remainder([new TokenRange(hit.Full.End, heard.Tokens.Count)], hit.Full.End);
        return ResolvedResult(view, DecisionFor(heard, hit, remainder, call), call);
    }

    /// <summary>A correction directly after a chosen traffic span amends that traffic, not the addressee.</summary>
    private static bool AmendsTraffic(CallsignDecision decision, int separator) =>
        decision.Selection.Chosen.Any(span => (span.Source.Traffic is not null) && (span.Range.End == separator));

    /// <summary>Traffic described between the winner and a correction may be what the correction amends, so the address is in doubt.</summary>
    private static bool DescribesTraffic(HeardText heard, CallsignDecision decision, int separator) =>
        heard.Slice(new TokenRange(decision.Hit.Full.End, separator)).Any(token => ContextPhraseWords.TrafficDescriptionWords.Contains(token.Text));

    private static CallsignHit? HitAfterSeparator(HeardText heard, int separator, bool separatorOpens, DecodeCall call)
    {
        int start = separator + 1;
        int lastEnd = Math.Min(heard.Tokens.Count, start + call.Settings.MaxCallsignSpan);
        List<TokenRange> ranges = [.. Enumerable.Range(start + 1, Math.Max(0, lastEnd - start)).Select(end => new TokenRange(start, end))];
        return Ranked(HitsByAircraft(ranges, new CallsignScan(heard, call, separatorOpens)), call.Settings).FirstOrDefault();
    }

    private static bool NamesUnknownCallsign(HeardText heard, int separator) =>
        IsUnknownCallAt(heard.Tokens, separator + 1) && !IsCorrectionValueWord(heard.Tokens, separator + 1);

    /// <summary>A word the phraseology does not know, then digits or letters: a callsign that is not in the room.</summary>
    private static bool IsUnknownCallAt(IReadOnlyList<SpokenToken> tokens, int index) =>
        (index + 1 < tokens.Count)
        && (tokens[index].Kind == TokenKind.Word)
        && !NatoNearMissResolver.RuleLiteralWords.Contains(tokens[index].Text)
        && (tokens[index + 1].Kind != TokenKind.Word);

    private static bool IsCorrectionValueWord(IReadOnlyList<SpokenToken> tokens, int index) =>
        ContextPhraseWords.CorrectionValueWords.Contains(tokens[index].Text);
}
