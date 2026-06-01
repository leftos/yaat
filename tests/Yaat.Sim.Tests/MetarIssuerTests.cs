using Xunit;

namespace Yaat.Sim.Tests;

public class MetarIssuerTests
{
    private static readonly Func<string, (double Lat, double Lon)?> NoLocator = _ => null;
    private static readonly DateTime Anchor = new(2026, 6, 1, 18, 40, 0, DateTimeKind.Utc);

    private static WeatherProfile Weather(string metar, double dir = 270, double speed = 12) =>
        new()
        {
            Metars = [metar],
            WindLayers =
            [
                new WindLayer
                {
                    Altitude = 0,
                    Direction = dir,
                    Speed = speed,
                },
            ],
        };

    [Fact]
    public void Construction_ReportsBaseMetarsVerbatim()
    {
        var w = Weather("KOAK 011840Z 27012KT 10SM CLR 18/12 A2992");
        var issuer = new MetarIssuer(w, Anchor, NoLocator);
        Assert.Equal("KOAK 011840Z 27012KT 10SM CLR 18/12 A2992", Assert.Single(issuer.Reports));
    }

    [Fact]
    public void Tick_BeforeRoutineMinute_NoReissue()
    {
        var w = Weather("KOAK 011840Z 27012KT 10SM CLR 18/12 A2992");
        var issuer = new MetarIssuer(w, Anchor, NoLocator);
        Assert.False(issuer.Tick(600, w, NoLocator)); // 18:50, unchanged
    }

    [Fact]
    public void Tick_AtRoutineMinute_ReissuesRoutineWithNewStamp()
    {
        var w = Weather("KOAK 011840Z 27012KT 10SM CLR 18/12 A2992");
        var issuer = new MetarIssuer(w, Anchor, NoLocator);
        Assert.True(issuer.Tick(13 * 60, w, NoLocator)); // 18:53
        var report = Assert.Single(issuer.Reports);
        Assert.StartsWith("METAR KOAK 011853Z", report);
        Assert.Contains("27012KT", report);
    }

    [Fact]
    public void Tick_SignificantChange_IssuesSpeci()
    {
        var w = Weather("KOAK 011840Z 27012KT 10SM CLR 18/12 A2992");
        var issuer = new MetarIssuer(w, Anchor, NoLocator);
        var low = Weather("KOAK 011841Z 27012KT 2SM BR CLR 18/12 A2992"); // vis crosses 3
        Assert.True(issuer.Tick(60, low, NoLocator)); // 18:41, before routine
        var report = Assert.Single(issuer.Reports);
        Assert.StartsWith("SPECI KOAK 011841Z", report);
        Assert.Contains("2SM", report);
    }

    [Fact]
    public void Tick_AfterSpeci_RebaselinesAndHolds()
    {
        var w = Weather("KOAK 011840Z 27012KT 10SM CLR 18/12 A2992");
        var issuer = new MetarIssuer(w, Anchor, NoLocator);
        var low = Weather("KOAK 011841Z 27012KT 2SM BR CLR 18/12 A2992");
        Assert.True(issuer.Tick(60, low, NoLocator)); // SPECI
        Assert.False(issuer.Tick(120, low, NoLocator)); // no further change since last issued
    }

    [Fact]
    public void Tick_PrefersPhysicsSurfaceWind_OverBaseMetarWind()
    {
        // Base METAR says 09005KT, but the physics surface layer is 270/12.
        var w = Weather("KOAK 011840Z 09005KT 10SM CLR 18/12 A2992", dir: 270, speed: 12);
        var issuer = new MetarIssuer(w, Anchor, NoLocator);
        Assert.True(issuer.Tick(13 * 60, w, NoLocator)); // routine
        var report = Assert.Single(issuer.Reports);
        Assert.Contains("27012KT", report);
        Assert.DoesNotContain("09005KT", report);
    }
}
