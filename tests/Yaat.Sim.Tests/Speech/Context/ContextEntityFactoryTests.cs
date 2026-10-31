using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Speech;
using Yaat.Sim.Speech.Context;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Speech.Context;

public class ContextEntityFactoryTests
{
    private static readonly IReadOnlyDictionary<string, string> NoOverrides = new Dictionary<string, string>();

    private static NavigationDatabase Nav => NavigationDatabase.Instance;

    private static DecoderSettings Settings => DecoderSettings.Default;

    // --- Callsigns ---

    [Fact]
    public void Swa2495_GroupedAndDigitFormsDedupe()
    {
        ContextEntity entity = AirlineEntity("SWA2495", NoOverrides, ["SWA2495"]);

        Assert.Equal(EntityKind.Callsign, entity.Kind);
        Assert.Equal("SWA2495", entity.Value);
        Assert.Equal(1.0, PriorOf(entity, "southwest 2 4 9 5"));
        Assert.Equal(0.90, PriorOf(entity, "2 4 9 5"));
    }

    [Fact]
    public void NumberOnly_OmittedWhenDigitsShared()
    {
        ContextEntity entity = AirlineEntity("SWA2495", NoOverrides, ["SWA2495", "SKW2495"]);

        Assert.All(entity.Forms, form => Assert.StartsWith("southwest ", Tokens(form), StringComparison.Ordinal));
    }

    [Fact]
    public void Qxe2101_HasHorizonForm()
    {
        ContextEntity entity = AirlineEntity("QXE2101", NoOverrides, ["QXE2101"]);

        Assert.Equal(0.95, PriorOf(entity, "horizon 2 1 0 1"));
        Assert.Equal(1.0, PriorOf(entity, "horizon air 2 1 0 1"));
    }

    [Fact]
    public void Pcm772_PacValley()
    {
        var overrides = new Dictionary<string, string> { ["PCM"] = "PAC VALLEY" };

        ContextEntity entity = AirlineEntity("PCM772", overrides, ["PCM772"]);

        Assert.Equal(1.0, PriorOf(entity, "pac valley 7 7 2"));
    }

    [Fact]
    public void UnknownAirline_ReturnsNull()
    {
        // ZZZ is a real airline ("LAKEAIR"), so the unknown prefix is QQQ, which airlines.tsv does not list.
        Assert.False(AirlineTelephony.TryGetTelephony("QQQ", out _));

        Assert.Null(ContextEntityFactory.Callsign("QQQ123", null, NoOverrides, ["QQQ123"], Settings));
    }

    [Fact]
    public void Aal28_NoNumberOnlyForm()
    {
        ContextEntity entity = AirlineEntity("AAL28", NoOverrides, ["AAL28"]);

        Assert.All(entity.Forms, form => Assert.StartsWith("american ", Tokens(form), StringComparison.Ordinal));
    }

    [Fact]
    public void N4926F_FullShortAndLastThree()
    {
        ContextEntity? entity = ContextEntityFactory.Callsign("N4926F", "C172", NoOverrides, ["N4926F"], Settings);

        Assert.NotNull(entity);
        Assert.Equal(1.0, PriorOf(entity, "november 4 9 2 6 foxtrot"));
        Assert.Equal(0.95, PriorOf(entity, "november 2 6 foxtrot"));
        Assert.Equal(0.90, PriorOf(entity, "2 6 foxtrot"));
    }

    [Fact]
    public void N4926F_LastThreeAbsentWhenShared()
    {
        ContextEntity? entity = ContextEntityFactory.Callsign("N4926F", "C172", NoOverrides, ["N4926F", "N1226F"], Settings);

        Assert.NotNull(entity);
        Assert.DoesNotContain(entity.Forms, form => Tokens(form) == "2 6 foxtrot");
        Assert.DoesNotContain(entity.Forms, form => Tokens(form) == "november 2 6 foxtrot");
    }

    [Fact]
    public void N360rs_BareTailForm_AtNumberOnlyPrior()
    {
        ContextEntity? entity = ContextEntityFactory.Callsign("N360RS", null, NoOverrides, ["N360RS"], Settings);

        Assert.NotNull(entity);
        Assert.Equal(Settings.NumberOnlyPrior, PriorOf(entity, "three six zero romeo sierra"));
    }

