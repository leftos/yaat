using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Yaat.Client.Services;
using Yaat.Sim.Speech;

namespace Yaat.SpeechSandbox;

/// <summary>
/// One controller-phraseology template: a spoken pattern and the canonical it must map to, with
/// <c>{slot}</c> placeholders filled per case. <see cref="Family"/> names the
/// <c>PhraseologyRules</c> builder whose rules the template exercises (<c>Compound</c> for
/// multi-clause transmissions).
/// </summary>
public sealed record SynthTemplate(string Key, string Family, string SpokenPattern, string CanonicalPattern);

/// <summary>A rendered case whose transcript did not map to its expected canonical and callsign through the rule mapper.</summary>
public sealed record TemplateGap(
    [property: JsonPropertyName("template")] string Template,
    [property: JsonPropertyName("transcript")] string Transcript,
    [property: JsonPropertyName("expectedCanonical")] string ExpectedCanonical,
    [property: JsonPropertyName("gotCanonical")] string? GotCanonical,
    [property: JsonPropertyName("failureReason")] string FailureReason
)
{
    /// <summary>The console line for this gap: <c>GAP &lt;template&gt;: "&lt;transcript&gt;" → &lt;got or none&gt; (&lt;reason&gt;)</c>.</summary>
    public string Describe() => $"GAP {Template}: \"{Transcript}\" → {GotCanonical ?? "none"} ({FailureReason})";
}

/// <summary>A template rendered with one set of slot values, plus the Piper voice it will be spoken with.</summary>
/// <param name="Template">The template the case came from.</param>
/// <param name="Index">Position in the planned case list (stable per seed).</param>
/// <param name="Expectation">Transcript, canonical, callsign and the scenario context the label holds under.</param>
/// <param name="Speaker">Piper multi-speaker id.</param>
/// <param name="Speed">Piper speaking-rate multiplier.</param>
public sealed record RenderedCase(SynthTemplate Template, int Index, EvalExpectation Expectation, int Speaker, float Speed);

/// <summary>The verified case list for one generation run, plus every gap found.</summary>
/// <param name="VerifiedTemplates">Templates whose up-front render verified — the only ones sampled.</param>
/// <param name="Cases">Cases whose labels verified, in plan order.</param>
/// <param name="Gaps">Renders that failed verification: one per unverifiable template, plus any failing sampled case.</param>
public sealed record SynthPlan(IReadOnlyList<SynthTemplate> VerifiedTemplates, IReadOnlyList<RenderedCase> Cases, IReadOnlyList<TemplateGap> Gaps);

/// <summary>
/// The synthetic controller-phraseology catalog and its pure pipeline: template sampling, slot
/// rendering with real KOAK / KSFO names, and label verification through the production rule
/// mapper (no audio, no models). Shared by <c>--synth-corpus</c> and <c>--atc-ouroboros</c>.
/// </summary>
public static partial class SynthTemplates
{
    /// <summary>Rule-family name for templates that chain clauses from several families.</summary>
    public const string CompoundFamily = "Compound";

