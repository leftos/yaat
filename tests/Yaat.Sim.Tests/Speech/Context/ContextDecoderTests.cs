using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Speech.Context;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Speech.Context;

public class ContextDecoderTests
{
    private const string KizerTranscript = "southwest twenty four ninety five abeam kizer";

    private const string Swa = "southwest twenty four ninety five ";

    private static readonly IReadOnlyDictionary<string, string> Overrides = new Dictionary<string, string> { ["PCM"] = "PAC VALLEY" };

    private static readonly string[] MainRoom = ["SWA2495", "N8298L", "PCM772", "QXE2101", "FDX568"];

    private static readonly string[] PaddedRoom =
    [
        "SWA1873",
        "SWA3317",
        "SWA6670",
        "SWA1367",
        "UAL1106",
        "UAL837",
        "UAL1631",
        "AAL2160",
        "AAL1338",
        "AAL1681",
        "DAL1770",
        "DAL863",
        "DAL1018",
        "ASA1176",
        "ASA307",
        "JBU1416",
        "JBU631",
        "FDX1310",
        "UPS2830",
        "ENY3366",
        "HAL18",
        "CPZ6013",
        "NKS1760",
        "NKS617",
        "QXE2030",
        "QXE2316",
        "ASA1316",
        "N172SP",
        "N738CF",
        "N6113D",
        "N3031T",
        "N801CA",
        "N16077",
        "N7380X",
        "N53PL",
    ];

    private static readonly Lazy<IReadOnlyList<ContextEntity>> AirportWithKaiser = new(() => BuildAirport([Kaiser]));

    // Real KOAK data ships the ground place KAISER, so the "no Kaiser in context" premise removes it explicitly.
    private static readonly Lazy<IReadOnlyList<ContextEntity>> AirportWithoutKaiser = new(() =>
        [.. BuildAirport([]).Where(entity => entity.Value != "KOAK/KAISER")]
    );

    private static DecoderSettings Settings => DecoderSettings.Default;

    private static NavigationDatabase Nav => NavigationDatabase.Instance;

    private static ContextEntity Kaiser => ContextEntityFactory.Landmark("KOAK", "kaiser", ["kaiser", "kaiser center"]);

    private static IReadOnlyList<ContextEntity> Airport => AirportWithKaiser.Value;

    private static IReadOnlyList<ContextEntity> NoKaiser => AirportWithoutKaiser.Value;

    [Theory]
    [InlineData("southwest twenty four ninety five", "SWA2495", "")]
    [InlineData("november eight two nine eight lima", "N8298L", "")]
    [InlineData("pac valley seven seventy two", "PCM772", "")]
    [InlineData("horizon twenty one oh one", "QXE2101", "")]
    [InlineData("fedex five sixty eight heavy", "FDX568", "")]
    [InlineData("runway three zero", null, "Runway=KOAK/30")]
    [InlineData("taxi via whiskey four", null, "Taxiway=KOAK/W4")]
    [InlineData("taxiway charlie abeam kaiser", null, "Taxiway=KOAK/C;Landmark=KOAK/KAISER")]
    [InlineData("direct mitoe", null, "Fix=MITOE")]
    public void NineTranscripts_ResolveToGold(string transcript, string? callsign, string entities)
    {
        DecodeResult result = DecodeSpoken(transcript, Room(MainRoom, Airport));

        if (callsign is not null)
        {
            AssertResolved(result, callsign);
        }
        else
        {
            Assert.Equal(DecodeState.NoCallsign, result.State);
        }
        foreach (string expected in entities.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = expected.Split('=');
            AssertHasEntity(result, Enum.Parse<EntityKind>(parts[0]), parts[1]);
        }
    }

    [Fact]
    public void FedexHeavy_SpanIncludesHeavy()
    {
        DecodeResult result = DecodeSpoken("fedex five sixty eight heavy", Room(MainRoom, Airport));

        AssertResolved(result, "FDX568");
        Assert.Equal(new WordSpan(0, 5), result.Callsign!.Span);
    }

    [Fact]
    public void TrailingSouthwest_RoomHasMatchingTail()
    {
        DecodeResult result = DecodeSpoken(
            "runway three zero cleared to land southwest twenty four ninety five",
            Room(["SWA2495", "N1230L"], Airport)
        );

        AssertResolved(result, "SWA2495");
        AssertHasEntity(result, EntityKind.Runway, "KOAK/30");
    }

    [Fact]
    public void NumberOnly_AtStart_Resolves()
    {
        DecodeResult result = DecodeSpoken("twenty four ninety five runway three zero", Room(MainRoom, Airport));

        AssertResolved(result, "SWA2495");
    }

    [Fact]
    public void CloseCall_RemainderDecides()
    {
        ContextEntity runway30 = Airport.Single(entity => (entity.Kind == EntityKind.Runway) && (entity.Value == "KOAK/30"));
        string[] callsigns = ["QXE2101", "HOR2101"];
        RoomAircraft[] aircraft = [Aircraft("QXE2101", callsigns, [runway30]), Aircraft("HOR2101", callsigns, [])];
        var context = new TransmissionContext("KOAK", aircraft, Airport);

        DecodeResult result = DecodeSpoken("horizon twenty one oh one runway three zero", context);

        AssertResolved(result, "QXE2101");
    }

    [Fact]
    public void PaddedRoom_SkywestDoesNotSnap()
    {
        Assert.Equal(35, PaddedRoom.Length);

        DecodeResult result = DecodeSpoken("skywest twenty four ninety five", Room(PaddedRoom, Airport));

        Assert.Equal(DecodeState.NoCallsign, result.State);
        Assert.Null(result.Callsign);
        Assert.Null(result.BestGuess);
    }

    [Fact]
    public void SkywestWithSameDigitsSwa_IsSayAgainNotResolved()
    {
        DecodeResult result = DecodeSpoken("skywest twenty four ninety five", Room(["SWA2495"], Airport));

        Assert.Equal(DecodeState.SayAgain, result.State);
        Assert.Null(result.Callsign);
        Assert.Equal("SWA2495", result.BestGuess?.Entity.Value);
    }

    [Fact]
    public void Kizer_WithKaiser_Resolves()
    {
        DecodeResult result = DecodeSpoken(KizerTranscript, Room(["SWA2495"], Airport));

        AssertResolved(result, "SWA2495");
        AssertHasValue(result, "KOAK/KAISER");
    }

    [Fact]
    public void Kizer_NoKaiserInContext_NotResolved()
    {
        DecodeResult result = DecodeSpoken(KizerTranscript, Room(["SWA2495"], NoKaiser));

        Assert.DoesNotContain(result.Entities, entity => (entity.Span.Start <= 6) && (entity.Span.End > 6));
    }

    [Fact]
    public void Typed_Swa249_StaysUnresolved_TextUnchanged()
    {
        const string text = "SWA249 cleared to land runway 30";

        DecodeResult result = Decode([new DecoderHypothesis(text, null, null)], InputSource.Typed, MatchMode.Fuzzy, Room(["SWA2495"], Airport));

        Assert.Equal(DecodeState.NoCallsign, result.State);
        Assert.Equal(text, result.RewrittenTranscript);
    }

