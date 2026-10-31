namespace Yaat.Sim.Speech.Context;

public static partial class ContextDecoder
{
    private const int MaxTaxiwayDigits = 2;

    /// <summary>Where the entity stage looks, in priority order; <paramref name="Traffic"/> is the aircraft a traffic callsign names.</summary>
    private sealed record EntitySource(ContextEntity Entity, double Prior, bool Own, RoomAircraft? Traffic);

    /// <summary>An accepted entity span. <see cref="Weight"/> is what the selection maximises; <paramref name="Order"/> breaks its ties.</summary>
    private sealed record AcceptedSpan(SpanPlace Place, EntitySource Source, SpokenForm Form, SpanScore Score, int Order)
    {
        public TokenRange Range => Place.Full;

        public double Weighted => Score.Weighted;

        public double Weight => Score.Weighted * Source.Prior * Range.Length;
    }

    /// <summary>The tokens the entity stage may read; <paramref name="AddresseeEnd"/> is where an addressee's callsign ends.</summary>
    private sealed record Remainder(List<TokenRange> Runs, int? AddresseeEnd);

    private readonly record struct CandidateRange(TokenRange Range, TokenRange Run);

    /// <summary>One entity-stage pass: the ranges it may match and the floor under its accept thresholds.</summary>
    private sealed record EntityScan(HeardText Heard, Remainder Remainder, ScanRanges Ranges, double AcceptFloor, DecodeCall Call);

    /// <summary>The ranges each kind of source may match; a traffic callsign takes the callsign stage's span rules.</summary>
    private sealed record ScanRanges(List<CandidateRange> Entity, List<CandidateRange> Traffic);

    /// <summary>A form and its place in source order.</summary>
    private readonly record struct ScanForm(SpokenForm Form, int Order);

    /// <summary>The chosen spans in token order, with the mean of their scores and the share of the remainder's content they explain.</summary>
    private sealed record EntitySelection(IReadOnlyList<AcceptedSpan> Chosen, double MeanScore, double Coverage);

    /// <summary>A selection from one token position on, as a chain so its spans compare in token order.</summary>
    private sealed record SpanPlan(AcceptedSpan Span, SpanPlan? Next, double Total);

    private static List<EntitySource> CandidateSources(RoomAircraft addressee, DecodeCall call)
    {
        double airportPrior = call.Settings.AirportEntityPrior;
        var sources = new List<EntitySource>();
        var own = new HashSet<(EntityKind Kind, string Value)>();
        foreach (ContextEntity entity in addressee.OwnEntities)
        {
            sources.Add(new EntitySource(entity, 1.0, Own: true, Traffic: null));
            own.Add((entity.Kind, entity.Value));
        }
        foreach (ContextEntity entity in call.Context.AirportEntities)
        {
            if (!own.Contains((entity.Kind, entity.Value)))
            {
                sources.Add(new EntitySource(entity, airportPrior, Own: false, Traffic: null));
            }
        }
        foreach (RoomAircraft traffic in call.Context.Aircraft)
        {
            if (!ReferenceEquals(traffic, addressee))
            {
                sources.Add(new EntitySource(traffic.Callsign, airportPrior, Own: false, traffic));
            }
        }
        return sources;
    }

    private static List<EntitySource> AirportSources(DecodeCall call) =>
        [.. call.Context.AirportEntities.Select(entity => new EntitySource(entity, call.Settings.AirportEntityPrior, Own: false, Traffic: null))];

    /// <summary>The entity stage with no addressed aircraft: airport entities only, held to the no-callsign threshold.</summary>
    private static EntitySelection SelectAirportEntities(HeardText heard, CallsignHit? bestGuess, DecodeCall call)
    {
        var remainder = new Remainder(RemainderOf(bestGuess, heard.Tokens.Count).Runs, null);
        return SelectEntities(heard, remainder, AirportSources(call), call.Settings.EntityAcceptNoCallsign, call);
    }

    private static Remainder RemainderOf(CallsignHit? hit, int tokenCount)
    {
        if (hit is null)
        {
            return new Remainder([new TokenRange(0, tokenCount)], null);
        }
        return new Remainder([new TokenRange(0, hit.Full.Start), new TokenRange(hit.Full.End, tokenCount)], hit.Full.End);
    }

