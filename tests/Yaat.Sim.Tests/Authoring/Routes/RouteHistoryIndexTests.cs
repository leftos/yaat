using Xunit;
using Yaat.Sim.Authoring.Routes;

namespace Yaat.Sim.Tests.Authoring.Routes;

/// <summary>
/// <see cref="RouteHistoryIndex"/> keeps the routes observed on the SWIM feed as per-day buckets of counts. These pin what it
/// records (and refuses), how it merges route-text variants, the window and budget trimming, the query filters, the observed
/// sample and the line format it saves and reloads, which never holds a full callsign.
/// </summary>
public class RouteHistoryIndexTests
{
    private const string SstikIrnmn = "SSTIK5 SUSEY EBAYE BURGL IRNMN2";

    private static readonly DateOnly Today = new(2026, 11, 21);

    private static readonly RouteQuery SfoLax = new("KSFO", "KLAX", [], [], null);

    private static DateTime At(DateOnly day) => day.ToDateTime(new TimeOnly(15, 30), DateTimeKind.Utc);

    /// <summary>A B738 between KSFO and KLAX on the SSTIK5 route.</summary>
    private static RouteObservation Flight(DateOnly day, string callsign) => Flight(day, callsign, "B738", SstikIrnmn);

    private static RouteObservation Flight(DateOnly day, string callsign, string? type, string? route) =>
        new(At(day), callsign, type, "KSFO", "KLAX", route);

    private static RouteHistoryIndex Reloaded(string lines)
    {
        var index = new RouteHistoryIndex();
        index.ReadLines(new StringReader(lines));
        return index;
    }

    private static string Saved(RouteHistoryIndex index)
    {
        var writer = new StringWriter();
        index.WriteLines(writer);
        return writer.ToString();
    }

    private static RouteHistoryIndex IndexOf(params RouteObservation[] flights)
    {
        var index = new RouteHistoryIndex();
        foreach (RouteObservation flight in flights)
        {
            index.Add(flight);
        }

        return index;
    }

    [Fact]
    public void PlanInsideWindow_IsKept_AndQueryableByDepartureAndDestination()
    {
        RouteHistoryIndex index = IndexOf(Flight(Today.AddDays(-3), "UAL123"));

        index.Trim(Today, maxAgeDays: 30, maxBytes: long.MaxValue);

        FoundRoute route = Assert.Single(index.Query(SfoLax));
        Assert.Equal(SstikIrnmn, route.RouteText);
        Assert.Equal(1, route.Count);
        Assert.Equal(["history"], route.Sources);
        Assert.Null(route.Published);
        Assert.Null(route.Preferred);
        Assert.Empty(index.Query(new RouteQuery("KSFO", "KSAN", [], [], null)));
    }

    [Fact]
    public void NasRouteText_IsRecordedAsItsSpacedKey()
    {
        var index = new RouteHistoryIndex();

        index.Add(new RouteObservation(At(Today), "UAL174", "C525", "KHOU", "KABQ", "KHOU.RETYR8.JCT..CME.MIERA2.KABQ/0210"));

        RouteHistoryRecord record = Assert.Single(index.Records());
        Assert.Equal("RETYR8 JCT CME MIERA2", record.RouteText);
    }

    [Fact]
    public void AnAmendmentFromAPresentPosition_IsNotRecorded()
    {
        var index = new RouteHistoryIndex();

        Assert.Null(index.Add(new RouteObservation(At(Today), "UAL174", "C525", "KHOU", "KABQ", "./.CME.MIERA2.KABQ")));
        Assert.Empty(index.Records());
    }

    [Fact]
    public void PlanOlderThanWindow_IsTrimmed()
    {
        RouteHistoryIndex index = IndexOf(Flight(Today.AddDays(-31), "UAL123"), Flight(Today, "UAL456", "B738", "WESLA5 SUSEY EBAYE BURGL IRNMN2"));

        IReadOnlyList<DateOnly> dropped = index.Trim(Today, maxAgeDays: 30, maxBytes: long.MaxValue);

        Assert.Equal([Today.AddDays(-31)], dropped);
        Assert.Equal(["WESLA5 SUSEY EBAYE BURGL IRNMN2"], index.Query(SfoLax).Select(r => r.RouteText));
    }