    [Theory]
    [InlineData("SWA2495 30")]
    [InlineData("SWA2495 3 0")]
    public void Typed_ExactIcao_Resolves(string text)
    {
        DecoderHypothesis typed = new(text, null, null);

        DecodeResult result = Decode([typed], InputSource.Typed, MatchMode.ExactOnly, Room(["SWA2495"], Airport));

        AssertResolved(result, "SWA2495");
        AssertHasEntity(result, EntityKind.Runway, "KOAK/30");
    }

    [Fact]
    public void ExactOnly_NearMissNotRecovered()
    {
        DecoderHypothesis spoken = new(KizerTranscript, null, null);

        DecodeResult result = Decode([spoken], InputSource.Spoken, MatchMode.ExactOnly, Room(["SWA2495"], Airport));

        AssertResolved(result, "SWA2495");
        AssertLacksValue(result, "KOAK/KAISER");
    }

    [Fact]
    public void HeadingDigits_NotRunway30()
    {
        DecodeResult result = DecodeSpoken("southwest twenty four ninety five heading three zero zero", Room(MainRoom, Airport));

        AssertLacksValue(result, "KOAK/30");
    }

    [Fact]
    public void ConfidentWord_NotSnapped()
    {
        DecodeResult result = DecodeWithPieces(KizerPieces("▁abeam", -0.005));

        Assert.True(result.ConfidenceGated);
        AssertLacksValue(result, "KOAK/KAISER");
    }

    [Fact]
    public void LowConfidence_Snapped()
    {
        DecodeResult result = DecodeWithPieces(KizerPieces("▁abeam", -0.75));

        Assert.True(result.ConfidenceGated);
        AssertHasValue(result, "KOAK/KAISER");
    }

    [Fact]
    public void PieceCountMismatch_GateOff()
    {
        DecodeResult result = DecodeWithPieces(KizerPieces("abeam", -0.005));

        Assert.False(result.ConfidenceGated);
        AssertHasValue(result, "KOAK/KAISER");
    }

    [Fact]
    public void NBest_PrefersResolvedHypothesis()
    {
        DecoderHypothesis[] hypotheses = [new("skywest twenty four ninety five", null, null), new("southwest twenty four ninety five", null, null)];

        DecodeResult result = Decode(hypotheses, InputSource.Spoken, MatchMode.Fuzzy, Room(["SWA2495"], Airport));

        AssertResolved(result, "SWA2495");
        Assert.Equal(1, result.HypothesisIndex);
    }

    [Fact]
    public void NBest_LargeAcousticGapWins()
    {
        const string clean = "southwest twenty four ninety five runway three zero";
        const string noisy = "southwest twenty four ninety five runway two eight right blorp";
        TransmissionContext context = Room(["SWA2495"], Airport);

        DecoderHypothesis[] apart = [new(clean, null, -20.0), new(noisy, null, -5.0)];
        DecoderHypothesis[] level = [new(clean, null, -5.0), new(noisy, null, -5.0)];

        Assert.Equal(1, Decode(apart, InputSource.Spoken, MatchMode.Fuzzy, context).HypothesisIndex);
        Assert.Equal(0, Decode(level, InputSource.Spoken, MatchMode.Fuzzy, context).HypothesisIndex);
    }

    [Fact]
    public void Rewrite_ReplacesNameKeepsDigits()
    {
        DecodeResult result = DecodeSpoken(KizerTranscript, Room(["SWA2495"], Airport));

        Assert.Equal("southwest twenty four ninety five abeam kaiser", result.RewrittenTranscript);
    }

    [Theory]
    [InlineData("southwest twenty four ninety five turn left heading two five zero", "SWA2495", "UAL250")]
    [InlineData("southwest twenty four ninety five reduce speed to two one zero", "SWA2495", "UAL210")]
    [InlineData("southwest twenty four ninety five squawk four five two one", "SWA2495", "UAL4521")]
    [InlineData("southwest twenty four ninety five descend and maintain five thousand", "SWA2495", "UAL5000")]
    [InlineData("fedex five sixty eight turn right heading two seven zero", "FDX568", "AAL270")]
    [InlineData("november eight two nine eight lima turn right heading seven seven two", "N8298L", "PCM772")]
    public void ValueAfterCallsign_KeepsAddressee(string transcript, string addressee, string valueLookalike)
    {
        DecodeResult result = DecodeSpoken(transcript, Room(WithBase(valueLookalike), Airport));

        AssertResolved(result, addressee);
    }

    [Fact]
    public void ValueAlone_ResolvesNoAircraft()
    {
        DecodeResult result = DecodeSpoken("turn left heading two five zero", Room(WithBase("UAL250"), Airport));

        Assert.NotEqual(DecodeState.Resolved, result.State);
        Assert.Null(result.Callsign);
    }

    [Theory]
    [InlineData("fedex five sixty eight follow southwest twenty four ninety five", "FDX568", false)]
    [InlineData("fedex five sixty eight follow southwest twenty four ninety five", "FDX568", true)]
    [InlineData("southwest twenty four ninety five follow fedex five sixty eight heavy", "SWA2495", false)]
    [InlineData("southwest twenty four ninety five follow fedex five sixty eight heavy", "SWA2495", true)]
    [InlineData("southwest twenty four ninety five traffic twelve o'clock two miles fedex five sixty eight heavy", "SWA2495", false)]
    [InlineData("southwest twenty four ninety five traffic twelve o'clock two miles fedex five sixty eight heavy", "SWA2495", true)]
    [InlineData("southwest twenty four ninety five give way to skyhawk nine eight lima", "SWA2495", false)]
    [InlineData("southwest twenty four ninety five give way to skyhawk nine eight lima", "SWA2495", true)]
    public void TrafficAfterAddressee_BothRoomOrders(string transcript, string addressee, bool reversed)
    {
        string[] room = reversed ? [.. WithBase().Reverse()] : WithBase();

        DecodeResult result = DecodeSpoken(transcript, Room(room, Airport));

        AssertResolved(result, addressee);
    }

    [Theory]
    [InlineData("delta twenty four ninety five", "SWA2495", DecodeState.SayAgain, "SWA2495")]
    [InlineData("sierra twenty four ninety five", "SWA2495", DecodeState.SayAgain, "SWA2495")]
    [InlineData("november twenty four ninety five", "SWA2495", DecodeState.SayAgain, "SWA2495")]
    [InlineData("november seven alpha bravo charlie", "N6ABC", DecodeState.SayAgain, "N6ABC")]
    [InlineData("november six alpha bravo charlie", "N7ABC", DecodeState.SayAgain, "N7ABC")]
    public void OtherTelephony_DoesNotResolveByDigits(string transcript, string aircraft, DecodeState state, string? guess)
    {
        DecodeResult result = DecodeSpoken(transcript, Room([aircraft], Airport));

        Assert.Equal(state, result.State);
        Assert.Null(result.Callsign);
        Assert.Equal(guess, result.BestGuess?.Entity.Value);
    }

    [Theory]
    [InlineData("tower twenty four ninety five")]
    [InlineData("roger twenty four ninety five")]
    [InlineData("correction twenty four ninety five")]
    public void Opener_AllowsPrefixless(string transcript) => AssertResolved(DecodeSpoken(transcript, Room(WithBase(), Airport)), "SWA2495");