    private static EntitySelection SelectEntities(
        HeardText heard,
        Remainder remainder,
        IReadOnlyList<EntitySource> sources,
        double acceptFloor,
        DecodeCall call
    )
    {
        var scan = new EntityScan(heard, remainder, RangesOf(heard, remainder.Runs, call.Settings), acceptFloor, call);
        Dictionary<TokenRange, AcceptedSpan> accepted = AcceptSpans(scan, sources);
        List<AcceptedSpan> chosen = ChooseSpans(accepted.Values, heard.Tokens.Count);
        double meanScore = chosen.Count == 0 ? 1.0 : chosen.Average(span => span.Weighted * span.Source.Prior);
        return new EntitySelection(chosen, meanScore, Coverage(heard, remainder.Runs, chosen));
    }

    private static ScanRanges RangesOf(HeardText heard, List<TokenRange> runs, DecoderSettings settings)
    {
        var entity = new List<CandidateRange>();
        var traffic = new List<CandidateRange>();
        foreach (TokenRange run in runs)
        {
            for (int start = run.Start; start < run.End; start++)
            {
                int lastEnd = Math.Min(run.End, start + Math.Max(settings.MaxEntitySpan, settings.MaxCallsignSpan));
                for (int end = start + 1; end <= lastEnd; end++)
                {
                    var range = new TokenRange(start, end);
                    if (heard.StartsInsideDigits(range, run))
                    {
                        continue;
                    }
                    if (range.Length <= settings.MaxCallsignSpan)
                    {
                        traffic.Add(new CandidateRange(range, run));
                    }
                    if ((range.Length <= settings.MaxEntitySpan) && !heard.EndsInsideDigits(range, run))
                    {
                        entity.Add(new CandidateRange(range, run));
                    }
                }
            }
        }
        return new ScanRanges(entity, traffic);
    }

    /// <summary>
    /// Scores every form on every range, keeping for each range the heaviest accepted span and, among equals, the earliest in
    /// source order.
    /// </summary>
    private static Dictionary<TokenRange, AcceptedSpan> AcceptSpans(EntityScan scan, IReadOnlyList<EntitySource> sources)
    {
        var accepted = new Dictionary<TokenRange, AcceptedSpan>();
        int order = 0;
        foreach (EntitySource source in sources)
        {
            foreach (SpokenForm form in source.Entity.Forms)
            {
                order++;
                AcceptForm(scan, source, new ScanForm(form, order), accepted);
            }
        }
        return accepted;
    }

    private static void AcceptForm(EntityScan scan, EntitySource source, ScanForm form, Dictionary<TokenRange, AcceptedSpan> accepted)
    {
        if (source.Traffic is { } aircraft)
        {
            AcceptTraffic(scan, aircraft, source, form, accepted);
        }
        else
        {
            AcceptEntity(scan, source, form, accepted);
        }
    }

    private static void AcceptTraffic(
        EntityScan scan,
        RoomAircraft aircraft,
        EntitySource source,
        ScanForm form,
        Dictionary<TokenRange, AcceptedSpan> accepted
    )
    {
        FormShape shape = scan.Call.ShapeOf(form.Form);
        if (!shape.IsPrefixed)
        {
            return;
        }

        double threshold = AcceptThreshold(scan, shape);
        foreach (CandidateRange candidate in scan.Ranges.Traffic)
        {
            if ((FormHit(aircraft, candidate, form.Form, scan.Heard, scan.Call) is { } hit) && (hit.Raw >= threshold))
            {
                Hold(accepted, new AcceptedSpan(hit.Place, source, form.Form, hit.Score, form.Order));
            }
        }
    }

    private static void AcceptEntity(EntityScan scan, EntitySource source, ScanForm form, Dictionary<TokenRange, AcceptedSpan> accepted)
    {
        FormShape shape = scan.Call.ShapeOf(form.Form);
        double threshold = AcceptThreshold(scan, shape);
        bool bare = !scan.Heard.Typed && IsBareForm(source.Entity.Kind, shape);
        foreach (CandidateRange candidate in scan.Ranges.Entity)
        {
            bool allowed = scan.Heard.Fits(candidate.Range, shape) && (!bare || IsBareFormAllowed(scan, source.Entity.Kind, candidate, shape));
            SpanScore score = allowed ? scan.Heard.Score(candidate.Range, form.Form, shape) : default;
            if (allowed && (score.Raw >= threshold))
            {
                Hold(accepted, new AcceptedSpan(new SpanPlace(candidate.Range, candidate.Range), source, form.Form, score, form.Order));
            }
        }
    }