    [Fact]
    public void PlanExactlyAtWindowEdge_IsKept()
    {
        RouteHistoryIndex index = IndexOf(Flight(Today.AddDays(-30), "UAL123"));

        IReadOnlyList<DateOnly> dropped = index.Trim(Today, maxAgeDays: 30, maxBytes: long.MaxValue);

        Assert.Empty(dropped);
        Assert.Single(index.Query(SfoLax));
    }

    [Fact]
    public void SameRoute_ThreeFlights_IsOneEntryWithCountThree()
    {
        RouteHistoryIndex index = IndexOf(Flight(Today, "UAL1"), Flight(Today, "UAL2"), Flight(Today, "UAL3"));

        RouteHistoryRecord record = Assert.Single(index.Records());
        Assert.Equal(3, record.Count);
        Assert.Equal(3, Assert.Single(index.Query(SfoLax)).Count);
    }

    [Fact]
    public void SameRouteOnSeveralDays_IsOneQueryRow_SummedOverDays()
    {
        RouteHistoryIndex index = IndexOf(Flight(Today.AddDays(-2), "UAL1"), Flight(Today.AddDays(-1), "UAL2"), Flight(Today, "UAL3"));

        Assert.Equal(3, index.Records().Count);
        Assert.Equal(3, Assert.Single(index.Query(SfoLax)).Count);
    }

    [Fact]
    public void RouteTextVariants_WithDctAndAirports_MergeIntoOneEntry()
    {
        RouteHistoryIndex index = IndexOf(
            Flight(Today, "UAL1"),
            Flight(Today, "UAL2", "B738", "KSFO SSTIK5 SUSEY DCT EBAYE BURGL IRNMN2 KLAX"),
            Flight(Today, "UAL3", "B738", "  sstik5   susey ebaye burgl irnmn2 ")
        );

        FoundRoute route = Assert.Single(index.Query(SfoLax));
        Assert.Equal(SstikIrnmn, route.RouteText);
        Assert.Equal(3, route.Count);
    }

    [Fact]
    public void FixFilter_KeepsOnlyRoutesHoldingTheFixAsAWholeToken()
    {
        RouteHistoryIndex index = IndexOf(Flight(Today, "UAL1"), Flight(Today, "UAL2", "B738", "OFFSH9 SNS LYNSY ANJLL4"));

        Assert.Equal([SstikIrnmn], index.Query(SfoLax with { Fix = "susey" }).Select(r => r.RouteText));
        Assert.Empty(index.Query(SfoLax with { Fix = "SUS" }));
        Assert.Equal(["OFFSH9 SNS LYNSY ANJLL4"], index.Query(SfoLax with { Fix = "SNS" }).Select(r => r.RouteText));
    }

    [Fact]
    public void AirlinePrefixFilter_MatchesThePrefixExactly_CaseInsensitive()
    {
        RouteHistoryIndex index = IndexOf(
            Flight(Today, "UAL123"),
            Flight(Today, "UAE12", "B738", "WESLA5 SUSEY EBAYE BURGL IRNMN2"),
            Flight(Today, "N123AB", "B738", "OFFSH9 SNS LYNSY ANJLL4")
        );

        Assert.Equal([SstikIrnmn], index.Query(SfoLax with { AirlinePrefix = "UAL" }).Select(r => r.RouteText));
        Assert.Equal([SstikIrnmn], index.Query(SfoLax with { AirlinePrefix = "ual" }).Select(r => r.RouteText));
        Assert.Empty(index.Query(SfoLax with { AirlinePrefix = "UA" }));
        Assert.Empty(index.Query(SfoLax with { AirlinePrefix = "N12" }));
    }

