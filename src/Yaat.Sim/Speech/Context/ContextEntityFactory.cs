using System.Globalization;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Speech.Context;

/// <summary>
/// Builds the entities the context decoder scores a transmission against, each with every spoken form a controller uses for
/// it. Every form's text is lowercase; the forms of one entity are deduplicated by their tokenized text, keeping the first
/// text seen and the highest prior, in insertion order.
/// </summary>
public static class ContextEntityFactory
{
    private static readonly IReadOnlySet<string> NavaidTypeWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "vor",
        "vortac",
        "vordme",
        "tacan",
        "ndb",
        "dme",
    };

    /// <summary>
    /// A room aircraft's callsign entity. A US N-number gets its "november", type-name and shortened forms and, when no other
    /// room callsign ends in the same three characters, those three alone; an airline callsign gets its telephony with the
    /// grouped and digit-by-digit flight number, the telephony without a trailing "air"/"airlines", and, when no other room
    /// callsign carries the same digits, the flight number alone.
    /// </summary>
    /// <param name="icaoCallsign">The ICAO callsign ("SWA2495", "N4926F").</param>
    /// <param name="aircraftType">The ICAO type designator, or null when unknown; drives the GA type-name forms.</param>
    /// <param name="telephonyOverrides">Telephony by airline prefix, consulted before the built-in table, any key case.</param>
    /// <param name="roomCallsigns">Every callsign in the room; the aircraft's own entry is ignored.</param>
    /// <param name="settings">The decoder settings whose priors rank the forms.</param>
    /// <returns>The callsign entity, or null for an airline callsign whose telephony is unknown.</returns>
    public static ContextEntity? Callsign(
        string icaoCallsign,
        string? aircraftType,
        IReadOnlyDictionary<string, string> telephonyOverrides,
        IReadOnlyCollection<string> roomCallsigns,
        DecoderSettings settings
    )
    {
        string callsign = icaoCallsign.Trim().ToUpperInvariant();
        return IsUsGa(callsign)
            ? GaCallsign(callsign, aircraftType, roomCallsigns, settings)
            : AirlineCallsign(callsign, telephonyOverrides, roomCallsigns, settings);
    }

    /// <summary>Both ends of every runway at the airport, each as "runway &lt;spelled&gt;" and the bare spelled designator.</summary>
    /// <param name="airportIcao">The airport's ICAO id ("KOAK").</param>
    /// <param name="nav">The navigation database.</param>
    /// <param name="settings">The decoder settings whose priors rank the forms.</param>
    /// <returns>One entity per runway end, valued "&lt;ICAO&gt;/&lt;designator&gt;".</returns>
    public static IReadOnlyList<ContextEntity> Runways(string airportIcao, NavigationDatabase nav, DecoderSettings settings)
    {
        string airport = AirportPrefix(airportIcao);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entities = new List<ContextEntity>();
        foreach (RunwayInfo runway in nav.GetRunways(airportIcao))
        {
            foreach (string end in new[] { runway.Id.End1, runway.Id.End2 })
            {
                string designator = RunwayIdentifier.NormalizeDesignator(end);
                if (seen.Add(designator))
                {
                    entities.Add(RunwayEntity(airport, designator, settings));
                }
            }
        }

        return entities;
    }

    /// <summary>
    /// The airport's straight-in approaches, as "&lt;type&gt; runway &lt;rwy&gt; approach", "&lt;type&gt; runway &lt;rwy&gt;" and
    /// "&lt;type&gt; &lt;rwy&gt;"; an RNAV approach also gets each form with "r nav". Circling approaches are skipped.
    /// </summary>
    /// <param name="airportIcao">The airport's ICAO id ("KOAK").</param>
    /// <param name="nav">The navigation database.</param>
    /// <param name="settings">The decoder settings whose priors rank the forms.</param>
    /// <returns>One procedure entity per approach, valued "&lt;ICAO&gt;/&lt;approach id&gt;".</returns>
    public static IReadOnlyList<ContextEntity> Approaches(string airportIcao, NavigationDatabase nav, DecoderSettings settings)
    {
        string airport = AirportPrefix(airportIcao);
        var entities = new List<ContextEntity>();
        foreach (CifpApproachProcedure approach in nav.GetApproaches(airportIcao))
        {
            if (ApproachReadbackName.TypeAndRunway(approach.ApproachId) is not { } parts)
            {
                continue;
            }

            string spoken = parts.Spoken.ToLowerInvariant();
            string runway = PhraseologyVerbalizer.SpellRunway(parts.Runway);
            var forms = new FormSet();
            AddApproachForms(forms, spoken, runway, settings);
            if (spoken.StartsWith("rnav", StringComparison.Ordinal))
            {
                AddApproachForms(forms, "r nav" + spoken[4..], runway, settings);
            }

            entities.Add(new ContextEntity(EntityKind.Procedure, $"{airport}/{approach.ApproachId}", forms.ToList()));
        }

        return entities;
    }

    /// <summary>
    /// The airport's SIDs and STARs, each as its spoken names alone and with "departure" or "arrival": the current cycle's
    /// SIDs, then the ARTCC-fragment SIDs the current cycle lacks (<see cref="NavigationDatabase.GetArtccFragmentSids"/>),
    /// then the same two for STARs. A base name with registered procedure pronunciations is spoken by those first.
    /// </summary>
    /// <param name="airportIcao">The airport's ICAO id ("KOAK").</param>
    /// <param name="nav">The navigation database.</param>
    /// <returns>One procedure entity per SID or STAR with a spoken name, valued "&lt;ICAO&gt;/&lt;procedure id&gt;".</returns>
    public static IReadOnlyList<ContextEntity> DeparturesAndArrivals(string airportIcao, NavigationDatabase nav)
    {
        string airport = AirportPrefix(airportIcao);
        var entities = new List<ContextEntity>();
        foreach (CifpSidProcedure sid in nav.GetSids(airportIcao).Concat(nav.GetArtccFragmentSids(airportIcao)))
        {
            AddProcedure(entities, airport, sid.ProcedureId, nav, "departure");
        }

        foreach (CifpStarProcedure star in nav.GetStars(airportIcao).Concat(nav.GetArtccFragmentStars(airportIcao)))
        {
            AddProcedure(entities, airport, star.ProcedureId, nav, "arrival");
        }

        return entities;
    }

    /// <summary>
    /// A fix entity with its registered pronunciations, its display name, its spoken name and its lowercase id, all at prior 1.0.
    /// </summary>
    /// <param name="fixId">The fix id ("MITOE").</param>
    /// <param name="nav">The navigation database.</param>
    /// <returns>The fix entity, valued by the bare uppercase id.</returns>
    /// <exception cref="ArgumentException"><paramref name="fixId"/> is blank.</exception>
    public static ContextEntity Fix(string fixId, NavigationDatabase nav)
    {
        if (string.IsNullOrWhiteSpace(fixId))
        {
            throw new ArgumentException("A fix entity needs a fix id.", nameof(fixId));
        }

        string id = fixId.Trim().ToUpperInvariant();
        var forms = new FormSet();
        foreach (string pronunciation in nav.GetFixPronunciations(id))
        {
            forms.Add(pronunciation, 1.0);
        }

        if (nav.GetFixDisplayName(id) is { } displayName)
        {
            forms.Add(displayName, 1.0);
        }

        forms.Add(PhraseologyVerbalizer.SpellFix(id), 1.0);
        forms.Add(id.ToLowerInvariant(), 1.0);
        return new ContextEntity(EntityKind.Fix, id, forms.ToList());
    }

    /// <summary>
    /// The layout's taxiways ("taxiway whiskey four", "whiskey four"), gates ("gate …", "parking …" and bare) and spots
    /// ("spot …" and bare), all at prior 1.0. After its spelled forms, a gate gets "gate p", "parking p" and bare p for each
    /// registered gate pronunciation p, and a spot "spot p" and bare p for each spot pronunciation; a bare form is added only
    /// when the layout name has a letter and the form's tokens are not a taxiway's bare form.
    /// </summary>
    /// <param name="airportIcao">The airport's ICAO id ("KSFO").</param>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="nav">The navigation database holding the gate and spot pronunciations.</param>
    /// <returns>Taxiway entities, then gate entities, then spot entities valued "&lt;ICAO&gt;/SPOT &lt;name&gt;".</returns>
    public static IReadOnlyList<ContextEntity> GroundEntities(string airportIcao, AirportGroundLayout layout, NavigationDatabase nav)
    {
        string airport = AirportPrefix(airportIcao);
        var entities = new List<ContextEntity>();
        var taxiwayBareTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in GroundLayoutSpeechNames.TaxiwayNames(layout))
        {
            string spoken = PhraseologyVerbalizer.SpellTaxiway(name);
            taxiwayBareTokens.Add(TokenText(spoken));
            entities.Add(EntityAtFullPrior(EntityKind.Taxiway, $"{airport}/{name}", ["taxiway " + spoken, spoken]));
        }

        foreach (string name in GroundLayoutSpeechNames.GateNames(layout))
        {
            string gate = PhraseologyVerbalizer.SpellDestinationName(name, "gate");
            string parking = PhraseologyVerbalizer.SpellDestinationName(name, "parking");
            List<string> texts = ["gate " + gate, "parking " + parking];
            AddBareIfUnambiguous(texts, name, parking, taxiwayBareTokens);
            foreach (string pronunciation in nav.GetGatePronunciations(airportIcao, name))
            {
                texts.Add("gate " + pronunciation);
                texts.Add("parking " + pronunciation);
                AddBareIfUnambiguous(texts, name, pronunciation, taxiwayBareTokens);
            }

            entities.Add(EntityAtFullPrior(EntityKind.Gate, $"{airport}/{name}", texts));
        }

        foreach (string name in GroundLayoutSpeechNames.SpotNames(layout))
        {
            string spot = PhraseologyVerbalizer.SpellDestinationName(name, "spot");
            List<string> texts = ["spot " + spot];
            AddBareIfUnambiguous(texts, name, spot, taxiwayBareTokens);
            foreach (string pronunciation in nav.GetSpotPronunciations(airportIcao, name))
            {
                texts.Add("spot " + pronunciation);
                AddBareIfUnambiguous(texts, name, pronunciation, taxiwayBareTokens);
            }

            entities.Add(EntityAtFullPrior(EntityKind.Gate, $"{airport}/SPOT {name}", texts));
        }

        return entities;
    }

    /// <summary>
    /// Adds a gate or spot's bare spoken name only when it names something on its own: the name has a letter (an all-digit
    /// name said bare is a number) and its tokens are not a taxiway's bare form.
    /// </summary>
    private static void AddBareIfUnambiguous(List<string> texts, string name, string bare, HashSet<string> taxiwayBareTokens)
    {
        if (name.Any(char.IsLetter) && !taxiwayBareTokens.Contains(TokenText(bare)))
        {
            texts.Add(bare);
        }
    }

    private static string TokenText(string text) => string.Join(" ", SpokenTokenizer.Tokenize(text).Select(token => token.Text));

    /// <summary>A landmark entity whose forms are the given spoken patterns, lowercased, at prior 1.0.</summary>
    /// <param name="airportIcao">The airport the landmark belongs to ("KOAK").</param>
    /// <param name="alias">The landmark's alias ("kaiser").</param>
    /// <param name="spokenPatterns">How the landmark is spoken; at least one.</param>
    /// <returns>The landmark entity, valued "&lt;ICAO&gt;/&lt;ALIAS&gt;".</returns>
    /// <exception cref="ArgumentException"><paramref name="spokenPatterns"/> is empty.</exception>
    public static ContextEntity Landmark(string airportIcao, string alias, IReadOnlyList<string> spokenPatterns)
    {
        if (spokenPatterns.Count == 0)
        {
            throw new ArgumentException($"Landmark '{alias}' at {airportIcao} needs at least one spoken pattern.", nameof(spokenPatterns));
        }

        return EntityAtFullPrior(EntityKind.Landmark, $"{AirportPrefix(airportIcao)}/{alias.Trim().ToUpperInvariant()}", spokenPatterns);
    }

    private static bool IsUsGa(string callsign) => (callsign.Length >= 2) && (callsign[0] == 'N') && char.IsAsciiDigit(callsign[1]);

    private static ContextEntity GaCallsign(
        string callsign,
        string? aircraftType,
        IReadOnlyCollection<string> roomCallsigns,
        DecoderSettings settings
    )
    {
        string tail = callsign[1..];
        string fullTailSuffix = " " + string.Join(" ", tail.Select(NatoPhoneticAlphabet.SpellChar));
        var forms = new FormSet();
        foreach (string variant in CallsignParser.GetSpokenVariants(callsign, aircraftType, roomCallsigns))
        {
            forms.Add(variant, variant.EndsWith(fullTailSuffix, StringComparison.Ordinal) ? 1.0 : settings.ShortGaPrior);
        }

        string lastThree = callsign[^3..];
        bool lastThreeUnique = !OtherCallsigns(roomCallsigns, callsign).Any(other => other.EndsWith(lastThree, StringComparison.OrdinalIgnoreCase));
        if ((tail.Length > 3) && lastThreeUnique)
        {
            forms.Add(string.Join(" ", lastThree.Select(NatoPhoneticAlphabet.SpellChar)), settings.GaLastThreePrior);
        }

        return new ContextEntity(EntityKind.Callsign, callsign, forms.ToList());
    }

    private static ContextEntity? AirlineCallsign(
        string callsign,
        IReadOnlyDictionary<string, string> telephonyOverrides,
        IReadOnlyCollection<string> roomCallsigns,
        DecoderSettings settings
    )
    {
        int split = 0;
        while ((split < callsign.Length) && char.IsAsciiLetter(callsign[split]))
        {
            split++;
        }

        string prefix = callsign[..split];
        string flight = callsign[split..];
        if ((prefix.Length == 0) || (flight.Length == 0) || !TryGetTelephony(prefix, telephonyOverrides, out string telephony))
        {
            return null;
        }

        IReadOnlyList<string> flightWords = FlightWords(flight);
        var forms = new FormSet();
        AddWithFlight(forms, telephony, flightWords, 1.0);
        if (TelephonyWithoutAir(telephony) is { } shortTelephony)
        {
            AddWithFlight(forms, shortTelephony, flightWords, settings.TelephonyMinusAirPrior);
        }

        string digits = DigitsOf(flight);
        // A flight number of two digits or fewer said alone ("two eight") is indistinguishable from a bare runway.
        if ((digits.Length >= 3) && !OtherCallsigns(roomCallsigns, callsign).Any(other => DigitsOf(other) == digits))
        {
            foreach (string words in flightWords)
            {
                forms.Add(words, settings.NumberOnlyPrior);
            }
        }

        return new ContextEntity(EntityKind.Callsign, callsign, forms.ToList());
    }

    private static bool TryGetTelephony(string prefix, IReadOnlyDictionary<string, string> telephonyOverrides, out string telephony)
    {
        foreach (KeyValuePair<string, string> entry in telephonyOverrides)
        {
            if (string.Equals(entry.Key.Trim(), prefix, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry.Value))
            {
                telephony = entry.Value.Trim().ToLowerInvariant();
                return true;
            }
        }

        if (AirlineTelephony.TryGetTelephony(prefix, out string builtIn) && !string.IsNullOrWhiteSpace(builtIn))
        {
            telephony = builtIn.Trim().ToLowerInvariant();
            return true;
        }

        telephony = "";
        return false;
    }

    private static IReadOnlyList<string> FlightWords(string flight)
    {
        bool plainNumber = (flight[0] != '0') && flight.All(char.IsAsciiDigit);
        if (plainNumber && int.TryParse(flight, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return [AtcNumberParser.FlightNumberToPairedWords(number), AtcNumberParser.FlightNumberToWords(number)];
        }

        return [string.Join(" ", flight.Select(NatoPhoneticAlphabet.SpellChar))];
    }

    private static string? TelephonyWithoutAir(string telephony)
    {
        string[] words = telephony.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if ((words.Length >= 2) && (words[^1] is "air" or "airlines"))
        {
            return string.Join(" ", words[..^1]);
        }

        return null;
    }

    private static void AddWithFlight(FormSet forms, string telephony, IReadOnlyList<string> flightWords, double prior)
    {
        foreach (string words in flightWords)
        {
            forms.Add($"{telephony} {words}", prior);
        }
    }

    private static IEnumerable<string> OtherCallsigns(IReadOnlyCollection<string> roomCallsigns, string callsign) =>
        roomCallsigns
            .Where(other => !string.IsNullOrWhiteSpace(other))
            .Select(other => other.Trim())
            .Where(other => !string.Equals(other, callsign, StringComparison.OrdinalIgnoreCase));

    private static string DigitsOf(string text) => new([.. text.Where(char.IsAsciiDigit)]);

    private static string AirportPrefix(string airportIcao) => airportIcao.Trim().ToUpperInvariant();

    private static ContextEntity RunwayEntity(string airport, string designator, DecoderSettings settings)
    {
        string spoken = PhraseologyVerbalizer.SpellRunway(designator);
        var forms = new FormSet();
        forms.Add("runway " + spoken, 1.0);
        forms.Add(spoken, settings.BareRunwayPrior);
        return new ContextEntity(EntityKind.Runway, $"{airport}/{designator}", forms.ToList());
    }

    private static void AddApproachForms(FormSet forms, string spoken, string runway, DecoderSettings settings)
    {
        forms.Add($"{spoken} runway {runway} approach", 1.0);
        forms.Add($"{spoken} runway {runway}", 1.0);
        forms.Add($"{spoken} {runway}", settings.BareRunwayPrior);
    }

    /// <summary>
    /// Adds a SID or STAR entity: each registered pronunciation of its base name with the version words (file order), then
    /// the base name spelled like a fix with the version words, each alone and with <paramref name="suffix"/>. An id with no
    /// name/version split is spoken as <see cref="PhraseologyVerbalizer.ProcedureName"/> says it.
    /// </summary>
    private static void AddProcedure(List<ContextEntity> entities, string airport, string procedureId, NavigationDatabase nav, string suffix)
    {
        var spokenNames = new List<string>();
        if (PhraseologyVerbalizer.SplitProcedureId(procedureId) is { } split)
        {
            spokenNames.AddRange(nav.GetProcedurePronunciations(split.BaseName).Select(name => $"{name} {split.VersionWords}"));
            spokenNames.Add($"{PhraseologyVerbalizer.SpellFix(split.BaseName)} {split.VersionWords}");
        }
        else
        {
            spokenNames.Add(PhraseologyVerbalizer.ProcedureName(procedureId).Tts);
        }

        var texts = new List<string>();
        foreach (string spokenName in spokenNames)
        {
            // A procedure named after a navaid is spoken by the navaid's name alone ("oakland six"), never with its type word.
            IEnumerable<string> words = spokenName
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(word => !NavaidTypeWords.Contains(word));
            string spoken = string.Join(" ", words);
            if (spoken.Length > 0)
            {
                texts.Add(spoken);
                texts.Add($"{spoken} {suffix}");
            }
        }

        if (texts.Count > 0)
        {
            entities.Add(EntityAtFullPrior(EntityKind.Procedure, $"{airport}/{procedureId}", texts));
        }
    }

    private static ContextEntity EntityAtFullPrior(EntityKind kind, string value, IReadOnlyList<string> texts)
    {
        var forms = new FormSet();
        foreach (string text in texts)
        {
            forms.Add(text, 1.0);
        }

        return new ContextEntity(kind, value, forms.ToList());
    }

    /// <summary>An entity's forms in insertion order, one per tokenized text, keeping the first text and the highest prior.</summary>
    private sealed class FormSet
    {
        private readonly List<SpokenForm> _forms = [];
        private readonly Dictionary<string, int> _indexByTokens = new(StringComparer.Ordinal);

        public void Add(string text, double prior)
        {
            string lower = text.Trim().ToLowerInvariant();
            string tokens = TokenText(lower);
            if (tokens.Length == 0)
            {
                return;
            }

            if (_indexByTokens.TryGetValue(tokens, out int index))
            {
                if (prior > _forms[index].Prior)
                {
                    _forms[index] = _forms[index] with { Prior = prior };
                }

                return;
            }

            _indexByTokens[tokens] = _forms.Count;
            _forms.Add(new SpokenForm(lower, prior));
        }

        public IReadOnlyList<SpokenForm> ToList() => [.. _forms];
    }
}
