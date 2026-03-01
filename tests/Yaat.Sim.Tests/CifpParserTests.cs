using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

public class CifpParserTests
{
    [Theory]
    [InlineData("N38573910", 38.960861)]
    [InlineData("N37424600", 37.712778)]
    [InlineData("S33525000", -33.880556)]
    public void ParseArinc424Latitude_ValidInput_ReturnsDecimalDegrees(
        string input, double expected)
    {
        var result = CifpParser.ParseArinc424Latitude(input);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Value, precision: 4);
    }

    [Theory]
    [InlineData("W121292540", -121.490389)]
    [InlineData("W122131200", -122.220000)]
    [InlineData("E002174200", 2.295000)]
    public void ParseArinc424Longitude_ValidInput_ReturnsDecimalDegrees(
        string input, double expected)
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
        var lines = new[]
        {
            BuildTerminalWaypointLine("KOAK", "FITKI", "N37424600", "W122131200"),
        };

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
        var lines = new[]
        {
            BuildApproachLine("KSFO", "I28LY ", "DUMOS", 'F'),
        };

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

    /// <summary>
    /// Builds a minimal ARINC 424 approach record line (subsection F).
    /// Positions are 0-indexed; the record must be at least 50 chars.
    /// </summary>
    private static string BuildApproachLine(
        string icao, string approachId, string fixId, char waypointDesc)
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
    private static string BuildTerminalWaypointLine(
        string icao, string ident, string latArinc, string lonArinc)
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
}
