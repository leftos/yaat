using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Speech;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// A varied solo pilot reads back a PTAC (7110.65 5-6-2.a.1, 5-9-4, AIM 4-4-7.b) by the frequency's load band: a courtesy on
/// a quiet frequency, "turn" or "fly" dropped on a normal one, and on a busy one the vertical verb as "maintain" or the
/// climbing/descending progressive and the word runway or "approach" dropped. The turn direction, the word heading, the
/// heading, the altitude, the approach type and the runway always stay; the terminal text is the canonical in every band. A
/// PTAC on the present heading, a later-block PTAC with PA, or one naming no approach and runway is read back canonical in
/// every band.
/// </summary>
public class PtacVariantTests
{
    private const string Cs = "november one two three";

    private const string DescendTurn =
        "turn left heading two four zero, descend and maintain three thousand, cleared ILS runway two eight right approach";

    private const string ClimbFly = "fly heading two four zero, climb and maintain five thousand, cleared ILS runway two eight right approach";

    public PtacVariantTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // Five miles east of KOAK 28R, heading for it.
    private static AircraftState MakeArrival(double altitude) =>
        new()
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(295),
            Altitude = altitude,
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
    private static List<PilotSpeechText> FirstBlockReadbacks(string text, FrequencyLoadBand band, double altitude)
    {
        AircraftState ac = MakeArrival(altitude);
        CompoundCommand compound = ParseCompound(text);
        CommandResult result = CommandDispatcher.Dispatch(compound.Blocks[0].Commands[0], ac, TestDispatch.Context(new Random(1)));
        Assert.True(result.Success, result.Message);
        return Sweep(ctx => PilotResponder.BuildReadbackAsApplied(compound, result, ac, ctx, ActiveRunways.Empty), band);
    }

    /// <summary>The readbacks a varied pilot speaks for a compound with no handler readback (a later block).</summary>
    private static List<PilotSpeechText> UndispatchedReadbacks(string text, FrequencyLoadBand band, double altitude)
    {
        AircraftState ac = MakeArrival(altitude);
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
        string nl = Environment.NewLine;
        string message = $"Expected exactly:{nl}{string.Join(nl, expected)}{nl}Spoken:{nl}{string.Join(nl, actual)}";
        Assert.True(actual.SetEquals(expected), message);
    }

    private static void AssertTerminal(IEnumerable<PilotSpeechText> readbacks, string terminal) =>
        Assert.All(readbacks, readback => Assert.Equal(terminal, readback.Terminal));

    /// <summary>The text after the last "then ", the later block's clause and the closing callsign.</summary>
    private static string LaterClause(string tts) => tts[(tts.LastIndexOf("then ", StringComparison.Ordinal) + 5)..];

    // --- First block, descend shape with a turn direction ---

    [Fact]
    public void TurnDescend_Quiet_SpeaksTheCanonicalOrTheCourtesyRowOnce()
    {
        List<PilotSpeechText> readbacks = FirstBlockReadbacks("PTAC L240 030 ILS28R", FrequencyLoadBand.Quiet, 5000);

        AssertSpoken(readbacks.Select(r => r.Tts), $"{DescendTurn}, {Cs}.", $"{DescendTurn}, {Cs}, thanks.");
        Assert.DoesNotContain(readbacks, r => r.Tts.Contains("thanks, thanks", StringComparison.Ordinal));
        AssertTerminal(readbacks, "turn left heading 240, descend and maintain 3000, cleared ILS runway 28R approach");
    }

    [Fact]
    public void TurnDescend_Normal_SpeaksTheCanonicalOrDropsTurn() =>
        AssertSpoken(
            FirstBlockReadbacks("PTAC L240 030 ILS28R", FrequencyLoadBand.Normal, 5000).Select(r => r.Tts),
            $"{DescendTurn}, {Cs}.",
            $"left heading two four zero, descend and maintain three thousand, cleared ILS runway two eight right approach, {Cs}."
        );

    [Theory]
    [InlineData("PTAC L240 030 ILS28R")]
    [InlineData("PTACF L240 030 ILS28R")]
    public void TurnDescend_Busy_KeepsDirectionHeadingAltitudeTypeAndRunway(string text)
    {
        List<PilotSpeechText> readbacks = FirstBlockReadbacks(text, FrequencyLoadBand.Busy, 5000);

        AssertSpoken(
            readbacks.Select(r => r.Tts),
            $"left heading two four zero, maintain three thousand, cleared ILS runway two eight right, {Cs}.",
            $"left heading two four zero, maintain three thousand, cleared ILS two eight right, {Cs}.",
            $"left heading two four zero, descending three thousand, cleared ILS two eight right, {Cs}."
        );
        AssertTerminal(readbacks, "turn left heading 240, descend and maintain 3000, cleared ILS runway 28R approach");
    }