    private static double AcceptThreshold(EntityScan scan, FormShape shape)
    {
        DecoderSettings settings = scan.Call.Settings;
        return Math.Max(shape.NameCount > 0 ? settings.EntityAcceptName : settings.EntityAcceptCodeOnly, scan.AcceptFloor);
    }

    private static void Hold(Dictionary<TokenRange, AcceptedSpan> accepted, AcceptedSpan span)
    {
        if (!accepted.TryGetValue(span.Range, out AcceptedSpan? held) || (span.Weight > held.Weight))
        {
            accepted[span.Range] = span;
        }
    }

    private static bool IsBareForm(EntityKind kind, FormShape shape) =>
        (kind is EntityKind.Runway or EntityKind.Taxiway or EntityKind.Gate)
        && (shape.Tokens.Count > 0)
        && !ContextPhraseWords.EntityNouns.Contains(shape.Tokens[0].Text);

    private static bool IsBareFormAllowed(EntityScan scan, EntityKind kind, CandidateRange candidate, FormShape shape)
    {
        if (kind == EntityKind.Runway)
        {
            return IsBareRunwayAllowed(scan, candidate, shape);
        }

        IReadOnlyList<SpokenToken> tokens = scan.Heard.Tokens;
        int end = candidate.Range.End;
        bool nextIsDigit = (end < tokens.Count) && (tokens[end].Kind == TokenKind.Digit);
        bool singleLetter = (shape.Tokens.Count == 1) && (shape.Tokens[0].Kind == TokenKind.Nato);
        return !((kind == EntityKind.Taxiway) && singleLetter && nextIsDigit) && HasSurfaceLeadIn(tokens, candidate);
    }

    /// <summary>A bare number directly after the addressee's callsign is a runway; anywhere else it needs a word that expects one.</summary>
    private static bool IsBareRunwayAllowed(EntityScan scan, CandidateRange candidate, FormShape shape)
    {
        if (IsValueOrWrongSide(scan.Heard.Tokens, candidate.Range.End, shape))
        {
            return false;
        }

        int start = candidate.Range.Start;
        if (start == scan.Remainder.AddresseeEnd)
        {
            return true;
        }
        return (start > candidate.Run.Start) && HasRunwayLeadIn(scan.Heard.Tokens, start, candidate.Run);
    }

    private static bool IsValueOrWrongSide(IReadOnlyList<SpokenToken> tokens, int end, FormShape shape)
    {
        if (end >= tokens.Count)
        {
            return false;
        }

        string next = tokens[end].Text;
        bool wrongSide =
            ContextPhraseWords.RunwaySides.Contains(next) && !shape.Tokens.Any(token => ContextPhraseWords.RunwaySides.Contains(token.Text));
        return wrongSide || ContextPhraseWords.RunwayValueFollowers.Contains(next);
    }

    private static bool HasRunwayLeadIn(IReadOnlyList<SpokenToken> tokens, int start, TokenRange run)
    {
        SpokenToken before = tokens[start - 1];
        if ((before.Kind != TokenKind.Word) || !ContextPhraseWords.BareRunwayLeadIns.Contains(before.Text))
        {
            return false;
        }
        return (before.Text != "of") || ((start - 2 >= run.Start) && (tokens[start - 2].Text == "short"));
    }

    /// <summary>Walks back over a chain of letters and short digit runs, inside the run, to the word that introduces it.</summary>
    private static bool HasSurfaceLeadIn(IReadOnlyList<SpokenToken> tokens, CandidateRange candidate)
    {
        TokenRange run = candidate.Run;
        for (int i = candidate.Range.Start - 1; i >= run.Start; i--)
        {
            if (tokens[i].Kind == TokenKind.Word)
            {
                return ContextPhraseWords.BareSurfaceLeadIns.Contains(tokens[i].Text);
            }
            if (tokens[i].Text == "november")
            {
                return false;
            }
            if (tokens[i].Kind == TokenKind.Digit)
            {
                i = TaxiwayDigitsStart(tokens, i, run);
                if (i < 0)
                {
                    return false;
                }
            }
        }
        return false;
    }

