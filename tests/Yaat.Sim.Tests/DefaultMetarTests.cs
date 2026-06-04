using Xunit;

namespace Yaat.Sim.Tests;

public class DefaultMetarTests
{
    private static readonly DateTime Obs = new(2026, 6, 3, 19, 53, 0, DateTimeKind.Utc);

    [Fact]
    public void Build_FaaId_PrefixesK_AndStampsDdHHmmZ()
    {
        Assert.Equal("KSFO 031953Z AUTO 00000KT 10SM CLR A2992", DefaultMetar.Build("SFO", Obs));
    }

    [Fact]
    public void Build_IcaoId_LeavesStationUnchanged()
    {
        Assert.Equal("KOAK 031953Z AUTO 00000KT 10SM CLR A2992", DefaultMetar.Build("KOAK", Obs));
    }

    [Fact]
    public void Build_RoundTrips_ToCalmWindAndStandardAltimeter()
    {
        // Proves the radar/ground per-airport overlay renders the calm/standard default
        // (e.g. "SFO 29.92 00000") and the METAR panel parses a valid station.
        var parsed = MetarParser.Parse(DefaultMetar.Build("SFO", Obs));

        Assert.NotNull(parsed);
        Assert.Equal("KSFO", parsed!.StationId);
        Assert.Equal(0, parsed.WindDirectionDeg);
        Assert.Equal(0, parsed.WindSpeedKts);
        Assert.Null(parsed.WindGustKts);
        Assert.Equal(29.92, parsed.AltimeterInHg);
        Assert.Equal(10.0, parsed.VisibilityStatuteMiles);
    }
}