    // --- First block, climb shape with no direction ---

    [Fact]
    public void FlyHeadingClimb_Quiet_SpeaksTheCanonicalOrTheCourtesyRow() =>
        AssertSpoken(
            FirstBlockReadbacks("PTAC 240 050 ILS28R", FrequencyLoadBand.Quiet, 3000).Select(r => r.Tts),
            $"{ClimbFly}, {Cs}.",
            $"{ClimbFly}, {Cs}, thanks."
        );

    [Fact]
    public void FlyHeadingClimb_Normal_SpeaksTheCanonicalOrDropsFly() =>
        AssertSpoken(
            FirstBlockReadbacks("PTAC 240 050 ILS28R", FrequencyLoadBand.Normal, 3000).Select(r => r.Tts),
            $"{ClimbFly}, {Cs}.",
            $"heading two four zero, climb and maintain five thousand, cleared ILS runway two eight right approach, {Cs}."
        );

    [Fact]
    public void FlyHeadingClimb_Busy_KeepsHeadingAltitudeTypeAndRunway()
    {
        List<PilotSpeechText> readbacks = FirstBlockReadbacks("PTAC 240 050 ILS28R", FrequencyLoadBand.Busy, 3000);

        AssertSpoken(
            readbacks.Select(r => r.Tts),
            $"heading two four zero, maintain five thousand, cleared ILS runway two eight right, {Cs}.",
            $"heading two four zero, maintain five thousand, cleared ILS two eight right, {Cs}.",
            $"heading two four zero, climbing five thousand, cleared ILS two eight right, {Cs}."
        );
        AssertTerminal(readbacks, "fly heading 240, climb and maintain 5000, cleared ILS runway 28R approach");
    }

    // --- First block, maintain shape: no progressive row ---

    [Fact]
    public void TurnMaintain_Busy_HasNoProgressiveRow() =>
        AssertSpoken(
            FirstBlockReadbacks("PTAC R240 030 ILS28R", FrequencyLoadBand.Busy, 3000).Select(r => r.Tts),
            $"right heading two four zero, maintain three thousand, cleared ILS runway two eight right, {Cs}.",
            $"right heading two four zero, maintain three thousand, cleared ILS two eight right, {Cs}."
        );

    /// <summary>A first-block PA altitude is the one the aircraft keeps, read back "maintain": the maintain shape's rows.</summary>
    [Fact]
    public void FirstBlockPresentAltitude_Busy_SpeaksTheMaintainRows() =>
        AssertSpoken(
            FirstBlockReadbacks("PTAC 240 PA ILS28R", FrequencyLoadBand.Busy, 3000).Select(r => r.Tts),
            $"heading two four zero, maintain three thousand, cleared ILS runway two eight right, {Cs}.",
            $"heading two four zero, maintain three thousand, cleared ILS two eight right, {Cs}."
        );

    // --- Canonical in every band ---

    [Theory]
    [InlineData(FrequencyLoadBand.Normal)]
    [InlineData(FrequencyLoadBand.Busy)]
    public void PresentHeading_IsCanonical(FrequencyLoadBand band) =>
        AssertSpoken(
            FirstBlockReadbacks("PTAC PH 030 ILS28R", band, 5000).Select(r => r.Tts),
            $"fly present heading, descend and maintain three thousand, cleared ILS runway two eight right approach, {Cs}."
        );

    /// <summary>A present-heading PTAC has no Quiet rows: a quiet pilot speaks the canonical, which the flavour may close with thanks.</summary>
    [Fact]
    public void PresentHeading_Quiet_IsCanonicalOrItsFlavour()
    {
        const string canonical = "fly present heading, descend and maintain three thousand, cleared ILS runway two eight right approach";
        HashSet<string> spoken = [.. FirstBlockReadbacks("PTAC PH 030 ILS28R", FrequencyLoadBand.Quiet, 5000).Select(r => r.Tts)];

        Assert.Subset(new HashSet<string> { $"{canonical}, {Cs}.", $"{canonical}, thanks, {Cs}." }, spoken);
        Assert.Contains($"{canonical}, {Cs}.", spoken);
    }

    [Fact]
    public void PresentHeading_TheHandlerCarriesNoSet()
    {
        CommandResult result = CommandDispatcher.Dispatch(
            CommandParser.Parse("PTAC PH 030 ILS28R").Value!,
            MakeArrival(5000),
            TestDispatch.Context(new Random(1))
        );

        HandlerReadback readback = Assert.IsType<HandlerReadback>(result.PilotReadback);
        Assert.Null(readback.Variants);
        Assert.Null(readback.Captures);
    }

