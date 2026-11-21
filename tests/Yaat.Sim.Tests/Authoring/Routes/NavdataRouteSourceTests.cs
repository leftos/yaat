using Xunit;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests.Authoring.Routes;

/// <summary>
/// <see cref="NavdataRouteSource"/> joins every SID at the departure to every STAR at the destination on the committed
/// navdata and CIFP: the shortest transition pair per SID and STAR, the max(1.25 x gc, gc + 50 NM) plausibility cut,
/// runway and equipment filters only when the query carries them, twin SIDs merged, and the rank order.
/// </summary>
public class NavdataRouteSourceTests
{
    private static readonly string[] WestFlowKoak = ["28L", "28R", "30"];

    public NavdataRouteSourceTests() => TestVnasData.EnsureInitialized();

    private static NavigationDatabase Db => NavigationDatabase.Instance;

    private static RouteQuery Query(string departure, string destination) => new(departure, destination, [], [], null);

    private static Task<List<FoundRoute>> FindAsync(RouteQuery query) => FindAsync(query, Db);

    private static async Task<List<FoundRoute>> FindAsync(RouteQuery query, NavigationDatabase navDb)
    {
        RouteSourceResult result = await new NavdataRouteSource(navDb).FindAsync(query, CancellationToken.None);
        Assert.Null(result.FailureMessage);
        return [.. result.Routes];
    }

    private static PublishedRouteInfo Info(FoundRoute route) => Assert.IsType<PublishedRouteInfo>(route.Published);

    /// <summary>The SID x STAR combinations behind the rows: a merged twin row stands for two.</summary>
    private static int SidStarCombos(List<FoundRoute> routes) => routes.Count + routes.Count(r => Info(r).TwinSid is not null);

    private static List<string> Expand(string routeText) => RouteExpander.Expand(routeText, Db, includeAllTransitionsOnMismatch: false);

    // Row 1
    [Fact]
    public async Task UnknownAirport_ReturnsNoRoutes_AndNoFailure()
    {
        Assert.Null(Db.GetAirportPosition("KZZZ"));

        Assert.Empty(await FindAsync(Query("KZZZ", "KLAX")));
    }

    // Row 1
    [Fact]
    public async Task KnownAirportWithNoSids_ReturnsNoRoutes_AndNoFailure()
    {
        Assert.NotNull(Db.GetAirportPosition("KHWD"));
        Assert.Empty(Db.GetSids("KHWD"));

        Assert.Empty(await FindAsync(Query("KHWD", "KLAX")));
    }

    // Row 2
    [Fact]
    public async Task OnlyTheDepartureHasProcedures_ReturnsNoRoutes()
    {
        Assert.NotEmpty(Db.GetSids("KHAF"));
        Assert.Empty(Db.GetStars("KHAF"));

        Assert.Empty(await FindAsync(Query("KSFO", "KHAF")));
    }

    // Row 2
    [Fact]
    public async Task OnlyTheArrivalHasProcedures_ReturnsNoRoutes()
    {
        Assert.Empty(Db.GetSids("KHWD"));
        Assert.NotEmpty(Db.GetStars("KHWD"));

        Assert.Empty(await FindAsync(Query("KHWD", "KSFO")));
    }

    // Row 3
    [Fact]
    public async Task RadarVectorSidWithoutEnrouteTransitions_TheNoExitFormWinsWhenShorter()
    {
        CifpSidProcedure oak7 = Assert.IsType<CifpSidProcedure>(Db.GetSid("KOAK", "OAK7"));
        Assert.Empty(oak7.EnrouteTransitions);
        string vnasId = Assert.IsType<string>(Db.ResolveSidId("OAK7"));
        List<string> hints = [.. (Db.GetSidTransitions(vnasId) ?? []).Select(t => t.Fixes[^1])];
        Assert.Contains("SYRAH", hints);

        List<FoundRoute> routes = await FindAsync(Query("KOAK", "KLAS"));

        List<FoundRoute> oak7Rows = [.. routes.Where(r => Info(r).Sid == "OAK7")];
        Assert.Contains(oak7Rows, r => r.RouteText == "OAK7 DAG CRESO5");
        Assert.All(oak7Rows, r => Assert.True((r.RouteText.Split(' ').Length == 3) || hints.Contains(r.RouteText.Split(' ')[1]), r.RouteText));
        Assert.All(oak7Rows, r => Assert.Equal(ProcedureRouteKind.RadarVector, Info(r).SidKind));
    }

