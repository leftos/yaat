using System.Text;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Pilot;

/// <summary>
/// Renders an accepted <see cref="ParsedCommand"/> back into spoken-English ATC phraseology
/// by inverting the same <see cref="PhraseologyRule"/>s that <see cref="PhraseologyMapper"/>
/// uses to parse controller speech. One source of truth — adding a recognition rule on the
/// input side gives us the readback for free.
///
/// Pipeline:
/// <list type="number">
///   <item><description>Look up the preferred rule for the command's canonical type. First-declared wins
///     (textbook readback before terser variants — a rule's <c>PilotVariants</c> rows are spoken
///     only by a <c>PilotPersonality.Varied</c> pilot, by a seeded pick per load band).</description></item>
///   <item><description>Extract the command's capture arguments via a subtype switch. Each capture name
///     (<c>{alt}</c>, <c>{hdg}</c>, etc.) gets formatted in its spoken form for that capture
///     class — altitudes via <see cref="AtcNumberParser.AltitudeToWords"/>, headings as
///     three individual digits, runways as "two eight right", and so on.</description></item>
///   <item><description>Render the pattern: drop optional-token markers (<c>?</c>) but keep their content,
///     substitute <c>{capture}</c> placeholders, lowercase joined-by-space.</description></item>
/// </list>
///
/// Returns <see langword="null"/> when no rule maps the canonical type — admin/diagnostic
/// verbs (WARP, PAUSE, TRACK, strip ops) intentionally have no PhraseologyRule and therefore
/// produce no readback.
/// </summary>
public static class PhraseologyVerbalizer
{
    private static readonly ILogger Log = SimLog.CreateLogger("PhraseologyVerbalizer");

    /// <summary>
    /// Cached map: declaration-ordered <see cref="PhraseologyRule"/> candidates per
    /// <see cref="CanonicalCommandType"/>. Rule selection happens per-call in
    /// <see cref="PickPreferredRule"/> because the right rule depends on which args the
    /// specific command instance carries — picking "enter right downwind runway {rwy}"
    /// when the command has a runway, or "enter right downwind" when it doesn't.
    ///
    /// Rules flagged <see cref="PhraseologyRule.SttOnly"/> are excluded entirely — they exist
    /// purely as acoustic-alias safety nets for STT and must never be spoken back by the
    /// pilot AI (e.g. "descent and maintain {alt}" is a recovery alias for a Whisper
    /// mistranscription of "descend"; speaking "descent and maintain" would be wrong).
    /// </summary>
    private static readonly Dictionary<CanonicalCommandType, PhraseologyRule[]> RulesByType = BuildRulesByType();

    private static Dictionary<CanonicalCommandType, PhraseologyRule[]> BuildRulesByType()
    {
        var dict = new Dictionary<CanonicalCommandType, PhraseologyRule[]>();
        foreach (IGrouping<CanonicalCommandType, PhraseologyRule> group in PhraseologyRules.All.Where(r => !r.SttOnly).GroupBy(r => r.Type))
        {
            dict[group.Key] = [.. group];
        }
        return dict;
    }

    /// <summary>
    /// Among the declared rules for this canonical type, pick the richest spoken form
    /// whose <c>{captures}</c> are all satisfied by <paramref name="args"/>. Among rules
    /// with equal capture counts, pick the first declared (which encodes textbook
    /// preference). Falls back to the first-declared zero-capture form when no
    /// multi-capture variant is fully satisfied.
    /// </summary>
    private static PhraseologyRule? PickPreferredRule(PhraseologyRule[] rules, IReadOnlyDictionary<string, string> args)
    {
        PhraseologyRule? best = null;
        int bestCaptures = -1;
        foreach (PhraseologyRule rule in rules)
        {
            int captureCount = 0;
            bool allSatisfied = true;
            foreach (string token in rule.Pattern)
            {
                string t = token.EndsWith('?') ? token[..^1] : token;
                if (!IsCapture(t))
                {
                    continue;
                }
                captureCount++;
                string name = t[1..^1];
                if (name.EndsWith("..."))
                {
                    name = name[..^3];
                }
                if (!args.ContainsKey(name) || string.IsNullOrEmpty(args[name]))
                {
                    allSatisfied = false;
                    break;
                }
            }
            if (!allSatisfied)
            {
                continue;
            }
            if (captureCount > bestCaptures)
            {
                best = rule;
                bestCaptures = captureCount;
            }
        }
        return best;
    }

    private static bool IsCapture(string token) => token.StartsWith('{') && token.EndsWith('}');

    private static readonly CaptureFormatter Spoken = new SpokenCaptureFormatter();
    private static readonly CaptureFormatter Terminal = new TerminalCaptureFormatter();

    /// <summary>The canonical spoken readback of <paramref name="cmd"/>: the rule pattern's own wording, never a variant.</summary>
    /// <param name="cmd">The command to read back.</param>
    /// <returns>The spoken clause, or null when no rule speaks the command.</returns>
    public static string? Verbalize(ParsedCommand cmd) =>
        VerbalizeCore(cmd, new VariantRequest(string.Empty, PilotSpeechContext.Canonical, WholeReadback: true), Spoken, out _);

    /// <summary>
    /// One clause of a readback as this pilot says it now. A <see cref="PilotPersonality.Varied"/> pilot whose rule has
    /// <see cref="PhraseologyRule.PilotVariants"/> speaks one row of the context's load band (Quiet and Normal: the canonical
    /// pattern or one of the band's rows), chosen by a seeded, uniform pick over the callsign, the canonical clause, the whole
    /// second and the scenario seed, so a replay speaks the same row; rows that leave a placeholder unresolved, or whose tag
    /// fails (flight-level-only below FL180, a speed change the current speed does not show), are never candidates, and with
    /// no candidate left the canonical is spoken. When the clause is not the whole readback (<paramref name="wholeReadback"/>
    /// false: other clauses, or a condition, share the transmission), no row that is a bare "wilco" or that places the
    /// callsign is a candidate, since either stands only for a whole readback. A row that places the callsign keeps
    /// <see cref="PilotVariant.CallsignPlaceholder"/> in the text for the readback to fill. A
    /// <see cref="PilotPersonality.Verbatim"/> pilot always speaks the canonical.
    /// </summary>
    /// <param name="cmd">The command to read back.</param>
    /// <param name="callsign">The reading-back aircraft's callsign, which seeds the pick.</param>
    /// <param name="context">The pilot's personality, the trait-shifted load band, the scenario seed and the elapsed second.</param>
    /// <param name="wholeReadback">Whether this clause is the readback's only clause, with no condition.</param>
    /// <returns>The spoken clause (null when no rule speaks the command) and whether the Quiet band offered a row.</returns>
    public static ClauseVerbalization VerbalizeClause(ParsedCommand cmd, string callsign, PilotSpeechContext context, bool wholeReadback)
    {
        string? text = VerbalizeCore(cmd, new VariantRequest(callsign, context, wholeReadback), Spoken, out bool offeredQuietRows);
        return new ClauseVerbalization(text, offeredQuietRows);
    }

    /// <summary>
    /// Compact controller-readable form of the same readback — runway/taxiway identifiers
    /// (<c>8R</c>, <c>B C D</c>), digit numbers (<c>270</c>, <c>5000</c>), raw fix/callsign
    /// identifiers. Shares the rule patterns and selection with <see cref="Verbalize"/>; only
    /// the per-capture token formatting differs. Used for the terminal <c>SAY</c> echo, which is
    /// built independently from the canonical command — never by stripping the spoken string.
    /// </summary>
    public static string? VerbalizeTerminal(ParsedCommand cmd) =>
        VerbalizeCore(cmd, new VariantRequest(string.Empty, PilotSpeechContext.Canonical, WholeReadback: true), Terminal, out _);

    /// <summary>
    /// Renders <paramref name="cmd"/> with <paramref name="fmt"/>: a direct-rendered shape when the command needs one, else
    /// the preferred rule's pattern, replaced by a seeded pick among the rule's variant rows when the context is varied
    /// (<see cref="VerbalizeClause"/>). <paramref name="offeredQuietRows"/> is whether the pick ran in the Quiet band with at
    /// least one row beside the canonical left after filtering.
    /// </summary>
    private static string? VerbalizeCore(ParsedCommand cmd, VariantRequest request, CaptureFormatter fmt, out bool offeredQuietRows)
    {
        offeredQuietRows = false;
        // UnsupportedCommand and similar non-canonical placeholders shouldn't crash the
        // verbalizer — they just have no pilot speech to render.
        if (cmd is UnsupportedCommand)
        {
            return null;
        }

        // TAXIAUTO is a TAXI with no route named; it reads back through the same rules.
        if (cmd is TaxiAutoCommand taxiAuto)
        {
            cmd = new TaxiCommand(
                [],
                [],
                taxiAuto.Expedite,
                taxiAuto.DestinationRunway,
                DestinationParking: taxiAuto.DestinationParking,
                DestinationSpot: taxiAuto.DestinationSpot
            );
        }

        if (cmd is ClimbMaintainCommand { Modifier: AltitudeAssignmentModifier.AtOrAbove } atOrAbove)
        {
            return $"maintain at or above {fmt.Altitude(atOrAbove.Altitude)}";
        }

        if (cmd is ClimbMaintainCommand { Modifier: AltitudeAssignmentModifier.AtOrBelow } atOrBelow)
        {
            return $"maintain at or below {fmt.Altitude(atOrBelow.Altitude)}";
        }

        // CrossFix AtOrAbove/AtOrBelow render directly because PickPreferredRule would tiebreak by declaration order and
        // always pick the first 2-capture rule (the bare "at" form), which doesn't carry the modifier in its template. The
        // bare "at" with no speed renders here too, to pick among its variant set; "at" with a speed falls through to the
        // rule-driven "at and maintain … at … knots" path below.
        if ((cmd is CrossFixCommand crossFix) && RendersAsCrossFixBuilder(crossFix))
        {
            return RenderCrossFix(crossFix, request, fmt, out offeredQuietRows);
        }

        // An expected visual approach (7110.65 4-7-10.d "expect visual approach runway two five right") names a runway, not
        // a procedure: every ExpectApproach rule ties on one capture, so the rule path would read it back as the first
        // declared ("expect ils runway ...") with the id spelled letter by letter.
        if (cmd is ExpectApproachCommand expectApproach && ApproachIds.TryParseVisual(expectApproach.ApproachId, out string? visualRunway))
        {
            return $"expect visual approach runway {fmt.Runway(visualRunway)}";
        }

        // An expected instrument approach names its own type and variant ("expect localizer runway two eight right
        // approach"), which the rule path cannot: every ExpectApproach rule ties on one capture.
        if ((cmd is ExpectApproachCommand expected) && (ApproachReadbackName.ForShorthand(expected.ApproachId) is { } expectedName))
        {
            return RenderExpectApproach(expected, expectedName, request, fmt, out offeredQuietRows);
        }

        // A TAXI clearance naming a runway as a path segment (taxi ALONG it) needs per-segment
        // connectors — "via" for the taxiway route, "on" for the runway (7110.65 §3-7-2.a separates
        // "VIA (route)" from "ON (runway)") — which the flat "taxi via {path}" rule template can't
        // express. A TAXI carrying hold-shorts is direct-rendered for a different reason: the rule
        // template's "runway?" literal is spoken unconditionally, so a taxiway HS target would be
        // read back as a runway ("hold short of runway charlie") — the direct clause picks the
        // "runway" word per target type (AIM 2-3-5.a.3 vs 7110.65 §3-7-2.b). A parking / spot
        // destination is direct-rendered too: the destination clause has to sit between the route
        // and any cross / hold-short clause (7110.65 §3-7-2.a.2.b: route first, then the hold-short),
        // and a rule template per {destination} × {cross} × {hold-short} shape would otherwise be
        // needed to keep the mandatory crossing / hold-short readback.
        if (
            cmd is TaxiCommand taxiDirect
            && (
                TaxiPathHasRunway(taxiDirect)
                || taxiDirect.HoldShorts.Count > 0
                || taxiDirect.DestinationParking is not null
                || taxiDirect.DestinationSpot is not null
                || taxiDirect.Expedite
            )
        )
        {
            return RenderTaxiAlongRunway(taxiDirect, fmt);
        }

        // RES with CROSS/HS modifiers composes clauses the flat "resume taxi" rule template can't
        // express (same reason RenderTaxiAlongRunway exists). Bare RES has no modifiers and falls
        // through to the rule-driven path below.
        if (cmd is ResumeCommand { CrossRunways.Count: > 0 } or ResumeCommand { HoldShorts.Count: > 0 })
        {
            return RenderResume((ResumeCommand)cmd, fmt);
        }

        // CROSS with multiple runways, an HS modifier, or any TAXIWAY target composes clauses the
        // single-{rwy} rule template can't express — a taxiway target must never be read back as
        // "cross runway bravo" (crossing clearances are a runway construct, 7110.65 §3-7-2.a.3).
        // A single-runway, no-HS CROSS falls through to the rule-driven path so its readback and
        // STT stay unchanged.
        if (
            (cmd is CrossRunwayCommand crossCmd)
            && (
                (crossCmd.RunwayIds.Count > 1)
                || (crossCmd.HoldShorts.Count > 0)
                || crossCmd.RunwayIds.Any(r => !CommandParser.IsRunwayArg(r))
                || (crossCmd.Expedite && (crossCmd.RunwayIds.Count > 0))
            )
        )
        {
            return RenderCross(crossCmd, fmt);
        }

        CanonicalCommandType canonicalType = CommandDescriber.ToCanonicalType(cmd);
        if (!RulesByType.TryGetValue(canonicalType, out PhraseologyRule[]? rules))
        {
            return null;
        }

        IReadOnlyDictionary<string, string> args = ExtractArgs(cmd, fmt);
        PhraseologyRule? rule = PickPreferredRule(rules, args);
        if (rule is null)
        {
            return null;
        }

        string canonical = RenderPattern(rule.Pattern, args);
        if ((request.Context.Personality != PilotPersonality.Varied) || (rule.PilotVariants is not { } variants))
        {
            return canonical;
        }

        ClauseVerbalization picked = PickVariant(cmd, variants, canonical, args, request);
        offeredQuietRows = picked.OfferedQuietRows;
        return picked.Text;
    }