    [Theory]
    [InlineData("UAL123", "UAL")]
    [InlineData("ual9", "UAL")]
    [InlineData("SWA1234", "SWA")]
    [InlineData("N123AB", null)]
    [InlineData("RCH123", "RCH")]
    [InlineData("REACH1", null)]
    [InlineData("UA123", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AirlinePrefixOf_IsTheLeadingThreeLetters_OnlyBeforeADigit(string? callsign, string? expected) =>
        Assert.Equal(expected, RouteHistoryIndex.AirlinePrefixOf(callsign));

    [Fact]
    public void AircraftTypeFilter_MatchesTheExactDesignator()
    {
        RouteHistoryIndex index = IndexOf(
            Flight(Today, "UAL1", "B738", SstikIrnmn),
            Flight(Today, "UAL2", "B38M", "WESLA5 SUSEY EBAYE BURGL IRNMN2")
        );

        Assert.Equal([SstikIrnmn], index.Query(SfoLax with { AircraftType = "B738" }).Select(r => r.RouteText));
        Assert.Empty(index.Query(SfoLax with { AircraftType = "B73" }));
    }

    [Theory]
    [InlineData("KSFO", "KLAX", "")]
    [InlineData("KSFO", "KLAX", "   ")]
    [InlineData("KSFO", "KLAX", null)]
    [InlineData("KSFO", "KLAX", "KSFO KLAX")]
    [InlineData(null, "KLAX", SstikIrnmn)]
    [InlineData("KSFO", "", SstikIrnmn)]
    public void EmptyRouteOrMissingAirport_IsNotRecorded(string? departure, string? destination, string? route)
    {
        var index = new RouteHistoryIndex();

        RouteHistoryRecord? added = index.Add(new RouteObservation(At(Today), "UAL1", "B738", departure, destination, route));

        Assert.Null(added);
        Assert.Empty(index.Records());
    }

    [Fact]
    public void Add_ReturnsTheIncrementItRecorded_WithPrefixAndTypeOnly()
    {
        var index = new RouteHistoryIndex();

        RouteHistoryRecord? added = index.Add(new RouteObservation(At(Today), "ual123", "b738", "ksfo", "klax", "KSFO DCT " + SstikIrnmn));

        Assert.Equal(new RouteHistoryRecord(Today, "KSFO", "KLAX", SstikIrnmn, "B738", "UAL", 1), added);
    }

    [Fact]
    public void SerializedLine_HoldsNoFullCallsign()
    {
        RouteHistoryIndex index = IndexOf(Flight(Today, "UAL1234"), Flight(Today, "N123AB", "B738", "OFFSH9 SNS LYNSY ANJLL4"));

        var writer = new StringWriter();
        index.WriteLines(writer);
        string saved = writer.ToString();

        Assert.DoesNotContain("UAL1234", saved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("N123AB", saved, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"UAL\"", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void Observed_IsTheMostFlownPrefixAndTypeOfTheRoute()
    {
        RouteHistoryIndex index = IndexOf(
            Flight(Today, "SWA1", "B737", SstikIrnmn),
            Flight(Today.AddDays(-1), "UAL1", "A320", SstikIrnmn),
            Flight(Today, "UAL2", "A320", SstikIrnmn),
            Flight(Today, "UAL3", "B738", SstikIrnmn)
        );

        FoundRoute route = Assert.Single(index.Query(SfoLax));
        Assert.Equal(4, route.Count);
        Assert.Equal(new ObservedRouteSample("UAL", "A320"), route.Observed);
    }

    [Fact]
    public void Query_OrdersRoutesMostFlownFirst()
    {
        RouteHistoryIndex index = IndexOf(
            Flight(Today, "UAL1", "B738", "OFFSH9 SNS LYNSY ANJLL4"),
            Flight(Today, "UAL2"),
            Flight(Today, "UAL3"),
            Flight(Today, "UAL4", "B738", "OFFSH9 SNS LYNSY ANJLL4"),
            Flight(Today, "UAL5", "B738", "OFFSH9 SNS LYNSY ANJLL4")
        );

        Assert.Equal([("OFFSH9 SNS LYNSY ANJLL4", 3), (SstikIrnmn, 2)], index.Query(SfoLax).Select(r => (r.RouteText, r.Count)));
    }

    [Fact]
    public void BudgetExceeded_DropsTheOldestDaysFirst_KeepingTheNewest()
    {
        RouteHistoryIndex index = IndexOf(Flight(Today.AddDays(-2), "UAL1"), Flight(Today.AddDays(-1), "UAL2"), Flight(Today, "UAL3"));
        long twoDays = index.Records().Where(r => r.Day >= Today.AddDays(-1)).Sum(r => RouteHistoryIndex.ToLine(r).Length + 1L);

        IReadOnlyList<DateOnly> dropped = index.Trim(Today, maxAgeDays: 30, maxBytes: twoDays);

        Assert.Equal([Today.AddDays(-2)], dropped);
        Assert.Equal([Today.AddDays(-1), Today], index.Records().Select(r => r.Day));
        Assert.Equal(twoDays, index.SerializedBytes);
    }

    [Fact]
    public void BudgetSmallerThanTheNewestDay_KeepsTheNewestDay()
    {
        RouteHistoryIndex index = IndexOf(Flight(Today.AddDays(-1), "UAL1"), Flight(Today, "UAL2"));

        IReadOnlyList<DateOnly> dropped = index.Trim(Today, maxAgeDays: 30, maxBytes: 1);

        Assert.Equal([Today.AddDays(-1)], dropped);
        Assert.Equal([Today], index.Records().Select(r => r.Day));
    }

    [Fact]
    public void ReloadFromLines_EqualsTheIndexBeforeSaving()
    {
        RouteHistoryIndex index = IndexOf(
            Flight(Today.AddDays(-1), "UAL1"),
            Flight(Today, "UAL2"),
            Flight(Today, "N123AB", null, "OFFSH9 SNS LYNSY ANJLL4"),
            Flight(Today, "UAL3")
        );
        var writer = new StringWriter();
        index.WriteLines(writer);

        var reloaded = new RouteHistoryIndex();
        int loaded = reloaded.ReadLines(new StringReader(writer.ToString()));

        Assert.Equal(3, loaded);
        Assert.Equal(index.Records(), reloaded.Records());
        Assert.Equal(index.SerializedBytes, reloaded.SerializedBytes);
    }

    [Fact]
    public void ReloadFromLines_SkipsATornLastLine_AndLoadsTheRest()
    {
        RouteHistoryIndex index = IndexOf(Flight(Today, "UAL1"), Flight(Today, "SWA2", "B738", "OFFSH9 SNS LYNSY ANJLL4"));
        var writer = new StringWriter();
        index.WriteLines(writer);
        string torn = writer.ToString() + "{\"day\":\"2026-11-21\",\"departure\":\"KS";

        var reloaded = new RouteHistoryIndex();
        int loaded = reloaded.ReadLines(new StringReader(torn));

        Assert.Equal(2, loaded);
        Assert.Equal(index.Records(), reloaded.Records());
    }

    [Fact]
    public void ReloadFromLines_AppendedIncrements_SumIntoOneBucket()
    {
        var index = new RouteHistoryIndex();
        var writer = new StringWriter();
        foreach (RouteObservation flight in new[] { Flight(Today, "UAL1"), Flight(Today, "UAL2") })
        {
            RouteHistoryRecord? added = index.Add(flight);
            Assert.NotNull(added);
            writer.WriteLine(RouteHistoryIndex.ToLine(added));
        }

        var reloaded = new RouteHistoryIndex();
        reloaded.ReadLines(new StringReader(writer.ToString()));

        Assert.Equal(2, Assert.Single(reloaded.Records()).Count);
    }

    [Fact]
    public void WriteLines_EndsEveryLineInLineFeed_Only()
    {
        string saved = Saved(IndexOf(Flight(Today, "UAL1"), Flight(Today, "SWA2", "B737", "OFFSH9 SNS LYNSY ANJLL4")));

        Assert.DoesNotContain('\r', saved);
        Assert.EndsWith("\n", saved, StringComparison.Ordinal);
        Assert.Equal(2, saved.Count(c => c == '\n'));
    }

    [Fact]
    public void ReloadFromLines_SkipsABadMiddleLine_AndLoadsTheRest()
    {
        RouteHistoryIndex index = IndexOf(Flight(Today, "UAL1"), Flight(Today, "SWA2", "B737", "OFFSH9 SNS LYNSY ANJLL4"));
        string[] lines = Saved(index).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        RouteHistoryIndex reloaded = Reloaded(lines[0] + "\n{not json\n" + lines[1] + "\n");

        Assert.Equal(index.Records(), reloaded.Records());
    }

    [Fact]
    public void ReloadFromLines_RejectsALineWithCountZero()
    {
        string line = RouteHistoryIndex.ToLine(new RouteHistoryRecord(Today, "KSFO", "KLAX", SstikIrnmn, "B738", "UAL", 1));

        RouteHistoryIndex reloaded = Reloaded(line.Replace("\"count\":1", "\"count\":0", StringComparison.Ordinal) + "\n");

        Assert.Empty(reloaded.Records());
    }

    [Fact]
    public void ReloadFromLines_SkipsALineWhosePrefixIsAFullCallsign_AndMergeRefusesOne()
    {
        var fullCallsign = new RouteHistoryRecord(Today, "KSFO", "KLAX", SstikIrnmn, "B738", "UAL1234", 1);
        var good = new RouteHistoryRecord(Today, "KSFO", "KLAX", SstikIrnmn, "B738", "UAL", 1);

        RouteHistoryIndex reloaded = Reloaded(RouteHistoryIndex.ToLine(fullCallsign) + "\n" + RouteHistoryIndex.ToLine(good) + "\n");

        Assert.Equal([good], reloaded.Records());
        Assert.Throws<ArgumentException>(() => new RouteHistoryIndex().Merge(fullCallsign));
    }

    [Fact]
    public void Add_RefusesALocalTime()
    {
        var local = new RouteObservation(new DateTime(2026, 11, 21, 7, 30, 0, DateTimeKind.Local), "UAL1", "B738", "KSFO", "KLAX", SstikIrnmn);

        Assert.Throws<ArgumentException>(() => new RouteHistoryIndex().Add(local));
    }

    [Fact]
    public void Observed_OnATie_TakesThePrefixThenTheTypeInOrdinalOrder()
    {
        RouteHistoryIndex index = IndexOf(
            Flight(Today, "UAL1", "B738", SstikIrnmn),
            Flight(Today, "SWA1", "B737", SstikIrnmn),
            Flight(Today, "SWA2", "B38M", SstikIrnmn)
        );

        Assert.Equal(new ObservedRouteSample("SWA", "B38M"), Assert.Single(index.Query(SfoLax)).Observed);
    }

    [Fact]
    public void EveryDayOlderThanTheWindow_IncludingTheNewest_IsDroppedOldestFirst()
    {
        RouteHistoryIndex index = IndexOf(Flight(Today.AddDays(-35), "UAL1"), Flight(Today.AddDays(-40), "UAL2"), Flight(Today.AddDays(-31), "UAL3"));

        IReadOnlyList<DateOnly> dropped = index.Trim(Today, maxAgeDays: 30, maxBytes: long.MaxValue);

        Assert.Equal([Today.AddDays(-40), Today.AddDays(-35), Today.AddDays(-31)], dropped);
        Assert.Empty(index.Records());
    }

    [Fact]
    public void AgeAndBudgetTogether_DropTheOldDayThenTheOldestInWindow()
    {
        RouteHistoryIndex index = IndexOf(
            Flight(Today.AddDays(-31), "UAL1"),
            Flight(Today.AddDays(-2), "UAL2"),
            Flight(Today.AddDays(-1), "UAL3"),
            Flight(Today, "UAL4")
        );
        long twoDays = index.Records().Where(r => r.Day >= Today.AddDays(-1)).Sum(r => RouteHistoryIndex.ToLine(r).Length + 1L);

        IReadOnlyList<DateOnly> dropped = index.Trim(Today, maxAgeDays: 30, maxBytes: twoDays);

        Assert.Equal([Today.AddDays(-31), Today.AddDays(-2)], dropped);
        Assert.Equal([Today.AddDays(-1), Today], index.Records().Select(r => r.Day));
    }
}