    // Row 3
    [Fact]
    public async Task RadarVectorSidExitHint_WinsWhenTheQueryFixNamesIt()
    {
        List<FoundRoute> routes = await FindAsync(Query("KOAK", "KLAS") with { Fix = "SYRAH" });

        Assert.Contains(routes, r => r.RouteText.StartsWith("OAK7 SYRAH ", StringComparison.Ordinal));
    }

    // Row 4
    [Fact]
    public async Task RadarVectorSidWithoutExitFixes_JoinsTheStarEntryDirect()
    {
        CifpSidProcedure sns3 = Assert.IsType<CifpSidProcedure>(Db.GetSid("KSNS", "SNS3"));
        Assert.Equal(ProcedureRouteKind.RadarVector, sns3.RouteKind);
        Assert.Empty(sns3.EnrouteTransitions);
        string vnasId = Assert.IsType<string>(Db.ResolveSidId("SNS3"));
        Assert.Empty(Db.GetSidTransitions(vnasId) ?? []);

        List<FoundRoute> routes = await FindAsync(Query("KSNS", "KLAX"));

        List<FoundRoute> sns3Rows = [.. routes.Where(r => Info(r).Sid == "SNS3")];
        Assert.NotEmpty(sns3Rows);
        Assert.All(sns3Rows, r => Assert.Equal(3, r.RouteText.Split(' ').Length));
    }

    // Row 5
    [Fact]
    public async Task BodyOnlySid_ExitsAtItsLastBodyFix()
    {
        List<FoundRoute> routes = await FindAsync(Query("KOAK", "KLAS"));

        List<FoundRoute> sunneRows = [.. routes.Where(r => Info(r).Sid == "SUNNE1")];
        Assert.Contains(sunneRows, r => r.RouteText == "SUNNE1 SUNNE BTY PUMLE1");
        Assert.All(sunneRows, r => Assert.Equal("SUNNE", r.RouteText.Split(' ')[1]));
    }

    // Row 5
    [Fact]
    public async Task RunwayTransitionOnlySid_UnderARunwayQuery_ExitsAtThatRunwaysEndFix()
    {
        List<FoundRoute> routes = await FindAsync(Query("KOAK", "KLAS") with { DepartureRunways = ["30"] });

        Assert.Contains(routes, r => r.RouteText == "SUNNE1 SUNNE BTY PUMLE1");
    }

    // Row 18
    [Fact]
    public async Task CandidateThroughAFixTheNavdataLacks_IsDropped()
    {
        // Built by hand: no SID on the committed data exits at a fix the navdata lacks.
        var exitLeg = new CifpLeg("GHOST", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null);
        var sid = new CifpSidProcedure(
            "KAAA",
            "RVXX1",
            [],
            new Dictionary<string, CifpTransition>(),
            new Dictionary<string, CifpTransition> { ["GHOST"] = new("GHOST", [exitLeg]) },
            ProcedureRouteKind.Conventional
        );
        var entryLeg = new CifpLeg("ENTRY", CifpPathTerminator.IF, null, null, null, CifpFixRole.None, 10, null, null, null);
        var star = new CifpStarProcedure(
            "KBBB",
            "STRRR1",
            [entryLeg],
            new Dictionary<string, CifpTransition>(),
            new Dictionary<string, CifpTransition>(),
            ProcedureRouteKind.Conventional
        );
        var navDb = NavigationDatabase.ForTesting(
            fixes: new Dictionary<string, (double Lat, double Lon)> { ["ENTRY"] = (37.5, -122.0), ["MIDDL"] = (38.0, -122.0) },
            sids: [sid],
            stars: [star],
            starBodies: new Dictionary<string, IReadOnlyList<string>> { ["STRRR1"] = ["ENTRY", "MIDDL"] },
            sidBodies: new Dictionary<string, IReadOnlyList<string>> { ["RVXX1"] = [] },
            airportPositions: new Dictionary<string, (double Lat, double Lon)> { ["KAAA"] = (37.0, -122.0), ["KBBB"] = (38.5, -122.0) }
        );

        Assert.Null(navDb.GetFixPosition("GHOST"));

        Assert.Empty(await FindAsync(Query("KAAA", "KBBB"), navDb));
    }

