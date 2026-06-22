using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// M10.1.2: airborne-spawn check-ins. Covers <see cref="PilotResponder.BuildAirborneCheckIn"/>
/// template branches (IFR Approach/Center/Tower; VFR inbound/transit/no-dest at
/// Tower/Approach/Center), gates on <see cref="SimScenarioState.StudentPositionType"/>
/// and <see cref="AircraftState.HasMadeInitialContact"/>, and the
/// <see cref="PilotProactive.TickAirborneCheckIn"/> driver's idempotency.
/// </summary>
public class M102AirborneCheckInTests
{
    public M102AirborneCheckInTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // KOAK-ish reference; tests use ProjectPoint to place aircraft at exact bearings/distances.
    private static readonly LatLon AirportPos = new(37.7212, -122.2208);

    private static AircraftState MakeAircraft(
        string callsign = "AAL123",
        bool isVfr = false,
        string destination = "",
        double altitude = 6000,
        double bearingFromAirport = 180,
        double distanceNm = 5,
        double headingDeg = 0,
        string? destinationRunway = null
    )
    {
        var pos = GeoMath.ProjectPoint(AirportPos, new TrueHeading(bearingFromAirport), distanceNm);
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = pos,
            TrueHeading = new TrueHeading(headingDeg),
            Altitude = altitude,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { FlightRules = isVfr ? "VFR" : "IFR", Destination = destination },
            Procedure = new AircraftProcedure { DestinationRunway = destinationRunway },
        };
    }

    private static SimScenarioState MakeScenario(string? positionType, bool soloMode = true, string? primaryAirport = "KOAK") =>
        new()
        {
            ScenarioId = "test",
            ScenarioName = "test",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = primaryAirport,
            SoloTrainingMode = soloMode,
            StudentPositionType = positionType,
        };

    private static string PilotLineAt(AircraftState ac, int index) => ac.PendingPilotTransmissions[index].Text;

    private static SimScenarioState MakeScenarioWithStudentRadioName(string positionType, string callsign, string radioName)
    {
        var scenario = MakeScenario(positionType);
        scenario.StudentPosition = TrackOwner.CreateStars(callsign, "OAK", 3, "O");
        scenario.ArtccConfig = new ArtccConfigRoot
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
                        Positions =
                        [
                            new PositionConfig
                            {
                                Id = "oak-twr",
                                Callsign = callsign,
                                RadioName = radioName,
                            },
                        ],
                    },
                ],
            },
        };
        return scenario;
    }

    // ─────────────────────────────────────────────────────────────────────
    // PilotResponder.BuildAirborneCheckIn — IFR
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ifr_Approach_SubFL180_LevelAltitudeForm()
    {
        var ac = MakeAircraft("AAL123", isVfr: false, altitude: 6000);
        var sc = MakeScenario("APP");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal("approach, american one twenty three, level six thousand, with information Alpha.", line!.Tts);
    }

    [Fact]
    public void Ifr_Approach_HeavyAircraft_AppendsHeavyToTerminalAndTts()
    {
        // AIM 4-2-4.a.5: a heavy aircraft identifies itself with "heavy" after the call sign,
        // in both the spoken (TTS) and the terminal SAY echo (the airborne check-in is the one
        // path that carries the callsign inline in the terminal text).
        var ac = MakeAircraft("AAL123", isVfr: false, altitude: 6000);
        ac.AircraftType = "B763"; // CWT C -> Heavy
        var sc = MakeScenario("APP");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.NotNull(line);
        Assert.Equal("approach, american one twenty three heavy, level six thousand, with information Alpha.", line!.Tts);
        Assert.Contains("AAL123 heavy", line.Terminal);
    }

    [Fact]
    public void Ifr_Approach_FL180Plus_FlightLevelForm()
    {
        var ac = MakeAircraft("AAL123", isVfr: false, altitude: 23000);
        var sc = MakeScenario("APP");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal("approach, american one twenty three, flight level two three zero, with information Alpha.", line!.Tts);
    }

    [Fact]
    public void Ifr_Center_FL180Plus_FlightLevelOnly_NoAtisSuffix()
    {
        var ac = MakeAircraft("AAL123", isVfr: false, altitude: 24000);
        var sc = MakeScenario("CTR");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal("center, american one twenty three, flight level two four zero.", line!.Tts);
    }

    [Fact]
    public void Ifr_Center_SubFL180_LevelAltitudeForm_WithAtis()
    {
        var ac = MakeAircraft("AAL123", isVfr: false, altitude: 12000);
        var sc = MakeScenario("CTR");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal("center, american one twenty three, level one two thousand, with information Alpha.", line!.Tts);
    }

    [Fact]
    public void Ifr_Tower_DirectToTower_WithDestinationRunway()
    {
        var ac = MakeAircraft("AAL123", isVfr: false, altitude: 2500, destinationRunway: "28R");
        var sc = MakeScenario("TWR");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal("tower, american one twenty three, runway two eight right, with information Alpha.", line!.Tts);
    }

    [Fact]
    public void Ifr_Tower_NoDestinationRunway_DropsRunwayClause()
    {
        var ac = MakeAircraft("AAL123", isVfr: false, altitude: 2500);
        var sc = MakeScenario("TWR");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal("tower, american one twenty three, with information Alpha.", line!.Tts);
    }

    [Fact]
    public void Ifr_Tower_UsesStudentPositionRadioName()
    {
        var ac = MakeAircraft("AAL123", isVfr: false, altitude: 2500, destinationRunway: "28R");
        var sc = MakeScenarioWithStudentRadioName("TWR", "OAK_TWR", "Oakland Tower");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal("Oakland Tower, american one twenty three, runway two eight right, with information Alpha.", line!.Tts);
    }

    // ─────────────────────────────────────────────────────────────────────
    // IFR vertical-state verb: descend-via / climb-via / climbing / descending.
    // AIM 5-3-1.b.2.a (level / leaving X climbing|descending Y), 5-4-1.b.2 (STAR),
    // 5-2-9.b.9 (SID). Altitude rounds to the nearest 100 ft; FL form ≥ FL180.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ifr_Approach_DescendingViaStar_RoundsToFlightLevel_NamesArrival()
    {
        // Repro of the bundle bug: 25,264 ft descending via RAZRR5 was reported "level 25331".
        var ac = MakeAircraft("AAL123", isVfr: false, altitude: 25264);
        ac.Procedure.StarViaMode = true;
        ac.Procedure.ActiveStarId = "RAZRR5";
        ac.Targets.TargetAltitude = 20000;
        var sc = MakeScenario("APP");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        var (procTerm, procTts) = PhraseologyVerbalizer.ProcedureName("RAZRR5");
        Assert.Equal(
            $"approach, american one twenty three, leaving flight level two five three, descending via the {procTts} arrival, with information Alpha.",
            line!.Tts
        );
        Assert.Equal($"approach, AAL123, leaving FL253, descending via the {procTerm} arrival, with information Alpha.", line.Terminal);
    }

    [Fact]
    public void Ifr_Approach_ClimbingViaSid_IsDeparture_DropsAtis()
    {
        var ac = MakeAircraft("AAL123", isVfr: false, altitude: 2000);
        ac.Procedure.SidViaMode = true;
        ac.Procedure.ActiveSidId = "LAURA2";
        ac.Targets.TargetAltitude = 14000;
        var sc = MakeScenario("APP");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        var (_, procTts) = PhraseologyVerbalizer.ProcedureName("LAURA2");
        Assert.Equal($"approach, american one twenty three, leaving two thousand, climbing via the {procTts} departure.", line!.Tts);
    }

    [Fact]
    public void Ifr_Approach_PlainClimbing_WithAssigned_LeavingClimbing_DropsAtis()
    {
        var ac = MakeAircraft("AAL123", isVfr: false, altitude: 8000);
        ac.Targets.AssignedAltitude = 14000;
        var sc = MakeScenario("APP");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal("approach, american one twenty three, leaving eight thousand climbing one four thousand.", line!.Tts);
    }

    [Fact]
    public void Ifr_Approach_PlainDescending_WithAssigned_LeavingDescending_KeepsAtis()
    {
        var ac = MakeAircraft("AAL123", isVfr: false, altitude: 15000);
        ac.Targets.AssignedAltitude = 9000;
        var sc = MakeScenario("APP");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal("approach, american one twenty three, leaving one five thousand descending nine thousand, with information Alpha.", line!.Tts);
    }

    [Fact]
    public void Ifr_Approach_Descending_AssignedUnknown_OmitsTargetAltitude()
    {
        var ac = MakeAircraft("AAL123", isVfr: false, altitude: 15000);
        ac.Targets.TargetAltitude = 9000; // direction only; no discrete assigned altitude
        var sc = MakeScenario("APP");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal("approach, american one twenty three, leaving one five thousand descending, with information Alpha.", line!.Tts);
    }

    [Fact]
    public void Ifr_Approach_Level_TerminalIncludesCallsign()
    {
        var ac = MakeAircraft("AAL123", isVfr: false, altitude: 6000);
        var sc = MakeScenario("APP");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal("approach, AAL123, level 6000, with information Alpha.", line!.Terminal);
    }

    [Theory]
    [InlineData("ZZZZZ5", "ZZZZZ5", "zzzzz five")]
    [InlineData("ZZZZZ12", "ZZZZZ12", "zzzzz twelve")]
    public void ProcedureName_SplitsTrailingVersionDigits(string id, string expectedTerm, string expectedTts)
    {
        var (term, tts) = PhraseologyVerbalizer.ProcedureName(id);
        Assert.Equal(expectedTerm, term);
        Assert.Equal(expectedTts, tts);
    }

    // ─────────────────────────────────────────────────────────────────────
    // PilotResponder.BuildAirborneCheckIn — VFR
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Vfr_Tower_DestinationMatchesPrimary_InboundForLanding()
    {
        var ac = MakeAircraft("N123AB", isVfr: true, altitude: 2000, destination: "KOAK", bearingFromAirport: 180, distanceNm: 5);
        var sc = MakeScenario("TWR", primaryAirport: "KOAK");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal(
            "tower, november one two three alpha bravo five miles south at two thousand, inbound for landing, with information Alpha.",
            line!.Tts
        );
    }

    [Fact]
    public void Vfr_Tower_DestinationIsKPrefixedPrimary_InboundForLanding()
    {
        // Issue #154 #4: scenario JSON often files destination as the full ICAO (KOAK)
        // while PrimaryAirportId is the bare FAA id (OAK). A naïve case-insensitive
        // Equals would mismatch and route through the transit phrasing.
        TestVnasData.EnsureInitialized();
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);

        var ac = MakeAircraft("N123AB", isVfr: true, altitude: 2000, destination: "KOAK", bearingFromAirport: 180, distanceNm: 5);
        var sc = MakeScenario("TWR", primaryAirport: "OAK");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal(
            "tower, november one two three alpha bravo five miles south at two thousand, inbound for landing, with information Alpha.",
            line!.Tts
        );
    }

    [Fact]
    public void Vfr_Tower_DestinationDifferent_RequestTransition()
    {
        var ac = MakeAircraft("N123AB", isVfr: true, altitude: 2500, destination: "KSQL", bearingFromAirport: 90, distanceNm: 8);
        var sc = MakeScenario("TWR", primaryAirport: "KOAK");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal("tower, november one two three alpha bravo eight miles east at two thousand five hundred, request transition.", line!.Tts);
    }

    [Fact]
    public void Vfr_Tower_NoDestination_HeadingSouthbound_VfrTransition()
    {
        var ac = MakeAircraft("N123AB", isVfr: true, altitude: 1500, destination: "", bearingFromAirport: 0, distanceNm: 4, headingDeg: 180);
        var sc = MakeScenario("TWR", primaryAirport: "KOAK");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal(
            "tower, november one two three alpha bravo four miles north of the field, VFR southbound at one thousand five hundred.",
            line!.Tts
        );
    }

    [Fact]
    public void Vfr_Approach_DestinationMatchesPrimary_RequestLanding()
    {
        var ac = MakeAircraft("N123AB", isVfr: true, altitude: 3000, destination: "KOAK", bearingFromAirport: 270, distanceNm: 10);
        var sc = MakeScenario("APP", primaryAirport: "KOAK");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal(
            "approach, november one two three alpha bravo one zero miles west at three thousand, request landing, with information Alpha.",
            line!.Tts
        );
    }

    [Fact]
    public void Vfr_Approach_DestinationDifferent_RequestTransition()
    {
        var ac = MakeAircraft("N123AB", isVfr: true, altitude: 3500, destination: "KSFO", bearingFromAirport: 45, distanceNm: 7);
        var sc = MakeScenario("APP", primaryAirport: "KOAK");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal(
            "approach, november one two three alpha bravo seven miles northeast at three thousand five hundred, request transition.",
            line!.Tts
        );
    }

    [Fact]
    public void Vfr_Approach_NoDestination_HeadingNorthbound_VfrTransition()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);
        var ac = MakeAircraft("N123AB", isVfr: true, altitude: 4500, destination: "", bearingFromAirport: 135, distanceNm: 6, headingDeg: 0);
        var sc = MakeScenario("APP", primaryAirport: "KOAK");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal(
            "approach, november one two three alpha bravo six miles southeast of Oakland Airport, VFR northbound at four thousand five hundred.",
            line!.Tts
        );
    }

    [Fact]
    public void Vfr_Center_DestinationDifferent_RequestTransition_AirportSpelled()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);
        var ac = MakeAircraft("N123AB", isVfr: true, altitude: 8500, destination: "KLAX", bearingFromAirport: 0, distanceNm: 15);
        var sc = MakeScenario("CTR", primaryAirport: "KOAK");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal(
            "center, november one two three alpha bravo at eight thousand five hundred, one five miles north of Oakland Airport, request transition.",
            line!.Tts
        );
    }

    [Fact]
    public void Vfr_Center_NoDestination_HeadingEastbound_VfrTransition()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);
        var ac = MakeAircraft("N123AB", isVfr: true, altitude: 7500, destination: "", bearingFromAirport: 225, distanceNm: 12, headingDeg: 90);
        var sc = MakeScenario("CTR", primaryAirport: "KOAK");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal(
            "center, november one two three alpha bravo one two miles southwest of Oakland Airport, VFR eastbound at seven thousand five hundred.",
            line!.Tts
        );
    }

    // ─────────────────────────────────────────────────────────────────────
    // Sub-100 ft altitude — AtcNumberParser.AltitudeToWords returns "".
    // Format strings must drop the "at {altitudeWords}" / "level {altitudeWords}"
    // clause rather than emit a dangling "at , " or "level , ".
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Vfr_Tower_DestinationDifferent_LowAltitude_OmitsAtAltitudeClause()
    {
        var ac = MakeAircraft("N123AB", isVfr: true, altitude: 50, destination: "KSQL", bearingFromAirport: 90, distanceNm: 1);
        var sc = MakeScenario("TWR", primaryAirport: "KOAK");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal("tower, november one two three alpha bravo one miles east, request transition.", line!.Tts);
    }

    [Fact]
    public void Vfr_Tower_NoDestination_LowAltitude_OmitsAtAltitudeClause()
    {
        var ac = MakeAircraft("N123AB", isVfr: true, altitude: 50, destination: "", bearingFromAirport: 0, distanceNm: 1, headingDeg: 180);
        var sc = MakeScenario("TWR", primaryAirport: "KOAK");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal("tower, november one two three alpha bravo one miles north of the field, VFR southbound.", line!.Tts);
    }

    [Fact]
    public void Vfr_Center_DestinationDifferent_LowAltitude_OmitsAtAltitudeClause()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }
        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);
        var ac = MakeAircraft("N123AB", isVfr: true, altitude: 50, destination: "KLAX", bearingFromAirport: 0, distanceNm: 15);
        var sc = MakeScenario("CTR", primaryAirport: "KOAK");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal("center, november one two three alpha bravo, one five miles north of Oakland Airport, request transition.", line!.Tts);
    }

    [Fact]
    public void Ifr_Approach_LowAltitude_OmitsLevelAltitudeClause()
    {
        var ac = MakeAircraft("AAL123", isVfr: false, altitude: 50);
        var sc = MakeScenario("APP");

        var line = PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos);

        Assert.Equal("approach, american one twenty three, with information Alpha.", line!.Tts);
    }

    // ─────────────────────────────────────────────────────────────────────
    // BuildAirborneCheckIn — skip / fall-through cases
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Skip_StudentPositionTypeIsGround()
    {
        var ac = MakeAircraft();
        var sc = MakeScenario("GND");

        Assert.Null(PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos));
    }

    [Fact]
    public void Skip_StudentPositionTypeIsNull()
    {
        var ac = MakeAircraft();
        var sc = MakeScenario(null);

        Assert.Null(PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos));
    }

    [Fact]
    public void Skip_StudentPositionTypeUnknown()
    {
        var ac = MakeAircraft();
        var sc = MakeScenario("FSS");

        Assert.Null(PilotResponder.BuildAirborneCheckIn(ac, sc, AirportPos));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Direction quantizer unit tests
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "north")]
    [InlineData(22.4, "north")]
    [InlineData(22.6, "northeast")]
    [InlineData(45, "northeast")]
    [InlineData(67.4, "northeast")]
    [InlineData(67.6, "east")]
    [InlineData(90, "east")]
    [InlineData(135, "southeast")]
    [InlineData(180, "south")]
    [InlineData(225, "southwest")]
    [InlineData(270, "west")]
    [InlineData(315, "northwest")]
    [InlineData(337.4, "northwest")]
    [InlineData(337.6, "north")]
    [InlineData(359.9, "north")]
    [InlineData(360, "north")]
    public void BearingToCardinal8_QuantizesCorrectly(double bearingDeg, string expected)
    {
        Assert.Equal(expected, PilotResponder.BearingToCardinal8(bearingDeg));
    }

    [Theory]
    [InlineData(0, "northbound")]
    [InlineData(44.9, "northbound")]
    [InlineData(45.1, "eastbound")]
    [InlineData(90, "eastbound")]
    [InlineData(134.9, "eastbound")]
    [InlineData(135.1, "southbound")]
    [InlineData(180, "southbound")]
    [InlineData(224.9, "southbound")]
    [InlineData(225.1, "westbound")]
    [InlineData(270, "westbound")]
    [InlineData(314.9, "westbound")]
    [InlineData(315.1, "northbound")]
    [InlineData(359.9, "northbound")]
    public void HeadingToBoundCardinal_QuantizesCorrectly(double headingDeg, string expected)
    {
        Assert.Equal(expected, PilotResponder.HeadingToBoundCardinal(headingDeg));
    }

    // ─────────────────────────────────────────────────────────────────────
    // PilotProactive.TickAirborneCheckIn — gate / idempotency tests
    // ─────────────────────────────────────────────────────────────────────

    private static Func<string, LatLon?> AirportLookup(string airportId, LatLon pos) => id => id == airportId ? pos : null;

    [Fact]
    public void TickAirborneCheckIn_FiresOnceAndSetsHasMadeInitialContact()
    {
        var ac = MakeAircraft("AAL123", altitude: 6000);
        var sc = MakeScenario("APP", primaryAirport: "KOAK");

        PilotProactive.TickAirborneCheckIn(ac, sc, AirportLookup("KOAK", AirportPos));

        Assert.Single(ac.PendingPilotTransmissions);
        Assert.Contains("approach", PilotLineAt(ac, 0));
        Assert.True(ac.HasMadeInitialContact);
    }

    [Fact]
    public void TickAirborneCheckIn_DoesNotRefireOnSubsequentTicks()
    {
        var ac = MakeAircraft("AAL123", altitude: 6000);
        var sc = MakeScenario("APP", primaryAirport: "KOAK");
        var lookup = AirportLookup("KOAK", AirportPos);

        PilotProactive.TickAirborneCheckIn(ac, sc, lookup);
        PilotProactive.TickAirborneCheckIn(ac, sc, lookup);
        PilotProactive.TickAirborneCheckIn(ac, sc, lookup);

        Assert.Single(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void TickAirborneCheckIn_OnGround_DoesNotFire()
    {
        var ac = MakeAircraft();
        ac.IsOnGround = true;
        var sc = MakeScenario("APP", primaryAirport: "KOAK");

        PilotProactive.TickAirborneCheckIn(ac, sc, AirportLookup("KOAK", AirportPos));

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.False(ac.HasMadeInitialContact);
    }

    [Fact]
    public void TickAirborneCheckIn_HasMadeInitialContact_DoesNotFire()
    {
        var ac = MakeAircraft();
        ac.HasMadeInitialContact = true;
        var sc = MakeScenario("APP", primaryAirport: "KOAK");

        PilotProactive.TickAirborneCheckIn(ac, sc, AirportLookup("KOAK", AirportPos));

        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void TickAirborneCheckIn_StudentPositionTypeGround_DoesNotFire()
    {
        var ac = MakeAircraft();
        var sc = MakeScenario("GND", primaryAirport: "KOAK");

        PilotProactive.TickAirborneCheckIn(ac, sc, AirportLookup("KOAK", AirportPos));

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.False(ac.HasMadeInitialContact);
    }

    [Fact]
    public void TickAirborneCheckIn_StudentPositionTypeNull_DoesNotFire()
    {
        var ac = MakeAircraft();
        var sc = MakeScenario(null, primaryAirport: "KOAK");

        PilotProactive.TickAirborneCheckIn(ac, sc, AirportLookup("KOAK", AirportPos));

        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void TickAirborneCheckIn_SoloModeOff_DoesNotFire()
    {
        var ac = MakeAircraft();
        var sc = MakeScenario("APP", soloMode: false, primaryAirport: "KOAK");

        PilotProactive.TickAirborneCheckIn(ac, sc, AirportLookup("KOAK", AirportPos));

        Assert.Empty(ac.PendingPilotTransmissions);
    }

    [Fact]
    public void TickAirborneCheckIn_AirportLookupReturnsNull_DoesNotFire()
    {
        var ac = MakeAircraft();
        var sc = MakeScenario("APP", primaryAirport: "KOAK");

        // Lookup returns null for unknown airport — VFR template needs the airport position,
        // and IFR Approach/Center don't strictly need it but the gate is uniform: no airport,
        // no fire.
        PilotProactive.TickAirborneCheckIn(ac, sc, _ => null);

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.False(ac.HasMadeInitialContact);
    }

    [Fact]
    public void TickAirborneCheckIn_ApproachStudent_WaitsForAcceptedHandoff()
    {
        var student = TrackOwner.CreateStars("NCT_APP", "NCT", 4, "A");
        var previousOwner = TrackOwner.CreateStars("ZOA_CTR", "ZOA", 1, "C");
        var ac = MakeAircraft("AAL123", altitude: 6000);
        ac.Track.Owner = previousOwner;
        ac.Track.HandoffPeer = student;
        var sc = MakeScenario("APP", primaryAirport: "KOAK");
        sc.StudentPosition = student;

        PilotProactive.TickAirborneCheckIn(ac, sc, AirportLookup("KOAK", AirportPos));

        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.False(ac.HasMadeInitialContact);

        ac.Track.Owner = student;
        ac.Track.HandoffPeer = null;
        PilotProactive.TickAirborneCheckIn(ac, sc, AirportLookup("KOAK", AirportPos));

        Assert.Single(ac.PendingPilotTransmissions);
        Assert.True(ac.HasMadeInitialContact);
    }
}