    [Fact]
    public void N360rs_BareTailAbsentWhenAnotherCallsignSharesTail()
    {
        ContextEntity? entity = ContextEntityFactory.Callsign("N360RS", null, NoOverrides, ["N360RS", "N12360RS"], Settings);

        Assert.NotNull(entity);
        Assert.DoesNotContain(entity.Forms, form => Tokens(form) == TextTokens("three six zero romeo sierra"));
    }

    [Fact]
    public void N2866_GroupedDigitForms_WithEachPrefix()
    {
        ContextEntity? entity = ContextEntityFactory.Callsign("N2866", "P28A", NoOverrides, ["N2866"], Settings);

        Assert.NotNull(entity);
        Assert.Equal(1.0, PriorOf(entity, "november twenty eight sixty six"));
        Assert.Equal(1.0, PriorOf(entity, "archer twenty eight sixty six"));
        Assert.Equal(Settings.NumberOnlyPrior, PriorOf(entity, "twenty eight sixty six"));
    }

    [Fact]
    public void N2000_NoGroupedForm()
    {
        ContextEntity? entity = ContextEntityFactory.Callsign("N2000", "P28A", NoOverrides, ["N2000"], Settings);

        Assert.NotNull(entity);
        Assert.DoesNotContain(entity.Forms, form => form.Text.Contains("twenty", StringComparison.Ordinal));
        Assert.Equal(Settings.NumberOnlyPrior, PriorOf(entity, "two zero zero zero"));
    }

    [Fact]
    public void CareFlight_KeepsFullTelephony()
    {
        // CFH says "care flight"; only the codes with a hand-kept short form (FFT, NKS) lose their last word.
        Assert.True(AirlineTelephony.TryGetTelephony("CFH", out string telephony));
        Assert.EndsWith(" flight", telephony, StringComparison.OrdinalIgnoreCase);

        ContextEntity entity = AirlineEntity("CFH1639", NoOverrides, ["CFH1639"]);

        Assert.Equal(1.0, PriorOf(entity, "care flight one six three nine"));
        Assert.DoesNotContain(entity.Forms, form => Tokens(form) == TextTokens("care one six three nine"));
    }

    [Fact]
    public void N787y_Pa30_HasTwinComancheForms()
    {
        ContextEntity? entity = ContextEntityFactory.Callsign("N787Y", "PA30", NoOverrides, ["N787Y"], Settings);

        Assert.NotNull(entity);
        Assert.Equal(1.0, PriorOf(entity, "twin comanche seven eight seven yankee"));
        Assert.Equal(1.0, PriorOf(entity, "comanche seven eight seven yankee"));
    }

    [Fact]
    public void N8394q_C206_HasStationairForm()
    {
        ContextEntity? entity = ContextEntityFactory.Callsign("N8394Q", "C206", NoOverrides, ["N8394Q"], Settings);

        Assert.NotNull(entity);
        Assert.Equal(1.0, PriorOf(entity, "stationair eight three nine four quebec"));
    }

    [Fact]
    public void Fft_DropsTrailingFlight()
    {
        ContextEntity entity = AirlineEntity("FFT1639", NoOverrides, ["FFT1639"]);

        Assert.Equal(1.0, PriorOf(entity, "frontier flight one six three nine"));
        Assert.Equal(Settings.TelephonyMinusAirPrior, PriorOf(entity, "frontier one six three nine"));
    }

    [Fact]
    public void Nks_DropsTrailingWings()
    {
        ContextEntity entity = AirlineEntity("NKS1639", NoOverrides, ["NKS1639"]);

        Assert.Equal(1.0, PriorOf(entity, "spirit wings one six three nine"));
        Assert.Equal(Settings.TelephonyMinusAirPrior, PriorOf(entity, "spirit one six three nine"));
    }

    [Fact]
    public void Jbu_HasSplitJetBlueForm()
    {
        ContextEntity entity = AirlineEntity("JBU1639", NoOverrides, ["JBU1639"]);

        Assert.Equal(1.0, PriorOf(entity, "jetblue one six three nine"));
        Assert.Equal(Settings.TelephonyMinusAirPrior, PriorOf(entity, "jet blue one six three nine"));
    }

    // --- Runways and procedures ---