    // Row 6
    [Fact]
    public async Task EveryTransitionPair_YieldsOneRowPerSidAndStar()
    {
        List<FoundRoute> routes = await FindAsync(Query("KSFO", "KLAX"));

        Assert.NotEmpty(routes);
        Assert.Equal(routes.Count, routes.Select(r => (Info(r).Sid, Info(r).Star)).Distinct().Count());
        Assert.All(routes, r => Assert.Equal(0, r.Count));
        Assert.All(routes, r => Assert.Equal(["navdata"], r.Sources));
    }

    // Row 7
    [Fact]
    public async Task QueryFix_KeepsRoutesThroughIt_ChoosingTheBestPairAmongThem()
    {
        List<FoundRoute> all = await FindAsync(Query("KSFO", "KLAX"));
        Dictionary<(string, string), string> bestText = all.ToDictionary(r => (Info(r).Sid, Info(r).Star), r => r.RouteText);

        List<FoundRoute> throughAve = await FindAsync(Query("KSFO", "KLAX") with { Fix = "ave" });

        Assert.NotEmpty(throughAve);
        Assert.All(throughAve, r => Assert.Contains("AVE", Expand(r.RouteText)));
        Assert.Contains(throughAve, r => !bestText.TryGetValue((Info(r).Sid, Info(r).Star), out string? text) || (text != r.RouteText));
    }

    // Row 8
    [Fact]
    public async Task SharedExitAndEntryFix_IsWrittenOnce()
    {
        List<FoundRoute> routes = await FindAsync(Query("KSFO", "KRNO"));

        Assert.Contains(routes, r => r.RouteText.Split(' ').Length == 3);
        Assert.All(routes, r => AssertNoRepeatedAdjacentToken(r.RouteText));
    }

    // Row 9
    [Fact]
    public async Task DifferentExitAndEntry_AreAdjacentTokens_WithNoDctDotsOrAirports()
    {
        List<FoundRoute> routes = await FindAsync(Query("KSFO", "KLAX"));

        Assert.Contains(routes, r => r.RouteText.Split(' ').Length == 4);
        Assert.All(
            routes,
            r =>
            {
                string[] tokens = r.RouteText.Split(' ');
                Assert.DoesNotContain("DCT", tokens);
                Assert.DoesNotContain('.', r.RouteText);
                Assert.DoesNotContain("KSFO", tokens);
                Assert.DoesNotContain("KLAX", tokens);
                AssertNoRepeatedAdjacentToken(r.RouteText);
            }
        );
    }

    // Row 10
    [Theory]
    [InlineData("KOAK", "KSMF", 4)]
    [InlineData("KSFO", "KSJC", 0)]
    [InlineData("KSFO", "KLAX", 68)]
    [InlineData("KOAK", "KLAS", 46)]
    public async Task ImplausiblePairs_AreDropped_ByTheLargerOfRatioAndAdditiveAllowance(string departure, string destination, int combos)
    {
        List<FoundRoute> routes = await FindAsync(Query(departure, destination));

        Assert.True(
            SidStarCombos(routes) == combos,
            $"{SidStarCombos(routes)} combos, expected {combos}: " + string.Join("; ", routes.Select(r => $"{r.RouteText} {Info(r).Ratio:F3}"))
        );
        (double Lat, double Lon) dep = Db.GetAirportPosition(departure)!.Value;
        (double Lat, double Lon) arr = Db.GetAirportPosition(destination)!.Value;
        double gc = GeoMath.DistanceNm(dep.Lat, dep.Lon, arr.Lat, arr.Lon);
        Assert.All(routes, r => Assert.True(Info(r).LengthNm <= Math.Max(1.25 * gc, gc + 50), $"{r.RouteText} {Info(r).LengthNm:F1} NM"));
    }