    [Theory]
    [InlineData("DM 30; PTAC 240 PA ILS28R", "fly heading two four zero, cleared ILS runway two eight right approach")]
    [InlineData("DM 30; PTAC PH 030 ILS28R", "fly present heading, descend and maintain three thousand, cleared ILS runway two eight right approach")]
    [InlineData("DM 30; PTAC 240 030 Z28R", "fly heading two four zero, descend and maintain three thousand, cleared approach")]
    public void LaterBlock_NoAltitudePresentHeadingOrUnnamed_IsCanonicalInNormalAndBusy(string text, string canonical)
    {
        foreach (FrequencyLoadBand band in new[] { FrequencyLoadBand.Normal, FrequencyLoadBand.Busy })
        {
            AssertSpoken(UndispatchedReadbacks(text, band, 5000).Select(r => LaterClause(r.Tts)), $"{canonical}, {Cs}.");
        }
    }

    [Theory]
    [InlineData("PTAC PH 030 ILS28R")]
    [InlineData("PTAC 240 PA ILS28R")]
    [InlineData("PTAC L240 PA ILS28R")]
    [InlineData("PTAC 240 030 Z28R")]
    [InlineData("PTAC 240 030 VOR-A")]
    public void LaterBlock_CanonicalShapes_HaveNoVariantSet(string text) =>
        Assert.Null(PilotResponder.VariedBuilderClause(CommandParser.Parse(text).Value!, MakeArrival(5000)));

    // --- Later block ---

    [Fact]
    public void LaterBlock_TurnDescend_Busy_Varies()
    {
        List<PilotSpeechText> readbacks = UndispatchedReadbacks("DM 30; PTAC R240 030 ILS28R", FrequencyLoadBand.Busy, 5000);

        AssertSpoken(
            readbacks.Select(r => LaterClause(r.Tts)),
            $"right heading two four zero, maintain three thousand, cleared ILS runway two eight right, {Cs}.",
            $"right heading two four zero, maintain three thousand, cleared ILS two eight right, {Cs}.",
            $"right heading two four zero, descending three thousand, cleared ILS two eight right, {Cs}."
        );
        Assert.Single(readbacks.Select(r => r.Terminal).Distinct());
    }

    // --- The varied clause is the whole canonical ---

    [Theory]
    [InlineData("PTAC L240 050 ILS28R", 3000, "PTAC turn heading climb")]
    [InlineData("PTAC L240 030 ILS28R", 5000, "PTAC turn heading descend")]
    [InlineData("PTACF R240 030 ILS28R", 3000, "PTAC turn heading maintain")]
    [InlineData("PTAC 240 050 ILS28R", 3000, "PTAC fly heading climb")]
    [InlineData("PTACF 240 030 ILS28R", 5000, "PTAC fly heading descend")]
    [InlineData("PTAC 240 030 ILS28R", 3000, "PTAC fly heading maintain")]
    [InlineData("PTAC 240 PA ILS28R", 5000, "PTAC fly heading maintain")]
    public void FirstBlock_ThePatternIsTheHandlersCanonical(string text, double altitude, string setName)
    {
        CommandResult result = CommandDispatcher.Dispatch(
            CommandParser.Parse(text).Value!,
            MakeArrival(altitude),
            TestDispatch.Context(new Random(1))
        );

        HandlerReadback readback = Assert.IsType<HandlerReadback>(result.PilotReadback);
        Assert.Equal(setName, readback.Variants?.Name);
        Assert.NotNull(readback.Captures);
        Assert.Equal(PhraseologyVerbalizer.RenderSpokenVariant(string.Join(' ', readback.Variants!.Pattern), readback.Captures), readback.Speech.Tts);
    }

    [Theory]
    [InlineData("PTAC R240 070 ILS28R", "PTAC turn heading climb")]
    [InlineData("PTAC R240 030 ILS28R", "PTAC turn heading descend")]
    [InlineData("PTAC 240 050 RNAV28RZ", "PTAC fly heading maintain")]
    public void LaterBlock_ThePatternIsTheBuildersCanonical(string text, string setName)
    {
        PilotResponder.BuilderClause clause = PilotResponder.VariedBuilderClause(CommandParser.Parse(text).Value!, MakeArrival(5000))!.Value;

        Assert.Equal(setName, clause.Variants.Name);
        Assert.NotNull(clause.Captures);
        Assert.Equal(PhraseologyVerbalizer.RenderSpokenVariant(string.Join(' ', clause.Variants.Pattern), clause.Captures), clause.Canonical.Tts);
    }

    // --- The selector ---

