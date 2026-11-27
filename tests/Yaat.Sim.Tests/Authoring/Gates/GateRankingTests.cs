using System.Text.Json;
using Xunit;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Gates;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Authoring.Gates;

/// <summary>
/// Gate ranking for one scenario aircraft (<see cref="GateRanking"/>) on the committed SFO layout, with sidecar parking
/// areas the tests build over SFO's real A and B stands.
/// </summary>
public class GateRankingTests
{
    private const string TerminalA = "Terminal A";
    private const string TerminalB = "Terminal B";

    /// <summary>SFO's distinct parking names starting with A.</summary>
    private const int AStands = 16;

    /// <summary>SFO's distinct parking names starting with B.</summary>
    private const int BStands = 32;

    /// <summary>The SFO stands the committed sidecar's <c>Plot {n}</c> entry covers: every digits-dash name.</summary>
    private static readonly string[] PlotStands =
    [
        "11-1",
        "11-2",
        "11-3",
        "12-1",
        "12-2",
        "12-3",
        "12-4",
        "12-5",
        "12-6",
        "2-1",
        "2-1A",
        "2-1B",
        "2-2",
        "2-2B",
        "2-3A",
        "40-8",
        "40-9",
        "41-1",
        "41-10",
        "41-11",
        "41-12",
        "41-13",
        "41-15",
        "41-16",
        "41-18",
        "41-19",
        "41-2",
        "41-20",
        "41-21",
        "41-22",
        "41-23",
        "41-3",
        "41-4",
        "41-5",
        "41-6",
        "41-7",
        "41-8",
        "41-9",
        "42-1",
        "42-2",
        "42-3",
        "42-4",
        "42-5",
        "50-1",
        "50-2",
        "50-3",
        "50-4",
        "50-5",
        "50-6",
        "50-7",
        "50-8",
        "6-1",
        "6-2",
        "6-3",
        "6-4",
        "9-3",
        "9-3A",
        "9-3B",
        "9-4",
        "9-4A",
        "9-4B",
        "9-5",
        "9-5A",
        "9-5B",
        "9-8",
        "9-8A",
        "9-8B",
        "9-9",
        "9-9A",
        "9-9B",
    ];