    // Row 11
    [Fact]
    public async Task NoRunwayInQuery_KeepsEveryRunway_AndTagsTheCodedOnes()
    {
        List<FoundRoute> routes = await FindAsync(Query("KSFO", "KLAX"));

        FoundRoute sstik = routes.First(r => Info(r).Sid == "SSTIK5");
        Assert.Contains("01B", Info(sstik).SidRunways);
        Assert.Contains(routes, r => Info(r).StarRunways.Count == 0);
        Assert.Contains(routes, r => Info(r).Star == "ZUUMA4");
    }

    // Row 11
    [Fact]
    public async Task RunwayOnlySid_WithDifferingTransitionEnds_ListsOnlyTheRunwaysEndingAtTheRowsExit()
    {
        CifpSidProcedure montn = Assert.IsType<CifpSidProcedure>(Db.GetSid("KSEA", "MONTN2"));
        Assert.Empty(montn.CommonLegs);
        Assert.Empty(montn.EnrouteTransitions);
        Assert.Equal(6, montn.RunwayTransitions.Count);

        List<FoundRoute> routes = await FindAsync(Query("KSEA", "KLAS"));

        List<FoundRoute> montnRows = [.. routes.Where(r => Info(r).Sid == "MONTN2")];
        Assert.NotEmpty(montnRows);
        Assert.All(montnRows, r => Assert.Equal(RunwaysEndingAt(montn, r.RouteText.Split(' ')[1]), Info(r).SidRunways));

        // The rows exit at PEAKK, which only the 16s' transitions reach; the 34s end at NEZUG.
        List<string> listed = [.. montnRows.Select(r => string.Join(',', Info(r).SidRunways)).Distinct()];
        Assert.Equal(["16C,16L,16R"], listed);
    }

    // Row 12
    [Fact]
    public async Task QueryRunways_DropProceduresCodedOnlyForOtherRunways()
    {
        RouteQuery westFlow = Query("KSFO", "KLAX") with { DepartureRunways = ["28L", "28R"], ArrivalRunways = ["24L", "24R", "25L", "25R"] };

        List<FoundRoute> routes = await FindAsync(westFlow);

        Assert.NotEmpty(routes);
        Assert.DoesNotContain(routes, r => Info(r).Star == "ZUUMA4");
        Assert.DoesNotContain(routes, r => Info(r).Sid == "SSTIK5");
        Assert.Contains(routes, r => (Info(r).Sid == "WESLA5") && (Info(r).TwinSid is null));
    }

    // Row 12
    [Theory]
    [InlineData("1L")]
    [InlineData("01L")]
    [InlineData("RW1R")]
    public async Task DepartureRunwayWithOrWithoutLeadingZero_MatchesTheCodedRunway(string runway)
    {
        List<FoundRoute> routes = await FindAsync(Query("KSFO", "KLAX") with { DepartureRunways = [runway] });

        Assert.Contains(routes, r => Info(r).Sid == "SSTIK5");
    }

    // Row 12
    [Theory]
    [InlineData("6L")]
    [InlineData("06L")]
    [InlineData("7R")]
    public async Task ArrivalRunwayWithOrWithoutLeadingZero_MatchesTheCodedRunway(string runway)
    {
        List<FoundRoute> routes = await FindAsync(Query("KSFO", "KLAX") with { ArrivalRunways = [runway] });

        Assert.Contains(routes, r => Info(r).Star == "ZUUMA4");
    }