    /// <summary>The start of the digit run ending at <paramref name="last"/> when it can be a taxiway's number, else -1.</summary>
    private static int TaxiwayDigitsStart(IReadOnlyList<SpokenToken> tokens, int last, TokenRange run)
    {
        int first = last;
        while ((first > run.Start) && (tokens[first - 1].Kind == TokenKind.Digit))
        {
            first--;
        }

        bool shortRun = last - first + 1 <= MaxTaxiwayDigits;
        bool afterLetter = (first > run.Start) && (tokens[first - 1].Kind == TokenKind.Nato) && (tokens[first - 1].Text != "november");
        return (shortRun && afterLetter) ? first : -1;
    }

    /// <summary>
    /// Weighted-interval selection over the token positions; among equal totals it prefers the selection whose first differing
    /// span comes earlier in source order, then the span over a skip.
    /// </summary>
    private static List<AcceptedSpan> ChooseSpans(IEnumerable<AcceptedSpan> accepted, int tokenCount)
    {
        ILookup<int, AcceptedSpan> byStart = accepted.OrderBy(span => span.Order).ThenBy(span => span.Range.End).ToLookup(span => span.Range.Start);
        var plans = new SpanPlan?[tokenCount + 1];
        for (int i = tokenCount - 1; i >= 0; i--)
        {
            SpanPlan? best = null;
            foreach (AcceptedSpan span in byStart[i])
            {
                SpanPlan? rest = plans[span.Range.End];
                var taken = new SpanPlan(span, rest, span.Weight + (rest?.Total ?? 0.0));
                best = IsBetterPlan(taken, best) ? taken : best;
            }
            plans[i] = IsBetterPlan(plans[i + 1], best) ? plans[i + 1] : best;
        }

        var chosen = new List<AcceptedSpan>();
        for (SpanPlan? plan = plans[0]; plan is not null; plan = plan.Next)
        {
            chosen.Add(plan.Span);
        }
        return chosen;
    }

    private static bool IsBetterPlan(SpanPlan? plan, SpanPlan? held)
    {
        if ((plan is null) || (held is null))
        {
            return (plan is not null) && (held is null);
        }
        if (plan.Total != held.Total)
        {
            return plan.Total > held.Total;
        }

        for (SpanPlan? a = plan, b = held; (a is not null) && (b is not null); a = a.Next, b = b.Next)
        {
            if (a.Span.Order != b.Span.Order)
            {
                return a.Span.Order < b.Span.Order;
            }
        }
        return false;
    }

    private static double Coverage(HeardText heard, List<TokenRange> runs, List<AcceptedSpan> chosen)
    {
        int content = 0;
        int explained = 0;
        foreach (TokenRange run in runs)
        {
            for (int i = run.Start; i < run.End; i++)
            {
                if (IsContent(heard.Tokens[i]))
                {
                    content++;
                    explained += chosen.Any(span => (span.Range.Start <= i) && (i < span.Range.End)) ? 1 : 0;
                }
            }
        }
        return content == 0 ? 1.0 : (double)explained / content;
    }

    private static bool IsContent(SpokenToken token) => (token.Kind != TokenKind.Word) || !NatoNearMissResolver.RuleLiteralWords.Contains(token.Text);

    /// <summary>Adds, for each typed word no match covers, the first entity whose value is that word, then orders the matches by position.</summary>
    private static List<EntityMatch> AddExactValueMatches(
        HeardText heard,
        List<EntityMatch> entities,
        CallsignHit? callsign,
        IReadOnlyList<EntitySource> sources
    )
    {
        var covered = new HashSet<int>();
        IEnumerable<EntityMatch> matches = callsign is null
            ? entities
            : entities.Append(heard.Match(callsign.Aircraft.Callsign, callsign.Full, 1.0, own: true));
        foreach (EntityMatch match in matches)
        {
            covered.UnionWith(Enumerable.Range(match.Span.Start, match.Span.End - match.Span.Start));
        }

        var all = new List<EntityMatch>(entities);
        for (int word = 0; word < heard.Words.Length; word++)
        {
            string upper = heard.Words[word].ToUpperInvariant();
            if (!covered.Contains(word) && (sources.FirstOrDefault(candidate => ValuePart(candidate.Entity.Value) == upper) is { } source))
            {
                all.Add(new EntityMatch(source.Entity, new WordSpan(word, word + 1), 1.0, source.Own, heard.Words[word]));
            }
        }
        return [.. all.OrderBy(match => match.Span.Start)];
    }

    private static string ValuePart(string value)
    {
        int slash = value.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? value : value[(slash + 1)..];
    }
}