    [Fact]
    public void Runway30_RunwayAndBareForms()
    {
        IReadOnlyList<ContextEntity> runways = ContextEntityFactory.Runways("KOAK", Nav, Settings);

        ContextEntity runway30 = Assert.Single(runways, entity => entity.Value == "KOAK/30");
        Assert.Equal(EntityKind.Runway, runway30.Kind);
        Assert.Equal(1.0, PriorOf(runway30, "runway 3 0"));
        Assert.Equal(0.90, PriorOf(runway30, "3 0"));
        Assert.Contains(runways, entity => entity.Value == "KOAK/28L");
        Assert.Contains(runways, entity => entity.Value == "KOAK/12");
    }

    [Fact]
    public void OakI30_IlsRunwayThreeZero()
    {
        IReadOnlyList<ContextEntity> approaches = ContextEntityFactory.Approaches("KOAK", Nav, Settings);

        ContextEntity ils30 = Assert.Single(approaches, entity => entity.Value == "KOAK/I30");
        Assert.Equal(EntityKind.Procedure, ils30.Kind);
        Assert.Contains(ils30.Forms, form => Tokens(form) == "ils runway 3 0 approach");
    }

    [Fact]
    public void OakRnav_HasRNavForm()
    {
        IReadOnlyList<ContextEntity> approaches = ContextEntityFactory.Approaches("KOAK", Nav, Settings);

        ContextEntity rnav = approaches.First(entity => entity.Value.EndsWith("H30-Z", StringComparison.Ordinal));
        Assert.Contains(rnav.Forms, form => form.Text.StartsWith("rnav ", StringComparison.Ordinal));
        Assert.Contains(rnav.Forms, form => form.Text.StartsWith("r nav ", StringComparison.Ordinal));
        Assert.Contains(rnav.Forms, form => Tokens(form) == "rnav zulu runway 3 0 approach");
        Assert.Contains(rnav.Forms, form => Tokens(form) == "romeo nav zulu runway 3 0 approach");
    }

    [Fact]
    public void RnavZ_OnlyRnavForRunway_HasFormWithoutVariant()
    {
        (string airport, CifpApproachProcedure approach) = FindRnavApproach(uniqueForTypeAndRunway: true);

        IReadOnlyList<ContextEntity> approaches = ContextEntityFactory.Approaches(airport, Nav, Settings);

        ContextEntity entity = Assert.Single(approaches, candidate => candidate.Value == $"{airport}/{approach.ApproachId}");
        string runway = RunwayOf(approach);
        Assert.Equal(Settings.BareRunwayPrior, PriorOf(entity, $"rnav runway {runway} approach"));
        Assert.Equal(Settings.BareRunwayPrior, PriorOf(entity, $"rnav {runway}"));
        Assert.Equal(Settings.BareRunwayPrior, PriorOf(entity, $"r nav runway {runway} approach"));
        Assert.DoesNotContain(entity.Forms, form => Tokens(form).Split(' ').Contains("gps"));
    }

    [Fact]
    public void RnavVariants_TwoForSameRunway_NoFormWithoutVariant()
    {
        (string airport, CifpApproachProcedure approach) = FindRnavApproach(uniqueForTypeAndRunway: false);

        IReadOnlyList<ContextEntity> approaches = ContextEntityFactory.Approaches(airport, Nav, Settings);

        ContextEntity entity = Assert.Single(approaches, candidate => candidate.Value == $"{airport}/{approach.ApproachId}");
        string approachWords = ApproachReadbackName.TypeAndRunway(approach.ApproachId)!.Value.Spoken.ToLowerInvariant();
        string withoutVariant = approachWords[..approachWords.LastIndexOf(' ')];
        string variantLess = TextTokens($"{withoutVariant} runway {RunwayOf(approach)} approach");
        Assert.DoesNotContain(entity.Forms, form => Tokens(form) == variantLess);
    }

