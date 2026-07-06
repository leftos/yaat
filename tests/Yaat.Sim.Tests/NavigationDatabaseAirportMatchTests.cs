using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for the ICAO/FAA-aware airport id matching helpers exposed on
/// <see cref="NavigationDatabase"/>. These back the auto-departure strip
/// fix — without them, an aircraft filed as "KOAK" and a scenario with
/// primaryAirportId "OAK" compare as different airports and the strip
/// never auto-prints.
/// </summary>
public class NavigationDatabaseAirportMatchTests
{
    // Pins the nav database (populated by TestVnasData) before AirportIdsMatchResolved reads NavigationDatabase.Instance.
    public NavigationDatabaseAirportMatchTests() => TestVnasData.EnsureInitialized();

    [Theory]
    [InlineData("KOAK", "OAK", true)]
    [InlineData("OAK", "KOAK", true)]
    [InlineData("KSFO", "SFO", true)]
    [InlineData("OAK", "OAK", true)]
    [InlineData("KOAK", "KOAK", true)]
    [InlineData("oak", "KOAK", true)] // case-insensitive
    [InlineData("  KOAK  ", "OAK", true)] // whitespace tolerant
    [InlineData("KOAK", "KSFO", false)]
    [InlineData("KOAK", "SFO", false)]
    [InlineData("KOAK", "", false)]
    [InlineData("", "OAK", false)]
    [InlineData(null, "OAK", false)]
    [InlineData("KOAK", null, false)]
    public void AirportIdsMatch_HandlesIcaoAndFaaEquivalence(string? a, string? b, bool expected)
    {
        Assert.Equal(expected, NavigationDatabase.AirportIdsMatch(a, b));
    }

    [Fact]
    public void NormalizeAirport_StripsKPrefixForCONUS()
    {
        Assert.Equal("OAK", NavigationDatabase.NormalizeAirport("KOAK"));
        Assert.Equal("OAK", NavigationDatabase.NormalizeAirport("OAK"));
        Assert.Equal("OAK", NavigationDatabase.NormalizeAirport("  koak  "));
    }

    [Fact]
    public void NormalizeAirport_LeavesNonCONUSAlone()
    {
        // 4-char ICAO not starting with K (e.g. EGLL) — leave as-is.
        Assert.Equal("EGLL", NavigationDatabase.NormalizeAirport("EGLL"));
        // 3-char FAA that happens to start with K (none real; stay safe) — leave.
        Assert.Equal("KEY", NavigationDatabase.NormalizeAirport("KEY"));
    }

    // AirportIdsMatchResolved resolves each id through the FAA/ICAO index so non-CONUS pairs match too
    // (ANC/PANC, HNL/PHNL, SJU/TJSJ) — not just the CONUS K-prefix (docs/crc/eram.md:1423 autotrack equivalence).
    [Theory]
    [InlineData("OAK", "KOAK", true)] // CONUS K-prefix
    [InlineData("KOAK", "OAK", true)]
    [InlineData("ANC", "PANC", true)] // Alaska — non-K ICAO prefix
    [InlineData("HNL", "PHNL", true)] // Hawaii
    [InlineData("SJU", "TJSJ", true)] // Puerto Rico
    [InlineData("OAK", "SFO", false)]
    [InlineData("OAK", "", false)]
    [InlineData("", "OAK", false)]
    public void AirportIdsMatchResolved_MatchesEitherFaaOrIcao(string a, string b, bool expected)
    {
        Assert.Equal(expected, NavigationDatabase.Instance.AirportIdsMatchResolved(a, b));
    }
}