    /// <summary>
    /// Every template, grouped by the <c>PhraseologyRules</c> builder it exercises. Spoken sides use
    /// ATC word forms; canonical sides are the rule's canonical template filled with the same slot
    /// values. Slots: {hdg} {alt} {fl} {spd} {deg} {rwy} {rwy2} {sq} {fix} {twy} {hson} {path}
    /// {parking} {spot} {miles} {type} {traffic} {cardinal}.
    /// </summary>
    public static IReadOnlyList<SynthTemplate> All { get; } =
    [
        // HeadingRules
        new("tl", "HeadingRules", "turn left heading {hdg}", "TL {hdg}"),
        new("tr", "HeadingRules", "turn right heading {hdg}", "TR {hdg}"),
        new("fh", "HeadingRules", "fly heading {hdg}", "FH {hdg}"),
        new("hdg", "HeadingRules", "heading {hdg}", "FH {hdg}"),
        new("rell", "HeadingRules", "turn {deg} degrees left", "RELL {deg}"),
        new("relr", "HeadingRules", "turn {deg} degrees right", "RELR {deg}"),
        new("fph", "HeadingRules", "fly present heading", "FPH"),
        // AltitudeSpeedRules
        new("cm", "AltitudeSpeedRules", "climb and maintain {alt}", "CM {alt}"),
        new("dm", "AltitudeSpeedRules", "descend and maintain {alt}", "DM {alt}"),
        new("cm-fl", "AltitudeSpeedRules", "climb and maintain {fl}", "CM {fl}"),
        new("dm-fl", "AltitudeSpeedRules", "descend and maintain {fl}", "DM {fl}"),
        new("mnt-alt", "AltitudeSpeedRules", "maintain {alt}", "CM {alt}"),
        new("exp-climb", "AltitudeSpeedRules", "expedite climb", "EXP"),
        new("spd-reduce", "AltitudeSpeedRules", "reduce speed to {spd}", "SPD {spd}"),
        new("spd-increase", "AltitudeSpeedRules", "increase speed to {spd}", "SPD {spd}"),
        new("spd-knots", "AltitudeSpeedRules", "maintain {spd} knots", "SPD {spd}"),
        new("spd-maintain", "AltitudeSpeedRules", "maintain speed {spd}", "SPD {spd}"),
        new("spd-until", "AltitudeSpeedRules", "maintain {spd} knots until {fix}", "SPD {spd} UNTIL {fix}"),
        new("rns", "AltitudeSpeedRules", "resume normal speed", "RNS"),
        new("dsr", "AltitudeSpeedRules", "delete speed restrictions", "DSR"),
        new("rfas", "AltitudeSpeedRules", "reduce to final approach speed", "RFAS"),
        new("cvia", "AltitudeSpeedRules", "climb via sid", "CVIA"),
        new("cvia-alt", "AltitudeSpeedRules", "climb via sid except maintain {alt}", "CVIA {alt}"),
        // NavigationRules
        new("dct", "NavigationRules", "proceed direct {fix}", "DCT {fix}"),
        new("dct-fly", "NavigationRules", "fly direct {fix}", "DCT {fix}"),
        new("tldct", "NavigationRules", "turn left direct {fix}", "TLDCT {fix}"),
        new("trdct", "NavigationRules", "turn right direct {fix}", "TRDCT {fix}"),
        new("adct", "NavigationRules", "when able direct {fix}", "ADCT {fix}"),
        new("cfix", "NavigationRules", "cross {fix} at {alt}", "CFIX {fix} AT {alt}"),
        new("cfix-above", "NavigationRules", "cross {fix} at or above {alt}", "CFIX {fix} A{alt}"),
        new("cfix-spd", "NavigationRules", "cross {fix} at and maintain {alt} at {spd} knots", "CFIX {fix} AT {alt} {spd}"),
        new("depart", "NavigationRules", "depart {fix} heading {hdg}", "DEPART {fix} {hdg}"),
        // TowerRules
        new("cto", "TowerRules", "runway {rwy} cleared for takeoff", "CTO"),
        new("cto-icao", "TowerRules", "cleared for takeoff runway {rwy}", "CTO"),
        new("cland", "TowerRules", "runway {rwy} cleared to land", "CLAND"),
        new("cland-icao", "TowerRules", "cleared to land runway {rwy}", "CLAND"),
        new("luaw", "TowerRules", "runway {rwy} line up and wait", "LUAW"),
        new("lahso", "TowerRules", "runway {rwy} cleared to land hold short of runway {rwy2}", "LAHSO {rwy2}"),
        new("ga", "TowerRules", "go around", "GA"),
        new("ga-hdg", "TowerRules", "go around fly heading {hdg}", "GA {hdg}"),
        new("ga-mrt", "TowerRules", "go around make right traffic", "GA MRT"),
        new("tg", "TowerRules", "runway {rwy} cleared touch and go", "TG"),
        new("sg", "TowerRules", "cleared for stop and go", "SG"),
        new("la", "TowerRules", "cleared for low approach", "LA"),
        new("copt", "TowerRules", "cleared for the option", "COPT"),
        new("ctoc", "TowerRules", "cancel takeoff clearance", "CTOC"),
        new("cwt", "TowerRules", "caution wake turbulence", "CWT"),
        new("clbrv", "TowerRules", "cleared into bravo airspace", "CLBRV"),
        // ApproachRules
        new("capp-ils", "ApproachRules", "cleared ils runway {rwy} approach", "CAPP ILS{rwy}"),
        new("capp-rnav", "ApproachRules", "cleared rnav runway {rwy} approach", "CAPP RNAV{rwy}"),
        new("capp-loc", "ApproachRules", "cleared localizer runway {rwy} approach", "CAPP LOC{rwy}"),
        new("capp", "ApproachRules", "cleared for the approach", "CAPP"),
        new("cva", "ApproachRules", "cleared visual approach runway {rwy}", "CVA {rwy}"),
        new("japp", "ApproachRules", "join the ils runway {rwy} approach", "JAPP ILS{rwy}"),
        new("eapp-ils", "ApproachRules", "expect ils runway {rwy} approach", "EAPP ILS{rwy}"),
        new("eapp-vis", "ApproachRules", "expect visual approach runway {rwy}", "EAPP VIS{rwy}"),
        new("follow", "ApproachRules", "follow {traffic}", "FOLLOW {traffic}"),
        new("rfis", "ApproachRules", "report field in sight", "RFIS"),
        new("rtis", "ApproachRules", "report traffic in sight", "RTIS"),
        // TrafficAdvisoryRules
        new("rtis-nr", "TrafficAdvisoryRules", "traffic off your nose and to the right {miles} miles a {type}", "RTIS NR {miles} {type}"),
        new("rtis-left", "TrafficAdvisoryRules", "traffic off your left {miles} miles a {type}", "RTIS L {miles} {type}"),
        new("rtis-dw", "TrafficAdvisoryRules", "traffic on a {miles} mile left downwind for runway {rwy} a {type}", "RTIS DW L {miles} {rwy} {type}"),
        new("rtis-final", "TrafficAdvisoryRules", "traffic on a {miles} mile final for runway {rwy} a {type}", "RTIS FINAL {miles} {rwy} {type}"),
        // PtacRules
        new(
            "ptac-tl-ils",
            "PtacRules",
            "turn left heading {hdg} descend and maintain {alt} cleared ils runway {rwy} approach",
            "PTAC {hdg} {alt} ILS{rwy}"
        ),
        new(
            "ptac-tr-ils",
            "PtacRules",
            "turn right heading {hdg} climb and maintain {alt} cleared ils runway {rwy} approach",
            "PTAC {hdg} {alt} ILS{rwy}"
        ),
        new(
            "ptac-fh-rnav",
            "PtacRules",
            "fly heading {hdg} descend and maintain {alt} cleared rnav runway {rwy} approach",
            "PTAC {hdg} {alt} RNAV{rwy}"
        ),
        // PatternRules
        new("eld", "PatternRules", "enter left downwind runway {rwy}", "ELD {rwy}"),
        new("erd", "PatternRules", "runway {rwy} enter right downwind", "ERD {rwy}"),
        new("erb", "PatternRules", "runway {rwy} enter right base", "ERB {rwy}"),
        new("ef", "PatternRules", "make straight in runway {rwy}", "EF {rwy}"),
        new("mrt", "PatternRules", "runway {rwy} make right traffic", "MRT {rwy}"),
        new("mlt", "PatternRules", "make left traffic", "MLT"),
        new("tb", "PatternRules", "turn base", "TB"),
        new("ext", "PatternRules", "extend downwind", "EXT"),
        new("sa", "PatternRules", "make short approach", "SA"),
        new("l360", "PatternRules", "make left three sixty", "L360"),
        new("r270", "PatternRules", "make right two seventy", "R270"),
        new("circle", "PatternRules", "circle the airport", "CIRCLE"),
        // HoldRules
        new("hfix", "HoldRules", "hold at {fix}", "HFIX {fix}"),
        new("hfixl", "HoldRules", "hold at {fix} left turns", "HFIXL {fix}"),
        new("hfixr", "HoldRules", "hold at {fix} right turns", "HFIXR {fix}"),
        new("hppr", "HoldRules", "hold present position right turns", "HPPR"),
        // HelicopterRules
        new("atxi", "HelicopterRules", "cleared for air taxi", "ATXI"),
        new("atxi-rwy", "HelicopterRules", "air taxi to runway {rwy} at {twy}", "ATXI {rwy}@{twy}"),
        new("ctopp", "HelicopterRules", "cleared for takeoff present position", "CTOPP"),
        // TransponderRules
        new("sq", "TransponderRules", "squawk {sq}", "SQ {sq}"),
        new("sqvfr", "TransponderRules", "squawk vfr", "SQVFR"),
        new("sqnorm", "TransponderRules", "squawk normal", "SQNORM"),
        new("sqsby", "TransponderRules", "squawk standby", "SQSBY"),
        new("ident", "TransponderRules", "squawk ident", "IDENT"),
        // GroundRules
        new("taxi-rwy", "GroundRules", "taxi to runway {rwy}", "TAXI {rwy}"),
        new("taxi-via", "GroundRules", "taxi via {path}", "TAXI {path}"),
        new("rwy-taxi-via", "GroundRules", "runway {rwy} taxi via {path}", "TAXI {path} {rwy}"),
        new("taxi-hs", "GroundRules", "taxi via {path} hold short of runway {rwy}", "TAXI {path} HS {rwy}"),
        new("taxi-cross", "GroundRules", "taxi via {path} cross runway {rwy}", "TAXI {path} CROSS {rwy}"),
        new("taxi-cross-hs", "GroundRules", "taxi via {path} cross runway {rwy} hold short of runway {rwy2}", "TAXI {path} CROSS {rwy} HS {rwy2}"),
        new("taxi-parking", "GroundRules", "taxi to parking {parking} via {path}", "TAXI {path} @{parking}"),
        new("taxi-gate", "GroundRules", "taxi via {path} to gate {parking}", "TAXI {path} @{parking}"),
        new("taxi-spot", "GroundRules", "taxi to spot {spot}", "TAXI ${spot}"),
        new("taxi-parking-hs", "GroundRules", "taxi via {path} to parking {parking} hold short of runway {rwy}", "TAXI {path} @{parking} HS {rwy}"),
        new("taxi-parking-cross", "GroundRules", "taxi to parking {parking} via {path} cross runway {rwy}", "TAXI {path} @{parking} CROSS {rwy}"),
        new("hold", "GroundRules", "hold position", "HOLD"),
        new("res", "GroundRules", "continue taxi", "RES"),
        new("cross", "GroundRules", "cross runway {rwy}", "CROSS {rwy}"),
        new("hs-rwy", "GroundRules", "hold short of runway {rwy}", "HS {rwy}"),
        new("hs-twy-at", "GroundRules", "hold short of {twy} at {hson}", "HS {twy}@{hson}"),
        new("push", "GroundRules", "pushback approved", "PUSH"),
        new("push-onto", "GroundRules", "pushback onto {twy} approved", "PUSH {twy}"),
        new("push-face", "GroundRules", "pushback approved facing {cardinal}", "PUSH FACE {cardinal}"),
        new("el", "GroundRules", "exit left at {twy}", "EL {twy}"),
        new("er", "GroundRules", "exit right", "ER"),
        new("giveway", "GroundRules", "give way to {traffic}", "GIVEWAY {traffic}"),
        new("followg", "GroundRules", "follow {traffic} on ground", "FOLLOWG {traffic}"),
        // BroadcastRules
        new("salt", "BroadcastRules", "say altitude", "SALT"),
        new("sspd", "BroadcastRules", "say speed", "SSPD"),
        new("shdg", "BroadcastRules", "say heading", "SHDG"),
        new("spos", "BroadcastRules", "report position", "SPOS"),
        new("seapp", "BroadcastRules", "say expected approach", "SEAPP"),
        // Compound transmissions (two and three clauses)
        new("dm-spd", CompoundFamily, "descend and maintain {alt} reduce speed to {spd}", "DM {alt}, SPD {spd}"),
        new("tr-dm", CompoundFamily, "turn right heading {hdg} descend and maintain {alt}", "TR {hdg}, DM {alt}"),
        new("tl-cm", CompoundFamily, "turn left heading {hdg} climb and maintain {alt}", "TL {hdg}, CM {alt}"),
        new("dct-dm", CompoundFamily, "proceed direct {fix} descend and maintain {alt}", "DCT {fix}, DM {alt}"),
        new("sq-cm", CompoundFamily, "squawk {sq} climb and maintain {alt}", "SQ {sq}, CM {alt}"),
        new("cross-hs", CompoundFamily, "cross runway {rwy} hold short of runway {rwy2}", "CROSS {rwy}, HS {rwy2}"),
        new("fh-cm-spd", CompoundFamily, "fly heading {hdg} climb and maintain {alt} maintain {spd} knots", "FH {hdg}, CM {alt}, SPD {spd}"),
        new("tl-dm-spd", CompoundFamily, "turn left heading {hdg} descend and maintain {alt} reduce speed to {spd}", "TL {hdg}, DM {alt}, SPD {spd}"),
    ];