    [Fact]
    public void KoakRunway30_RnpAndGpsShareLetters_NoLetterlessForm()
    {
        // RNAV (RNP) and RNAV (GPS) are both spoken "rnav" and share one letter sequence per runway, so neither letter is
        // the only one its runway carries and neither can be said without it.
        List<CifpApproachProcedure> rnavOnThirty = [.. Nav.GetApproaches("KOAK").Where(approach => IsRnavOnRunway(approach, "30"))];
        Assert.True(rnavOnThirty.Count >= 2, $"Expected two RNAV approaches to KOAK runway 30, found {rnavOnThirty.Count}");

        IReadOnlyList<ContextEntity> approaches = ContextEntityFactory.Approaches("KOAK", Nav, Settings);
        string runway = PhraseologyVerbalizer.SpellRunway("30");
        Assert.All(
            rnavOnThirty,
            rnav =>
            {
                ContextEntity entity = Assert.Single(approaches, candidate => candidate.Value == $"KOAK/{rnav.ApproachId}");
                Assert.DoesNotContain(entity.Forms, form => Tokens(form) == TextTokens($"rnav runway {runway} approach"));
                Assert.DoesNotContain(entity.Forms, form => Tokens(form) == TextTokens($"rnav runway {runway}"));
                Assert.DoesNotContain(entity.Forms, form => Tokens(form) == TextTokens($"rnav {runway}"));
                Assert.DoesNotContain(entity.Forms, form => Tokens(form) == TextTokens($"r nav runway {runway} approach"));
            }
        );
    }

    [Fact]
    public void SingleDigitRunway_HasZeroForms()
    {
        (string airport, string designator) = FindSingleDigitRunway();

        IReadOnlyList<ContextEntity> runways = ContextEntityFactory.Runways(airport, Nav, Settings);

        ContextEntity entity = Assert.Single(runways, candidate => candidate.Value == $"{airport}/{designator}");
        string spoken = PhraseologyVerbalizer.SpellRunway(designator);
        Assert.Equal(1.0, PriorOf(entity, $"runway {spoken}"));
        Assert.Equal(Settings.BareRunwayPrior, PriorOf(entity, $"runway zero {spoken}"));
        Assert.Equal(Settings.BareRunwayPrior, PriorOf(entity, $"zero {spoken}"));
    }

    [Fact]
    public void KoakOaklandSid_SpokenByChartedNameWithoutNavaidWord()
    {
        // The OAK SID's version changes with the AIRAC cycle, so it is found by its ^OAK\d$ shape rather than named.
        // Its entity is said by the charted name, "oakland <version>", never with a "vor"/"vortac" word.
        CifpSidProcedure sid = Assert.Single(Nav.GetSids("KOAK"), IsOaklandSid);
        string id = sid.ProcedureId;
        string digit = id[3..];

        IReadOnlyList<ContextEntity> procedures = ContextEntityFactory.DeparturesAndArrivals("KOAK", Nav);

        ContextEntity oakland = Assert.Single(procedures, entity => entity.Value == $"KOAK/{id}");
        Assert.Equal($"oakland {digit}", Tokens(oakland.Forms[0]));
        Assert.Contains(oakland.Forms, form => Tokens(form) == $"oakland {digit} departure");
        Assert.DoesNotContain(oakland.Forms, form => Tokens(form).Split(' ').Any(word => word is "vor" or "vortac"));
    }

    [Fact]
    public void KoakNimitzSid_SpokenByRegisteredPronunciationFirst()
    {
        CifpSidProcedure sid = Assert.Single(
            Nav.GetSids("KOAK").Concat(Nav.GetArtccFragmentSids("KOAK")),
            candidate => candidate.ProcedureId.StartsWith("NIMI", StringComparison.Ordinal)
        );
        string id = sid.ProcedureId;
        string version = id[4..];

        IReadOnlyList<ContextEntity> procedures = ContextEntityFactory.DeparturesAndArrivals("KOAK", Nav);

        ContextEntity nimitz = Assert.Single(procedures, entity => entity.Value == $"KOAK/{id}");
        Assert.Equal($"nimitz {version}", Tokens(nimitz.Forms[0]));
        Assert.Contains(nimitz.Forms, form => Tokens(form) == $"nimitz {version} departure");
        string codeSpelled = TextTokens($"{PhraseologyVerbalizer.SpellFix("NIMI")} {version}");
        Assert.Contains(nimitz.Forms, form => Tokens(form) == codeSpelled);
    }

    private static bool IsOaklandSid(CifpSidProcedure sid) =>
        (sid.ProcedureId.Length == 4) && sid.ProcedureId.StartsWith("OAK", StringComparison.Ordinal) && char.IsAsciiDigit(sid.ProcedureId[3]);

    // --- Ground layout ---

