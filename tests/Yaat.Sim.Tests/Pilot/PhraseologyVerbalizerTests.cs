using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Speech;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

public class PhraseologyVerbalizerTests
{
    private const string Callsign = "N123AB";

    private static PilotSpeechContext Varied(FrequencyLoadBand band, int rngSeed, int second) =>
        new(PilotPersonality.Varied, band, rngSeed, second)
        {
            ShortenCallsign = false,
            GroupContactFrequency = false,
            CurrentIndicatedAirspeed = null,
        };

    public PhraseologyVerbalizerTests()
    {
        // CrossFix readback walks SpellFix → NavigationDatabase to resolve fix pronunciations
        // and navaid names. Pin the singleton up-front so the class doesn't race against any
        // other test class also populating it.
        TestVnasData.EnsureInitialized();
    }

    // --- Altitude ---

    [Fact]
    public void Verbalize_ClimbMaintain_ReadsbackVerbatimWithSpokenAltitude()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new ClimbMaintainCommand(5000));
        Assert.Equal("climb and maintain five thousand", result);
    }

    [Fact]
    public void Verbalize_DescendMaintain_ReadsbackVerbatimWithSpokenAltitude()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new DescendMaintainCommand(3500));
        Assert.Equal("descend and maintain three thousand five hundred", result);
    }

    [Fact]
    public void Verbalize_ClimbMaintain_FlightLevel()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new ClimbMaintainCommand(33000));
        Assert.Equal("climb and maintain flight level three three zero", result);
    }

    [Fact]
    public void Verbalize_VariedNormal_KeepsVerbatim()
    {
        string? result = PhraseologyVerbalizer
            .VerbalizeClause(new ClimbMaintainCommand(5000), Callsign, Varied(FrequencyLoadBand.Normal, 1, 0), wholeReadback: true)
            .Text;

        Assert.Equal("climb and maintain five thousand", result);
    }

    [Theory]
    [InlineData(FrequencyLoadBand.Busy)]
    public void Verbalize_VariedBusy_ToRowsAreFlightLevelOnly(FrequencyLoadBand band)
    {
        string? belowFl180 = PhraseologyVerbalizer
            .VerbalizeClause(new ClimbMaintainCommand(5000), Callsign, Varied(band, 1, 0), wholeReadback: true)
            .Text;
        string? flightLevel = PhraseologyVerbalizer
            .VerbalizeClause(new ClimbMaintainCommand(19000), Callsign, Varied(band, 1, 0), wholeReadback: true)
            .Text;

        Assert.Contains(belowFl180, (string[])["five thousand", "climbing five thousand"]);
        Assert.Contains(
            flightLevel,
            (string[])
                [
                    "climbing to flight level one nine zero",
                    "up to flight level one nine zero",
                    "flight level one nine zero",
                    "climbing flight level one nine zero",
                ]
        );
    }

    // --- Heading ---

    [Fact]
    public void Verbalize_FlyHeading_ThreeDigitForm()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new FlyHeadingCommand(new MagneticHeading(270)));
        Assert.Equal("fly heading two seven zero", result);
    }

    [Fact]
    public void Verbalize_TurnLeft_ThreeDigitForm()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new TurnLeftCommand(new MagneticHeading(90)));
        Assert.Equal("turn left heading zero nine zero", result);
    }

    [Fact]
    public void Verbalize_TurnRight_ZeroPaddedDigits()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new TurnRightCommand(new MagneticHeading(5)));
        Assert.Equal("turn right heading zero zero five", result);
    }

    [Fact]
    public void Verbalize_VariedBusy_SpeaksTheHeadingBusyRow()
    {
        string? result = PhraseologyVerbalizer
            .VerbalizeClause(new TurnLeftCommand(new MagneticHeading(90)), Callsign, Varied(FrequencyLoadBand.Busy, 1, 0), wholeReadback: true)
            .Text;

        Assert.Equal("left heading zero nine zero", result);
    }

    [Fact]
    public void Verbalize_RelativeLeft_TensWordsForm()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new LeftTurnCommand(30));
        Assert.Equal("turn thirty degrees left", result);
    }

    [Fact]
    public void Verbalize_VariedBusy_RelativeTurnShortcutKeepsDegrees()
    {
        string? result = PhraseologyVerbalizer
            .VerbalizeClause(new LeftTurnCommand(30), Callsign, Varied(FrequencyLoadBand.Busy, 1, 0), wholeReadback: true)
            .Text;

        Assert.Equal("thirty degrees left", result);
    }

    /// <summary>
    /// Every rule with busy variant rows speaks one of them at Varied/Busy, never the canonical form. CM and DM use flight
    /// levels, since their rows are flight-level only. One row per
    /// <c>PilotVariants</c> rule in <c>PhraseologyRules</c>; <paramref name="expected"/> lists the allowed texts, split on '|'.
    /// </summary>
    [Theory]
    [InlineData("TL", "left heading zero nine zero")]
    [InlineData("TR", "right heading two seven zero")]
    [InlineData("RELL", "thirty degrees left")]
    [InlineData("RELR", "thirty degrees right")]
    [InlineData("FH", "heading two seven zero")]
    [InlineData("FPH", "present heading")]
    [InlineData(
        "CM",
        "climbing to flight level one nine zero|up to flight level one nine zero|flight level one nine zero|climbing flight level one nine zero"
    )]
    [InlineData(
        "DM",
        "descending to flight level two four zero|down to flight level two four zero|flight level two four zero|descending flight level two four zero"
    )]
    [InlineData("SPD", "two five zero knots|speed two five zero|two fifty knots|speed two fifty")]
    [InlineData("MACH", "mach point seven eight")]
    [InlineData("DCT", "direct {fix}")]
    [InlineData("TLDCT", "left direct {fix}")]
    [InlineData("TRDCT", "right direct {fix}")]
    [InlineData("ADCT", "when able direct {fix}")]
    [InlineData("TAXI", "taxi runway one left")]
    [InlineData("RNS", "wilco")]
    [InlineData("RPS", "wilco")]
    [InlineData("RFAS", "wilco")]
    public void Verbalize_VariedBusy_EveryShortcutRuleSpeaksABusyRow(string rule, string expected)
    {
        List<ResolvedFix> fixes = [new ResolvedFix("SUNOL", 37.6, -121.9)];
        ParsedCommand command = rule switch
        {
            "TL" => new TurnLeftCommand(new MagneticHeading(90)),
            "TR" => new TurnRightCommand(new MagneticHeading(270)),
            "RELL" => new LeftTurnCommand(30),
            "RELR" => new RightTurnCommand(30),
            "FH" => new FlyHeadingCommand(new MagneticHeading(270)),
            "FPH" => new FlyPresentHeadingCommand(),
            "MACH" => new MachCommand(0.78),
            "CM" => new ClimbMaintainCommand(19000),
            "DM" => new DescendMaintainCommand(24000),
            "SPD" => new SpeedCommand(250),
            "DCT" => new DirectToCommand(fixes, []),
            "TLDCT" => new TurnLeftDirectToCommand(fixes, []),
            "TRDCT" => new TurnRightDirectToCommand(fixes, []),
            "ADCT" => new AppendDirectToCommand(fixes, []),
            "TAXI" => new TaxiCommand([], [], Expedite: false, "1L"),
            "RNS" => new ResumeNormalSpeedCommand(),
            "RPS" => new ResumePublishedSpeedCommand(),
            "RFAS" => new ReduceToFinalApproachSpeedCommand(),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "No representative command for this rule."),
        };

        string? busy = PhraseologyVerbalizer.VerbalizeClause(command, Callsign, Varied(FrequencyLoadBand.Busy, 1, 0), wholeReadback: true).Text;

        string[] allowed = expected.Replace("{fix}", PhraseologyVerbalizer.SpellFixSequence(fixes), StringComparison.Ordinal).Split('|');
        string? canonical = PhraseologyVerbalizer.Verbalize(command);
        Assert.Contains(busy, allowed);
        Assert.NotEqual(canonical, busy);
    }

    [Fact]
    public void PhraseologyRules_ShortcutRuleCount_MatchesTheWiringTheory()
    {
        int shortcutRules = PhraseologyRules.All.Count(rule =>
            rule.PilotVariants is { } variants && (variants.Quiet.Count + variants.Normal.Count + variants.Busy.Count > 0)
        );

        Assert.True(
            shortcutRules == 18,
            $"PhraseologyRules has {shortcutRules} rules with PilotVariants rows, the wiring theory covers 18: add a row to "
                + $"{nameof(Verbalize_VariedBusy_EveryShortcutRuleSpeaksABusyRow)} for each new rule and update this count."
        );
    }

    [Theory]
    [InlineData("CM")]
    [InlineData("DM")]
    public void Verbalize_VariedBusy_BelowFl180_NeverSaysToBeforeTheAltitude(string rule)
    {
        ParsedCommand command = rule == "CM" ? new ClimbMaintainCommand(5000) : new DescendMaintainCommand(5000);

        for (int second = 0; second < 200; second++)
        {
            string? spoken = PhraseologyVerbalizer
                .VerbalizeClause(command, Callsign, Varied(FrequencyLoadBand.Busy, 7, second), wholeReadback: true)
                .Text;

            Assert.NotNull(spoken);
            int altitudeAt = spoken.IndexOf("five thousand", StringComparison.Ordinal);
            Assert.True(altitudeAt >= 0, $"Second {second}: \"{spoken}\" does not speak the altitude.");
            Assert.DoesNotContain(" to ", " " + spoken[..altitudeAt], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Verbalize_VariedBusy_FlightLevel_MayUseUpTo()
    {
        string flightLevel = PhraseologyVerbalizer.AltitudeWords(19000);
        string[] toRows = [$"up to {flightLevel}", $"climbing to {flightLevel}"];
        Assert.StartsWith("flight level ", flightLevel);

        bool spokeToRow = Enumerable
            .Range(0, 200)
            .Select(second =>
                PhraseologyVerbalizer
                    .VerbalizeClause(new ClimbMaintainCommand(19000), Callsign, Varied(FrequencyLoadBand.Busy, 7, second), wholeReadback: true)
                    .Text
            )
            .Any(spoken => toRows.Contains(spoken));

        Assert.True(spokeToRow, "No second in 0-199 spoke a flight-level \"to\" row for CM 19000 at Busy.");
    }

    /// <summary>
    /// The pick is a pure function of callsign, canonical clause, second and seed; and over many seconds it reaches more than
    /// one row. FH has a single busy row, so the spread is shown on SPD, which has two. One SPD pick is pinned, so a change
    /// to the pick's inputs (its salt) shows up here.
    /// </summary>
    [Fact]
    public void Verbalize_VariedBusy_PickIsDeterministic()
    {
        var heading = new FlyHeadingCommand(new MagneticHeading(270));
        for (int second = 0; second < 200; second++)
        {
            PilotSpeechContext context = Varied(FrequencyLoadBand.Busy, 7, second);
            Assert.Equal(
                PhraseologyVerbalizer.VerbalizeClause(heading, Callsign, context, wholeReadback: true).Text,
                PhraseologyVerbalizer.VerbalizeClause(heading, Callsign, context, wholeReadback: true).Text
            );
        }

        int distinctSpeedRows = Enumerable
            .Range(0, 200)
            .Select(second =>
                PhraseologyVerbalizer
                    .VerbalizeClause(new SpeedCommand(250), Callsign, Varied(FrequencyLoadBand.Busy, 7, second), wholeReadback: true)
                    .Text
            )
            .Distinct()
            .Count();

        Assert.True(distinctSpeedRows >= 2, $"SPD 250 at Busy spoke {distinctSpeedRows} distinct row(s) over seconds 0-199.");
        Assert.Equal(
            "speed two fifty",
            PhraseologyVerbalizer.VerbalizeClause(new SpeedCommand(250), Callsign, Varied(FrequencyLoadBand.Busy, 7, 0), wholeReadback: true).Text
        );
    }

    [Fact]
    public void PhraseologyRules_FlightLevelOnlyRows_SitOnClimbOrDescendRules()
    {
        string[] misplaced =
        [
            .. PhraseologyRules
                .All.Where(rule => rule.PilotVariants is not null)
                .Where(rule => rule.Type is not (CanonicalCommandType.ClimbMaintain or CanonicalCommandType.DescendMaintain))
                .SelectMany(rule =>
                    new[] { rule.PilotVariants!.Quiet, rule.PilotVariants.Normal, rule.PilotVariants.Busy }
                        .SelectMany(rows => rows)
                        .Where(variant => variant.Tags.HasFlag(PilotVariantTags.FlightLevelOnly))
                        .Select(variant => $"{rule.OutputTemplate}: \"{variant.Template}\"")
                ),
        ];

        Assert.True(misplaced.Length == 0, $"FlightLevelOnly rows on a rule with no altitude to test: {string.Join(", ", misplaced)}");
    }

    [Fact]
    public void PhraseologyRules_EveryVariant_HoldsTheFloor()
    {
        FrequencyLoadBand[] bands = [FrequencyLoadBand.Quiet, FrequencyLoadBand.Normal, FrequencyLoadBand.Busy];
        List<string> failures =
        [
            .. PhraseologyRules
                .All.Where(rule => rule.PilotVariants is not null)
                .SelectMany(rule =>
                    bands.SelectMany(band =>
                        PhraseologyVerbalizer
                            .BandRows(rule.PilotVariants!, band)
                            .Where(variant => !AviationFloor.HoldsTemplate(rule.Pattern, rule.Type, variant, band))
                            .Select(variant => $"{rule.OutputTemplate} {band}: \"{variant.Template}\" against \"{string.Join(' ', rule.Pattern)}\"")
                    )
                ),
            .. PhraseologyRules.BuilderVariantSets.SelectMany(set =>
                bands.SelectMany(band =>
                    PhraseologyVerbalizer
                        .BandRows(set.Variants, band)
                        .Where(variant => !AviationFloor.HoldsTemplate(set.Pattern, set.Type, variant, band))
                        .Select(variant => $"{set.Name} {band}: \"{variant.Template}\" against \"{string.Join(' ', set.Pattern)}\"")
                )
            ),
        ];

        Assert.True(
            failures.Count == 0,
            $"Variant rows that break the aviation floor:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}"
        );
    }

    [Fact]
    public void RenderSpokenVariant_CommaLedCapture_AttachesWithoutASpace() =>
        Assert.Equal(
            "cleared visual runway two eight right, left traffic, follow the traffic",
            PhraseologyVerbalizer.RenderSpokenVariant(
                "cleared visual runway {rwy} {side} {follow}",
                new Dictionary<string, string>
                {
                    ["rwy"] = "two eight right",
                    ["side"] = ", left traffic",
                    ["follow"] = ", follow the traffic",
                }
            )
        );

    [Fact]
    public void RenderSpokenVariant_EmptyCaptureBeforeAComma_KeepsTheComma() =>
        Assert.Equal(
            "cleared visual approach runway two eight right, {cs}, thanks",
            PhraseologyVerbalizer.RenderSpokenVariant(
                "cleared visual approach runway {rwy} {side} {follow}, {cs}, thanks",
                new Dictionary<string, string>
                {
                    ["rwy"] = "two eight right",
                    ["side"] = "",
                    ["follow"] = "",
                }
            )
        );

    [Fact]
    public void RenderSpokenVariant_PunctuatedCaptureToken_KeepsItsComma() =>
        Assert.Equal(
            "cleared visual approach to petaluma, landing runway two nine",
            PhraseologyVerbalizer.RenderSpokenVariant(
                "cleared visual approach to {apt}, landing runway {rwy} {side}",
                new Dictionary<string, string>
                {
                    ["apt"] = "petaluma",
                    ["rwy"] = "two nine",
                    ["side"] = "",
                }
            )
        );

    [Theory]
    [InlineData(4000)]
    [InlineData(19000)]
    public void SpokenCandidates_VariedBusy_GroupRowsNeverRepeatTheDigitWording(int feet)
    {
        IReadOnlyList<string> candidates = PhraseologyVerbalizer.SpokenCandidates(
            new ClimbMaintainCommand(feet),
            Aircraft(),
            FrequencyLoadBand.Busy,
            null
        );

        Assert.NotEmpty(candidates);
        Assert.Equal(candidates.Count, candidates.Distinct().Count());
    }

    [Fact]
    public void SpokenCandidates_VariedBusy_GroupFormRangeOffersTheGroupForm() =>
        Assert.Contains(
            "eleven thousand",
            PhraseologyVerbalizer.SpokenCandidates(new ClimbMaintainCommand(11000), Aircraft(), FrequencyLoadBand.Busy, null)
        );

    private static AircraftState Aircraft() => new() { Callsign = "N123AB", AircraftType = "B738" };

    [Fact]
    public void PhraseologyRules_GroupForm_SitsOnBusyRowsOnly()
    {
        string[] misplaced =
        [
            .. PhraseologyRules
                .All.Where(rule => rule.PilotVariants is not null)
                .SelectMany(rule =>
                    rule.PilotVariants!.Quiet.Concat(rule.PilotVariants.Normal)
                        .Where(variant => variant.Template.Contains(PilotVariant.GroupFormSuffix, StringComparison.Ordinal))
                        .Select(variant => $"{rule.OutputTemplate}: \"{variant.Template}\"")
                ),
        ];

        Assert.True(misplaced.Length == 0, $"Group-form rows outside the busy band: {string.Join(", ", misplaced)}");
    }

    [Fact]
    public void PhraseologyRules_BusySpeedRows_KeepKnotsOrSpeed()
    {
        string[] bare =
        [
            .. PhraseologyRules
                .All.Where(rule => (rule.PilotVariants is not null) && rule.Pattern.Contains("{spd}"))
                .SelectMany(rule =>
                    rule.PilotVariants!.Busy.Where(variant => !variant.Template.Split(' ').Any(word => word is "knots" or "speed"))
                        .Select(variant => $"{rule.OutputTemplate}: \"{variant.Template}\"")
                ),
        ];

        Assert.True(bare.Length == 0, $"Busy speed rows with neither \"knots\" nor \"speed\": {string.Join(", ", bare)}");
    }

    [Fact]
    public void Verbalize_VariedQuiet_ClimbMaySpeakTheCatalogueCourtesyRow()
    {
        const string courtesy = "alright, we'll climb and maintain five thousand, {cs}, thanks";

        // Seed 7: the first second in 0-199 whose pick is the courtesy row.
        int first = Enumerable
            .Range(0, 200)
            .First(second =>
                PhraseologyVerbalizer
                    .VerbalizeClause(new ClimbMaintainCommand(5000), Callsign, Varied(FrequencyLoadBand.Quiet, 7, second), wholeReadback: true)
                    .Text == courtesy
            );

        Assert.Equal(3, first);
    }

    [Fact]
    public void PhraseologyRules_QuietRows_NeverSpeakTheGroupForm()
    {
        string[] misplaced =
        [
            .. PhraseologyRules
                .All.Where(rule => rule.PilotVariants is not null)
                .SelectMany(rule =>
                    rule.PilotVariants!.Quiet.Where(variant => variant.Template.Contains(":group", StringComparison.Ordinal))
                        .Select(variant => $"{rule.OutputTemplate}: \"{variant.Template}\"")
                ),
        ];

        Assert.True(misplaced.Length == 0, $"Quiet rows with a group-form capture: {string.Join(", ", misplaced)}");
    }

    [Fact]
    public void Verbalize_VariedBusy_ClimbBelowFl180_MaySpeakTheGroupForm() =>
        // Second 3 at seed 7 is the first second in 0-199 that picks the bare group-form row.
        Assert.Equal(
            "eleven thousand",
            PhraseologyVerbalizer
                .VerbalizeClause(new ClimbMaintainCommand(11000), Callsign, Varied(FrequencyLoadBand.Busy, 7, 3), wholeReadback: true)
                .Text
        );

    [Fact]
    public void Verbalize_RelativeRight_TensWordsForm()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new RightTurnCommand(20));
        Assert.Equal("turn twenty degrees right", result);
    }

    [Theory]
    [InlineData(45, "forty five")]
    [InlineData(270, "two seventy")]
    public void DegreesWords_RelativeTurnsUseGroupedForm(int degrees, string expected) =>
        Assert.Equal(expected, PhraseologyVerbalizer.DegreesWords(degrees));

    // --- Speed ---

    [Fact]
    public void SpeedWords_SpeaksSeparateDigits() => Assert.Equal("two five zero", PhraseologyVerbalizer.SpeedWords(250));

    [Theory]
    [InlineData(250, "two fifty")]
    [InlineData(200, "two hundred")]
    [InlineData(213, "two one three")]
    public void SpeedGroupWords_RoundTensUseTheGroupForm(int knots, string expected) =>
        Assert.Equal(expected, PhraseologyVerbalizer.SpeedGroupWords(knots));

    [Theory]
    [InlineData(4500, "four thousand five hundred")]
    [InlineData(10000, "ten thousand")]
    [InlineData(11000, "eleven thousand")]
    [InlineData(17900, "seventeen thousand nine hundred")]
    public void AltitudeGroupWords_SaysTheThousandsAsOneNumber(int feet, string expected) =>
        Assert.Equal(expected, PhraseologyVerbalizer.AltitudeGroupWords(feet));

    [Fact]
    public void RenderSpokenVariant_GroupPlaceholders_SpeakTheGroupFormOfTheSameValue()
    {
        Assert.Equal("eleven thousand", PhraseologyVerbalizer.RenderSpokenVariant(new ClimbMaintainCommand(11000), "{alt:group}"));
        Assert.Equal(
            PhraseologyVerbalizer.AltitudeWords(19000),
            PhraseologyVerbalizer.RenderSpokenVariant(new ClimbMaintainCommand(19000), "{alt:group}")
        );
        Assert.Equal("speed two fifty", PhraseologyVerbalizer.RenderSpokenVariant(new SpeedCommand(250), "speed {spd:group}"));
    }

    [Theory]
    [InlineData(18000)]
    [InlineData(33000)]
    public void AltitudeGroupWords_FlightLevel_IsTheDigitForm(int feet) =>
        Assert.Equal(PhraseologyVerbalizer.AltitudeWords(feet), PhraseologyVerbalizer.AltitudeGroupWords(feet));

    [Fact]
    public void Verbalize_Speed_SpellsKnotsLiteral()
    {
        // Rule pattern is ["maintain", "{spd}", "knots"] (first declared in AltitudeSpeedRules); the canonical speaks the
        // speed in separate digits (AIM 4-2-11).
        string? result = PhraseologyVerbalizer.Verbalize(new SpeedCommand(250));
        Assert.Equal("maintain two five zero knots", result);
    }

    [Fact]
    public void Verbalize_VariedBusy_SpeedShortcutKeepsKnots()
    {
        string? result = PhraseologyVerbalizer
            .VerbalizeClause(new SpeedCommand(250), Callsign, Varied(FrequencyLoadBand.Busy, 1, 0), wholeReadback: true)
            .Text;

        Assert.Contains(result, (string[])["two five zero knots", "speed two five zero", "two fifty knots", "speed two fifty"]);
    }

    // --- Transponder ---

    [Fact]
    public void Verbalize_Squawk_FourDigitsSpoken()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new SquawkCommand(1234));
        Assert.Equal("squawk one two three four", result);
    }

    [Fact]
    public void Verbalize_Squawk_PadsTo4DigitsForLowCodes()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new SquawkCommand(56));
        Assert.Equal("squawk zero zero five six", result);
    }

    [Fact]
    public void Verbalize_Ident_LiteralFromRule()
    {
        // The rule is ["squawk", "ident"] (it's filed under transponder phraseology).
        string? result = PhraseologyVerbalizer.Verbalize(new IdentCommand());
        Assert.Equal("squawk ident", result);
    }

    [Fact]
    public void Verbalize_VariedBusy_NoShortcutFallsBackToVerbatim()
    {
        string? result = PhraseologyVerbalizer
            .VerbalizeClause(new IdentCommand(), Callsign, Varied(FrequencyLoadBand.Busy, 1, 0), wholeReadback: true)
            .Text;

        Assert.Equal("squawk ident", result);
    }

    // --- Tower (no captures filled — fewest-captures rule wins) ---

    [Fact]
    public void Verbalize_LineUpAndWait_PicksRuleWithoutRunway()
    {
        // LineUpAndWaitCommand carries no runway. Verbalizer prefers the "line up and wait"
        // rule over "line up and wait runway {rwy}" because it has zero captures.
        string? result = PhraseologyVerbalizer.Verbalize(new LineUpAndWaitCommand());
        Assert.Equal("line up and wait", result);
    }

    [Fact]
    public void Verbalize_ClearedForTakeoff_PicksRuleWithoutRunway()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new ClearedForTakeoffCommand(new DefaultDeparture()));
        Assert.Equal("cleared for takeoff", result);
    }

    [Fact]
    public void Verbalize_ClearedToLand_PicksRuleWithoutRunway()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new ClearedToLandCommand());
        Assert.Equal("cleared to land", result);
    }

    // --- Unknown / unverbalizable ---

    [Fact]
    public void Verbalize_UnsupportedCommand_ReturnsNull()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new UnsupportedCommand("ZZZ 999"));
        Assert.Null(result);
    }

    // --- Round-trip: verbalized output should re-parse via PhraseologyMapper ---

    [Fact]
    public void Verbalize_ClimbMaintain_RoundTripsViaPhraseologyMapper()
    {
        string verbalized = PhraseologyVerbalizer.Verbalize(new ClimbMaintainCommand(5000))!;
        string normalized = Yaat.Sim.Speech.AtcNumberParser.NormalizeDigits(verbalized);
        Assert.Equal("climb and maintain 5000", normalized);
    }

    [Fact]
    public void Verbalize_FlyHeading_RoundTripsViaPhraseologyMapper()
    {
        string verbalized = PhraseologyVerbalizer.Verbalize(new FlyHeadingCommand(new MagneticHeading(270)))!;
        string normalized = Yaat.Sim.Speech.AtcNumberParser.NormalizeDigits(verbalized);
        Assert.Equal("fly heading 270", normalized);
    }

    // --- Helpers exposed for AtParkingPhase + responder ---

    [Fact]
    public void HeadingDigits_Wraps360ToZero() => Assert.Equal("zero zero zero", PhraseologyVerbalizer.HeadingDigits(new MagneticHeading(360)));

    [Fact]
    public void HeadingDigits_NegativeWraps() => Assert.Equal("two seven zero", PhraseologyVerbalizer.HeadingDigits(new MagneticHeading(-90)));

    [Fact]
    public void DigitsWords_Padding() => Assert.Equal("zero zero one", PhraseologyVerbalizer.DigitsWords(1, minWidth: 3));

    [Fact]
    public void SpellRunway_LeftRightCenter()
    {
        Assert.Equal("two eight right", PhraseologyVerbalizer.SpellRunway("28R"));
        Assert.Equal("two eight left", PhraseologyVerbalizer.SpellRunway("28L"));
        Assert.Equal("three four center", PhraseologyVerbalizer.SpellRunway("34C"));
    }

    [Fact]
    public void SpellRunway_DropsPaddingLeadingZero()
    {
        // Single-digit runways are spoken without the padding leading zero (AIM / 7110.65):
        // "runway eight right", "runway nine" — not "runway zero eight". Designators arrive
        // zero-padded (08R) from RunwayIdentifier.NormalizeDesignator.
        Assert.Equal("eight right", PhraseologyVerbalizer.SpellRunway("08R"));
        Assert.Equal("nine", PhraseologyVerbalizer.SpellRunway("09"));
        Assert.Equal("eight left", PhraseologyVerbalizer.SpellRunway("08L"));
        Assert.Equal("one", PhraseologyVerbalizer.SpellRunway("01"));
        // Two-digit and already-bare forms are unaffected.
        Assert.Equal("two eight right", PhraseologyVerbalizer.SpellRunway("28R"));
        Assert.Equal("eight right", PhraseologyVerbalizer.SpellRunway("8R"));
    }

    [Fact]
    public void SpellTaxiway_NatoLetters()
    {
        Assert.Equal("bravo six", PhraseologyVerbalizer.SpellTaxiway("B6"));
        Assert.Equal("alpha alpha", PhraseologyVerbalizer.SpellTaxiway("AA"));
    }

    // --- VerbalizeTerminal (compact controller form: digits + identifiers, shared rule patterns) ---

    [Fact]
    public void VerbalizeTerminal_FlyHeading_UsesDigits() =>
        Assert.Equal("fly heading 270", PhraseologyVerbalizer.VerbalizeTerminal(new FlyHeadingCommand(new MagneticHeading(270))));

    [Fact]
    public void VerbalizeTerminal_ClimbMaintain_UsesDigits() =>
        Assert.Equal("climb and maintain 5000", PhraseologyVerbalizer.VerbalizeTerminal(new ClimbMaintainCommand(5000)));

    [Fact]
    public void VerbalizeTerminal_TaxiPathOnly_UsesSpaceJoinedLetters() =>
        Assert.Equal("taxi via B C", PhraseologyVerbalizer.VerbalizeTerminal(new TaxiCommand(["B", "C"], [], Expedite: false)));

    [Fact]
    public void VerbalizeTerminal_TaxiWithRunway_CompactIdentifiers()
    {
        // 08R → 8R; taxiway path → space-joined letters. Spoken form spells the phonetics; the
        // terminal form mirrors the same rule with compact tokens.
        var taxi = new TaxiCommand(["B", "C", "D"], [], Expedite: false, DestinationRunway: "08R");
        string? spoken = PhraseologyVerbalizer.Verbalize(taxi);
        string? terminal = PhraseologyVerbalizer.VerbalizeTerminal(taxi);

        Assert.Contains("eight right", spoken);
        Assert.Contains("bravo, charlie, delta", spoken);
        Assert.Contains("8R", terminal);
        Assert.Contains("B C D", terminal);
        Assert.DoesNotContain("eight", terminal);
    }

    // --- Runway exits (issue #840): the readback mirrors the instruction ---
    //
    // The speech-input rules carry filler optionals ("when? if? able? at? on? to?") so a controller
    // utterance like "exit left when able at charlie" maps to EL C. Those rules are SttOnly, so the
    // pilot never speaks the fillers back; the readback comes from the clean rule with no optionals,
    // "exit left W3" — the exit instruction mirrored, not embellished. Nothing in 7110.65 §3-7-2 or
    // §3-10-9 prescribes a pilot readback for an exit, and the controller's "if able" is a
    // condition on the instruction, not something the crew repeats.

    [Fact]
    public void VerbalizeTerminal_ExitTaxiway_MirrorsTheInstruction() =>
        Assert.Equal("exit W3", PhraseologyVerbalizer.VerbalizeTerminal(new ExitTaxiwayCommand("W3")));

    [Fact]
    public void VerbalizeTerminal_ExitLeft_DropsFillerWords() =>
        Assert.Equal("exit left W3", PhraseologyVerbalizer.VerbalizeTerminal(new ExitLeftCommand(Taxiway: "W3")));

    [Fact]
    public void VerbalizeTerminal_ExitRight_DropsFillerWords() =>
        Assert.Equal("exit right W3", PhraseologyVerbalizer.VerbalizeTerminal(new ExitRightCommand(Taxiway: "W3")));

    [Fact]
    public void VerbalizeTerminal_BareExitLeftAndRight_KeepTheirCleanForm()
    {
        Assert.Equal("exit left", PhraseologyVerbalizer.VerbalizeTerminal(new ExitLeftCommand()));
        Assert.Equal("exit right", PhraseologyVerbalizer.VerbalizeTerminal(new ExitRightCommand()));
    }

    [Fact]
    public void Verbalize_ExitLeftWithTaxiway_SpellsTheTaxiwayPhonetically() =>
        // PilotSpeechText.Tts is built from this spoken form ("whiskey three", never "W3").
        Assert.Contains("exit left whiskey three", PhraseologyVerbalizer.Verbalize(new ExitLeftCommand(Taxiway: "W3")));

    // --- Frequency formatting (FAA 7110.65 §2-4-17.k.1) ---

    [Theory]
    [InlineData(125.35, "twenty five thirty five")]
    [InlineData(118.65, "eighteen sixty five")]
    [InlineData(120.85, "twenty eighty five")]
    [InlineData(125.3, "twenty five point three")]
    [InlineData(121.5, "twenty one point five")]
    [InlineData(118.275, "eighteen twenty seven")]
    [InlineData(118.05, "one one eight point zero five")]
    [InlineData(121.0, "one two one point zero")]
    [InlineData(108.2, "one zero eight point two")]
    [InlineData(251.0, "two five one point zero")]
    public void FrequencyToWords_Grouped_DropsTheLeadingOneWhereTheFrequencyGroups(double mhz, string expected) =>
        Assert.Equal(expected, PhraseologyVerbalizer.FrequencyToWords(mhz, grouped: true));

    [Fact]
    public void FrequencyToWords_NotGrouped_IsTheDigitForm() =>
        Assert.Equal("one two five point three five", PhraseologyVerbalizer.FrequencyToWords(125.35, grouped: false));

    [Fact]
    public void FrequencyToWords_TwoDecimalDigits() => Assert.Equal("one two five point three five", PhraseologyVerbalizer.FrequencyToWords(125.35));

    [Fact]
    public void FrequencyToWords_OneDecimalDigit_NoZeroPad() =>
        // 7110.65 §2-4-17.k.1 example: 121.5 MHz → "One two one point five." Trailing zeros dropped.
        Assert.Equal("one two one point five", PhraseologyVerbalizer.FrequencyToWords(121.5));

    [Fact]
    public void FrequencyToWords_ThirdDecimalTruncated() =>
        // 7110.65 §2-4-17.k.1 example: 135.275 MHz → "One three five point two seven."
        Assert.Equal("one three five point two seven", PhraseologyVerbalizer.FrequencyToWords(135.275));

    [Fact]
    public void FrequencyToWords_WholeNumber_SingleZeroAfterPoint() =>
        // 7110.65 §2-4-17.k.1 example: 369.0 MHz → "Three six niner point zero." YAAT uses "nine"
        // for digit 9 across all spoken numbers; see DigitToWord in AtcNumberParser.
        Assert.Equal("three six nine point zero", PhraseologyVerbalizer.FrequencyToWords(369.0));

    [Fact]
    public void FrequencyToWords_LiveAtcExamples()
    {
        Assert.Equal("one one eight point two", PhraseologyVerbalizer.FrequencyToWords(118.2));
        Assert.Equal("one one nine point six", PhraseologyVerbalizer.FrequencyToWords(119.6));
        Assert.Equal("one two four point nine five", PhraseologyVerbalizer.FrequencyToWords(124.95));
    }

    [Fact]
    public void FrequencyToWords_8_33kHzSpacing_DropsImpliedThirdDigit()
    {
        // 8.33 kHz channels: 128.525 reads as "one two eight point five two" — pilots and
        // controllers omit the trailing 5 because it's implied by the channel spacing.
        Assert.Equal("one two eight point five two", PhraseologyVerbalizer.FrequencyToWords(128.525));
        Assert.Equal("one three two point zero one", PhraseologyVerbalizer.FrequencyToWords(132.015));
    }

    [Fact]
    public void FrequencyToWords_FloatingPointTolerance() =>
        // 119.6 represented as 119.60000000000001 (binary-fp drift) must not produce extra digits.
        Assert.Equal("one one nine point six", PhraseologyVerbalizer.FrequencyToWords(119.60000000000001));

    // --- Speed: round knots in separate digits ---

    [Theory]
    [InlineData(100, "maintain one zero zero knots")]
    [InlineData(150, "maintain one five zero knots")]
    [InlineData(180, "maintain one eight zero knots")]
    [InlineData(200, "maintain two zero zero knots")]
    [InlineData(220, "maintain two two zero knots")]
    [InlineData(250, "maintain two five zero knots")]
    [InlineData(280, "maintain two eight zero knots")]
    [InlineData(310, "maintain three one zero knots")]
    [InlineData(350, "maintain three five zero knots")]
    public void Verbalize_Speed_RoundKnots_SpeaksSeparateDigits(int speed, string expected)
    {
        // The canonical speed readback is separate digits plus "knots" (AIM 4-2-11; 7110.65 2-4-17.l.1).
        string? result = PhraseologyVerbalizer.Verbalize(new SpeedCommand(speed));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(213, "maintain two one three knots")]
    [InlineData(247, "maintain two four seven knots")]
    public void Verbalize_Speed_NonRoundKnots_FallsBackToDigits(int speed, string expected)
    {
        // Speeds not on a 10-knot boundary aren't real pilot-readback values, but if the system
        // is ever asked to verbalize one, fall back to digit-by-digit rather than guess at a
        // colloquial form.
        string? result = PhraseologyVerbalizer.Verbalize(new SpeedCommand(speed));
        Assert.Equal(expected, result);
    }

    // --- Pattern entry: runway-bearing form preferred when command carries runway (issue #154 #6) ---

    [Fact]
    public void Verbalize_EnterRightDownwind_WithRunway_IncludesRunwayPhrase()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new EnterRightDownwindCommand("28R"));
        Assert.Equal("enter right downwind runway two eight right", result);
    }

    [Fact]
    public void Verbalize_EnterRightDownwind_NoRunway_FallsBackToBareForm()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new EnterRightDownwindCommand());
        Assert.Equal("enter right downwind", result);
    }

    [Fact]
    public void Verbalize_EnterLeftDownwind_WithRunway_IncludesRunwayPhrase()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new EnterLeftDownwindCommand("10L"));
        Assert.Equal("enter left downwind runway one zero left", result);
    }

    [Fact]
    public void Verbalize_EnterLeftBase_WithRunway_IncludesRunwayPhrase()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new EnterLeftBaseCommand("28R"));
        Assert.Equal("enter left base runway two eight right", result);
    }

    [Fact]
    public void Verbalize_EnterRightBase_WithRunway_IncludesRunwayPhrase()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new EnterRightBaseCommand("28R"));
        Assert.Equal("enter right base runway two eight right", result);
    }

    [Fact]
    public void Verbalize_MakeLeftTraffic_WithRunway_IncludesRunwayPhrase()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new MakeLeftTrafficCommand("28R", null));
        Assert.Equal("make left traffic runway two eight right", result);
    }

    [Fact]
    public void Verbalize_MakeRightTraffic_WithRunway_IncludesRunwayPhrase()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new MakeRightTrafficCommand("28R", null));
        Assert.Equal("make right traffic runway two eight right", result);
    }

    [Fact]
    public void EnterFinal_WithRunway_ReadsBackMakeStraightInRunway()
    {
        // 7110.65 §3-10-1.a "MAKE STRAIGHT-IN"; the "…approach" variant is never spoken back.
        string? result = PhraseologyVerbalizer.Verbalize(new EnterFinalCommand("28R"));
        Assert.Equal("make straight in runway two eight right", result);
    }

    [Fact]
    public void EnterFinal_WithoutRunway_ReadsBackMakeStraightIn()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new EnterFinalCommand());
        Assert.Equal("make straight in", result);
    }

    // --- CrossFix ---
    // CEPIN is a real intersection in OAK-area test data with a published pronunciation
    // override of "seppin" (Data/ARTCCs/ZOA/FixPronunciations/ambiguous.json) — that's what
    // SpellFix returns and what pilots actually say on frequency.

    [Fact]
    public void Verbalize_CrossFix_At_ReadsBackBasicForm()
    {
        var cmd = new CrossFixCommand("CEPIN", 0, 0, 5000, CrossFixAltitudeType.At, null);
        string? result = PhraseologyVerbalizer.Verbalize(cmd);
        Assert.Equal("cross seppin at five thousand", result);
    }

    [Fact]
    public void Verbalize_CrossFix_AtOrAbove_ReadsBackWithModifier()
    {
        var cmd = new CrossFixCommand("CEPIN", 0, 0, 5000, CrossFixAltitudeType.AtOrAbove, null);
        string? result = PhraseologyVerbalizer.Verbalize(cmd);
        Assert.Equal("cross seppin at or above five thousand", result);
    }

    [Fact]
    public void Verbalize_CrossFix_AtOrBelow_ReadsBackWithModifier()
    {
        var cmd = new CrossFixCommand("CEPIN", 0, 0, 5000, CrossFixAltitudeType.AtOrBelow, null);
        string? result = PhraseologyVerbalizer.Verbalize(cmd);
        Assert.Equal("cross seppin at or below five thousand", result);
    }

    [Fact]
    public void Verbalize_CrossFix_AtFlightLevel_UsesFlightLevelForm()
    {
        var cmd = new CrossFixCommand("CEPIN", 0, 0, 25000, CrossFixAltitudeType.At, null);
        string? result = PhraseologyVerbalizer.Verbalize(cmd);
        Assert.Equal("cross seppin at flight level two five zero", result);
    }

    [Fact]
    public void Verbalize_CrossFix_WithSpeed_UsesAndMaintainSpeedForm()
    {
        var cmd = new CrossFixCommand("CEPIN", 0, 0, 5000, CrossFixAltitudeType.At, 250);
        string? result = PhraseologyVerbalizer.Verbalize(cmd);
        Assert.Equal("cross seppin at and maintain five thousand at two five zero knots", result);
    }

    // --- ClimbVia ---

    [Fact]
    public void Verbalize_ClimbVia_BareForm()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new ClimbViaCommand(null));
        Assert.Equal("climb via sid", result);
    }

    [Fact]
    public void Verbalize_ClimbVia_ExceptMaintain()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new ClimbViaCommand(5000));
        Assert.Equal("climb via sid except maintain five thousand", result);
    }

    [Fact]
    public void Verbalize_ClimbVia_ExceptMaintain_FlightLevel()
    {
        string? result = PhraseologyVerbalizer.Verbalize(new ClimbViaCommand(18000));
        Assert.Equal("climb via sid except maintain flight level one eight zero", result);
    }

    // --- Helicopter ---

    [Fact]
    public void Verbalize_AirTaxiToRunway_ReadsBackTheRunway()
    {
        // An air taxi to a runway ends holding short of it (7110.65 3-11-1.c), so the runway is read back
        // (4-4-7.b.4) — unlike an air taxi to a helipad, which needs only the acknowledgement.
        Assert.Equal("air taxi to runway two eight left", PhraseologyVerbalizer.Verbalize(new AirTaxiCommand("28L")));
        Assert.Equal("air taxi to runway 28L", PhraseologyVerbalizer.VerbalizeTerminal(new AirTaxiCommand("28L")));
    }

    [Fact]
    public void Verbalize_AirTaxiToRunwayAtTaxiway_ReadsBackTheLocativeForm()
    {
        // The AT locative ground phraseology uses for an intersection, matching the located hold short
        // ("hold short of charlie at juliett", issue #358).
        Assert.Equal("air taxi to runway two eight left at juliett", PhraseologyVerbalizer.Verbalize(new AirTaxiCommand("28L@J")));
        Assert.Equal("air taxi to runway 28L at J", PhraseologyVerbalizer.VerbalizeTerminal(new AirTaxiCommand("28L@J")));
    }

    // --- SttOnly rules must NOT leak into pilot speech ---

    [Fact]
    public void Verbalize_DescendMaintain_PrefersDescendOverSttOnlyDescentAlias()
    {
        // PhraseologyRules.AltitudeSpeedRules() declares a "descent and maintain {alt}" alias
        // flagged SttOnly to recover Whisper mistranscriptions of "descend" as "descent".
        // The verbalizer must skip SttOnly rules so the pilot AI never speaks "descent and maintain"
        // — that would be grammatically wrong and a regression of the canonical readback.
        string? result = PhraseologyVerbalizer.Verbalize(new DescendMaintainCommand(3000));
        Assert.Equal("descend and maintain three thousand", result);
    }
}