    /// <summary>Who reads back, in what context, and whether the clause is the whole readback.</summary>
    /// <param name="Callsign">The reading-back aircraft's callsign, which seeds the pick.</param>
    /// <param name="Context">The pilot's personality, the trait-shifted load band, the scenario seed and the elapsed second.</param>
    /// <param name="WholeReadback">Whether the clause is the readback's only clause, with no condition.</param>
    internal readonly record struct VariantRequest(string Callsign, PilotSpeechContext Context, bool WholeReadback);

    /// <summary>
    /// The spoken clause a readback builder renders directly (an expedite, an altitude restriction): its own
    /// <paramref name="canonical"/> text, or for a <see cref="PilotPersonality.Varied"/> pilot one seeded pick among it and
    /// <paramref name="set"/>'s rows of the band, filtered and salted as <see cref="VerbalizeClause"/> picks a rule's rows.
    /// The rows are filled from <paramref name="cmd"/>'s own captures.
    /// </summary>
    /// <param name="cmd">The command read back.</param>
    /// <param name="set">The shape's variant set, chosen by the builder from the same flag that picks its words.</param>
    /// <param name="canonical">The builder's canonical spoken clause.</param>
    /// <param name="request">Who reads back, in what context, and whether the clause is the whole readback.</param>
    /// <returns>The spoken clause and whether the Quiet band offered a row.</returns>
    internal static ClauseVerbalization VerbalizeBuilderClause(ParsedCommand cmd, BuilderVariantSet set, string canonical, VariantRequest request) =>
        VaryDirectClause(cmd, set.Variants, canonical, ExtractArgs(cmd, Spoken), request);

    /// <summary>
    /// <see cref="VerbalizeBuilderClause(ParsedCommand, BuilderVariantSet, string, VariantRequest)"/> with the rows filled from
    /// <paramref name="captures"/>, for a builder whose captures the command does not carry (an approach clearance names the
    /// approach it resolved).
    /// </summary>
    /// <param name="cmd">The command read back.</param>
    /// <param name="set">The shape's variant set.</param>
    /// <param name="canonical">The builder's canonical spoken clause.</param>
    /// <param name="captures">The spoken values of the set's captures.</param>
    /// <param name="request">Who reads back, in what context, and whether the clause is the whole readback.</param>
    /// <returns>The spoken clause and whether the Quiet band offered a row.</returns>
    internal static ClauseVerbalization VerbalizeBuilderClause(
        ParsedCommand cmd,
        BuilderVariantSet set,
        string canonical,
        IReadOnlyDictionary<string, string> captures,
        VariantRequest request
    ) => VaryDirectClause(cmd, set.Variants, canonical, captures, request);

    /// <summary>The conditions a band's rows are filtered by before the pick.</summary>
    private readonly record struct CandidateFilter(FrequencyLoadBand Band, double? CurrentIndicatedAirspeed, bool WholeReadback);

    /// <summary>
    /// One seeded, uniform pick among the band's speakable rows (Quiet and Normal: the canonical first, then the band's rows).
    /// A row is speakable when every placeholder but the callsign (<see cref="PilotVariant.CallsignPlaceholder"/>) resolves
    /// and, if tagged <see cref="PilotVariantTags.FlightLevelOnly"/>, the command's altitude is a flight level, or if tagged
    /// <see cref="PilotVariantTags.GroupFormRange"/>, it is from 10,000 to 17,999 ft, or if tagged with a speed-change
    /// condition (<see cref="PilotVariant.SpeedChangeTags"/>), the command is a speed with no modifier and the context's
    /// current speed meets the condition; a bare "wilco" row and a row that places the callsign are speakable only when the
    /// clause is the whole readback. The pick salts the canonical with its own prefix, which keeps it apart from other
    /// <see cref="VariantPick"/> draws on the same callsign, second and seed (the quiet flavour's included); with no
    /// speakable row it is the canonical. It reports Quiet rows offered when the band is Quiet and a row beside the canonical
    /// survived the filter.
    /// </summary>
    private static ClauseVerbalization PickVariant(
        ParsedCommand cmd,
        PilotVariantSet variants,
        string canonical,
        IReadOnlyDictionary<string, string> args,
        VariantRequest request
    )
    {
        PilotSpeechContext context = request.Context;
        var filter = new CandidateFilter(context.Band, context.CurrentIndicatedAirspeed, request.WholeReadback);
        List<string> candidates = Candidates(cmd, variants, canonical, args, filter);
        if (candidates.Count == 0)
        {
            return new ClauseVerbalization(canonical, OfferedQuietRows: false);
        }

        int index = VariantPick.Index(request.Callsign, "readback-variant:" + canonical, context.ElapsedSecond, context.RngSeed, candidates.Count);
        return new ClauseVerbalization(candidates[index], OfferedQuietRows: (context.Band == FrequencyLoadBand.Quiet) && (candidates.Count > 1));
    }

    /// <summary>
    /// The spoken texts a <see cref="PilotPersonality.Varied"/> pilot picks among for <paramref name="cmd"/> read back alone in
    /// <paramref name="band"/>, in pick order: the band's speakable rows (Quiet and Normal: the canonical first). A row that
    /// places the callsign keeps <see cref="PilotVariant.CallsignPlaceholder"/> in its text. A clause the readback builds from
    /// a variant set (an altitude restriction, an expedite, a cross-fix restriction with no speed) offers that set's rows,
    /// chosen by <paramref name="aircraft"/> as the readback chooses them, or its canonical when no row is speakable; a
    /// builder-rendered clause read back canonical in every band (an at-or-above or at-or-below cross-fix restriction with a
    /// speed, a military route clearance) offers its canonical only.
    /// </summary>
    /// <param name="cmd">The command to read back.</param>
    /// <param name="aircraft">The aircraft reading back, whose flight rules and vertical goal pick a builder's variant set.</param>
    /// <param name="band">The frequency load band.</param>
    /// <param name="currentIndicatedAirspeed">The aircraft's indicated airspeed now, or null when unknown.</param>
    /// <returns>
    /// The candidates. A builder-rendered clause returns at least its canonical. A rule-read command returns empty when no rule
    /// speaks it, the rule has no variants, or no row is speakable.
    /// </returns>
    public static IReadOnlyList<string> SpokenCandidates(
        ParsedCommand cmd,
        AircraftState aircraft,
        FrequencyLoadBand band,
        double? currentIndicatedAirspeed
    )
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        var filter = new CandidateFilter(band, currentIndicatedAirspeed, WholeReadback: true);
        if (BuilderCandidates(cmd, aircraft, filter) is { } built)
        {
            return built;
        }

        if (!RulesByType.TryGetValue(CommandDescriber.ToCanonicalType(cmd), out PhraseologyRule[]? rules))
        {
            return [];
        }

        IReadOnlyDictionary<string, string> args = ExtractArgs(cmd, Spoken);
        if (PickPreferredRule(rules, args) is not { PilotVariants: { } variants } rule)
        {
            return [];
        }