    [Theory]
    [InlineData("good morning november eight two nine eight lima")]
    [InlineData("sorry november eight two nine eight lima")]
    [InlineData("abeam kaiser november eight two nine eight lima")]
    public void NovemberPrefix_AfterAnyWord(string transcript) => AssertResolved(DecodeSpoken(transcript, Room(WithBase(), Airport)), "N8298L");

    [Fact]
    public void NumberAtReadbackEnd_NoLongerResolves()
    {
        DecodeResult result = DecodeSpoken("runway three zero cleared to land twenty four ninety five", Room(WithBase(), Airport));

        Assert.NotEqual(DecodeState.Resolved, result.State);
    }

    [Theory]
    [InlineData("southwest twenty four ninety five correction fedex five sixty eight turn left heading two seven zero", "FDX568")]
    [InlineData("fedex five sixty eight correction southwest twenty four ninety five", "SWA2495")]
    public void Correction_SwitchesAddressee(string transcript, string addressee) =>
        AssertResolved(DecodeSpoken(transcript, Room(WithBase(), Airport)), addressee);

    [Fact]
    public void Correction_EntitiesComeFromAfterTheNewAddressee()
    {
        const string transcript = "southwest twenty four ninety five runway three zero correction fedex five sixty eight runway two eight right";

        DecodeResult result = DecodeSpoken(transcript, Room(WithBase(), Airport));

        AssertResolved(result, "FDX568");
        AssertHasValue(result, "KOAK/28R");
        AssertLacksValue(result, "KOAK/30");
    }

    [Theory]
    [InlineData("southwest twenty four ninety five heading two seven zero break fedex five sixty eight descend and maintain three thousand")]
    [InlineData("southwest twenty four ninety five and fedex five sixty eight")]
    public void BreakOrAnd_SaysAgain(string transcript)
    {
        DecodeResult result = DecodeSpoken(transcript, Room(WithBase(), Airport));

        Assert.Equal(DecodeState.SayAgain, result.State);
        Assert.Null(result.Callsign);
        Assert.Equal("SWA2495", result.BestGuess?.Entity.Value);
        Assert.Equal(0.0, result.JointScore);
    }

    [Theory]
    [InlineData("southwest twenty four ninety five seven miles from mitoe", "SWA2495")]
    [InlineData("southwest twenty four ninety five three thousand", "SWA2495")]
    [InlineData("fedex five sixty eight two seven zero", "FDX568")]
    [InlineData("southwest twenty four ninety five one five left", "SWA2495")]
    [InlineData("southwest twenty four ninety five three zero", "SWA2495")]
    public void CallsignThenNumber_Resolves(string transcript, string addressee)
    {
        DecodeResult result = DecodeSpoken(transcript, Room(WithBase(), Airport));

        AssertResolved(result, addressee);
        if (transcript.EndsWith("three zero", StringComparison.Ordinal))
        {
            AssertHasValue(result, "KOAK/30");
        }
    }

    [Fact]
    public void BareRunway_BeforeCallsign_NeedsLeadIn()
    {
        DecodeResult result = DecodeSpoken("wind three zero southwest twenty four ninety five", Room(WithBase(), Airport));

        AssertResolved(result, "SWA2495");
        AssertLacksValue(result, "KOAK/30");
    }

    [Fact]
    public void ShorterFlight_NotResolvedFromLongerNumber()
    {
        const string transcript = "united twenty four ninety five";

        Assert.NotEqual(DecodeState.Resolved, DecodeSpoken(transcript, Room(["UAL24"], Airport)).State);
        AssertResolved(DecodeSpoken(transcript, Room(["UAL24", "UAL2495"], Airport)), "UAL2495");
    }

    [Theory]
    [InlineData("cleared to land three zero", "KOAK/30")]
    [InlineData("cross two eight right", "KOAK/28R")]
    [InlineData("hold short of three zero", "KOAK/30")]
    public void BareRunway_WithLeadIn_Resolves(string transcript, string runway) =>
        AssertHasEntity(DecodeSpoken(transcript, Room(WithBase(), Airport)), EntityKind.Runway, runway);

    [Theory]
    [InlineData("traffic twelve o'clock one five miles")]
    [InlineData("wind two seven zero at one five")]
    [InlineData("wind three one zero at one two gusts three zero")]
    [InlineData("contact tower one two four point three zero")]
    [InlineData("contact norcal one two eight point one five")]
    [InlineData("turn thirty degrees right")]
    [InlineData("turn left three zero degrees")]
    [InlineData("mach point three three")]
    [InlineData("time three zero")]
    [InlineData("expect further clearance three zero")]
    public void BareRunway_NeedsLeadIn(string transcript) =>
        Assert.DoesNotContain(DecodeSpoken(transcript, Room(WithBase(), Airport)).Entities, match => match.Entity.Kind == EntityKind.Runway);

    [Theory]
    [InlineData("taxi via whiskey four", "KOAK/W4")]
    [InlineData("taxi via alfa bravo charlie", "KOAK/A;KOAK/B;KOAK/C")]
    [InlineData("hold short of charlie", "KOAK/C")]
    [InlineData("at charlie", "KOAK/C")]
    public void BareTaxiway_WithLeadIn_Resolves(string transcript, string taxiways)
    {
        DecodeResult result = DecodeSpoken(transcript, Room(WithBase(), Airport));

        foreach (string taxiway in taxiways.Split(';'))
        {
            AssertHasEntity(result, EntityKind.Taxiway, taxiway);
        }
    }

    [Theory]
    [InlineData("whiskey four")]
    [InlineData("charlie")]
    [InlineData("information charlie is current")]
    [InlineData("advise you have information whiskey")]
    [InlineData("via victor one eight seven")]
    [InlineData("follow delta seven thirty seven")]
    [InlineData("tango november eight two nine eight lima")]
    [InlineData("good morning nine eight lima")]
    [InlineData("the golf course")]
    [InlineData("the hotel")]
    public void BareTaxiway_NeedsLeadIn(string transcript) =>
        Assert.DoesNotContain(DecodeSpoken(transcript, Room(WithBase(), Airport)).Entities, match => match.Entity.Kind == EntityKind.Taxiway);

    [Fact]
    public void Discriminators_NotFuzzed()
    {
        TransmissionContext context = Room(WithBase(), Airport);

        Assert.DoesNotContain(
            DecodeSpoken("ils runway two eight left approach", context).Entities,
            match => match.Entity.Value.EndsWith("28R", StringComparison.Ordinal)
        );
        AssertLacksSuffix(DecodeSpoken("localizer runway two eight left approach", context), "L28R");
        AssertLacksSuffix(DecodeSpoken("vor runway one zero left approach", context), "R10L");
    }

    [Theory]
    [InlineData("say altitude")]
    [InlineData("say again")]
    [InlineData("contact san francisco tower")]
    [InlineData("oakland airport in sight")]
    public void RuleLiteral_NotRewritten(string transcript) =>
        Assert.Equal(transcript, DecodeSpoken(transcript, Room(WithBase(), Airport)).RewrittenTranscript);

    [Fact]
    public void NatoNearMiss_Resolves()
    {
        DecodeResult result = DecodeSpoken("november eight two nine eight leema", Room(WithBase(), Airport));

        AssertResolved(result, "N8298L");
        Assert.Equal("november eight two nine eight lima", result.RewrittenTranscript);
    }