    public GateRankingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData("B748")]
    [InlineData("B744")]
    public void Heavy_NeverGetsStand_WhoseAreaMaxAdgIsTooSmall(string type)
    {
        AirportGroundLayout layout = Sfo();

        IReadOnlyList<GateCandidate> small = Rank(
            layout,
            Sidecars(Area(TerminalA, "A*", AirplaneDesignGroup.III)),
            Aircraft("KAL123", type, spawnDelay: 0)
        );
        IReadOnlyList<GateCandidate> large = Rank(
            layout,
            Sidecars(Area(TerminalA, "A*", AirplaneDesignGroup.VI)),
            Aircraft("KAL123", type, spawnDelay: 0)
        );

        Assert.Equal(AStands, InArea(small, TerminalA).Count);
        Assert.Equal(AStands, InArea(large, TerminalA).Count);
        Assert.All(InArea(small, TerminalA), c => Assert.Equal(GateReason.TooSmall, c.Reason));
        Assert.All(InArea(large, TerminalA), c => Assert.Equal(GateReason.Fits, c.Reason));
    }

    [Fact]
    public void Heavy_B763_Rejected_ByMaxAdgIII_AcceptedBy_IV()
    {
        AirportGroundLayout layout = Sfo();

        IReadOnlyList<GateCandidate> three = Rank(
            layout,
            Sidecars(Area(TerminalA, "A*", AirplaneDesignGroup.III)),
            Aircraft("KAL123", "B763", spawnDelay: 0)
        );
        IReadOnlyList<GateCandidate> four = Rank(
            layout,
            Sidecars(Area(TerminalA, "A*", AirplaneDesignGroup.IV)),
            Aircraft("KAL123", "B763", spawnDelay: 0)
        );

        Assert.Equal(AStands, InArea(three, TerminalA).Count);
        Assert.Equal(AStands, InArea(four, TerminalA).Count);
        Assert.All(InArea(three, TerminalA), c => Assert.Equal(GateReason.TooSmall, c.Reason));
        Assert.All(InArea(four, TerminalA), c => Assert.Equal(GateReason.Fits, c.Reason));
    }

    [Fact]
    public void Small_C172_FitsMaxAdgI()
    {
        IReadOnlyList<GateCandidate> ranked = Rank(
            Sfo(),
            Sidecars(Area(TerminalA, "A*", AirplaneDesignGroup.I)),
            Aircraft("N172SP", "C172", spawnDelay: 0)
        );

        Assert.Equal(AStands, InArea(ranked, TerminalA).Count);
        Assert.All(InArea(ranked, TerminalA), c => Assert.Equal(GateReason.Fits, c.Reason));
    }

    [Fact]
    public void UnknownSpan_Heavy_UsesTheWakeFloor()
    {
        AirportGroundLayout layout = Sfo();

        IReadOnlyList<GateCandidate> three = Rank(
            layout,
            Sidecars(Area(TerminalA, "A*", AirplaneDesignGroup.III)),
            Aircraft("KAL123", "L101", spawnDelay: 0)
        );
        IReadOnlyList<GateCandidate> four = Rank(
            layout,
            Sidecars(Area(TerminalA, "A*", AirplaneDesignGroup.IV)),
            Aircraft("KAL123", "L101", spawnDelay: 0)
        );

        Assert.Equal(AStands, InArea(three, TerminalA).Count);
        Assert.Equal(AStands, InArea(four, TerminalA).Count);
        Assert.All(InArea(three, TerminalA), c => Assert.Equal(GateReason.TooSmall, c.Reason));
        Assert.All(InArea(four, TerminalA), c => Assert.Equal(GateReason.Fits, c.Reason));
    }

    [Fact]
    public void AirlineTag_Wins_OverUntagged()
    {
        IReadOnlyList<GateCandidate> ranked = Rank(Sfo(), TaggedAndUntagged(), Aircraft("UAL123", "A320", spawnDelay: 0));

        List<int> a = Indexes(ranked, TerminalA);
        List<int> b = Indexes(ranked, TerminalB);
        Assert.Equal(AStands, a.Count);
        Assert.Equal(BStands, b.Count);
        Assert.All(a, i => Assert.Equal(GateReason.Preferred, ranked[i].Reason));
        Assert.All(b, i => Assert.Equal(GateReason.Fits, ranked[i].Reason));
        Assert.True(a.Max() < b.Min(), $"last A stand at {a.Max()}, first B stand at {b.Min()}");
    }

    [Fact]
    public void AirlineTag_ForAnotherAirline_IsOtherAirline()
    {
        IReadOnlyList<GateCandidate> ranked = Rank(Sfo(), TaggedAndUntagged(), Aircraft("DAL123", "A320", spawnDelay: 0));

        Assert.Equal(AStands, InArea(ranked, TerminalA).Count);
        Assert.Equal(BStands, InArea(ranked, TerminalB).Count);
        Assert.All(InArea(ranked, TerminalA), c => Assert.Equal(GateReason.OtherAirline, c.Reason));
        Assert.All(InArea(ranked, TerminalB), c => Assert.Equal(GateReason.Fits, c.Reason));
        Assert.DoesNotContain(ranked, c => c.Reason == GateReason.Preferred);
    }

    [Fact]
    public void AirlineTag_NotApplied_ToRegistration()
    {
        IReadOnlyList<GateCandidate> ranked = Rank(Sfo(), TaggedAndUntagged(), Aircraft("N377L", "A320", spawnDelay: 0));

        Assert.Equal(AStands, InArea(ranked, TerminalA).Count);
        Assert.Equal(BStands, InArea(ranked, TerminalB).Count);
        Assert.All(InArea(ranked, TerminalA), c => Assert.Equal(GateReason.OtherAirline, c.Reason));
        Assert.All(InArea(ranked, TerminalB), c => Assert.Equal(GateReason.Fits, c.Reason));
    }

    [Fact]
    public void Occupied_BySpawnAndTaxiTime()
    {
        AirportGroundLayout layout = Sfo();
        AirportSidecarCatalog sidecars = Sidecars(Area(TerminalB, "B*", AirplaneDesignGroup.IV));
        ScenarioAircraft other = Parked("DAL456", "B5", "SFO", spawnDelay: 0, taxiSeconds: 300);

        GateCandidate during = Stand(Rank(layout, sidecars, Aircraft("DAL123", "A320", spawnDelay: 120), other), "B5");
        GateCandidate clearing = Stand(Rank(layout, sidecars, Aircraft("DAL123", "A320", spawnDelay: 300), other), "B5");
        GateCandidate after = Stand(Rank(layout, sidecars, Aircraft("DAL123", "A320", spawnDelay: 600), other), "B5");
        ScenarioAircraft stays = Parked("DAL789", "B5", "SFO", spawnDelay: 0, taxiSeconds: null);
        GateCandidate toTheEnd = Stand(Rank(layout, sidecars, Aircraft("DAL123", "A320", spawnDelay: 36000), stays), "B5");

        Assert.Equal(GateReason.Occupied, during.Reason);
        Assert.Equal(new GateOccupant("DAL456", new GateWindow(0, 300 + GateRanking.StandClearBufferSeconds)), during.Occupant);
        Assert.Equal(GateReason.Occupied, clearing.Reason);
        Assert.Equal(600, 300 + GateRanking.StandClearBufferSeconds);
        Assert.Equal(GateReason.Fits, after.Reason);
        Assert.Null(after.Occupant);
        Assert.Equal(GateReason.Occupied, toTheEnd.Reason);
        Assert.Equal(new GateOccupant("DAL789", new GateWindow(0, null)), toTheEnd.Occupant);
    }

    [Fact]
    public void Occupied_OwnWindow_HoldsTheStandClearBufferToo()
    {
        AirportGroundLayout layout = Sfo();
        AirportSidecarCatalog sidecars = Sidecars(Area(TerminalB, "B*", AirplaneDesignGroup.IV));
        ScenarioAircraft self = Parked("DAL123", "B4", "SFO", spawnDelay: 0, taxiSeconds: 300);
        ScenarioAircraft early = Parked("DAL456", "B5", "SFO", spawnDelay: 400, taxiSeconds: null);
        ScenarioAircraft late = Parked("DAL789", "B5", "SFO", spawnDelay: 600, taxiSeconds: null);

        GateCandidate overlapping = Stand(Rank(layout, sidecars, self, early), "B5");
        GateCandidate clear = Stand(Rank(layout, sidecars, self, late), "B5");

        Assert.Equal(GateReason.Occupied, overlapping.Reason);
        Assert.Equal("DAL456", overlapping.Occupant?.AircraftId);
        Assert.Equal(GateReason.Fits, clear.Reason);
    }

    [Fact]
    public void Occupied_IgnoresAnotherAirport_AndMatchesNamesIgnoringCaseAndSpaces()
    {
        AirportGroundLayout layout = Sfo();
        AirportSidecarCatalog sidecars = Sidecars(Area(TerminalB, "B*", AirplaneDesignGroup.IV));
        ScenarioAircraft atOak = Parked("DAL456", "B5", "OAK", spawnDelay: 0, taxiSeconds: null);
        ScenarioAircraft spaced = Parked("DAL789", " b 5 ", "KSFO", spawnDelay: 0, taxiSeconds: null);

        GateCandidate oak = Stand(Rank(layout, sidecars, Aircraft("DAL123", "A320", spawnDelay: 0), atOak), "B5");
        GateCandidate sfo = Stand(Rank(layout, sidecars, Aircraft("DAL123", "A320", spawnDelay: 0), spaced), "B5");

        Assert.Equal(GateReason.Fits, oak.Reason);
        Assert.Equal(GateReason.Occupied, sfo.Reason);
        Assert.Equal("DAL789", sfo.Occupant?.AircraftId);
    }

    [Fact]
    public void Pick_SeededRandom_IsStable_AndOnlyEverPicksTheBestTiersFittingStands()
    {
        AirportGroundLayout layout = Sfo();
        AirportSidecarCatalog sidecars = Sidecars(
            Area(TerminalA, "A*", AirplaneDesignGroup.IV, "UAL"),
            Area(TerminalB, "B*", AirplaneDesignGroup.IV),
            Area("Small", "C*", AirplaneDesignGroup.I)
        );
        ScenarioAircraft other = Parked("DAL456", "B5", "SFO", spawnDelay: 0, taxiSeconds: null);
        IReadOnlyList<GateCandidate> dal = Rank(layout, sidecars, Aircraft("DAL123", "A320", spawnDelay: 0), other);
        IReadOnlyList<GateCandidate> ual = Rank(layout, sidecars, Aircraft("UAL123", "A320", spawnDelay: 0), other);

        Assert.Equal("D14", GateRanking.Pick(dal, new Random(42))?.Stand);
        Assert.Equal("D14", GateRanking.Pick(dal, new Random(42))?.Stand);
        Assert.Contains(dal, c => c.Reason == GateReason.Occupied);
        Assert.Contains(dal, c => c.Reason == GateReason.TooSmall);
        Assert.Contains(dal, c => c.Reason == GateReason.OtherAirline);
        for (int seed = 0; seed < 200; seed++)
        {
            Assert.Equal(GateReason.Fits, GateRanking.Pick(dal, new Random(seed))?.Reason);
            Assert.Equal(GateReason.Preferred, GateRanking.Pick(ual, new Random(seed))?.Reason);
        }
    }

    [Fact]
    public void Pick_NothingFits_ReturnsNull()
    {
        IReadOnlyList<GateCandidate> ranked = Rank(
            Sfo(),
            Sidecars(Area(TerminalA, "A*", AirplaneDesignGroup.III)),
            Aircraft("KAL123", "B748", spawnDelay: 0),
            GateScope.Area(TerminalA)
        );

        Assert.Equal(AStands, ranked.Count);
        Assert.All(ranked, c => Assert.Equal(GateReason.TooSmall, c.Reason));
        Assert.Null(GateRanking.Pick(ranked, new Random(42)));
    }

    [Fact]
    public void Scope_Area_RanksOnlyThatAreasStands()
    {
        AirportGroundLayout layout = Sfo();
        AirportSidecarCatalog sidecars = TaggedAndUntagged();

        IReadOnlyList<GateCandidate> ranked = Rank(layout, sidecars, Aircraft("DAL123", "A320", spawnDelay: 0), GateScope.Area("terminal  b"));
        List<string> expected =
        [
            .. layout
                .Nodes.Values.Where(n => n.Type == GroundNodeType.Parking)
                .Select(n => n.Name?.Trim())
                .OfType<string>()
                .Where(name => name.StartsWith('B'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(BStands, expected.Count);
        Assert.Equal(expected, ranked.Select(c => c.Stand).Order(StringComparer.Ordinal));
        Assert.Empty(Rank(layout, sidecars, Aircraft("DAL123", "A320", spawnDelay: 0), GateScope.Area("Nowhere")));
    }

    [Fact]
    public void UnknownSpan_Super_IsHeldToAtLeastAdgVI()
    {
        AirportGroundLayout layout = Sfo();

        IReadOnlyList<GateCandidate> five = Rank(
            layout,
            Sidecars(Area(TerminalA, "A*", AirplaneDesignGroup.V)),
            Aircraft("ADB123", "A225", spawnDelay: 0)
        );
        IReadOnlyList<GateCandidate> six = Rank(
            layout,
            Sidecars(Area(TerminalA, "A*", AirplaneDesignGroup.VI)),
            Aircraft("ADB123", "A225", spawnDelay: 0)
        );

        Assert.Equal(AStands, InArea(five, TerminalA).Count);
        Assert.All(InArea(five, TerminalA), c => Assert.Equal(GateReason.TooSmall, c.Reason));
        Assert.Equal(AStands, InArea(six, TerminalA).Count);
        Assert.All(InArea(six, TerminalA), c => Assert.Equal(GateReason.Fits, c.Reason));
        Assert.All(five.Where(c => c.Area is null), c => Assert.Equal(c.MaxWingspanFt >= 262.0, c.IsFit));
    }

    [Theory]
    [InlineData(290.0, true, 262.0, AirplaneDesignGroup.VI, true)]
    [InlineData(290.0, true, 262.0, null, false)]
    [InlineData(290.0, true, 300.0, null, false)]
    [InlineData(290.0, true, 214.0, AirplaneDesignGroup.V, false)]
    [InlineData(262.0, false, 262.0, null, true)]
    [InlineData(262.0, false, 214.0, AirplaneDesignGroup.V, false)]
    public void SpanFit_KnownSpanPastAdgVI_FitsOnlyAnAdgVIArea(double spanFt, bool known, double capFt, AirplaneDesignGroup? maxAdg, bool fits) =>
        Assert.Equal(fits, GateRanking.FitsSpan(spanFt, known, capFt, maxAdg));

    [Fact]
    public void TemplatedArea_ReportsItsConcreteArea_AndScopesToThatPlot()
    {
        AirportGroundLayout layout = Sfo();
        AirportSidecarCatalog sidecars = NavigationDatabase.Instance.AirportSidecars;

        IReadOnlyList<GateCandidate> all = Rank(layout, sidecars, Aircraft("N172SP", "C172", spawnDelay: 0));
        IReadOnlyList<GateCandidate> plot2 = Rank(layout, sidecars, Aircraft("N172SP", "C172", spawnDelay: 0), GateScope.Area("PLOT2"));

        Assert.Equal("PLOT2", Stand(all, "2-1").Area);
        Assert.Equal("PLOT41", Stand(all, "41-10").Area);
        Assert.Equal(["2-1", "2-1A", "2-1B", "2-2", "2-2B", "2-3A"], plot2.Select(c => c.Stand).Order(StringComparer.Ordinal));
        Assert.All(plot2, c => Assert.Equal("PLOT2", c.Area));
    }

    [Fact]
    public void AllAreaStands_PlotEntry_CoversExactlyTheDigitsDashStands()
    {
        AirportGroundLayout layout = Sfo();
        IReadOnlyList<ParkingAreaAlias> areas = NavigationDatabase.Instance.AirportSidecars.GetParkingAreas("SFO");

        (ParkingAreaAlias Alias, List<(GroundNode Node, string Name, string Area)> Stands) plot = ArrivalExitSide
            .AllAreaStands(layout, areas)
            .Single(a => a.Alias.Area == "PLOT" + ParkingAreaAlias.NumberToken);

        Assert.Equal(PlotStands.Order(StringComparer.Ordinal), plot.Stands.Select(s => s.Name.Trim()).Distinct().Order(StringComparer.Ordinal));
        Assert.All(plot.Stands, s => Assert.Equal("PLOT" + s.Name.Trim().Split('-')[0], s.Area));
    }

    [Fact]
    public void NonGateParking_IsLeftOut()
    {
        var sidecars = new AirportSidecarCatalog([new AirportSidecar("SFO") { NonGateParking = ["B5"] }]);

        IReadOnlyList<GateCandidate> ranked = Rank(Sfo(), sidecars, Aircraft("DAL123", "A320", spawnDelay: 0));

        Assert.DoesNotContain(ranked, c => string.Equals(c.Stand, "B5", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ranked, c => c.Stand == "B4");
    }

    [Fact]
    public void DuplicateStandName_GivesOneEntry()
    {
        AirportGroundLayout layout = Sfo();

        IReadOnlyList<GateCandidate> ranked = Rank(layout, AirportSidecarCatalog.Empty, Aircraft("DAL123", "A320", spawnDelay: 0));

        Assert.Equal(2, layout.Nodes.Values.Count(n => n is { Type: GroundNodeType.Parking, Name: "2-2B" }));
        Assert.Single(ranked, c => c.Stand == "2-2B");
    }

    private static AirportGroundLayout Sfo() => new TestAirportGroundData().GetLayout("SFO") ?? throw new InvalidOperationException("no SFO layout");

    private static AirportSidecarCatalog TaggedAndUntagged() =>
        Sidecars(Area(TerminalA, "A*", AirplaneDesignGroup.IV, "UAL"), Area(TerminalB, "B*", AirplaneDesignGroup.IV));

    private static AirportSidecarCatalog Sidecars(params ParkingAreaAlias[] areas) => new([new AirportSidecar("SFO") { ParkingAreas = areas }]);

    private static ParkingAreaAlias Area(string name, string pattern, AirplaneDesignGroup maxAdg, params string[] airlines) =>
        new(Normalized(name), [pattern], null) { MaxAdg = maxAdg, Airlines = airlines };

    private static string Normalized(string area) => string.Concat(area.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    private static ScenarioAircraft Aircraft(string callsign, string type, int spawnDelay) =>
        new()
        {
            Id = callsign,
            AircraftId = callsign,
            AircraftType = type,
            AirportId = "SFO",
            SpawnDelay = spawnDelay,
            StartingConditions = new StartingConditions { Type = StartingConditionKinds.Parking },
        };

    private static ScenarioAircraft Parked(string callsign, string stand, string airport, int spawnDelay, int? taxiSeconds)
    {
        ScenarioAircraft aircraft = Aircraft(callsign, "A320", spawnDelay);
        aircraft.AirportId = airport;
        aircraft.StartingConditions.Parking = stand;
        if (taxiSeconds is { } seconds)
        {
            aircraft.ExtensionData = new Dictionary<string, JsonElement>
            {
                [WorkingCopyExporter.AircraftTaxiSecondsKey] = JsonSerializer.SerializeToElement(seconds),
            };
        }

        return aircraft;
    }

    private static IReadOnlyList<GateCandidate> Rank(
        AirportGroundLayout layout,
        AirportSidecarCatalog sidecars,
        ScenarioAircraft aircraft,
        params ScenarioAircraft[] others
    ) => Rank(layout, sidecars, aircraft, GateScope.Any, others);

    private static IReadOnlyList<GateCandidate> Rank(
        AirportGroundLayout layout,
        AirportSidecarCatalog sidecars,
        ScenarioAircraft aircraft,
        GateScope scope,
        params ScenarioAircraft[] others
    ) => GateRanking.Rank(layout, sidecars, new Scenario { Aircraft = [aircraft, .. others] }, aircraft, scope);

    private static List<GateCandidate> InArea(IReadOnlyList<GateCandidate> ranked, string area) => [.. ranked.Where(c => c.Area == Normalized(area))];

    private static List<int> Indexes(IReadOnlyList<GateCandidate> ranked, string area) =>
        [.. Enumerable.Range(0, ranked.Count).Where(i => ranked[i].Area == Normalized(area))];

    private static GateCandidate Stand(IReadOnlyList<GateCandidate> ranked, string stand) =>
        Assert.Single(ranked, c => string.Equals(c.Stand, stand, StringComparison.Ordinal));
}
