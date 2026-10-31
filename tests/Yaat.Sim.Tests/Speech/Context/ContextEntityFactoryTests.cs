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
    public void KoakOaklandSid_SpokenWithoutNavaidWord()
    {
        // The OAK SID's version changes with the AIRAC cycle, so it is found by its ^OAK\d$ shape rather than named.
        CifpSidProcedure sid = Assert.Single(Nav.GetSids("KOAK"), IsOaklandSid);
        string id = sid.ProcedureId;
        string digit = id[3..];

        string rawTts = PhraseologyVerbalizer.ProcedureName(id).Tts.ToLowerInvariant();
        string[] rawWords = rawTts.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(rawWords.Contains("vor") || rawWords.Contains("vortac"), $"Raw procedure TTS has no navaid word: '{rawTts}'");

        IReadOnlyList<ContextEntity> procedures = ContextEntityFactory.DeparturesAndArrivals("KOAK", Nav);

        ContextEntity oakland = Assert.Single(procedures, entity => entity.Value == $"KOAK/{id}");
        Assert.Contains(oakland.Forms, form => Tokens(form) == $"oakland {digit}");
        Assert.Contains(oakland.Forms, form => Tokens(form) == $"oakland {digit} departure");
        Assert.DoesNotContain(oakland.Forms, form => form.Text.Contains("vor", StringComparison.Ordinal));
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

    private static double PriorOf(ContextEntity entity, string tokens) => Assert.Single(entity.Forms, form => Tokens(form) == tokens).Prior;

    private static string Tokens(SpokenForm form) => TextTokens(form.Text);

    private static string TextTokens(string text) => string.Join(" ", SpokenTokenizer.Tokenize(text).Select(token => token.Text));
}
