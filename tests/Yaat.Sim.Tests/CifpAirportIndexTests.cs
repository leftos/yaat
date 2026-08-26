using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="CifpAirportIndex"/> must hand the airport-scoped parsers exactly the lines a full
/// <c>File.ReadLines</c> scan filtered by airport would — including airports whose records sit in
/// two separate blocks of the file.
/// </summary>
public sealed class CifpAirportIndexTests
{
    [Theory]
    [InlineData("KOAK")]
    [InlineData("KSFO")]
    [InlineData("KABI")] // records split across two blocks in the FAA file
    [InlineData("kabq")] // case-insensitive
    public void ReadAirportLines_MatchesFullScan(string icao)
    {
        var path = CifpPathResolver.CachedPath;
        if (path is null || !File.Exists(path))
        {
            return;
        }

        string padded = icao.ToUpperInvariant().PadRight(4);
        var expected = File.ReadLines(path)
            .Where(l => l.StartsWith("SUSAP", StringComparison.Ordinal) && l.Length >= 10 && l[6..10] == padded)
            .ToList();

        var actual = CifpAirportIndex.ReadAirportLines(path, icao).ToList();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ReadAirportLines_UnknownAirport_IsEmpty()
    {
        var path = CifpPathResolver.CachedPath;
        if (path is null || !File.Exists(path))
        {
            return;
        }

        Assert.Empty(CifpAirportIndex.ReadAirportLines(path, "ZZZZ"));
    }

    [Fact]
    public void ReadAirportLines_ShortSyntheticRecords_AreIndexed()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tmpFile, ["SUSAP KABQK2CCFPTK short record", "SUSAP KOAKK2CCFOAK short record", "SUSAP KABQK2CCFDXH short record"]);

            var lines = CifpAirportIndex.ReadAirportLines(tmpFile, "KABQ").ToList();

            Assert.Equal(["SUSAP KABQK2CCFPTK short record", "SUSAP KABQK2CCFDXH short record"], lines);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }
}