    [Fact]
    public void OakTaxiwayW4_FromLayout()
    {
        IReadOnlyList<ContextEntity> ground = ContextEntityFactory.GroundEntities("KOAK", Layout("OAK"), Nav);

        ContextEntity w4 = Assert.Single(ground, entity => entity.Value == "KOAK/W4");
        Assert.Equal(EntityKind.Taxiway, w4.Kind);
        Assert.Contains(w4.Forms, form => Tokens(form) == "taxiway whiskey 4");
        Assert.Contains(w4.Forms, form => Tokens(form) == "whiskey 4");
    }

    [Fact]
    public void SfoSpot6B_ValueSpot6B()
    {
        IReadOnlyList<ContextEntity> ground = ContextEntityFactory.GroundEntities("KSFO", Layout("SFO"), Nav);

        ContextEntity spot = Assert.Single(ground, entity => entity.Value == "KSFO/SPOT 6B");
        Assert.Equal(EntityKind.Gate, spot.Kind);
        Assert.Contains(spot.Forms, form => Tokens(form) == "spot 6 bravo");
    }

    [Fact]
    public void AllDigitSpot_NoBareForm()
    {
        AirportGroundLayout sfo = Layout("SFO");
        string? allDigit = GroundLayoutSpeechNames.SpotNames(sfo).FirstOrDefault(name => name.All(char.IsAsciiDigit));
        Assert.NotNull(allDigit);

        IReadOnlyList<ContextEntity> ground = ContextEntityFactory.GroundEntities("KSFO", sfo, Nav);

        ContextEntity spot = Assert.Single(ground, entity => entity.Value == $"KSFO/SPOT {allDigit}");
        SpokenForm only = Assert.Single(spot.Forms);
        Assert.StartsWith("spot ", Tokens(only), StringComparison.Ordinal);
    }

    [Fact]
    public void SfoC11_GateForms()
    {
        IReadOnlyList<ContextEntity> ground = ContextEntityFactory.GroundEntities("KSFO", Layout("SFO"), Nav);

        ContextEntity gate = Assert.Single(ground, entity => entity.Value == "KSFO/C11");
        Assert.Equal(EntityKind.Gate, gate.Kind);
        Assert.Contains(gate.Forms, form => Tokens(form) == "gate charlie 1 1");
        Assert.Contains(gate.Forms, form => Tokens(form) == "parking charlie 1 1");
        Assert.Contains(gate.Forms, form => Tokens(form) == "charlie 1 1");
    }

    [Fact]
    public void GroundLayoutSpeechNames_TaxiwayNamesMatchClientFilter()
    {
        IReadOnlyList<string> names = GroundLayoutSpeechNames.TaxiwayNames(Layout("OAK"));

        Assert.Contains("W4", names);
        Assert.DoesNotContain(names, name => name.StartsWith("RWY", StringComparison.Ordinal));
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
    }

    // --- Airport context ---

    [Fact]
    public void KoakFixesWithin_IncludeMitoe()
    {
        IReadOnlyList<ContextEntity> entities = AirportContextBuilder.Build("KOAK", null, 10, [], Nav, Settings);

        ContextEntity mitoe = Assert.Single(entities, entity => (entity.Kind == EntityKind.Fix) && (entity.Value == "MITOE"));
        Assert.Contains(mitoe.Forms, form => Tokens(form) == "mitoe");
    }

    [Fact]
    public void Build_DedupesAndIncludesLandmark()
    {
        ContextEntity kaiser = ContextEntityFactory.Landmark("KOAK", "kaiser", ["kaiser", "kaiser center"]);

        IReadOnlyList<ContextEntity> entities = AirportContextBuilder.Build("KOAK", Layout("OAK"), 10, [kaiser], Nav, Settings);

        Assert.Single(entities, entity => entity.Value == "KOAK/KAISER");
        Assert.All(entities.GroupBy(entity => (entity.Kind, entity.Value)), group => Assert.Single(group));
    }

    // The airports the navdata searches below look through: real airports whose CIFP changes with the AIRAC cycle, so the
    // approach and runway a test uses are found by shape rather than named.
    private static readonly string[] CandidateAirports =
    [
        "KOAK",
        "KSFO",
        "KSJC",
        "KLAX",
        "KSEA",
        "KPDX",
        "KSAN",
        "KLAS",
        "KPHX",
        "KDEN",
        "KDFW",
        "KORD",
        "KJFK",
        "KBOS",
        "KATL",
    ];