    [Fact]
    public void JointScore_NeverZero()
    {
        DecodeResult result = DecodeSpoken("southwest twenty four ninety five climb and maintain one two thousand", Room(WithBase(), Airport));

        AssertResolved(result, "SWA2495");
        Assert.True(result.JointScore > 0.0, $"joint score {result.JointScore}");
    }

    [Fact]
    public void Acoustics_RankValueHypotheses()
    {
        DecoderHypothesis[] hypotheses =
        [
            new("southwest twenty four ninety five climb and maintain one two thousand", null, -30.0),
            new("southwest twenty four ninety five climb and maintain one three thousand", null, -2.0),
        ];

        DecodeResult result = Decode(hypotheses, InputSource.Spoken, MatchMode.Fuzzy, Room(WithBase(), Airport));

        Assert.Equal(1, result.HypothesisIndex);
    }

    [Fact]
    public void Ties_FollowSourceOrder()
    {
        ContextEntity first = FixOf("FIRST", "bar baz");
        ContextEntity second = FixOf("SECOND", "foo bar");

        DecodeResult result = DecodeSpoken("foo bar baz", Room([], [first, second]));

        Assert.Equal("FIRST", Assert.Single(result.Entities).Entity.Value);
    }

    [Fact]
    public void LetterRun_RewritesWholeRun()
    {
        DecodeResult result = DecodeSpoken("southwest twenty four ninety five abeam k i z e r", Room(["SWA2495"], Airport));

        Assert.Equal("southwest twenty four ninety five abeam kaiser", result.RewrittenTranscript);
        Assert.Equal(new WordSpan(6, 11), Assert.Single(result.Entities, match => match.Entity.Value == "KOAK/KAISER").Span);
    }

    [Fact]
    public void JoinedWord_KeepsTheOtherWord()
    {
        const string transcript = "southwest twenty four ninety five abeam kizer,runway three zero";

        DecodeResult result = DecodeSpoken(transcript, Room(["SWA2495"], Airport));

        AssertHasValue(result, "KOAK/KAISER");
        Assert.Equal(transcript, result.RewrittenTranscript);
    }

    [Fact]
    public void TypedSayAgain_NoEntityInsideBestGuess()
    {
        ContextEntity entity = new(EntityKind.Runway, "KOAK/249", [new SpokenForm("blorp", 1.0)]);

        DecodeResult result = Decode(
            [new DecoderHypothesis("249 cleared", null, null)],
            InputSource.Typed,
            MatchMode.ExactOnly,
            Room(["SWA2495"], [entity])
        );

        Assert.Equal(DecodeState.SayAgain, result.State);
        Assert.DoesNotContain(result.Entities, match => match.Span.Start == 0);
    }

    [Fact]
    public void TokenlessExactWord_DoesNotThrow()
    {
        RoomAircraft aircraft = new(new ContextEntity(EntityKind.Callsign, "UM", [new SpokenForm("um", 1.0)]), []);
        var context = new TransmissionContext("KOAK", [aircraft], []);

        DecodeResult result = Decode([new DecoderHypothesis("um", null, null)], InputSource.Typed, MatchMode.ExactOnly, context);

        Assert.Equal(DecodeState.NoCallsign, result.State);
    }

    [Fact]
    public void OwnWinsOverAirportDuplicate()
    {
        ContextEntity runway30 = Airport.Single(entity => (entity.Kind == EntityKind.Runway) && (entity.Value == "KOAK/30"));
        string[] callsigns = ["SWA2495"];
        var context = new TransmissionContext("KOAK", [Aircraft("SWA2495", callsigns, [runway30])], Airport);

        DecodeResult result = DecodeSpoken("southwest twenty four ninety five runway three zero", context);

        Assert.True(Assert.Single(result.Entities, match => match.Entity.Value == "KOAK/30").OwnAircraft);
    }

    [Fact]
    public void SameEntityChosenTwice()
    {
        DecodeResult result = DecodeSpoken("southwest twenty four ninety five runway three zero runway three zero", Room(["SWA2495"], Airport));

        Assert.Equal(2, result.Entities.Count(match => match.Entity.Value == "KOAK/30"));
    }

    [Fact]
    public void CodeOnlyThreshold()
    {
        ContextEntity codeOnly = FixOf("CODED", "one two three four");
        TransmissionContext context = Room(["SWA2495"], [codeOnly]);

        // Four of five digits align: 0.8 clears the name threshold and misses the code-only one.
        AssertLacksValue(DecodeSpoken("southwest twenty four ninety five direct one two three four five", context), "CODED");
        AssertHasValue(DecodeSpoken("southwest twenty four ninety five direct one two three four", context), "CODED");
    }

    [Fact]
    public void MarginExcludesFarCandidate()
    {
        string[] callsigns = ["SWA2495", "SWA2496"];
        ContextEntity blorp = FixOf("BLORP", "blorp");
        RoomAircraft[] aircraft = [Aircraft("SWA2495", callsigns, []), Aircraft("SWA2496", callsigns, [blorp])];
        var context = new TransmissionContext("KOAK", aircraft, []);

        // SWA2496 explains "blorp" and would win on the joint score, but its one wrong digit puts it outside the margin.
        DecodeResult result = DecodeSpoken("southwest twenty four ninety five direct blorp", context);

        AssertResolved(result, "SWA2495");
    }

    [Fact]
    public void RawBelowFloor_IsNoCallsign()
    {
        DecodeResult result = DecodeSpoken("southwest two four seven six", Room(["SWA2495"], Airport));

        Assert.Equal(DecodeState.NoCallsign, result.State);
        Assert.Null(result.Callsign);
        Assert.Null(result.BestGuess);
    }

    [Fact]
    public void SayAgain_EntitiesAreAirportOnly()
    {
        string[] callsigns = ["SWA2495"];
        var context = new TransmissionContext("KOAK", [Aircraft("SWA2495", callsigns, [FixOf("BLORP", "blorp")])], Airport);

        DecodeResult result = DecodeSpoken("skywest twenty four ninety five direct blorp runway three zero", context);

        Assert.Equal(DecodeState.SayAgain, result.State);
        AssertLacksValue(result, "BLORP");
        AssertHasValue(result, "KOAK/30");
    }

    [Fact]
    public void SayAgain_JointScoreZero()
    {
        DecodeResult result = DecodeSpoken("skywest twenty four ninety five", Room(["SWA2495"], Airport));

        Assert.Equal(DecodeState.SayAgain, result.State);
        Assert.Equal(0.0, result.JointScore);
    }

    [Fact]
    public void EntityMatch_ScoreHeardSpan()
    {
        DecodeResult result = DecodeSpoken(KizerTranscript, Room(["SWA2495"], Airport));

        EntityMatch match = Assert.Single(result.Entities, entity => entity.Entity.Value == "KOAK/KAISER");
        double prior = match.Entity.Forms.First(form => form.Text == "kaiser").Prior;
        Assert.Equal(SpokenFormScore.WordScore("kizer", "kaiser", MatchMode.Fuzzy, Settings) * prior, match.Score, 6);
        Assert.Equal("kizer", match.Heard);
        Assert.Equal(new WordSpan(6, 7), match.Span);
    }

    [Fact]
    public void Rewrite_KeepsConfidentWord()
    {
        DecodeResult result = DecodeWithPieces(KizerPieces("▁abeam", -0.005));

        Assert.Equal(KizerTranscript, result.RewrittenTranscript);
    }