        return Candidates(cmd, variants, RenderPattern(rule.Pattern, args), args, filter);
    }

    /// <summary>
    /// The candidates of a clause a readback builder renders directly, as <see cref="SpokenCandidates"/> describes; null when
    /// <paramref name="cmd"/> is read back from a rule.
    /// </summary>
    private static IReadOnlyList<string>? BuilderCandidates(ParsedCommand cmd, AircraftState aircraft, CandidateFilter filter)
    {
        if (PilotResponder.HeldCanonicalClause(cmd) is { } held)
        {
            return [held];
        }

        if ((cmd is CrossFixCommand crossFix) && RendersAsCrossFixBuilder(crossFix))
        {
            string canonical = CrossFixCanonical(crossFix, Spoken);
            return (VariedCrossFixSet(crossFix) is { } set)
                ? SetCandidates(crossFix, set.Variants, canonical, CrossFixArgs(crossFix, Spoken), filter)
                : [canonical];
        }

        if ((cmd is ExpectApproachCommand expected) && (ExpectApproachVariants(expected) is { } expect))
        {
            string canonical = RenderSetPattern(expect.Set, expect.Captures);
            return SetCandidates(expected, expect.Set.Variants, canonical, expect.Captures, filter);
        }

        if (PilotResponder.VariedBuilderClause(cmd, aircraft) is { } clause)
        {
            return (clause.Captures is not null)
                ? FinalClauseCandidates(cmd, clause, filter)
                : SetCandidates(cmd, clause.Variants.Variants, clause.Canonical.Tts, ExtractArgs(cmd, Spoken), filter);
        }

        return null;
    }

    /// <summary>
    /// The candidates of a builder clause whose set varies only its final clause (an approach clearance after its route and
    /// crossing): the canonical text before that clause, then each of the clause's candidates, picked from the normal rows on
    /// a quiet frequency when a route or crossing comes first, as the readback picks them.
    /// </summary>
    private static List<string> FinalClauseCandidates(ParsedCommand cmd, PilotResponder.BuilderClause clause, CandidateFilter filter)
    {
        IReadOnlyDictionary<string, string> captures = clause.Captures ?? new Dictionary<string, string>();
        string final = RenderSetPattern(clause.Variants, captures);
        string tts = clause.Canonical.Tts;
        if (!tts.EndsWith(final, StringComparison.Ordinal))
        {
            Log.LogError(
                "Candidates of {Command}: canonical does not end with \"{Final}\" of variant set \"{Set}\": \"{Tts}\"; offering canonical",
                cmd,
                final,
                clause.Variants.Name,
                tts
            );
            return [tts];
        }

        // A clause after the route and crossing chains the readback, which is never chatty: quiet speaks the normal rows.
        string prefix = tts[..^final.Length];
        CandidateFilter clauseFilter =
            ((prefix.Length > 0) && (filter.Band == FrequencyLoadBand.Quiet)) ? filter with { Band = FrequencyLoadBand.Normal } : filter;
        return [.. SetCandidates(cmd, clause.Variants.Variants, final, captures, clauseFilter).Select(candidate => prefix + candidate)];
    }

    /// <summary>The variant set and captures of an expected instrument approach; null when the id names no type and runway.</summary>
    internal static (BuilderVariantSet Set, IReadOnlyDictionary<string, string> Captures)? ExpectApproachVariants(ExpectApproachCommand cmd) =>
        ApproachReadbackName.SpokenParts(cmd.ApproachId) is { } parts
            ? ApproachCommandHandler.ClauseVariants(CanonicalCommandType.ExpectApproach, parts.Approach, parts.Runway)
            : null;

    /// <summary>
    /// An expected instrument approach, "expect ILS runway two eight right approach", which names its own type and variant (the
    /// rule path cannot: every ExpectApproach rule ties on one capture); a varied pilot picks among it and its set's rows.
    /// </summary>
    private static string RenderExpectApproach(
        ExpectApproachCommand cmd,
        PilotSpeechText name,
        VariantRequest request,
        CaptureFormatter fmt,
        out bool offeredQuietRows
    )
    {
        offeredQuietRows = false;
        string canonical = $"expect {fmt.Form(name)} approach";
        if (ExpectApproachVariants(cmd) is not { } expect)
        {
            return canonical;
        }

        ClauseVerbalization picked = VaryDirectClause(cmd, expect.Set.Variants, canonical, expect.Captures, request);
        offeredQuietRows = picked.OfferedQuietRows;
        return picked.Text ?? canonical;
    }

    /// <summary>A builder set's candidates, or its canonical alone when no row is speakable, as <see cref="PickVariant"/> speaks it.</summary>
    private static List<string> SetCandidates(
        ParsedCommand cmd,
        PilotVariantSet variants,
        string canonical,
        IReadOnlyDictionary<string, string> args,
        CandidateFilter filter
    )
    {
        List<string> candidates = Candidates(cmd, variants, canonical, args, filter);
        return (candidates.Count == 0) ? [canonical] : candidates;
    }

    private static List<string> Candidates(
        ParsedCommand cmd,
        PilotVariantSet variants,
        string canonical,
        IReadOnlyDictionary<string, string> args,
        CandidateFilter filter
    )
    {
        List<string> candidates = (filter.Band is FrequencyLoadBand.Quiet or FrequencyLoadBand.Normal) ? [canonical] : [];
        foreach (PilotVariant variant in BandRows(variants, filter.Band).Where(row => RowSpeakable(row, cmd, filter)))
        {
            string rendered = RenderVariantTemplate(variant.Template, args);
            if (!string.IsNullOrWhiteSpace(rendered) && !ContainsUnresolvedPlaceholder(rendered))
            {
                candidates.Add(rendered);
            }
        }

        return candidates;
    }

    /// <summary>Whether a row's tags and shape let it be spoken for <paramref name="cmd"/> under <paramref name="filter"/>.</summary>
    private static bool RowSpeakable(PilotVariant variant, ParsedCommand cmd, CandidateFilter filter) =>
        AltitudeConditionHolds(variant.Tags, AssignedAltitude(cmd))
        && SpeedChangeConditionHolds(variant.Tags, cmd, filter.CurrentIndicatedAirspeed)
        && (filter.WholeReadback || (!variant.IsBareWilco && !variant.PlacesCallsign));

    /// <summary>
    /// Whether the altitude tags hold: <see cref="PilotVariantTags.FlightLevelOnly"/> needs a flight level,
    /// <see cref="PilotVariantTags.GroupFormRange"/> an altitude from 10,000 to 17,999 ft.
    /// </summary>
    private static bool AltitudeConditionHolds(PilotVariantTags tags, int? altitude) =>
        (!tags.HasFlag(PilotVariantTags.FlightLevelOnly) || ((altitude is int fl) && AtcNumberParser.IsFlightLevel(fl)))
        && (!tags.HasFlag(PilotVariantTags.GroupFormRange) || ((altitude is int feet) && IsGroupFormRange(feet)));

    /// <summary>
    /// The altitude a climb, descent, expedite or cross-fix restriction assigns, read from the command, never from the
    /// rendered text, so the altitude tags test the value itself; null for a command with no assigned altitude.
    /// </summary>
    private static int? AssignedAltitude(ParsedCommand cmd) =>
        cmd switch
        {
            ClimbMaintainCommand climb => climb.Altitude,
            DescendMaintainCommand descend => descend.Altitude,
            ExpediteCommand expedite => expedite.Altitude,
            CrossFixCommand crossFix => crossFix.Altitude,
            _ => null,
        };

    /// <summary>
    /// Whether a row tagged <paramref name="tags"/> may be spoken against the aircraft's current speed: an untagged row always
    /// may; a speed-change row only for a speed with no modifier and a known current speed, slowing when it is more than
    /// <see cref="PilotVariant.SpeedChangeThresholdKnots"/> above the assigned speed, speeding up when more than that below,
    /// maintaining when within it (AIM 4-4-12.c).
    /// </summary>
    private static bool SpeedChangeConditionHolds(PilotVariantTags tags, ParsedCommand cmd, double? currentIndicatedAirspeed)
    {
        PilotVariantTags speedTags = tags & PilotVariant.SpeedChangeTags;
        if (speedTags == PilotVariantTags.None)
        {
            return true;
        }

        return (CurrentSpeedAboveAssigned(cmd, currentIndicatedAirspeed) is double fasterBy) && SpeedChangeShows(speedTags, fasterBy);
    }

    /// <summary>How many knots the current speed is above a plain speed assignment's; null for any other command or no current speed.</summary>
    private static double? CurrentSpeedAboveAssigned(ParsedCommand cmd, double? currentIndicatedAirspeed) =>
        ((cmd is SpeedCommand { Modifier: SpeedModifier.None } speed) && (currentIndicatedAirspeed is double current)) ? current - speed.Speed : null;

    private static bool SpeedChangeShows(PilotVariantTags speedTag, double fasterBy) =>
        speedTag switch
        {
            PilotVariantTags.SlowingOnly => fasterBy > PilotVariant.SpeedChangeThresholdKnots,
            PilotVariantTags.SpeedingOnly => fasterBy < -PilotVariant.SpeedChangeThresholdKnots,
            PilotVariantTags.MaintainingOnly => Math.Abs(fasterBy) <= PilotVariant.SpeedChangeThresholdKnots,
            _ => false,
        };

    /// <summary>Whether <paramref name="feet"/> lies from 10,000 to 17,999 ft, where the group form differs from the digit form.</summary>
    private static bool IsGroupFormRange(int feet) => feet is >= 10000 and < 18000;

    /// <summary>The listed rows of <paramref name="band"/>, without the canonical pattern the Normal band also speaks.</summary>
    /// <param name="variants">The rule's variant set.</param>
    /// <param name="band">The frequency load band.</param>
    /// <returns>The band's rows.</returns>
    public static IReadOnlyList<PilotVariant> BandRows(PilotVariantSet variants, FrequencyLoadBand band) =>
        band switch
        {
            FrequencyLoadBand.Quiet => variants.Quiet,
            FrequencyLoadBand.Normal => variants.Normal,
            FrequencyLoadBand.Busy => variants.Busy,
            _ => throw new ArgumentOutOfRangeException(nameof(band), band, "Unknown frequency load band."),
        };

    /// <summary>
    /// The spoken render of one variant <paramref name="template"/> over <paramref name="cmd"/>'s own captures. It applies no
    /// band or tag filter: the seeded pick (<see cref="SpokenCandidates"/>) decides which rows are spoken.
    /// </summary>
    /// <param name="cmd">The command whose captures fill the template.</param>
    /// <param name="template">A variant row's template, e.g. <c>"{alt:group}"</c>.</param>
    /// <returns>The rendered text; an unresolved placeholder stays in braces.</returns>
    public static string RenderSpokenVariant(ParsedCommand cmd, string template) => RenderVariantTemplate(template, ExtractArgs(cmd, Spoken));

    /// <summary>The spoken render of one variant <paramref name="template"/> over <paramref name="captures"/>, a builder's own captures.</summary>
    /// <param name="template">A variant row's or a set pattern's template.</param>
    /// <param name="captures">The spoken capture values.</param>
    /// <returns>The rendered text; an unresolved placeholder stays in braces.</returns>
    public static string RenderSpokenVariant(string template, IReadOnlyDictionary<string, string> captures) =>
        RenderVariantTemplate(template, captures);

    /// <summary>The spoken render of <paramref name="set"/>'s pattern, whose tokens may carry punctuation, over a builder's <paramref name="captures"/>.</summary>
    internal static string RenderSetPattern(BuilderVariantSet set, IReadOnlyDictionary<string, string> captures) =>
        RenderVariantTemplate(string.Join(' ', set.Pattern), captures);

    /// <summary>
    /// Renders a variant row token by token, as <see cref="RenderPattern"/> does, except that a token may carry trailing
    /// punctuation ("{hdg}," or "alright,"), kept after its rendered value; the callsign placeholder stays as it is, for the
    /// readback to fill. A token whose value is empty (an optional tail such as no pattern side) still leaves its punctuation
    /// on the text before it, unless that text already ends with it: the rule only stops the same mark doubling.
    /// </summary>
    private static string RenderVariantTemplate(string template, IReadOnlyDictionary<string, string> args)
    {
        var sb = new StringBuilder();
        foreach (string token in template.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string core = token.TrimEnd(',', '.');
            string punctuation = token[core.Length..];
            string rendered = RenderPattern([core], args);
            if (rendered.Length > 0)
            {
                Append(sb, rendered + punctuation);
            }
            else if ((punctuation.Length > 0) && (sb.Length > 0) && (sb[^1] != punctuation[0]))
            {
                sb.Append(punctuation);
            }
        }

        return sb.ToString();
    }

    private static bool ContainsUnresolvedPlaceholder(string text)
    {
        string withoutCallsign = text.Replace(PilotVariant.CallsignPlaceholder, string.Empty, StringComparison.Ordinal);
        return withoutCallsign.Contains('{') || withoutCallsign.Contains('}');
    }

    /// <summary>
    /// Per-subtype extractor: pulls the command's parsed args into a capture-name → spoken-string
    /// dictionary. Capture names match the placeholders in <see cref="PhraseologyRules"/> patterns.
    /// Commands without captures return an empty dictionary; their patterns are pure literals.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ExtractArgs(ParsedCommand cmd, CaptureFormatter fmt) =>
        cmd switch
        {
            // Heading
            FlyHeadingCommand h => Map("hdg", fmt.Heading(h.MagneticHeading)),
            TurnLeftCommand h => Map("hdg", fmt.Heading(h.MagneticHeading)),
            TurnRightCommand h => Map("hdg", fmt.Heading(h.MagneticHeading)),
            LeftTurnCommand r => Map("deg", fmt.Degrees(r.Degrees)),
            RightTurnCommand r => Map("deg", fmt.Degrees(r.Degrees)),

            // Altitude / speed
            ClimbMaintainCommand c => AltitudeArgs(c.Altitude, fmt),
            DescendMaintainCommand c => AltitudeArgs(c.Altitude, fmt),
            SpeedCommand s => SpeedArgs(s.Speed, fmt),
            ExpediteCommand e => e.Altitude is int alt ? AltitudeArgs(alt, fmt) : Empty(),
            MachCommand m => Map("mach", fmt.Mach(m.MachNumber)),

            // Navigation
            DirectToCommand d when d.Fixes.Count > 0 => Map("fix", fmt.FixSequence(d.Fixes)),
            ForceDirectToCommand d when d.Fixes.Count > 0 => Map("fix", fmt.FixSequence(d.Fixes)),
            AppendDirectToCommand d when d.Fixes.Count > 0 => Map("fix", fmt.FixSequence(d.Fixes)),
            AppendForceDirectToCommand d when d.Fixes.Count > 0 => Map("fix", fmt.FixSequence(d.Fixes)),
            TurnLeftDirectToCommand d when d.Fixes.Count > 0 => Map("fix", fmt.FixSequence(d.Fixes)),
            TurnRightDirectToCommand d when d.Fixes.Count > 0 => Map("fix", fmt.FixSequence(d.Fixes)),
            ExpectApproachCommand e => Map("rwy", fmt.Approach(e.ApproachId)),
            ClearedVisualApproachCommand v when v.RunwayId is { } visualRunway => Map("rwy", fmt.Runway(visualRunway)),
            JoinFinalApproachCourseCommand jfac when jfac.ApproachId is { } id => Map("rwy", fmt.Approach(id)),
            CrossFixCommand cf => CrossFixArgs(cf, fmt),
            ClimbViaCommand cv when cv.Altitude is int alt => AltitudeArgs(alt, fmt),

            // The military route and aerial refueling clearances are absent on purpose: their rules
            // are SttOnly and PilotResponder.VerbalizeForReadback builds their readbacks, because
            // rule selection cannot see which arguments are present and a pattern cannot carry the
            // comma before the altitude clause.

            // Tower
            LandAndHoldShortCommand l => Map("crossrwy", fmt.Runway(l.CrossingRunwayId)),

            // Pattern entries (runway-suffixed forms in the rules)
            EnterLeftDownwindCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            EnterRightDownwindCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            EnterLeftCrosswindCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            EnterRightCrosswindCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            EnterLeftBaseCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            EnterRightBaseCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            EnterFinalCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            MakeLeftTrafficCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),
            MakeRightTrafficCommand p when p.RunwayId is { } r => Map("rwy", fmt.Runway(r)),

            // Helicopter
            AirTaxiCommand atxi => AirTaxiArgs(atxi, fmt),

            // Ground
            // Multi-runway / HS-modified CROSS is direct-rendered in VerbalizeCore; only the
            // single-runway, no-HS form reaches the rule template here.
            CrossRunwayCommand { RunwayIds.Count: 1, HoldShorts.Count: 0 } c => Map("rwy", fmt.Runway(c.RunwayIds[0])),
            HoldShortCommand h => HoldShortCommandArgs(h, fmt),
            AssignRunwayCommand a => Map("rwy", fmt.Runway(a.RunwayId)),
            TaxiCommand taxi => TaxiArgs(taxi, fmt),
            ExitTaxiwayCommand e => Map("taxiway", fmt.Taxiway(e.Taxiway)),
            ExitLeftCommand e when e.Taxiway is { } t => Map("taxiway", fmt.Taxiway(t)),
            ExitRightCommand e when e.Taxiway is { } t => Map("taxiway", fmt.Taxiway(t)),

            // Transponder
            SquawkCommand s => Map("code", fmt.Squawk((int)s.Code)),

            // Holding
            HoldPresentPosition360Command => Empty(), // pattern uses no captures (left/right is in the rule literal)
            HoldAtFixOrbitCommand h => Map("fix", fmt.Fix(h.FixName)),
            HoldAtFixHoverCommand h => Map("fix", fmt.Fix(h.FixName)),

            // Pushback's {path...} capture isn't extracted yet, so the verbalizer falls through to the
            // rule's literal "pushback" keyword with no path filled in.
            _ => Empty(),
        };

    /// <summary>
    /// Captures for an <c>ATXI</c> readback. Only a runway destination is voiced with its destination —
    /// "air taxi to runway 28L", or "air taxi to runway 28L at J" for the located form — because an air taxi
    /// to a runway ends holding short of it and the numbers have to be verified (7110.65 3-11-1.c, AIM 4-4-7.b.4).
    /// A helipad, parking or spot destination yields no capture, so it keeps the bare acknowledgement.
    /// </summary>
    private static IReadOnlyDictionary<string, string> AirTaxiArgs(AirTaxiCommand atxi, CaptureFormatter fmt)
    {
        if ((atxi.TargetKind != AirTaxiTargetKind.Runway) || (atxi.Destination is not { Length: > 0 } destination))
        {
            return Empty();
        }

        int at = destination.IndexOf('@');
        string target = at < 0 ? destination : destination[..at];
        string onTaxiway = at < 0 ? "" : destination[(at + 1)..];
        if (!CommandParser.IsRunwayArg(target))
        {
            return Empty();
        }

        var dict = new Dictionary<string, string> { ["rwy"] = fmt.Runway(target) };
        if (onTaxiway.Length > 0)
        {
            dict["taxiway"] = fmt.Taxiway(onTaxiway);
        }

        return dict;
    }

    /// <summary>
    /// Extracts the spoken-readback captures for a TAXI clearance: the route path (with turn words),
    /// the destination runway, and crossings. <see cref="PickPreferredRule"/> then selects the richest
    /// matching rule, so a path-only command reads "taxi via …" while a fuller clearance reads
    /// "runway … taxi via … cross runway …". Hold-shorts never reach here — a TAXI carrying them is
    /// direct-rendered in <see cref="VerbalizeCore"/> so the "runway" word is decided per target type.
    /// A TAXI with no route (bare <c>TAXI 1L</c> at the runway, <c>TAXIAUTO 1L</c>) yields only the runway
    /// capture and reads "taxi to runway …"; one with neither a route nor a runway (a node-ref-only
    /// draw-route debug taxi) yields no captures and is not voiced.
    /// </summary>
    private static IReadOnlyDictionary<string, string> TaxiArgs(TaxiCommand taxi, CaptureFormatter fmt)
    {
        var dict = new Dictionary<string, string>();
        string path = fmt.TaxiPath(taxi);
        if (!string.IsNullOrEmpty(path))
        {
            dict["path"] = path;
        }

        if (taxi.DestinationRunway is { Length: > 0 } rwy)
        {
            dict["rwy"] = fmt.Runway(rwy);
        }

        if (taxi.CrossRunways is { Count: > 0 } crossRunways)
        {
            dict["crossrwy"] = string.Join(" and ", crossRunways.Select(fmt.Runway));
        }

        return dict;
    }

    /// <summary>
    /// Renders a taxi path's taxiways, prefixing a hinted taxiway with its turn ("right on bravo" /
    /// "left on charlie") — the action form a pilot echoes for the controller's "make right/left turn
    /// onto &lt;taxiway&gt;" (7110.65 §3-7-2 NOTE, AIM 4-3-17). Node-reference tokens (#NNNN) are
    /// dropped. The spoken form joins with ", " ("bravo, charlie, delta"); the terminal form joins
    /// with " " ("B C D"). Per-taxiway token formatting comes from <paramref name="fmt"/>.
    /// </summary>
    private static string RenderTaxiPath(TaxiCommand taxi, CaptureFormatter fmt, string separator)
    {
        List<TurnDirection?>? hints = taxi.PathTurnHints;
        var parts = new List<string>(taxi.Path.Count);
        for (int i = 0; i < taxi.Path.Count; i++)
        {
            string name = taxi.Path[i];
            if (name.StartsWith('#'))
            {
                continue;
            }

            TurnDirection? hint = (hints is not null && i < hints.Count) ? hints[i] : null;
            parts.Add(fmt.TaxiTurn(name, hint));
        }

        return string.Join(separator, parts);
    }

    private static bool TaxiPathHasRunway(TaxiCommand taxi) => taxi.Path.Any(t => !t.StartsWith('#') && CommandParser.IsRunwayArg(t));

    /// <summary>
    /// Renders a TAXI readback whose path includes a runway segment with FAA-correct connectors
    /// (7110.65 §3-7-2.a, §3-1-3.b): the taxiway route is introduced with "via", a runway taxied along
    /// is introduced with "on" — "taxi via bravo, on runway two eight right, golf, delta" (and, runway
    /// first, "taxi on runway two eight right, golf, delta"). Trailing cross/hold-short clauses are set off
    /// with a comma, the pause a pilot puts between the route and its restrictions ("…charlie, hold short
    /// of runway two eight right"); the destination runway leads, matching the rule-driven forms.
    /// </summary>
    private static string RenderTaxiAlongRunway(TaxiCommand taxi, CaptureFormatter fmt)
    {
        List<TurnDirection?>? hints = taxi.PathTurnHints;
        var parts = new List<string>(taxi.Path.Count);
        bool firstSegment = true;
        for (int i = 0; i < taxi.Path.Count; i++)
        {
            string name = taxi.Path[i];
            if (name.StartsWith('#'))
            {
                continue;
            }

            if (CommandParser.IsRunwayArg(name))
            {
                parts.Add(fmt.RunwaySegment(name));
            }
            else
            {
                TurnDirection? hint = (hints is not null && i < hints.Count) ? hints[i] : null;
                string twy = fmt.TaxiTurn(name, hint);
                parts.Add(firstSegment ? $"via {twy}" : twy);
            }

            firstSegment = false;
        }

        string body = parts.Count == 0 ? "taxi" : $"taxi {string.Join(fmt.TaxiSeparator, parts)}";

        // Destination before the runway clauses: 7110.65 §3-7-2.a states the route "and then" the
        // hold short instructions, and AIM 4-4-7.b.2 has the pilot read them back in that sequence.
        body += RenderTaxiDestinationClause(taxi, fmt);

        if (taxi.CrossRunways is { Count: > 0 } cross)
        {
            body += $", cross runway {string.Join(" and ", cross.Select(fmt.Runway))}";
        }

        // "Taxi without delay" (7110.65 §3-7-2.j) follows the route; the required hold-short readback stays last.
        if (taxi.Expedite)
        {
            body += ", without delay";
        }

        if (taxi.HoldShorts is { Count: > 0 } holdShorts)
        {
            body += $", hold short of {string.Join(" and ", holdShorts.Select(h => RenderHoldShortTarget(h, fmt)))}";
        }

        if (taxi.DestinationRunway is { Length: > 0 } rwy)
        {
            body = $"runway {fmt.Runway(rwy)} {body}";
        }

        return body;
    }

    /// <summary>
    /// " to spot seven alpha" / " to parking bravo one two", or empty (7110.65 §3-7-2.a "TAXI TO (location)").
    /// A spot wins over a parking name, matching <c>CommandDescriber</c>'s canonical form.
    /// </summary>
    private static string RenderTaxiDestinationClause(TaxiCommand taxi, CaptureFormatter fmt)
    {
        if (taxi.DestinationSpot is { Length: > 0 } spot)
        {
            return $" to spot {fmt.Spot(spot)}";
        }

        return taxi.DestinationParking is { Length: > 0 } parking ? $" to parking {fmt.Parking(parking)}" : "";
    }

    /// <summary>
    /// What a route readback reads off a resolved route: the route from <see cref="StartSegment"/>, the taxiway names it
    /// may say (<see cref="TaxiwayNamed"/>: a leg the clearance left out is driven silently), each named taxiway's turn
    /// hint, the runway designators the clearance named, which pick the end a two-ended runway is read as, the runways
    /// crossed "without delay" (<see cref="ExpeditedRunways"/>), the cleared bars read as uncleared
    /// (<see cref="UnclearedBars"/>, matched by reference): a bar another command's own clause crosses, which ends the walk
    /// silently, read neither as a crossing nor as a hold short, with nothing past it, and what picks the end of a runway the
    /// clearance did not name (<see cref="UnnamedEnd"/>).
    /// </summary>
    public sealed record TaxiRouteReadbackInputs(
        TaxiRoute Route,
        int StartSegment,
        Func<string, bool> TaxiwayNamed,
        IReadOnlyDictionary<string, TurnDirection>? TurnHints,
        IReadOnlyCollection<string> NamedRunways,
        IReadOnlyCollection<string> ExpeditedRunways,
        IReadOnlyCollection<HoldShortPoint> UnclearedBars,
        RunwayEndChooser UnnamedEnd
    );

    /// <summary>
    /// What picks the end of a runway a readback names that the clearance did not: the aircraft reading back, the airport's
    /// ground layout and the room's active runways (<see cref="RunwayEndNamed"/>).
    /// </summary>
    public sealed record RunwayEndChooser(AircraftState Aircraft, AirportGroundLayout? Layout, ActiveRunways ActiveRunways);

    /// <summary>
    /// A runway crossing a route readback reads: <see cref="Runway"/> the runway ("runway two eight left"), and whether it is
    /// crossed "without delay".
    /// </summary>
    public sealed record RunwayCrossing(string Runway, bool Expedite);

    /// <summary>
    /// A route's readback clauses in one form, up to the first hold short that stops the aircraft: <see cref="Route"/> the
    /// legs ("via bravo, charlie", "on runway …"; empty when none is said), <see cref="StartCrossings"/> each runway cleared
    /// at the bar the walk starts on, <see cref="Crossings"/> each further runway crossed before the stop ("runway two eight
    /// left"; a runway is read once), <see cref="HoldShort"/> its target ("runway two eight right", "taxiway charlie"), null
    /// when nothing stops the aircraft on the route, and <see cref="EndsAtUnclearedBar"/>, whether a bar of
    /// <see cref="TaxiRouteReadbackInputs.UnclearedBars"/> ended the walk with no hold short: nothing past that bar is read,
    /// neither the issued taxiways past the route's end nor the destination.
    /// </summary>
    public sealed record TaxiRouteClauses(
        string Route,
        IReadOnlyList<RunwayCrossing> StartCrossings,
        IReadOnlyList<RunwayCrossing> Crossings,
        string? HoldShort,
        bool EndsAtUnclearedBar
    );

    /// <summary>
    /// Walks the route's segments in order from <see cref="TaxiRouteReadbackInputs.StartSegment"/>, collecting the taxiway
    /// legs (consecutive duplicates collapsed, as <see cref="TaxiRouteFormatter.TaxiwayLegs"/> does), every cleared runway
    /// hold short as a crossing (implicit, auto-crossed or by a <c>CROSS</c>), and stops at the first hold short that is not
    /// cleared: nothing past it is read (7110.65 3-7-2.b: route, crossings, then the hold short). A bar of
    /// <see cref="TaxiRouteReadbackInputs.UnclearedBars"/> ends the walk the same way but is not read as a hold short. The
    /// destination runway's own bar stops nothing here: the runway assignment says it.
    /// </summary>
    public static TaxiRouteClauses RenderRouteToFirstStop(TaxiRouteReadbackInputs inputs, bool spoken) =>
        RenderRouteToFirstStop(inputs, null, false, spoken ? Spoken : Terminal);

    /// <summary>
    /// <see cref="RenderRouteToFirstStop(TaxiRouteReadbackInputs, bool)"/>, with <paramref name="released"/>, the bar a
    /// clearance has just released and the hold short phase has not yet marked cleared, read as cleared. With
    /// <paramref name="stopAtDestinationRunway"/>, an uncleared destination runway bar is a stop like any other: a continuation
    /// readback has no runway assignment to say it.
    /// </summary>
    private static TaxiRouteClauses RenderRouteToFirstStop(
        TaxiRouteReadbackInputs inputs,
        HoldShortPoint? released,
        bool stopAtDestinationRunway,
        CaptureFormatter fmt
    )
    {
        int start = Math.Max(0, inputs.StartSegment);
        List<RunwayCrossing> crossings = StartNodeCrossings(inputs, start, released);
        int startCrossings = crossings.Count;
        (HoldShortPoint? stop, int lastSegment, bool endsAtUnclearedBar) = WalkToFirstStop(inputs, start, stopAtDestinationRunway, crossings);
        return new TaxiRouteClauses(
            RenderRouteLegs(inputs, lastSegment, fmt),
            [.. crossings.Take(startCrossings).Select(c => c with { Runway = $"runway {fmt.Runway(c.Runway)}" })],
            [.. crossings.Skip(startCrossings).Select(c => c with { Runway = $"runway {fmt.Runway(c.Runway)}" })],
            stop is null ? null : RenderStopTarget(stop, inputs, fmt),
            endsAtUnclearedBar
        );
    }

    /// <summary>
    /// Whether the walk reads <paramref name="hs"/> as cleared: cleared and not one of
    /// <see cref="TaxiRouteReadbackInputs.UnclearedBars"/>.
    /// </summary>
    private static bool IsReadCleared(TaxiRouteReadbackInputs inputs, HoldShortPoint hs) =>
        hs.IsCleared && !inputs.UnclearedBars.Any(bar => ReferenceEquals(bar, hs));

    /// <summary>
    /// The runways cleared at the bar the walk starts on (the aircraft holding short there), which is behind no segment and is
    /// read only as a crossing; <paramref name="released"/> counts as cleared.
    /// </summary>
    private static List<RunwayCrossing> StartNodeCrossings(TaxiRouteReadbackInputs inputs, int start, HoldShortPoint? released)
    {
        var crossings = new List<RunwayCrossing>();
        TaxiRoute route = inputs.Route;
        if (start >= route.Segments.Count)
        {
            return crossings;
        }

        int startNode = route.Segments[start].FromNodeId;
        foreach (
            HoldShortPoint hs in route.HoldShortPoints.Where(h =>
                (h.NodeId == startNode) && (IsReadCleared(inputs, h) || ReferenceEquals(h, released)) && (h.TargetName is { Length: > 0 })
            )
        )
        {
            AddCrossing(crossings, hs, inputs);
        }

        return crossings;
    }

    /// <summary>
    /// Walks the route from <paramref name="start"/>, adding each cleared runway bar to <paramref name="crossings"/>, to the first
    /// uncleared hold short that stops the aircraft: the stop and the index of the segment ending at it, or no stop and the
    /// route's last segment. A stopping bar of <see cref="TaxiRouteReadbackInputs.UnclearedBars"/> ends the walk with no stop,
    /// at the segment ending at it, and <c>EndsAtUnclearedBar</c> set.
    /// </summary>
    private static (HoldShortPoint? Stop, int LastSegment, bool EndsAtUnclearedBar) WalkToFirstStop(
        TaxiRouteReadbackInputs inputs,
        int start,
        bool stopAtDestinationRunway,
        List<RunwayCrossing> crossings
    )
    {
        TaxiRoute route = inputs.Route;
        for (int i = start; i < route.Segments.Count; i++)
        {
            int node = route.Segments[i].ToNodeId;
            foreach (HoldShortPoint hs in route.HoldShortPoints.Where(h => (h.NodeId == node) && (h.TargetName is { Length: > 0 })))
            {
                if (IsReadCleared(inputs, hs))
                {
                    AddCrossing(crossings, hs, inputs);
                }
                else if (stopAtDestinationRunway || (hs.Reason != HoldShortReason.DestinationRunway))
                {
                    return inputs.UnclearedBars.Any(bar => ReferenceEquals(bar, hs)) ? (null, i, true) : (hs, i, false);
                }
            }
        }

        return (null, route.Segments.Count - 1, false);
    }

    /// <summary>
    /// The readback of a <c>CROSS</c> or <c>RES</c> that releases <paramref name="released"/> on a route that goes on past
    /// it, read off the route from the segment leaving the bar as a TAXI from there would be: the lead ("cross runway two
    /// eight right" for the runways released at the bar, "continue taxi" for a taxiway or spot bar), "without delay" for an
    /// expedited crossing, the legs ("continue taxi via bravo, charlie"; after a "continue taxi" lead, the legs follow it
    /// directly, and end with <paramref name="issuedBeyondRoute"/>, the issued taxiways past the route's end, when nothing
    /// stops the aircraft first), the further crossings, and the hold short that next stops the aircraft (7110.65 3-7-2.b order).
    /// </summary>
    public static PilotSpeechText RenderRouteContinuationReadback(
        TaxiRouteReadbackInputs inputs,
        HoldShortPoint released,
        bool expedite,
        IReadOnlyList<string> issuedBeyondRoute
    ) =>
        new(
            RenderRouteContinuationReadback(inputs, released, expedite, issuedBeyondRoute, Terminal),
            RenderRouteContinuationReadback(inputs, released, expedite, issuedBeyondRoute, Spoken)
        );

    private static string RenderRouteContinuationReadback(
        TaxiRouteReadbackInputs inputs,
        HoldShortPoint released,
        bool expedite,
        IReadOnlyList<string> issuedBeyondRoute,
        CaptureFormatter fmt
    )
    {
        TaxiRouteClauses clauses = RenderRouteToFirstStop(inputs, released, true, fmt);
        string route = RouteWithIssuedTail(clauses, inputs, issuedBeyondRoute, fmt);
        string continueTaxi = route.Length == 0 ? ContinueTaxiRelease : $"{ContinueTaxiRelease} {route}";
        bool crossesAtBar = clauses.StartCrossings.Count > 0;
        var body = new StringBuilder(crossesAtBar ? $"cross {string.Join(" and ", clauses.StartCrossings.Select(c => c.Runway))}" : continueTaxi);
        if (expedite)
        {
            body.Append(", without delay");
        }

        if (crossesAtBar && (route.Length > 0))
        {
            body.Append($", {continueTaxi}");
        }

        if (clauses.Crossings.Count > 0)
        {
            body.Append($", cross {string.Join(" and ", clauses.Crossings.Select(c => c.Runway))}");
        }

        if (clauses.HoldShort is { } holdShort)
        {
            body.Append($", hold short of {holdShort}");
        }

        return body.ToString();
    }

    /// <summary>
    /// The clauses' route legs, followed by <paramref name="issuedBeyondRoute"/> when nothing stops the aircraft on the route
    /// ("via bravo, charlie" from a route on B and an issued C past its end); the legs alone after a stop.
    /// </summary>
    private static string RouteWithIssuedTail(
        TaxiRouteClauses clauses,
        TaxiRouteReadbackInputs inputs,
        IReadOnlyList<string> issuedBeyondRoute,
        CaptureFormatter fmt
    )
    {
        if ((clauses.HoldShort is not null) || clauses.EndsAtUnclearedBar || (issuedBeyondRoute.Count == 0))
        {
            return clauses.Route;
        }

        string tail = string.Join(fmt.TaxiSeparator, issuedBeyondRoute.Select(t => fmt.TaxiTurn(t, TurnHintOf(inputs, t))));
        return clauses.Route.Length == 0 ? $"via {tail}" : $"{clauses.Route}{fmt.TaxiSeparator}{tail}";
    }

    /// <summary>
    /// The TAXI readback read off the resolved route (<see cref="RenderRouteToFirstStop"/>) in both forms: the runway
    /// assignment first ("runway three zero, …"), then the route, the issued taxiways past the route's end
    /// (<paramref name="issuedBeyondRoute"/>) and the destination when nothing stops the aircraft first, the crossings
    /// ("cross runway two eight left and runway two eight right"), "without delay", and the hold short. An expedited TAXI's
    /// one trailing "without delay" covers every crossing, so each joins as a plain one.
    /// </summary>
    public static PilotSpeechText RenderTaxiRouteReadback(
        TaxiCommand taxi,
        TaxiRouteReadbackInputs inputs,
        IReadOnlyList<string> issuedBeyondRoute
    ) => new(RenderTaxiRouteReadback(taxi, inputs, issuedBeyondRoute, Terminal), RenderTaxiRouteReadback(taxi, inputs, issuedBeyondRoute, Spoken));

    private static string RenderTaxiRouteReadback(
        TaxiCommand taxi,
        TaxiRouteReadbackInputs inputs,
        IReadOnlyList<string> issuedBeyondRoute,
        CaptureFormatter fmt
    )
    {
        TaxiRouteClauses clauses = RenderRouteToFirstStop(inputs, null, false, fmt);
        string route = RouteWithIssuedTail(clauses, inputs, issuedBeyondRoute, fmt);
        string body = route.Length == 0 ? "taxi" : $"taxi {route}";
        if ((clauses.HoldShort is null) && !clauses.EndsAtUnclearedBar)
        {
            body += RenderTaxiDestinationClause(taxi, fmt);
        }

        List<RunwayCrossing> crossings = [.. clauses.StartCrossings, .. clauses.Crossings];
        foreach (string clause in CrossingClauses(taxi.Expedite ? [.. crossings.Select(c => c with { Expedite = false })] : crossings))
        {
            body += $", {clause}";
        }

        if (taxi.Expedite)
        {
            body += ", without delay";
        }

        if (clauses.HoldShort is { } holdShort)
        {
            body += $", hold short of {holdShort}";
        }

        return taxi.DestinationRunway is { Length: > 0 } rwy ? $"runway {fmt.Runway(rwy)}, {body}" : body;
    }

    /// <summary>The legs said up to and including segment <paramref name="lastSegment"/>, "via" leading a taxiway first.</summary>
    private static string RenderRouteLegs(TaxiRouteReadbackInputs inputs, int lastSegment, CaptureFormatter fmt)
    {
        List<TaxiRouteFormatter.TaxiwayLeg> legs = TaxiRouteFormatter.TaxiwayLegs(inputs.Route);
        var said = new List<TaxiRouteFormatter.TaxiwayLeg>();
        for (int k = 0; k < legs.Count; k++)
        {
            if (IsLegSaid(inputs, legs, k, lastSegment) && ((said.Count == 0) || !SameLeg(said[^1], legs[k])))
            {
                said.Add(legs[k]);
            }
        }

        var parts = new List<string>(said.Count);
        foreach (TaxiRouteFormatter.TaxiwayLeg leg in said)
        {
            string part = leg.IsRunway ? fmt.RunwaySegment(RunwayEndNamed(leg.Name, inputs)) : fmt.TaxiTurn(leg.Name, TurnHintOf(inputs, leg.Name));
            parts.Add((parts.Count == 0) && !leg.IsRunway ? $"via {part}" : part);
        }

        return string.Join(fmt.TaxiSeparator, parts);
    }

    /// <summary>
    /// Whether leg <paramref name="k"/> of <paramref name="legs"/> is read: it starts by <paramref name="lastSegment"/>, the walk
    /// has not already passed it (the next leg starts after the walk's start), and it is a runway or a taxiway the readback says.
    /// </summary>
    private static bool IsLegSaid(TaxiRouteReadbackInputs inputs, List<TaxiRouteFormatter.TaxiwayLeg> legs, int k, int lastSegment)
    {
        int first = inputs.Route.Segments.IndexOf(legs[k].Segment);
        bool reachedBeforeStart = (k + 1 < legs.Count) && (inputs.Route.Segments.IndexOf(legs[k + 1].Segment) <= inputs.StartSegment);
        return (first <= lastSegment) && !reachedBeforeStart && (legs[k].IsRunway || inputs.TaxiwayNamed(legs[k].Name));
    }

    private static bool SameLeg(TaxiRouteFormatter.TaxiwayLeg a, TaxiRouteFormatter.TaxiwayLeg b) =>
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private static TurnDirection? TurnHintOf(TaxiRouteReadbackInputs inputs, string taxiway) =>
        (inputs.TurnHints is not null) && inputs.TurnHints.TryGetValue(taxiway, out TurnDirection hint) ? hint : null;

    /// <summary>A hold short's target as read: "runway two eight right", "spot one seven", "taxiway charlie".</summary>
    private static string RenderStopTarget(HoldShortPoint stop, TaxiRouteReadbackInputs inputs, CaptureFormatter fmt)
    {
        string target = stop.TargetName!;
        string said;
        if (IsRunwayTarget(target))
        {
            said = $"runway {fmt.Runway(RunwayEndNamed(target, inputs))}";
        }
        else
        {
            var parsed = HoldShortTarget.Parse(target);
            said = parsed.IsSpot ? $"spot {fmt.Spot(parsed.Target)}" : $"taxiway {fmt.Taxiway(parsed.Target)}";
        }

        return said;
    }

    /// <summary>
    /// The "cross …" clauses of a TAXI route readback, in route order: consecutive crossings not expedited joined with " and "
    /// in one clause ("cross runway two eight left and runway two eight right"), each expedited runway in its own clause
    /// ("cross runway two eight right without delay", as a standalone expedited <c>CROSS</c> reads, 7110.65 §3-7-2.j).
    /// </summary>
    private static List<string> CrossingClauses(IReadOnlyList<RunwayCrossing> crossings)
    {
        var clauses = new List<string>();
        var plain = new List<string>();
        foreach (RunwayCrossing crossing in crossings)
        {
            if (!crossing.Expedite)
            {
                plain.Add(crossing.Runway);
                continue;
            }

            FlushPlainCrossings(clauses, plain);
            clauses.Add($"cross {crossing.Runway} without delay");
        }

        FlushPlainCrossings(clauses, plain);
        return clauses;
    }

    /// <summary>Adds the pending crossings not expedited to <paramref name="clauses"/> as one "cross …" clause, and clears them.</summary>
    private static void FlushPlainCrossings(List<string> clauses, List<string> plain)
    {
        if (plain.Count > 0)
        {
            clauses.Add($"cross {string.Join(" and ", plain)}");
            plain.Clear();
        }
    }

    /// <summary>
    /// Adds a runway hold short's runway, crossed, to <paramref name="crossings"/> once, expedited when one of
    /// <see cref="TaxiRouteReadbackInputs.ExpeditedRunways"/> names it; any other hold short adds nothing.
    /// </summary>
    private static void AddCrossing(List<RunwayCrossing> crossings, HoldShortPoint hs, TaxiRouteReadbackInputs inputs)
    {
        if (!IsRunwayTarget(hs.TargetName!))
        {
            return;
        }

        string end = RunwayEndNamed(hs.TargetName!, inputs);
        if (!crossings.Any(c => string.Equals(c.Runway, end, StringComparison.OrdinalIgnoreCase)))
        {
            bool expedite = inputs.ExpeditedRunways.Any(runway => HoldShortAnnotator.TargetMatches(hs.TargetName, runway));
            crossings.Add(new RunwayCrossing(end, expedite));
        }
    }

    private static bool IsRunwayTarget(string target) => CommandParser.IsRunwayArg(target.Split('/')[0].Trim());

    /// <summary>
    /// The end of a runway (one end, or a pair like "28L/10R") the readback names: the end the clearance named, echoed as the
    /// controller said it; else, when the room lists an end of the runway as active, the end a pilot names it by
    /// (<see cref="RunwayCrossingEnd.Spoken"/>: the active end, the nearer one when both are listed); else its first end, as
    /// listed in the pair (not the nearest).
    /// </summary>
    private static string RunwayEndNamed(string runway, TaxiRouteReadbackInputs inputs)
    {
        var id = RunwayIdentifier.Parse(runway);
        if (inputs.NamedRunways.FirstOrDefault(id.Contains) is { } named)
        {
            return named;
        }

        RunwayEndChooser chooser = inputs.UnnamedEnd;
        return (chooser.Layout is { } layout) && RunwayCrossingEnd.HasActiveEnd(id, layout.AirportId, chooser.ActiveRunways)
            ? RunwayCrossingEnd.Spoken(chooser.Aircraft, runway, layout, chooser.ActiveRunways)
            : id.End1;
    }

    /// <summary>
    /// The readback of a taxiway or spot hold short's release — a <c>CROSS</c> naming a taxiway, or a bare <c>RES</c>
    /// pre-clearing the bar ahead: 7110.65 P/CG CONTINUE ("continue taxi"); release per 3-7-2.a.
    /// </summary>
    public const string ContinueTaxiRelease = "continue taxi";

    /// <summary>
    /// Renders a RES readback that carries CROSS / HS modifiers, composing the same
    /// "cross runway …" / "hold short of …" clauses as <see cref="RenderTaxiAlongRunway"/>
    /// after a "resume taxi" lead-in (7110.65 §3-7-2). Bare RES never reaches here.
    /// </summary>
    private static string RenderResume(ResumeCommand resume, CaptureFormatter fmt)
    {
        var body = new StringBuilder("resume taxi");

        if (resume.CrossRunways is { Count: > 0 } cross)
        {
            body.Append($", cross runway {string.Join(" and ", cross.Select(fmt.Runway))}");
        }

        if (resume.HoldShorts is { Count: > 0 } holdShorts)
        {
            body.Append($", hold short of {string.Join(" and ", holdShorts.Select(h => RenderHoldShortTarget(h, fmt)))}");
        }

        return body.ToString();
    }

    /// <summary>
    /// Renders a multi-runway (or HS-modified) CROSS readback, composing the same
    /// "cross runway … and …" / "hold short of …" clauses as <see cref="RenderResume"/>
    /// (7110.65 §3-7-2). A single-runway, no-HS CROSS never reaches here.
    /// </summary>
    private static string RenderCross(CrossRunwayCommand cross, CaptureFormatter fmt)
    {
        var body = new StringBuilder();

        // A taxiway target in the CROSS list releases a taxiway hold-short — that is read back as
        // 7110.65 P/CG CONTINUE ("continue taxi"); release per 3-7-2.a — never as a runway-crossing clause. Naming the
        // taxiway is not codified, so the clause carries no identifier.
        var runways = cross.RunwayIds.Where(CommandParser.IsRunwayArg).ToList();
        bool releasesTaxiwayHold = cross.RunwayIds.Count > runways.Count;

        if (runways.Count > 0)
        {
            body.Append($"cross runway {string.Join(" and ", runways.Select(fmt.Runway))}");
        }

        if (releasesTaxiwayHold)
        {
            if (body.Length > 0)
            {
                body.Append(", ");
            }
            body.Append(ContinueTaxiRelease);
        }

        // "Cross runway 28R without delay" (7110.65 §3-7-2.j), ahead of any hold-short.
        if (cross.Expedite && (body.Length > 0))
        {
            body.Append(" without delay");
        }

        if (cross.HoldShorts is { Count: > 0 } holdShorts)
        {
            if (body.Length > 0)
            {
                body.Append(", ");
            }
            body.Append($"hold short of {string.Join(" and ", holdShorts.Select(h => RenderHoldShortTarget(h, fmt)))}");
        }

        return body.ToString();
    }

    /// <summary>
    /// The crew's answer to a hold-short the aircraft is already too close to make: <c>unable to hold short
    /// of tango, stopping</c>. "Unable" is the P/CG word for exactly this — "indicates inability to comply
    /// with a specific instruction, request, or clearance" — and AIM 4-4-7.c leaves the choice with the crew:
    /// "it is the responsibility of the pilot to accept or refuse the clearance issued". The clearance being
    /// refused is the one AIM 2-3-5.b.3 defines: told to hold short of a taxiway, "the pilot MUST STOP so that
    /// no part of the aircraft extends beyond the holding position marking", which is what an aircraft inside
    /// its braking distance cannot do. The shape — the word, then what the aircraft will do instead — follows
    /// the 7110.65 §2-1-18.c template for the same exchange the other way round ("state the word 'UNABLE'
    /// and, time permitting, a reason").
    ///
    /// <para>Direct-rendered rather than added as a <see cref="PhraseologyRule"/>: rules are bidirectional,
    /// and this wording is a pilot answer that must never become controller input for STT to match.</para>
    /// </summary>
    /// <param name="target">The hold-short target as issued.</param>
    /// <param name="spoken">True for the spoken (TTS) form, false for the compact terminal form.</param>
    /// <returns>The rendered clause.</returns>
    public static string RenderUnableHoldShort(HoldShortTarget target, bool spoken) =>
        $"unable to hold short of {RenderHoldShortTarget(target, spoken ? Spoken : Terminal)}, stopping";

    /// <summary>
    /// Spoken form of one hold-short target in a direct-rendered clause: <c>runway two eight right</c>
    /// for a runway target (the "runway" word is mandatory — a taxiway target read back as a runway
    /// would assert a runway hold the controller never issued), <c>charlie</c> for a taxiway target.
    /// A located target (<c>C@J</c>) adds its ON-taxiway with the 7110.65 §3-7-2 locative —
    /// <c>charlie at juliett</c> — so the readback says which crossing binds.
    /// </summary>
    private static string RenderHoldShortTarget(HoldShortTarget target, CaptureFormatter fmt)
    {
        if (target.IsSpot)
        {
            return $"spot {fmt.Taxiway(target.Target)}";
        }

        string spoken = CommandParser.IsRunwayArg(target.Target) ? $"runway {fmt.Runway(target.Target)}" : fmt.Taxiway(target.Target);
        return target.OnTaxiway is { } onTaxiway ? $"{spoken} at {fmt.Taxiway(onTaxiway)}" : spoken;
    }

    /// <summary>
    /// Captures for a standalone <c>HS</c> readback, keyed to the rule the target type satisfies:
    /// <c>{rwy}</c> for a runway target ("hold short of runway …"), <c>{taxiway}</c> for a taxiway
    /// target ("hold short of …"). A located target adds <c>{hson}</c>, which selects the richer
    /// "… at {hson}" rule variant.
    /// </summary>
    private static IReadOnlyDictionary<string, string> HoldShortCommandArgs(HoldShortCommand h, CaptureFormatter fmt)
    {
        if (h.Target.IsSpot)
        {
            return Map("spot", fmt.Taxiway(h.Target.Target));
        }

        bool isRunway = CommandParser.IsRunwayArg(h.Target.Target);
        var dict = new Dictionary<string, string>
        {
            [isRunway ? "rwy" : "taxiway"] = isRunway ? fmt.Runway(h.Target.Target) : fmt.Taxiway(h.Target.Target),
        };
        if (h.Target.OnTaxiway is { } onTaxiway)
        {
            dict["hson"] = fmt.Taxiway(onTaxiway);
        }

        return dict;
    }

    private static IReadOnlyDictionary<string, string> Map(string k, string v) => new Dictionary<string, string> { [k] = v };

    private static IReadOnlyDictionary<string, string> Empty() => new Dictionary<string, string>(0);

    /// <summary>The <c>{alt}</c> capture and its busy group form <c>{alt:group}</c>, both rendered from <paramref name="feet"/>.</summary>
    private static IReadOnlyDictionary<string, string> AltitudeArgs(int feet, CaptureFormatter fmt) =>
        new Dictionary<string, string> { ["alt"] = fmt.Altitude(feet), [AltGroup] = fmt.AltitudeGroup(feet) };

    /// <summary>The <c>{spd}</c> capture and its busy group form <c>{spd:group}</c>, both rendered from <paramref name="knots"/>.</summary>
    private static IReadOnlyDictionary<string, string> SpeedArgs(int knots, CaptureFormatter fmt) =>
        new Dictionary<string, string> { ["spd"] = fmt.Speed(knots), [SpdGroup] = fmt.SpeedGroup(knots) };

    private const string AltGroup = "alt" + PilotVariant.GroupFormSuffix;
    private const string SpdGroup = "spd" + PilotVariant.GroupFormSuffix;

    /// <summary>
    /// A cross-fix readback with no rule of its own: "cross {fix} at|at or above|at or below {alt}" from the shape's variant set
    /// (<see cref="PhraseologyRules.CrossFixAt"/> and its siblings), which a varied pilot picks among; an at-or-above or
    /// at-or-below restriction with a speed adds "at {speed} knots" after the altitude, as the plain "at" form speaks it
    /// (7110.65 5-7-2.d), and is read back canonical in every band. <paramref name="offeredQuietRows"/> is whether the pick
    /// ran in the Quiet band with a row beside the canonical.
    /// </summary>
    private static string RenderCrossFix(CrossFixCommand cf, VariantRequest request, CaptureFormatter fmt, out bool offeredQuietRows)
    {
        offeredQuietRows = false;
        string canonical = CrossFixCanonical(cf, fmt);
        if (VariedCrossFixSet(cf) is not { } set)
        {
            return canonical;
        }

        ClauseVerbalization picked = VaryDirectClause(cf, set.Variants, canonical, CrossFixArgs(cf, fmt), request);
        offeredQuietRows = picked.OfferedQuietRows;
        return picked.Text ?? canonical;
    }

    /// <summary>
    /// Whether a cross-fix restriction is rendered by <see cref="RenderCrossFix"/>: every at-or-above or at-or-below
    /// restriction, and an "at" restriction with no speed; an "at" restriction with a speed reads back from its rule.
    /// </summary>
    private static bool RendersAsCrossFixBuilder(CrossFixCommand cf) => (cf.AltType != CrossFixAltitudeType.At) || (cf.Speed is null);

    /// <summary>The variant set a varied pilot picks a cross-fix restriction's readback from; null when it is held canonical (a speed).</summary>
    private static BuilderVariantSet? VariedCrossFixSet(CrossFixCommand cf) => (cf.Speed is null) ? CrossFixVariantSet(cf) : null;

    /// <summary>The canonical cross-fix readback ("cross SUNOL at or above 3400") in its terminal and spoken forms.</summary>
    /// <param name="cf">The crossing restriction.</param>
    /// <returns>The terminal and spoken readback.</returns>
    public static PilotSpeechText CrossFixReadback(CrossFixCommand cf) => new(CrossFixCanonical(cf, Terminal), CrossFixCanonical(cf, Spoken));

    /// <summary>The canonical cross-fix readback: its shape's pattern, then "at {speed} knots" when it carries a speed.</summary>
    private static string CrossFixCanonical(CrossFixCommand cf, CaptureFormatter fmt)
    {
        string canonical = RenderPattern(CrossFixVariantSet(cf).Pattern, CrossFixArgs(cf, fmt));
        return (cf.Speed is int knots) ? $"{canonical} at {fmt.Speed(knots)} knots" : canonical;
    }

    /// <summary>The variant set of a cross-fix restriction's shape, by its altitude type.</summary>
    internal static BuilderVariantSet CrossFixVariantSet(CrossFixCommand cf) =>
        cf.AltType switch
        {
            CrossFixAltitudeType.AtOrAbove => PhraseologyRules.CrossFixAtOrAbove,
            CrossFixAltitudeType.AtOrBelow => PhraseologyRules.CrossFixAtOrBelow,
            CrossFixAltitudeType.At => PhraseologyRules.CrossFixAt,
            _ => throw new ArgumentOutOfRangeException(nameof(cf), cf.AltType, "Unknown cross-fix altitude type."),
        };

    /// <summary>
    /// <paramref name="canonical"/>, or for a <see cref="PilotPersonality.Varied"/> pilot one seeded pick among it and
    /// <paramref name="variants"/>' rows of the context's band, as a rule's variants are picked.
    /// </summary>
    private static ClauseVerbalization VaryDirectClause(
        ParsedCommand cmd,
        PilotVariantSet variants,
        string canonical,
        IReadOnlyDictionary<string, string> args,
        VariantRequest request
    ) =>
        (request.Context.Personality == PilotPersonality.Varied)
            ? PickVariant(cmd, variants, canonical, args, request)
            : new ClauseVerbalization(canonical, OfferedQuietRows: false);

    private static IReadOnlyDictionary<string, string> CrossFixArgs(CrossFixCommand cf, CaptureFormatter fmt)
    {
        var dict = new Dictionary<string, string>
        {
            ["fix"] = fmt.Fix(cf.FixName),
            ["alt"] = fmt.Altitude(cf.Altitude),
            [AltGroup] = fmt.AltitudeGroup(cf.Altitude),
        };
        if (cf.Speed is int s)
        {
            dict["speed"] = fmt.Speed(s);
        }
        return dict;
    }

    /// <summary>
    /// Renders a <see cref="PhraseologyRule.Pattern"/> as a spoken English string.
    /// Drops trailing <c>?</c> on optional tokens but keeps the literal content.
    /// Substitutes <c>{name}</c> placeholders from <paramref name="args"/>; unknown placeholders
    /// are emitted verbatim (with braces) so the gap is visible during development.
    /// </summary>
    public static string RenderPattern(string[] pattern, IReadOnlyDictionary<string, string> args)
    {
        var sb = new StringBuilder();
        foreach (string rawToken in pattern)
        {
            string t = rawToken.EndsWith('?') ? rawToken[..^1] : rawToken;

            if (t.StartsWith('{') && t.EndsWith('}'))
            {
                string name = t[1..^1];
                if (name.EndsWith("..."))
                {
                    name = name[..^3];
                }

                string value = args.TryGetValue(name, out string? v) ? v : t;
                Append(sb, value);
            }
            else
            {
                Append(sb, t);
            }
        }

        return sb.ToString();
    }

    /// <summary>Appends <paramref name="s"/> after a space; a comma-led value (", left traffic") attaches without one.</summary>
    private static void Append(StringBuilder sb, string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return;
        }

        if ((sb.Length > 0) && !s.StartsWith(','))
        {
            sb.Append(' ');
        }

        sb.Append(s);
    }

    // --- Spoken-form formatters ---

    /// <summary>Heading 270 → "two seven zero" (always 3 digits, ATC convention).</summary>
    public static string HeadingDigits(MagneticHeading h)
    {
        int deg = ((int)Math.Round(h.Degrees) % 360 + 360) % 360;
        return string.Join(' ', deg.ToString("D3").Select(SpellDigit));
    }

    /// <summary>Relative-turn degrees: 45 → "forty five", 270 → "two seventy".</summary>
    public static string DegreesWords(int degrees)
    {
        if (degrees is >= 1 and <= 99)
        {
            return TwoDigitWords(degrees);
        }

        if (degrees is >= 100 and <= 360)
        {
            int hundreds = degrees / 100;
            int remainder = degrees % 100;
            string leading = SpellDigit((char)('0' + hundreds));
            return remainder switch
            {
                0 => $"{leading} hundred",
                < 10 => $"{leading} zero {SpellDigit((char)('0' + remainder))}",
                _ => $"{leading} {TwoDigitWords(remainder)}",
            };
        }

        return DigitsWords(degrees);
    }

    private static string TwoDigitWords(int value) =>
        value switch
        {
            0 => "zero",
            1 => "one",
            2 => "two",
            3 => "three",
            4 => "four",
            5 => "five",
            6 => "six",
            7 => "seven",
            8 => "eight",
            9 => "nine",
            10 => "ten",
            11 => "eleven",
            12 => "twelve",
            13 => "thirteen",
            14 => "fourteen",
            15 => "fifteen",
            16 => "sixteen",
            17 => "seventeen",
            18 => "eighteen",
            19 => "nineteen",
            < 30 => "twenty" + OnesSuffix(value - 20),
            < 40 => "thirty" + OnesSuffix(value - 30),
            < 50 => "forty" + OnesSuffix(value - 40),
            < 60 => "fifty" + OnesSuffix(value - 50),
            < 70 => "sixty" + OnesSuffix(value - 60),
            < 80 => "seventy" + OnesSuffix(value - 70),
            < 90 => "eighty" + OnesSuffix(value - 80),
            < 100 => "ninety" + OnesSuffix(value - 90),
            _ => DigitsWords(value),
        };

    private static string OnesSuffix(int value) => value == 0 ? "" : " " + TwoDigitWords(value);

    /// <summary>Altitude → "five thousand" / "flight level three three zero" (delegates to <see cref="AtcNumberParser"/>).</summary>
    public static string AltitudeWords(int feet) => AtcNumberParser.AltitudeToWords(feet);

    /// <summary>
    /// Altitude → the group form a busy pilot may speak (7110.65 2-4-17.b.1 NOTE): from 10,000 to 17,999 ft the thousands
    /// as one number and the hundreds as <see cref="AltitudeWords"/> speaks them ("eleven thousand", "seventeen thousand nine
    /// hundred"). A flight level, or any other altitude, is exactly <see cref="AltitudeWords"/>.
    /// </summary>
    public static string AltitudeGroupWords(int feet)
    {
        if (!IsGroupFormRange(feet))
        {
            return AltitudeWords(feet);
        }

        string thousands = $"{TwoDigitWords(feet / 1000)} thousand";
        string hundreds = AltitudeWords(feet % 1000);
        return hundreds.Length == 0 ? thousands : $"{thousands} {hundreds}";
    }

    /// <summary>Generic positive integer → digit-by-digit ("250" → "two five zero").</summary>
    public static string DigitsWords(int value, int minWidth = 0)
    {
        if (value < 0)
        {
            return value.ToString();
        }

        string s = minWidth > 0 ? value.ToString($"D{minWidth}") : value.ToString();
        return string.Join(' ', s.Select(SpellDigit));
    }

    /// <summary>
    /// Airspeed (knots) → separate digits ("two five zero"), the form a speed readback takes in every band and in the
    /// canonical readback (AIM 4-2-11; 7110.65 2-4-17.l.1). Only a busy pilot may speak the group form
    /// (<see cref="SpeedGroupWords"/>), and always with "knots" or "speed".
    /// </summary>
    public static string SpeedWords(int speedKnots) => DigitsWords(speedKnots);

    /// <summary>
    /// Airspeed (knots) → the group form a busy pilot may speak: round tens from 100 to 999 as "two hundred" / "two fifty",
    /// any other speed as separate digits.
    /// </summary>
    public static string SpeedGroupWords(int speedKnots)
    {
        if ((speedKnots is < 100 or >= 1000) || (speedKnots % 10 != 0))
        {
            return DigitsWords(speedKnots);
        }
        int hundreds = speedKnots / 100;
        int remainder = speedKnots % 100;
        string leading = SpellDigit((char)('0' + hundreds));
        return remainder == 0 ? $"{leading} hundred" : $"{leading} {TwoDigitWords(remainder)}";
    }

    public static string MachWords(double mach)
    {
        // 0.78 → "point seven eight"; 1.5 → "one point five".
        int integerPart = (int)Math.Floor(mach);
        double fractional = mach - integerPart;
        string fractionalDigits = ((int)Math.Round(fractional * 100)).ToString("D2");
        var sb = new StringBuilder();
        if (integerPart > 0)
        {
            sb.Append(SpellDigit((char)('0' + integerPart)));
            sb.Append(" point ");
        }
        else
        {
            sb.Append("point ");
        }

        sb.Append(string.Join(' ', fractionalDigits.Select(SpellDigit)));
        return sb.ToString();
    }

    /// <summary>
    /// Spoken radio frequency per FAA 7110.65 §2-4-17.k.1: separate digits before and after the decimal,
    /// "point" at the decimal. Truncates after the second fractional digit; drops trailing zeros so
    /// 121.5 MHz reads "one two one point five" (not "...point five zero"). Whole numbers like
    /// 369.0 read "three six nine point zero" (single zero after point).
    /// </summary>
    public static string FrequencyToWords(double mhz)
    {
        int integerPart = (int)Math.Floor(mhz);
        double fractional = mhz - integerPart;
        // 7110.65 §2-4-17.k.1: omit digits after the second decimal — truncate, don't round.
        // The 1e-9 nudge absorbs binary-fp drift (e.g. 119.6 → 119.60000000000001) so
        // 119.6 reads "point six" instead of "point five nine".
        int hundredths = (int)Math.Floor(fractional * 100 + 1e-9);
        var sb = new StringBuilder();
        sb.Append(string.Join(' ', integerPart.ToString().Select(SpellDigit)));
        sb.Append(" point ");
        if (hundredths == 0)
        {
            sb.Append("zero");
        }
        else if (hundredths % 10 == 0)
        {
            // Single non-zero tenths digit (e.g. 121.5 → "five", not "five zero")
            sb.Append(SpellDigit((char)('0' + hundredths / 10)));
        }
        else
        {
            sb.Append(string.Join(' ', hundredths.ToString("D2").Select(SpellDigit)));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Spoken radio frequency, grouped the way a pilot on a busy frequency says it when <paramref name="grouped"/>: the
    /// leading 1 dropped and the digits paired ("twenty five thirty five" for 125.35, "twenty one point five" for 121.5).
    /// Only a frequency from 110 to 199 MHz whose first decimal is not 0 groups; any other frequency, and every frequency
    /// when <paramref name="grouped"/> is false, reads as <see cref="FrequencyToWords(double)"/>. Truncates after the
    /// second decimal as that method does.
    /// </summary>
    public static string FrequencyToWords(double mhz, bool grouped)
    {
        int integerPart = (int)Math.Floor(mhz);
        int hundredths = (int)Math.Floor((mhz - integerPart) * 100 + 1e-9);
        int tenths = hundredths / 10;
        if (!grouped || (integerPart < 110) || (integerPart > 199) || (tenths == 0))
        {
            return FrequencyToWords(mhz);
        }

        string megahertz = TwoDigitWords(integerPart - 100);
        return (hundredths % 10) == 0 ? $"{megahertz} point {SpellDigit((char)('0' + tenths))}" : $"{megahertz} {TwoDigitWords(hundredths)}";
    }

    private static string SpellDigit(char c) =>
        c switch
        {
            '0' => "zero",
            '1' => "one",
            '2' => "two",
            '3' => "three",
            '4' => "four",
            '5' => "five",
            '6' => "six",
            '7' => "seven",
            '8' => "eight",
            '9' => "nine",
            _ => c.ToString(),
        };

    /// <summary>
    /// Spoken fix label for readbacks. Published VHF navaids use the same friendly name source as
    /// SPOS position anchors ("MOD" -> "Modesto VOR"); fixes registered in
    /// <c>FixPronunciations/*.json</c> use the first published pronunciation
    /// ("VPCOL" -> "Oakland Colliseum"); ordinary intersections keep the concise identifier form
    /// ("SUNOL" -> "sunol").
    /// </summary>
    /// <summary>
    /// Spells multiple fixes for a single DCT-style command. The phraseology pattern's
    /// <c>{fix}</c> placeholder accepts one rendered string, so multi-fix DCTs are expressed by
    /// joining each fix with <c>", then direct "</c>: e.g. <c>DCT OAK30NUM VPMID</c> becomes
    /// <c>"oak30num, then direct vpmid"</c>, which the rule pattern <c>"proceed direct to {fix}"</c>
    /// then renders as <c>"proceed direct to oak30num, then direct vpmid"</c>.
    /// </summary>
    public static string SpellFixSequence(IEnumerable<ResolvedFix> fixes) =>
        string.Join(", then direct ", fixes.Select(f => SpellFix(f.Name)).Where(s => !string.IsNullOrEmpty(s)));

    public static string SpellFix(string fix)
    {
        if (string.IsNullOrWhiteSpace(fix))
        {
            return "";
        }

        string trimmed = fix.Trim();
        if (TryGetNavigationDatabase() is { } navDb)
        {
            // Custom pronunciations win over auto-derived navaid/airport names: authors register
            // them precisely to override the default speech for visual reporting points and
            // hard-to-pronounce intersections (e.g. "VPCOL" → "Oakland Colliseum").
            IReadOnlyList<string> pronunciations = navDb.GetFixPronunciations(trimmed);
            if (pronunciations.Count > 0 && !string.IsNullOrWhiteSpace(pronunciations[0]))
            {
                return pronunciations[0];
            }

            // Authored display name — a custom-fix friendly name (e.g. "OAK30NUM" → "Oakland Runway
            // 30 Numbers") or a visual point defined for display only. Authors who name the fix have
            // already supplied the natural form pilots speak; no duplicate pronunciation entry needed.
            string? displayName = navDb.GetFixDisplayName(trimmed);
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }

            string? navaidName = navDb.GetNavaidName(trimmed);
            if (!string.IsNullOrWhiteSpace(navaidName))
            {
                return $"{TitleCase(navaidName)} {navDb.GetNavaidType(trimmed) ?? "VOR"}";
            }

            string? airportName = navDb.GetAirportName(trimmed);
            if (!string.IsNullOrWhiteSpace(airportName))
            {
                return PilotSayBuilder.FriendlyAirportName(airportName);
            }
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Renders a fix for operator-facing terminal text. When the fix has an authored display name
    /// it reads as "Name (ID)" (e.g. <c>VPCBT</c> → "Lake Chabot (VPCBT)"); otherwise the bare
    /// uppercase identifier. The display name keeps its natural case — used for pilot-readback echoes
    /// where the surrounding text is sentence-style.
    /// </summary>
    public static string FixDisplayText(string fix) => FormatFixDisplay(fix, uppercaseName: false);

    /// <summary>
    /// Like <see cref="FixDisplayText"/> but uppercases the display name ("LAKE CHABOT (VPCBT)") to
    /// match the terse all-caps fix-token style of RPO command-response messages.
    /// </summary>
    public static string FixDisplayTextUpper(string fix) => FormatFixDisplay(fix, uppercaseName: true);

    private static string FormatFixDisplay(string fix, bool uppercaseName)
    {
        if (string.IsNullOrWhiteSpace(fix))
        {
            return "";
        }

        string id = fix.Trim().ToUpperInvariant();
        string? displayName = TryGetNavigationDatabase()?.GetFixDisplayName(id);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return id;
        }

        return $"{(uppercaseName ? displayName.ToUpperInvariant() : displayName)} ({id})";
    }

    /// <summary>
    /// Renders a STAR/SID identifier for pilot phraseology. Terminal keeps the raw uppercase id
    /// ("RAZRR5"); TTS spells the name then the trailing version number as a cardinal word
    /// ("RAZRR5" → "razrr five", "LAURA2" → "laura two"). Used for descend-via / climb-via
    /// initial check-ins (AIM 5-4-1.b.2 / 5-2-9.b.9).
    /// </summary>
    public static (string Terminal, string Tts) ProcedureName(string procedureId)
    {
        string id = (procedureId ?? "").Trim();
        if (id.Length == 0)
        {
            return ("", "");
        }

        string terminal = id.ToUpperInvariant();

        int split = id.Length;
        while (split > 0 && char.IsDigit(id[split - 1]))
        {
            split--;
        }
        string name = id[..split];
        string version = id[split..];
        if (name.Length == 0 || version.Length == 0)
        {
            // No alpha+digit split (all digits or all letters) — spell the whole id like a fix.
            return (terminal, SpellFix(id));
        }

        string versionWords = int.TryParse(
            version,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int v
        )
            ? AtcNumberParser.CardinalWord(v)
            : version.ToLowerInvariant();
        return (terminal, $"{SpellFix(name)} {versionWords}");
    }

    /// <summary>
    /// Spoken airport name for readbacks where the caller knows the identifier refers to an
    /// airport (boundary holds tied to a Class B/C, primary-airport position anchors). Always
    /// uses the airport-name lookup — distinct from <see cref="SpellFix"/> which prefers the
    /// VOR/navaid name when the same code is published as both. Falls back to the bare
    /// identifier (NOT NATO-spelled letters) when the lookup misses, since published
    /// identifiers like "OAK" already read as a single word.
    /// </summary>
    public static string SpellAirportName(string airportIdent)
    {
        if (string.IsNullOrWhiteSpace(airportIdent))
        {
            return "";
        }

        string trimmed = airportIdent.Trim();
        if (TryGetNavigationDatabase() is { } navDb)
        {
            // GetAirportName handles ICAO ↔ FAA key reconciliation (KOAK → OAK and back).
            string? airportName = navDb.GetAirportName(trimmed);
            if (!string.IsNullOrWhiteSpace(airportName))
            {
                return PilotSayBuilder.FriendlyAirportName(airportName);
            }
        }

        return trimmed.ToUpperInvariant();
    }

    private static NavigationDatabase? TryGetNavigationDatabase()
    {
        try
        {
            return NavigationDatabase.Instance;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string TitleCase(string raw) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Trim().ToLowerInvariant());

    /// <summary>"28R" → "two eight right"; "08R" → "eight right". Letters expand; digits spell out.</summary>
    public static string SpellRunway(string runway)
    {
        if (string.IsNullOrEmpty(runway))
        {
            return "";
        }

        // Runway designators are 01–36, so a leading zero is padding (NormalizeDesignator) and is
        // not spoken — "runway eight right" / "runway nine", not "runway zero eight". Only the
        // first character can be a padding zero. Headings keep their leading zero via HeadingDigits.
        if (runway.Length > 1 && runway[0] == '0')
        {
            runway = runway[1..];
        }

        var sb = new StringBuilder();
        foreach (char c in runway)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            switch (c)
            {
                case 'L':
                case 'l':
                    sb.Append("left");
                    break;
                case 'R':
                case 'r':
                    sb.Append("right");
                    break;
                case 'C':
                case 'c':
                    sb.Append("center");
                    break;
                default:
                    sb.Append(char.IsDigit(c) ? SpellDigit(c) : char.ToLowerInvariant(c).ToString());
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Military training route designator in the form FAA JO 7110.65 §2-5-1.f prescribes: "State the
    /// letters 'I-R' or 'V-R' followed by the number of the route in group form."
    /// <para>
    /// "IR531" → "I-R five thirty one"; "VR52" → "V-R fifty two" — the section's own two examples.
    /// The prefix is hyphenated so text-to-speech reads the letters rather than a word, and an
    /// alternate-route letter suffix (AIM 3-5-2.d.1.c, e.g. "IR008A") expands phonetically.
    /// </para>
    /// </summary>
    public static string SpellMilitaryRoute(string designator)
    {
        if (string.IsNullOrWhiteSpace(designator))
        {
            return "";
        }

        string text = designator.Trim().Replace("-", string.Empty).ToUpperInvariant();
        if (text.Length < 3 || !char.IsLetter(text[0]) || !char.IsLetter(text[1]))
        {
            return designator;
        }

        string prefix = $"{char.ToLowerInvariant(text[0])}-{char.ToLowerInvariant(text[1])}";
        string rest = text[2..];
        string digits = new([.. rest.TakeWhile(char.IsDigit)]);
        string suffix = rest[digits.Length..];

        if (digits.Length == 0 || !int.TryParse(digits, out int number))
        {
            return designator;
        }

        // Group form says "hundred" for a round hundred — §2-5-1.e's own ATS-route example is
        // "Alfa Seven Hundred". The shared flight-number speller pairs digits instead ("nine zero
        // zero"), which is right for a callsign but not for a route number, so the hundreds case is
        // handled here rather than by changing a helper every callsign readback depends on.
        string groupForm =
            number >= 100 && number % 100 == 0
                ? $"{AtcNumberParser.FlightNumberToPairedWords(number / 100)} hundred"
                : AtcNumberParser.FlightNumberToPairedWords(number);
        string spoken = $"{prefix} {groupForm}";
        foreach (char c in suffix)
        {
            spoken += " " + NatoPhoneticAlphabet.SpellChar(c);
        }

        return spoken;
    }

    /// <summary>
    /// Aerial refueling track number in the form FAA JO 7110.65 §9-2-13 prescribes: "CLEARED TO
    /// CONDUCT REFUELING ALONG (number) TRACK".
    /// <para>
    /// The designator inverts against §2-5-1.f's training-route form. §9-2-13's phraseology names
    /// the <em>number</em> and puts "track" after it, so "AR312" is "three twelve track", not "a-r
    /// three twelve" — the letters are not spoken at all. The group form matches §2-5-1.e, and an
    /// alternate-track letter suffix ("AR3H") expands phonetically.
    /// </para>
    /// </summary>
    public static string SpellRefuelingTrack(string designator)
    {
        if (string.IsNullOrWhiteSpace(designator))
        {
            return "";
        }

        string text = designator.Trim().Replace("-", string.Empty).ToUpperInvariant();
        if (!text.StartsWith("AR", StringComparison.Ordinal))
        {
            return designator;
        }

        string rest = text[2..];
        string digits = new([.. rest.TakeWhile(char.IsDigit)]);
        string suffix = rest[digits.Length..];
        if (digits.Length == 0 || !int.TryParse(digits, out int number))
        {
            return designator;
        }

        string spoken =
            number >= 100 && number % 100 == 0
                ? $"{AtcNumberParser.FlightNumberToPairedWords(number / 100)} hundred"
                : AtcNumberParser.FlightNumberToPairedWords(number);
        foreach (char c in suffix)
        {
            spoken += " " + NatoPhoneticAlphabet.SpellChar(c);
        }

        return spoken;
    }

    /// <summary>
    /// A whole route of flight, element by element: "V495 SEA" → "victor four ninety five, seattle".
    /// <para>
    /// FAA JO 7110.65 §2-5-1.a states an ATS route by its phonetic-letter prefix and its number in
    /// group form ("Victor Twelve"), while a fix keeps its own pronunciation via
    /// <see cref="SpellFix"/>. Elements are comma-separated so a synthesiser pauses between them
    /// rather than running the route into one word.
    /// </para>
    /// </summary>
    public static string SpellRouteString(string route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return "";
        }

        IEnumerable<string> spoken = route.Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries).Select(SpellRouteElement);
        return string.Join(", ", spoken);
    }

    /// <summary>
    /// One route element. An airway is a single leading letter followed by digits — "V495", "J80",
    /// "Q42" — which is a shape no fix or navaid identifier takes, so the two never collide.
    /// </summary>
    private static string SpellRouteElement(string element)
    {
        string text = element.Trim().ToUpperInvariant();
        if (text.Length < 2 || !char.IsLetter(text[0]) || !text[1..].All(char.IsDigit))
        {
            return SpellFix(text);
        }

        if (!int.TryParse(text[1..], out int number))
        {
            return SpellFix(text);
        }

        // §2-5-1.e's own example is "Alfa Seven Hundred", so a round hundred says "hundred" rather
        // than pairing the trailing zeros the way a flight number would.
        string groupForm =
            number >= 100 && number % 100 == 0
                ? $"{AtcNumberParser.FlightNumberToPairedWords(number / 100)} hundred"
                : AtcNumberParser.FlightNumberToPairedWords(number);
        return $"{NatoPhoneticAlphabet.SpellChar(text[0])} {groupForm}";
    }

    /// <summary>Taxiway "B6" → "bravo six"; "AA" → "alpha alpha".</summary>
    public static string SpellTaxiway(string tw)
    {
        if (string.IsNullOrEmpty(tw))
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (char c in tw)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(char.IsDigit(c) ? SpellDigit(c) : NatoPhoneticAlphabet.SpellChar(c));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parking / spot name as spoken: codes character by character ("B12" → "bravo one two", "7A" →
    /// "seven alpha", "41-10" → "four one dash one zero", "FDX1" → "foxtrot delta xray one"), but a
    /// pronounceable word — four or more letters with a vowel, the "CARGO1" / "JANET" / "HELI1" names
    /// real layouts carry — is said as the word ("cargo one"). A leading word that merely repeats the
    /// destination noun is dropped: PHL's "SPOT7" reads "spot seven", not "spot spot seven".
    /// </summary>
    public static string SpellDestinationName(string name, string noun)
    {
        var runs = new List<string>();
        string trimmed = name.Trim();
        int i = 0;
        while (i < trimmed.Length)
        {
            char c = trimmed[i];
            if (char.IsLetter(c))
            {
                int start = i;
                while (i < trimmed.Length && char.IsLetter(trimmed[i]))
                {
                    i++;
                }

                string letters = trimmed[start..i];
                if (runs.Count == 0 && string.Equals(letters, noun, StringComparison.OrdinalIgnoreCase) && i < trimmed.Length)
                {
                    continue;
                }

                runs.Add(
                    IsPronounceableWord(letters) ? letters.ToLowerInvariant() : string.Join(' ', letters.Select(NatoPhoneticAlphabet.SpellChar))
                );
                continue;
            }

            if (char.IsDigit(c))
            {
                runs.Add(SpellDigit(c));
            }
            else if (c == '-')
            {
                runs.Add("dash");
            }

            i++;
        }

        return string.Join(' ', runs);
    }

    /// <summary>A letter run that reads as a word: four or more letters, at least one of them a vowel.</summary>
    public static bool IsPronounceableWord(string letters) => (letters.Length >= 4) && letters.Any(c => "AEIOUaeiou".Contains(c));

    /// <summary>True when any letter run of a name reads as a word ("KILO RAMP", "SIGNATURE", "CARGO1") — such a name
    /// takes no gate/parking noun.</summary>
    public static bool ContainsPronounceableWord(string name)
    {
        int i = 0;
        while (i < name.Length)
        {
            if (!char.IsLetter(name[i]))
            {
                i++;
                continue;
            }

            int start = i;
            while ((i < name.Length) && char.IsLetter(name[i]))
            {
                i++;
            }

            if (IsPronounceableWord(name[start..i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Approach name like "ILS28R" → "I L S two eight right". Letters via NATO; L/R/C as words.</summary>
    public static string SpellApproach(string approachId)
    {
        if (string.IsNullOrEmpty(approachId))
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (char c in approachId)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            switch (c)
            {
                case 'L':
                case 'l':
                    sb.Append("left");
                    break;
                case 'R':
                case 'r':
                    sb.Append("right");
                    break;
                case 'C':
                case 'c':
                    sb.Append("center");
                    break;
                default:
                    sb.Append(char.IsDigit(c) ? SpellDigit(c) : NatoPhoneticAlphabet.SpellChar(c));
                    break;
            }
        }

        return sb.ToString();
    }

    // --- Compact terminal-form formatters (public: reused by PilotResponder's dual-output clauses) ---

    /// <summary>"08R" → "8R", "28L" → "28L". Drops the padding leading zero, uppercases.</summary>
    public static string CompactRunway(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "";
        }

        string s = id.Trim().ToUpperInvariant();
        if (s.Length > 1 && s[0] == '0')
        {
            s = s[1..];
        }
        return s;
    }

    /// <summary>Heading 270 → "270" (3-digit, leading zeros kept — controllers write "090").</summary>
    public static string HeadingNumber(MagneticHeading h)
    {
        int deg = ((int)Math.Round(h.Degrees) % 360 + 360) % 360;
        return deg.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Altitude → "5000" / "FL350" (mirrors the FL180+ threshold of the spoken form).</summary>
    public static string CompactAltitude(int feet)
    {
        if (feet <= 0)
        {
            return "";
        }

        if (feet >= 18000 && feet % 100 == 0)
        {
            return "FL" + (feet / 100).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
        }
        return feet.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Per-capture token formatting strategy. The rule pattern (literals) is shared between the
    /// spoken (TTS) and compact terminal forms; only these token renderings differ.
    /// </summary>
    private abstract class CaptureFormatter
    {
        public abstract string Runway(string id);
        public abstract string Approach(string id);

        /// <summary>This formatter's form of a text built in both forms: the spoken or the terminal one.</summary>
        public abstract string Form(PilotSpeechText text);
        public abstract string Heading(MagneticHeading h);
        public abstract string Degrees(int degrees);
        public abstract string Altitude(int feet);

        /// <summary>The busy group form of an altitude, from the same value as <see cref="Altitude"/>.</summary>
        public abstract string AltitudeGroup(int feet);
        public abstract string Speed(int knots);

        /// <summary>The busy group form of a speed, from the same value as <see cref="Speed"/>.</summary>
        public abstract string SpeedGroup(int knots);
        public abstract string Mach(double mach);
        public abstract string Fix(string fix);
        public abstract string FixSequence(IEnumerable<ResolvedFix> fixes);
        public abstract string Taxiway(string tw);
        public abstract string Parking(string name);
        public abstract string Spot(string name);
        public abstract string Callsign(string callsign);
        public abstract string Squawk(int code);
        public abstract string TaxiPath(TaxiCommand taxi);
        public abstract string TaxiTurn(string taxiway, TurnDirection? hint);

        /// <summary>A runway taxied ALONG as a path segment: "on runway two eight right" / "on 28R" (7110.65 §3-7-2.a "ON (runway)").</summary>
        public abstract string RunwaySegment(string id);

        /// <summary>Separator between taxi-path segments: comma-spoken, space-terminal.</summary>
        public abstract string TaxiSeparator { get; }
    }

    private sealed class SpokenCaptureFormatter : CaptureFormatter
    {
        public override string Runway(string id) => SpellRunway(id);

        public override string Approach(string id) => SpellApproach(id);

        public override string Form(PilotSpeechText text) => text.Tts;

        public override string Heading(MagneticHeading h) => HeadingDigits(h);

        public override string Degrees(int degrees) => DegreesWords(degrees);

        public override string Altitude(int feet) => AltitudeWords(feet);

        public override string AltitudeGroup(int feet) => AltitudeGroupWords(feet);

        public override string Speed(int knots) => SpeedWords(knots);

        public override string SpeedGroup(int knots) => SpeedGroupWords(knots);

        public override string Mach(double mach) => MachWords(mach);

        public override string Fix(string fix) => SpellFix(fix);

        public override string FixSequence(IEnumerable<ResolvedFix> fixes) => SpellFixSequence(fixes);

        public override string Taxiway(string tw) => SpellTaxiway(tw);

        public override string Parking(string name) => SpellDestinationName(name, "parking");

        public override string Spot(string name) => SpellDestinationName(name, "spot");

        public override string Callsign(string callsign) => CallsignParser.IcaoToSpoken(callsign);

        public override string Squawk(int code) => DigitsWords(code, minWidth: 4);

        public override string TaxiPath(TaxiCommand taxi) => RenderTaxiPath(taxi, this, ", ");

        public override string TaxiTurn(string taxiway, TurnDirection? hint) =>
            hint switch
            {
                TurnDirection.Right => $"right on {SpellTaxiway(taxiway)}",
                TurnDirection.Left => $"left on {SpellTaxiway(taxiway)}",
                _ => SpellTaxiway(taxiway),
            };

        public override string RunwaySegment(string id) => $"on runway {SpellRunway(id)}";

        public override string TaxiSeparator => ", ";
    }

    private sealed class TerminalCaptureFormatter : CaptureFormatter
    {
        public override string Runway(string id) => CompactRunway(id);

        public override string Approach(string id) => id.Trim().ToUpperInvariant();

        public override string Form(PilotSpeechText text) => text.Terminal;

        public override string Heading(MagneticHeading h) => HeadingNumber(h);

        public override string Degrees(int degrees) => degrees.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public override string Altitude(int feet) => CompactAltitude(feet);

        public override string AltitudeGroup(int feet) => CompactAltitude(feet);

        public override string Speed(int knots) => knots.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public override string SpeedGroup(int knots) => Speed(knots);

        public override string Mach(double mach) => mach.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        public override string Fix(string fix) => FixDisplayText(fix);

        public override string FixSequence(IEnumerable<ResolvedFix> fixes) =>
            string.Join(", ", fixes.Select(f => FixDisplayText(f.Name)).Where(s => s.Length > 0));

        public override string Taxiway(string tw) => tw.Trim().ToUpperInvariant();

        public override string Parking(string name) => name.Trim().ToUpperInvariant();

        public override string Spot(string name) => name.Trim().ToUpperInvariant();

        public override string Callsign(string callsign) => callsign.Trim().ToUpperInvariant();

        public override string Squawk(int code) => code.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

        public override string TaxiPath(TaxiCommand taxi) => RenderTaxiPath(taxi, this, " ");

        public override string TaxiTurn(string taxiway, TurnDirection? hint) =>
            hint switch
            {
                TurnDirection.Right => $"right on {Taxiway(taxiway)}",
                TurnDirection.Left => $"left on {Taxiway(taxiway)}",
                _ => Taxiway(taxiway),
            };

        public override string RunwaySegment(string id) => $"on {CompactRunway(id)}";

        public override string TaxiSeparator => " ";
    }
}