    private static (string Airport, CifpApproachProcedure Approach) FindRnavApproach(bool uniqueForTypeAndRunway)
    {
        foreach (string airport in CandidateAirports)
        {
            IReadOnlyList<CifpApproachProcedure> approaches = Nav.GetApproaches(airport);
            foreach (CifpApproachProcedure approach in approaches)
            {
                if (NavigationDatabase.ParseShorthand(approach.ApproachId) is not { } parsed)
                {
                    continue;
                }

                if ((parsed.TypeCode is not ('H' or 'R')) || (parsed.Runway is not { } runway) || !IsSingleVariantLetter(parsed.Variant))
                {
                    continue;
                }

                // Siblings are counted by the spoken type words: RNAV (RNP) and RNAV (GPS) are both "rnav".
                string spokenTypeWords = SpokenTypeWords(approach);
                int sameSpokenType = approaches.Count(candidate =>
                    string.Equals(SpokenTypeWords(candidate), spokenTypeWords, StringComparison.Ordinal)
                    && string.Equals(RunwayOfId(candidate), runway, StringComparison.Ordinal)
                );
                if ((sameSpokenType == 1) == uniqueForTypeAndRunway)
                {
                    return (airport, approach);
                }
            }
        }

        throw new InvalidOperationException(
            $"No RNAV approach {(uniqueForTypeAndRunway ? "alone on its runway" : "sharing its runway")} in the candidate airports."
        );
    }

    private static string SpokenTypeWords(CifpApproachProcedure approach)
    {
        if (NavigationDatabase.ParseShorthand(approach.ApproachId) is not { } parsed)
        {
            return "";
        }

        return parsed.TypeCode is { } typeCode ? ApproachReadbackName.TypeWords(typeCode)?.Spoken ?? "" : "";
    }

    private static string? RunwayOfId(CifpApproachProcedure approach) => NavigationDatabase.ParseShorthand(approach.ApproachId)?.Runway;

    private static bool IsRnavOnRunway(CifpApproachProcedure approach, string runway)
    {
        if (NavigationDatabase.ParseShorthand(approach.ApproachId) is not { } parsed)
        {
            return false;
        }

        return (parsed.TypeCode is 'H' or 'R') && string.Equals(parsed.Runway, runway, StringComparison.Ordinal);
    }

    private static (string Airport, string Designator) FindSingleDigitRunway()
    {
        foreach (string airport in CandidateAirports)
        {
            foreach (string end in Nav.GetRunways(airport).SelectMany(runway => new[] { runway.Id.End1, runway.Id.End2 }))
            {
                string designator = RunwayIdentifier.NormalizeDesignator(end);
                if ((designator.Length >= 2) && (designator[0] == '0') && char.IsAsciiDigit(designator[1]))
                {
                    return (airport, designator);
                }
            }
        }

        throw new InvalidOperationException("No single-digit runway in the candidate airports.");
    }

    private static string RunwayOf(CifpApproachProcedure approach) =>
        PhraseologyVerbalizer.SpellRunway(ApproachReadbackName.TypeAndRunway(approach.ApproachId)!.Value.Runway);

    private static bool IsSingleVariantLetter(string? variant)
    {
        string trimmed = (variant ?? "").Trim().TrimStart('-');
        return (trimmed.Length == 1) && char.IsLetter(trimmed[0]);
    }

    private static ContextEntity AirlineEntity(string callsign, IReadOnlyDictionary<string, string> overrides, IReadOnlyCollection<string> room)
    {
        ContextEntity? entity = ContextEntityFactory.Callsign(callsign, null, overrides, room, Settings);
        Assert.NotNull(entity);
        return entity;
    }

    private static AirportGroundLayout Layout(string airportId)
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airportId);
        Assert.NotNull(layout);
        return layout;
    }

    private static double PriorOf(ContextEntity entity, string spokenOrTokens) =>
        Assert.Single(entity.Forms, form => Tokens(form) == TextTokens(spokenOrTokens)).Prior;

    private static string Tokens(SpokenForm form) => TextTokens(form.Text);

    private static string TextTokens(string text) => string.Join(" ", SpokenTokenizer.Tokenize(text).Select(token => token.Text));
}