    // Row 13
    [Fact]
    public async Task RunwayTwinSids_MergeIntoOneRow_UnderTheOrdinalFirstSid_EachKeepingItsRunways()
    {
        List<FoundRoute> routes = await FindAsync(Query("KSFO", "KLAX"));

        FoundRoute twin = routes.First(r => Info(r).TwinSid == "WESLA5");
        Assert.Equal("SSTIK5", Info(twin).Sid);
        Assert.StartsWith("SSTIK5 ", twin.RouteText);
        Assert.Equal(["01B"], Info(twin).SidRunways);
        Assert.Equal(["28B"], Info(twin).TwinSidRunways);
        Assert.DoesNotContain(routes, r => Info(r).Sid == "WESLA5");
        Assert.All(routes.Where(r => Info(r).TwinSid is null), r => Assert.Empty(Info(r).TwinSidRunways));
    }

    // Row 14
    [Fact]
    public async Task NoEquipmentSuffix_FiltersNothing_AndTagsEachKind()
    {
        List<FoundRoute> routes = await FindAsync(Query("KSFO", "KLAX"));

        Assert.Contains(routes, r => Info(r).SidKind == ProcedureRouteKind.Rnav);
        Assert.Contains(routes, r => Info(r).SidKind == ProcedureRouteKind.RadarVector);
        Assert.Contains(routes, r => Info(r).StarKind == ProcedureRouteKind.Rnav);
        Assert.Contains(routes, r => Info(r).StarKind == ProcedureRouteKind.Conventional);
    }

    // Row 15
    [Theory]
    [InlineData("/A", true)]
    [InlineData("A", true)]
    [InlineData("/L", false)]
    [InlineData("/G", false)]
    [InlineData("/H", false)]
    [InlineData("/O", false)]
    [InlineData("/Q", false)]
    [InlineData("/E", false)]
    [InlineData("/W", true)]
    public async Task EquipmentSuffix_DropsRnavProceduresOnlyWhenKnownNotRnavCapable(string suffix, bool dropsRnav)
    {
        List<FoundRoute> unfiltered = await FindAsync(Query("KSFO", "KLAX"));

        List<FoundRoute> routes = await FindAsync(Query("KSFO", "KLAX") with { EquipmentSuffix = suffix });

        if (dropsRnav)
        {
            Assert.All(routes, r => Assert.NotEqual(ProcedureRouteKind.Rnav, Info(r).SidKind));
            Assert.All(routes, r => Assert.NotEqual(ProcedureRouteKind.Rnav, Info(r).StarKind));
            Assert.Contains(routes, r => Info(r).Sid == "GAPP7");
            Assert.Contains(routes, r => Info(r).StarKind == ProcedureRouteKind.Conventional);

            // No conventional KSFO SID survives the plausibility cut to KLAX; KOAK's conventional SIDs do to KLAS.
            List<FoundRoute> fromOakland = await FindAsync(Query("KOAK", "KLAS") with { EquipmentSuffix = suffix });
            Assert.Contains(fromOakland, r => Info(r).SidKind == ProcedureRouteKind.Conventional);
        }
        else
        {
            Assert.Equal(unfiltered.Select(r => r.RouteText), routes.Select(r => r.RouteText));
        }
    }

    // Row 16
    [Fact]
    public async Task NonRnavSuffix_DropsEveryPairWithADirectLegOver260Nm_AndKeepsTheRest()
    {
        List<FoundRoute> routes = await FindAsync(Query("KOAK", "KLAS") with { EquipmentSuffix = "/A" });

        List<string> texts = [.. routes.Select(r => r.RouteText)];
        Assert.DoesNotContain("OAK7 DAG CRESO5", texts);
        Assert.DoesNotContain("OAK7 DAG LARKK1", texts);
        Assert.DoesNotContain("OAK7 BTY PUMLE1", texts);
        Assert.DoesNotContain("SUNNE1 SUNNE DAG LARKK1", texts);
        Assert.DoesNotContain("SALAD6 ALTAM DAG LARKK1", texts);
        Assert.Contains("SALAD6 ALTAM BTY PUMLE1", texts);
        Assert.Contains("SUNNE1 SUNNE BTY PUMLE1", texts);
        Assert.Contains("SKYL2 PXN DAG LARKK1", texts);
    }

