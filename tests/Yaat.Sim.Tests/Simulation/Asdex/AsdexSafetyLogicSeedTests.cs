using System.Collections.Immutable;
using System.Text.Json;
using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Asdex;

/// <summary>
/// The safety-logic configuration a scenario load seeds for every ASDE-X facility in the ARTCC config, from the real
/// ZOA config (<c>docs/vnas-artcc-config-examples/zoa.json</c>) and real navdata: the facility's own runway ids (the
/// vNAS pair form its positions list, then every navdata runway no position names), each with a four-corner
/// footprint CRC can draw, all open, on the configuration the room's active runways select.
/// </summary>
public class AsdexSafetyLogicSeedTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Lazy<ArtccConfigRoot> Zoa = new(() =>
        JsonSerializer.Deserialize<ArtccConfigRoot>(
            File.ReadAllText(Path.Combine(TickRecorder.FindRepoRoot(), "docs", "vnas-artcc-config-examples", "zoa.json")),
            JsonOptions
        )!
    );

    public AsdexSafetyLogicSeedTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private const string L28D01 = "01GVF4AGA8HX03NSN8QJS4WJS4";
    private const string L28D28 = "01GVM1SKN089G82RCS84S0W8W8";
    private const string L10D10 = "01GVM1VF16S824B4JHH1YR1FZR";

    private static ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> Seed() => Seed(ActiveRunways.Empty);

    private static ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> Seed(ActiveRunways activeRunways) =>
        AsdexSafetyLogicSeed.Build(Zoa.Value, NavigationDatabase.Instance, activeRunways);

    /// <summary>
    /// One airport's active runways from ARWY-style tokens: a bare end is used for both, a <c>D</c> or <c>A</c> prefix
    /// for departures or arrivals only.
    /// </summary>
    private static ActiveRunways Active(string airport, string tokens) =>
        ActiveRunways.Empty.With(
            airport,
            [
                .. tokens
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(token =>
                        token[0] switch
                        {
                            'A' => new ActiveRunway(token[1..], ActiveRunwayUse.Arrival),
                            'D' => new ActiveRunway(token[1..], ActiveRunwayUse.Departure),
                            _ => new ActiveRunway(token, ActiveRunwayUse.Both),
                        }
                    ),
            ]
        );

    private static RunwayInfo NavRunway(string airport, string end1) =>
        NavigationDatabase.Instance.GetRunway(airport, end1)
        ?? NavigationDatabase.Instance.GetRunway("K" + airport, end1)
        ?? throw new InvalidOperationException($"{airport} {end1} is not in navdata");

    [Fact]
    public void Sfo_HasExactlyTheRunwayIdsItsPositionsName_InConfigOrder()
    {
        AsdexSafetyLogicConfig sfo = Seed()["SFO"];

        Assert.Equal(["1L-19R", "1R-19L", "28L-10R", "28R-10L"], sfo.Runways.Select(r => r.Id));
    }

    [Fact]
    public void Sfo_EveryRunwayIsOpen_AndWithNoActiveRunwaysTheFirstConfigurationIsSeeded()
    {
        AsdexSafetyLogicConfig sfo = Seed(ActiveRunways.Empty)["SFO"];

        Assert.All(sfo.Runways, r => Assert.False(r.IsClosed, $"runway {r.Id} starts closed"));
        Assert.Equal(L28D01, sfo.RunwayConfigurationId);
        Assert.Empty(sfo.InhibitedArrivalAlertPositionIds);
    }

    /// <summary>
    /// The configuration whose arrival and departure runway ids cover the active runways, the smallest such set on more
    /// than one (28L/28R for both is covered by L28D01 too, which is larger), and the first configuration when none
    /// covers them.
    /// </summary>
    [Theory]
    [InlineData("A28L A28R D01L D01R", L28D01)]
    [InlineData("28L 28R", L28D28)]
    [InlineData("10L 10R", L10D10)]
    [InlineData("28L 19L", L28D01)]
    public void Sfo_SeedsTheSmallestConfigurationCoveringItsActiveRunways(string tokens, string expectedId)
    {
        AsdexSafetyLogicConfig sfo = Seed(Active("SFO", tokens))["SFO"];

        Assert.Equal(expectedId, sfo.RunwayConfigurationId);
    }

    [Fact]
    public void Sfo_ReadsOnlyItsOwnAirportsActiveRunways()
    {
        AsdexSafetyLogicConfig sfo = Seed(Active("OAK", "28R 30"))["SFO"];

        Assert.Equal(L28D01, sfo.RunwayConfigurationId);
    }

    [Fact]
    public void Oak_HasNoConfigurations_SoNoConfigurationIdIsSeeded()
    {
        Assert.Empty(Zoa.Value.FindFacility("OAK")!.AsdexConfiguration!.RunwayConfigurations);

        Assert.Equal("", Seed(Active("OAK", "28R 30"))["OAK"].RunwayConfigurationId);
        Assert.Equal("", Seed(ActiveRunways.Empty)["OAK"].RunwayConfigurationId);
    }

    /// <summary>
    /// Four corners, the first two at the navdata runway's end 1 one runway width apart (each half a width from the
    /// threshold), and the ring counter-clockwise seen from above — the order CRC draws its closed-runway X from.
    /// </summary>
    [Theory]
    [InlineData("1L-19R")]
    [InlineData("1R-19L")]
    [InlineData("28L-10R")]
    [InlineData("28R-10L")]
    public void Sfo_EachFootprintIsACounterClockwiseRing_StartingAcrossEnd1(string runwayId)
    {
        AsdexRunwayConfig runway = Seed()["SFO"].Runways.Single(r => r.Id == runwayId);
        RunwayInfo nav = NavRunway("SFO", runwayId.Split('-')[0]);
        var end1 = new LatLon(nav.Lat1, nav.Lon1);
        double widthNm = nav.WidthFt / GeoMath.FeetPerNm;

        Assert.Equal(4, runway.AreaPoints.Count);
        Assert.Equal(widthNm, GeoMath.DistanceNm(runway.AreaPoints[0], runway.AreaPoints[1]), 4);
        Assert.Equal(widthNm / 2, GeoMath.DistanceNm(end1, runway.AreaPoints[0]), 4);
        Assert.Equal(widthNm / 2, GeoMath.DistanceNm(end1, runway.AreaPoints[1]), 4);
        Assert.True(SignedArea(runway.AreaPoints) > 0, $"runway {runwayId}'s ring is clockwise");
    }

    [Fact]
    public void Oak_HasAnEntry_NamingEveryNavdataRunwayEndToEnd()
    {
        AsdexSafetyLogicConfig oak = Seed()["OAK"];

        IReadOnlyList<RunwayInfo> nav = NavigationDatabase.Instance.GetRunways("OAK") is { Count: > 0 } byFaa
            ? byFaa
            : NavigationDatabase.Instance.GetRunways("KOAK");
        Assert.Equal(
            nav.Select(r => $"{RunwayIdentifier.ToDisplayDesignator(r.Id.End1)}-{RunwayIdentifier.ToDisplayDesignator(r.Id.End2)}"),
            oak.Runways.Select(r => r.Id)
        );
    }

    [Fact]
    public void AFacilityWithoutAnAsdexConfiguration_HasNoEntry()
    {
        ImmutableSortedDictionary<string, AsdexSafetyLogicConfig> seed = Seed();

        Assert.Null(Zoa.Value.FindFacility("NCT")?.AsdexConfiguration);
        Assert.False(seed.ContainsKey("NCT"));
        Assert.False(seed.ContainsKey("ZOA"));
    }

    [Fact]
    public void TheSimLoadPath_SetsTheMapFromTheSeed_AndTheFirstSnapshotCarriesIt()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(AiTestFixture.ParkedAtOak, 7, MagneticDeclination.EvaluationDateUtc, scenario => scenario.ArtccConfig = Zoa.Value);

        Assert.Equal(Seed().Keys, engine.Scenario!.AsdexSafetyLogicConfigs.Keys);
        Assert.Equal(["1L-19R", "1R-19L", "28L-10R", "28R-10L"], engine.Scenario.AsdexSafetyLogicConfigs["SFO"].Runways.Select(r => r.Id));

        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        Assert.Equal(Seed().Keys, snapshot.Scenario.AsdexSafetyLogicConfigs!.Keys);
    }

    [Fact]
    public void ABareSimLoad_WithNoArtccConfig_SeedsNothing()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(AiTestFixture.ParkedAtOak, 7, MagneticDeclination.EvaluationDateUtc);

        Assert.Empty(engine.Scenario!.AsdexSafetyLogicConfigs);
    }

    [Fact]
    public void ANullConfig_GivesAnEmptyMap() => Assert.Empty(AsdexSafetyLogicSeed.Build(null, NavigationDatabase.Instance, ActiveRunways.Empty));

    /// <summary>Shoelace area in lon/lat: positive when the ring runs counter-clockwise seen from above.</summary>
    private static double SignedArea(IReadOnlyList<LatLon> ring)
    {
        double sum = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            LatLon a = ring[i];
            LatLon b = ring[(i + 1) % ring.Count];
            sum += (a.Lon * b.Lat) - (b.Lon * a.Lat);
        }

        return sum / 2;
    }
}