    private static readonly string[] CallsignPool = ["UAL234", "SWA1943", "DAL512", "AAL2231", "ASA331", "N346G", "N9225L", "N514RM"];

    /// <summary>Runway designators of the two Bay Area airports every case's scenario context carries.</summary>
    private static readonly Dictionary<string, List<string>> AirportRunways = new()
    {
        ["KOAK"] = ["28R", "28L", "10R", "10L", "30", "12", "33", "15"],
        ["KSFO"] = ["28L", "28R", "10L", "10R", "01L", "01R", "19L", "19R"],
    };

    private static readonly string[] RunwayPool = [.. AirportRunways.Values.SelectMany(r => r).Distinct()];

    /// <summary>
    /// KOAK single-letter taxiways. T is left out because KOAK also has TC and TE, which a spoken
    /// "tango charlie" / "tango echo" run legitimately collapses to.
    /// </summary>
    private static readonly string[] TaxiwayPool = ["A", "B", "C", "D", "E", "F", "G", "H", "J", "K", "L", "P", "R", "S", "U", "V", "W"];

    /// <summary>KOAK parking positions: (layout name, spoken form).</summary>
    private static readonly (string Name, string Spoken)[] ParkingPool =
    [
        ("CARGO1", "cargo one"),
        ("GA5", "golf alpha five"),
        ("S12", "sierra one two"),
        ("KAI3", "kilo alpha india three"),
        ("FDX12", "foxtrot delta xray one two"),
    ];

