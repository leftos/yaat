using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// A bare identifier shared by an airport's FAA id and a CIFP navaid (SAC: Sacramento Executive and the
/// SACRAMENTO VORTAC) resolves to the navaid through <see cref="NavigationDatabase.GetFixPosition"/>, while
/// <see cref="NavigationDatabase.GetAirportPosition"/> returns the airport for either the FAA or the ICAO id.
/// </summary>
[Collection("NavDbMutator")]
public class NavigationDatabaseAirportNavaidCollisionTests
{
    // Sacramento Executive (KSAC) airport reference point in the pinned NavData.
    private const double KsacLat = 38.51286;
    private const double KsacLon = -121.49330;

    private static readonly Lazy<IReadOnlyDictionary<string, (double Lat, double Lon, string Name, string Type)>> CifpNavaids = new(() =>
        CifpParser.ParseNavaids(TestVnasData.GetCifpPath()!)
    );

    public NavigationDatabaseAirportNavaidCollisionTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static NavigationDatabase NavDb => TestVnasData.NavigationDb!;

    [Fact]
    public void GetFixPosition_BareSac_IsTheSacramentoVortac()
    {
        (double Lat, double Lon, string Name, string Type) vortac = CifpNavaids.Value["SAC"];

        (double Lat, double Lon)? pos = NavDb.GetFixPosition("SAC");

        Assert.NotNull(pos);
        AssertWithin(pos.Value, (vortac.Lat, vortac.Lon), 0.1, "GetFixPosition(SAC) vs SACRAMENTO VORTAC");
    }

    [Fact]
    public void GetFixPosition_IcaoKsac_IsTheAirport()
    {
        (double Lat, double Lon)? pos = NavDb.GetFixPosition("KSAC");

        Assert.NotNull(pos);
        AssertWithin(pos.Value, (KsacLat, KsacLon), 0.01, "GetFixPosition(KSAC) vs Sacramento Executive");
    }

    [Theory]
    [InlineData("SAC")]
    [InlineData("KSAC")]
    [InlineData("sac")]
    public void GetAirportPosition_FaaOrIcaoSac_IsTheAirport(string id)
    {
        (double Lat, double Lon)? pos = NavDb.GetAirportPosition(id);

        Assert.NotNull(pos);
        AssertWithin(pos.Value, (KsacLat, KsacLon), 0.01, $"GetAirportPosition({id}) vs Sacramento Executive");
    }

    [Fact]
    public void GetAirportPosition_Sac_IsFarFromTheVortac()
    {
        (double Lat, double Lon, string Name, string Type) vortac = CifpNavaids.Value["SAC"];

        (double Lat, double Lon)? pos = NavDb.GetAirportPosition("SAC");

        Assert.NotNull(pos);
        double apartNm = GeoMath.DistanceNm(pos.Value.Lat, pos.Value.Lon, vortac.Lat, vortac.Lon);
        Assert.True(apartNm > 4.5, $"Sacramento Executive is {apartNm:F2} nm from the SACRAMENTO VORTAC, expected ~4.97");
    }

    [Fact]
    public void GetFixPosition_BareOak_IsTheOaklandVor()
    {
        (double Lat, double Lon, string Name, string Type) vor = CifpNavaids.Value["OAK"];

        (double Lat, double Lon)? pos = NavDb.GetFixPosition("OAK");

        Assert.NotNull(pos);
        AssertWithin(pos.Value, (vor.Lat, vor.Lon), 0.01, "GetFixPosition(OAK) vs OAKLAND VOR");
    }

    [Fact]
    public void GetAirportPosition_OakAndKoak_AreTheSameAirportPoint()
    {
        (double Lat, double Lon)? faa = NavDb.GetAirportPosition("OAK");
        (double Lat, double Lon)? icao = NavDb.GetAirportPosition("KOAK");

        Assert.NotNull(faa);
        Assert.Equal(faa, icao);
        Assert.Equal(icao, NavDb.GetFixPosition("KOAK"));
    }

    [Theory]
    [InlineData("ZZZZ")]
    [InlineData("SUNOL")]
    [InlineData("")]
    public void GetAirportPosition_UnknownAirport_ReturnsNull(string id) => Assert.Null(NavDb.GetAirportPosition(id));

    [Fact]
    public void GetFixPosition_AirportOnlyName_StillResolvesToTheAirport()
    {
        // PAO (Palo Alto) names no CIFP navaid, so the bare FAA id keeps resolving to the airport.
        Assert.False(CifpNavaids.Value.ContainsKey("PAO"));

        (double Lat, double Lon)? fix = NavDb.GetFixPosition("PAO");
        (double Lat, double Lon)? airport = NavDb.GetAirportPosition("PAO");

        Assert.NotNull(airport);
        Assert.Equal(airport, fix);
        Assert.Equal(airport, NavDb.GetAirportPosition("KPAO"));
    }

