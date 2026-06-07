using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Pilot;

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

    private static CompoundCommand Compound(params ParsedCommand[] commands) => new([new ParsedBlock(null, commands.ToList())]);

    private static CompoundCommand CompoundWithCondition(BlockCondition condition, params ParsedCommand[] commands) =>
        new([new ParsedBlock(condition, commands.ToList())]);

    private static AircraftState MakeAircraftWithAssignedRunway(string callsign, string runwayId)
    {
        var ac = MakeAircraft(callsign);
        ac.Phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(runwayId) };
        return ac;
    }

    [Fact]
    public void BuildReadback_SingleAltitudeCommand_SpokenFormWithCallsign()
    {
        var ac = MakeAircraft("AAL123");
        var compound = Compound(new DescendMaintainCommand(5000));

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Equal("descend and maintain five thousand, american one twenty three.", result);
    }

    [Fact]
    public void BuildReadback_NNumber_SpokenForm()
    {
        var ac = MakeAircraft("N123AB");
        var compound = Compound(new ClimbMaintainCommand(3500));

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.StartsWith("climb and maintain three thousand five hundred, november one two three alpha bravo", result);
    }

    [Fact]
    public void BuildReadback_TwoCommandsInOneBlock_JoinedWithCommas()
    {
        var ac = MakeAircraft("AAL123");
        var compound = Compound(new DescendMaintainCommand(5000), new TurnRightCommand(new MagneticHeading(270)));

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Contains("descend and maintain five thousand", result!);
        Assert.Contains("turn right heading two seven zero", result);
        Assert.EndsWith(", american one twenty three.", result);
    }

    [Fact]
    public void BuildReadback_VariedBusy_ShortensClausesAndKeepsCallsign()
    {
        var ac = MakeAircraft("AAL123");
        var compound = Compound(new DescendMaintainCommand(5000), new TurnRightCommand(new MagneticHeading(270)));

        var result = PilotResponder.BuildReadback(compound, ac, PilotPersonality.Varied, FrequencyActivityLevel.Busy)?.Tts;

        Assert.Equal("down to five thousand, right heading two seven zero, american one twenty three.", result);
    }

    [Fact]
    public void BuildReadback_VariedSaturated_DoesNotDropRunwayCriticalContent()
    {
        var ac = MakeAircraftWithAssignedRunway("N436MS", "28R");
        var compound = Compound(new LineUpAndWaitCommand());

        var result = PilotResponder.BuildReadback(compound, ac, PilotPersonality.Varied, FrequencyActivityLevel.Saturated)?.Tts;

        Assert.Equal("line up and wait runway two eight right, november four three six mike sierra.", result);
    }

    [Fact]
    public void BuildReadback_VariedQuietFlavor_IsDeterministicAndRare()
    {
        var flavored = new List<string>();
        for (int i = 0; i < 1000; i++)
        {
            var ac = MakeAircraft($"N{i:D3}AB");
            var compound = Compound(new FlyHeadingCommand(new MagneticHeading(270)));

            var first = PilotResponder.BuildReadback(compound, ac, PilotPersonality.Varied, FrequencyActivityLevel.Quiet)?.Tts;
            var second = PilotResponder.BuildReadback(compound, ac, PilotPersonality.Varied, FrequencyActivityLevel.Quiet)?.Tts;

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
    public void BuildReadback_VariedBusy_DisablesQuietFlavor()
    {
        var ac = MakeAircraft("N004AB");
        var compound = Compound(new FlyHeadingCommand(new MagneticHeading(270)));

        var result = PilotResponder.BuildReadback(compound, ac, PilotPersonality.Varied, FrequencyActivityLevel.Busy)?.Tts;

        Assert.Equal("heading two seven zero, november zero zero four alpha bravo.", result);
        Assert.DoesNotContain("alright", result);
        Assert.DoesNotContain("thanks", result);
    }

    [Fact]
    public void BuildReadback_AtFixCondition_PrependsLeadingClause()
    {
        var ac = MakeAircraft("AAL123");
        var compound = CompoundWithCondition(AtFixCondition.FromName("SUNOL"), new TurnLeftCommand(new MagneticHeading(180)));

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Contains("at sunol, turn left heading one eight zero", result!);
    }

    [Fact]
    public void BuildReadback_AtFixCondition_ConditionSpokenOnceForBlock()
    {
        var ac = MakeAircraft("AAL123");
        var compound = CompoundWithCondition(
            AtFixCondition.FromName("SUNOL"),
            new TurnLeftCommand(new MagneticHeading(180)),
            new DescendMaintainCommand(5000)
        );

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        // The first command gets the "at sunol," lead; the second does not (same block).
        Assert.Contains("at sunol, turn left heading one eight zero", result!);
        Assert.DoesNotContain("at sunol, descend", result);
        Assert.Contains("descend and maintain five thousand", result);
    }

    [Fact]
    public void BuildReadback_NoVerbalizableCommands_ReturnsNull()
    {
        var ac = MakeAircraft("AAL123");
        var compound = Compound(new UnsupportedCommand("ZZZ 999"));

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Null(result);
    }

    [Fact]
    public void BuildReadback_ClearedForTakeoff_IncludesRunwayDepartureAndAltitude()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        var ac = MakeAircraft("N436MS");
        ac.Procedure.DepartureRunway = "28R";
        var compound = Compound(new ClearedForTakeoffCommand(new DirectFixDeparture("MOD", 37.625, -120.957, TurnDirection.Right), 2500));

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Equal(
            "cleared for takeoff runway two eight right, turn right direct Modesto VOR, climb and maintain two thousand five hundred, november four three six mike sierra.",
            result
        );
    }

    [Fact]
    public void BuildReadback_ClearedForTakeoff_RelativeTurnDepartureUsesGroupedDegrees()
    {
        var ac = MakeAircraft("N172SP");
        ac.Procedure.DepartureRunway = "28R";
        var compound = Compound(new ClearedForTakeoffCommand(new RelativeTurnDeparture(270, TurnDirection.Right), 1400));

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Equal(
            "cleared for takeoff runway two eight right, make a right two seventy degree departure, climb and maintain one thousand four hundred, november one seven two sierra papa.",
            result
        );
    }

    [Fact]
    public void BuildReadback_AcknowledgePilotContact_RemainsSilent()
    {
        var ac = MakeAircraft("N172SP");
        var compound = Compound(new AcknowledgePilotContactCommand());

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Null(result);
    }

    [Fact]
    public void BuildReadback_ClearedTakeoffPresent_IncludesDepartureAndAltitudeWithoutRunway()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        var ac = MakeAircraft("N436MS");
        ac.Procedure.DepartureRunway = "28R";
        var compound = Compound(new ClearedTakeoffPresentCommand(new DirectFixDeparture("MOD", 37.625, -120.957, TurnDirection.Right), 2500));

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

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
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        var ac = MakeAircraft("N436MS");
        var compound = Compound(new DirectToCommand([new ResolvedFix("MOD", 37.625, -120.957)], []));

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Equal("proceed direct to Modesto VOR, november four three six mike sierra.", result);
    }

    [Fact]
    public void BuildReadback_DirectToMultipleFixes_ReadsAllJoinedWithThenDirect()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        // Mix two real ZOA entries: OAK30NUM is a custom fix with friendly name "Oakland
        // Runway 30 Numbers"; VPMID has a published pronunciation "Midspan San Mateo Bridge".
        // Exercises both lookups within a single multi-fix DCT readback.
        var ac = MakeAircraft("N172SP");
        var compound = Compound(new DirectToCommand([new ResolvedFix("OAK30NUM", 0, 0), new ResolvedFix("VPMID", 0, 0)], []));

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

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
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        // VPCOL is registered in ARTCCs/ZOA/FixPronunciations/visual.json with pronunciation "Oakland Colliseum".
        var ac = MakeAircraft("N172SP");
        var compound = Compound(new DirectToCommand([new ResolvedFix("VPCOL", 0, 0)], []));

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Equal("proceed direct to Oakland Colliseum, november one seven two sierra papa.", result);
    }

    [Fact]
    public void BuildReadback_DirectToCustomFixWithFriendlyName_UsesFriendlyName()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        // OAK30NUM is a CustomFix in ARTCCs/ZOA/CustomFixes/oak-landmarks.json with friendly
        // name "Oakland Runway 30 Numbers" — the natural-language form pilots speak. With no
        // explicit pronunciation entry, SpellFix should fall through to the custom-fix name
        // rather than the literal alias spelled letter-by-letter.
        var ac = MakeAircraft("N172SP");
        var compound = Compound(new DirectToCommand([new ResolvedFix("OAK30NUM", 0, 0)], []));

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Equal("proceed direct to Oakland Runway 30 Numbers, november one seven two sierra papa.", result);
    }

    [Fact]
    public void BuildReadback_SequentialBlocks_JoinedWithThen()
    {
        var ac = MakeAircraft("AAL123");
        // Two blocks separated by ; — controller said "DM 5000 ; FH 270".
        var compound = new CompoundCommand([
            new ParsedBlock(null, [new DescendMaintainCommand(5000)]),
            new ParsedBlock(null, [new TurnRightCommand(new MagneticHeading(270))]),
        ]);

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Equal("descend and maintain five thousand, then turn right heading two seven zero, american one twenty three.", result);
    }

    [Fact]
    public void BuildReadback_ParallelBlock_JoinedWithCommaNotThen()
    {
        var ac = MakeAircraft("AAL123");
        // Single block with two parallel commands — controller said "DM 5000, FH 270".
        // Parallel commands stay comma-joined; "then" is sequential-only.
        var compound = Compound(new DescendMaintainCommand(5000), new TurnRightCommand(new MagneticHeading(270)));

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.DoesNotContain("then", result!);
        Assert.Equal("descend and maintain five thousand, turn right heading two seven zero, american one twenty three.", result);
    }

    public static TheoryData<ParsedCommand, string> RunwayCriticalTowerReadbackCases() =>
        new()
        {
            { new LineUpAndWaitCommand(), "line up and wait runway two eight right" },
            { new ClearedToLandCommand(), "cleared to land runway two eight right" },
            { new LandAndHoldShortCommand("28L"), "cleared to land runway two eight right, hold short runway two eight left" },
            { new TouchAndGoCommand(null, null), "cleared touch and go runway two eight right" },
            { new StopAndGoCommand(null), "cleared stop and go runway two eight right" },
            { new LowApproachCommand(null), "cleared low approach runway two eight right" },
            { new ClearedForOptionCommand(null), "cleared for the option runway two eight right" },
            { new ClearedForOptionCommand(PatternDirection.Right), "cleared for the option runway two eight right, make right traffic" },
        };

    [Theory]
    [MemberData(nameof(RunwayCriticalTowerReadbackCases))]
    public void BuildReadback_RunwayCriticalTowerClearances_IncludeAssignedRunway(ParsedCommand command, string expectedClause)
    {
        var ac = MakeAircraftWithAssignedRunway("N436MS", "28R");
        var compound = Compound(command);

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Equal($"{expectedClause}, november four three six mike sierra.", result);
    }

    // --- Dual-output readback: compact terminal SAY echo vs spelled TTS (issue #193) ---

    [Fact]
    public void BuildReadback_ClearedToLand_TerminalDropsCallsignAndLeadingZero()
    {
        var ac = MakeAircraftWithAssignedRunway("N436MS", "08R");
        var compound = Compound(new ClearedToLandCommand());

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.NotNull(result);
        // Terminal SAY message: compact runway (no leading zero), no callsign — the SAY column carries it.
        Assert.Equal("cleared to land runway 8R", result!.Terminal);
        // TTS: spelled runway without the padding zero + spoken callsign appended.
        Assert.Equal("cleared to land runway eight right, november four three six mike sierra.", result.Tts);
    }

    [Fact]
    public void BuildReadback_Taxi_TerminalUsesIdentifiersTtsSpellsPhonetics()
    {
        var ac = MakeAircraft("N225R");
        var compound = Compound(new TaxiCommand(["B", "C", "D"], [], DestinationRunway: "08R"));

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.NotNull(result);
        Assert.Contains("8R", result!.Terminal);
        Assert.Contains("B C D", result.Terminal);
        Assert.DoesNotContain("november", result.Terminal); // callsign is column-only in terminal
        Assert.DoesNotContain("eight right", result.Terminal);
        Assert.Contains("eight right", result.Tts);
        Assert.Contains("bravo, charlie, delta", result.Tts);
        Assert.Contains("november two two five", result.Tts);
    }

    // --- EXT readback (issue #154 #7) ---

    [Fact]
    public void BuildReadback_ExtendOnDownwind_SaysDownwindWithRunway()
    {
        // Issue #154 #7: bare EXT used to readback as "extend upwind" no matter what leg
        // the aircraft was on, because the parser leaves Leg=null and the verbalizer
        // tied on capture count and fell to the first-declared rule.
        var ac = MakeAircraftWithAssignedRunway("N342T", "28R");
        ac.Procedure.DestinationRunway = "28R";
        ac.Phases!.Add(new DownwindPhase());
        var compound = Compound(new ExtendPatternCommand());

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Equal("extend downwind runway two eight right, november three four two tango.", result);
    }

    [Fact]
    public void BuildReadback_ExtendOnUpwind_SaysUpwindWithRunway()
    {
        var ac = MakeAircraftWithAssignedRunway("N342T", "28R");
        ac.Procedure.DestinationRunway = "28R";
        ac.Phases!.Add(new UpwindPhase());
        var compound = Compound(new ExtendPatternCommand());

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Equal("extend upwind runway two eight right, november three four two tango.", result);
    }

    [Fact]
    public void BuildReadback_ExtendOnCrosswind_SaysCrosswindWithRunway()
    {
        var ac = MakeAircraftWithAssignedRunway("N342T", "28R");
        ac.Procedure.DestinationRunway = "28R";
        ac.Phases!.Add(new CrosswindPhase());
        var compound = Compound(new ExtendPatternCommand());

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Equal("extend crosswind runway two eight right, november three four two tango.", result);
    }

    [Fact]
    public void BuildReadback_ExtendOnDownwind_NoDestinationRunway_DropsRunwayClause()
    {
        var ac = MakeAircraft("N342T");
        ac.Phases = new PhaseList();
        ac.Phases.Add(new DownwindPhase());
        var compound = Compound(new ExtendPatternCommand());

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Equal("extend downwind, november three four two tango.", result);
    }

    // --- BuildReadyToTaxi ---

    [Fact]
    public void BuildReadyToTaxi_WithKnownParkingSpot_IncludesLowercaseSpot()
    {
        var ac = MakeAircraft("N123AB", parkingSpot: "GATE B22");
        var result = PilotResponder.BuildReadyToTaxi(ac);

        Assert.Equal("ground, november one two three alpha bravo at gate b22, with information Alpha, ready to taxi.", result.Tts);
    }

    [Fact]
    public void BuildReadyToTaxi_WithoutParkingSpot_FallsBackToRamp()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildReadyToTaxi(ac);

        Assert.Equal("ground, american one twenty three at the ramp, with information Alpha, ready to taxi.", result.Tts);
    }

    [Fact]
    public void BuildReadyToTaxi_WithRadioName_AddressesFacility()
    {
        var ac = MakeAircraft("N123AB", parkingSpot: "GATE B22");
        var result = PilotResponder.BuildReadyToTaxi(ac, "Oakland Ground");

        Assert.Equal("Oakland Ground, november one two three alpha bravo at gate b22, with information Alpha, ready to taxi.", result.Tts);
    }

    // --- BuildHoldingShortReady ---

    [Fact]
    public void BuildHoldingShortReady_FormatsRunwaySpoken()
    {
        var ac = MakeAircraft("N123AB");
        var result = PilotResponder.BuildHoldingShortReady(ac, "28R");

        Assert.Equal("tower, november one two three alpha bravo holding short runway two eight right, ready for departure.", result.Tts);
    }

    [Fact]
    public void BuildHoldingShortReady_AirlineCallsign_UsesTelephony()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildHoldingShortReady(ac, "9L");

        Assert.Contains("american one twenty three holding short runway nine left", result.Tts);
    }

    [Fact]
    public void BuildHoldingShortReady_WithRadioName_AddressesFacility()
    {
        var ac = MakeAircraft("N123AB");
        var result = PilotResponder.BuildHoldingShortReady(ac, "28R", "Oakland Tower");

        Assert.Equal("Oakland Tower, november one two three alpha bravo holding short runway two eight right, ready for departure.", result.Tts);
    }

    // --- BuildLinedUpReady ---

    [Fact]
    public void BuildLinedUpReady_FormatsRunwaySpoken()
    {
        var ac = MakeAircraft("N123AB");
        var result = PilotResponder.BuildLinedUpReady(ac, "28R");

        Assert.Equal("tower, november one two three alpha bravo runway two eight right, ready.", result.Tts);
    }

    [Fact]
    public void BuildLinedUpReady_WithRadioName_AddressesFacility()
    {
        var ac = MakeAircraft("N123AB");
        var result = PilotResponder.BuildLinedUpReady(ac, "28R", "Oakland Tower");

        Assert.Equal("Oakland Tower, november one two three alpha bravo runway two eight right, ready.", result.Tts);
    }

    // --- BuildOnFinal ---

    [Fact]
    public void BuildOnFinal_IfrWithIlsApproach_SpellsIlsBeforeRunway()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: true, approachId: "I28R", distanceMilesForVfr: 0);

        Assert.Equal("tower, american one twenty three, ILS two eight right.", result.Tts);
    }

    [Fact]
    public void BuildOnFinal_IfrWithRnavApproachAndSuffix_IncludesSuffixLetter()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: true, approachId: "R28R-Y", distanceMilesForVfr: 0);

        Assert.Equal("tower, american one twenty three, RNAV two eight right yankee.", result.Tts);
    }

    [Fact]
    public void BuildOnFinal_IfrWithVisualApproach_UsesExpandedPhrasing()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: true, approachId: "VIS28R", distanceMilesForVfr: 0);

        Assert.Equal("tower, american one twenty three, visual approach runway two eight right.", result.Tts);
    }

    [Fact]
    public void BuildOnFinal_VfrNoApproach_ReportsDistanceAndAtis()
    {
        var ac = MakeAircraft("N123AB", isVfr: true);
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: false, approachId: null, distanceMilesForVfr: 3);

        Assert.Equal("tower, november one two three alpha bravo three-mile final runway two eight right, with information Alpha.", result.Tts);
    }

    [Fact]
    public void BuildOnFinal_WithRadioName_AddressesFacility()
    {
        var ac = MakeAircraft("N123AB", isVfr: true);
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: false, approachId: null, distanceMilesForVfr: 3, "Oakland Tower");

        Assert.Equal(
            "Oakland Tower, november one two three alpha bravo three-mile final runway two eight right, with information Alpha.",
            result.Tts
        );
    }

    [Fact]
    public void BuildOnFinal_VfrUnderOneMile_ClampsToOneMile()
    {
        var ac = MakeAircraft("N123AB", isVfr: true);
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: false, approachId: null, distanceMilesForVfr: 0);

        Assert.Contains("one-mile final", result.Tts);
    }

    [Fact]
    public void BuildOnFinal_IfrNoApproach_FallsBackToVfrTemplate()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildOnFinal(ac, "9", ifrWithActiveApproach: false, approachId: null, distanceMilesForVfr: 5);

        Assert.Equal("tower, american one twenty three five-mile final runway nine, with information Alpha.", result.Tts);
    }

    // --- BuildClosedTrafficRequest ---

    [Fact]
    public void BuildClosedTrafficRequest_VfrThreeMilesSouth_FormatsCanonical()
    {
        var airport = new LatLon(37.7212, -122.2208);
        // 3 nm south at 1500 ft.
        var ac = MakeAircraft("N123AB", isVfr: true);
        ac.Position = GeoMath.ProjectPoint(airport, new TrueHeading(180), 3);

        var result = PilotResponder.BuildClosedTrafficRequest(ac, airport, altitudeFt: 1500);

        Assert.Equal(
            "tower, november one two three alpha bravo, three miles south at one thousand five hundred, request closed traffic, with information Alpha.",
            result.Tts
        );
    }

    [Fact]
    public void BuildClosedTrafficRequest_AirlineCallsign_UsesTelephony()
    {
        var airport = new LatLon(37.7212, -122.2208);
        var ac = MakeAircraft("AAL123", isVfr: true);
        ac.Position = GeoMath.ProjectPoint(airport, new TrueHeading(90), 5);

        var result = PilotResponder.BuildClosedTrafficRequest(ac, airport, altitudeFt: 2000);

        Assert.Equal(
            "tower, american one twenty three, five miles east at two thousand, request closed traffic, with information Alpha.",
            result.Tts
        );
    }

    [Fact]
    public void BuildClosedTrafficRequest_WithRadioName_AddressesFacility()
    {
        var airport = new LatLon(37.7212, -122.2208);
        var ac = MakeAircraft("N123AB", isVfr: true);
        ac.Position = GeoMath.ProjectPoint(airport, new TrueHeading(180), 3);

        var result = PilotResponder.BuildClosedTrafficRequest(ac, airport, altitudeFt: 1500, "Oakland Tower");

        Assert.Equal(
            "Oakland Tower, november one two three alpha bravo, three miles south at one thousand five hundred, request closed traffic, with information Alpha.",
            result.Tts
        );
    }

    // --- BuildContactReadback ---

    [Fact]
    public void BuildContactReadback_FormatsFacilityFreqAndSignoff()
    {
        var ac = MakeAircraft("N123AB");

        var result = PilotResponder.BuildContactReadback(ac, "Approach", 125.35);

        // Facility name preserves the caller's casing — sentence-initial after the bracket
        // strip in CompactForTerminal, so capitalization matters for the terminal display.
        Assert.Equal("Approach on one two five point three five, november one two three alpha bravo, so long.", result.Tts);
    }

    [Fact]
    public void BuildContactReadback_AirlineCallsign_UsesTelephony()
    {
        var ac = MakeAircraft("AAL123");

        var result = PilotResponder.BuildContactReadback(ac, "Departure", 119.6);

        Assert.Equal("Departure on one one nine point six, american one twenty three, so long.", result.Tts);
    }

    [Fact]
    public void BuildContactReadback_PreservesFacilityNameCasing()
    {
        var ac = MakeAircraft("N123AB");

        // Caller may pass a Position.RadioName like "NorCal Approach" or "Oakland Tower" —
        // multi-word natural casing must reach the terminal and TTS unchanged.
        var result = PilotResponder.BuildContactReadback(ac, "NorCal Approach", 125.35);

        Assert.Contains("NorCal Approach on", result.Tts);
    }

    // --- BuildFrequencyChangeApproved ---

    [Fact]
    public void BuildFrequencyChangeApproved_PilotSignsOff_DoesNotParrotControllerPhrase()
    {
        // Per AIM 4-2-3 ¶3, pilots acknowledge with a sign-off, not a verbatim recital of
        // "frequency change approved" (which is the controller's phraseology in 7110.65 §7-6-11).
        var ac = MakeAircraft("N123AB", isVfr: true);

        var result = PilotResponder.BuildFrequencyChangeApproved(ac);

        Assert.Equal("november one two three alpha bravo, good day.", result.Tts);
        Assert.DoesNotContain("frequency change approved", result.Tts);
    }

    [Fact]
    public void BuildFrequencyChangeApproved_AirlineCallsign_UsesTelephony()
    {
        var ac = MakeAircraft("AAL123");

        var result = PilotResponder.BuildFrequencyChangeApproved(ac);

        Assert.Equal("american one twenty three, good day.", result.Tts);
    }

    // --- BuildMidfieldDownwindReminder ---

    [Fact]
    public void BuildMidfieldDownwindReminder_FormatsRunwaySpoken()
    {
        var ac = MakeAircraft("N123AB", isVfr: true);
        var result = PilotResponder.BuildMidfieldDownwindReminder(ac, "28R");

        Assert.Equal("november one two three alpha bravo, midfield downwind runway two eight right.", result.Tts);
    }

    [Fact]
    public void BuildMidfieldDownwindReminder_AirlineCallsign_UsesTelephony()
    {
        var ac = MakeAircraft("AAL123", isVfr: true);
        var result = PilotResponder.BuildMidfieldDownwindReminder(ac, "9L");

        Assert.Equal("american one twenty three, midfield downwind runway nine left.", result.Tts);
    }

    // --- BuildShortFinalReminder ---

    [Fact]
    public void BuildShortFinalReminder_FormatsRunwaySpoken()
    {
        var ac = MakeAircraft("N123AB", isVfr: true);
        var result = PilotResponder.BuildShortFinalReminder(ac, "28R");

        Assert.Equal("november one two three alpha bravo, short final runway two eight right.", result.Tts);
    }

    [Fact]
    public void BuildShortFinalReminder_NoSuffixRunway()
    {
        var ac = MakeAircraft("N123AB", isVfr: true);
        var result = PilotResponder.BuildShortFinalReminder(ac, "9");

        Assert.Equal("november one two three alpha bravo, short final runway nine.", result.Tts);
    }

    // --- BuildTrafficInSight ---

    [Fact]
    public void BuildTrafficInSight_WithTargetCallsign_SpellsBothCallsigns()
    {
        var ac = MakeAircraft("N294MG");
        var result = PilotResponder.BuildTrafficInSight(ac, "N784ME");

        Assert.Equal("[N294MG] november two nine four mike golf, traffic in sight, november seven eight four mike echo.", result);
    }

    [Fact]
    public void BuildTrafficInSight_NoTargetCallsign_OmitsTargetClause()
    {
        var ac = MakeAircraft("N123AB");
        var result = PilotResponder.BuildTrafficInSight(ac, null);

        Assert.Equal("[N123AB] november one two three alpha bravo, traffic in sight.", result);
    }

    [Fact]
    public void BuildFieldInSight_FormatsCallsign()
    {
        var ac = MakeAircraft("UAL238");
        var result = PilotResponder.BuildFieldInSight(ac);

        Assert.Equal("[UAL238] united two thirty eight, field in sight.", result);
    }

    [Fact]
    public void BuildLostSightOfField_UsesNegativeContact()
    {
        var ac = MakeAircraft("N172SP");
        var result = PilotResponder.BuildLostSightOfField(ac);

        Assert.Contains("negative contact", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[N172SP]", result);
        Assert.Contains("field", result);
    }

    [Fact]
    public void BuildLostSightOfTraffic_NamesTarget()
    {
        var ac = MakeAircraft("N172SP");
        var result = PilotResponder.BuildLostSightOfTraffic(ac, "N784ME");

        Assert.Contains("negative contact", result.Tts, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("november seven eight four mike echo", result.Tts);
    }

    [Fact]
    public void BuildGoingAround_IncludesReason()
    {
        var ac = MakeAircraft("FDX3807");
        var result = PilotResponder.BuildGoingAround(ac, "no landing clearance");

        Assert.Contains("going around", result.Tts);
        Assert.Contains("(no landing clearance)", result.Tts);
    }

    [Fact]
    public void BuildGoingAround_EmptyReason_OmitsParenthetical()
    {
        var ac = MakeAircraft("FDX3807");
        var result = PilotResponder.BuildGoingAround(ac, "");

        Assert.EndsWith("going around.", result.Tts);
        Assert.DoesNotContain("()", result.Tts);
    }

    [Fact]
    public void BuildHoldingShortCrossing_FormatsRunway()
    {
        var ac = MakeAircraft("N172SP");
        var result = PilotResponder.BuildHoldingShortCrossing(ac, "28R");

        Assert.Contains("holding short runway two eight right", result.Tts);
    }

    [Fact]
    public void BuildClearOfRunway_TerminalUsesIdentifierAndTtsUsesSpoken()
    {
        var ac = MakeAircraft("N569SX");

        var text = PilotResponder.BuildClearOfRunwayText(ac, "28R", "G");

        // Terminal form: digit identifier, no callsign (SAY column carries it).
        Assert.Equal("clear of runway 28R at G.", text.Terminal);
        // TTS form: spelled-out callsign + runway, AIM phraseology (NATO X = "xray", no hyphen).
        Assert.Equal("november five six nine sierra xray, clear of runway two eight right at G.", text.Tts);
    }

    [Fact]
    public void BuildUnableToExit_UsesNegative()
    {
        var ac = MakeAircraft("N123AB");
        var result = PilotResponder.BuildUnableToExit(ac, "M2");

        Assert.Contains("negative", result.Tts, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("M2", result.Tts);
    }

    [Fact]
    public void BuildUnableToMaintainSeparation_NamesLead()
    {
        var ac = MakeAircraft("N294MG");
        var result = PilotResponder.BuildUnableToMaintainSeparation(ac, "N10194");

        Assert.Contains("unable to maintain separation", result);
        Assert.Contains("november one zero one nine four", result);
    }

    [Fact]
    public void BuildTargetLanded_BreaksOff()
    {
        var ac = MakeAircraft("N294MG");
        var result = PilotResponder.BuildTargetLanded(ac, "N784ME");

        Assert.Contains("on the ground", result.Tts);
        Assert.Contains("breaking off the follow", result.Tts);
    }

    [Fact]
    public void BuildUnableToCatchUp_NamesTarget()
    {
        var ac = MakeAircraft("N294MG");
        var result = PilotResponder.BuildUnableToCatchUp(ac, "N784ME");

        Assert.Contains("unable to catch up", result.Tts);
        Assert.Contains("breaking off", result.Tts);
    }

    // --- RouteRpoTransmission three-way branch ---

    [Fact]
    public void RouteRpoTransmission_SoloMode_RoutesToWarnings_NotPilotSpeech()
    {
        // Caller is responsible for solo→PendingPilotTransmissions routing; this helper only
        // handles the RPO branch. In solo mode it falls through to warnings (the old default).
        var ac = MakeAircraft("AAL123");

        PilotResponder.RouteRpoTransmission(ac, soloTrainingMode: true, rpoShowPilotSpeech: false, "speech", "warning");

        Assert.Empty(ac.PendingPilotSpeech);
        Assert.Equal("warning", Assert.Single(ac.PendingWarnings));
    }

    [Fact]
    public void RouteRpoTransmission_RpoMode_PilotSpeechOff_RoutesToWarnings()
    {
        var ac = MakeAircraft("AAL123");

        PilotResponder.RouteRpoTransmission(ac, soloTrainingMode: false, rpoShowPilotSpeech: false, "speech", "warning");

        Assert.Empty(ac.PendingPilotSpeech);
        Assert.Equal("warning", Assert.Single(ac.PendingWarnings));
    }

    [Fact]
    public void RouteRpoTransmission_RpoMode_PilotSpeechOn_RoutesToPilotSpeech()
    {
        var ac = MakeAircraft("AAL123");

        PilotResponder.RouteRpoTransmission(ac, soloTrainingMode: false, rpoShowPilotSpeech: true, "speech", "warning");

        Assert.Equal("speech", Assert.Single(ac.PendingPilotSpeech));
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void RouteRpoTransmission_SoloMode_OverridesRpoFlag()
    {
        // Solo mode wins even if rpoShowPilotSpeech happens to be true — solo paths handle
        // their own routing via PendingPilotTransmissions, so this helper conservatively falls
        // back to warning text.
        var ac = MakeAircraft("AAL123");

        PilotResponder.RouteRpoTransmission(ac, soloTrainingMode: true, rpoShowPilotSpeech: true, "speech", "warning");

        Assert.Empty(ac.PendingPilotSpeech);
        Assert.Equal("warning", Assert.Single(ac.PendingWarnings));
    }
}