    private static readonly string[] SpotPool = ["1", "2", "3", "4", "5", "7", "9"];
    private static readonly string[] FixPool = ["CEPIN", "SUNOL", "ALTAM"];
    private static readonly string[] AircraftTypePool = ["cessna", "boeing", "airbus", "cirrus"];
    private static readonly string[] CardinalPool = ["north", "south", "east", "west"];
    private static readonly string[] DigitWords = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "niner"];

    // LibriTTS-R medium is multi-speaker; a spread of ids + speeds approximates voice variety.
    // Faster speeds mimic rapid-fire controller delivery.
    private static readonly int[] SpeakerPool = [50, 92, 147, 246, 421, 588, 700, 810];
    private static readonly float[] SpeedPool = [0.9f, 1.0f, 1.1f, 1.2f];

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex SlotRegex();

    /// <summary>
    /// Picks <paramref name="count"/> templates: whole round-robin passes over
    /// <paramref name="templates"/> first (so each gets at least <c>count / templates.Count</c>),
    /// then the remainder drawn without replacement from a seeded shuffle. Deterministic per seed.
    /// </summary>
    public static List<SynthTemplate> SampleTemplates(IReadOnlyList<SynthTemplate> templates, int count, int seed)
    {
        var picked = new List<SynthTemplate>(count);
        int fullRounds = count / templates.Count;
        for (int round = 0; round < fullRounds; round++)
        {
            picked.AddRange(templates);
        }
        SynthTemplate[] shuffled = [.. templates];
        new Random(seed).Shuffle(shuffled);
        picked.AddRange(shuffled.Take(count - picked.Count));
        return picked;
    }

    /// <summary>
    /// Verifies one render of every template up front, then samples <paramref name="count"/> cases
    /// from the templates that verified only, rendering and verifying each case again. Anything that
    /// does not verify — a whole template, or one slot combination of a verified template — is a gap
    /// and never reaches the corpus, so a mislabeled case cannot be emitted.
    /// </summary>
    public static async Task<SynthPlan> PlanAsync(IReadOnlyList<SynthTemplate> templates, int count, int seed)
    {
        // Real navdata: PhraseologyMapper validates emitted canonicals through CommandParser, whose
        // fix resolution (the DCT / CFIX / HFIX templates) requires NavigationDatabase.
        Yaat.Sim.Testing.TestVnasData.EnsureInitialized();

        var rng = new Random(seed);
        var gaps = new List<TemplateGap>();
        var verified = new List<SynthTemplate>();
        foreach (SynthTemplate template in templates)
        {
            TemplateGap? gap = await VerifyAsync(Render(template, -1, rng)).ConfigureAwait(false);
            if (gap is null)
            {
                verified.Add(template);
            }
            else
            {
                gaps.Add(gap);
            }
        }

        var cases = new List<RenderedCase>();
        List<SynthTemplate> sampled = verified.Count == 0 ? [] : SampleTemplates(verified, count, seed);
        for (int i = 0; i < sampled.Count; i++)
        {
            RenderedCase rendered = Render(sampled[i], i, rng);
            TemplateGap? gap = await VerifyAsync(rendered).ConfigureAwait(false);
            if (gap is null)
            {
                cases.Add(rendered);
            }
            else
            {
                gaps.Add(gap);
            }
        }
        return new SynthPlan(verified, cases, gaps);
    }

    /// <summary>Renders one case: callsign + active traffic, slot values, the scenario context, and a Piper voice.</summary>
    public static RenderedCase Render(SynthTemplate template, int index, Random rng)
    {
        string callsign = CallsignPool[rng.Next(CallsignPool.Length)];
        List<string> activeCallsigns = BuildActiveCallsigns(callsign, rng);
        var slots = new Dictionary<string, (string Spoken, string Canonical)>(StringComparer.Ordinal);
        foreach (Match match in SlotRegex().Matches(template.SpokenPattern))
        {
            string name = match.Groups[1].Value;
            if (!slots.ContainsKey(name))
            {
                slots[name] = SampleSlot(name, rng, slots, activeCallsigns);
            }
        }

        string spokenBody = SlotRegex().Replace(template.SpokenPattern, m => slots[m.Groups[1].Value].Spoken);
        string canonical = SlotRegex().Replace(template.CanonicalPattern, m => slots[m.Groups[1].Value].Canonical);
        string transcript = $"{CallsignParser.IcaoToSpoken(callsign)} {spokenBody}";
        List<string> programmedFixes = slots.TryGetValue("fix", out (string Spoken, string Canonical) fix) ? [fix.Canonical] : [];

        var expectation = new EvalExpectation(
            canonical,
            transcript,
            callsign,
            activeCallsigns,
            programmedFixes,
            Synthetic: true,
            Template: template.Key,
            AvailableRunways: AirportRunways.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value)),
            TaxiwayNames: [.. TaxiwayPool, "T", "TC", "TE"],
            DestinationNames: [.. ParkingPool.Select(p => p.Name), .. SpotPool]
        );
        int speaker = SpeakerPool[rng.Next(SpeakerPool.Length)];
        float speed = SpeedPool[rng.Next(SpeedPool.Length)];
        return new RenderedCase(template, index, expectation, speaker, speed);
    }

    /// <summary>
    /// Maps the case's exact transcript through the production text pipeline (rule mapper only —
    /// deterministic, no models) under the case's own context. Returns null when it yields exactly
    /// the expected canonical and callsign, otherwise the gap.
    /// </summary>
    public static async Task<TemplateGap?> VerifyAsync(RenderedCase rendered)
    {
        EvalExpectation e = rendered.Expectation;
        var ctx = e.ToSpeechContext(WhisperBiasingPrompt.Default);
        TranscriptMapResult mapped = await SpeechRecognitionService
            .MapTranscriptAsync(e.Transcript!, ctx, new PhraseologyCommandMapper(), llmMapper: null, callsignResolver: null, CancellationToken.None)
            .ConfigureAwait(false);

        string? reason =
            mapped.Canonical is null ? "no rule matched"
            : !EvalRunner.CanonicalsMatch(e.Canonical, mapped.Canonical) ? "canonical mismatch"
            : !string.Equals(mapped.Callsign, e.Callsign, StringComparison.OrdinalIgnoreCase) ? $"callsign mismatch (got {mapped.Callsign ?? "none"})"
            : null;
        return reason is null ? null : new TemplateGap(rendered.Template.Key, e.Transcript!, e.Canonical, mapped.Canonical, reason);
    }

    private static (string Spoken, string Canonical) SampleSlot(
        string name,
        Random rng,
        Dictionary<string, (string Spoken, string Canonical)> slots,
        List<string> activeCallsigns
    ) =>
        name switch
        {
            "hdg" => SampleHeading(rng),
            "alt" => SampleAltitude(rng),
            "fl" => SampleFlightLevel(rng),
            "spd" => SampleNumber(rng.Next(15, 26) * 10),
            "deg" => Pick(rng, [("ten", "10"), ("twenty", "20"), ("thirty", "30")]),
            "rwy" => SampleRunway(rng, exclude: null),
            "rwy2" => SampleRunway(rng, exclude: slots["rwy"].Canonical),
            "sq" => SampleNumberString(string.Concat(Enumerable.Range(0, 4).Select(_ => rng.Next(0, 8).ToString(CultureInfo.InvariantCulture)))),
            "fix" => Pick(rng, [.. FixPool.Select(f => (f.ToLowerInvariant(), f))]),
            "twy" => SampleTaxiway(rng, exclude: null),
            "hson" => SampleTaxiway(rng, exclude: slots["twy"].Canonical),
            "path" => SamplePath(rng),
            "parking" => Pick(rng, [.. ParkingPool.Select(p => (p.Spoken, p.Name))]),
            "spot" => Pick(rng, [.. SpotPool.Select(s => (SpeakDigits(s), s))]),
            "miles" => SampleNumberWord(rng.Next(2, 6)),
            "type" => Pick(rng, [.. AircraftTypePool.Select(t => (t, t.ToUpperInvariant()))]),
            "traffic" => SampleTraffic(rng, activeCallsigns),
            "cardinal" => Pick(rng, [.. CardinalPool.Select(c => (c, c[..1].ToUpperInvariant()))]),
            _ => throw new InvalidOperationException($"Template slot '{{{name}}}' has no sampler — add one to SynthTemplates.SampleSlot."),
        };

    private static (string Spoken, string Canonical) Pick(Random rng, (string Spoken, string Canonical)[] options) =>
        options[rng.Next(options.Length)];

    private static (string Spoken, string Canonical) SampleHeading(Random rng)
    {
        int hdg = rng.Next(1, 37) * 10;
        string digits = hdg.ToString("D3", CultureInfo.InvariantCulture);
        return (SpeakDigits(digits), digits);
    }

    /// <summary>2,000–16,000 ft; a quarter of the sub-10,000 values carry "five hundred".</summary>
    private static (string Spoken, string Canonical) SampleAltitude(Random rng)
    {
        int thousands = rng.Next(2, 17);
        string spoken =
            thousands <= 9 ? $"{DigitWords[thousands]} thousand" : $"{SpeakDigits(thousands.ToString(CultureInfo.InvariantCulture))} thousand";
        int alt = thousands * 1000;
        if (thousands <= 9 && rng.Next(4) == 0)
        {
            spoken += " five hundred";
            alt += 500;
        }
        return (spoken, alt.ToString(CultureInfo.InvariantCulture));
    }

    private static (string Spoken, string Canonical) SampleFlightLevel(Random rng)
    {
        int level = rng.Next(18, 36) * 10;
        return ($"flight level {SpeakDigits(level.ToString(CultureInfo.InvariantCulture))}", (level * 100).ToString(CultureInfo.InvariantCulture));
    }

    private static (string Spoken, string Canonical) SampleNumber(int value) => SampleNumberString(value.ToString(CultureInfo.InvariantCulture));

    private static (string Spoken, string Canonical) SampleNumberString(string digits) => (SpeakDigits(digits), digits);

    private static (string Spoken, string Canonical) SampleNumberWord(int value) => (DigitWords[value], value.ToString(CultureInfo.InvariantCulture));

    private static (string Spoken, string Canonical) SampleRunway(Random rng, string? exclude)
    {
        string[] pool = [.. RunwayPool.Where(r => r != exclude)];
        string rwy = pool[rng.Next(pool.Length)];
        return (SpeakRunway(rwy), rwy);
    }

    private static (string Spoken, string Canonical) SampleTaxiway(Random rng, string? exclude)
    {
        string[] pool = [.. TaxiwayPool.Where(t => t != exclude)];
        string twy = pool[rng.Next(pool.Length)];
        return (NatoPhoneticAlphabet.SpellChar(twy[0]), twy);
    }

    /// <summary>Two or three distinct taxiways, spoken as NATO words.</summary>
    private static (string Spoken, string Canonical) SamplePath(Random rng)
    {
        string[] shuffled = [.. TaxiwayPool];
        rng.Shuffle(shuffled);
        string[] path = shuffled[..rng.Next(2, 4)];
        return (string.Join(' ', path.Select(t => NatoPhoneticAlphabet.SpellChar(t[0]))), string.Join(' ', path));
    }

    /// <summary>Another aircraft from the active list, spoken as its telephony.</summary>
    private static (string Spoken, string Canonical) SampleTraffic(Random rng, List<string> activeCallsigns)
    {
        string[] others = [.. activeCallsigns.Skip(1)];
        string traffic = others[rng.Next(others.Length)];
        return (CallsignParser.IcaoToSpoken(traffic), traffic);
    }

    private static string SpeakDigits(string digits) => string.Join(' ', digits.Select(c => DigitWords[c - '0']));

    /// <summary>"28R" → "two eight right"; zero-padded designators drop the zero ("01L" → "one left").</summary>
    private static string SpeakRunway(string runway)
    {
        string digits = new([.. runway.TakeWhile(char.IsDigit)]);
        string suffix = runway[digits.Length..] switch
        {
            "L" => " left",
            "R" => " right",
            "C" => " center",
            _ => "",
        };
        string spokenDigits = digits.TrimStart('0');
        spokenDigits = spokenDigits.Length == 0 ? "0" : spokenDigits;
        return SpeakDigits(spokenDigits) + suffix;
    }

    /// <summary>The commanded callsign first, then two distinct decoys.</summary>
    private static List<string> BuildActiveCallsigns(string callsign, Random rng)
    {
        var list = new List<string> { callsign };
        while (list.Count < 3)
        {
            string decoy = CallsignPool[rng.Next(CallsignPool.Length)];
            if (!list.Contains(decoy))
            {
                list.Add(decoy);
            }
        }
        return list;
    }
}
