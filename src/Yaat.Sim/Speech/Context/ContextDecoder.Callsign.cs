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

    private const int MaxOpenerRun = 6;

    private const int MaxFacilityFunctionWords = 2;

    private const int MaxClosingTailWords = 5;

    private const int MaxClosingPhrases = 2;

    private const int SightingBlockWindow = 3;

    private const string MedevacWord = "medevac";

    private const string TangoWord = "tango";

    /// <summary>A sighting report's object before a trailing callsign, from <paramref name="Start"/>, and the words that block it.</summary>
    private readonly record struct SightingPattern(int Start, IReadOnlySet<string> Blockers);

    /// <summary>
    /// One callsign pass; <paramref name="SeparatorOpens"/> marks a scan after a second-callsign word directly after the winner,
    /// which acts as an opener. <paramref name="OpenerRun"/> is how many tokens of opener run precede the callsign; they do not
    /// count toward the start offset. <paramref name="AfterSeparator"/> marks any scan after a second-callsign word, whose ranges
    /// start right after it or, past an identification prefix, one token later.
    /// </summary>
    private sealed record CallsignScan(HeardText Heard, DecodeCall Call, bool SeparatorOpens, int OpenerRun, bool AfterSeparator);

    /// <summary>The classes an opener run is built from, each used at most once.</summary>
    private enum OpenerClass
    {
        Lead,
        Facility,
        Greeting,
    }

    private readonly record struct OpenerPart(OpenerClass Class, int Length);

    private static List<CallsignHit> FindCallsignCandidates(HeardText heard, DecodeCall call)
    {
        if (heard.Typed && (ExactCallsignWord(heard, call) is { } exact))
        {
            return [exact];
        }

        var scan = new CallsignScan(heard, call, SeparatorOpens: false, OpenerRunLength(heard, call), AfterSeparator: false);
        List<CallsignHit> hits = HitsByAircraft(CallsignRanges(scan), scan);
        List<CallsignHit> leading = [.. hits.Where(hit => Leads(hit, scan) && !FollowsTrafficLeadIn(heard, hit))];
        return Ranked(leading.Count > 0 ? leading : TrailingCandidates(hits, scan), call.Settings);
    }

    /// <summary>
    /// The candidates left when none leads. An opening no candidate matched is another aircraft's call, so nothing later can be
    /// the addressee; a candidate refused as traffic still matched its opening, and is still the addressee when it closes a
    /// sighting report.
    /// </summary>
    private static IEnumerable<CallsignHit> TrailingCandidates(List<CallsignHit> hits, CallsignScan scan)
    {
        bool startUnclaimed = !hits.Any(hit => Leads(hit, scan)) && OpensWithUnmatchedCall(scan);
        return startUnclaimed ? [] : hits.Where(hit => !FollowsTrafficLeadIn(scan.Heard, hit) || ClosesSightingReport(scan.Heard, hit));
    }

    /// <summary>
    /// A span reaching the last token right after "that/the traffic", "traffic/it/him/her/them in sight" or "negative contact"
    /// identifies the speaker (AIM 4-2-3c), unless a word shortly before the pattern makes the callsign its object.
    /// </summary>
    private static bool ClosesSightingReport(HeardText heard, CallsignHit hit) =>
        ReachesLastToken(heard, hit.Core.End)
        && (SightingPatternBefore(heard.Tokens, hit.Full.Start) is { } pattern)
        && !IsSightingBlocked(heard.Tokens, pattern);

    private static SightingPattern? SightingPatternBefore(IReadOnlyList<SpokenToken> tokens, int spanStart)
    {
        string last = TextAt(tokens, spanStart - 1);
        string second = TextAt(tokens, spanStart - 2);
        bool theTraffic = (last == "traffic") && ContextPhraseWords.SightingArticles.Contains(second);
        bool negativeContact = (last == "contact") && (second == "negative");
        if (theTraffic || negativeContact)
        {
            return new SightingPattern(spanStart - 2, ContextPhraseWords.SightingBlockers);
        }
        bool inSight = (last == "sight") && (second == "in") && ContextPhraseWords.SightingObjects.Contains(TextAt(tokens, spanStart - 3));
        return inSight ? new SightingPattern(spanStart - 3, ContextPhraseWords.InSightBlockers) : null;
    }

    private static bool IsSightingBlocked(IReadOnlyList<SpokenToken> tokens, SightingPattern pattern)
    {
        int from = Math.Max(0, pattern.Start - SightingBlockWindow);
        return Enumerable.Range(from, pattern.Start - from).Any(index => pattern.Blockers.Contains(tokens[index].Text));
    }

    private static string TextAt(IReadOnlyList<SpokenToken> tokens, int index) => index >= 0 ? tokens[index].Text : "";

    /// <summary>The opening is scanned from where the opener run ends.</summary>
    private static bool OpensWithUnmatchedCall(CallsignScan scan) =>
        Enumerable.Range(scan.OpenerRun, scan.Call.Settings.MaxStartOffset + 1).Any(index => OpensCallAt(scan.Heard.Tokens, index));

    /// <summary>A start's offset past the opener run; a start inside the run keeps its own offset.</summary>
    private static int EffectiveStart(int start, CallsignScan scan) => start >= scan.OpenerRun ? start - scan.OpenerRun : start;

    /// <summary>
    /// How many tokens at the start of the transmission are an opener run: a lead word at token 0, a facility, a greeting, each at
    /// most once and in any order, at most <see cref="MaxOpenerRun"/> tokens. A run that reaches a frequency change is none.
    /// </summary>
    private static int OpenerRunLength(HeardText heard, DecodeCall call)
    {
        IReadOnlyList<SpokenToken> tokens = heard.Tokens;
        List<string[]> names = FacilityNameWords(call);
        var used = new HashSet<OpenerClass>();
        int end = 0;
        while ((OpenerPartAt(tokens, end, names) is { } part) && used.Add(part.Class) && (end + part.Length <= MaxOpenerRun))
        {
            end += part.Length;
        }
        return StartsFrequencyChange(tokens, end) ? 0 : end;
    }

    private static OpenerPart? OpenerPartAt(IReadOnlyList<SpokenToken> tokens, int index, List<string[]> names)
    {
        if (index >= tokens.Count)
        {
            return null;
        }
        if ((index == 0) && ContextPhraseWords.OpenerLeads.Contains(tokens[0].Text))
        {
            return new OpenerPart(OpenerClass.Lead, 1);
        }
        int greeting = GreetingLength(tokens, index);
        if (greeting > 0)
        {
            return new OpenerPart(OpenerClass.Greeting, greeting);
        }
        int facility = FacilityLength(tokens, index, names);
        return facility > 0 ? new OpenerPart(OpenerClass.Facility, facility) : null;
    }

    private static int GreetingLength(IReadOnlyList<SpokenToken> tokens, int index)
    {
        bool good = (tokens[index].Text == "good") && (index + 1 < tokens.Count) && ContextPhraseWords.GoodGreetings.Contains(tokens[index + 1].Text);
        if (good)
        {
            return 2;
        }
        return ContextPhraseWords.Greetings.Contains(tokens[index].Text) ? 1 : 0;
    }

    /// <summary>A facility name then up to two function words, or one function word alone.</summary>
    private static int FacilityLength(IReadOnlyList<SpokenToken> tokens, int index, List<string[]> names)
    {
        int nameLength = names.Where(name => WordsAt(tokens, index, name)).Select(name => name.Length).DefaultIfEmpty(0).Max();
        int maxFunctions = nameLength > 0 ? MaxFacilityFunctionWords : 1;
        int end = index + nameLength;
        while (
            (end < tokens.Count) && (end - index - nameLength < maxFunctions) && ContextPhraseWords.FacilityFunctionWords.Contains(tokens[end].Text)
        )
        {
            end++;
        }
        return end - index;
    }

    private static bool WordsAt(IReadOnlyList<SpokenToken> tokens, int index, string[] words) =>
        (index + words.Length <= tokens.Count) && words.Select((word, offset) => tokens[index + offset].Text == word).All(match => match);

    /// <summary>The room's facility names as token texts, without any name that shares a word with a room aircraft's telephony.</summary>
    private static List<string[]> FacilityNameWords(DecodeCall call)
    {
        HashSet<string> telephony =
        [
            .. call
                .Context.Aircraft.SelectMany(aircraft => aircraft.Callsign.Forms)
                .SelectMany(form => call.ShapeOf(form).Tokens)
                .Where(token => token.Kind == TokenKind.Word)
                .Select(token => token.Text),
        ];
        return
        [
            .. call
                .Context.FacilityNames.Select(name => SpokenTokenizer.Tokenize(name).Select(token => token.Text).ToArray())
                .Where(words => (words.Length > 0) && !words.Any(telephony.Contains)),
        ];
    }

    private static bool StartsFrequencyChange(IReadOnlyList<SpokenToken> tokens, int index)
    {
        if (index >= tokens.Count)
        {
            return false;
        }
        string text = tokens[index].Text;
        bool overTo = (text == "over") && (index + 1 < tokens.Count) && (tokens[index + 1].Text == "to");
        return overTo || ContextPhraseWords.FrequencyChangeWords.Contains(text);
    }

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

    /// <summary>
    /// The addressee is named first, so once a candidate leads, a later match can only be a value or traffic. An absorbed
    /// identification prefix starts the span.
    /// </summary>
    private static bool Leads(CallsignHit hit, CallsignScan scan) =>
        IsEarly(hit.Full.Start, scan) && (hit.Raw >= scan.Call.Settings.CallsignSayAgainFloor);

    private static bool IsEarly(int start, CallsignScan scan) => EffectiveStart(start, scan) <= scan.Call.Settings.MaxStartOffset;

    /// <summary>The word before the span, an identification prefix included, skipping articles.</summary>
    private static bool FollowsTrafficLeadIn(HeardText heard, CallsignHit hit)
    {
        int before = hit.Full.Start - 1;
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

    private static List<TokenRange> CallsignRanges(CallsignScan scan)
    {
        HeardText heard = scan.Heard;
        DecoderSettings settings = scan.Call.Settings;
        int tokenCount = heard.Tokens.Count;
        var ranges = new List<TokenRange>();
        for (int start = 0; start < tokenCount; start++)
        {
            int lastEnd = Math.Min(tokenCount, start + settings.MaxCallsignSpan);
            for (int end = start + 1; end <= lastEnd; end++)
            {
                var range = new TokenRange(start, end);
                bool early = IsEarly(start, scan) || (IsPrefixWordAt(heard, start - 1) && IsEarly(start - 1, scan));
                if ((early || ReachesLastToken(heard, end)) && !heard.StartsInsideDigits(range, heard.Whole))
                {
                    ranges.Add(range);
                }
            }
        }
        return ranges;
    }

    /// <summary>Whether a span ending at <paramref name="end"/> ends the transmission, past a weight class and a closing tail.</summary>
    private static bool ReachesLastToken(HeardText heard, int end)
    {
        int tokenCount = heard.Tokens.Count;
        int tailStart = (end < tokenCount) && IsWeightClassToken(heard.Tokens[end]) ? end + 1 : end;
        return (tailStart == tokenCount) || IsClosingTail(heard, tailStart);
    }

    /// <summary>One or two closing phrases, at most <see cref="MaxClosingTailWords"/> words, from a word boundary to the end.</summary>
    private static bool IsClosingTail(HeardText heard, int start)
    {
        if ((start >= heard.Tokens.Count) || !heard.OpensSourceWord(start))
        {
            return false;
        }

        string[] words = [.. heard.Words[heard.Tokens[start].SourceWord..].Select(LetterWord)];
        return (words.Length <= MaxClosingTailWords) && IsClosingPhraseRun(words, MaxClosingPhrases);
    }

    private static bool IsClosingPhraseRun(string[] words, int phrasesLeft)
    {
        if (words.Length == 0)
        {
            return true;
        }
        return (phrasesLeft > 0)
            && ContextPhraseWords.ClosingPhrases.Any(phrase =>
                words.Take(phrase.Length).SequenceEqual(phrase) && IsClosingPhraseRun(words[phrase.Length..], phrasesLeft - 1)
            );
    }

    private static string LetterWord(string word) => string.Concat(word.Where(char.IsLetter)).ToLowerInvariant();

    private static bool IsWeightClassToken(SpokenToken token) =>
        (token.Kind == TokenKind.Word) && (token.Text is "heavy" or "super" or "heavys" or "supers");

    private static CallsignHit? BestCallsignSpan(RoomAircraft aircraft, List<TokenRange> ranges, CallsignScan scan)
    {
        CallsignHit? best = null;
        foreach (SpokenForm form in aircraft.Callsign.Forms)
        {
            foreach (TokenRange range in ranges)
            {
                if (HitAt(aircraft, range, form, scan) is { } hit)
                {
                    best = IsBetterSpan(hit, best, scan) ? hit : best;
                }
            }
        }
        return best;
    }

    /// <summary>A form's hit on a range; an identification prefix directly before the range joins the span.</summary>
    private static CallsignHit? HitAt(RoomAircraft aircraft, TokenRange range, SpokenForm form, CallsignScan scan)
    {
        FormShape shape = scan.Call.ShapeOf(form);
        int? prefix = PrefixStart(aircraft, range, shape, scan);
        if (!IsHitPlaced(range, prefix, shape, scan))
        {
            return null;
        }

        CallsignHit? hit = FormHit(aircraft, new CandidateRange(range, scan.Heard.Whole), form, scan.Heard, scan.Call);
        return (hit is null) || (prefix is not { } start) ? hit : hit with { Place = new SpanPlace(hit.Core, new TokenRange(start, hit.Full.End)) };
    }

    /// <summary>
    /// The position rules a range obeys before it is scored. A prefix word is never scored as part of a form that does not open
    /// with it; after "medevac" a prefixless form counts as prefixed; a range placed only through a prefix word that does not
    /// apply to this form is out of place.
    /// </summary>
    private static bool IsHitPlaced(TokenRange range, int? prefix, FormShape shape, CallsignScan scan)
    {
        HeardText heard = scan.Heard;
        bool scoresPrefixWord = IsPrefixWordAt(heard, range.Start) && (shape.Tokens.FirstOrDefault()?.Text != heard.Tokens[range.Start].Text);
        if (scoresPrefixWord)
        {
            return false;
        }
        if (prefix is { } start)
        {
            return (heard.Tokens[start].Text == MedevacWord) || !IsPrefixlessRefused(range, shape, scan);
        }

        bool placedByItself = !scan.AfterSeparator && (IsEarly(range.Start, scan) || ReachesLastToken(heard, range.End));
        bool placedOnlyByPrefix = IsPrefixWordAt(heard, range.Start - 1) && !placedByItself;
        return !placedOnlyByPrefix && !IsPrefixlessRefused(range, shape, scan);
    }

    /// <summary>
    /// Where an identification prefix directly before the range starts, or null: "medevac" before any form unless an article
    /// makes it a noun ("the medevac"), per 7110.65 2-4-20a4 and a5; "tango" per <see cref="IsTangoPrefix"/>.
    /// </summary>
    private static int? PrefixStart(RoomAircraft aircraft, TokenRange range, FormShape shape, CallsignScan scan)
    {
        HeardText heard = scan.Heard;
        int index = range.Start - 1;
        if (!IsPrefixWordAt(heard, index))
        {
            return null;
        }

        bool applies =
            heard.Tokens[index].Text == MedevacWord ? !((index > 0) && heard.IsArticle(index - 1)) : IsTangoPrefix(aircraft, shape, index, scan);
        return applies ? index : null;
    }

    /// <summary>
    /// "tango" is stated on initial contact (7110.65 2-4-20a3), so it is a prefix only before a registration form on a span that
    /// opens the transmission or follows a second-callsign word, and never as a taxiway after a surface lead-in.
    /// </summary>
    private static bool IsTangoPrefix(RoomAircraft aircraft, FormShape shape, int index, CallsignScan scan)
    {
        bool afterSurfaceLeadIn = (index > 0) && ContextPhraseWords.BareSurfaceLeadIns.Contains(scan.Heard.Tokens[index - 1].Text);
        bool opensCall = scan.AfterSeparator || IsEarly(index, scan);
        return opensCall && !afterSurfaceLeadIn && IsRegistrationForm(aircraft, shape);
    }

    private static bool IsPrefixWordAt(HeardText heard, int index) =>
        (index >= 0) && (index < heard.Tokens.Count) && (heard.Tokens[index].Text is MedevacWord or TangoWord);

    /// <summary>A form that opens with "november", or a US registration's form that opens with its type or manufacturer name.</summary>
    private static bool IsRegistrationForm(RoomAircraft aircraft, FormShape shape)
    {
        SpokenToken? first = shape.Tokens.FirstOrDefault();
        string acid = aircraft.Callsign.Value;
        bool usRegistration = (acid.Length >= 2) && (acid[0] == 'N') && char.IsAsciiDigit(acid[1]);
        return (first is not null) && ((first.Text == "november") || (usRegistration && (first.Kind == TokenKind.Word)));
    }

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
        return beforeValue || !(scan.SeparatorOpens || OpensPrefixless(range.Start, scan));
    }

    /// <summary>At token 0, or within the start offset past the opener run with an opener directly before it.</summary>
    private static bool OpensPrefixless(int start, CallsignScan scan)
    {
        if (start == 0)
        {
            return true;
        }
        if (EffectiveStart(start, scan) > scan.Call.Settings.MaxStartOffset)
        {
            return false;
        }

        HeardText heard = scan.Heard;
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

    private static bool IsBetterSpan(CallsignHit hit, CallsignHit? best, CallsignScan scan)
    {
        if (best is null)
        {
            return true;
        }

        (bool, double, double, int, int) hitRank = (Leads(hit, scan), hit.Weighted, hit.Raw, hit.Full.Length, -hit.Full.Start);
        (bool, double, double, int, int) bestRank = (Leads(best, scan), best.Weighted, best.Raw, best.Full.Length, -best.Full.Start);
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
        List<TokenRange> ranges = [.. RangesFrom(heard, start, call)];
        if (IsPrefixWordAt(heard, start))
        {
            ranges.AddRange(RangesFrom(heard, start + 1, call));
        }
        var scan = new CallsignScan(heard, call, separatorOpens, OpenerRun: 0, AfterSeparator: true);
        return Ranked(HitsByAircraft(ranges, scan), call.Settings).FirstOrDefault();
    }

    private static IEnumerable<TokenRange> RangesFrom(HeardText heard, int start, DecodeCall call)
    {
        int lastEnd = Math.Min(heard.Tokens.Count, start + call.Settings.MaxCallsignSpan);
        return Enumerable.Range(start + 1, Math.Max(0, lastEnd - start)).Select(end => new TokenRange(start, end));
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