    // Row 16
    [Fact]
    public void RadarVectorSid_LegsFromItsBodyThroughTheRouteHint_AreDirectLegs()
    {
        List<NavdataRoutePairer.DirectLeg> legs = NavdataRoutePairer.DirectLegs(
            Db,
            "KOAK",
            "OAK7 SYRAH BTY PUMLE1",
            "OAK7",
            ProcedureRouteKind.RadarVector
        );

        Assert.Equal(["KOAK->OAK", "OAK->SYRAH", "SYRAH->BTY"], legs.Select(leg => $"{leg.From}->{leg.To}"));
        Assert.InRange(legs[1].Nm, 50.0, 60.0);
    }

    // Row 16
    [Fact]
    public async Task NonRnavSuffix_DropsARadarVectorSidWhoseAirportLegIsOver260Nm_RnavKeepsIt()
    {
        // KPSM TANKR3's vNAS body opens at NFW286006, 1399.6 NM from KPSM: the vectors end direct there.
        List<FoundRoute> nonRnav = await FindAsync(Query("KPSM", "KDFW") with { EquipmentSuffix = "/A" });
        List<FoundRoute> rnav = await FindAsync(Query("KPSM", "KDFW") with { EquipmentSuffix = "/L" });

        Assert.DoesNotContain(nonRnav, r => Info(r).Sid == "TANKR3");
        Assert.Contains(rnav, r => Info(r).Sid == "TANKR3");
    }

    // Row 16
    [Fact]
    public async Task NonRnavSuffix_DropsOak7DagCreso5_WhoseDirectLegToDagIsOver260Nm_RnavKeepsIt()
    {
        // OAK7's body is the OAK VOR; OAK to DAG is 319 NM, over AIM 5-1-6c.3(b)'s 260 NM navaid spacing.
        List<FoundRoute> nonRnav = await FindAsync(Query("KOAK", "KLAS") with { EquipmentSuffix = "/A" });
        List<FoundRoute> rnav = await FindAsync(Query("KOAK", "KLAS") with { EquipmentSuffix = "/L" });

        Assert.DoesNotContain(nonRnav, r => r.RouteText == "OAK7 DAG CRESO5");
        Assert.Contains(rnav, r => r.RouteText == "OAK7 DAG CRESO5");
    }

    // Row 16
    [Fact]
    public async Task Ranking_SortsByRatio_WithAtMostTwoRowsPerSidAndStarOnTheFirstPage()
    {
        List<FoundRoute> routes = await FindAsync(Query("KSFO", "KLAX"));
        Assert.True(routes.Count > 10);

        List<FoundRoute> firstPage = [.. routes.Take(10)];
        Assert.All(firstPage.GroupBy(r => Info(r).Sid), g => Assert.True(g.Count() <= 2, g.Key));
        Assert.All(firstPage.GroupBy(r => Info(r).Star), g => Assert.True(g.Count() <= 2, g.Key));
        AssertRatioOrder(firstPage);
        AssertRatioOrder([.. routes.Skip(10)]);
        Assert.Equal(routes.Min(r => Info(r).Ratio), Info(routes[0]).Ratio);
    }

    // Row 17
    [Fact]
    public async Task TwoRuns_GiveTheSameRoutesInTheSameOrder()
    {
        List<FoundRoute> first = await FindAsync(Query("KOAK", "KLAS"));
        List<FoundRoute> second = await FindAsync(Query("KOAK", "KLAS"));

        Assert.Equal(first.Select(r => r.RouteText), second.Select(r => r.RouteText));
    }

    // Row 18
    [Fact]
    public async Task OfferedRoutes_ExpandToFixesTheNavdataKnows()
    {
        List<FoundRoute> routes = await FindAsync(Query("KSFO", "KLAX"));

        Assert.All(
            routes,
            r =>
            {
                List<string> fixes = Expand(r.RouteText);
                Assert.NotEmpty(fixes);
                Assert.All(fixes, f => Assert.NotNull(Db.GetFixPosition(f)));
            }
        );
    }