    [Fact]
    public void Rewrite_NormalisesWhitespace()
    {
        DecodeResult result = DecodeSpoken("southwest   twenty four  ninety five", Room(["SWA2495"], Airport));

        Assert.Equal("southwest twenty four ninety five", result.RewrittenTranscript);
    }

    [Fact]
    public void Typed_LowercaseIcao()
    {
        DecodeResult result = Decode(
            [new DecoderHypothesis("swa2495 30", null, null)],
            InputSource.Typed,
            MatchMode.ExactOnly,
            Room(["SWA2495"], Airport)
        );

        AssertResolved(result, "SWA2495");
        Assert.Equal("swa2495 30", result.RewrittenTranscript);
    }

    [Fact]
    public void Typed_FixValue()
    {
        DecodeResult result = Decode(
            [new DecoderHypothesis("SWA2495 MITOE", null, null)],
            InputSource.Typed,
            MatchMode.ExactOnly,
            Room(["SWA2495"], Airport)
        );

        AssertHasEntity(result, EntityKind.Fix, "MITOE");
    }

    [Fact]
    public void Typed_NoCallsign_AirportOnly()
    {
        string[] callsigns = ["SWA2495"];
        var context = new TransmissionContext("KOAK", [Aircraft("SWA2495", callsigns, [FixOf("BLORP", "blorp")])], Airport);

        DecodeResult result = Decode([new DecoderHypothesis("BLORP 30", null, null)], InputSource.Typed, MatchMode.ExactOnly, context);

        Assert.Equal(DecodeState.NoCallsign, result.State);
        AssertHasEntity(result, EntityKind.Runway, "KOAK/30");
        AssertLacksValue(result, "BLORP");
    }

