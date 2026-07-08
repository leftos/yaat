using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

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
    public void BuildReadback_LineUpAndWaitWithoutDelay_AppendsWithoutDelay()
    {
        var ac = MakeAircraftWithAssignedRunway("N436MS", "28R");
        var compound = Compound(new LineUpAndWaitCommand { WithoutDelay = true });

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Contains("line up and wait runway two eight right", result!);
        Assert.Contains("without delay", result);
    }

    [Fact]
    public void BuildReadback_ClearedForImmediateTakeoff_SaysImmediate()
    {
        var ac = MakeAircraftWithAssignedRunway("AAL123", "28R");
        var compound = Compound(new ClearedForTakeoffCommand(new DefaultDeparture()) { Immediate = true });

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Contains("cleared for immediate takeoff runway two eight right", result!);
    }

    // --- A4-6: "caution wake turbulence" is a controller advisory, not a pilot readback item ---

    [Fact]
    public void BuildReadback_ClearedForTakeoffWithWakeCaution_OmitsControllerAdvisory()
    {
        var ac = MakeAircraftWithAssignedRunway("AAL123", "28R");
        var compound = Compound(new ClearedForTakeoffCommand(new DefaultDeparture()) { CautionWakeTurbulence = true });

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.NotNull(result);
        Assert.DoesNotContain("caution wake turbulence", result!.Tts);
        Assert.DoesNotContain("caution wake turbulence", result.Terminal);
        Assert.Contains("cleared for takeoff runway two eight right", result.Tts);
    }

    [Fact]
    public void BuildReadback_ClearedToLandWithWakeCaution_OmitsControllerAdvisory()
    {
        var ac = MakeAircraftWithAssignedRunway("AAL123", "28R");
        var compound = Compound(new ClearedToLandCommand { CautionWakeTurbulence = true });

        var result = PilotResponder.BuildReadback(compound, ac);

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

    // --- A8-1: heavy/super wake-class suffix on the aircraft's own callsign (AIM 4-2-4.a.5) ---

    [Fact]
    public void BuildReadback_HeavyAircraft_AppendsHeavyToSpokenCallsign()
    {
        var ac = MakeAircraft("AAL123");
        ac.AircraftType = "B77W"; // CWT B -> Heavy
        var compound = Compound(new DescendMaintainCommand(5000));

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Equal("descend and maintain five thousand, american one twenty three heavy.", result);
    }

    [Fact]
    public void BuildReadback_SuperAircraft_AppendsSuperToSpokenCallsign()
    {
        var ac = MakeAircraft("AAL123");
        ac.AircraftType = "A388"; // CWT A -> Super
        var compound = Compound(new DescendMaintainCommand(5000));

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Equal("descend and maintain five thousand, american one twenty three super.", result);
    }

    [Fact]
    public void BuildReadback_LargeAircraft_NoWakeSuffix()
    {
        var ac = MakeAircraft("AAL123"); // B738 default -> Large (CWT F)
        var compound = Compound(new DescendMaintainCommand(5000));

        var result = PilotResponder.BuildReadback(compound, ac)?.Tts;

        Assert.Equal("descend and maintain five thousand, american one twenty three.", result);
    }

    [Fact]
    public void BuildHoldingShortReady_HeavyAircraft_AppendsHeavyToProactiveCallsign()
    {
        var ac = MakeAircraft("AAL123");
        ac.AircraftType = "B763"; // CWT C -> Heavy

        var result = PilotResponder.BuildHoldingShortReady(ac, "28R");

        Assert.Equal("tower, american one twenty three heavy holding short runway two eight right, ready for departure.", result.Tts);
    }

    // --- A1-2: data-driven ATIS letter + suppression when the field has no ATIS ---

    [Fact]
    public void BuildReadyToTaxi_NoAtis_DropsInformationClause()
    {
        var ac = MakeAircraft("N123AB", parkingSpot: "GATE B22");

        var result = PilotResponder.BuildReadyToTaxi(ac, "ground", atisLetter: null);

        Assert.Equal("ground, november one two three alpha bravo at gate b22, ready to taxi.", result.Tts);
        Assert.DoesNotContain("information", result.Tts);
    }

    [Fact]
    public void BuildReadyToTaxi_NonDefaultLetter_SpeaksThatLetter()
    {
        var ac = MakeAircraft("N123AB", parkingSpot: "GATE B22");

        var result = PilotResponder.BuildReadyToTaxi(ac, "ground", atisLetter: "B");

        Assert.Contains("with information Bravo", result.Tts);
    }

    [Fact]
    public void BuildClosedTrafficRequest_NoAtis_DropsInformationClause()
    {
        var airport = new LatLon(37.7212, -122.2208);
        var ac = MakeAircraft("N123AB", isVfr: true);
        ac.Position = GeoMath.ProjectPoint(airport, new TrueHeading(180), 3);

        var result = PilotResponder.BuildClosedTrafficRequest(ac, airport, altitudeFt: 1500, "tower", atisLetter: null);

        Assert.EndsWith("request closed traffic.", result.Tts);
        Assert.DoesNotContain("information", result.Tts);
    }

    [Fact]
    public void ResolvePrimaryFieldAtisLetter_FieldHasAtis_ReturnsScenarioLetter()
    {
        var sc = ScenarioWithPrimaryFieldAtis("KOAK", hasAtis: true, atisLetter: "C");

        Assert.Equal("C", PilotResponder.ResolvePrimaryFieldAtisLetter(sc));
    }

    [Fact]
    public void ResolvePrimaryFieldAtisLetter_FieldHasNoAtisPosition_ReturnsNull()
    {
        var sc = ScenarioWithPrimaryFieldAtis("KOAK", hasAtis: false, atisLetter: "A");

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

    // --- A4-1: TAXI readback completeness (path + runway + cross + hold-short all echoed) ---

    [Fact]
    public void BuildReadback_TaxiWithRunwayCrossAndHoldShort_ReadsBackEveryCapture()
    {
        var ac = MakeAircraft("N225R");
        // "taxi to runway 28L via B C, cross runway 10R, hold short runway 28R" — the richest
        // capture set. PickPreferredRule must choose the four-capture TAXI variant so none of the
        // path / destination runway / cross runway / hold-short clauses are dropped.
        var compound = Compound(new TaxiCommand(["B", "C"], ["28R"], DestinationRunway: "28L", CrossRunways: ["10R"]));

        var result = PilotResponder.BuildReadback(compound, ac);

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

    // --- A4-2: runway L/R/C suffix is never dropped from a taxi readback ---

    [Fact]
    public void BuildReadback_TaxiPreservesCenterRunwaySuffix()
    {
        var ac = MakeAircraft("N225R");
        var compound = Compound(new TaxiCommand(["A"], [], DestinationRunway: "1C"));

        var result = PilotResponder.BuildReadback(compound, ac);

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
        var result = PilotResponder.BuildReadyToTaxi(ac, "Oakland Ground", "A");

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
        var result = PilotResponder.BuildOnFinal(
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

        var result = PilotResponder.BuildClosedTrafficRequest(ac, airport, altitudeFt: 1500, "Oakland Tower", "A");

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
    public void BuildTrafficInSight_WithTargetCallsign_TtsOmitsTargetCallsign()
    {
        var ac = MakeAircraft("N294MG");
        var result = PilotResponder.BuildTrafficInSight(ac, "N784ME");

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
        var ac = MakeAircraft("N123AB");
        var result = PilotResponder.BuildTrafficInSight(ac, null);

        Assert.Equal("traffic in sight.", result.Terminal);
        Assert.Equal("november one two three alpha bravo, traffic in sight.", result.Tts);
    }

    [Fact]
    public void BuildFieldInSight_FormatsCallsign()
    {
        var ac = MakeAircraft("UAL238");
        var result = PilotResponder.BuildFieldInSight(ac);

        Assert.Equal("field in sight.", result.Terminal);
        Assert.Equal("united two thirty eight, field in sight.", result.Tts);
    }

    [Fact]
    public void BuildLostSightOfField_SaysLostSightOfField()
    {
        // "Negative contact" (PCG) means traffic never acquired / radio-contact failure — not the
        // loss of a previously-acquired visual. The pilot has lost sight of the field, so the
        // phraseology is "lost sight of the field".
        var ac = MakeAircraft("N172SP");
        var result = PilotResponder.BuildLostSightOfField(ac);

        Assert.Equal("lost sight of the field.", result.Terminal);
        Assert.Contains("lost sight of the field", result.Tts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("negative contact", result.Tts, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("november one seven two sierra papa,", result.Tts);
    }

    [Fact]
    public void BuildLostSightOfTraffic_TtsAndSoloTerminalOmitTarget()
    {
        var ac = MakeAircraft("N172SP");
        var result = PilotResponder.BuildLostSightOfTraffic(ac, "N784ME");

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
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        var ac = MakeAircraft("N123AB");
        var displayName = PhraseologyVerbalizer.FixDisplayText("VPCBT");
        // Guard: this visual fix has a human display name distinct from the raw alias, so the
        // assertions below are meaningful (not a vacuous "VPCBT" == "VPCBT").
        Assert.NotEqual("VPCBT", displayName);

        var result = PilotResponder.BuildAtFixReport(ac, "VPCBT");

        Assert.Equal($"passing {displayName}.", result.Terminal);
        Assert.EndsWith($"passing {PhraseologyVerbalizer.SpellFix("VPCBT")}.", result.Tts);
        Assert.DoesNotContain("VPCBT", result.Tts);
    }

    [Fact]
    public void BuildGoingAround_ReasonIsTerminalOnly_NotSpoken()
    {
        // The reason is a sim-internal diagnostic — it belongs in the controller-facing terminal
        // line, never in the spoken callout (the pilot just says "going around").
        var ac = MakeAircraft("FDX3807");
        var result = PilotResponder.BuildGoingAround(ac, "no landing clearance");

        Assert.Contains("(no landing clearance)", result.Terminal);
        Assert.EndsWith("going around.", result.Tts);
        Assert.DoesNotContain("no landing clearance", result.Tts);
    }

    [Fact]
    public void BuildGoingAround_EmptyReason_OmitsParenthetical()
    {
        var ac = MakeAircraft("FDX3807");
        var result = PilotResponder.BuildGoingAround(ac, "");

        Assert.EndsWith("going around.", result.Tts);
        Assert.DoesNotContain("()", result.Tts);
    }

    // --- A1-3: arrival approach reminder is a callsign-only prompt for an approach assignment; the
    //          pilot names neither runway nor approach type (the controller assigns both) ---

    [Fact]
    public void BuildArrivalApproachRequest_IsCallsignOnlyApproachAssignmentPrompt()
    {
        var ac = MakeAircraft("UAL325");

        var result = PilotResponder.BuildArrivalApproachRequest(ac);

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
        var ac = MakeAircraft("N123AB", isVfr: true);
        ac.Position = GeoMath.ProjectPoint(airport, new TrueHeading(180), 3);

        var result = PilotResponder.BuildClosedTrafficRequest(ac, airport, altitudeFt: 1487, "tower", "A");

        Assert.Contains("one thousand five hundred", result.Tts);
        Assert.DoesNotContain("eighty", result.Tts);
    }

    // --- A6-2: ready-to-taxi states op-type + destination (AIM 4-2-3) ---

    [Fact]
    public void BuildReadyToTaxi_IfrWithDestination_StatesOpTypeAndDestination()
    {
        var ac = MakeAircraft("AAL123");
        ac.FlightPlan.Destination = "KSFO";

        var result = PilotResponder.BuildReadyToTaxi(ac, "ground", "A");

        Assert.Contains(", IFR to ", result.Tts);
        Assert.EndsWith(", ready to taxi.", result.Tts);
    }

    [Fact]
    public void BuildReadyToTaxi_VfrLocalNoDestination_OmitsIntentClause()
    {
        var ac = MakeAircraft("N123AB", isVfr: true);

        var result = PilotResponder.BuildReadyToTaxi(ac, "ground", "A");

        Assert.DoesNotContain("VFR to", result.Tts);
        Assert.DoesNotContain("IFR to", result.Tts);
        Assert.EndsWith("ready to taxi.", result.Tts);
    }

    // --- A6-1: taxi hold-short report keeps the "holding short of" verb ---

    [Fact]
    public void BuildHoldingShortTaxi_KeepsHoldingShortOfVerb()
    {
        var ac = MakeAircraft("N172SP");

        var result = PilotResponder.BuildHoldingShortTaxi(ac, "holding short of 28R", "B");

        Assert.Contains("holding short of 28R at B", result.Tts);
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
    public void BuildUnableToMaintainSeparation_RpoTerminalNamesLead_TtsDoesNot()
    {
        var ac = MakeAircraft("N294MG");
        var result = PilotResponder.BuildUnableToMaintainSeparation(ac, "N10194");

        Assert.Equal("unable to maintain separation, breaking off the follow.", result.Terminal);
        Assert.Equal("unable to maintain separation from N10194, breaking off the follow.", result.TerminalForRpo);
        Assert.Contains("unable to maintain separation", result.Tts);
        Assert.DoesNotContain("november one zero one nine four", result.Tts);
    }

    [Fact]
    public void BuildTargetLanded_BreaksOff()
    {
        var ac = MakeAircraft("N294MG");
        var result = PilotResponder.BuildTargetLanded(ac, "N784ME");

        Assert.Equal("the traffic's on the ground, breaking off the follow.", result.Terminal);
        Assert.Equal("N784ME is on the ground, breaking off the follow.", result.TerminalForRpo);
        Assert.Contains("on the ground", result.Tts);
        Assert.Contains("breaking off the follow", result.Tts);
        Assert.DoesNotContain("seven eight four", result.Tts);
    }

    [Fact]
    public void BuildUnableToCatchUp_RpoTerminalNamesTarget_TtsDoesNot()
    {
        var ac = MakeAircraft("N294MG");
        var result = PilotResponder.BuildUnableToCatchUp(ac, "N784ME");

        Assert.Equal("unable to catch up to the traffic, breaking off the follow.", result.Terminal);
        Assert.Equal("unable to catch up to N784ME, breaking off the follow.", result.TerminalForRpo);
        Assert.Contains("unable to catch up", result.Tts);
        Assert.Contains("breaking off", result.Tts);
        Assert.DoesNotContain("seven eight four", result.Tts);
    }

    [Fact]
    public void BuildFollowExtendingUnableToTurn_RpoTerminalNamesTarget_TtsDoesNot()
    {
        var ac = MakeAircraft("N294MG");
        var result = PilotResponder.BuildFollowExtendingUnableToTurn(ac, "N784ME", "downwind");

        Assert.Equal("extending downwind behind the traffic, unable to turn — request instructions.", result.Terminal);
        Assert.Equal("extending downwind behind N784ME, unable to turn — request instructions.", result.TerminalForRpo);
        Assert.Contains("extending downwind behind the traffic", result.Tts);
        Assert.DoesNotContain("seven eight four", result.Tts);
    }

    [Fact]
    public void BuildSTurnsForSpacing_RpoTerminalNamesTarget_TtsDoesNot()
    {
        var ac = MakeAircraft("N294MG");
        var result = PilotResponder.BuildSTurnsForSpacing(ac, "N784ME");

        Assert.Equal("S-turning for spacing behind the traffic.", result.Terminal);
        Assert.Equal("S-turning for spacing behind N784ME.", result.TerminalForRpo);
        Assert.Contains("S-turning for spacing behind the traffic", result.Tts);
        Assert.DoesNotContain("seven eight four", result.Tts);
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

    // --- RouteRpoSayReadback dual-output branch (A5-2) ---

    [Fact]
    public void RouteRpoSayReadback_RpoShowsSpeech_UsesTtsForm()
    {
        var ac = MakeAircraft("N294MG");
        var text = new PilotSpeechText("traffic in sight.", "november two nine four mike golf, traffic in sight.");

        PilotResponder.RouteRpoSayReadback(ac, soloTrainingMode: false, rpoShowPilotSpeech: true, text);

        Assert.Equal("november two nine four mike golf, traffic in sight.", Assert.Single(ac.PendingPilotSpeech));
        Assert.Empty(ac.PendingPilotReadbacks);
    }

    [Fact]
    public void RouteRpoSayReadback_RpoNoSpeech_UsesTerminalForm()
    {
        var ac = MakeAircraft("N294MG");
        var text = new PilotSpeechText("traffic in sight.", "november two nine four mike golf, traffic in sight.");

        PilotResponder.RouteRpoSayReadback(ac, soloTrainingMode: false, rpoShowPilotSpeech: false, text);

        Assert.Equal("traffic in sight.", Assert.Single(ac.PendingPilotReadbacks));
        Assert.Empty(ac.PendingPilotSpeech);
    }

    [Fact]
    public void RouteRpoSayReadback_SoloMode_QueuesBothFormsAsSayReadback()
    {
        var ac = MakeAircraft("N294MG");
        var text = new PilotSpeechText("traffic in sight.", "november two nine four mike golf, traffic in sight.");

        PilotResponder.RouteRpoSayReadback(ac, soloTrainingMode: true, rpoShowPilotSpeech: true, text);

        var tx = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("traffic in sight.", tx.Text);
        Assert.Equal("november two nine four mike golf, traffic in sight.", tx.SpeechText);
        Assert.Empty(ac.PendingPilotSpeech);
        Assert.Empty(ac.PendingPilotReadbacks);
    }
}