    [Fact]
    public void ExpandRouteForNavigation_BareFirstTokenNamingTheDeparture_IsTheNavaidAndKept()
    {
        // CCR names both Buchanan Field (KCCR) and the CONCORD VOR, ~3.4 nm north of it. A bare route token is
        // always the navaid, so leading a route from KCCR it lies outside the departure vicinity and stays.
        (double Lat, double Lon) vor = NavDb.GetFixPosition("CCR")!.Value;
        (double Lat, double Lon) airport = NavDb.GetAirportPosition("KCCR")!.Value;
        Assert.True(GeoMath.DistanceNm(vor.Lat, vor.Lon, airport.Lat, airport.Lon) > 3.0);

        IReadOnlyList<string> expanded = NavDb.ExpandRouteForNavigation("CCR SAC", "KCCR");

        Assert.Equal(["CCR", "SAC"], expanded);
    }

    [Fact]
    public void ExpandRouteForNavigation_SameNameAfterTheAirportToken_IsTheNavaid()
    {
        IReadOnlyList<string> expanded = NavDb.ExpandRouteForNavigation("KCCR CCR SAC", "KCCR");

        Assert.Equal(["CCR", "SAC"], expanded);
    }

    [Fact]
    public void ResolveDepartureRoute_KccrCcrSac_FirstTargetIsTheConcordVor()
    {
        (double Lat, double Lon, string Name, string Type) vor = CifpNavaids.Value["CCR"];
        using IDisposable _ = NavigationDatabase.ScopedOverride(NavDb);
        var aircraft = new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Position = new LatLon(37.99, -122.057),
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                Route = "KCCR CCR SAC",
                Departure = "KCCR",
                Destination = "KSMF",
            },
        };

        DepartureRouteResult? result = DepartureClearanceHandler.ResolveDepartureRoute(new DefaultDeparture(), aircraft);

        Assert.NotNull(result);
        Assert.Equal(["CCR", "SAC"], result.Targets.Select(t => t.Name));
        AssertWithin((result.Targets[0].Position.Lat, result.Targets[0].Position.Lon), (vor.Lat, vor.Lon), 0.1, "CCR target vs CONCORD VOR");
    }

    [Fact]
    public void ResolveDepartureRoute_FrdToken_IsFlownAtItsFixRadialDistance()
    {
        LatLon expected = FrdResolver.Resolve("SAC090020", NavDb)!.Value;
        using IDisposable _ = NavigationDatabase.ScopedOverride(NavDb);
        var aircraft = new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Position = new LatLon(37.99, -122.057),
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                Route = "KCCR CCR SAC SAC090020",
                Departure = "KCCR",
                Destination = "KRNO",
            },
        };

        DepartureRouteResult? result = DepartureClearanceHandler.ResolveDepartureRoute(new DefaultDeparture(), aircraft);

        Assert.NotNull(result);
        Assert.Equal(["CCR", "SAC", "SAC090020"], result.Targets.Select(t => t.Name));
        AssertWithin((result.Targets[2].Position.Lat, result.Targets[2].Position.Lon), (expected.Lat, expected.Lon), 0.1, "SAC090020 target");
    }

    // Airports published with only their FAA id, which a CIFP navaid also carries.
    [Theory]
    [InlineData("AZN")]
    [InlineData("HEY")]
    [InlineData("HGT")]
    [InlineData("TNV")]
    public void GetFixPosition_FaaOnlyAirportIdSharedWithANavaid_IsTheNavaid(string id)
    {
        (double Lat, double Lon, string Name, string Type) navaid = CifpNavaids.Value[id];

        (double Lat, double Lon)? pos = NavDb.GetFixPosition(id);

        Assert.NotNull(pos);
        Assert.Equal((navaid.Lat, navaid.Lon), pos.Value);
    }

    [Theory]
    [InlineData("AZN")]
    [InlineData("HEY")]
    [InlineData("HGT")]
    [InlineData("TNV")]
    public void GetFixPosition_KFormOfFaaOnlyAirport_IsTheAirport(string id)
    {
        (double Lat, double Lon)? airport = NavDb.GetAirportPosition(id);

        Assert.NotNull(airport);
        Assert.Equal(airport, NavDb.GetFixPosition("K" + id));
        Assert.Equal(airport, NavDb.GetAirportPosition("K" + id));
        Assert.True(NavDb.TryResolveAirport("K" + id, out string canonical));
        Assert.Equal(id, canonical);
    }

    private static void AssertWithin((double Lat, double Lon) actual, (double Lat, double Lon) expected, double maxNm, string what)
    {
        double offNm = GeoMath.DistanceNm(actual.Lat, actual.Lon, expected.Lat, expected.Lon);
        Assert.True(offNm < maxNm, $"{what}: {offNm:F2} nm apart ({actual.Lat:F5},{actual.Lon:F5})");
    }
}
