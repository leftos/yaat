using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Pilot;

public class PilotResponderTests
{
    public PilotResponderTests()
    {
        // Needed for AtFixCondition.FromName lookups in the at-fix-condition tests.
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft(string callsign, string? parkingSpot = null, bool isVfr = false)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Ground = new AircraftGroundOps { ParkingSpot = parkingSpot },
        };
        if (isVfr)
        {
            ac.FlightPlan.FlightRules = "VFR";
        }
        return ac;
    }

    private static CompoundCommand Compound(params ParsedCommand[] commands) => new([new ParsedBlock(null, [.. commands])]);

    private static CompoundCommand CompoundWithCondition(BlockCondition condition, params ParsedCommand[] commands) =>
        new([new ParsedBlock(condition, [.. commands])]);

    private static AircraftState MakeAircraftWithAssignedRunway(string callsign, string runwayId)
    {
        AircraftState ac = MakeAircraft(callsign);
        ac.Phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(runwayId) };
        return ac;
    }

    [Fact]
    public void BuildReadback_SingleAltitudeCommand_SpokenFormWithCallsign()
    {
        AircraftState ac = MakeAircraft("AAL123");
        CompoundCommand compound = Compound(new DescendMaintainCommand(5000));

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Equal("descend and maintain five thousand, american one twenty three.", result);
    }

    [Fact]
    public void BuildReadback_NNumber_SpokenForm()
    {
        AircraftState ac = MakeAircraft("N123AB");
        CompoundCommand compound = Compound(new ClimbMaintainCommand(3500));

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.StartsWith("climb and maintain three thousand five hundred, november one two three alpha bravo", result);
    }

    [Fact]
    public void BuildReadback_TwoCommandsInOneBlock_JoinedWithCommas()
    {
        AircraftState ac = MakeAircraft("AAL123");
        CompoundCommand compound = Compound(new DescendMaintainCommand(5000), new TurnRightCommand(new MagneticHeading(270)));

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Contains("descend and maintain five thousand", result!);
        Assert.Contains("turn right heading two seven zero", result);
        Assert.EndsWith(", american one twenty three.", result);
    }

    [Fact]
    public void BuildReadback_VariedBusy_ShortensClausesAndKeepsCallsign()
    {
        AircraftState ac = MakeAircraft("AAL123");
        CompoundCommand compound = Compound(new DescendMaintainCommand(5000), new TurnRightCommand(new MagneticHeading(270)));

        string? result = PilotResponder
            .BuildReadback(
                compound,
                ac,
                new PilotSpeechContext(PilotPersonality.Varied, FrequencyLoadBand.Busy, 1, 0)
                {
                    ShortenCallsign = false,
                    GroupContactFrequency = false,
                    CurrentIndicatedAirspeed = null,
                },
                ActiveRunways.Empty
            )
            ?.Tts;

        // Below FL180 the descent drops its verb ("down to" is flight-level only); the turn shortens.
        Assert.Equal("five thousand, right heading two seven zero, american one twenty three.", result);
    }

    [Fact]
    public void BuildReadback_VariedBusy_DoesNotDropRunwayCriticalContent()
    {
        AircraftState ac = MakeAircraftWithAssignedRunway("N436MS", "28R");
        CompoundCommand compound = Compound(new LineUpAndWaitCommand());

        string? result = PilotResponder
            .BuildReadback(
                compound,
                ac,
                new PilotSpeechContext(PilotPersonality.Varied, FrequencyLoadBand.Busy, 1, 0)
                {
                    ShortenCallsign = false,
                    GroupContactFrequency = false,
                    CurrentIndicatedAirspeed = null,
                },
                ActiveRunways.Empty
            )
            ?.Tts;

        Assert.Equal("line up and wait runway two eight right, november four three six mike sierra.", result);
    }

    [Fact]
    public void BuildReadback_VariedBusy_TerminalIsCanonical()
    {
        AircraftState ac = MakeAircraft("AAL123");
        CompoundCommand compound = Compound(new DescendMaintainCommand(5000), new TurnRightCommand(new MagneticHeading(270)));
        PilotSpeechText? canonical = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty);

        PilotSpeechText? varied = PilotResponder.BuildReadback(
            compound,
            ac,
            new PilotSpeechContext(PilotPersonality.Varied, FrequencyLoadBand.Busy, 1, 0)
            {
                ShortenCallsign = false,
                GroupContactFrequency = false,
                CurrentIndicatedAirspeed = null,
            },
            ActiveRunways.Empty
        );

        Assert.NotNull(canonical);
        Assert.NotNull(varied);
        Assert.Equal(canonical.Terminal, varied.Terminal);
        Assert.Equal(canonical.TerminalForRpo, varied.TerminalForRpo);
        Assert.StartsWith("five thousand, right heading two seven zero", varied.Tts);
    }

    [Fact]
    public void BuildReadback_LineUpAndWaitWithoutDelay_AppendsWithoutDelay()
    {
        AircraftState ac = MakeAircraftWithAssignedRunway("N436MS", "28R");
        CompoundCommand compound = Compound(new LineUpAndWaitCommand { WithoutDelay = true });

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Contains("line up and wait runway two eight right", result!);
        Assert.Contains("without delay", result);
    }

    [Fact]
    public void BuildReadback_ClearedForImmediateTakeoff_SaysImmediate()
    {
        AircraftState ac = MakeAircraftWithAssignedRunway("AAL123", "28R");
        CompoundCommand compound = Compound(new ClearedForTakeoffCommand(new DefaultDeparture()) { Immediate = true });

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Contains("cleared for immediate takeoff runway two eight right", result!);
    }

    // --- A4-6: "caution wake turbulence" is a controller advisory, not a pilot readback item ---

    [Fact]
    public void BuildReadback_ClearedForTakeoffWithWakeCaution_OmitsControllerAdvisory()
    {
        AircraftState ac = MakeAircraftWithAssignedRunway("AAL123", "28R");
        CompoundCommand compound = Compound(new ClearedForTakeoffCommand(new DefaultDeparture()) { CautionWakeTurbulence = true });

        PilotSpeechText? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty);

        Assert.NotNull(result);
        Assert.DoesNotContain("caution wake turbulence", result!.Tts);
        Assert.DoesNotContain("caution wake turbulence", result.Terminal);
        Assert.Contains("cleared for takeoff runway two eight right", result.Tts);
    }

    [Fact]
    public void BuildReadback_ClearedToLandWithWakeCaution_OmitsControllerAdvisory()
    {
        AircraftState ac = MakeAircraftWithAssignedRunway("AAL123", "28R");
        CompoundCommand compound = Compound(new ClearedToLandCommand { CautionWakeTurbulence = true });

        PilotSpeechText? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty);

        Assert.NotNull(result);
        Assert.DoesNotContain("caution wake turbulence", result!.Tts);
        Assert.DoesNotContain("caution wake turbulence", result.Terminal);
        Assert.Contains("cleared to land runway two eight right", result.Tts);
    }

    [Fact]
    public void BuildReadback_VariedQuietFlavor_IsDeterministicAndRare()
    {
        var flavored = new List<string>();
        for (int i = 0; i < 1000; i++)
        {
            AircraftState ac = MakeAircraft($"N{i:D3}AB");
            CompoundCommand compound = Compound(new SquawkCommand(4521));

            string? first = PilotResponder
                .BuildReadback(
                    compound,
                    ac,
                    new PilotSpeechContext(PilotPersonality.Varied, FrequencyLoadBand.Quiet, 1, 0)
                    {
                        ShortenCallsign = false,
                        GroupContactFrequency = false,
                        CurrentIndicatedAirspeed = null,
                    },
                    ActiveRunways.Empty
                )
                ?.Tts;
            string? second = PilotResponder
                .BuildReadback(
                    compound,
                    ac,
                    new PilotSpeechContext(PilotPersonality.Varied, FrequencyLoadBand.Quiet, 1, 0)
                    {
                        ShortenCallsign = false,
                        GroupContactFrequency = false,
                        CurrentIndicatedAirspeed = null,
                    },
                    ActiveRunways.Empty
                )
                ?.Tts;

            Assert.Equal(first, second);
            if (first!.Contains("alright", StringComparison.OrdinalIgnoreCase) || first.Contains("thanks", StringComparison.OrdinalIgnoreCase))
            {
                flavored.Add(first);
            }
        }

        Assert.NotEmpty(flavored);
        Assert.InRange(flavored.Count, 20, 150);
    }

    [Fact]
    public void BuildReadback_VariedQuietFlavor_PicksFromTheCanonicalBody()
    {
        AircraftState ac = MakeAircraft("N004AB");
        CompoundCommand compound = Compound(new SquawkCommand(4521));
        PilotSpeechText? canonical = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty);
        Assert.NotNull(canonical);

        var picks = new List<int>();
        for (int second = 0; second < 200; second++)
        {
            string? tts = PilotResponder
                .BuildReadback(
                    compound,
                    ac,
                    new PilotSpeechContext(PilotPersonality.Varied, FrequencyLoadBand.Quiet, 1, second)
                    {
                        ShortenCallsign = false,
                        GroupContactFrequency = false,
                        CurrentIndicatedAirspeed = null,
                    },
                    ActiveRunways.Empty
                )
                ?.Tts;
            int pick = VariantPick.Index(ac.Callsign, canonical.Terminal, second, 1, 100);
            picks.Add(pick);

            Assert.NotNull(tts);
            Assert.Equal(pick < 5, tts.StartsWith("alright, ", StringComparison.Ordinal));
            Assert.Equal(pick is >= 5 and < 10, tts.Contains("thanks", StringComparison.Ordinal));
        }

        Assert.Contains(picks, pick => pick < 10);
        Assert.Contains(picks, pick => pick >= 10);
    }

    [Fact]
    public void BuildReadback_VariedQuietFlavor_RuleWithoutQuietRowsTakesBothFlavors() =>
        AssertQuietFlavors(MakeAircraft("N004AB"), Compound(new SquawkCommand(4521)), alrightAllowed: true, thanksAllowed: true);

    [Fact]
    public void BuildReadback_VariedQuietFlavor_NoAlrightOnLandingClearance() =>
        AssertQuietFlavors(
            MakeAircraftWithAssignedRunway("N004AB", "28R"),
            Compound(new ClearedToLandCommand()),
            alrightAllowed: false,
            thanksAllowed: true
        );

    [Fact]
    public void BuildReadback_VariedQuietFlavor_NoAlrightOnLeftOrRight() =>
        AssertQuietFlavors(MakeAircraft("N004AB"), Compound(new EnterLeftDownwindCommand()), alrightAllowed: false, thanksAllowed: true);

    [Fact]
    public void BuildReadback_VariedQuietFlavor_NoFlavorOnGoAround() =>
        AssertQuietFlavors(
            MakeAircraftWithAssignedRunway("N004AB", "28R"),
            Compound(new GoAroundCommand(null, null, null, RunwayHeading: false)),
            alrightAllowed: false,
            thanksAllowed: false
        );

    [Fact]
    public void BuildReadback_VariedQuietFlavor_NoThanksOnLahso() =>
        AssertQuietFlavors(
            MakeAircraftWithAssignedRunway("N004AB", "28R"),
            Compound(new LandAndHoldShortCommand("33")),
            alrightAllowed: false,
            thanksAllowed: false
        );

    [Fact]
    public void BuildReadback_VariedQuietFlavor_NoAlrightOnRunwaySide()
    {
        TestVnasData.EnsureInitialized();
        AssertQuietFlavors(
            MakeAircraft("N004AB"),
            Compound(new TaxiCommand([], [], Expedite: false, "28R")),
            alrightAllowed: false,
            thanksAllowed: true
        );
    }

    /// <summary>
    /// The HOLD readback on a runway mid-crossing ("holding position on runway 33") takes neither flavour: the pilot's
    /// -ing form of hold position is as safety-critical as the controller's.
    /// </summary>
    [Fact]
    public void ApplyQuietFlavor_HoldingPositionOnRunway_TakesNoFlavor()
    {
        const string callsign = "N004AB";
        const string canonicalBody = "holding position on runway 33";
        const string body = "holding position on runway three three";
        CompoundCommand compound = Compound(new HoldPositionCommand());

        bool alrightPicked = false;
        bool thanksPicked = false;
        for (int second = 0; second < 200; second++)
        {
            var context = new PilotSpeechContext(PilotPersonality.Varied, FrequencyLoadBand.Quiet, 1, second)
            {
                ShortenCallsign = false,
                GroupContactFrequency = false,
                CurrentIndicatedAirspeed = null,
            };
            string spoken = PilotResponder.ApplyQuietFlavor(callsign, body, canonicalBody, compound, context);
            int pick = VariantPick.Index(callsign, canonicalBody, second, 1, 100);
            alrightPicked |= pick < 5;
            thanksPicked |= pick is >= 5 and < 10;

            Assert.Equal(body, spoken);
        }

        Assert.True(alrightPicked);
        Assert.True(thanksPicked);
    }

    /// <summary>
    /// Over seconds 0..199 at Quiet, the readback carries "alright, " exactly where the pick allows it and the body permits
    /// it, and ", thanks" likewise; both picks occur in the loop, so each permission is exercised.
    /// </summary>
    private static void AssertQuietFlavors(AircraftState ac, CompoundCommand compound, bool alrightAllowed, bool thanksAllowed)
    {
        PilotSpeechText? canonical = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty);
        Assert.NotNull(canonical);

        bool alrightPicked = false;
        bool thanksPicked = false;
        for (int second = 0; second < 200; second++)
        {
            string? tts = PilotResponder
                .BuildReadback(
                    compound,
                    ac,
                    new PilotSpeechContext(PilotPersonality.Varied, FrequencyLoadBand.Quiet, 1, second)
                    {
                        ShortenCallsign = false,
                        GroupContactFrequency = false,
                        CurrentIndicatedAirspeed = null,
                    },
                    ActiveRunways.Empty
                )
                ?.Tts;
            int pick = VariantPick.Index(ac.Callsign, canonical.Terminal, second, 1, 100);
            alrightPicked |= pick < 5;
            thanksPicked |= pick is >= 5 and < 10;

            Assert.NotNull(tts);
            Assert.Equal(alrightAllowed && (pick < 5), tts.StartsWith("alright, ", StringComparison.Ordinal));
            Assert.Equal(thanksAllowed && (pick is >= 5 and < 10), tts.Contains("thanks", StringComparison.Ordinal));
        }

        Assert.True(alrightPicked);
        Assert.True(thanksPicked);
    }

    [Fact]
    public void BuildReadback_VariedBusy_DisablesQuietFlavor()
    {
        AircraftState ac = MakeAircraft("N004AB");
        CompoundCommand compound = Compound(new FlyHeadingCommand(new MagneticHeading(270)));

        string? result = PilotResponder
            .BuildReadback(
                compound,
                ac,
                new PilotSpeechContext(PilotPersonality.Varied, FrequencyLoadBand.Busy, 1, 0)
                {
                    ShortenCallsign = false,
                    GroupContactFrequency = false,
                    CurrentIndicatedAirspeed = null,
                },
                ActiveRunways.Empty
            )
            ?.Tts;

        Assert.Equal("heading two seven zero, november zero zero four alpha bravo.", result);
        Assert.DoesNotContain("alright", result);
        Assert.DoesNotContain("thanks", result);
    }

    private static readonly string[] SpeedChangeWords = ["slowing to", "speeding up to", "maintaining"];

    private static PilotSpeechContext VariedWithSpeed(FrequencyLoadBand band, int seed, int second, double? indicatedAirspeed) =>
        new(PilotPersonality.Varied, band, seed, second)
        {
            ShortenCallsign = false,
            GroupContactFrequency = false,
            CurrentIndicatedAirspeed = indicatedAirspeed,
        };

    /// <summary>The spoken readbacks of <paramref name="compound"/> over seeds 0..<paramref name="seeds"/>-1 and seconds 0..199.</summary>
    private static List<string> SpokenReadbacks(CompoundCommand compound, FrequencyLoadBand band, double? indicatedAirspeed, int seeds)
    {
        AircraftState ac = MakeAircraft("N123AB");
        ac.IndicatedAirspeed = indicatedAirspeed ?? 0;
        return
        [
            .. Enumerable
                .Range(0, seeds)
                .SelectMany(seed =>
                    Enumerable
                        .Range(0, 200)
                        .Select(second =>
                            PilotResponder
                                .BuildReadback(compound, ac, VariedWithSpeed(band, seed, second, indicatedAirspeed), ActiveRunways.Empty)!
                                .Tts
                        )
                ),
        ];
    }

    [Theory]
    [InlineData(FrequencyLoadBand.Quiet)]
    [InlineData(FrequencyLoadBand.Normal)]
    public void BuildReadback_VariedSpeed_FasterThanAssigned_MaySaySlowingNeverSpeedingUp(FrequencyLoadBand band)
    {
        List<string> spoken = SpokenReadbacks(Compound(new SpeedCommand(210)), band, 250, 1);

        Assert.Contains(spoken, tts => tts.StartsWith("slowing to two one zero knots, ", StringComparison.Ordinal));
        Assert.DoesNotContain(
            spoken,
            tts => tts.Contains("speeding up", StringComparison.Ordinal) || tts.Contains("maintaining", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void BuildReadback_VariedQuietSpeed_FasterThanAssigned_SlowingRowIsPinned()
    {
        List<string> spoken = SpokenReadbacks(Compound(new SpeedCommand(210)), FrequencyLoadBand.Quiet, 250, 1);

        // Seed 1: the first second in 0-199 whose pick is the slowing row.
        int first = spoken.FindIndex(tts => tts.StartsWith("slowing to", StringComparison.Ordinal));
        Assert.Equal(4, first);
        Assert.Equal("slowing to two one zero knots, november one two three alpha bravo.", spoken[first]);
    }

    [Fact]
    public void BuildReadback_VariedQuietSpeed_WithinTenKnots_SaysMaintainingNeverSlowingOrSpeedingUp()
    {
        List<string> spoken = SpokenReadbacks(Compound(new SpeedCommand(210)), FrequencyLoadBand.Quiet, 205, 1);

        Assert.Contains(spoken, tts => tts.StartsWith("maintaining two one zero knots, ", StringComparison.Ordinal));
        Assert.DoesNotContain(
            spoken,
            tts => tts.Contains("slowing", StringComparison.Ordinal) || tts.Contains("speeding up", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void BuildReadback_VariedQuietSpeed_SlowerThanAssigned_MaySaySpeedingUpNeverSlowing()
    {
        List<string> spoken = SpokenReadbacks(Compound(new SpeedCommand(210)), FrequencyLoadBand.Quiet, 180, 1);

        Assert.Contains(spoken, tts => tts.StartsWith("speeding up to two one zero knots, ", StringComparison.Ordinal));
        Assert.DoesNotContain(
            spoken,
            tts => tts.Contains("slowing", StringComparison.Ordinal) || tts.Contains("maintaining", StringComparison.Ordinal)
        );
    }

    [Theory]
    [InlineData(FrequencyLoadBand.Quiet)]
    [InlineData(FrequencyLoadBand.Normal)]
    public void BuildReadback_VariedSpeed_NoCurrentSpeed_NeverSpeaksASpeedChangeRow(FrequencyLoadBand band)
    {
        List<string> spoken = SpokenReadbacks(Compound(new SpeedCommand(210)), band, null, 10);

        Assert.DoesNotContain(spoken, tts => SpeedChangeWords.Any(words => tts.Contains(words, StringComparison.Ordinal)));
    }

    [Fact]
    public void BuildReadback_VariedQuietSpeed_ModifierOrUntil_NeverSpeaksASpeedChangeRow()
    {
        // "SPD 210 UNTIL SUNOL" reaches the readback as CommandSchemeParser.ExpandSpeedUntil writes it: "SPD 210; AT SUNOL RNS".
        var until = new CompoundCommand([
            new ParsedBlock(null, [new SpeedCommand(210)]),
            new ParsedBlock(AtFixCondition.FromName("SUNOL"), [new ResumeNormalSpeedCommand()]),
        ]);
        List<string> spoken =
        [
            .. SpokenReadbacks(Compound(new SpeedCommand(210, SpeedModifier.Floor)), FrequencyLoadBand.Quiet, 250, 5),
            .. SpokenReadbacks(Compound(new SpeedCommand(210, SpeedModifier.Ceiling)), FrequencyLoadBand.Quiet, 180, 5),
            .. SpokenReadbacks(until, FrequencyLoadBand.Quiet, 250, 5),
        ];

        Assert.DoesNotContain(spoken, tts => SpeedChangeWords.Any(words => tts.Contains(words, StringComparison.Ordinal)));
    }

    /// <summary>
    /// A quiet readback from a rule with Quiet rows is exactly one of the rule's candidates framed with the callsign: the
    /// canonical or a row, never the quiet flavour's "alright, " or ", thanks" on top. A row that places the callsign speaks
    /// it there, in the closing callsign's spoken form.
    /// </summary>
    [Theory]
    [InlineData("FH")]
    [InlineData("DM")]
    [InlineData("DCT")]
    public void BuildReadback_VariedQuiet_RuleWithQuietRows_SpeaksOnlyItsRowsWithoutTheFlavor(string rule)
    {
        ParsedCommand command = rule switch
        {
            "FH" => new FlyHeadingCommand(new MagneticHeading(270)),
            "DM" => new DescendMaintainCommand(5000),
            "DCT" => new DirectToCommand([new ResolvedFix("SUNOL", 37.6, -121.9)], []),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "No representative command for this rule."),
        };
        const string spokenCallsign = "november one two three alpha bravo";
        string[] allowed =
        [
            .. PhraseologyVerbalizer
                .SpokenCandidates(command, MakeAircraft("N123AB"), FrequencyLoadBand.Quiet, null)
                .Select(candidate =>
                    candidate.Contains("{cs}", StringComparison.Ordinal)
                        ? candidate.Replace("{cs}", spokenCallsign, StringComparison.Ordinal) + "."
                        : $"{candidate}, {spokenCallsign}."
                ),
        ];

        List<string> spoken = SpokenReadbacks(Compound(command), FrequencyLoadBand.Quiet, null, 5);

        Assert.All(spoken, tts => Assert.Contains(tts, allowed));
        Assert.Contains(spoken, tts => tts.Contains($", {spokenCallsign}, thanks.", StringComparison.Ordinal));
        Assert.Contains(spoken, tts => tts == allowed[0]);
    }

    /// <summary>
    /// A two-command readback is chained, and a chained readback is never chatty: it speaks the normal rows and takes no quiet
    /// flavour, so FH and squawk on a quiet frequency never gain "alright" or ", thanks".
    /// </summary>
    [Fact]
    public void BuildReadback_VariedQuiet_ChainedCompound_TakesNoFlavor()
    {
        List<string> spoken = SpokenReadbacks(
            Compound(new FlyHeadingCommand(new MagneticHeading(270)), new SquawkCommand(4521)),
            FrequencyLoadBand.Quiet,
            null,
            1
        );

        Assert.DoesNotContain(
            spoken,
            tts => tts.StartsWith("alright, ", StringComparison.Ordinal) || tts.Contains(", thanks", StringComparison.Ordinal)
        );
    }

    [Theory]
    [InlineData("SPD 210 UNTIL SUNOL")]
    [InlineData("SPD 210 UNTIL 5")]
    [InlineData("FH 270, RNS")]
    public void BuildReadback_VariedBusy_WilcoNeverSitsInsideACompound(string command)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, $"'{command}' was refused: {parsed.Reason}");

        List<string> spoken = SpokenReadbacks(parsed.Value!, FrequencyLoadBand.Busy, null, 5);

        Assert.DoesNotContain(spoken, tts => tts.Contains("wilco", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildReadback_VariedBusy_LoneResumeNormalSpeed_MaySayWilco()
    {
        List<string> spoken = SpokenReadbacks(Compound(new ResumeNormalSpeedCommand()), FrequencyLoadBand.Busy, null, 1);

        Assert.Contains("wilco, november one two three alpha bravo.", spoken);
    }

    /// <summary>
    /// A cross-fix restriction at or above / at or below with a speed reads the speed back after the altitude, as the plain
    /// "at" form does (7110.65 5-7-2.d), in the Terminal text and in every band's spoken text.
    /// </summary>
    [Theory]
    [InlineData("CFIX SUNOL A049 250", "at or above")]
    [InlineData("CFIX SUNOL B049 250", "at or below")]
    public void BuildReadback_CrossFixRestrictionWithSpeed_ReadsBackTheSpeedInEveryBand(string command, string qualifier)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, $"'{command}' was refused: {parsed.Reason}");
        AircraftState ac = MakeAircraft("N123AB");
        PilotSpeechContext[] contexts =
        [
            PilotSpeechContext.Canonical,
            .. new[] { FrequencyLoadBand.Quiet, FrequencyLoadBand.Normal, FrequencyLoadBand.Busy }.SelectMany(band =>
                Enumerable.Range(0, 20).Select(second => VariedWithSpeed(band, 1, second, null))
            ),
        ];

        foreach (PilotSpeechContext context in contexts)
        {
            PilotSpeechText readback = PilotResponder.BuildReadback(parsed.Value!, ac, context, ActiveRunways.Empty)!;

            Assert.Contains($"cross SUNOL {qualifier} 4900 at 250 knots", readback.Terminal, StringComparison.Ordinal);
            Assert.Contains($"cross sunol {qualifier} four thousand nine hundred at two five zero knots", readback.Tts, StringComparison.Ordinal);
        }
    }

    private const string SpokenN123AB = "november one two three alpha bravo";

    /// <summary>The spoken readbacks of <paramref name="command"/> by <paramref name="ac"/> over seconds 0-199 at <paramref name="seed"/>.</summary>
    private static List<string> SpokenOverSeconds(AircraftState ac, ParsedCommand command, FrequencyLoadBand band, int seed) =>
        [
            .. Enumerable
                .Range(0, 200)
                .Select(second =>
                    PilotResponder.BuildReadback(Compound(command), ac, VariedWithSpeed(band, seed, second, null), ActiveRunways.Empty)!.Tts
                ),
        ];

    [Theory]
    [InlineData(false, "maintaining at or above four thousand")]
    [InlineData(true, "maintaining VFR at or above four thousand")]
    public void BuildReadback_VariedQuietRestriction_MaySayMaintainingWithTheCourtesy(bool isVfr, string row)
    {
        AircraftState ac = MakeAircraft("N123AB", isVfr: isVfr);

        var command = new ClimbMaintainCommand(4000, AltitudeAssignmentModifier.AtOrAbove);
        string[] allowed = [PilotResponder.BuildReadback(Compound(command), ac, ActiveRunways.Empty)!.Tts, $"{row}, {SpokenN123AB}, thanks."];

        List<string> spoken = SpokenOverSeconds(ac, command, FrequencyLoadBand.Quiet, 1);

        // Seed 1: the first second in 0-199 whose pick is the courtesy row.
        Assert.Equal(isVfr ? 2 : 0, spoken.IndexOf(allowed[1]));
        Assert.All(spoken, tts => Assert.Contains(tts, allowed));
        Assert.All(spoken, tts => Assert.Equal(isVfr, tts.Contains("VFR", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A busy altitude or cross-fix restriction from 10,000 to 17,999 ft may speak the group form, and never says "above"
    /// without "at or".
    /// </summary>
    [Theory]
    [InlineData("CM A110", "at or above eleven thousand")]
    [InlineData("CFIX SUNOL A110", "cross sunol at or above eleven thousand")]
    public void BuildReadback_VariedBusyRestriction_MaySpeakTheGroupFormAndKeepsTheQualifier(string shape, string groupRow)
    {
        ParsedCommand command =
            shape == "CM A110"
                ? new ClimbMaintainCommand(11000, AltitudeAssignmentModifier.AtOrAbove)
                : new CrossFixCommand("SUNOL", 37.6, -121.9, 11000, CrossFixAltitudeType.AtOrAbove, null);
        AircraftState ac = MakeAircraft("N123AB");

        List<string> spoken = [.. Enumerable.Range(0, 5).SelectMany(seed => SpokenOverSeconds(ac, command, FrequencyLoadBand.Busy, seed))];

        Assert.Contains($"{groupRow}, {SpokenN123AB}.", spoken);
        Assert.DoesNotContain(
            spoken,
            tts => tts.Replace("at or above", string.Empty, StringComparison.Ordinal).Contains("above", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// Each restriction and expedite variant set's pattern is the builder's canonical text: filled with the command's own
    /// captures, it speaks the same words as the canonical readback, punctuation and case aside.
    /// </summary>
    [Theory]
    [InlineData("restriction at or above")]
    [InlineData("restriction at or below")]
    [InlineData("restriction VFR at or above")]
    [InlineData("restriction VFR at or below")]
    [InlineData("expedite climb with altitude")]
    [InlineData("expedite descent with altitude")]
    [InlineData("expedite climb")]
    [InlineData("expedite descent")]
    [InlineData("expedite taxi")]
    [InlineData("expedite exit")]
    [InlineData("cleared approach")]
    [InlineData("cleared approach naming no type")]
    [InlineData("cleared straight in approach")]
    [InlineData("join approach")]
    [InlineData("join straight in approach")]
    [InlineData("expect approach")]
    [InlineData("cleared visual approach")]
    [InlineData("follow traffic airborne")]
    [InlineData("PTAC turn heading climb")]
    [InlineData("PTAC turn heading descend")]
    [InlineData("PTAC turn heading maintain")]
    [InlineData("PTAC fly heading climb")]
    [InlineData("PTAC fly heading descend")]
    [InlineData("PTAC fly heading maintain")]
    public void BuilderVariantSet_Pattern_SpeaksTheBuildersCanonical(string setName)
    {
        BuilderVariantSet set = PhraseologyRules.BuilderVariantSets.Single(candidate => candidate.Name == setName);
        (ParsedCommand command, AircraftState ac) = BuilderShape(setName);

        string canonical = PilotResponder.BuildReadback(Compound(command), ac, ActiveRunways.Empty)!.Tts;
        string body = canonical[..canonical.LastIndexOf($", {SpokenN123AB}.", StringComparison.Ordinal)];
        string pattern = string.Join(' ', set.Pattern);
        string rendered = BuilderCaptures(command, ac) is { } captures
            ? PhraseologyVerbalizer.RenderSpokenVariant(pattern, captures)
            : PhraseologyVerbalizer.RenderSpokenVariant(command, pattern);

        Assert.Equal(WordsOnly(rendered), WordsOnly(body));
    }

    /// <summary>The captures a builder supplies for its set's rows (an approach readback's); null when the command's own fill them.</summary>
    private static IReadOnlyDictionary<string, string>? BuilderCaptures(ParsedCommand command, AircraftState ac) =>
        command is ExpectApproachCommand expect
            ? PhraseologyVerbalizer.ExpectApproachVariants(expect)?.Captures
            : PilotResponder.VariedBuilderClause(command, ac)?.Captures;

    /// <summary>A command and an aircraft whose readback the builder renders from the variant set named <paramref name="setName"/>.</summary>
    private static (ParsedCommand Command, AircraftState Aircraft) BuilderShape(string setName)
    {
        AircraftState ac = MakeAircraft("N123AB", isVfr: setName.Contains("VFR", StringComparison.Ordinal));
        ac.Altitude = 5000;
        ac.Targets.TargetAltitude = setName == "expedite climb" ? 9000 : 2000;
        ParsedCommand command = setName switch
        {
            "restriction at or above" or "restriction VFR at or above" => new ClimbMaintainCommand(4000, AltitudeAssignmentModifier.AtOrAbove),
            "restriction at or below" or "restriction VFR at or below" => new ClimbMaintainCommand(4000, AltitudeAssignmentModifier.AtOrBelow),
            "expedite climb with altitude" => new ExpediteCommand(11000),
            "expedite descent with altitude" => new ExpediteCommand(3000),
            "cross fix at" => new CrossFixCommand("SUNOL", 37.6, -121.9, 11000, CrossFixAltitudeType.At, null),
            "cross fix at or above" => new CrossFixCommand("SUNOL", 37.6, -121.9, 11000, CrossFixAltitudeType.AtOrAbove, null),
            "cross fix at or below" => new CrossFixCommand("SUNOL", 37.6, -121.9, 11000, CrossFixAltitudeType.AtOrBelow, null),
            "expedite climb" or "expedite descent" or "expedite taxi" or "expedite exit" => new ExpediteCommand(),
            "cleared approach" => new ClearedApproachCommand("ILS28R", null, false, null, null, null, null, null, null, null, null),
            "cleared approach naming no type" => new ClearedApproachCommand("Z28R", null, false, null, null, null, null, null, null, null, null),
            "cleared straight in approach" => new ClearedApproachStraightInCommand("ILS28R", null),
            "join approach" => new JoinApproachCommand("ILS28R", null, false),
            "join straight in approach" => new JoinApproachStraightInCommand("ILS28R", null),
            "expect approach" => new ExpectApproachCommand("RNAV28RZ", null),
            "cleared visual approach" => new ClearedVisualApproachCommand("28R", null, PatternDirection.Left, "N456CD", false),
            "follow traffic airborne" => new FollowCommand("N456CD", false),
            "PTAC turn heading climb" => new PositionTurnAltitudeClearanceCommand(
                new MagneticHeading(240),
                TurnDirection.Left,
                7000,
                "ILS28R",
                false
            ),
            "PTAC turn heading descend" => new PositionTurnAltitudeClearanceCommand(
                new MagneticHeading(240),
                TurnDirection.Left,
                3000,
                "ILS28R",
                false
            ),
            "PTAC turn heading maintain" => new PositionTurnAltitudeClearanceCommand(
                new MagneticHeading(240),
                TurnDirection.Right,
                5000,
                "ILS28R",
                false
            ),
            "PTAC fly heading climb" => new PositionTurnAltitudeClearanceCommand(new MagneticHeading(240), null, 7000, "ILS28R", false),
            "PTAC fly heading descend" => new PositionTurnAltitudeClearanceCommand(new MagneticHeading(240), null, 3000, "ILS28R", false),
            "PTAC fly heading maintain" => new PositionTurnAltitudeClearanceCommand(new MagneticHeading(240), null, 5000, "ILS28R", false),
            _ => throw new ArgumentOutOfRangeException(nameof(setName), setName, "No builder shape for this set."),
        };
        if (setName is "expedite taxi" or "expedite exit")
        {
            ac.Altitude = 13;
            ac.IsOnGround = true;
            ac.Ground.AssignedTaxiRoute = new TaxiRoute { Segments = [], HoldShortPoints = [] };
        }
        if (setName == "expedite exit")
        {
            ac.Phases = new PhaseList();
            ac.Phases.Add(new LandingPhase { Status = PhaseStatus.Active });
        }

        return (command, ac);
    }

    /// <summary>The first command of <paramref name="text"/> as the parser reads it.</summary>
    private static ParsedCommand ParseOne(string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' was refused: {parsed.Reason}");
        return parsed.Value!.Blocks[0].Commands[0];
    }

    [Fact]
    public void SpokenCandidates_QuietCrossFixAtOrAbove_OffersWellCross() =>
        Assert.Contains(
            "we'll cross sunol at or above four thousand nine hundred",
            PhraseologyVerbalizer.SpokenCandidates(ParseOne("CFIX SUNOL A049"), MakeAircraft("N123AB"), FrequencyLoadBand.Quiet, null)
        );

    [Theory]
    [InlineData(false, "maintaining at or above four thousand, {cs}, thanks", "at or above four thousand")]
    [InlineData(true, "maintaining VFR at or above four thousand, {cs}, thanks", "VFR at or above four thousand")]
    public void SpokenCandidates_AltitudeRestriction_OffersTheRestrictionRows(bool isVfr, string quietRow, string busyRow)
    {
        AircraftState ac = MakeAircraft("N123AB", isVfr: isVfr);
        var command = new ClimbMaintainCommand(4000, AltitudeAssignmentModifier.AtOrAbove);

        Assert.Contains(quietRow, PhraseologyVerbalizer.SpokenCandidates(command, ac, FrequencyLoadBand.Quiet, null));
        Assert.Contains(busyRow, PhraseologyVerbalizer.SpokenCandidates(command, ac, FrequencyLoadBand.Busy, null));
    }

    [Fact]
    public void SpokenCandidates_BusyBareExpediteDescending_IsExpeditingNeverWilco()
    {
        (ParsedCommand command, AircraftState ac) = BuilderShape("expedite descent");

        IReadOnlyList<string> busy = PhraseologyVerbalizer.SpokenCandidates(command, ac, FrequencyLoadBand.Busy, null);

        Assert.Equal(["expediting"], busy);
        Assert.Equal(["expedite descent", "expediting"], PhraseologyVerbalizer.SpokenCandidates(command, ac, FrequencyLoadBand.Normal, null));
        Assert.DoesNotContain(busy, candidate => candidate.Contains("wilco", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(FrequencyLoadBand.Quiet)]
    [InlineData(FrequencyLoadBand.Normal)]
    [InlineData(FrequencyLoadBand.Busy)]
    public void SpokenCandidates_CrossFixWithSpeed_IsTheCanonicalOnly(FrequencyLoadBand band) =>
        Assert.Equal(
            ["cross sunol at or above four thousand nine hundred at two five zero knots"],
            PhraseologyVerbalizer.SpokenCandidates(ParseOne("CFIX SUNOL A049 250"), MakeAircraft("N123AB"), band, null)
        );

    /// <summary>
    /// For every builder set and band, the texts a varied pilot speaks for the builder-rendered readback over a sweep of seeds
    /// and seconds are exactly <see cref="PhraseologyVerbalizer.SpokenCandidates"/> framed with the callsign: none spoken
    /// outside it, none of it never spoken.
    /// </summary>
    [Fact]
    public void SpokenCandidates_EveryBuilderSetAndBand_EqualsEveryTextTheReadbackSpeaks()
    {
        FrequencyLoadBand[] bands = [FrequencyLoadBand.Quiet, FrequencyLoadBand.Normal, FrequencyLoadBand.Busy];
        List<string> failures = [];
        // The airport-level visual's runway is the pilot's pick resolved at dispatch, so only the handler readback carries it.
        foreach (
            BuilderVariantSet set in PhraseologyRules.BuilderVariantSets.Where(set =>
                !ReferenceEquals(set, PhraseologyRules.AirportVisualApproachClearance)
            )
        )
        {
            (ParsedCommand command, AircraftState ac) = BuilderShape(set.Name);
            Assert.Same(set, SelectedBuilderSet(command, ac));
            foreach (FrequencyLoadBand band in bands)
            {
                IReadOnlyList<string> candidates = PhraseologyVerbalizer.SpokenCandidates(command, ac, band, null);
                HashSet<string> allowed = [.. candidates.Select(FramedWithN123AB)];
                // A set with no Quiet rows leaves the quiet frequency to the readback's flavour, a layer outside the candidates.
                HashSet<string> flavoured =
                    (band == FrequencyLoadBand.Quiet) && (set.Variants.Quiet.Count == 0)
                        ? [.. candidates.Select(candidate => $"{candidate}, thanks, {SpokenN123AB}.")]
                        : [];
                HashSet<string> spoken = [.. Enumerable.Range(0, 5).SelectMany(seed => SpokenOverSeconds(ac, command, band, seed))];
                failures.AddRange(spoken.Except(allowed).Except(flavoured).Select(tts => $"{set.Name} {band}: spoken, not a candidate: \"{tts}\""));
                failures.AddRange(allowed.Except(spoken).Select(tts => $"{set.Name} {band}: a candidate never spoken: \"{tts}\""));
            }
        }

        Assert.True(
            failures.Count == 0,
            $"SpokenCandidates differs from the spoken readbacks:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}"
        );
    }

    /// <summary>
    /// A bare CAPP clearing an expected visual reads back the visual clearance's rows; the candidates framed with the callsign
    /// are exactly what the readback speaks over a sweep of seeds and seconds.
    /// </summary>
    [Theory]
    [InlineData(FrequencyLoadBand.Busy)]
    [InlineData(FrequencyLoadBand.Quiet)]
    public void SpokenCandidates_BareCappClearingAnExpectedVisual_EqualsWhatItSpeaks(FrequencyLoadBand band)
    {
        AircraftState ac = MakeAircraft("N123AB");
        ac.Approach.Expected = "VIS28R";
        var command = new ClearedApproachCommand(null, null, false, null, null, null, null, null, null, null, null);

        HashSet<string> allowed = [.. PhraseologyVerbalizer.SpokenCandidates(command, ac, band, null).Select(FramedWithN123AB)];
        HashSet<string> spoken = [.. Enumerable.Range(0, 5).SelectMany(seed => SpokenOverSeconds(ac, command, band, seed))];

        Assert.Contains(allowed, candidate => candidate.StartsWith("cleared visual", StringComparison.Ordinal));
        Assert.True(spoken.SetEquals(allowed), $"Candidates: {string.Join(" | ", allowed)}; spoken: {string.Join(" | ", spoken)}");
    }

    /// <summary>
    /// A CAPP with a route clears no expected visual (the dispatcher clears the visual only for a bare CAPP), so neither its
    /// candidates nor its readback speak the visual clearance. The parser always gives a routed CAPP an approach id (its last
    /// token), so the routed shape with none is built directly.
    /// </summary>
    [Fact]
    public void SpokenCandidates_CappWithARoute_WithAVisualExpected_IsNotTheVisual()
    {
        AircraftState ac = MakeAircraft("N123AB");
        ac.Approach.Expected = "VIS28R";
        var command = new ClearedApproachCommand(null, null, false, null, null, null, "SUNOL", 37.6, -121.9, null, null);

        foreach (FrequencyLoadBand band in new[] { FrequencyLoadBand.Busy, FrequencyLoadBand.Normal })
        {
            Assert.DoesNotContain(
                PhraseologyVerbalizer.SpokenCandidates(command, ac, band, null),
                candidate => candidate.StartsWith("cleared visual", StringComparison.Ordinal)
            );
        }

        string tts = PilotResponder.BuildReadback(Compound(command), ac, ActiveRunways.Empty)!.Tts;
        Assert.False(tts.StartsWith("cleared visual", StringComparison.Ordinal), tts);
    }

    /// <summary>The variant set the readback picks for <paramref name="command"/> by <paramref name="ac"/>.</summary>
    private static BuilderVariantSet SelectedBuilderSet(ParsedCommand command, AircraftState ac) =>
        command switch
        {
            CrossFixCommand crossFix => PhraseologyVerbalizer.CrossFixVariantSet(crossFix),
            ExpectApproachCommand expect => PhraseologyVerbalizer.ExpectApproachVariants(expect)!.Value.Set,
            _ => PilotResponder.VariedBuilderClause(command, ac)!.Value.Variants,
        };

    /// <summary>A candidate as N123AB's whole readback speaks it: the callsign in its row's place, or closing the clause.</summary>
    private static string FramedWithN123AB(string candidate) =>
        candidate.Contains("{cs}", StringComparison.Ordinal)
            ? candidate.Replace("{cs}", SpokenN123AB, StringComparison.Ordinal) + "."
            : $"{candidate}, {SpokenN123AB}.";

    /// <summary>
    /// A military route clearance is read back canonical in every band, so its only candidate is the readback's own clause.
    /// Candidates are clause text before the readback's text-to-speech fixups ("i-r" → "i r"), as every candidate is.
    /// </summary>
    [Theory]
    [InlineData("CMTR IR149 B50", FrequencyLoadBand.Quiet)]
    [InlineData("CMTR IR149 B50", FrequencyLoadBand.Normal)]
    [InlineData("CMTR IR149 B50", FrequencyLoadBand.Busy)]
    [InlineData("CMTR IR149", FrequencyLoadBand.Quiet)]
    [InlineData("CMTR IR149", FrequencyLoadBand.Normal)]
    [InlineData("CMTR IR149", FrequencyLoadBand.Busy)]
    public void SpokenCandidates_MilitaryRouteClearance_IsTheReadbacksCanonicalOnly(string text, FrequencyLoadBand band)
    {
        ParsedCommand command = ParseOne(text);
        AircraftState ac = MakeAircraft("N123AB");
        string canonical = PilotResponder.BuildReadback(Compound(command), ac, ActiveRunways.Empty)!.Tts;
        string body = canonical[..canonical.LastIndexOf($", {SpokenN123AB}.", StringComparison.Ordinal)];

        IReadOnlyList<string> candidates = PhraseologyVerbalizer.SpokenCandidates(command, ac, band, null);

        Assert.Equal([body], candidates.Select(candidate => PilotResponder.PrepareForTts(ac, candidate)));
    }

    private static string WordsOnly(string text) =>
        string.Join(' ', text.ToLowerInvariant().Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    [Fact]
    public void BuildReadback_VariedQuietCrossFixAtOrAbove_MaySayWellCross()
    {
        var crossFix = new CrossFixCommand("SUNOL", 37.6, -121.9, 4900, CrossFixAltitudeType.AtOrAbove, null);

        List<string> spoken = SpokenOverSeconds(MakeAircraft("N123AB"), crossFix, FrequencyLoadBand.Quiet, 1);

        // Seed 1: the first second in 0-199 whose pick is the quiet row.
        Assert.Equal(0, spoken.IndexOf($"we'll cross sunol at or above four thousand nine hundred, {SpokenN123AB}."));
    }

    [Fact]
    public void VerbalizeClause_CrossFixRestrictionWithSpeed_IsCanonicalInEveryBand()
    {
        var crossFix = new CrossFixCommand("SUNOL", 37.6, -121.9, 4900, CrossFixAltitudeType.AtOrAbove, 250);
        FrequencyLoadBand[] bands = [FrequencyLoadBand.Quiet, FrequencyLoadBand.Normal, FrequencyLoadBand.Busy];

        IEnumerable<string?> spoken = bands.SelectMany(band =>
            Enumerable
                .Range(0, 5)
                .SelectMany(seed =>
                    Enumerable
                        .Range(0, 200)
                        .Select(second =>
                            PhraseologyVerbalizer
                                .VerbalizeClause(crossFix, "N123AB", VariedWithSpeed(band, seed, second, null), wholeReadback: true)
                                .Text
                        )
                )
        );

        Assert.All(spoken, text => Assert.Equal("cross sunol at or above four thousand nine hundred at two five zero knots", text));
    }

    [Fact]
    public void BuildReadback_MilitaryRouteAtOrBelow_IsCanonicalInEveryBand()
    {
        AircraftState ac = MakeAircraft("N123AB");
        var command = new ClearedIntoMilitaryRouteCommand("IR111", 11000, AtOrBelow: true);
        PilotSpeechText canonical = PilotResponder.BuildReadback(Compound(command), ac, ActiveRunways.Empty)!;
        string canonicalBody = canonical.Tts[..canonical.Tts.LastIndexOf($", {SpokenN123AB}.", StringComparison.Ordinal)];
        Assert.Contains("maintain at or below one one thousand", canonicalBody, StringComparison.Ordinal);

        foreach (FrequencyLoadBand band in new[] { FrequencyLoadBand.Quiet, FrequencyLoadBand.Normal, FrequencyLoadBand.Busy })
        {
            foreach (int seed in Enumerable.Range(0, 5))
            {
                Assert.All(SpokenOverSeconds(ac, command, band, seed), tts => Assert.StartsWith(canonicalBody + ", ", tts, StringComparison.Ordinal));
            }
        }
    }

    /// <summary>
    /// A quiet readback from a clause builder with Quiet rows takes its courtesy from the rows: never the flavour's leading
    /// "alright, ", and ", thanks" only where a row places the callsign before it.
    /// </summary>
    [Theory]
    [InlineData("EXP 110")]
    [InlineData("EXP descending")]
    [InlineData("EXP taxiing")]
    [InlineData("CM A040")]
    [InlineData("CFIX A049")]
    public void BuildReadback_VariedQuietClauseBuilder_NeverTakesTheFlavor(string shape)
    {
        AircraftState ac = MakeAircraft("N123AB");
        ac.Altitude = 5000;
        ParsedCommand command = shape switch
        {
            "EXP 110" => new ExpediteCommand(11000),
            "EXP descending" or "EXP taxiing" => new ExpediteCommand(),
            "CM A040" => new ClimbMaintainCommand(4000, AltitudeAssignmentModifier.AtOrAbove),
            "CFIX A049" => new CrossFixCommand("SUNOL", 37.6, -121.9, 4900, CrossFixAltitudeType.AtOrAbove, null),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "No representative command for this shape."),
        };
        ac.Targets.TargetAltitude = 2000;
        if (shape == "EXP taxiing")
        {
            ac.Altitude = 13;
            ac.IsOnGround = true;
            ac.Ground.AssignedTaxiRoute = new TaxiRoute { Segments = [], HoldShortPoints = [] };
        }

        List<string> spoken = [.. Enumerable.Range(0, 5).SelectMany(seed => SpokenOverSeconds(ac, command, FrequencyLoadBand.Quiet, seed))];

        Assert.DoesNotContain(spoken, tts => tts.StartsWith("alright, ", StringComparison.Ordinal));
        Assert.All(
            spoken,
            tts =>
                Assert.True(
                    !tts.Contains("thanks", StringComparison.Ordinal) || tts.EndsWith($", {SpokenN123AB}, thanks.", StringComparison.Ordinal),
                    $"\"{tts}\" carries the quiet flavour's \", thanks\"."
                )
        );
    }

    [Theory]
    [InlineData(FrequencyLoadBand.Quiet)]
    [InlineData(FrequencyLoadBand.Normal)]
    public void BuildReadback_VariedSpeed_InALaterBlock_NeverSaysSlowingOrSpeedingUp(FrequencyLoadBand band)
    {
        // "CM 100; SPD 250": the speed is assigned after the climb, so the current speed says nothing about that change.
        var compound = new CompoundCommand([
            new ParsedBlock(null, [new ClimbMaintainCommand(10000)]),
            new ParsedBlock(null, [new SpeedCommand(250)]),
        ]);

        List<string> spoken = SpokenReadbacks(compound, band, 280, 5);

        Assert.DoesNotContain(
            spoken,
            tts => tts.Contains("slowing", StringComparison.Ordinal) || tts.Contains("speeding up", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// A readback of more than one clause, or with a condition, closes with the callsign and never speaks a row that places
    /// it elsewhere: no placeholder leaks, and the callsign is spoken once, last.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildReadback_VariedQuiet_MultiClauseOrConditioned_ClosesWithTheCallsignOnly(bool conditioned)
    {
        const string spokenCallsign = "november one two three alpha bravo";
        CompoundCommand compound = conditioned
            ? CompoundWithCondition(AtFixCondition.FromName("SUNOL"), new FlyHeadingCommand(new MagneticHeading(270)))
            : Compound(new FlyHeadingCommand(new MagneticHeading(270)), new DescendMaintainCommand(5000));

        List<string> spoken = SpokenReadbacks(compound, FrequencyLoadBand.Quiet, null, 5);

        Assert.All(
            spoken,
            tts =>
            {
                Assert.DoesNotContain("{cs}", tts, StringComparison.Ordinal);
                Assert.Equal(tts.Length - spokenCallsign.Length - 1, tts.IndexOf(spokenCallsign, StringComparison.Ordinal));
            }
        );
    }

    [Theory]
    [InlineData("N123AB", "C172", true, "november three alpha bravo")]
    [InlineData("UAL25", "B744", false, "united twenty five heavy")]
    public void WithBravoClearance_RowPlacedCallsignWithThanks_ClearanceComesLastBeforeTheCallsign(
        string callsign,
        string aircraftType,
        bool shortenCallsign,
        string spokenCallsign
    )
    {
        AircraftState ac = MakeAircraft(callsign);
        ac.AircraftType = aircraftType;
        PilotSpeechContext context = VariedWithSpeed(FrequencyLoadBand.Quiet, 1, 0, null) with { ShortenCallsign = shortenCallsign };
        Assert.Equal(spokenCallsign, PilotResponder.SpokenReadbackCallsign(ac, context));
        var readback = new PilotSpeechText("fly heading 270", $"fly heading two seven zero, {spokenCallsign}, thanks.");

        PilotSpeechText result = PilotResponder.WithBravoClearance(ac, readback, BravoClearanceWording.Into, context);

        Assert.Equal($"fly heading two seven zero, cleared into the bravo, {spokenCallsign}.", result.Tts);
    }

    [Fact]
    public void WithBravoClearance_UnknownSuffixAfterTheCallsign_IsKept()
    {
        AircraftState ac = MakeAircraft("N123AB");
        PilotSpeechContext context = VariedWithSpeed(FrequencyLoadBand.Quiet, 1, 0, null);
        var readback = new PilotSpeechText("fly heading 270", "fly heading two seven zero, november one two three alpha bravo, good day.");

        PilotSpeechText result = PilotResponder.WithBravoClearance(ac, readback, BravoClearanceWording.Into, context);

        Assert.Equal("fly heading two seven zero, cleared into the bravo, november one two three alpha bravo, good day.", result.Tts);
    }

    /// <summary>
    /// The Quiet rows keep the quiet flavour's exclusions: no "alright" row on a rule whose canonical holds a word that
    /// "alright, " never leads, and no ", thanks" row on a rule whose canonical holds a word ", thanks" never follows.
    /// </summary>
    [Fact]
    public void PhraseologyRules_QuietCourtesyRows_KeepTheFlavorExclusions()
    {
        var misplaced = new List<string>();
        foreach (PhraseologyRule rule in PhraseologyRules.All.Where(rule => rule.PilotVariants is not null))
        {
            string[] words = [.. rule.Pattern.Select(token => token.TrimEnd('?')).Where(token => !token.StartsWith('{'))];
            foreach (PilotVariant row in rule.PilotVariants!.Quiet)
            {
                if (row.Template.StartsWith("alright", StringComparison.Ordinal) && ContainsTerm(words, PilotResponder.NoAlrightTerms))
                {
                    misplaced.Add($"{rule.OutputTemplate}: \"{row.Template}\" (alright)");
                }

                if (row.Template.EndsWith(", thanks", StringComparison.Ordinal) && ContainsTerm(words, PilotResponder.NoThanksTerms))
                {
                    misplaced.Add($"{rule.OutputTemplate}: \"{row.Template}\" (thanks)");
                }
            }
        }

        foreach (BuilderVariantSet set in PhraseologyRules.BuilderVariantSets)
        {
            string[] words = [.. set.Pattern.Select(token => token.TrimEnd('?')).Where(token => !token.StartsWith('{'))];
            foreach (PilotVariant row in set.Variants.Quiet)
            {
                if (row.Template.StartsWith("alright", StringComparison.Ordinal) && ContainsTerm(words, PilotResponder.NoAlrightTerms))
                {
                    misplaced.Add($"{set.Name}: \"{row.Template}\" (alright)");
                }

                if (row.Template.EndsWith(", thanks", StringComparison.Ordinal) && ContainsTerm(words, PilotResponder.NoThanksTerms))
                {
                    misplaced.Add($"{set.Name}: \"{row.Template}\" (thanks)");
                }
            }
        }

        Assert.True(misplaced.Count == 0, $"Quiet rows that break a flavour exclusion: {string.Join(", ", misplaced)}");
    }

    private static bool ContainsTerm(string[] words, string[][] terms) =>
        terms.Any(term =>
            Enumerable.Range(0, Math.Max(0, words.Length - term.Length + 1)).Any(i => words.AsSpan(i, term.Length).SequenceEqual(term))
        );

    [Fact]
    public void BuildReadback_AtFixCondition_PrependsLeadingClause()
    {
        AircraftState ac = MakeAircraft("AAL123");
        CompoundCommand compound = CompoundWithCondition(AtFixCondition.FromName("SUNOL"), new TurnLeftCommand(new MagneticHeading(180)));

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Contains("at sunol, turn left heading one eight zero", result!);
    }

    [Fact]
    public void BuildReadback_AtFixCondition_ConditionSpokenOnceForBlock()
    {
        AircraftState ac = MakeAircraft("AAL123");
        CompoundCommand compound = CompoundWithCondition(
            AtFixCondition.FromName("SUNOL"),
            new TurnLeftCommand(new MagneticHeading(180)),
            new DescendMaintainCommand(5000)
        );

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        // The first command gets the "at sunol," lead; the second does not (same block).
        Assert.Contains("at sunol, turn left heading one eight zero", result!);
        Assert.DoesNotContain("at sunol, descend", result);
        Assert.Contains("descend and maintain five thousand", result);
    }

    [Fact]
    public void BuildReadback_NoVerbalizableCommands_ReturnsNull()
    {
        AircraftState ac = MakeAircraft("AAL123");
        CompoundCommand compound = Compound(new UnsupportedCommand("ZZZ 999"));

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Null(result);
    }

    [Fact]
    public void BuildReadback_ClearedForTakeoff_IncludesRunwayDepartureAndAltitude()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        AircraftState ac = MakeAircraft("N436MS");
        ac.Procedure.DepartureRunway = "28R";
        CompoundCommand compound = Compound(new ClearedForTakeoffCommand(new DirectFixDeparture("MOD", 37.625, -120.957, TurnDirection.Right), 2500));

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Equal(
            "cleared for takeoff runway two eight right, turn right direct Modesto VOR, climb and maintain two thousand five hundred, november four three six mike sierra.",
            result
        );
    }

    [Fact]
    public void BuildReadback_ClearedForTakeoff_RelativeTurnDepartureUsesGroupedDegrees()
    {
        AircraftState ac = MakeAircraft("N172SP");
        ac.Procedure.DepartureRunway = "28R";
        CompoundCommand compound = Compound(new ClearedForTakeoffCommand(new RelativeTurnDeparture(270, TurnDirection.Right), 1400));

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Equal(
            "cleared for takeoff runway two eight right, make a right two seventy degree departure, climb and maintain one thousand four hundred, november one seven two sierra papa.",
            result
        );
    }

    [Fact]
    public void BuildReadback_AcknowledgePilotContact_RemainsSilent()
    {
        AircraftState ac = MakeAircraft("N172SP");
        CompoundCommand compound = Compound(new AcknowledgePilotContactCommand());

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Null(result);
    }

    [Fact]
    public void BuildReadback_ClearedTakeoffPresent_IncludesDepartureAndAltitudeWithoutRunway()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        AircraftState ac = MakeAircraft("N436MS");
        ac.Procedure.DepartureRunway = "28R";
        CompoundCommand compound = Compound(
            new ClearedTakeoffPresentCommand(new DirectFixDeparture("MOD", 37.625, -120.957, TurnDirection.Right), 2500)
        );

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Equal(
            "cleared for takeoff, present position, turn right direct Modesto VOR, climb and maintain two thousand five hundred, november four three six mike sierra.",
            result
        );
    }

    [Fact]
    public void BuildReadback_DirectToVor_UsesPublishedNavaidName()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        AircraftState ac = MakeAircraft("N436MS");
        CompoundCommand compound = Compound(new DirectToCommand([new ResolvedFix("MOD", 37.625, -120.957)], []));

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Equal("proceed direct to Modesto VOR, november four three six mike sierra.", result);
    }

    [Fact]
    public void BuildReadback_DirectToMultipleFixes_ReadsAllJoinedWithThenDirect()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        // Mix two real ZOA entries: OAK30NUM is a custom fix with friendly name "Oakland
        // Runway 30 Numbers"; VPMID has a published pronunciation "Midspan San Mateo Bridge".
        // Exercises both lookups within a single multi-fix DCT readback.
        AircraftState ac = MakeAircraft("N172SP");
        CompoundCommand compound = Compound(new DirectToCommand([new ResolvedFix("OAK30NUM", 0, 0), new ResolvedFix("VPMID", 0, 0)], []));

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        // Variable-length DCT must read every fix; "then direct" joins later fixes.
        Assert.Equal(
            "proceed direct to Oakland Runway 30 Numbers, then direct Midspan San Mateo Bridge, november one seven two sierra papa.",
            result
        );
    }

    [Fact]
    public void BuildReadback_DirectToFixWithCustomPronunciation_UsesPronunciation()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        // VPCOL is registered in ARTCCs/ZOA/FixPronunciations/visual.json with pronunciation "Oakland Colliseum".
        AircraftState ac = MakeAircraft("N172SP");
        CompoundCommand compound = Compound(new DirectToCommand([new ResolvedFix("VPCOL", 0, 0)], []));

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Equal("proceed direct to Oakland Colliseum, november one seven two sierra papa.", result);
    }

    [Fact]
    public void BuildReadback_DirectToCustomFixWithFriendlyName_UsesFriendlyName()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        // OAK30NUM is a CustomFix in ARTCCs/ZOA/CustomFixes/oak-landmarks.json with friendly
        // name "Oakland Runway 30 Numbers" — the natural-language form pilots speak. With no
        // explicit pronunciation entry, SpellFix should fall through to the custom-fix name
        // rather than the literal alias spelled letter-by-letter.
        AircraftState ac = MakeAircraft("N172SP");
        CompoundCommand compound = Compound(new DirectToCommand([new ResolvedFix("OAK30NUM", 0, 0)], []));

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Equal("proceed direct to Oakland Runway 30 Numbers, november one seven two sierra papa.", result);
    }

    [Fact]
    public void BuildReadback_SequentialBlocks_JoinedWithThen()
    {
        AircraftState ac = MakeAircraft("AAL123");
        // Two blocks separated by ; — controller said "DM 5000 ; FH 270".
        var compound = new CompoundCommand([
            new ParsedBlock(null, [new DescendMaintainCommand(5000)]),
            new ParsedBlock(null, [new TurnRightCommand(new MagneticHeading(270))]),
        ]);

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Equal("descend and maintain five thousand, then turn right heading two seven zero, american one twenty three.", result);
    }

    [Fact]
    public void BuildReadback_ParallelBlock_JoinedWithCommaNotThen()
    {
        AircraftState ac = MakeAircraft("AAL123");
        // Single block with two parallel commands — controller said "DM 5000, FH 270".
        // Parallel commands stay comma-joined; "then" is sequential-only.
        CompoundCommand compound = Compound(new DescendMaintainCommand(5000), new TurnRightCommand(new MagneticHeading(270)));

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.DoesNotContain("then", result!);
        Assert.Equal("descend and maintain five thousand, turn right heading two seven zero, american one twenty three.", result);
    }

    public static TheoryData<ParsedCommand, string> RunwayCriticalTowerReadbackCases() =>
        new()
        {
            { new LineUpAndWaitCommand(), "line up and wait runway two eight right" },
            { new ClearedToLandCommand(), "cleared to land runway two eight right" },
            { new LandAndHoldShortCommand("28L"), "cleared to land runway two eight right, hold short runway two eight left" },
            { new TouchAndGoCommand(null, null, null, null), "cleared touch and go runway two eight right" },
            { new StopAndGoCommand(null, null, null), "cleared stop and go runway two eight right" },
            { new LowApproachCommand(null, null, null), "cleared low approach runway two eight right" },
            { new ClearedForOptionCommand(null, null, null), "cleared for the option runway two eight right" },
            { new ClearedForOptionCommand(PatternDirection.Right, null, null), "cleared for the option runway two eight right, make right traffic" },
            // A pattern modifier that names a runway assigns one for the next circuit, so the readback
            // states it (AIM 4-4-7.b.4) — "the option on 28R, then left traffic for 28L".
            {
                new ClearedForOptionCommand(PatternDirection.Left, "28L", null),
                "cleared for the option runway two eight right, make left traffic runway two eight left"
            },
            { new TouchAndGoCommand(null, PatternDirection.Right, null, null), "cleared touch and go runway two eight right, make right traffic" },
            {
                new TouchAndGoCommand(null, PatternDirection.Left, "28L", 1500),
                "cleared touch and go runway two eight right, make left traffic runway two eight left"
            },
            {
                new StopAndGoCommand(PatternDirection.Right, "10L", null),
                "cleared stop and go runway two eight right, make right traffic runway one zero left"
            },
            {
                new LowApproachCommand(PatternDirection.Left, "28L", null),
                "cleared low approach runway two eight right, make left traffic runway two eight left"
            },
        };

    [Theory]
    [MemberData(nameof(RunwayCriticalTowerReadbackCases))]
    public void BuildReadback_RunwayCriticalTowerClearances_IncludeAssignedRunway(ParsedCommand command, string expectedClause)
    {
        AircraftState ac = MakeAircraftWithAssignedRunway("N436MS", "28R");
        CompoundCommand compound = Compound(command);

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Equal($"{expectedClause}, november four three six mike sierra.", result);
    }

    /// <summary>
    /// The three output forms are built independently, so a clause added to the spoken readback can
    /// silently miss the compact terminal (solo student) and RPO forms. The pattern runway an option
    /// clearance names has to reach all three.
    /// </summary>
    [Fact]
    public void BuildReadback_OptionClearanceWithPatternRunway_CarriesItInEveryForm()
    {
        AircraftState ac = MakeAircraftWithAssignedRunway("N436MS", "28R");
        CompoundCommand compound = Compound(new ClearedForOptionCommand(PatternDirection.Left, "28L", null));

        PilotSpeechText? speech = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty);

        Assert.NotNull(speech);
        Assert.Contains("cleared for the option runway 28R, make left traffic runway 28L", speech.Terminal, StringComparison.Ordinal);
        Assert.Contains("cleared for the option runway 28R, make left traffic runway 28L", speech.TerminalForRpo, StringComparison.Ordinal);
        Assert.Contains("make left traffic runway two eight left", speech.Tts, StringComparison.Ordinal);
    }

    /// <summary>A bare MLT/MRT keeps the runway-less clause — it means the runway just cleared for.</summary>
    [Fact]
    public void BuildReadback_OptionClearanceWithoutPatternRunway_OmitsTheRunwayFromTheTrafficClause()
    {
        AircraftState ac = MakeAircraftWithAssignedRunway("N436MS", "28R");
        CompoundCommand compound = Compound(new ClearedForOptionCommand(PatternDirection.Left, null, null));

        PilotSpeechText? speech = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty);

        Assert.NotNull(speech);
        Assert.Contains("make left traffic", speech.Terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("make left traffic runway", speech.Terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("make left traffic runway", speech.Tts, StringComparison.Ordinal);
    }

    // --- A8-1: heavy/super wake-class suffix on the aircraft's own callsign (AIM 4-2-4.a.5) ---

    [Fact]
    public void BuildReadback_HeavyAircraft_AppendsHeavyToSpokenCallsign()
    {
        AircraftState ac = MakeAircraft("AAL123");
        ac.AircraftType = "B77W"; // CWT B -> Heavy
        CompoundCommand compound = Compound(new DescendMaintainCommand(5000));

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Equal("descend and maintain five thousand, american one twenty three heavy.", result);
    }

    [Fact]
    public void BuildReadback_SuperAircraft_AppendsSuperToSpokenCallsign()
    {
        AircraftState ac = MakeAircraft("AAL123");
        ac.AircraftType = "A388"; // CWT A -> Super
        CompoundCommand compound = Compound(new DescendMaintainCommand(5000));

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Equal("descend and maintain five thousand, american one twenty three super.", result);
    }

    [Fact]
    public void BuildReadback_LargeAircraft_NoWakeSuffix()
    {
        AircraftState ac = MakeAircraft("AAL123"); // B738 default -> Large (CWT F)
        CompoundCommand compound = Compound(new DescendMaintainCommand(5000));

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Equal("descend and maintain five thousand, american one twenty three.", result);
    }

    [Fact]
    public void BuildHoldingShortReady_HeavyAircraft_AppendsHeavyToProactiveCallsign()
    {
        AircraftState ac = MakeAircraft("AAL123");
        ac.AircraftType = "B763"; // CWT C -> Heavy

        PilotSpeechText result = PilotResponder.BuildHoldingShortReady(ac, "28R");

        Assert.Equal("tower, american one twenty three heavy holding short runway two eight right, ready for departure.", result.Tts);
    }

    // --- A1-2: data-driven ATIS letter + suppression when the field has no ATIS ---

    [Fact]
    public void BuildReadyToTaxi_NoAtis_DropsInformationClause()
    {
        AircraftState ac = MakeAircraft("N123AB", parkingSpot: "B22");

        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac, "ground", atisLetter: null);

        Assert.Equal("ground, november one two three alpha bravo at gate bravo two two, ready to taxi.", result.Tts);
        Assert.DoesNotContain("information", result.Tts);
    }

    [Fact]
    public void BuildReadyToTaxi_NonDefaultLetter_SpeaksThatLetter()
    {
        AircraftState ac = MakeAircraft("N123AB", parkingSpot: "GATE B22");

        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac, "ground", atisLetter: "B");

        Assert.Contains("with information Bravo", result.Tts);
    }

    [Fact]
    public void BuildClosedTrafficRequest_NoAtis_DropsInformationClause()
    {
        var airport = new LatLon(37.7212, -122.2208);
        AircraftState ac = MakeAircraft("N123AB", isVfr: true);
        ac.Position = GeoMath.ProjectPoint(airport, new TrueHeading(180), 3);

        PilotSpeechText result = PilotResponder.BuildClosedTrafficRequest(ac, airport, altitudeFt: 1500, "tower", atisLetter: null);

        Assert.EndsWith("request closed traffic.", result.Tts);
        Assert.DoesNotContain("information", result.Tts);
    }

    [Fact]
    public void ResolvePrimaryFieldAtisLetter_FieldHasAtis_ReturnsScenarioLetter()
    {
        SimScenarioState sc = ScenarioWithPrimaryFieldAtis("KOAK", hasAtis: true, atisLetter: "C");

        Assert.Equal("C", PilotResponder.ResolvePrimaryFieldAtisLetter(sc));
    }

    [Fact]
    public void ResolvePrimaryFieldAtisLetter_FieldHasNoAtisPosition_ReturnsNull()
    {
        SimScenarioState sc = ScenarioWithPrimaryFieldAtis("KOAK", hasAtis: false, atisLetter: "A");

        Assert.Null(PilotResponder.ResolvePrimaryFieldAtisLetter(sc));
    }

    [Fact]
    public void ResolvePrimaryFieldAtisLetter_NoArtccConfig_AssumesAtisAndReturnsLetter()
    {
        var sc = new SimScenarioState
        {
            ScenarioId = "t",
            ScenarioName = "t",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "KOAK",
            AtisLetter = "A",
        };

        Assert.Equal("A", PilotResponder.ResolvePrimaryFieldAtisLetter(sc));
    }

    private static SimScenarioState ScenarioWithPrimaryFieldAtis(string primaryAirportId, bool hasAtis, string atisLetter)
    {
        var positions = new List<PositionConfig>
        {
            new()
            {
                Id = "twr",
                Name = "Tower",
                RadioName = "Tower",
            },
        };
        if (hasAtis)
        {
            positions.Add(
                new PositionConfig
                {
                    Id = "atis",
                    Name = "ATIS",
                    RadioName = "ATIS",
                }
            );
        }
        return new SimScenarioState
        {
            ScenarioId = "t",
            ScenarioName = "t",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = primaryAirportId,
            AtisLetter = atisLetter,
            ArtccConfig = new ArtccConfigRoot
            {
                Id = "ZOA",
                Facility = new FacilityConfig
                {
                    Id = "ZOA",
                    Type = "Artcc",
                    Name = "Oakland",
                    ChildFacilities =
                    [
                        new FacilityConfig
                        {
                            Id = "OAK",
                            Type = "Atct",
                            Name = "Oakland",
                            Positions = positions,
                        },
                    ],
                },
            },
        };
    }

    // --- Dual-output readback: compact terminal SAY echo vs spelled TTS (issue #193) ---

    [Fact]
    public void BuildReadback_ClearedToLand_TerminalDropsCallsignAndLeadingZero()
    {
        AircraftState ac = MakeAircraftWithAssignedRunway("N436MS", "08R");
        CompoundCommand compound = Compound(new ClearedToLandCommand());

        PilotSpeechText? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty);

        Assert.NotNull(result);
        // Terminal SAY message: compact runway (no leading zero), no callsign — the SAY column carries it.
        Assert.Equal("cleared to land runway 8R", result!.Terminal);
        // TTS: spelled runway without the padding zero + spoken callsign appended.
        Assert.Equal("cleared to land runway eight right, november four three six mike sierra.", result.Tts);
    }

    [Fact]
    public void BuildReadback_Taxi_TerminalUsesIdentifiersTtsSpellsPhonetics()
    {
        AircraftState ac = MakeAircraft("N225R");
        CompoundCommand compound = Compound(new TaxiCommand(["B", "C", "D"], [], Expedite: false, DestinationRunway: "08R"));

        PilotSpeechText? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty);

        Assert.NotNull(result);
        Assert.Contains("8R", result!.Terminal);
        Assert.Contains("B C D", result.Terminal);
        Assert.DoesNotContain("november", result.Terminal); // callsign is column-only in terminal
        Assert.DoesNotContain("eight right", result.Terminal);
        Assert.Contains("eight right", result.Tts);
        Assert.Contains("bravo, charlie, delta", result.Tts);
        Assert.Contains("november two two five", result.Tts);
    }

    // --- A4-1: TAXI readback completeness (path + runway + cross + hold-short all echoed) ---

    [Fact]
    public void BuildReadback_TaxiWithRunwayCrossAndHoldShort_ReadsBackEveryCapture()
    {
        AircraftState ac = MakeAircraft("N225R");
        // "taxi to runway 28L via B C, cross runway 10R, hold short runway 28R" — the richest
        // capture set. PickPreferredRule must choose the four-capture TAXI variant so none of the
        // path / destination runway / cross runway / hold-short clauses are dropped.
        CompoundCommand compound = Compound(
            new TaxiCommand(["B", "C"], [HoldShortTarget.Parse("28R")], Expedite: false, DestinationRunway: "28L", CrossRunways: ["10R"])
        );

        PilotSpeechText? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty);

        Assert.NotNull(result);
        Assert.Contains("B C", result!.Terminal);
        Assert.Contains("28L", result.Terminal);
        Assert.Contains("10R", result.Terminal);
        Assert.Contains("28R", result.Terminal);
        Assert.Contains("bravo, charlie", result.Tts);
        Assert.Contains("two eight left", result.Tts);
        Assert.Contains("one zero right", result.Tts);
        Assert.Contains("two eight right", result.Tts);
    }

    // --- Issue #396: a rewritten TAXI reads back the route the aircraft will actually taxi ---

    [Fact]
    public void BuildReadbackAsApplied_DroppedLeadOut_SaysUnableThenEffectiveRoute()
    {
        AircraftState ac = MakeAircraft("AAL436");
        // Controller cleared M4 M1 A; the gate lead-out lane M4 was dropped by TryTaxi, so the aircraft taxis M1 A.
        var issued = new TaxiCommand(["M4", "M1", "A"], [], Expedite: false, DestinationRunway: "1R");
        var effective = new TaxiCommand(["M1", "A"], [], Expedite: false, DestinationRunway: "1R");

        PilotSpeechText? result = PilotResponder.BuildReadbackAsApplied(
            Compound(issued),
            new CommandResult(true, EffectiveCommand: effective),
            ac,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );

        Assert.NotNull(result);
        Assert.StartsWith("unable M4, ", result!.Terminal);
        Assert.Contains("M1 A", result.Terminal);
        Assert.StartsWith("unable mike four, ", result.Tts);
        Assert.Contains("mike one, alpha", result.Tts);
        Assert.Equal(1, result.Tts.Split("mike four").Length - 1); // the dropped lane is named once, never read back as route
    }

    [Fact]
    public void BuildReadbackAsApplied_NoRewrite_MatchesPlainReadback()
    {
        AircraftState ac = MakeAircraft("AAL436");
        CompoundCommand compound = Compound(new TaxiCommand(["M1", "A"], [], Expedite: false, DestinationRunway: "1R"));

        PilotSpeechText? plain = PilotResponder.BuildReadback(compound, ac, PilotSpeechContext.Canonical, ActiveRunways.Empty);
        PilotSpeechText? asApplied = PilotResponder.BuildReadbackAsApplied(
            compound,
            new CommandResult(true),
            ac,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );

        Assert.Equal(plain, asApplied);
    }

    /// <summary>
    /// A compound of two pushes names each one: the handler's readback (<see cref="CommandResult.PilotReadback"/>, the
    /// goal the dispatcher resolved) replaces the first push's clause only, and a later push reads back from its own
    /// parsed form. Substituting it into every push clause would have the crew read the first goal twice.
    /// </summary>
    [Fact]
    public void BuildReadbackAsApplied_CompoundOfTwoPushes_OnlyTheFirstTakesTheHandlerReadback()
    {
        AircraftState ac = MakeAircraft("AAL123");
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound("PUSH FACE E; PUSH $7A");
        Assert.True(parsed.IsSuccess, $"'PUSH FACE E; PUSH $7A' was refused: {parsed.Reason}");

        PilotSpeechText? result = PilotResponder.BuildReadbackAsApplied(
            parsed.Value!,
            new CommandResult(true) { PilotReadback = PushFaceEastReadback },
            ac,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );

        Assert.NotNull(result);
        Assert.Equal("push back, face east, then push to spot 7A", result!.Terminal);
    }

    /// <summary>
    /// The handler's readback replaces a clause of the command type that produced it, and only that: a push's resolved goal
    /// never lands on a <c>RES</c> ahead of it, which reads back from its own parsed form.
    /// </summary>
    [Fact]
    public void BuildReadbackAsApplied_ResThenPush_PushReadbackReplacesOnlyThePushClause()
    {
        AircraftState ac = MakeAircraft("AAL123");
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound("RES; PUSH FACE E");
        Assert.True(parsed.IsSuccess, $"'RES; PUSH FACE E' was refused: {parsed.Reason}");

        PilotSpeechText? result = PilotResponder.BuildReadbackAsApplied(
            parsed.Value!,
            new CommandResult(true) { PilotReadback = PushFaceEastReadback },
            ac,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );

        Assert.NotNull(result);
        Assert.Equal("resume taxi, then push back, face east", result!.Terminal);
    }

    /// <summary>
    /// A <c>RES</c> that pre-cleared a taxiway hold short reads back "continue taxi" in its own clause; a push ahead of it in
    /// the compound keeps its parsed form.
    /// </summary>
    [Fact]
    public void BuildReadbackAsApplied_PushThenRes_ResReadbackReplacesOnlyTheResClause()
    {
        AircraftState ac = MakeAircraft("AAL123");
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound("PUSH $7A; RES");
        Assert.True(parsed.IsSuccess, $"'PUSH $7A; RES' was refused: {parsed.Reason}");

        PilotSpeechText? result = PilotResponder.BuildReadbackAsApplied(
            parsed.Value!,
            new CommandResult(true)
            {
                PilotReadback = new HandlerReadback(
                    CanonicalCommandType.Resume,
                    new PilotSpeechText(PhraseologyVerbalizer.ContinueTaxiRelease, PhraseologyVerbalizer.ContinueTaxiRelease)
                ),
            },
            ac,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );

        Assert.NotNull(result);
        Assert.Equal("push to spot 7A, then continue taxi", result!.Terminal);
    }

    private static readonly HandlerReadback PushFaceEastReadback = new(
        CanonicalCommandType.Pushback,
        new PilotSpeechText("push back, face east", "push back, face east")
    );

    // --- A4-2: runway L/R/C suffix is never dropped from a taxi readback ---

    [Fact]
    public void BuildReadback_TaxiPreservesCenterRunwaySuffix()
    {
        AircraftState ac = MakeAircraft("N225R");
        CompoundCommand compound = Compound(new TaxiCommand(["A"], [], Expedite: false, DestinationRunway: "1C"));

        PilotSpeechText? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty);

        Assert.NotNull(result);
        Assert.Contains("1C", result!.Terminal);
        Assert.Contains("one center", result.Tts);
    }

    // --- EXT readback (issue #154 #7) ---

    [Fact]
    public void BuildReadback_ExtendOnDownwind_SaysDownwindWithRunway()
    {
        // Issue #154 #7: bare EXT used to readback as "extend upwind" no matter what leg
        // the aircraft was on, because the parser leaves Leg=null and the verbalizer
        // tied on capture count and fell to the first-declared rule.
        AircraftState ac = MakeAircraftWithAssignedRunway("N342T", "28R");
        ac.Procedure.DestinationRunway = "28R";
        ac.Phases!.Add(new DownwindPhase());
        CompoundCommand compound = Compound(new ExtendPatternCommand());

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Equal("extend downwind runway two eight right, november three four two tango.", result);
    }

    [Fact]
    public void BuildReadback_ExtendOnUpwind_SaysUpwindWithRunway()
    {
        AircraftState ac = MakeAircraftWithAssignedRunway("N342T", "28R");
        ac.Procedure.DestinationRunway = "28R";
        ac.Phases!.Add(new UpwindPhase());
        CompoundCommand compound = Compound(new ExtendPatternCommand());

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Equal("extend upwind runway two eight right, november three four two tango.", result);
    }

    [Fact]
    public void BuildReadback_ExtendOnCrosswind_SaysCrosswindWithRunway()
    {
        AircraftState ac = MakeAircraftWithAssignedRunway("N342T", "28R");
        ac.Procedure.DestinationRunway = "28R";
        ac.Phases!.Add(new CrosswindPhase());
        CompoundCommand compound = Compound(new ExtendPatternCommand());

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Equal("extend crosswind runway two eight right, november three four two tango.", result);
    }

    [Fact]
    public void BuildReadback_ExtendOnDownwind_NoDestinationRunway_DropsRunwayClause()
    {
        AircraftState ac = MakeAircraft("N342T");
        ac.Phases = new PhaseList();
        ac.Phases.Add(new DownwindPhase());
        CompoundCommand compound = Compound(new ExtendPatternCommand());

        string? result = PilotResponder.BuildReadback(compound, ac, ActiveRunways.Empty)?.Tts;

        Assert.Equal("extend downwind, november three four two tango.", result);
    }

    // --- BuildReadyToTaxi ---

    [Fact]
    public void BuildReadyToTaxi_WithGateParkingSpot_NamesTheGate()
    {
        AircraftState ac = MakeAircraft("N123AB", parkingSpot: "B22");
        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac);

        Assert.Equal("ground, at gate B22, with information Alpha, ready to taxi.", result.Terminal);
        Assert.Equal("ground, november one two three alpha bravo at gate bravo two two, with information Alpha, ready to taxi.", result.Tts);
    }

    [Fact]
    public void BuildReadyToTaxi_WithLetteredGate_SpellsTheLetter()
    {
        AircraftState ac = MakeAircraft("N123AB", parkingSpot: "F8");

        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac, "ground", "A");

        Assert.Contains("ground, at gate F8, with information Alpha", result.Terminal);
        Assert.Contains("at gate foxtrot eight", result.Tts);
    }

    [Fact]
    public void BuildReadyToTaxi_WithThreePartGate_SpellsEachDigit()
    {
        AircraftState ac = MakeAircraft("N123AB", parkingSpot: "G101");

        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac, "ground", "A");

        Assert.Contains("ground, at gate G101, with information Alpha", result.Terminal);
        Assert.Contains("at gate golf one zero one", result.Tts);
    }

    [Fact]
    public void BuildReadyToTaxi_WithNonGateSpot_SaysParking()
    {
        AircraftState ac = MakeAircraft("N123AB", parkingSpot: "GA13");

        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac, "ground", "A");

        Assert.Contains("ground, at parking GA13, with information Alpha", result.Terminal);
        Assert.Contains("at parking golf alpha one three, with information Alpha", result.Tts);
    }

    [Fact]
    public void BuildReadyToTaxi_WithWordName_TakesNoNoun()
    {
        AircraftState ac = MakeAircraft("N123AB", parkingSpot: "KILO RAMP");

        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac, "ground", "A");

        Assert.Contains("ground, at KILO RAMP, with information Alpha", result.Terminal);
        Assert.Contains("at kilo ramp, with information Alpha", result.Tts);
    }

    [Fact]
    public void BuildReadyToTaxi_WithWordThenDigitName_TakesNoNoun()
    {
        AircraftState ac = MakeAircraft("N123AB", parkingSpot: "CARGO1");

        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac, "ground", "A");

        Assert.Contains("ground, at CARGO1, with information Alpha", result.Terminal);
        Assert.Contains("at cargo one, with information Alpha", result.Tts);
    }

    [Fact]
    public void BuildReadyToTaxi_WithoutParkingSpot_FallsBackToRamp()
    {
        AircraftState ac = MakeAircraft("AAL123");
        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac);

        Assert.Contains("at the ramp", result.Terminal);
        Assert.Equal("ground, american one twenty three at the ramp, with information Alpha, ready to taxi.", result.Tts);
    }

    [Fact]
    public void BuildReadyToTaxi_TaxiwaySpawn_NamesTheTaxiway()
    {
        AircraftState ac = MakeAircraft("N123AB");
        ac.Ground.SpawnTaxiway = "K";

        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac, "ground", "A");

        Assert.Equal("ground, on taxiway K, with information Alpha, ready to taxi.", result.Terminal);
        Assert.Equal("ground, november one two three alpha bravo on taxiway kilo, with information Alpha, ready to taxi.", result.Tts);
    }

    [Fact]
    public void BuildReadyToTaxi_TaxiwaySpawnBoundForThisField_RequestsTaxiToParking()
    {
        AircraftState ac = MakeAircraft("N123AB", isVfr: true);
        ac.Ground.SpawnTaxiway = "K";
        ac.Ground.LayoutAirportId = "KLVK";
        ac.FlightPlan.Destination = "KLVK";

        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac, "ground", "A");

        Assert.Equal("ground, on taxiway K, with information Alpha, request taxi to parking.", result.Terminal);
        Assert.Equal("ground, november one two three alpha bravo on taxiway kilo, with information Alpha, request taxi to parking.", result.Tts);
    }

    [Fact]
    public void BuildClearanceRequest_Ifr_NamesTheDestination_NoTypeNoRequestClearance()
    {
        AircraftState ac = MakeAircraft("SWA2099");
        ac.FlightPlan.Destination = "KLAX";

        PilotSpeechText result = PilotResponder.BuildClearanceRequest(ac, "Oakland Clearance", "A", ReadyToTaxiLocation.Stand("F8"), null);

        Assert.Equal("Oakland Clearance, at gate F8, with information Alpha, IFR to Los Angeles Airport.", result.Terminal);
        Assert.Equal(
            "Oakland Clearance, southwest twenty ninety nine, at gate foxtrot eight, with information Alpha, IFR to Los Angeles Airport.",
            result.Tts
        );
    }

    [Fact]
    public void BuildClearanceRequest_IfrHeavy_SaysHeavyAfterTheCallsign()
    {
        AircraftState ac = MakeAircraft("UAL1");
        ac.AircraftType = "B77W";
        ac.FlightPlan.Destination = "KLAX";

        PilotSpeechText result = PilotResponder.BuildClearanceRequest(ac, "clearance", "A", ReadyToTaxiLocation.Stand("F8"), null);

        Assert.Equal("clearance, at gate F8, with information Alpha, IFR to Los Angeles Airport.", result.Terminal);
        Assert.StartsWith("clearance, united one heavy, at gate", result.Tts, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildClearanceRequest_Vfr_NamesTheDirectionAndAltitude_NeverTheDestination()
    {
        AircraftState ac = MakeAircraft("N123AB", isVfr: true);
        ac.FlightPlan.Destination = "KSAC";
        ac.FlightPlan.Altitude = new PlannedAltitude(4500, null, true, false, false);

        PilotSpeechText result = PilotResponder.BuildClearanceRequest(ac, "Oakland Clearance", "A", ReadyToTaxiLocation.Stand("F8"), "north");

        Assert.Equal("Oakland Clearance, at gate F8, with information Alpha, VFR departure to the north, at 4500.", result.Terminal);
        Assert.Equal(
            "Oakland Clearance, november one two three alpha bravo, at gate foxtrot eight, with information Alpha, VFR departure to the north, at four thousand five hundred.",
            result.Tts
        );
    }

    [Fact]
    public void BuildClearanceRequest_VfrWithNoFiledAltitude_LeavesTheAltitudeOut()
    {
        AircraftState ac = MakeAircraft("N123AB", isVfr: true);

        PilotSpeechText result = PilotResponder.BuildClearanceRequest(ac, "clearance", "A", ReadyToTaxiLocation.PushedBackFrom("F8"), "west");

        Assert.Equal("clearance, pushed back from gate F8, with information Alpha, VFR departure to the west.", result.Terminal);
    }

    [Fact]
    public void BuildReleaseRequest_NamesTheRunwayAndTheField()
    {
        AircraftState ac = MakeAircraft("N513SJ");
        string auburn = PhraseologyVerbalizer.SpellAirportName("AUN");

        PilotSpeechText result = PilotResponder.BuildReleaseRequest(ac, "25", "AUN", "NorCal Approach");

        Assert.StartsWith("Auburn", auburn, StringComparison.Ordinal);
        Assert.Equal($"NorCal Approach, runway 25 at {auburn}, ready for departure, request release.", result.Terminal);
        Assert.Equal(
            $"NorCal Approach, november five one three sierra juliett, runway two five at {auburn}, ready for departure, request release.",
            result.Tts
        );
    }

    [Fact]
    public void BuildReadyToTaxi_AfterAPush_SaysPushedBackFromTheGate()
    {
        AircraftState ac = MakeAircraft("N123AB");

        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac, "ground", "A", ReadyToTaxiLocation.PushedBackFrom("F8"));

        Assert.Equal("ground, pushed back from gate F8, with information Alpha, ready to taxi.", result.Terminal);
        Assert.Equal(
            "ground, november one two three alpha bravo pushed back from gate foxtrot eight, with information Alpha, ready to taxi.",
            result.Tts
        );
    }

    [Fact]
    public void BuildReadyToTaxi_AtASpot_TakesTheNounSpot()
    {
        AircraftState ac = MakeAircraft("N123AB");

        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac, "ground", "A", ReadyToTaxiLocation.Spot("9"));

        Assert.Equal("ground, at spot 9, with information Alpha, ready to taxi.", result.Terminal);
        Assert.Equal("ground, november one two three alpha bravo at spot nine, with information Alpha, ready to taxi.", result.Tts);
    }

    [Fact]
    public void BuildReadyToTaxi_AtASpotNamedSpot_DropsTheRepeatedNoun()
    {
        AircraftState ac = MakeAircraft("N123AB");

        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac, "ground", "A", ReadyToTaxiLocation.Spot("SPOT7"));

        Assert.Equal("ground, at spot 7, with information Alpha, ready to taxi.", result.Terminal);
        Assert.Equal("ground, november one two three alpha bravo at spot seven, with information Alpha, ready to taxi.", result.Tts);
    }

    [Fact]
    public void BuildReadyToTaxi_AtATaxiwayHoldShort_UsesTheHoldShortLocative()
    {
        AircraftState ac = MakeAircraft("N123AB");

        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac, "ground", "A", ReadyToTaxiLocation.HoldingShort("C", "T41W"));

        Assert.Equal("ground, holding short of C at T41W, with information Alpha, ready to taxi.", result.Terminal);
        Assert.Equal(
            "ground, november one two three alpha bravo holding short of charlie at tango four one whiskey, with information Alpha, ready to taxi.",
            result.Tts
        );
    }

    [Fact]
    public void BuildReadyToTaxi_WithRadioName_AddressesFacility()
    {
        AircraftState ac = MakeAircraft("N123AB", parkingSpot: "B22");
        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac, "Oakland Ground", "A");

        Assert.Equal("Oakland Ground, at gate B22, with information Alpha, ready to taxi.", result.Terminal);
        Assert.Equal("Oakland Ground, november one two three alpha bravo at gate bravo two two, with information Alpha, ready to taxi.", result.Tts);
    }

    // --- BuildHoldingShortReady ---

    [Fact]
    public void BuildHoldingShortReady_FormatsRunwaySpoken()
    {
        AircraftState ac = MakeAircraft("N123AB");
        PilotSpeechText result = PilotResponder.BuildHoldingShortReady(ac, "28R");

        Assert.Equal("tower, november one two three alpha bravo holding short runway two eight right, ready for departure.", result.Tts);
    }

    [Fact]
    public void BuildHoldingShortReady_AirlineCallsign_UsesTelephony()
    {
        AircraftState ac = MakeAircraft("AAL123");
        PilotSpeechText result = PilotResponder.BuildHoldingShortReady(ac, "9L");

        Assert.Contains("american one twenty three holding short runway nine left", result.Tts);
    }

    [Fact]
    public void BuildHoldingShortReady_WithRadioName_AddressesFacility()
    {
        AircraftState ac = MakeAircraft("N123AB");
        PilotSpeechText result = PilotResponder.BuildHoldingShortReady(ac, "28R", "Oakland Tower");

        Assert.Equal("Oakland Tower, november one two three alpha bravo holding short runway two eight right, ready for departure.", result.Tts);
    }

    // --- BuildLinedUpReady ---

    [Fact]
    public void BuildLinedUpReady_FormatsRunwaySpoken()
    {
        AircraftState ac = MakeAircraft("N123AB");
        PilotSpeechText result = PilotResponder.BuildLinedUpReady(ac, "28R");

        Assert.Equal("tower, november one two three alpha bravo runway two eight right, ready.", result.Tts);
    }

    [Fact]
    public void BuildLinedUpReady_WithRadioName_AddressesFacility()
    {
        AircraftState ac = MakeAircraft("N123AB");
        PilotSpeechText result = PilotResponder.BuildLinedUpReady(ac, "28R", "Oakland Tower");

        Assert.Equal("Oakland Tower, november one two three alpha bravo runway two eight right, ready.", result.Tts);
    }

    // --- BuildOnFinal ---

    [Fact]
    public void BuildOnFinal_IfrWithIlsApproach_SpellsIlsBeforeRunway()
    {
        AircraftState ac = MakeAircraft("AAL123");
        PilotSpeechText result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: true, approachId: "I28R", distanceMilesForVfr: 0);

        Assert.Equal("tower, american one twenty three, ILS two eight right.", result.Tts);
    }

    [Fact]
    public void BuildOnFinal_IfrWithRnavApproachAndSuffix_IncludesSuffixLetter()
    {
        AircraftState ac = MakeAircraft("AAL123");
        PilotSpeechText result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: true, approachId: "R28R-Y", distanceMilesForVfr: 0);

        Assert.Equal("tower, american one twenty three, RNAV Yankee two eight right.", result.Tts);
    }

    [Fact]
    public void BuildOnFinal_IfrWithGpsApproach_NamesGps()
    {
        AircraftState ac = MakeAircraft("AAL123");
        PilotSpeechText result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: true, approachId: "P28R", distanceMilesForVfr: 0);

        Assert.Equal("tower, american one twenty three, GPS two eight right.", result.Tts);
    }

    [Fact]
    public void BuildOnFinal_IfrWithLocalizerApproach_TerminalSaysLoc()
    {
        AircraftState ac = MakeAircraft("AAL123");
        PilotSpeechText result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: true, approachId: "L28R", distanceMilesForVfr: 0);

        Assert.Equal("tower, LOC 28R.", result.Terminal);
    }

    [Fact]
    public void BuildOnFinal_IfrWithVisualApproach_UsesExpandedPhrasing()
    {
        AircraftState ac = MakeAircraft("AAL123");
        PilotSpeechText result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: true, approachId: "VIS28R", distanceMilesForVfr: 0);

        Assert.Equal("tower, american one twenty three, visual approach runway two eight right.", result.Tts);
    }

    [Fact]
    public void BuildOnFinal_VfrNoApproach_ReportsDistanceAndAtis()
    {
        AircraftState ac = MakeAircraft("N123AB", isVfr: true);
        PilotSpeechText result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: false, approachId: null, distanceMilesForVfr: 3);

        Assert.Equal("tower, november one two three alpha bravo three-mile final runway two eight right, with information Alpha.", result.Tts);
    }

    [Fact]
    public void BuildOnFinal_WithRadioName_AddressesFacility()
    {
        AircraftState ac = MakeAircraft("N123AB", isVfr: true);
        PilotSpeechText result = PilotResponder.BuildOnFinal(
            ac,
            "28R",
            ifrWithActiveApproach: false,
            approachId: null,
            distanceMilesForVfr: 3,
            "Oakland Tower",
            "A"
        );

        Assert.Equal(
            "Oakland Tower, november one two three alpha bravo three-mile final runway two eight right, with information Alpha.",
            result.Tts
        );
    }

    [Fact]
    public void BuildOnFinal_VfrUnderOneMile_ClampsToOneMile()
    {
        AircraftState ac = MakeAircraft("N123AB", isVfr: true);
        PilotSpeechText result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: false, approachId: null, distanceMilesForVfr: 0);

        Assert.Contains("one-mile final", result.Tts);
    }

    [Fact]
    public void BuildOnFinal_IfrNoApproach_FallsBackToVfrTemplate()
    {
        AircraftState ac = MakeAircraft("AAL123");
        PilotSpeechText result = PilotResponder.BuildOnFinal(ac, "9", ifrWithActiveApproach: false, approachId: null, distanceMilesForVfr: 5);

        Assert.Equal("tower, american one twenty three five-mile final runway nine, with information Alpha.", result.Tts);
    }

    // --- BuildClosedTrafficRequest ---

    [Fact]
    public void BuildClosedTrafficRequest_VfrThreeMilesSouth_FormatsCanonical()
    {
        var airport = new LatLon(37.7212, -122.2208);
        // 3 nm south at 1500 ft.
        AircraftState ac = MakeAircraft("N123AB", isVfr: true);
        ac.Position = GeoMath.ProjectPoint(airport, new TrueHeading(180), 3);

        PilotSpeechText result = PilotResponder.BuildClosedTrafficRequest(ac, airport, altitudeFt: 1500);

        Assert.Equal(
            "tower, november one two three alpha bravo, three miles south at one thousand five hundred, request closed traffic, with information Alpha.",
            result.Tts
        );
    }

    [Fact]
    public void BuildClosedTrafficRequest_AirlineCallsign_UsesTelephony()
    {
        var airport = new LatLon(37.7212, -122.2208);
        AircraftState ac = MakeAircraft("AAL123", isVfr: true);
        ac.Position = GeoMath.ProjectPoint(airport, new TrueHeading(90), 5);

        PilotSpeechText result = PilotResponder.BuildClosedTrafficRequest(ac, airport, altitudeFt: 2000);

        Assert.Equal(
            "tower, american one twenty three, five miles east at two thousand, request closed traffic, with information Alpha.",
            result.Tts
        );
    }

    [Fact]
    public void BuildClosedTrafficRequest_WithRadioName_AddressesFacility()
    {
        var airport = new LatLon(37.7212, -122.2208);
        AircraftState ac = MakeAircraft("N123AB", isVfr: true);
        ac.Position = GeoMath.ProjectPoint(airport, new TrueHeading(180), 3);

        PilotSpeechText result = PilotResponder.BuildClosedTrafficRequest(ac, airport, altitudeFt: 1500, "Oakland Tower", "A");

        Assert.Equal(
            "Oakland Tower, november one two three alpha bravo, three miles south at one thousand five hundred, request closed traffic, with information Alpha.",
            result.Tts
        );
    }

    // --- BuildContactReadback ---

    [Fact]
    public void BuildContactReadback_FormatsFacilityFreqAndSignoff()
    {
        AircraftState ac = MakeAircraft("N123AB");

        PilotSpeechText result = PilotResponder.BuildContactReadback(ac, "Approach", 125.35, PilotSpeechContext.Canonical);

        // Facility name preserves the caller's casing — sentence-initial after the bracket
        // strip in CompactForTerminal, so capitalization matters for the terminal display.
        Assert.Equal("Approach on one two five point three five, november one two three alpha bravo, so long.", result.Tts);
    }

    [Fact]
    public void BuildContactReadback_AirlineCallsign_UsesTelephony()
    {
        AircraftState ac = MakeAircraft("AAL123");

        PilotSpeechText result = PilotResponder.BuildContactReadback(ac, "Departure", 119.6, PilotSpeechContext.Canonical);

        Assert.Equal("Departure on one one nine point six, american one twenty three, so long.", result.Tts);
    }

    [Fact]
    public void BuildContactReadback_PreservesFacilityNameCasing()
    {
        AircraftState ac = MakeAircraft("N123AB");

        // Caller may pass a Position.RadioName like "NorCal Approach" or "Oakland Tower" —
        // multi-word natural casing must reach the terminal and TTS unchanged.
        PilotSpeechText result = PilotResponder.BuildContactReadback(ac, "NorCal Approach", 125.35, PilotSpeechContext.Canonical);

        Assert.Contains("NorCal Approach on", result.Tts);
    }

    [Fact]
    public void BuildContactReadback_GroupedFrequency_SpeaksItWithoutTheLeadingOne()
    {
        AircraftState ac = MakeAircraft("N123AB");
        var busy = new PilotSpeechContext(PilotPersonality.Varied, FrequencyLoadBand.Busy, 1, 0)
        {
            ShortenCallsign = false,
            GroupContactFrequency = true,
            CurrentIndicatedAirspeed = null,
        };

        PilotSpeechText result = PilotResponder.BuildContactReadback(ac, "Approach", 125.35, busy);

        Assert.Equal("Approach on twenty five thirty five, november one two three alpha bravo, so long.", result.Tts);
        Assert.Equal("Approach on 125.35, so long.", result.Terminal);
    }

    [Fact]
    public void BuildContactReadback_NotGrouped_KeepsTheDigitForm()
    {
        AircraftState ac = MakeAircraft("N123AB");
        var busy = new PilotSpeechContext(PilotPersonality.Varied, FrequencyLoadBand.Busy, 1, 0)
        {
            ShortenCallsign = false,
            GroupContactFrequency = false,
            CurrentIndicatedAirspeed = null,
        };

        PilotSpeechText result = PilotResponder.BuildContactReadback(ac, "Approach", 125.35, busy);

        Assert.Equal("Approach on one two five point three five, november one two three alpha bravo, so long.", result.Tts);
        Assert.Equal("Approach on 125.35, so long.", result.Terminal);
    }

    [Fact]
    public void BuildReadback_ShortenCallsign_EndsWithTheAbbreviatedNNumber()
    {
        AircraftState ac = MakeAircraft("N123AB");
        CompoundCommand compound = Compound(new DescendMaintainCommand(5000));

        PilotSpeechText full = PilotResponder.BuildReadback(compound, ac, PilotSpeechContext.Canonical, ActiveRunways.Empty)!;
        PilotSpeechText shortened = PilotResponder.BuildReadback(
            compound,
            ac,
            PilotSpeechContext.Canonical with
            {
                ShortenCallsign = true,
            },
            ActiveRunways.Empty
        )!;

        Assert.EndsWith(", november one two three alpha bravo.", full.Tts);
        Assert.EndsWith(", november three alpha bravo.", shortened.Tts);
        Assert.Equal(full.Terminal, shortened.Terminal);
    }

    [Fact]
    public void WithBravoClearance_ShortenedCallsign_KeepsTheCallsignLast()
    {
        AircraftState ac = MakeAircraft("N123AB");
        PilotSpeechContext shortened = PilotSpeechContext.Canonical with { ShortenCallsign = true };
        PilotSpeechText readback = PilotResponder.BuildReadback(Compound(new DescendMaintainCommand(5000)), ac, shortened, ActiveRunways.Empty)!;

        PilotSpeechText result = PilotResponder.WithBravoClearance(ac, readback, BravoClearanceWording.Into, shortened);

        Assert.EndsWith(", cleared into the bravo, november three alpha bravo.", result.Tts);
    }

    // --- BuildFrequencyChangeApproved ---

    [Fact]
    public void BuildFrequencyChangeApproved_PilotSignsOff_DoesNotParrotControllerPhrase()
    {
        // Per AIM 4-2-3 ¶3, pilots acknowledge with a sign-off, not a verbatim recital of
        // "frequency change approved" (which is the controller's phraseology in 7110.65 §7-6-11).
        AircraftState ac = MakeAircraft("N123AB", isVfr: true);

        PilotSpeechText result = PilotResponder.BuildFrequencyChangeApproved(ac, PilotSpeechContext.Canonical);

        Assert.Equal("november one two three alpha bravo, good day.", result.Tts);
        Assert.DoesNotContain("frequency change approved", result.Tts);
    }

    [Fact]
    public void BuildFrequencyChangeApproved_AirlineCallsign_UsesTelephony()
    {
        AircraftState ac = MakeAircraft("AAL123");

        PilotSpeechText result = PilotResponder.BuildFrequencyChangeApproved(ac, PilotSpeechContext.Canonical);

        Assert.Equal("american one twenty three, good day.", result.Tts);
    }

    // --- BuildMidfieldDownwindReminder ---

    [Fact]
    public void BuildMidfieldDownwindReminder_FormatsRunwaySpoken()
    {
        AircraftState ac = MakeAircraft("N123AB", isVfr: true);
        PilotSpeechText result = PilotResponder.BuildMidfieldDownwindReminder(ac, "28R");

        Assert.Equal("november one two three alpha bravo, midfield downwind runway two eight right.", result.Tts);
    }

    [Fact]
    public void BuildMidfieldDownwindReminder_AirlineCallsign_UsesTelephony()
    {
        AircraftState ac = MakeAircraft("AAL123", isVfr: true);
        PilotSpeechText result = PilotResponder.BuildMidfieldDownwindReminder(ac, "9L");

        Assert.Equal("american one twenty three, midfield downwind runway nine left.", result.Tts);
    }

    // --- BuildShortFinalReminder ---

    [Fact]
    public void BuildShortFinalReminder_FormatsRunwaySpoken()
    {
        AircraftState ac = MakeAircraft("N123AB", isVfr: true);
        PilotSpeechText result = PilotResponder.BuildShortFinalReminder(ac, "28R");

        Assert.Equal("november one two three alpha bravo, short final runway two eight right.", result.Tts);
    }

    [Fact]
    public void BuildShortFinalReminder_NoSuffixRunway()
    {
        AircraftState ac = MakeAircraft("N123AB", isVfr: true);
        PilotSpeechText result = PilotResponder.BuildShortFinalReminder(ac, "9");

        Assert.Equal("november one two three alpha bravo, short final runway nine.", result.Tts);
    }

    // --- BuildTrafficInSight ---

    [Fact]
    public void BuildTrafficInSight_WithTargetCallsign_TtsOmitsTargetCallsign()
    {
        AircraftState ac = MakeAircraft("N294MG");
        PilotSpeechText result = PilotResponder.BuildTrafficInSight(ac, "N784ME");

        // The solo terminal and the spoken form omit the target callsign (the pilot acquired the
        // traffic by position/type, not by callsign); only the RPO diagnostic names it.
        Assert.Equal("traffic in sight.", result.Terminal);
        Assert.Equal("traffic (N784ME) in sight.", result.TerminalForRpo);
        Assert.Equal("november two nine four mike golf, traffic in sight.", result.Tts);
        Assert.DoesNotContain("seven eight four", result.Tts);
    }

    [Fact]
    public void BuildTrafficInSight_NoTargetCallsign_OmitsTargetClause()
    {
        AircraftState ac = MakeAircraft("N123AB");
        PilotSpeechText result = PilotResponder.BuildTrafficInSight(ac, null);

        Assert.Equal("traffic in sight.", result.Terminal);
        Assert.Equal("november one two three alpha bravo, traffic in sight.", result.Tts);
    }

    [Fact]
    public void BuildFieldInSight_FormatsCallsign()
    {
        AircraftState ac = MakeAircraft("UAL238");
        PilotSpeechText result = PilotResponder.BuildFieldInSight(ac);

        Assert.Equal("field in sight.", result.Terminal);
        Assert.Equal("united two thirty eight, field in sight.", result.Tts);
    }

    [Fact]
    public void BuildLostSightOfField_SaysLostSightOfField()
    {
        // "Negative contact" (PCG) means traffic never acquired / radio-contact failure — not the
        // loss of a previously-acquired visual. The pilot has lost sight of the field, so the
        // phraseology is "lost sight of the field".
        AircraftState ac = MakeAircraft("N172SP");
        PilotSpeechText result = PilotResponder.BuildLostSightOfField(ac);

        Assert.Equal("lost sight of the field.", result.Terminal);
        Assert.Contains("lost sight of the field", result.Tts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("negative contact", result.Tts, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("november one seven two sierra papa,", result.Tts);
    }

    [Fact]
    public void BuildLostSightOfTraffic_TtsAndSoloTerminalOmitTarget()
    {
        AircraftState ac = MakeAircraft("N172SP");
        PilotSpeechText result = PilotResponder.BuildLostSightOfTraffic(ac, "N784ME");

        Assert.Equal("lost sight of the traffic.", result.Terminal);
        Assert.Equal("lost sight of N784ME.", result.TerminalForRpo);
        Assert.Contains("lost sight of the traffic", result.Tts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("negative contact", result.Tts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("seven eight four", result.Tts);
    }

    // --- A3-4: fix-passage report uses the spoken/display name, not the raw fix id ---

    [Fact]
    public void BuildAtFixReport_UsesDisplayNameInTerminalAndSpokenNameInTts()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        AircraftState ac = MakeAircraft("N123AB");
        string displayName = PhraseologyVerbalizer.FixDisplayText("VPCBT");
        // Guard: this visual fix has a human display name distinct from the raw alias, so the
        // assertions below are meaningful (not a vacuous "VPCBT" == "VPCBT").
        Assert.NotEqual("VPCBT", displayName);

        PilotSpeechText result = PilotResponder.BuildAtFixReport(ac, "VPCBT");

        Assert.Equal($"passing {displayName}.", result.Terminal);
        Assert.EndsWith($"passing {PhraseologyVerbalizer.SpellFix("VPCBT")}.", result.Tts);
        Assert.DoesNotContain("VPCBT", result.Tts);
    }

    [Fact]
    public void BuildGoingAround_ReasonIsTerminalOnly_NotSpoken()
    {
        // The reason is a sim-internal diagnostic — it belongs in the controller-facing terminal
        // line, never in the spoken callout (the pilot just says "going around").
        AircraftState ac = MakeAircraft("FDX3807");
        PilotSpeechText result = PilotResponder.BuildGoingAround(ac, "no landing clearance");

        Assert.Contains("(no landing clearance)", result.Terminal);
        Assert.EndsWith("going around.", result.Tts);
        Assert.DoesNotContain("no landing clearance", result.Tts);
    }

    [Fact]
    public void BuildGoingAround_EmptyReason_OmitsParenthetical()
    {
        AircraftState ac = MakeAircraft("FDX3807");
        PilotSpeechText result = PilotResponder.BuildGoingAround(ac, "");

        Assert.EndsWith("going around.", result.Tts);
        Assert.DoesNotContain("()", result.Tts);
    }

    // --- A1-3: arrival approach reminder is a callsign-only prompt for an approach assignment; the
    //          pilot names neither runway nor approach type (the controller assigns both) ---

    [Fact]
    public void BuildArrivalApproachRequest_IsCallsignOnlyApproachAssignmentPrompt()
    {
        AircraftState ac = MakeAircraft("UAL325");

        PilotSpeechText result = PilotResponder.BuildArrivalApproachRequest(ac);

        Assert.Equal("request approach assignment.", result.Terminal);
        Assert.Equal("request approach assignment, united three twenty five.", result.Tts);
        Assert.DoesNotContain("runway", result.Tts);
        Assert.DoesNotContain("to land", result.Tts);
    }

    // --- A1-4: closed-traffic check-in rounds altitude to the nearest 100 ft ---

    [Fact]
    public void BuildClosedTrafficRequest_RoundsAltitudeToNearestHundred()
    {
        var airport = new LatLon(37.7212, -122.2208);
        AircraftState ac = MakeAircraft("N123AB", isVfr: true);
        ac.Position = GeoMath.ProjectPoint(airport, new TrueHeading(180), 3);

        PilotSpeechText result = PilotResponder.BuildClosedTrafficRequest(ac, airport, altitudeFt: 1487, "tower", "A");

        Assert.Contains("one thousand five hundred", result.Tts);
        Assert.DoesNotContain("eighty", result.Tts);
    }

    // --- A6-2: ready-to-taxi states op-type + destination (AIM 4-2-3) ---

    [Fact]
    public void BuildReadyToTaxi_IfrWithDestination_StatesOpTypeAndDestination()
    {
        AircraftState ac = MakeAircraft("AAL123");
        ac.FlightPlan.Destination = "KSFO";

        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac, "ground", "A");

        Assert.Contains(", IFR to ", result.Tts);
        Assert.EndsWith(", ready to taxi.", result.Tts);
    }

    [Fact]
    public void BuildReadyToTaxi_VfrLocalNoDestination_OmitsIntentClause()
    {
        AircraftState ac = MakeAircraft("N123AB", isVfr: true);

        PilotSpeechText result = PilotResponder.BuildReadyToTaxi(ac, "ground", "A");

        Assert.DoesNotContain("VFR to", result.Tts);
        Assert.DoesNotContain("IFR to", result.Tts);
        Assert.EndsWith("ready to taxi.", result.Tts);
    }

    // --- A6-1: taxi hold-short report keeps the "holding short of" verb ---

    [Fact]
    public void BuildHoldingShortTaxi_KeepsHoldingShortOfVerb()
    {
        AircraftState ac = MakeAircraft("N172SP");

        PilotSpeechText result = PilotResponder.BuildHoldingShortTaxi(ac, "holding short of 28R", "B", requestFurtherTaxi: false);

        Assert.Contains("holding short of 28R at B", result.Tts);
        Assert.DoesNotContain("request further taxi", result.Tts);
    }

    [Fact]
    public void BuildHoldingShortTaxi_RouteIncomplete_RequestsFurtherTaxi()
    {
        AircraftState ac = MakeAircraft("N9225L");

        PilotSpeechText result = PilotResponder.BuildHoldingShortTaxi(ac, "holding short of C", "E", requestFurtherTaxi: true);

        Assert.Equal("holding short of C at E, request further taxi.", result.Terminal);
        Assert.EndsWith(", holding short of C at E, request further taxi.", result.Tts);
    }

    [Fact]
    public void BuildHoldingShortCrossing_FormatsRunway()
    {
        AircraftState ac = MakeAircraft("N172SP");
        PilotSpeechText result = PilotResponder.BuildHoldingShortCrossing(ac, "28R");

        Assert.Contains("holding short runway two eight right", result.Tts);
    }

    [Fact]
    public void BuildClearOfRunway_TerminalUsesIdentifierAndTtsUsesSpoken()
    {
        AircraftState ac = MakeAircraft("N569SX");

        PilotSpeechText text = PilotResponder.BuildClearOfRunwayText(ac, "28R", "G");

        // Terminal form: digit identifier, no callsign (SAY column carries it).
        Assert.Equal("clear of runway 28R at G.", text.Terminal);
        // TTS form: spelled-out callsign + runway, AIM phraseology (NATO X = "xray", no hyphen).
        Assert.Equal("november five six nine sierra xray, clear of runway two eight right at G.", text.Tts);
    }

    [Fact]
    public void BuildUnableToExit_SaysUnableAndTheTaxiway()
    {
        AircraftState ac = MakeAircraft("N123AB");
        PilotSpeechText result = PilotResponder.BuildUnableToExit(ac, "M2");

        Assert.Equal("unable M2.", result.Terminal);
        Assert.EndsWith(", unable mike two.", result.Tts, StringComparison.Ordinal);
    }

    /// <summary>
    /// The terminal line is what <see cref="PilotResponder.BuildUnable"/> makes of the controller-facing "Unable, no M2 ahead",
    /// so the instructor reads the same text either way; only the spoken form spells the taxiway (AIM 4-2-7).
    /// </summary>
    [Fact]
    public void BuildUnableNoExitAhead_KeepsTheTerminalLine_AndSpellsTheTaxiway()
    {
        AircraftState ac = MakeAircraft("N123AB");
        PilotSpeechText result = PilotResponder.BuildUnableNoExitAhead(ac, "M2");

        Assert.Equal(PilotResponder.BuildUnable(ac, "Unable, no M2 ahead").Terminal, result.Terminal);
        Assert.Equal("unable, no M2 ahead.", result.Terminal);
        Assert.EndsWith(", unable, no mike two ahead.", result.Tts, StringComparison.Ordinal);
    }

    /// <summary>
    /// A jet's refusal to turn about on a taxiway: the terminal line is what <see cref="PilotResponder.BuildUnable"/> makes of
    /// the controller-facing reason, and the spoken form spells the taxiway, so text-to-speech says "charlie", not "C".
    /// </summary>
    [Fact]
    public void BuildUnableNoRoomToTurnAround_KeepsTheTerminalLine_AndSpellsTheTaxiway()
    {
        AircraftState ac = MakeAircraft("N123AB");
        PilotSpeechText result = PilotResponder.BuildUnableNoRoomToTurnAround(ac, "C");

        Assert.Equal(PilotResponder.BuildUnable(ac, GroundCommandHandler.NoRoomToTurnAroundReason("C")).Terminal, result.Terminal);
        Assert.EndsWith(", unable, no room to turn around on charlie, request a route ahead.", result.Tts, StringComparison.Ordinal);
    }

    /// <summary>
    /// YAAT-509: an aircraft whose turn about has no room, standing on no taxiway, leaves the "on" clause out: it asks for a
    /// route ahead with nothing to name.
    /// </summary>
    [Fact]
    public void BuildUnableNoRoomToTurnAround_NoTaxiway_LeavesOutTheOnClause()
    {
        AircraftState ac = MakeAircraft("N123AB");
        PilotSpeechText result = PilotResponder.BuildUnableNoRoomToTurnAround(ac, null);

        Assert.Equal("unable, no room to turn around, request a route ahead.", result.Terminal);
        Assert.EndsWith(", unable, no room to turn around, request a route ahead.", result.Tts, StringComparison.Ordinal);
    }

    /// <summary>
    /// YAAT-509: an aircraft whose turn about would carry a part past a runway's hold line holds short of the runway and says so;
    /// the spoken form spells the taxiway and the runway. Standing on no taxiway, it leaves the "on" clause out.
    /// </summary>
    [Theory]
    [InlineData(
        "C",
        "28R",
        "unable, no room to turn around on C, holding short of runway 28R.",
        ", unable, no room to turn around on charlie, holding short of runway two eight right."
    )]
    [InlineData(
        null,
        "1L",
        "unable, no room to turn around, holding short of runway 1L.",
        ", unable, no room to turn around, holding short of runway one left."
    )]
    public void BuildUnableNoRoomToTurnAroundAtRunway_NamesTheRunway_AndSpellsItSpoken(
        string? taxiway,
        string runway,
        string terminal,
        string ttsTail
    )
    {
        AircraftState ac = MakeAircraft("N123AB");
        PilotSpeechText result = PilotResponder.BuildUnableNoRoomToTurnAroundAtRunway(ac, taxiway, runway);

        Assert.Equal(terminal, result.Terminal);
        Assert.EndsWith(ttsTail, result.Tts, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejection reasons are authored as controller-facing text, where "Unable — reason" is natural
    /// typography. The leading-token strip has to treat en/em dashes like the ASCII hyphen it already
    /// handles, or the dash survives into the transmission: "unable, — already turning off at G".
    /// </summary>
    [Theory]
    [InlineData("Unable — already turning off at G", "unable, already turning off at G.")]
    [InlineData("Unable – no G ahead", "unable, no G ahead.")]
    [InlineData("Unable - runway-clearance geometry unavailable", "unable, runway-clearance geometry unavailable.")]
    [InlineData("Unable, no left exit ahead", "unable, no left exit ahead.")]
    [InlineData("Unable: traffic on the runway", "unable, traffic on the runway.")]
    public void BuildUnable_StripsTheLeadingTokenAndAnyDashAfterIt(string reason, string expectedTerminal)
    {
        AircraftState ac = MakeAircraft("N123AB");
        PilotSpeechText result = PilotResponder.BuildUnable(ac, reason);

        Assert.Equal(expectedTerminal, result.Terminal);
        Assert.DoesNotContain("—", result.Tts, StringComparison.Ordinal);
        Assert.DoesNotContain("–", result.Tts, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUnableToMaintainSeparation_RpoTerminalNamesLead_TtsDoesNot()
    {
        AircraftState ac = MakeAircraft("N294MG");
        PilotSpeechText result = PilotResponder.BuildUnableToMaintainSeparation(ac, "N10194");

        Assert.Equal("unable to maintain separation, breaking off the follow.", result.Terminal);
        Assert.Equal("unable to maintain separation from N10194, breaking off the follow.", result.TerminalForRpo);
        Assert.Contains("unable to maintain separation", result.Tts);
        Assert.DoesNotContain("november one zero one nine four", result.Tts);
    }

    [Fact]
    public void BuildUnableNoRouteToFollow_RpoTerminalNamesLead_TtsDoesNot()
    {
        AircraftState ac = MakeAircraft("N294MG");
        PilotSpeechText result = PilotResponder.BuildUnableNoRouteToFollow(ac, "N10194");

        Assert.Equal("Unable to follow traffic, no taxi route to its path, request taxi instructions", result.Terminal);
        Assert.Equal("Unable to follow N10194, no taxi route to its path, request taxi instructions", result.TerminalForRpo);
        Assert.EndsWith(", unable, no route to follow traffic, request taxi.", result.Tts, StringComparison.Ordinal);
        Assert.DoesNotContain("november one zero one nine four", result.Tts, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUnableRunwayNotOnRoute_TerminalNamesTheRunway_TtsSpellsIt()
    {
        AircraftState ac = MakeAircraft("N294MG");
        PilotSpeechText result = PilotResponder.BuildUnableRunwayNotOnRoute(ac, "28L");

        Assert.Equal("Unable, runway 28L is not on our route, request taxi instructions", result.Terminal);
        Assert.Equal(result.Terminal, result.TerminalForRpo);
        Assert.Equal("november two nine four mike golf, unable, runway two eight left isn't on our route, request taxi.", result.Tts);
    }

    [Fact]
    public void BuildHoldingShortNotClear_TerminalCompactsAndTtsSpells()
    {
        AircraftState ac = MakeAircraft("N294MG");
        PilotSpeechText result = PilotResponder.BuildHoldingShortNotClear(ac, "28R", "10L");

        Assert.Equal("Holding short of runway 28R, not clear of runway 10L", result.Terminal);
        Assert.Equal(result.Terminal, result.TerminalForRpo);
        Assert.EndsWith("holding short of runway two eight right, not clear of one zero left.", result.Tts, StringComparison.Ordinal);
        Assert.Contains("november two nine four mike golf", result.Tts, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHoldingPositionNotClear_TerminalCompactsAndTtsSpells()
    {
        AircraftState ac = MakeAircraft("N294MG");
        PilotSpeechText result = PilotResponder.BuildHoldingPositionNotClear(ac, "10L");

        Assert.Equal("Holding position, not clear of runway 10L", result.Terminal);
        Assert.Equal(result.Terminal, result.TerminalForRpo);
        Assert.EndsWith("holding position, not clear of one zero left.", result.Tts, StringComparison.Ordinal);
        Assert.Contains("november two nine four mike golf", result.Tts, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHoldingPositionNotClearTrafficAhead_AddsTheReasonToTheNotClearCall()
    {
        AircraftState ac = MakeAircraft("N294MG");
        PilotSpeechText result = PilotResponder.BuildHoldingPositionNotClearTrafficAhead(ac, "28L");

        Assert.Equal("Holding position, not clear of runway 28L, traffic ahead", result.Terminal);
        Assert.Equal(result.Terminal, result.TerminalForRpo);
        Assert.EndsWith("holding position, not clear of two eight left, traffic ahead.", result.Tts, StringComparison.Ordinal);
        Assert.Contains("november two nine four mike golf", result.Tts, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildLostTrafficHolding_RpoTerminalNamesLead_TtsDoesNot()
    {
        AircraftState pilot = MakeAircraft("N294MG");
        PilotSpeechText result = PilotResponder.BuildLostTrafficHolding(pilot, "N10194");

        Assert.Equal("Lost sight of traffic, holding position, request taxi instructions", result.Terminal);
        Assert.Equal("Lost sight of N10194, holding position, request taxi instructions", result.TerminalForRpo);
        Assert.EndsWith(", lost the traffic, holding, request taxi.", result.Tts, StringComparison.Ordinal);
        Assert.DoesNotContain("november one zero one nine four", result.Tts, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFollowExtendingUnableToTurn_RpoTerminalNamesTarget_TtsDoesNot()
    {
        AircraftState ac = MakeAircraft("N294MG");
        PilotSpeechText result = PilotResponder.BuildFollowExtendingUnableToTurn(ac, "N784ME", "downwind");

        Assert.Equal("extending downwind behind the traffic, unable to turn — request instructions.", result.Terminal);
        Assert.Equal("extending downwind behind N784ME, unable to turn — request instructions.", result.TerminalForRpo);
        Assert.Contains("extending downwind behind the traffic", result.Tts);
        Assert.DoesNotContain("seven eight four", result.Tts);
    }

    [Fact]
    public void BuildSTurnsForSpacing_RpoTerminalNamesTarget_TtsDoesNot()
    {
        AircraftState ac = MakeAircraft("N294MG");
        PilotSpeechText result = PilotResponder.BuildSTurnsForSpacing(ac, "N784ME");

        Assert.Equal("S-turning for spacing behind the traffic.", result.Terminal);
        Assert.Equal("S-turning for spacing behind N784ME.", result.TerminalForRpo);
        Assert.Contains("S-turning for spacing behind the traffic", result.Tts);
        Assert.DoesNotContain("seven eight four", result.Tts);
    }

    // --- RouteRpoTransmission branches ---

    private static readonly PilotSpeechText RouteRpoSpeech = new("written", "speech");

    [Fact]
    public void RouteRpoTransmission_SoloMode_RoutesToWarnings_NotPilotSpeech()
    {
        // Caller is responsible for solo→PendingPilotTransmissions routing; this helper only
        // handles the RPO branch. In solo mode it falls through to warnings (the old default).
        AircraftState ac = MakeAircraft("AAL123");

        PilotResponder.RouteRpoTransmission(ac, soloTrainingMode: true, rpoShowPilotSpeech: false, RouteRpoSpeech, "warning");

        Assert.Empty(ac.PendingPilotSpeech);
        Assert.Equal("warning", Assert.Single(ac.PendingWarnings));
    }

    [Fact]
    public void RouteRpoTransmission_RpoMode_PilotSpeechOff_RoutesToWarnings()
    {
        AircraftState ac = MakeAircraft("AAL123");

        PilotResponder.RouteRpoTransmission(ac, soloTrainingMode: false, rpoShowPilotSpeech: false, RouteRpoSpeech, "warning");

        Assert.Empty(ac.PendingPilotSpeech);
        Assert.Equal("warning", Assert.Single(ac.PendingWarnings));
    }

    [Fact]
    public void RouteRpoTransmission_RpoMode_PilotSpeechOn_RoutesToPilotSpeech()
    {
        AircraftState ac = MakeAircraft("AAL123");

        PilotResponder.RouteRpoTransmission(ac, soloTrainingMode: false, rpoShowPilotSpeech: true, RouteRpoSpeech, "warning");

        PilotSpeechText queued = Assert.Single(ac.PendingPilotSpeech);
        Assert.Equal("speech", queued.Tts);
        Assert.Equal("written", queued.TerminalForRpo);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void RouteRpoTransmission_SoloMode_OverridesRpoFlag()
    {
        // Solo mode wins even if rpoShowPilotSpeech happens to be true — solo paths handle
        // their own routing via PendingPilotTransmissions, so this helper conservatively falls
        // back to warning text.
        AircraftState ac = MakeAircraft("AAL123");

        PilotResponder.RouteRpoTransmission(ac, soloTrainingMode: true, rpoShowPilotSpeech: true, RouteRpoSpeech, "warning");

        Assert.Empty(ac.PendingPilotSpeech);
        Assert.Equal("warning", Assert.Single(ac.PendingWarnings));
    }

    // --- RouteRpoSayReadback dual-output branch (A5-2) ---

    [Fact]
    public void RouteRpoSayReadback_RpoShowsSpeech_QueuesWrittenAndSpokenForms()
    {
        AircraftState ac = MakeAircraft("N294MG");
        var text = new PilotSpeechText("traffic in sight.", "november two nine four mike golf, traffic in sight.");

        PilotResponder.RouteRpoSayReadback(ac, soloTrainingMode: false, rpoShowPilotSpeech: true, text);

        PilotSpeechText queued = Assert.Single(ac.PendingPilotSpeech);
        Assert.Equal("november two nine four mike golf, traffic in sight.", queued.Tts);
        Assert.Equal("traffic in sight.", queued.TerminalForRpo);
        Assert.Empty(ac.PendingPilotReadbacks);
    }

    [Fact]
    public void RouteRpoSayReadback_RpoNoSpeech_UsesTerminalForm()
    {
        AircraftState ac = MakeAircraft("N294MG");
        var text = new PilotSpeechText("traffic in sight.", "november two nine four mike golf, traffic in sight.");

        PilotResponder.RouteRpoSayReadback(ac, soloTrainingMode: false, rpoShowPilotSpeech: false, text);

        Assert.Equal("traffic in sight.", Assert.Single(ac.PendingPilotReadbacks));
        Assert.Empty(ac.PendingPilotSpeech);
    }

    [Fact]
    public void RouteRpoSayReadback_SoloMode_QueuesBothFormsAsSayReadback()
    {
        AircraftState ac = MakeAircraft("N294MG");
        var text = new PilotSpeechText("traffic in sight.", "november two nine four mike golf, traffic in sight.");

        PilotResponder.RouteRpoSayReadback(ac, soloTrainingMode: true, rpoShowPilotSpeech: true, text);

        PilotTransmission tx = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("traffic in sight.", tx.Text);
        Assert.Equal("november two nine four mike golf, traffic in sight.", tx.SpeechText);
        Assert.Empty(ac.PendingPilotSpeech);
        Assert.Empty(ac.PendingPilotReadbacks);
    }
}