    public static TheoryData<ApproachCommandHandler.PtacVertical, bool> EveryVerticalAndHeadingKind()
    {
        var data = new TheoryData<ApproachCommandHandler.PtacVertical, bool>();
        foreach (ApproachCommandHandler.PtacVertical vertical in Enum.GetValues<ApproachCommandHandler.PtacVertical>())
        {
            data.Add(vertical, true);
            data.Add(vertical, false);
        }

        return data;
    }

    /// <summary>Every vertical shape, with a turn direction and without, has a set whose pattern renders the canonical's words exactly.</summary>
    [Theory]
    [MemberData(nameof(EveryVerticalAndHeadingKind))]
    public void PtacVariants_EveryVerticalAndHeadingKind_PatternRendersTheCanonical(ApproachCommandHandler.PtacVertical vertical, bool withDirection)
    {
        ApproachCommandHandler.PtacAltitude altitude = vertical switch
        {
            ApproachCommandHandler.PtacVertical.Climb => new ApproachCommandHandler.PtacAltitude(5000, 3000),
            ApproachCommandHandler.PtacVertical.Descend => new ApproachCommandHandler.PtacAltitude(3000, 5000),
            ApproachCommandHandler.PtacVertical.Maintain => new ApproachCommandHandler.PtacAltitude(3000, 3000),
            _ => throw new ArgumentOutOfRangeException(nameof(vertical), vertical, "No altitude shape for this vertical."),
        };
        Assert.Equal(vertical, altitude.Vertical());
        TurnDirection? direction = withDirection ? TurnDirection.Left : null;
        var heading = new MagneticHeading(240);

        (BuilderVariantSet set, IReadOnlyDictionary<string, string> captures) = ApproachCommandHandler
            .PtacVariants(direction, heading, altitude, ApproachReadbackName.SpokenParts("ILS28R"))!
            .Value;
        string canonical = ApproachCommandHandler.PtacReadback(heading, direction, altitude, ApproachReadbackName.ForShorthand("ILS28R")).Tts;

        Assert.Equal(canonical, PhraseologyVerbalizer.RenderSpokenVariant(string.Join(' ', set.Pattern), captures));
    }

    [Theory]
    [InlineData("no heading")]
    [InlineData("no altitude")]
    [InlineData("no runway")]
    [InlineData("no parts")]
    public void PtacVariants_CanonicalShapes_AreNull(string shape)
    {
        MagneticHeading? heading = (shape == "no heading") ? null : new MagneticHeading(240);
        ApproachCommandHandler.PtacAltitude? altitude = (shape == "no altitude") ? null : new ApproachCommandHandler.PtacAltitude(3000, 5000);
        (string Approach, string? Runway)? parts = shape switch
        {
            "no runway" => ("VOR Alpha", null),
            "no parts" => null,
            _ => ("ILS", "two eight right"),
        };

        Assert.Null(ApproachCommandHandler.PtacVariants(TurnDirection.Left, heading, altitude, parts));
    }

    /// <summary>
    /// No PTAC row speaks a refused form: "down to" or "up to" ("to" only before a flight level), "alright" before a
    /// clearance, or a bare altitude with no verb before it.
    /// </summary>
    [Fact]
    public void PtacSets_NoRowSpeaksARefusedForm()
    {
        BuilderVariantSet[] sets =
        [
            PhraseologyRules.PtacTurnClimb,
            PhraseologyRules.PtacTurnDescend,
            PhraseologyRules.PtacTurnMaintain,
            PhraseologyRules.PtacFlyHeadingClimb,
            PhraseologyRules.PtacFlyHeadingDescend,
            PhraseologyRules.PtacFlyHeadingMaintain,
        ];
        string[] refusedPhrases = ["down to", "up to", "alright"];
        string[] altitudeVerbs = ["maintain", "climbing", "descending"];
        List<string> refused = [];
        foreach (BuilderVariantSet set in sets)
        {
            foreach (PilotVariant row in set.Variants.Quiet.Concat(set.Variants.Normal).Concat(set.Variants.Busy))
            {
                refused.AddRange(
                    refusedPhrases
                        .Where(phrase => row.Template.Contains(phrase, StringComparison.Ordinal))
                        .Select(phrase => $"{set.Name}: \"{row.Template}\" ({phrase})")
                );
                string[] words = [.. row.Template.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => word.TrimEnd(',', '.'))];
                for (int i = 0; i < words.Length; i++)
                {
                    if (words[i].StartsWith("{alt", StringComparison.Ordinal) && ((i == 0) || !altitudeVerbs.Contains(words[i - 1])))
                    {
                        refused.Add($"{set.Name}: \"{row.Template}\" (altitude with no verb before it)");
                    }
                }
            }
        }

        Assert.Empty(refused);
    }
}
