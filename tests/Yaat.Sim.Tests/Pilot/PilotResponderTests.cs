using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
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
    public void BuildReadback_SingleAltitudeCommand_AppendsBracketAndSpokenCallsign()
    {
        var ac = MakeAircraft("AAL123");
        var compound = Compound(new DescendMaintainCommand(5000));

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.Equal("[AAL123] descend and maintain five thousand, american one twenty three.", result);
    }

    [Fact]
    public void BuildReadback_NNumber_SpokenForm()
    {
        var ac = MakeAircraft("N123AB");
        var compound = Compound(new ClimbMaintainCommand(3500));

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.StartsWith("[N123AB] climb and maintain three thousand five hundred, november one two three alpha bravo", result);
    }

    [Fact]
    public void BuildReadback_TwoCommandsInOneBlock_JoinedWithCommas()
    {
        var ac = MakeAircraft("AAL123");
        var compound = Compound(new DescendMaintainCommand(5000), new TurnRightCommand(new MagneticHeading(270)));

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.Contains("descend and maintain five thousand", result!);
        Assert.Contains("turn right heading two seven zero", result);
        Assert.EndsWith(", american one twenty three.", result);
    }

    [Fact]
    public void BuildReadback_AtFixCondition_PrependsLeadingClause()
    {
        var ac = MakeAircraft("AAL123");
        var compound = CompoundWithCondition(AtFixCondition.FromName("SUNOL"), new TurnLeftCommand(new MagneticHeading(180)));

        var result = PilotResponder.BuildReadback(compound, ac);

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

        var result = PilotResponder.BuildReadback(compound, ac);

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

        var result = PilotResponder.BuildReadback(compound, ac);

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

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.Equal(
            "[N436MS] cleared for takeoff runway two eight right, turn right direct Modesto VOR, climb and maintain two thousand five hundred, november four three six mike sierra.",
            result
        );
    }

    [Fact]
    public void BuildReadback_ClearedForTakeoff_RelativeTurnDepartureUsesGroupedDegrees()
    {
        var ac = MakeAircraft("N172SP");
        ac.Procedure.DepartureRunway = "28R";
        var compound = Compound(new ClearedForTakeoffCommand(new RelativeTurnDeparture(270, TurnDirection.Right), 1400));

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.Equal(
            "[N172SP] cleared for takeoff runway two eight right, make a right two seventy degree departure, climb and maintain one thousand four hundred, november one seven two sierra papa.",
            result
        );
    }

    [Fact]
    public void BuildReadback_AcknowledgePilotContact_RemainsSilent()
    {
        var ac = MakeAircraft("N172SP");
        var compound = Compound(new AcknowledgePilotContactCommand());

        var result = PilotResponder.BuildReadback(compound, ac);

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

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.Equal(
            "[N436MS] cleared for takeoff, present position, turn right direct Modesto VOR, climb and maintain two thousand five hundred, november four three six mike sierra.",
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

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.Equal("[N436MS] proceed direct to Modesto VOR, november four three six mike sierra.", result);
    }

    [Fact]
    public void BuildReadback_DirectToMultipleFixes_ReadsAllJoinedWithThenDirect()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        var ac = MakeAircraft("N172SP");
        var compound = Compound(new DirectToCommand([new ResolvedFix("OAK30NUM", 0, 0), new ResolvedFix("VPMID", 0, 0)], []));

        var result = PilotResponder.BuildReadback(compound, ac);

        // Variable-length DCT must read every fix; "then direct" joins later fixes.
        Assert.Equal("[N172SP] proceed direct to oak30num, then direct vpmid, november one seven two sierra papa.", result);
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

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.Equal("[N172SP] proceed direct to Oakland Colliseum, november one seven two sierra papa.", result);
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

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.Equal("[AAL123] descend and maintain five thousand, then turn right heading two seven zero, american one twenty three.", result);
    }

    [Fact]
    public void BuildReadback_ParallelBlock_JoinedWithCommaNotThen()
    {
        var ac = MakeAircraft("AAL123");
        // Single block with two parallel commands — controller said "DM 5000, FH 270".
        // Parallel commands stay comma-joined; "then" is sequential-only.
        var compound = Compound(new DescendMaintainCommand(5000), new TurnRightCommand(new MagneticHeading(270)));

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.DoesNotContain("then", result!);
        Assert.Equal("[AAL123] descend and maintain five thousand, turn right heading two seven zero, american one twenty three.", result);
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

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.Equal($"[N436MS] {expectedClause}, november four three six mike sierra.", result);
    }

    // --- BuildReadyToTaxi ---

    [Fact]
    public void BuildReadyToTaxi_WithKnownParkingSpot_IncludesLowercaseSpot()
    {
        var ac = MakeAircraft("N123AB", parkingSpot: "GATE B22");
        var result = PilotResponder.BuildReadyToTaxi(ac);

        Assert.Equal("[N123AB] ground, november one two three alpha bravo at gate b22, with information Alpha, ready to taxi.", result);
    }

    [Fact]
    public void BuildReadyToTaxi_WithoutParkingSpot_FallsBackToRamp()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildReadyToTaxi(ac);

        Assert.Equal("[AAL123] ground, american one twenty three at the ramp, with information Alpha, ready to taxi.", result);
    }

    [Fact]
    public void BuildReadyToTaxi_WithRadioName_AddressesFacility()
    {
        var ac = MakeAircraft("N123AB", parkingSpot: "GATE B22");
        var result = PilotResponder.BuildReadyToTaxi(ac, "Oakland Ground");

        Assert.Equal("[N123AB] Oakland Ground, november one two three alpha bravo at gate b22, with information Alpha, ready to taxi.", result);
    }

    // --- BuildHoldingShortReady ---

    [Fact]
    public void BuildHoldingShortReady_FormatsRunwaySpoken()
    {
        var ac = MakeAircraft("N123AB");
        var result = PilotResponder.BuildHoldingShortReady(ac, "28R");

        Assert.Equal("[N123AB] tower, november one two three alpha bravo holding short runway two eight right, ready for departure.", result);
    }

    [Fact]
    public void BuildHoldingShortReady_AirlineCallsign_UsesTelephony()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildHoldingShortReady(ac, "9L");

        Assert.Contains("american one twenty three holding short runway nine left", result);
    }

    [Fact]
    public void BuildHoldingShortReady_WithRadioName_AddressesFacility()
    {
        var ac = MakeAircraft("N123AB");
        var result = PilotResponder.BuildHoldingShortReady(ac, "28R", "Oakland Tower");

        Assert.Equal("[N123AB] Oakland Tower, november one two three alpha bravo holding short runway two eight right, ready for departure.", result);
    }

    // --- BuildLinedUpReady ---

    [Fact]
    public void BuildLinedUpReady_FormatsRunwaySpoken()
    {
        var ac = MakeAircraft("N123AB");
        var result = PilotResponder.BuildLinedUpReady(ac, "28R");

        Assert.Equal("[N123AB] tower, november one two three alpha bravo runway two eight right, ready.", result);
    }

    [Fact]
    public void BuildLinedUpReady_WithRadioName_AddressesFacility()
    {
        var ac = MakeAircraft("N123AB");
        var result = PilotResponder.BuildLinedUpReady(ac, "28R", "Oakland Tower");

        Assert.Equal("[N123AB] Oakland Tower, november one two three alpha bravo runway two eight right, ready.", result);
    }

    // --- BuildOnFinal ---

    [Fact]
    public void BuildOnFinal_IfrWithIlsApproach_SpellsIlsBeforeRunway()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: true, approachId: "I28R", distanceMilesForVfr: 0);

        Assert.Equal("[AAL123] tower, american one twenty three, ILS two eight right.", result);
    }

    [Fact]
    public void BuildOnFinal_IfrWithRnavApproachAndSuffix_IncludesSuffixLetter()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: true, approachId: "R28R-Y", distanceMilesForVfr: 0);

        Assert.Equal("[AAL123] tower, american one twenty three, RNAV two eight right yankee.", result);
    }

    [Fact]
    public void BuildOnFinal_IfrWithVisualApproach_UsesExpandedPhrasing()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: true, approachId: "VIS28R", distanceMilesForVfr: 0);

        Assert.Equal("[AAL123] tower, american one twenty three, visual approach runway two eight right.", result);
    }

    [Fact]
    public void BuildOnFinal_VfrNoApproach_ReportsDistanceAndAtis()
    {
        var ac = MakeAircraft("N123AB", isVfr: true);
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: false, approachId: null, distanceMilesForVfr: 3);

        Assert.Equal("[N123AB] tower, november one two three alpha bravo three-mile final runway two eight right, with information Alpha.", result);
    }

    [Fact]
    public void BuildOnFinal_WithRadioName_AddressesFacility()
    {
        var ac = MakeAircraft("N123AB", isVfr: true);
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: false, approachId: null, distanceMilesForVfr: 3, "Oakland Tower");

        Assert.Equal(
            "[N123AB] Oakland Tower, november one two three alpha bravo three-mile final runway two eight right, with information Alpha.",
            result
        );
    }

    [Fact]
    public void BuildOnFinal_VfrUnderOneMile_ClampsToOneMile()
    {
        var ac = MakeAircraft("N123AB", isVfr: true);
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: false, approachId: null, distanceMilesForVfr: 0);

        Assert.Contains("one-mile final", result);
    }

    [Fact]
    public void BuildOnFinal_IfrNoApproach_FallsBackToVfrTemplate()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildOnFinal(ac, "9", ifrWithActiveApproach: false, approachId: null, distanceMilesForVfr: 5);

        Assert.Equal("[AAL123] tower, american one twenty three five-mile final runway nine, with information Alpha.", result);
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
            "[N123AB] tower, november one two three alpha bravo, three miles south at one thousand five hundred, request closed traffic, with information Alpha.",
            result
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
            "[AAL123] tower, american one twenty three, five miles east at two thousand, request closed traffic, with information Alpha.",
            result
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
            "[N123AB] Oakland Tower, november one two three alpha bravo, three miles south at one thousand five hundred, request closed traffic, with information Alpha.",
            result
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
        Assert.Equal("[N123AB] Approach on one two five point three five, november one two three alpha bravo, so long.", result);
    }

    [Fact]
    public void BuildContactReadback_AirlineCallsign_UsesTelephony()
    {
        var ac = MakeAircraft("AAL123");

        var result = PilotResponder.BuildContactReadback(ac, "Departure", 119.6);

        Assert.Equal("[AAL123] Departure on one one nine point six, american one twenty three, so long.", result);
    }

    [Fact]
    public void BuildContactReadback_PreservesFacilityNameCasing()
    {
        var ac = MakeAircraft("N123AB");

        // Caller may pass a Position.RadioName like "NorCal Approach" or "Oakland Tower" —
        // multi-word natural casing must reach the terminal and TTS unchanged.
        var result = PilotResponder.BuildContactReadback(ac, "NorCal Approach", 125.35);

        Assert.Contains("NorCal Approach on", result);
    }

    // --- BuildFrequencyChangeApproved ---

    [Fact]
    public void BuildFrequencyChangeApproved_PilotSignsOff_DoesNotParrotControllerPhrase()
    {
        // Per AIM 4-2-3 ¶3, pilots acknowledge with a sign-off, not a verbatim recital of
        // "frequency change approved" (which is the controller's phraseology in 7110.65 §7-6-11).
        var ac = MakeAircraft("N123AB", isVfr: true);

        var result = PilotResponder.BuildFrequencyChangeApproved(ac);

        Assert.Equal("[N123AB] november one two three alpha bravo, good day.", result);
        Assert.DoesNotContain("frequency change approved", result);
    }

    [Fact]
    public void BuildFrequencyChangeApproved_AirlineCallsign_UsesTelephony()
    {
        var ac = MakeAircraft("AAL123");

        var result = PilotResponder.BuildFrequencyChangeApproved(ac);

        Assert.Equal("[AAL123] american one twenty three, good day.", result);
    }

    // --- BuildMidfieldDownwindReminder ---

    [Fact]
    public void BuildMidfieldDownwindReminder_FormatsRunwaySpoken()
    {
        var ac = MakeAircraft("N123AB", isVfr: true);
        var result = PilotResponder.BuildMidfieldDownwindReminder(ac, "28R");

        Assert.Equal("[N123AB] november one two three alpha bravo, midfield downwind runway two eight right.", result);
    }

    [Fact]
    public void BuildMidfieldDownwindReminder_AirlineCallsign_UsesTelephony()
    {
        var ac = MakeAircraft("AAL123", isVfr: true);
        var result = PilotResponder.BuildMidfieldDownwindReminder(ac, "9L");

        Assert.Equal("[AAL123] american one twenty three, midfield downwind runway nine left.", result);
    }

    // --- BuildShortFinalReminder ---

    [Fact]
    public void BuildShortFinalReminder_FormatsRunwaySpoken()
    {
        var ac = MakeAircraft("N123AB", isVfr: true);
        var result = PilotResponder.BuildShortFinalReminder(ac, "28R");

        Assert.Equal("[N123AB] november one two three alpha bravo, short final runway two eight right.", result);
    }

    [Fact]
    public void BuildShortFinalReminder_NoSuffixRunway()
    {
        var ac = MakeAircraft("N123AB", isVfr: true);
        var result = PilotResponder.BuildShortFinalReminder(ac, "9");

        Assert.Equal("[N123AB] november one two three alpha bravo, short final runway nine.", result);
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

        Assert.Contains("negative contact", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("november seven eight four mike echo", result);
    }

    [Fact]
    public void BuildGoingAround_IncludesReason()
    {
        var ac = MakeAircraft("FDX3807");
        var result = PilotResponder.BuildGoingAround(ac, "no landing clearance");

        Assert.Contains("[FDX3807]", result);
        Assert.Contains("going around", result);
        Assert.Contains("(no landing clearance)", result);
    }

    [Fact]
    public void BuildGoingAround_EmptyReason_OmitsParenthetical()
    {
        var ac = MakeAircraft("FDX3807");
        var result = PilotResponder.BuildGoingAround(ac, "");

        Assert.EndsWith("going around.", result);
        Assert.DoesNotContain("()", result);
    }

    [Fact]
    public void BuildHoldingShortCrossing_FormatsRunway()
    {
        var ac = MakeAircraft("N172SP");
        var result = PilotResponder.BuildHoldingShortCrossing(ac, "28R");

        Assert.Contains("holding short runway two eight right", result);
        Assert.Contains("[N172SP]", result);
    }

    [Fact]
    public void BuildClearOfRunway_NamesRunwayAndTaxiway()
    {
        var ac = MakeAircraft("N569SX");
        var result = PilotResponder.BuildClearOfRunway(ac, "28R", "G");

        Assert.Contains("clear of runway two eight right", result);
        Assert.Contains("at G", result);
    }

    [Fact]
    public void BuildUnableToExit_UsesNegative()
    {
        var ac = MakeAircraft("N123AB");
        var result = PilotResponder.BuildUnableToExit(ac, "M2");

        Assert.Contains("negative", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("M2", result);
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

        Assert.Contains("on the ground", result);
        Assert.Contains("breaking off the follow", result);
    }

    [Fact]
    public void BuildUnableToCatchUp_NamesTarget()
    {
        var ac = MakeAircraft("N294MG");
        var result = PilotResponder.BuildUnableToCatchUp(ac, "N784ME");

        Assert.Contains("unable to catch up", result);
        Assert.Contains("breaking off", result);
    }

    // --- RouteRpoTransmission three-way branch ---

    [Fact]
    public void RouteRpoTransmission_SoloMode_RoutesToWarnings_NotPilotSpeech()
    {
        // Caller is responsible for solo→PendingNotifications routing; this helper only
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
        // their own routing via PendingNotifications, so this helper conservatively falls
        // back to warning text.
        var ac = MakeAircraft("AAL123");

        PilotResponder.RouteRpoTransmission(ac, soloTrainingMode: true, rpoShowPilotSpeech: true, "speech", "warning");

        Assert.Empty(ac.PendingPilotSpeech);
        Assert.Equal("warning", Assert.Single(ac.PendingWarnings));
    }
}
