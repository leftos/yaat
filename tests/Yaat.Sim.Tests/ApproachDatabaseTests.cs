using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

public class ApproachDatabaseTests
{
    [Fact]
    public void GetApproaches_NoCifpFile_ReturnsEmpty()
    {
        var db = new ApproachDatabase(null);
        var result = db.GetApproaches("OAK");
        Assert.Empty(result);
    }

    [Fact]
    public void GetApproaches_MissingFile_ReturnsEmpty()
    {
        var db = new ApproachDatabase("/nonexistent/path");
        var result = db.GetApproaches("OAK");
        Assert.Empty(result);
    }

    [Fact]
    public void GetApproach_FromCifpData_ReturnsCorrectProcedure()
    {
        var tmpFile = CreateCifpFile(
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 10, "FITKI", CifpFixRole.IF, "TF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 20, "MUXED", CifpFixRole.FAF, "TF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 30, "RW28L", CifpFixRole.MAHP, "CF")
        );

        try
        {
            var db = new ApproachDatabase(tmpFile);
            var proc = db.GetApproach("OAK", "I28L");

            Assert.NotNull(proc);
            Assert.Equal("I28L", proc.ApproachId);
            Assert.Equal("ILS", proc.ApproachTypeName);
            Assert.Equal("28L", proc.Runway);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void GetApproach_KPrefixNormalized()
    {
        var tmpFile = CreateCifpFile(
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 10, "FITKI", CifpFixRole.FAF, "TF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 20, "RW28L", CifpFixRole.MAHP, "CF")
        );

        try
        {
            var db = new ApproachDatabase(tmpFile);

            // Both "KOAK" and "OAK" should work
            Assert.NotNull(db.GetApproach("KOAK", "I28L"));
            Assert.NotNull(db.GetApproach("OAK", "I28L"));
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Theory]
    [InlineData("ILS28L", "I28L")]
    [InlineData("I28L", "I28L")]
    [InlineData("LOC30", "L30")]
    [InlineData("RNAV28LZ", "H28LZ")]
    public void ResolveApproachId_VariousShorthands_ResolvesCorrectly(string shorthand, string expectedId)
    {
        var tmpFile = CreateCifpFile(
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 10, "FITKI", CifpFixRole.FAF, "TF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 20, "RW28L", CifpFixRole.MAHP, "CF"),
            BuildFullApproachLine("KOAK", "L30   ", ' ', "", 10, "DUMBA", CifpFixRole.FAF, "TF"),
            BuildFullApproachLine("KOAK", "L30   ", ' ', "", 20, "RW30 ", CifpFixRole.MAHP, "CF"),
            BuildFullApproachLine("KOAK", "H28LZ ", ' ', "", 10, "GROVE", CifpFixRole.FAF, "TF"),
            BuildFullApproachLine("KOAK", "H28LZ ", ' ', "", 20, "RW28L", CifpFixRole.MAHP, "CF")
        );

        try
        {
            var db = new ApproachDatabase(tmpFile);
            var result = db.ResolveApproachId("OAK", shorthand);

            Assert.Equal(expectedId, result);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ResolveApproachId_RunwayOnly_ReturnsHighestPriority()
    {
        var tmpFile = CreateCifpFile(
            BuildFullApproachLine("KOAK", "H28L  ", ' ', "", 10, "GROVE", CifpFixRole.FAF, "TF"),
            BuildFullApproachLine("KOAK", "H28L  ", ' ', "", 20, "RW28L", CifpFixRole.MAHP, "CF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 10, "FITKI", CifpFixRole.FAF, "TF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 20, "RW28L", CifpFixRole.MAHP, "CF")
        );

        try
        {
            var db = new ApproachDatabase(tmpFile);
            // "28L" without type → ILS preferred over RNAV
            var result = db.ResolveApproachId("OAK", "28L");

            Assert.Equal("I28L", result);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ResolveApproachId_UnknownShorthand_ReturnsNull()
    {
        var tmpFile = CreateCifpFile(
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 10, "FITKI", CifpFixRole.FAF, "TF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 20, "RW28L", CifpFixRole.MAHP, "CF")
        );

        try
        {
            var db = new ApproachDatabase(tmpFile);
            Assert.Null(db.ResolveApproachId("OAK", "ILS99"));
            Assert.Null(db.ResolveApproachId("OAK", ""));
            Assert.Null(db.ResolveApproachId("OAK", "NONSENSE"));
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void GetApproaches_CachesPerAirport()
    {
        var tmpFile = CreateCifpFile(
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 10, "FITKI", CifpFixRole.FAF, "TF"),
            BuildFullApproachLine("KOAK", "I28L  ", ' ', "", 20, "RW28L", CifpFixRole.MAHP, "CF")
        );

        try
        {
            var db = new ApproachDatabase(tmpFile);

            // First call loads from file
            var result1 = db.GetApproaches("OAK");
            // Second call should return same cached list
            var result2 = db.GetApproaches("OAK");

            Assert.Same(result1, result2);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    // --- Helpers ---

    private static string CreateCifpFile(params string[] lines)
    {
        var tmpFile = Path.GetTempFileName();
        File.WriteAllLines(tmpFile, lines);
        return tmpFile;
    }

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
        line[42] = fixRole switch
        {
            CifpFixRole.IAF => 'A',
            CifpFixRole.IF => 'B',
            CifpFixRole.FAF => 'F',
            CifpFixRole.MAHP => 'M',
            _ => ' ',
        };
        if (pathTerminator.Length >= 2)
        {
            line[47] = pathTerminator[0];
            line[48] = pathTerminator[1];
        }

        return new string(line);
    }
}
