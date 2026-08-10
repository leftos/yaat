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

    // -------------------------------------------------------------------------
    // Observed variable wind
    // -------------------------------------------------------------------------

    private static WeatherProfile GustyWeather(string metar, double halfSpread, double? gusts) =>
        new()
        {
            Metars = [metar],
            WindLayers =
            [
                new WindLayer
                {
                    Altitude = 0,
                    Direction = 210,
                    Speed = 15,
                    Gusts = gusts,
                    DirectionVariabilityDeg = halfSpread,
                },
            ],
        };

    [Fact]
    public void Tick_GustyVariableLayer_ReportsGustAndVarGroup()
    {
        // 210/15 gusting 25 with a ±35° authored spread: the observed report should carry
        // a gust group and a dddVddd group derived from the simulated field.
        var w = GustyWeather("KOAK 011840Z 21015KT 10SM CLR 18/12 A2992", halfSpread: 35, gusts: 25);
        var issuer = new MetarIssuer(w, Anchor, NoLocator);
        Assert.True(issuer.Tick(13 * 60, w, NoLocator)); // routine at 18:53
        var report = Assert.Single(issuer.Reports);
        // The envelope groups are deterministic (authored gust; authored 210±35 arc → 180V250
        // after rounding); the observed 2-minute mean direction/speed legitimately wander a
        // little between reports, so only their shape is pinned.
        Assert.Matches(@"\b(?:19|20|21|22)0\d{2}G25KT 180V250\b", report);
    }

    [Fact]
    public void Tick_VrbLayer_ReportsVrb()
    {
        var w = new WeatherProfile
        {
            Metars = ["KOAK 011840Z 27004KT 10SM CLR 18/12 A2992"],
            WindLayers =
            [
                new WindLayer
                {
                    Altitude = 0,
                    Direction = 270,
                    Speed = 4,
                    Variable = true,
                },
            ],
        };
        var issuer = new MetarIssuer(w, Anchor, NoLocator);
        Assert.True(issuer.Tick(13 * 60, w, NoLocator));
        var report = Assert.Single(issuer.Reports);
        Assert.Contains("VRB04KT", report);
    }

    [Fact]
    public void Tick_TextOnlyVrbBase_KeepsVrbNotCalm()
    {
        // A text-only VRB base METAR used to be re-reported as 00000KT.
        var w = new WeatherProfile { Metars = ["KOAK 011840Z VRB15KT 10SM CLR 18/12 A2992"] };
        var issuer = new MetarIssuer(w, Anchor, NoLocator);
        Assert.True(issuer.Tick(13 * 60, w, NoLocator));
        var report = Assert.Single(issuer.Reports);
        Assert.Contains("VRB15KT", report);
        Assert.DoesNotContain("00000KT", report);
    }

    [Fact]
    public void Tick_SteadyLayer_NoSpuriousGroups()
    {
        // No authored variability: the observed report stays a plain wind group.
        var w = Weather("KOAK 011840Z 27012KT 10SM CLR 18/12 A2992");
        var issuer = new MetarIssuer(w, Anchor, NoLocator);
        Assert.True(issuer.Tick(13 * 60, w, NoLocator));
        var report = Assert.Single(issuer.Reports);
        Assert.Contains("27012KT", report);
        Assert.DoesNotContain("VRB", report);
        Assert.DoesNotContain("G", report.Split("KT")[0][^6..]); // no gust group inside the wind group
    }

    [Fact]
    public void Tick_VariabilityAlone_NeverIssuesWindShiftSpeci()
    {
        // 60 minutes of a gusty variable wind with a static configured mean must never
        // produce an off-cycle SPECI: the 2-minute mean direction wanders, but stays well
        // inside the 45° wind-shift criterion, and gust-group changes are not SPECI-worthy.
        var w = GustyWeather("KOAK 011753Z 21015KT 10SM CLR 18/12 A2992", halfSpread: 30, gusts: 25);
        // Anchor just past :53 so no routine issuance lands inside the hour under test.
        var anchor = new DateTime(2026, 6, 1, 17, 54, 0, DateTimeKind.Utc);
        var issuer = new MetarIssuer(w, anchor, NoLocator);

        for (int elapsed = 1; elapsed <= 3500; elapsed += 1)
        {
            bool changed = issuer.Tick(elapsed, w, NoLocator);
            Assert.False(changed, $"Unexpected SPECI at elapsed={elapsed}: {issuer.Reports[0]}");
        }
    }
}