    [Fact]
    public void NBest_MixedAcoustic_UsesRankPenalty()
    {
        const string text = "southwest twenty four ninety five";
        DecoderHypothesis[] hypotheses = [new(text, null, -20.0), new(text, null, -5.0), new(text, null, null)];

        DecodeResult result = Decode(hypotheses, InputSource.Spoken, MatchMode.Fuzzy, Room(["SWA2495"], Airport));

        Assert.Equal(0, result.HypothesisIndex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("uh um")]
    public void Empty_And_FillerOnly_NoCallsign(string transcript)
    {
        DecodeResult result = DecodeSpoken(transcript, Room(["SWA2495"], Airport));

        Assert.Equal(DecodeState.NoCallsign, result.State);
        Assert.Empty(result.Entities);
    }

    [Fact]
    public void EmptyRoom_NoCallsign()
    {
        DecodeResult result = DecodeSpoken("southwest twenty four ninety five runway three zero", Room([], Airport));

        Assert.Equal(DecodeState.NoCallsign, result.State);
        AssertHasValue(result, "KOAK/30");
    }

    [Fact]
    public void NoHypotheses_Throws() =>
        Assert.Throws<ArgumentException>(() => Decode([], InputSource.Spoken, MatchMode.Fuzzy, Room(["SWA2495"], Airport)));

    [Theory]
    [InlineData("contact tower one two four point three", "UAL124")]
    [InlineData("contact ground one two one point seven five", "UAL121")]
    [InlineData("contact departure one two zero point nine", "UAL120")]
    [InlineData("expect further clearance one five three zero", "UAL1530")]
    [InlineData("descend and maintain five thousand and two one zero", "UAL210")]
    [InlineData("maintain two one zero until approach one eight zero", "UAL180")]
    [InlineData("okay one eight zero knots", "UAL180")]
    [InlineData("contact tower one two four", "UAL124")]
    public void Frequency_ResolvesNoAircraft(string transcript, string lookalike)
    {
        DecodeResult result = DecodeSpoken(transcript, Room(WithBase(lookalike), Airport));

        Assert.NotEqual(lookalike, result.Callsign?.Entity.Value);
        Assert.NotEqual(lookalike, result.BestGuess?.Entity.Value);
    }

    [Theory]
    [InlineData("contact departure one two zero point nine southwest twenty four ninety five")]
    [InlineData("one two zero point nine southwest twenty four ninety five")]
    public void Frequency_ThenCallsign(string transcript) => AssertResolved(DecodeSpoken(transcript, Room(WithBase("UAL120"), Airport)), "SWA2495");

    [Theory]
    [InlineData("american twenty four ninety five follow fedex five sixty eight")]
    [InlineData("american twenty four ninety five give way to november eight two nine eight lima")]
    [InlineData("american twenty four ninety five number two behind fedex five sixty eight")]
    [InlineData("caution wake turbulence follow fedex five sixty eight heavy")]
    [InlineData("traffic twelve o'clock two miles fedex five sixty eight")]
    [InlineData("american twenty four ninety five hold for fedex five sixty eight")]
    [InlineData("american twenty four ninety five caution wake turbulence fedex five sixty eight")]
    [InlineData("american twenty four ninety five your traffic is fedex five sixty eight")]
    [InlineData("american twenty four ninety five maintain visual separation from fedex five sixty eight")]
    [InlineData("american twenty four ninety five report in sight fedex five sixty eight")]
    [InlineData("american twenty four ninety five traffic on your left is november eight two nine eight lima")]
    [InlineData("follow fedex five sixty eight turn left")]
    [InlineData("behind fedex five sixty eight cross runway three zero")]
    [InlineData("traffic fedex five sixty eight twelve o'clock two miles")]
    [InlineData("hold for fedex five sixty eight")]
    [InlineData("cleared to land runway three zero number two after fedex five sixty eight")]
    [InlineData("caution wake turbulence the fedex five sixty eight heavy")]
    [InlineData("follow the fedex five sixty eight")]
    public void Traffic_NotAddressee(string transcript) =>
        Assert.NotEqual(DecodeState.Resolved, DecodeSpoken(transcript, Room(WithBase(), Airport)).State);

    [Fact]
    public void UnmatchedOpening_RefusesTrailing()
    {
        DecodeResult result = DecodeSpoken("american twenty four ninety five cleared to land fedex five sixty eight", Room(WithBase(), Airport));

        Assert.Equal(DecodeState.NoCallsign, result.State);
        Assert.Null(result.Callsign);
        Assert.Null(result.BestGuess);
    }

    [Theory]
    [InlineData("altimeter two nine nine two southwest twenty four ninety five")]
    [InlineData("contact norcal one two eight point one five southwest twenty four ninety five")]
    public void ValueOpening_KeepsTrailingCallsign(string transcript) =>
        AssertResolved(DecodeSpoken(transcript, Room(WithBase(), Airport)), "SWA2495");

    [Fact]
    public void TrafficOpening_KeepsTrailingCallsign() =>
        AssertResolved(
            DecodeSpoken("follow fedex five sixty eight turn left runway three zero southwest twenty four ninety five", Room(WithBase(), Airport)),
            "SWA2495"
        );

    [Theory]
    [InlineData(Swa + "correction fedex five sixty eight three thousand", DecodeState.Resolved)]
    [InlineData(Swa + "correction fedex five sixty eight two seven zero", DecodeState.Resolved)]
    [InlineData(Swa + "correction fedex five sixty eight seven miles from mitoe", DecodeState.Resolved)]
    [InlineData(Swa + "break fedex five sixty eight three thousand", DecodeState.SayAgain)]
    [InlineData(Swa + "and fedex five sixty eight two seven zero", DecodeState.SayAgain)]
    public void SecondCallsign_FollowedByNumber(string transcript, DecodeState state)
    {
        DecodeResult result = DecodeSpoken(transcript, Room(WithBase(), Airport));

        Assert.Equal(state, result.State);
        if (state == DecodeState.Resolved)
        {
            AssertResolved(result, "FDX568");
        }
        else
        {
            Assert.Equal("SWA2495", result.BestGuess?.Entity.Value);
        }
    }

    [Theory]
    [InlineData(Swa + "correction five sixty eight turn left", "FDX568")]
    [InlineData(Swa + "correction nine eight lima turn left", "N8298L")]
    public void SecondCallsign_Prefixless_Switches(string transcript, string addressee) =>
        AssertResolved(DecodeSpoken(transcript, Room(WithBase(), Airport)), addressee);

    [Theory]
    [InlineData(Swa + "break five sixty eight turn left")]
    [InlineData(Swa + "and five sixty eight")]
    public void SecondCallsign_Prefixless_SaysAgain(string transcript)
    {
        DecodeResult result = DecodeSpoken(transcript, Room(WithBase(), Airport));

        Assert.Equal(DecodeState.SayAgain, result.State);
        Assert.Equal("SWA2495", result.BestGuess?.Entity.Value);
    }

    [Theory]
    [InlineData(Swa + "correction cessna eight two nine seven lima turn left", "", "N8298L")]
    [InlineData(Swa + "correction november eight two nine seven lima", "", "N8298L")]
    [InlineData(Swa + "correction united two fifty", "", "SWA2495")]
    [InlineData("fedex five sixty eight correction southwest twenty four ninety six", "", "SWA2495")]
    [InlineData(Swa + "correction united one two three four five turn left", "UAL1234", "UAL1234")]
    public void Correction_BelowAccept_SaysAgain(string transcript, string extra, string guess)
    {
        string[] room = extra.Length == 0 ? WithBase() : WithBase(extra);

        DecodeResult result = DecodeSpoken(transcript, Room(room, Airport));

        Assert.Equal(DecodeState.SayAgain, result.State);
        Assert.Equal(guess, result.BestGuess?.Entity.Value);
    }

    [Fact]
    public void SecondCallsign_SkipsSeparatorWithoutHit()
    {
        const string transcript = Swa + "descend and maintain three thousand break fedex five sixty eight";

        DecodeResult result = DecodeSpoken(transcript, Room(WithBase(), Airport));

        Assert.Equal(DecodeState.SayAgain, result.State);
        Assert.Equal("SWA2495", result.BestGuess?.Entity.Value);
    }

    [Fact]
    public void Correction_AfterSeparatorWithoutHit_Switches()
    {
        const string transcript = Swa + "descend and maintain three thousand correction fedex five sixty eight";

        AssertResolved(DecodeSpoken(transcript, Room(WithBase(), Airport)), "FDX568");
    }

    [Fact]
    public void Correction_OfNonCallsign_KeepsAddressee() =>
        AssertResolved(DecodeSpoken(Swa + "runway three zero correction runway two eight right", Room(WithBase(), Airport)), "SWA2495");

    [Fact]
    public void Correction_AmendsTraffic()
    {
        const string transcript = Swa + "follow united two fifty correction fedex five sixty eight";

        AssertResolved(DecodeSpoken(transcript, Room(WithBase("UAL250"), Airport)), "SWA2495");
    }

    [Theory]
    [InlineData(Swa + "turn left heading two six zero correction two seven zero", "UAL270", "SWA2495")]
    [InlineData(Swa + "climb and maintain flight level two one zero correction two two zero", "UAL220", "SWA2495")]
    [InlineData(Swa + "reduce speed to two one zero correction two two zero", "UAL220", "SWA2495")]
    [InlineData(Swa + "squawk four five one two correction four five two one", "SKW4521", "SWA2495")]
    [InlineData(Swa + "descend and maintain four thousand correction three thousand", "UAL3000", "SWA2495")]
    [InlineData(Swa + "heading two seven zero correction two eight zero", "UAL281", "SWA2495")]
    [InlineData(Swa + "taxi via alfa correction bravo charlie", "N6ABC", "SWA2495")]
    [InlineData("fedex five sixty eight heavy turn left heading two six zero correction two seven zero", "UAL270", "FDX568")]
    public void CorrectedValue_KeepsAddressee(string transcript, string lookalike, string addressee)
    {
        DecodeResult result = DecodeSpoken(transcript, Room(WithBase(lookalike), Airport));

        AssertResolved(result, addressee);
        Assert.Null(result.BestGuess);
    }

    [Theory]
    [InlineData(Swa + "follow the seven thirty seven correction fedex five sixty eight")]
    [InlineData(Swa + "number two behind a cessna correction fedex five sixty eight")]
    [InlineData(Swa + "follow fedex five sixty seven correction fedex five sixty eight")]
    public void TrafficDescription_BeforeCorrection_SaysAgain(string transcript)
    {
        DecodeResult result = DecodeSpoken(transcript, Room(WithBase(), Airport));

        Assert.Equal(DecodeState.SayAgain, result.State);
        Assert.Null(result.Callsign);
        Assert.Equal("SWA2495", result.BestGuess?.Entity.Value);
    }

    [Theory]
    [InlineData(Swa + "correction contact one two four point three")]
    [InlineData(Swa + "correction altimeter two nine nine two")]
    [InlineData(Swa + "correction wind two seven zero")]
    public void Correction_ValueWord_KeepsAddressee(string transcript) =>
        AssertResolved(DecodeSpoken(transcript, Room(WithBase(), Airport)), "SWA2495");

    [Theory]
    [InlineData("delta seven seventy two three thousand")]
    [InlineData("delta seven seventy two two seven zero")]
    [InlineData("delta seven seventy two three zero")]
    [InlineData("delta seven seventy two seven miles from mitoe")]
    [InlineData("medevac delta seven seventy two turn left")]
    [InlineData("runway three zero cleared to land delta seven seventy two")]
    [InlineData(Swa + "correction delta seven seventy two turn left")]
    public void Delta_IsPrefixed(string transcript) => AssertResolved(DecodeSpoken(transcript, Room(["DAL772", "SWA2495"], Airport)), "DAL772");

    [Fact]
    public void Delta_IsPrefixed_SecondAndTraffic()
    {
        TransmissionContext context = Room(["DAL772", "SWA2495"], Airport);

        Assert.Equal(DecodeState.SayAgain, DecodeSpoken(Swa + "and delta seven seventy two", context).State);
        DecodeResult traffic = DecodeSpoken(Swa + "follow delta seven seventy two", context);
        AssertResolved(traffic, "SWA2495");
        AssertHasEntity(traffic, EntityKind.Callsign, "DAL772");
    }

    [Theory]
    [InlineData(Swa + "left heading two seven zero", "SWA24", "SWA2495", "SWA2495")]
    [InlineData(Swa + "three thousand", "SWA24", "SWA2495", "SWA2495")]
    [InlineData(Swa + "two seven zero", "SWA24", "SWA2495", "SWA2495")]
    [InlineData("united twenty four ninety five two seven zero", "UAL249", "UAL2495", "UAL2495")]
    [InlineData("united two four nine five two seven zero", "UAL249", "UAL2495", "UAL2495")]
    public void LongestFlightWins_BothRoomOrders(string transcript, string shorter, string longer, string addressee)
    {
        AssertResolved(DecodeSpoken(transcript, Room([shorter, longer], Airport)), addressee);
        AssertResolved(DecodeSpoken(transcript, Room([longer, shorter], Airport)), addressee);
    }

    [Theory]
    [InlineData("SWA24", Swa + "seven miles from mitoe")]
    [InlineData("SWA24", Swa + "left heading two seven zero")]
    [InlineData("SWA24", Swa + "one five left")]
    [InlineData("SWA249", Swa + "seven miles")]
    [InlineData("SWA249", Swa + "three thousand")]
    [InlineData("UAL24", "united twenty four ninety five seven miles")]
    [InlineData("FDX56", "fedex five sixty eight two seven zero")]
    [InlineData("UAL12", "united twelve thirty")]
    [InlineData("UAL12", "united twelve fifteen descend and maintain three thousand")]
    [InlineData("UAL1", "united one two three four turn left")]
    public void ShorterFlight_DoesNotClaimDigits(string aircraft, string transcript) =>
        Assert.NotEqual(DecodeState.Resolved, DecodeSpoken(transcript, Room([aircraft], Airport)).State);

    [Theory]
    [InlineData("southwest two four nine five seven miles")]
    [InlineData("southwest two four nine five three thousand")]
    [InlineData("southwest two four nine five two seven zero")]
    public void DigitByDigit_ShorterFlight_NotResolved(string transcript) =>
        Assert.NotEqual(DecodeState.Resolved, DecodeSpoken(transcript, Room(["SWA249"], Airport)).State);

    [Theory]
    [InlineData("N1230L")]
    [InlineData("DAL30")]
    public void Prefixless_DoesNotStandInForWords(string aircraft)
    {
        DecodeResult result = DecodeSpoken("runway three zero cleared to land " + Swa.TrimEnd(), Room(WithBase(aircraft), Airport));

        AssertResolved(result, "SWA2495");
        AssertHasValue(result, "KOAK/30");
    }

    [Fact]
    public void LetterPrefix_MustBeHeard()
    {
        DecodeResult result = DecodeSpoken("eight two nine eight lima", Room(WithBase(), Airport));

        Assert.NotEqual(DecodeState.Resolved, result.State);
        Assert.Null(result.Callsign);
    }

    [Fact]
    public void Prefixless_LandThreeZero_IsTheRunway()
    {
        DecodeResult result = DecodeSpoken("land three zero", Room(WithBase("N1230L"), Airport));

        Assert.Equal(DecodeState.NoCallsign, result.State);
        AssertHasValue(result, "KOAK/30");
    }

    [Fact]
    public void Prefixless_HeadingIsNoGuessOfDelta()
    {
        DecodeResult result = DecodeSpoken("heading two one zero " + Swa.TrimEnd(), Room(WithBase("DAL210"), Airport));

        Assert.NotEqual("DAL210", result.BestGuess?.Entity.Value);
        Assert.NotEqual("DAL210", result.Callsign?.Entity.Value);
    }

    [Fact]
    public void Prefixless_RunwayLastThree_KeepsTrailingCallsign() =>
        AssertResolved(DecodeSpoken("runway nine eight lima " + Swa.TrimEnd(), Room(WithBase(), Airport)), "SWA2495");

    [Theory]
    [InlineData("skywest twenty four ninety five follow southwest twenty four ninety five")]
    [InlineData("southwest twenty four ninety eight follow southwest twenty four ninety five")]
    public void TrailingSpan_DoesNotHideLeading(string transcript) =>
        Assert.NotEqual("SWA2495", DecodeSpoken(transcript, Room(WithBase(), Airport)).Callsign?.Entity.Value);

    [Theory]
    [InlineData("air hong kong one two three four")]
    [InlineData(Swa + "correction air hong kong one two three four turn left")]
    public void LongTraffic_Span(string transcript) => AssertResolved(DecodeSpoken(transcript, Room(WithBase("AHK1234"), Airport)), "AHK1234");

    [Theory]
    [InlineData("one five miles from mitoe")]
    [InlineData("one two miles")]
    [InlineData("three zero mile arc")]
    [InlineData("one five degrees right")]
    [InlineData("three zero degrees left")]
    [InlineData("one five knots")]
    [InlineData("one two o'clock")]
    [InlineData("expect a delay of one five minutes")]
    [InlineData("hold for one five minutes")]
    [InlineData("maintain two one zero for one five miles")]
    [InlineData("vectors for three zero miles")]
    [InlineData("gusts of three zero")]
    [InlineData("one five left")]
    [InlineData("hold for thirty seconds")]
    [InlineData("wait thirty seconds")]
    [InlineData("one five seconds")]
    [InlineData("one five feet")]
    [InlineData("three zero dme arc")]
    [InlineData("one five nautical miles from mitoe")]
    public void RunwayValue_NotARunway(string phrase)
    {
        TransmissionContext context = Room(WithBase(), Airport);

        Assert.DoesNotContain(DecodeSpoken(Swa + phrase, context).Entities, match => match.Entity.Kind == EntityKind.Runway);
        Assert.DoesNotContain(DecodeSpoken(phrase, context).Entities, match => match.Entity.Kind == EntityKind.Runway);
    }

    [Theory]
    [InlineData("give way to nine eight lima")]
    [InlineData("behind nine eight lima")]
    [InlineData("give way to november four five six charlie")]
    [InlineData("behind november six seven kilo")]
    [InlineData("behind seven three seven charlie")]
    [InlineData("at one five charlie")]
    [InlineData("to three thousand hotel")]
    [InlineData("hold short of november six alpha bravo charlie")]
    public void TaxiwayWalk_StopsAtCallsignFragments(string phrase) =>
        Assert.DoesNotContain(DecodeSpoken(Swa + phrase, Room(WithBase(), Airport)).Entities, match => match.Entity.Kind == EntityKind.Taxiway);

    [Fact]
    public void TaxiwayWalk_CrossesShortTaxiwayDigits()
    {
        DecodeResult result = DecodeSpoken(Swa + "taxi via whiskey four golf", Room(WithBase(), Airport));

        AssertHasEntity(result, EntityKind.Taxiway, "KOAK/W4");
        AssertHasEntity(result, EntityKind.Taxiway, "KOAK/G");
    }

    [Fact]
    public void DigitTail_InsideOneWord() =>
        Assert.NotEqual(DecodeState.Resolved, DecodeSpoken("southwest 2495270", Room(WithBase(), Airport)).State);

    [Fact]
    public void OwnRunwayTail()
    {
        ContextEntity runway30 = Airport.Single(entity => (entity.Kind == EntityKind.Runway) && (entity.Value == "KOAK/30"));
        var context = new TransmissionContext("KOAK", [Aircraft("SWA2495", ["SWA2495"], [runway30])], []);

        DecodeResult result = DecodeSpoken(Swa + "three zero", context);

        AssertResolved(result, "SWA2495");
        Assert.True(Assert.Single(result.Entities, match => match.Entity.Value == "KOAK/30").OwnAircraft);
    }

    [Fact]
    public void Bare_RefusedAtRunStart() =>
        Assert.DoesNotContain(DecodeSpoken(Swa + "charlie", Room(WithBase(), Airport)).Entities, match => match.Entity.Kind == EntityKind.Taxiway);

    [Theory]
    [InlineData("taxi via charlie southwest twenty four ninety five", "SWA2495", "KOAK/C")]
    [InlineData("join taxiway bravo southwest twenty four ninety five", "SWA2495", null)]
    [InlineData("cross taxiway charlie fedex five sixty eight", "FDX568", null)]
    [InlineData("hold short charlie fedex five sixty eight", "FDX568", null)]
    public void StrayLetter_DoesNotBeatExactCallsign(string transcript, string addressee, string? taxiway)
    {
        DecodeResult result = DecodeSpoken(transcript, Room(WithBase(), Airport));

        AssertResolved(result, addressee);
        if (taxiway is not null)
        {
            AssertHasEntity(result, EntityKind.Taxiway, taxiway);
        }
    }

    [Fact]
    public void DigitRun_SpanStartRefused() => AssertLacksValue(DecodeSpoken("hold short of one three zero", Room(WithBase(), Airport)), "KOAK/30");

    [Fact]
    public void StartOffset_Limit()
    {
        AssertResolved(DecodeSpoken("good morning fedex five sixty eight turn left", Room(WithBase(), Airport)), "FDX568");
        Assert.Equal(DecodeState.NoCallsign, DecodeSpoken("good morning tower fedex five sixty eight turn left", Room(WithBase(), Airport)).State);
    }

    [Fact]
    public void HeavyOnTrailingCallsign()
    {
        DecodeResult result = DecodeSpoken("runway three zero cleared to land fedex five sixty eight heavy", Room(WithBase(), Airport));

        AssertResolved(result, "FDX568");
        Assert.Equal(new WordSpan(6, 11), result.Callsign!.Span);
    }

    [Fact]
    public void CallsignTopK_Limit()
    {
        ContextEntity runway30 = Airport.Single(entity => (entity.Kind == EntityKind.Runway) && (entity.Value == "KOAK/30"));
        string[] callsigns = ["QXE2101", "HOR2101"];
        RoomAircraft[] aircraft = [Aircraft("QXE2101", callsigns, [runway30]), Aircraft("HOR2101", callsigns, [])];
        var context = new TransmissionContext("KOAK", aircraft, Airport);
        var input = new DecoderInput(
            [new DecoderHypothesis("horizon twenty one oh one runway three zero", null, null)],
            InputSource.Spoken,
            MatchMode.Fuzzy
        );

        DecodeResult result = ContextDecoder.Decode(input, context, Settings with { CallsignTopK = 1 });

        // Only the higher-scoring telephony survives, so the remainder that would pick QXE2101 is never read.
        AssertResolved(result, "HOR2101");
    }

    [Fact]
    public void SayAgain_RaisedThreshold()
    {
        TransmissionContext context = Room(["SWA2495"], Airport);

        Assert.Equal(DecodeState.SayAgain, DecodeSpoken("skywest twenty four ninety five abeam kizer", context).State);
        AssertLacksValue(DecodeSpoken("skywest twenty four ninety five abeam kizer", context), "KOAK/KAISER");
        AssertHasValue(DecodeSpoken("skywest twenty four ninety five abeam kaiser", context), "KOAK/KAISER");
    }

    [Fact]
    public void Rewrite_CallsignName()
    {
        DecodeResult result = DecodeSpoken("southwests twenty four ninety five", Room(["SWA2495"], Airport));

        AssertResolved(result, "SWA2495");
        Assert.Equal("southwest twenty four ninety five", result.RewrittenTranscript);
    }

    [Fact]
    public void NBest_Tie_KeepsLowerIndex()
    {
        const string text = "southwest twenty four ninety five";
        DecoderHypothesis[] hypotheses = [new(text, null, -5.0), new(text, null, -5.0)];

        Assert.Equal(0, Decode(hypotheses, InputSource.Spoken, MatchMode.Fuzzy, Room(["SWA2495"], Airport)).HypothesisIndex);
    }

    private static string[] WithBase(params string[] extra) => [.. extra.Concat(["SWA2495", "FDX568", "N8298L"]).Distinct()];

    private static ContextEntity FixOf(string value, string form) => new(EntityKind.Fix, value, [new SpokenForm(form, 1.0)]);

    private static void AssertLacksSuffix(DecodeResult result, string suffix) =>
        Assert.DoesNotContain(result.Entities, match => match.Entity.Value.EndsWith(suffix, StringComparison.Ordinal));

    private static IReadOnlyList<ContextEntity> BuildAirport(IReadOnlyList<ContextEntity> landmarks) =>
        AirportContextBuilder.Build("KOAK", new TestAirportGroundData().GetLayout("OAK"), 10, landmarks, Nav, Settings);

    private static RoomAircraft Aircraft(string callsign, IReadOnlyCollection<string> room, IReadOnlyList<ContextEntity> own)
    {
        string? type = callsign.StartsWith('N') ? "C172" : null;
        ContextEntity? entity = ContextEntityFactory.Callsign(callsign, type, Overrides, room, Settings);
        Assert.NotNull(entity);
        return new RoomAircraft(entity, own);
    }

    private static TransmissionContext Room(string[] callsigns, IReadOnlyList<ContextEntity> airport) =>
        new("KOAK", [.. callsigns.Select(callsign => Aircraft(callsign, callsigns, []))], airport);

    private static DecodeResult Decode(DecoderHypothesis[] hypotheses, InputSource source, MatchMode mode, TransmissionContext context) =>
        ContextDecoder.Decode(new DecoderInput(hypotheses, source, mode), context, Settings);

    private static DecodeResult DecodeSpoken(string transcript, TransmissionContext context) =>
        Decode([new DecoderHypothesis(transcript, null, null)], InputSource.Spoken, MatchMode.Fuzzy, context);

    private static DecodeResult DecodeWithPieces(TokenLogProb[] pieces) =>
        Decode([new DecoderHypothesis(KizerTranscript, pieces, null)], InputSource.Spoken, MatchMode.Fuzzy, Room(["SWA2495"], Airport));

    private static TokenLogProb[] KizerPieces(string abeamPiece, double kizerPieceLogProb) =>
        [
            new("▁southwest", -0.01),
            new("▁twenty", -0.01),
            new("▁four", -0.01),
            new("▁ninety", -0.01),
            new("▁five", -0.01),
            new(abeamPiece, -0.01),
            new("▁ki", kizerPieceLogProb),
            new("zer", kizerPieceLogProb),
        ];

    private static void AssertResolved(DecodeResult result, string callsign)
    {
        Assert.Equal(DecodeState.Resolved, result.State);
        Assert.Equal(callsign, result.Callsign?.Entity.Value);
    }

    private static void AssertHasEntity(DecodeResult result, EntityKind kind, string value) =>
        Assert.Contains(result.Entities, match => (match.Entity.Kind == kind) && (match.Entity.Value == value));

    private static void AssertHasValue(DecodeResult result, string value) => Assert.Contains(result.Entities, match => match.Entity.Value == value);

    private static void AssertLacksValue(DecodeResult result, string value) =>
        Assert.DoesNotContain(result.Entities, match => match.Entity.Value == value);
}
