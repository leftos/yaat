using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

public class CifpParserTests
{
    [Theory]
    [InlineData("N38573910", 38.960861)]
    [InlineData("N37424600", 37.712778)]
    [InlineData("S33525000", -33.880556)]
    public void ParseArinc424Latitude_ValidInput_ReturnsDecimalDegrees(string input, double expected)
    {
        var result = CifpParser.ParseArinc424Latitude(input);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Value, precision: 4);
    }

    [Theory]
    [InlineData("W121292540", -121.490389)]
    [InlineData("W122131200", -122.220000)]
    [InlineData("E002174200", 2.295000)]
    public void ParseArinc424Longitude_ValidInput_ReturnsDecimalDegrees(string input, double expected)
    {
        var result = CifpParser.ParseArinc424Longitude(input);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Value, precision: 4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("N38")]
    [InlineData("X38573910")]
    public void ParseArinc424Latitude_InvalidInput_ReturnsNull(string input)
    {
        var result = CifpParser.ParseArinc424Latitude(input);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("W121")]
    [InlineData("X121292540")]
    public void ParseArinc424Longitude_InvalidInput_ReturnsNull(string input)
    {
        var result = CifpParser.ParseArinc424Longitude(input);

        Assert.Null(result);
    }

    [Fact]
    public void Parse_ApproachRecordWithFaf_ExtractsFafFix()
    {
        // Minimal CIFP file with one approach record containing a FAF (D at pos 42)
        // and one terminal waypoint record
        var lines = new[]
        {
            // Approach record: airport KOAK, subsection F at pos 12,
            // approach ID I28L (ILS 28L), FAF fix FITKI at pos 29-33,
            // waypoint desc D at pos 42
            BuildApproachLine("KOAK", "I28L  ", "FITKI", 'D'),
            // Terminal waypoint for FITKI
            BuildTerminalWaypointLine("KOAK", "FITKI", "N37424600", "W122131200"),
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var result = CifpParser.Parse(tmpFile);

            Assert.True(result.FafFixes.ContainsKey(("OAK", "28L")));
            Assert.Equal("FITKI", result.FafFixes[("OAK", "28L")]);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void Parse_IlsPreferredOverRnav_WhenBothExist()
    {
        var lines = new[]
        {
            // RNAV approach (H prefix = lower priority than ILS)
            BuildApproachLine("KOAK", "H28L  ", "RNFIX", 'F'),
            // ILS approach (I prefix = highest priority)
            BuildApproachLine("KOAK", "I28L  ", "ILFIX", 'F'),
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var result = CifpParser.Parse(tmpFile);

            Assert.True(result.FafFixes.ContainsKey(("OAK", "28L")));
            Assert.Equal("ILFIX", result.FafFixes[("OAK", "28L")]);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void Parse_TerminalWaypoints_ExtractsCoordinates()
    {
        var lines = new[] { BuildTerminalWaypointLine("KOAK", "FITKI", "N37424600", "W122131200") };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var result = CifpParser.Parse(tmpFile);

            Assert.True(result.TerminalWaypoints.ContainsKey("FITKI"));
            var (lat, lon) = result.TerminalWaypoints["FITKI"];
            Assert.Equal(37.7128, lat, precision: 3);
            Assert.Equal(-122.22, lon, precision: 2);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void Parse_RunwayExtraction_HandlesVariants()
    {
        // I28LY = ILS 28L variant Y
        var lines = new[] { BuildApproachLine("KSFO", "I28LY ", "DUMOS", 'F') };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var result = CifpParser.Parse(tmpFile);

            Assert.True(result.FafFixes.ContainsKey(("SFO", "28L")));
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    // --- ParseApproaches tests ---

    [Fact]
    public void ParseApproaches_IlsApproach_ExtractsCommonLegs()
    {
        var lines = new[]
        {
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 10, "GROVE", CifpFixRole.IAF, "IF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 20, "FITKI", CifpFixRole.IF, "TF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 30, "MUXED", CifpFixRole.FAF, "TF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 40, "RW28L", CifpFixRole.MAP, "CF"),
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var approaches = CifpParser.ParseApproaches(tmpFile, "KOAK");

            Assert.Single(approaches);
            var proc = approaches[0];
            Assert.Equal("OAK", proc.Airport);
            Assert.Equal("I28L", proc.ApproachId);
            Assert.Equal('I', proc.TypeCode);
            Assert.Equal("ILS", proc.ApproachTypeName);
            Assert.Equal("28L", proc.Runway);
            Assert.Equal(4, proc.CommonLegs.Count);
            Assert.Equal("GROVE", proc.CommonLegs[0].FixIdentifier);
            Assert.Equal(CifpFixRole.IAF, proc.CommonLegs[0].FixRole);
            Assert.Equal("FITKI", proc.CommonLegs[1].FixIdentifier);
            Assert.Equal(CifpFixRole.IF, proc.CommonLegs[1].FixRole);
            Assert.Equal("MUXED", proc.CommonLegs[2].FixIdentifier);
            Assert.Equal(CifpFixRole.FAF, proc.CommonLegs[2].FixRole);
            Assert.Equal("RW28L", proc.CommonLegs[3].FixIdentifier);
            Assert.Equal(CifpFixRole.MAP, proc.CommonLegs[3].FixRole);
            Assert.Empty(proc.Transitions);
            Assert.Empty(proc.MissedApproachLegs);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ParseApproaches_WithTransition_SeparatesTransitionAndCommon()
    {
        var lines = new[]
        {
            // Transition "SUNOL" with route type 'A'
            BuildFullApproachLine("KOAK", "I28L  ", 'A', "SUNOL", 10, "SUNOL", CifpFixRole.IAF, "IF"),
            BuildFullApproachLine("KOAK", "I28L  ", 'A', "SUNOL", 20, "GROVE", CifpFixRole.None, "TF"),
            // Common legs
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 30, "FITKI", CifpFixRole.IF, "TF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 40, "MUXED", CifpFixRole.FAF, "TF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 50, "RW28L", CifpFixRole.MAP, "CF"),
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var approaches = CifpParser.ParseApproaches(tmpFile, "KOAK");

            Assert.Single(approaches);
            var proc = approaches[0];
            Assert.Single(proc.Transitions);
            Assert.True(proc.Transitions.ContainsKey("SUNOL"));
            Assert.Equal(2, proc.Transitions["SUNOL"].Legs.Count);
            Assert.Equal("SUNOL", proc.Transitions["SUNOL"].Legs[0].FixIdentifier);
            Assert.Equal(3, proc.CommonLegs.Count);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ParseApproaches_MissedApproach_SeparatedAfterMahp()
    {
        var lines = new[]
        {
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 10, "FITKI", CifpFixRole.IF, "TF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 20, "MUXED", CifpFixRole.FAF, "TF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 30, "RW28L", CifpFixRole.MAP, "CF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 40, "GROVE", CifpFixRole.None, "TF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 50, "SUNOL", CifpFixRole.None, "DF"),
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var approaches = CifpParser.ParseApproaches(tmpFile, "KOAK");

            var proc = approaches[0];
            // MAP itself is in common legs (3 = IF, FAF, MAP)
            Assert.Equal(3, proc.CommonLegs.Count);
            Assert.Equal(2, proc.MissedApproachLegs.Count);
            Assert.Equal("GROVE", proc.MissedApproachLegs[0].FixIdentifier);
            Assert.Equal("SUNOL", proc.MissedApproachLegs[1].FixIdentifier);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ParseApproaches_HoldInLieu_Detected()
    {
        var lines = new[]
        {
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 10, "GROVE", CifpFixRole.IAF, "IF"),
            // HA = hold-in-lieu path terminator
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 20, "FITKI", CifpFixRole.IF, "HA"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 30, "MUXED", CifpFixRole.FAF, "TF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 40, "RW28L", CifpFixRole.MAP, "CF"),
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var approaches = CifpParser.ParseApproaches(tmpFile, "KOAK");

            var proc = approaches[0];
            Assert.True(proc.HasHoldInLieu);
            Assert.NotNull(proc.HoldInLieuLeg);
            Assert.Equal("FITKI", proc.HoldInLieuLeg.FixIdentifier);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ParseApproaches_RnavApproach_CorrectTypeName()
    {
        var lines = new[]
        {
            BuildFullApproachLine("KOAK", "H28LZ ", ' ', "", 10, "GROVE", CifpFixRole.IAF, "IF"),
            BuildFullApproachLine("KOAK", "H28LZ ", ' ', "", 20, "RW28L", CifpFixRole.MAP, "TF"),
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var approaches = CifpParser.ParseApproaches(tmpFile, "KOAK");

            Assert.Single(approaches);
            Assert.Equal('H', approaches[0].TypeCode);
            Assert.Equal("RNAV(GPS)", approaches[0].ApproachTypeName);
            Assert.Equal("28L", approaches[0].Runway);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ParseApproaches_MultipleApproaches_AllReturned()
    {
        var lines = new[]
        {
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 10, "FITKI", CifpFixRole.FAF, "TF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 20, "RW28L", CifpFixRole.MAP, "CF"),
            BuildFullApproachLine("KOAK", "I30   ", ' ', "", 10, "DUMBA", CifpFixRole.FAF, "TF"),
            BuildFullApproachLine("KOAK", "I30   ", ' ', "", 20, "RW30 ", CifpFixRole.MAP, "CF"),
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var approaches = CifpParser.ParseApproaches(tmpFile, "KOAK");

            Assert.Equal(2, approaches.Count);
            Assert.Contains(approaches, a => a.ApproachId == "I28L");
            Assert.Contains(approaches, a => a.ApproachId == "I30");
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ParseApproaches_WrongAirport_ReturnsEmpty()
    {
        var lines = new[] { BuildFullApproachLine("KSFO", "I28L  ", ' ', "", 10, "FITKI", CifpFixRole.FAF, "TF") };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var approaches = CifpParser.ParseApproaches(tmpFile, "KOAK");

            Assert.Empty(approaches);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    // ARINC 424 altitude fields are 5-char zero-padded feet (e.g., "01700") or "FLnnn" for flight levels.
    [Theory]
    [InlineData('+', "01700", null, CifpAltitudeRestrictionType.AtOrAbove, 1700)]
    [InlineData('-', "04000", null, CifpAltitudeRestrictionType.AtOrBelow, 4000)]
    [InlineData('@', "03400", null, CifpAltitudeRestrictionType.At, 3400)]
    [InlineData(' ', "03400", null, CifpAltitudeRestrictionType.At, 3400)]
    [InlineData('B', "05000", "03000", CifpAltitudeRestrictionType.Between, 5000)]
    [InlineData('G', "03400", null, CifpAltitudeRestrictionType.GlideSlopeIntercept, 3400)]
    public void ParseAltitudeRestriction_VariousTypes(
        char desc,
        string alt1Str,
        string? alt2Str,
        CifpAltitudeRestrictionType expectedType,
        int expectedAlt1
    )
    {
        var result = CifpParser.ParseAltitudeRestriction(desc, alt1Str, alt2Str ?? "     ");

        Assert.NotNull(result);
        Assert.Equal(expectedType, result.Type);
        Assert.Equal(expectedAlt1, result.Altitude1Ft);
    }

    [Fact]
    public void ParseArinc424Altitude_FlightLevel_ReturnsCorrectFeet()
    {
        // ARINC 424 flight levels use the FLnnn format where nnn is hundreds of feet.
        Assert.Equal(28000, CifpParser.ParseArinc424Altitude("FL280"));
        Assert.Equal(18000, CifpParser.ParseArinc424Altitude("FL180"));
        Assert.Equal(8000, CifpParser.ParseArinc424Altitude("FL080"));
    }

    [Fact]
    public void ParseArinc424Altitude_NumericFeet_ReturnsCorrectFeet()
    {
        // Numeric altitude fields are in feet, zero-padded to 5 chars.
        Assert.Equal(3400, CifpParser.ParseArinc424Altitude("03400"));
        Assert.Equal(17000, CifpParser.ParseArinc424Altitude("17000"));
        Assert.Equal(500, CifpParser.ParseArinc424Altitude("00500"));
    }

    [Fact]
    public void ParseArinc424Altitude_EmptyOrZero_ReturnsNull()
    {
        Assert.Null(CifpParser.ParseArinc424Altitude("     "));
        Assert.Null(CifpParser.ParseArinc424Altitude("00000"));
    }

    [Fact]
    public void ParseApproaches_ExistingParseMethod_StillWorks()
    {
        // Regression: ensure the original Parse() method is unaffected
        var lines = new[]
        {
            BuildApproachLine("KOAK", "I28L  ", "FITKI", 'D'),
            BuildTerminalWaypointLine("KOAK", "FITKI", "N37424600", "W122131200"),
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var result = CifpParser.Parse(tmpFile);

            Assert.True(result.FafFixes.ContainsKey(("OAK", "28L")));
            Assert.Equal("FITKI", result.FafFixes[("OAK", "28L")]);
            Assert.True(result.TerminalWaypoints.ContainsKey("FITKI"));
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    // --- ParseTerminalWaypoints (per-airport) ---

    [Fact]
    public void ParseTerminalWaypoints_FiltersbyAirport()
    {
        var lines = new[]
        {
            BuildTerminalWaypointLine("KABQ", "CFPTK", "N35004612", "W106431818"),
            BuildTerminalWaypointLine("KABQ", "CFDXH", "N35010000", "W106400000"),
            BuildTerminalWaypointLine("KOAK", "CFOAK", "N37424600", "W122131200"),
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var waypoints = CifpParser.ParseTerminalWaypoints(tmpFile, "KABQ");

            Assert.Equal(2, waypoints.Count);
            Assert.True(waypoints.ContainsKey("CFPTK"));
            Assert.True(waypoints.ContainsKey("CFDXH"));
            Assert.False(waypoints.ContainsKey("CFOAK"));

            // Verify coordinate parsing: N35°00'46.12" → 35.012811
            var (lat, lon) = waypoints["CFPTK"];
            Assert.Equal(35.0128, lat, precision: 3);
            Assert.Equal(-106.7217, lon, precision: 3);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ParseTerminalWaypoints_EmptyForUnknownAirport()
    {
        var lines = new[] { BuildTerminalWaypointLine("KABQ", "CFPTK", "N35004612", "W106431818") };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var waypoints = CifpParser.ParseTerminalWaypoints(tmpFile, "KJFK");

            Assert.Empty(waypoints);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    // --- RF/AF arc field extraction ---

    [Fact]
    public void ParseApproaches_RealKothH05Z_RfLegHasArcData()
    {
        // Real CIFP lines from FAACIFP18 (KOTH RNAV(GPS) RWY 5 H05-Z, DEROY transition).
        // Leg 040 at PIVLY is an RF leg with arc center fix CFLTZ, radius 0.300 NM (rho=300 thousandths),
        // turn direction Right. CFLTZ is a CIFP terminal waypoint at the airport.
        // To regenerate: grep '^SUSAP KOTHK1FH05-Z ADEROY' tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz (after gunzip)
        var lines = new[]
        {
            RealCifpLines.KothCfltzTerminalWaypoint,
            RealCifpLines.KothH05ZDeroy010,
            RealCifpLines.KothH05ZDeroy020,
            RealCifpLines.KothH05ZDeroy030,
            RealCifpLines.KothH05ZDeroy040PivlyRf,
            RealCifpLines.KothH05ZDeroy050FogixRf,
            RealCifpLines.KothH05ZDeroy060Oxvak,
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var approaches = CifpParser.ParseApproaches(tmpFile, "KOTH");

            Assert.Single(approaches);
            var deroy = approaches[0].Transitions["DEROY"];
            var pivlyRf = deroy.Legs.First(l => l.FixIdentifier == "PIVLY");
            Assert.Equal(CifpPathTerminator.RF, pivlyRf.PathTerminator);
            Assert.Equal(3.0, pivlyRf.ArcRadiusNm!.Value, precision: 2);
            Assert.Equal('R', pivlyRf.TurnDirection);
            // Outbound course on RF leg = tangent course at end of arc (0308.3°)
            Assert.NotNull(pivlyRf.OutboundCourse);
            Assert.Equal(308.3, pivlyRf.OutboundCourse!.Value, precision: 1);
            // Center fix CFLTZ should be resolved from the terminal waypoint
            Assert.NotNull(pivlyRf.ArcCenterLat);
            Assert.NotNull(pivlyRf.ArcCenterLon);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ParseApproaches_RealKabqI03_AfLegHasNavaidAndArcData()
    {
        // Real CIFP lines from FAACIFP18 (KABQ ILS RWY 3 I03, NODME transition).
        // Leg 020 at BIBQU is an AF leg referencing the ABQ navaid with theta/rho/arc data.
        // To regenerate: grep '^SUSAP KABQK2FI03   ANODME' tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz (after gunzip)
        var lines = new[] { RealCifpLines.KabqI03Nodme010, RealCifpLines.KabqI03Nodme020BibquAf };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var approaches = CifpParser.ParseApproaches(tmpFile, "KABQ");

            Assert.Single(approaches);
            var nodme = approaches[0].Transitions["NODME"];
            var bibquAf = nodme.Legs.First(l => l.FixIdentifier == "BIBQU");
            Assert.Equal(CifpPathTerminator.AF, bibquAf.PathTerminator);
            Assert.Equal("ABQ", bibquAf.RecommendedNavaidId);
            Assert.Equal('L', bibquAf.TurnDirection);
            Assert.NotNull(bibquAf.Theta);
            Assert.NotNull(bibquAf.Rho);
            // Real published values for the BIBQU AF leg from CFR ILS 3 NODME transition
            Assert.Equal(163.9, bibquAf.Theta!.Value, precision: 1);
            Assert.Equal(10.0, bibquAf.Rho!.Value, precision: 1);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    // --- SID/STAR parser tests ---

    [Fact]
    public void ParseSids_BasicSid_ExtractsCommonAndRunwayTransitions()
    {
        var lines = new[]
        {
            // Runway transition "RW28R"
            BuildSidStarLine('D', "KOAK", "PORTE3", "RW28R", 10, "OAK  "),
            BuildSidStarLine('D', "KOAK", "PORTE3", "RW28R", 20, "REBAS"),
            // Common legs
            BuildSidStarLine('D', "KOAK", "PORTE3", "", 30, "PORTE"),
            BuildSidStarLine('D', "KOAK", "PORTE3", "", 40, "BRIXX"),
            // Enroute transition "MOLIN"
            BuildSidStarLine('D', "KOAK", "PORTE3", "MOLIN", 50, "MOLIN"),
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var sids = CifpParser.ParseSids(tmpFile, "KOAK");

            Assert.Single(sids);
            var sid = sids[0];
            Assert.Equal("OAK", sid.Airport);
            Assert.Equal("PORTE3", sid.ProcedureId);

            // Common legs
            Assert.Equal(2, sid.CommonLegs.Count);
            Assert.Equal("PORTE", sid.CommonLegs[0].FixIdentifier);
            Assert.Equal("BRIXX", sid.CommonLegs[1].FixIdentifier);

            // Runway transition
            Assert.Single(sid.RunwayTransitions);
            Assert.True(sid.RunwayTransitions.ContainsKey("RW28R"));
            Assert.Equal(2, sid.RunwayTransitions["RW28R"].Legs.Count);
            Assert.Equal("OAK", sid.RunwayTransitions["RW28R"].Legs[0].FixIdentifier);

            // Enroute transition
            Assert.Single(sid.EnrouteTransitions);
            Assert.True(sid.EnrouteTransitions.ContainsKey("MOLIN"));
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ParseStars_BasicStar_ExtractsTransitions()
    {
        var lines = new[]
        {
            // Enroute transition "FAITH"
            BuildSidStarLine('E', "KOAK", "SUNOL1", "FAITH", 10, "FAITH"),
            BuildSidStarLine('E', "KOAK", "SUNOL1", "FAITH", 20, "KENNO"),
            // Common legs
            BuildSidStarLine('E', "KOAK", "SUNOL1", "", 30, "SUNOL"),
            BuildSidStarLine('E', "KOAK", "SUNOL1", "", 40, "GROVE"),
            // Runway transition "RW28L"
            BuildSidStarLine('E', "KOAK", "SUNOL1", "RW28L", 50, "FITKI"),
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var stars = CifpParser.ParseStars(tmpFile, "KOAK");

            Assert.Single(stars);
            var star = stars[0];
            Assert.Equal("OAK", star.Airport);
            Assert.Equal("SUNOL1", star.ProcedureId);

            Assert.Equal(2, star.CommonLegs.Count);
            Assert.Equal("SUNOL", star.CommonLegs[0].FixIdentifier);
            Assert.Equal("GROVE", star.CommonLegs[1].FixIdentifier);

            Assert.Single(star.EnrouteTransitions);
            Assert.True(star.EnrouteTransitions.ContainsKey("FAITH"));
            Assert.Equal(2, star.EnrouteTransitions["FAITH"].Legs.Count);

            Assert.Single(star.RunwayTransitions);
            Assert.True(star.RunwayTransitions.ContainsKey("RW28L"));
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ParseSids_RealKsfoCiity3_ExtractsAltOrAbove5000AtCiity()
    {
        // Real CIFP line from FAACIFP18 (KSFO CIITY3 SID, RW10L runway transition).
        // Leg 040 at fix CIITY has altitude restriction "+ 05000" → AtOrAbove 5000ft.
        // To regenerate: grep '^SUSAP KSFOK2DCIITY3' tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz (after gunzip)
        var lines = new[]
        {
            RealCifpLines.KsfoCiity3Rw10LLeg010,
            RealCifpLines.KsfoCiity3Rw10LLeg020,
            RealCifpLines.KsfoCiity3Rw10LLeg030,
            RealCifpLines.KsfoCiity3Rw10LLeg040CiityAtOrAbove5000,
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var sids = CifpParser.ParseSids(tmpFile, "KSFO");

            Assert.Single(sids);
            var sid = sids[0];
            Assert.Equal("CIITY3", sid.ProcedureId);

            var rw10L = sid.RunwayTransitions["RW10L"];
            var ciityLeg = rw10L.Legs.First(l => l.FixIdentifier == "CIITY");
            Assert.NotNull(ciityLeg.Altitude);
            Assert.Equal(CifpAltitudeRestrictionType.AtOrAbove, ciityLeg.Altitude.Type);
            Assert.Equal(5000, ciityLeg.Altitude.Altitude1Ft);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ParseSids_WrongAirport_ReturnsEmpty()
    {
        var lines = new[] { BuildSidStarLine('D', "KSFO", "PORTE3", "", 10, "PORTE") };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var sids = CifpParser.ParseSids(tmpFile, "KOAK");

            Assert.Empty(sids);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ParseStars_AllTransitionName_TreatedAsCommon()
    {
        var lines = new[]
        {
            BuildSidStarLine('E', "KOAK", "SUNOL1", "ALL  ", 10, "SUNOL"),
            BuildSidStarLine('E', "KOAK", "SUNOL1", "ALL  ", 20, "GROVE"),
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var stars = CifpParser.ParseStars(tmpFile, "KOAK");

            Assert.Single(stars);
            Assert.Equal(2, stars[0].CommonLegs.Count);
            Assert.Empty(stars[0].EnrouteTransitions);
            Assert.Empty(stars[0].RunwayTransitions);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ParseSids_MultipleRunwayTransitions_AllCaptured()
    {
        var lines = new[]
        {
            BuildSidStarLine('D', "KOAK", "PORTE3", "RW28R", 10, "OAK  "),
            BuildSidStarLine('D', "KOAK", "PORTE3", "RW28L", 10, "REBAS"),
            BuildSidStarLine('D', "KOAK", "PORTE3", "", 20, "PORTE"),
        };

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, lines);
            var sids = CifpParser.ParseSids(tmpFile, "KOAK");

            Assert.Single(sids);
            Assert.Equal(2, sids[0].RunwayTransitions.Count);
            Assert.True(sids[0].RunwayTransitions.ContainsKey("RW28R"));
            Assert.True(sids[0].RunwayTransitions.ContainsKey("RW28L"));
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    // --- Helpers ---

    /// <summary>
    /// Builds a minimal ARINC 424 SID/STAR record line (subsection D or E).
    /// </summary>
    private static string BuildSidStarLine(char subsection, string icao, string procedureId, string transition, int sequence, string fixId)
    {
        var line = new char[120];
        Array.Fill(line, ' ');
        "SUSAP".CopyTo(0, line, 0, 5);
        icao.PadRight(4).CopyTo(0, line, 6, 4);
        line[12] = subsection;
        procedureId.PadRight(6).CopyTo(0, line, 13, 6);
        line[19] = ' ';
        transition.PadRight(5).CopyTo(0, line, 20, 5);
        sequence.ToString("D3").CopyTo(0, line, 26, 3);
        fixId.PadRight(5).CopyTo(0, line, 29, 5);
        "TF".CopyTo(0, line, 47, 2);
        return new string(line);
    }

    /// <summary>
    /// Builds a minimal ARINC 424 approach record line (subsection F).
    /// Positions are 0-indexed; the record must be at least 50 chars.
    /// </summary>
    private static string BuildApproachLine(string icao, string approachId, string fixId, char waypointDesc)
    {
        // Positions (0-indexed):
        // 0-4: "SUSAP"
        // 5: space
        // 6-9: airport ICAO (4 chars)
        // 10-11: padding
        // 12: subsection 'F'
        // 13-18: approach ID (6 chars)
        // 19: route type
        // 20-24: transition
        // 25: space
        // 26-28: sequence "010"
        // 29-33: fix identifier (5 chars)
        // 34-41: padding
        // 42: waypoint description code
        // 43-49: padding to reach 50 chars min
        var line = new char[55];
        Array.Fill(line, ' ');
        "SUSAP".CopyTo(0, line, 0, 5);
        icao.PadRight(4).CopyTo(0, line, 6, 4);
        line[12] = 'F';
        approachId.PadRight(6).CopyTo(0, line, 13, 6);
        line[19] = ' '; // main route
        "010".CopyTo(0, line, 26, 3);
        fixId.PadRight(5).CopyTo(0, line, 29, 5);
        line[42] = waypointDesc;
        return new string(line);
    }

    /// <summary>
    /// Builds a minimal ARINC 424 terminal waypoint record (subsection C).
    /// </summary>
    private static string BuildTerminalWaypointLine(string icao, string ident, string latArinc, string lonArinc)
    {
        // Positions (0-indexed):
        // 0-4: "SUSAP"
        // 5: space
        // 6-9: airport ICAO
        // 10-11: padding
        // 12: subsection 'C'
        // 13-17: waypoint identifier (5 chars)
        // 18-31: padding
        // 32: N/S latitude start (we place at position 32)
        // 32-40: latitude (9 chars)
        // 41-50: longitude (10 chars)
        var line = new char[55];
        Array.Fill(line, ' ');
        "SUSAP".CopyTo(0, line, 0, 5);
        icao.PadRight(4).CopyTo(0, line, 6, 4);
        line[12] = 'C';
        ident.PadRight(5).CopyTo(0, line, 13, 5);
        latArinc.CopyTo(0, line, 32, latArinc.Length);
        lonArinc.CopyTo(0, line, 32 + latArinc.Length, lonArinc.Length);
        return new string(line);
    }

    /// <summary>
    /// Builds a full-length ARINC 424 approach record (subsection F) with altitude/speed fields.
    /// Line is 120 chars to cover all parsed positions.
    /// </summary>
    private static string BuildFullApproachLine(
        string icao,
        string approachId,
        char routeType,
        string transition,
        int sequence,
        string fixId,
        CifpFixRole fixRole,
        string pathTerminator
    )
    {
        var line = new char[120];
        Array.Fill(line, ' ');
        "SUSAP".CopyTo(0, line, 0, 5);
        icao.PadRight(4).CopyTo(0, line, 6, 4);
        line[12] = 'F';
        approachId.PadRight(6).CopyTo(0, line, 13, 6);
        line[19] = routeType == '\0' ? ' ' : routeType;
        if (transition.Length > 0)
        {
            transition.PadRight(5).CopyTo(0, line, 20, 5);
        }

        sequence.ToString("D3").CopyTo(0, line, 26, 3);
        fixId.PadRight(5).CopyTo(0, line, 29, 5);

        // Waypoint description code at position 42 (fix role)
        line[42] = fixRole switch
        {
            CifpFixRole.IAF => 'A',
            CifpFixRole.IF => 'B',
            CifpFixRole.FAF => 'F',
            CifpFixRole.MAP => 'M',
            _ => ' ',
        };

        // Path terminator at positions 47-48
        if (pathTerminator.Length >= 2)
        {
            line[47] = pathTerminator[0];
            line[48] = pathTerminator[1];
        }

        return new string(line);
    }
}

/// <summary>
/// Real ARINC 424 records extracted verbatim from FAACIFP18.gz (test data bundle).
/// Each constant documents the source approach/SID and what it exercises.
/// To regenerate after a CIFP cycle update:
///   gunzip -c tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz \
///     | grep '^SUSAP K&lt;ICAO&gt;K2&lt;sub&gt;&lt;procedure&gt;'
/// Lines must be exactly 132 chars (the parser requires Length &gt; column for each field).
/// </summary>
internal static class RealCifpLines
{
    // --- KSFO CIITY3 SID, RW10L runway transition ---
    // Source: FAACIFP18 lines 314556-314559

    public const string KsfoCiity3Rw10LLeg010 =
        "SUSAP KSFOK2DCIITY34RW10L 010         0        VA                     1038        + 00520     18000                        145541509";

    public const string KsfoCiity3Rw10LLeg020 =
        "SUSAP KSFOK2DCIITY34RW10L 020ORYANK2PC0E       DF                                                                          145551509";

    public const string KsfoCiity3Rw10LLeg030 =
        "SUSAP KSFOK2DCIITY34RW10L 030SAHEYK2PC0E       TF                                                                          145561509";

    public const string KsfoCiity3Rw10LLeg040CiityAtOrAbove5000 =
        "SUSAP KSFOK2DCIITY34RW10L 040CIITYK2PC0EE      TF                                 + 05000                                  145571509";

    // --- KOTH RNAV(GPS) RWY 5 H05-Z, DEROY transition (contains RF curved-final segments) ---
    // Source: FAACIFP18 lines 281582-281587 (DEROY transition) + 281530 (CFLTZ terminal waypoint)

    public const string KothCfltzTerminalWaypoint =
        "SUSAP KOTHK1CCFLTZ K10    A     N43191954W124204723                       E0144     NAR           CFLTZ(CNF)               815272504";

    public const string KothH05ZDeroy010 =
        "SUSAP KOTHK1FH05-Z ADEROY 010DEROYK1EA0E  A    IF                                             18000                 A FS   815792004";

    public const string KothH05ZDeroy020 =
        "SUSAP KOTHK1FH05-Z ADEROY 020JISDIK1PC0E  B 010TF                                 + 03600          180              A-FS   815802004";

    public const string KothH05ZDeroy030 =
        "SUSAP KOTHK1FH05-Z ADEROY 030HEKNOK1PC0E    010TF                                 + 02500                           A FS   815812004";

    public const string KothH05ZDeroy040PivlyRf =
        "SUSAP KOTHK1FH05-Z ADEROY 040PIVLYK1PC0E   R010RF       0030002567    30830027    + 02400                 CFLTZ K1PCA FS   815822004";

    public const string KothH05ZDeroy050FogixRf =
        "SUSAP KOTHK1FH05-Z ADEROY 050FOGIXK1PC0E   R010RF       0030003083    04560051    + 01900                 CFLTZ K1PCA FS   815832004";

    public const string KothH05ZDeroy060Oxvak =
        "SUSAP KOTHK1FH05-Z ADEROY 060OXVAKK1PC0EE   010TF                                 + 01300                           A FS   815842004";

    // --- KABQ ILS RWY 3 I03, NODME transition (contains AF arc-to-fix leg referencing ABQ navaid) ---
    // Source: FAACIFP18 lines 113817-113818

    public const string KabqI03Nodme010 =
        "SUSAP KABQK2FI03   ANODME 010NODMEK2EA0E  A    IF                                             18000                 0 DS   138132305";

    public const string KabqI03Nodme020BibquAf =
        "SUSAP KABQK2FI03   ANODME 020BIBQUK2PC0EE BL   AF ABQ K2      163901000230    D   + 08000                           0 DS   138142305";
}