    // Row 19
    [Fact]
    public async Task SameAirportBothEnds_ReturnsNoRoutes() => Assert.Empty(await FindAsync(Query("KSFO", "ksfo")));

    // Row 20
    [Fact]
    public async Task OnlyTheAirportsOwnCifpProcedures_AreOffered()
    {
        HashSet<string> sids = [.. Db.GetSids("KSFO").Select(s => s.ProcedureId)];
        HashSet<string> stars = [.. Db.GetStars("KLAX").Select(s => s.ProcedureId)];

        List<FoundRoute> routes = await FindAsync(Query("KSFO", "KLAX"));

        Assert.All(routes, r => Assert.Contains(Info(r).Sid, sids));
        Assert.All(routes, r => Assert.Contains(Info(r).Star, stars));
    }

    [Fact]
    public async Task SidWithoutRunwayCoding_IsTagged_AndKeptUnderAWestFlowRunwayQuery()
    {
        List<FoundRoute> routes = await FindAsync(Query("KOAK", "KLAS") with { DepartureRunways = WestFlowKoak });

        FoundRoute salad = routes.First(r => Info(r).Sid == "SALAD6");
        Assert.True(Info(salad).SidRunwaysNotCoded);
        Assert.Empty(Info(salad).SidRunways);
        Assert.Equal(ProcedureRouteKind.Conventional, Info(salad).SidKind);
        Assert.Equal("runways not coded", PublishedRouteInfo.RunwaysNotCodedTag);
    }

    [Fact]
    public async Task NavdataRow_MergedWithTheSameObservedRoute_KeepsItsPublishedInfo()
    {
        var navdata = new NavdataRouteSource(Db);
        FoundRoute published = (await FindAsync(Query("KSFO", "KLAX")))[0];
        var observed = new FixedSource(new FoundRoute(published.RouteText, 7, ["csko"], Published: null));

        RouteFindResult result = await new RouteFinder([observed, navdata], Db).FindAsync(Query("KSFO", "KLAX"), CancellationToken.None);

        FoundRoute merged = result.Routes.First(r => r.RouteText == published.RouteText);
        Assert.Equal(7, merged.Count);
        Assert.Equal(["csko", "navdata"], merged.Sources);
        Assert.Equal(
            (Info(published).Sid, Info(published).Star, Info(published).LengthNm),
            (Info(merged).Sid, Info(merged).Star, Info(merged).LengthNm)
        );
    }

    private sealed class FixedSource(params FoundRoute[] routes) : IRouteSource
    {
        public string Name => "csko";

        public Task<RouteSourceResult> FindAsync(RouteQuery query, CancellationToken ct) =>
            Task.FromResult(new RouteSourceResult(routes, FailureMessage: null));
    }

    /// <summary>The SID's coded runways whose runway transition ends at <paramref name="exit"/>, ordinal-sorted.</summary>
    private static List<string> RunwaysEndingAt(CifpSidProcedure sid, string exit) =>
        [
            .. sid
                .RunwayTransitions.Where(t => string.Equals(EndFix(t.Value), exit, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Key.StartsWith("RW", StringComparison.OrdinalIgnoreCase) ? t.Key[2..] : t.Key)
                .Order(StringComparer.Ordinal),
        ];

    private static string? EndFix(CifpTransition transition) =>
        transition.Legs.LastOrDefault(leg => !string.IsNullOrEmpty(leg.FixIdentifier))?.FixIdentifier;

    private static void AssertNoRepeatedAdjacentToken(string routeText)
    {
        string[] tokens = routeText.Split(' ');
        for (int i = 1; i < tokens.Length; i++)
        {
            Assert.False(string.Equals(tokens[i - 1], tokens[i], StringComparison.OrdinalIgnoreCase), routeText);
        }
    }

    private static void AssertRatioOrder(List<FoundRoute> routes)
    {
        for (int i = 1; i < routes.Count; i++)
        {
            Assert.True(Info(routes[i - 1]).Ratio <= Info(routes[i]).Ratio, $"{routes[i - 1].RouteText} before {routes[i].RouteText}");
        }
    }
}
