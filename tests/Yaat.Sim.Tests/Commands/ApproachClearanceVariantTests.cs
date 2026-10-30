using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Speech;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// A varied solo pilot reads back an approach clearance, a join and an expected approach by the frequency's load band: terse
/// on a busy frequency, a courtesy on a quiet one. Only the spoken form varies, and only the final clearance clause of a
/// compound CAPP; the terminal text is the canonical in every band. A busy pilot never drops "straight in", "join", the
/// approach type or its variant; a CAPP that names no type, or a circling approach, keeps its canonical words.
/// </summary>
public class ApproachClearanceVariantTests
{
    private const string Cs = "november one two three";

    public ApproachClearanceVariantTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // Five miles east of KOAK 28R, heading for it.
    private static AircraftState MakeArrival() =>
        new()
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(295),
            Altitude = 3000,
            IndicatedAirspeed = 180,
            Position = new LatLon(37.735, -122.12),
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };

    private static PilotSpeechContext Varied(FrequencyLoadBand band, int seed, int second) =>
        new(PilotPersonality.Varied, band, seed, second)
        {
            ShortenCallsign = false,
            GroupContactFrequency = false,
            CurrentIndicatedAirspeed = null,
        };

    private static CompoundCommand ParseCompound(string text) => CommandParser.ParseCompound(text).Value!;

    /// <summary>The readbacks a varied pilot speaks over seeds 0-4 and seconds 0-199 for a compound dispatched once.</summary>
    private static List<PilotSpeechText> FirstBlockReadbacks(string text, FrequencyLoadBand band, AircraftState ac)
    {
        CompoundCommand compound = ParseCompound(text);
        CommandResult result = CommandDispatcher.Dispatch(compound.Blocks[0].Commands[0], ac, TestDispatch.Context(new Random(1)));
        Assert.True(result.Success, result.Message);
        return Sweep(ctx => PilotResponder.BuildReadbackAsApplied(compound, result, ac, ctx, ActiveRunways.Empty), band);
    }

    /// <summary>The readbacks a varied pilot speaks for a compound with no handler readback (a later block, or no dispatch).</summary>
    private static List<PilotSpeechText> UndispatchedReadbacks(string text, FrequencyLoadBand band, AircraftState ac)
    {
        CompoundCommand compound = ParseCompound(text);
        return Sweep(ctx => PilotResponder.BuildReadbackAsApplied(compound, new CommandResult(true, "queued"), ac, ctx, ActiveRunways.Empty), band);
    }

    private static List<PilotSpeechText> Sweep(Func<PilotSpeechContext, PilotSpeechText?> build, FrequencyLoadBand band) =>
        [
            .. Enumerable
                .Range(0, 5)
                .SelectMany(seed =>
                    Enumerable.Range(0, 200).Select(second => build(Varied(band, seed, second)) ?? throw new InvalidOperationException("no readback"))
                ),
        ];

    private static void AssertSpoken(IEnumerable<string> spoken, params string[] expected)
    {
        HashSet<string> actual = [.. spoken];
        string expectedLines = string.Join(Environment.NewLine, expected);
        string spokenLines = string.Join(Environment.NewLine, actual);
        string nl = Environment.NewLine;
        string message = $"Expected exactly:{nl}{expectedLines}{nl}Spoken:{nl}{spokenLines}";
        Assert.True(actual.SetEquals(expected), message);
    }

    private static void AssertTerminal(IEnumerable<PilotSpeechText> readbacks, string terminal) =>
        Assert.All(readbacks, readback => Assert.Equal(terminal, readback.Terminal));

    /// <summary>The text after the last "then ", the later block's clause and the closing callsign.</summary>
    private static string LaterClause(string tts) => tts[(tts.LastIndexOf("then ", StringComparison.Ordinal) + 5)..];

    // --- First block: the handler's readback varies ---

    [Theory]
    [InlineData("CAPP ILS28R")]
    [InlineData("CAPPF ILS28R")]
    public void Capp_Quiet_SpeaksTheCanonicalOrTheCourtesyRowOnce(string text)
    {
        List<PilotSpeechText> readbacks = FirstBlockReadbacks(text, FrequencyLoadBand.Quiet, MakeArrival());

        AssertSpoken(
            readbacks.Select(r => r.Tts),
            $"cleared ILS runway two eight right approach, {Cs}.",
            $"cleared ILS runway two eight right approach, {Cs}, thanks."
        );
        Assert.DoesNotContain(readbacks, r => r.Tts.Contains("thanks, thanks", StringComparison.Ordinal));
        AssertTerminal(readbacks, "cleared ILS runway 28R approach");
    }

    [Fact]
    public void Capp_Normal_SpeaksTheCanonical()
    {
        List<PilotSpeechText> readbacks = FirstBlockReadbacks("CAPP ILS28R", FrequencyLoadBand.Normal, MakeArrival());

        AssertSpoken(readbacks.Select(r => r.Tts), $"cleared ILS runway two eight right approach, {Cs}.");
        AssertTerminal(readbacks, "cleared ILS runway 28R approach");
    }

    [Theory]
    [InlineData("CAPP ILS28R")]
    [InlineData("CAPPF ILS28R")]
    public void Capp_Busy_DropsApproachThenRunwayButKeepsTypeAndRunway(string text)
    {
        List<PilotSpeechText> readbacks = FirstBlockReadbacks(text, FrequencyLoadBand.Busy, MakeArrival());

        AssertSpoken(readbacks.Select(r => r.Tts), $"cleared ILS runway two eight right, {Cs}.", $"cleared ILS two eight right, {Cs}.");
        AssertTerminal(readbacks, "cleared ILS runway 28R approach");
    }

    [Fact]
    public void Cappsi_Busy_KeepsStraightIn()
    {
        List<PilotSpeechText> readbacks = FirstBlockReadbacks("CAPPSI ILS28R", FrequencyLoadBand.Busy, MakeArrival());

        AssertSpoken(
            readbacks.Select(r => r.Tts),
            $"cleared straight in ILS runway two eight right, {Cs}.",
            $"cleared straight in ILS two eight right, {Cs}."
        );
        AssertTerminal(readbacks, "cleared straight-in ILS runway 28R approach");
    }

    /// <summary>CAPPSI has no Quiet rows: a quiet pilot speaks the canonical, which the quiet flavour may close with thanks.</summary>
    [Fact]
    public void Cappsi_Quiet_SpeaksTheCanonicalWithTheFlavour()
    {
        List<PilotSpeechText> readbacks = FirstBlockReadbacks("CAPPSI ILS28R", FrequencyLoadBand.Quiet, MakeArrival());

        AssertSpoken(
            readbacks.Select(r => r.Tts),
            $"cleared straight in ILS runway two eight right approach, {Cs}.",
            $"cleared straight in ILS runway two eight right approach, thanks, {Cs}."
        );
    }

    [Theory]
    [InlineData("JAPP ILS28R", "join the ILS runway 28R approach")]
    [InlineData("JAPPF ILS28R", "join the ILS runway 28R approach")]
    public void Japp_Busy_KeepsJoinTheTypeAndRunway(string text, string terminal)
    {
        List<PilotSpeechText> readbacks = FirstBlockReadbacks(text, FrequencyLoadBand.Busy, MakeArrival());

        AssertSpoken(readbacks.Select(r => r.Tts), $"join the ILS runway two eight right, {Cs}.");
        AssertTerminal(readbacks, terminal);
    }

    [Fact]
    public void Japp_Normal_SpeaksTheCanonical() =>
        AssertSpoken(
            FirstBlockReadbacks("JAPP ILS28R", FrequencyLoadBand.Normal, MakeArrival()).Select(r => r.Tts),
            $"join the ILS runway two eight right approach, {Cs}."
        );

    [Fact]
    public void Jappsi_Busy_KeepsJoinTheStraightIn()
    {
        List<PilotSpeechText> readbacks = FirstBlockReadbacks("JAPPSI ILS28R", FrequencyLoadBand.Busy, MakeArrival());

        AssertSpoken(readbacks.Select(r => r.Tts), $"join the straight in ILS runway two eight right, {Cs}.");
        AssertTerminal(readbacks, "join the straight-in ILS runway 28R approach");
    }

    [Fact]
    public void BareCapp_WithAnExpectedApproach_VariesThatApproach()
    {
        AircraftState ac = MakeArrival();
        ac.Approach.Expected = "I28R";

        List<PilotSpeechText> readbacks = FirstBlockReadbacks("CAPP", FrequencyLoadBand.Busy, ac);

        AssertSpoken(readbacks.Select(r => r.Tts), $"cleared ILS runway two eight right, {Cs}.", $"cleared ILS two eight right, {Cs}.");
        AssertTerminal(readbacks, "cleared ILS runway 28R approach");
    }

    // --- Compound CAPP: only the final clearance clause varies ---

    private const string CompoundPrefix = "cleared direct sunol, cross sunol at or above three thousand four hundred, ";

    /// <summary>The route and crossing chain the clearance after them, and a chained readback is never chatty: no courtesy, no flavour.</summary>
    [Fact]
    public void CompoundCapp_Quiet_SpeaksTheNormalRowsWithNoCourtesy()
    {
        List<PilotSpeechText> readbacks = FirstBlockReadbacks("CAPP DCT SUNOL CFIX A034 ILS28R", FrequencyLoadBand.Quiet, MakeArrival());

        AssertSpoken(readbacks.Select(r => r.Tts), $"{CompoundPrefix}cleared ILS runway two eight right approach, {Cs}.");
        AssertTerminal(readbacks, "cleared direct SUNOL, cross SUNOL at or above 3400, cleared ILS runway 28R approach");
    }

    [Fact]
    public void CompoundCapp_Busy_VariesOnlyTheClearance()
    {
        List<PilotSpeechText> readbacks = FirstBlockReadbacks("CAPP DCT SUNOL CFIX A034 ILS28R", FrequencyLoadBand.Busy, MakeArrival());

        AssertSpoken(
            readbacks.Select(r => r.Tts),
            $"{CompoundPrefix}cleared ILS runway two eight right, {Cs}.",
            $"{CompoundPrefix}cleared ILS two eight right, {Cs}."
        );
        AssertTerminal(readbacks, "cleared direct SUNOL, cross SUNOL at or above 3400, cleared ILS runway 28R approach");
    }

    // --- Later blocks: the builder's clause varies ---

    [Theory]
    [InlineData("DM 20; CAPP ILS28R", "cleared ILS runway two eight right", "cleared ILS two eight right")]
    [InlineData("DM 20; CAPPSI ILS28R", "cleared straight in ILS runway two eight right", "cleared straight in ILS two eight right")]
    [InlineData("DM 20; JAPP ILS28R", "join the ILS runway two eight right", null)]
    [InlineData("DM 20; JAPPSI ILS28R", "join the straight in ILS runway two eight right", null)]
    public void LaterBlock_Busy_VariesTheApproachClause(string text, string row, string? secondRow)
    {
        List<PilotSpeechText> readbacks = UndispatchedReadbacks(text, FrequencyLoadBand.Busy, MakeArrival());

        string[] expected = secondRow is null ? [$"{row}, {Cs}."] : [$"{row}, {Cs}.", $"{secondRow}, {Cs}."];
        AssertSpoken(readbacks.Select(r => LaterClause(r.Tts)), expected);
        Assert.Single(readbacks.Select(r => r.Terminal).Distinct());
    }

    /// <summary>A later-block bare CAPP clears the expected approach when it fires, so it names that approach as the first block does.</summary>
    [Fact]
    public void BareCapp_InALaterBlock_WithAnExpectedApproach_NamesItInTheCanonical()
    {
        AircraftState ac = MakeArrival();
        ac.Approach.Expected = "I28R";

        PilotSpeechText? readback = PilotResponder.BuildReadbackAsApplied(
            ParseCompound("DM 20; CAPP"),
            new CommandResult(true, "queued"),
            ac,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );

        Assert.NotNull(readback);
        Assert.EndsWith("then cleared ILS runway 28R approach", readback.Terminal);
        Assert.EndsWith($"then cleared ILS runway two eight right approach, {Cs}.", readback.Tts);
    }

    [Fact]
    public void BareCapp_InALaterBlock_WithAnExpectedApproach_Varies()
    {
        AircraftState ac = MakeArrival();
        ac.Approach.Expected = "I28R";

        List<PilotSpeechText> readbacks = UndispatchedReadbacks("DM 20; CAPP", FrequencyLoadBand.Busy, ac);

        AssertSpoken(readbacks.Select(r => LaterClause(r.Tts)), $"cleared ILS runway two eight right, {Cs}.", $"cleared ILS two eight right, {Cs}.");
    }

    [Fact]
    public void BareCapp_WithAnExpectedApproach_SpokenCandidatesNameIt()
    {
        AircraftState ac = MakeArrival();
        ac.Approach.Expected = "I28R";

        Assert.Equal(
            ["cleared ILS runway two eight right", "cleared ILS two eight right"],
            PhraseologyVerbalizer.SpokenCandidates(CommandParser.Parse("CAPP").Value!, ac, FrequencyLoadBand.Busy, null)
        );
    }

    [Fact]
    public void BareCapp_WithNoExpectedApproach_IsCanonicalOnly() =>
        AssertSpoken(UndispatchedReadbacks("CAPP", FrequencyLoadBand.Busy, MakeArrival()).Select(r => r.Tts), $"cleared approach, {Cs}.");

    // --- No type, circling ---

    [Fact]
    public void CappNamingNoType_KeepsClearedApproachAndAddsOnlyTheCourtesy()
    {
        AssertSpoken(
            UndispatchedReadbacks("CAPP Z28R", FrequencyLoadBand.Quiet, MakeArrival()).Select(r => r.Tts),
            $"cleared approach, {Cs}.",
            $"cleared approach, {Cs}, thanks."
        );
        AssertSpoken(UndispatchedReadbacks("CAPP Z28R", FrequencyLoadBand.Normal, MakeArrival()).Select(r => r.Tts), $"cleared approach, {Cs}.");
        AssertSpoken(UndispatchedReadbacks("CAPP Z28R", FrequencyLoadBand.Busy, MakeArrival()).Select(r => r.Tts), $"cleared approach, {Cs}.");
    }

    [Theory]
    [InlineData(FrequencyLoadBand.Normal)]
    [InlineData(FrequencyLoadBand.Busy)]
    public void CappCircling_IsCanonical(FrequencyLoadBand band) =>
        AssertSpoken(UndispatchedReadbacks("CAPP VOR-A", band, MakeArrival()).Select(r => r.Tts), $"cleared approach, {Cs}.");

    /// <summary>
    /// A real circling approach (KAJO VOR-A) names no runway, so the handler carries no variant set for it: the handler
    /// refuses to clear it with no readback, and its spoken parts have no runway for a set's <c>{rwy}</c>.
    /// </summary>
    [Fact]
    public void CirclingProcedure_HasNoRunwayAndTheHandlerCarriesNoSet()
    {
        CifpApproachProcedure procedure =
            NavigationDatabase.Instance.GetApproach("KAJO", "VOR-A") ?? throw new InvalidOperationException("KAJO VOR-A missing");
        var aircraft = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "C172",
            TrueHeading = new TrueHeading(120),
            Altitude = 4000,
            IndicatedAirspeed = 100,
            Position = new LatLon(33.98, -117.70),
            FlightPlan = new AircraftFlightPlan { Destination = "AJO" },
        };

        Assert.Equal(("VOR Alpha", (string?)null), ApproachReadbackName.SpokenParts(procedure));
        foreach (string text in new[] { "CAPP VOR-A", "CAPPF VOR-A", "JAPP VOR-A" })
        {
            CommandResult result = CommandDispatcher.Dispatch(CommandParser.Parse(text).Value!, aircraft, TestDispatch.Context(new Random(1)));
            Assert.False(result.Success, text);
            Assert.Null(result.PilotReadback);
        }
    }

    /// <summary>A circling id in a later block reads "cleared approach" in every band; a quiet frequency may add only its flavour.</summary>
    [Fact]
    public void CappCircling_Quiet_IsCanonicalOrItsFlavour() =>
        Assert.Subset(
            new HashSet<string> { $"cleared approach, {Cs}.", $"cleared approach, thanks, {Cs}." },
            UndispatchedReadbacks("CAPP VOR-A", FrequencyLoadBand.Quiet, MakeArrival()).Select(r => r.Tts).ToHashSet()
        );

    // --- An EAPP earlier in the compound names a later bare CAPP's approach ---

    /// <summary>
    /// A bare CAPP clears the approach expected when it fires, so an EAPP before it in the compound (an earlier block or
    /// earlier in its block) names it, not the expected approach at readback time.
    /// </summary>
    [Theory]
    [InlineData("DM 20; EAPP RNAV28RZ; CAPP")]
    [InlineData("DM 20; EAPP RNAV28RZ, CAPP")]
    public void BareCapp_AfterAnEappInTheCompound_NamesThatApproach(string text)
    {
        AircraftState ac = MakeArrival();
        ac.Approach.Expected = "I28R";

        var queued = new CommandResult(true, "queued");
        PilotSpeechText? readback = PilotResponder.BuildReadbackAsApplied(
            ParseCompound(text),
            queued,
            ac,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );

        Assert.NotNull(readback);
        Assert.EndsWith("cleared RNAV Z runway 28R approach", readback.Terminal);
        Assert.EndsWith($"cleared RNAV Zulu runway two eight right approach, {Cs}.", readback.Tts);
    }

    // --- EAPP ---

    /// <summary>A visual EAPP has no variant rows: every band speaks the canonical, a quiet frequency only adding its flavour.</summary>
    [Theory]
    [InlineData(FrequencyLoadBand.Quiet)]
    [InlineData(FrequencyLoadBand.Normal)]
    [InlineData(FrequencyLoadBand.Busy)]
    public void VisualEapp_IsCanonicalInEveryBand(FrequencyLoadBand band)
    {
        string canonical = $"expect visual approach runway two eight right, {Cs}.";
        string flavoured = $"expect visual approach runway two eight right, thanks, {Cs}.";
        HashSet<string> allowed = (band == FrequencyLoadBand.Quiet) ? [canonical, flavoured] : [canonical];
        List<PilotSpeechText> readbacks = UndispatchedReadbacks("EAPP VIS28R", band, MakeArrival());

        Assert.Subset(allowed, readbacks.Select(r => r.Tts).ToHashSet());
        Assert.Contains(canonical, readbacks.Select(r => r.Tts));
        AssertTerminal(readbacks, "expect visual approach runway 28R");
    }

    [Theory]
    [InlineData("EAPP RNAV28RZ", "RNAV Zulu", "RNAV Z")]
    [InlineData("EAPP LOC28R", "localizer", "LOC")]
    public void Eapp_VariesByBand(string text, string spokenType, string terminalType)
    {
        List<PilotSpeechText> quiet = UndispatchedReadbacks(text, FrequencyLoadBand.Quiet, MakeArrival());
        List<PilotSpeechText> normal = UndispatchedReadbacks(text, FrequencyLoadBand.Normal, MakeArrival());
        List<PilotSpeechText> busy = UndispatchedReadbacks(text, FrequencyLoadBand.Busy, MakeArrival());

        string canonical = $"expect {spokenType} runway two eight right approach, {Cs}.";
        AssertSpoken(quiet.Select(r => r.Tts), canonical, $"we'll expect the {spokenType} runway two eight right, {Cs}.");
        AssertSpoken(normal.Select(r => r.Tts), canonical, $"expect the {spokenType} runway two eight right, {Cs}.");
        AssertSpoken(busy.Select(r => r.Tts), $"expect {spokenType} runway two eight right, {Cs}.");
        AssertTerminal([.. quiet, .. normal, .. busy], $"expect {terminalType} runway 28R approach");
    }

    // --- The varied clause is the canonical's final clause ---

    /// <summary>
    /// The set's pattern rendered with the handler's captures ends the handler's canonical readback, so the readback varies
    /// that clause and never falls back to the canonical for a mismatch.
    /// </summary>
    [Theory]
    [InlineData("CAPP ILS28R", "cleared approach")]
    [InlineData("CAPPF ILS28R", "cleared approach")]
    [InlineData("CAPP DCT SUNOL CFIX A034 ILS28R", "cleared approach")]
    [InlineData("CAPP AT SUNOL H28RZ", "cleared approach")]
    [InlineData("CAPPSI ILS28R", "cleared straight in approach")]
    [InlineData("JAPP LOC28R", "join approach")]
    [InlineData("JAPPF ILS28R", "join approach")]
    [InlineData("JAPPSI ILS28R", "join straight in approach")]
    public void FirstBlock_ThePatternEndsTheHandlersCanonical(string text, string setName)
    {
        CommandResult result = CommandDispatcher.Dispatch(CommandParser.Parse(text).Value!, MakeArrival(), TestDispatch.Context(new Random(1)));

        HandlerReadback readback = Assert.IsType<HandlerReadback>(result.PilotReadback);
        Assert.Equal(setName, readback.Variants?.Name);
        Assert.NotNull(readback.Captures);
        Assert.EndsWith(
            PhraseologyVerbalizer.RenderPattern(readback.Variants!.Pattern, readback.Captures),
            readback.Speech.Tts,
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData("CAPP ILS28R", "cleared approach")]
    [InlineData("CAPP DCT SUNOL CFIX A034 ILS28R", "cleared approach")]
    [InlineData("CAPP Z28R", "cleared approach naming no type")]
    [InlineData("CAPPSI ILS28R", "cleared straight in approach")]
    [InlineData("JAPP RNAV28RZ", "join approach")]
    [InlineData("JAPPSI ILS28R", "join straight in approach")]
    public void LaterBlock_ThePatternEndsTheBuildersCanonical(string text, string setName)
    {
        PilotResponder.BuilderClause clause = PilotResponder.VariedBuilderClause(CommandParser.Parse(text).Value!, MakeArrival())!.Value;

        Assert.Equal(setName, clause.Variants.Name);
        Assert.NotNull(clause.Captures);
        Assert.EndsWith(
            PhraseologyVerbalizer.RenderPattern(clause.Variants.Pattern, clause.Captures),
            clause.Canonical.Tts,
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData("CAPP VOR-A")]
    [InlineData("CAPP")]
    public void LaterBlock_CirclingOrBareWithNoExpected_HasNoVariantSet(string text) =>
        Assert.Null(PilotResponder.VariedBuilderClause(CommandParser.Parse(text).Value!, MakeArrival()));

    // --- Class B composition ---

    [Fact]
    public void WithBravoClearance_CappQuietCourtesyRow_TheClearanceComesBeforeTheCallsign()
    {
        AircraftState ac = MakeArrival();
        PilotSpeechText row = FirstBlockReadbacks("CAPP ILS28R", FrequencyLoadBand.Quiet, ac)
            .First(r => r.Tts.EndsWith("thanks.", StringComparison.Ordinal));

        PilotSpeechText result = PilotResponder.WithBravoClearance(ac, row, BravoClearanceWording.Into, Varied(FrequencyLoadBand.Quiet, 0, 0));

        Assert.Equal($"cleared ILS runway two eight right approach, cleared into the bravo, {Cs}.", result.Tts);
        Assert.Equal("cleared ILS runway 28R approach, cleared into the bravo", result.Terminal);
    }

    [Fact]
    public void WithBravoClearance_CappBusyRow_TheClearanceComesBeforeTheCallsign()
    {
        AircraftState ac = MakeArrival();
        PilotSpeechText row = FirstBlockReadbacks("CAPP ILS28R", FrequencyLoadBand.Busy, ac)
            .First(r => r.Tts.StartsWith("cleared ILS two eight right", StringComparison.Ordinal));

        PilotSpeechText result = PilotResponder.WithBravoClearance(ac, row, BravoClearanceWording.Into, Varied(FrequencyLoadBand.Busy, 0, 0));

        Assert.Equal($"cleared ILS two eight right, cleared into the bravo, {Cs}.", result.Tts);
    }
}
