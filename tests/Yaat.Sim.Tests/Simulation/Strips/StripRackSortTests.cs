using Xunit;
using Yaat.Sim.Simulation.Strips;

namespace Yaat.Sim.Tests.Simulation.Strips;

/// <summary>
/// The pure rack sort behind an auto-sorted rack. Racks are stored bottom-up (index 0 is the visual bottom), so each
/// test states the rack top-down, as a controller reads it, and the helpers reverse it into and out of storage order.
/// </summary>
public class StripRackSortTests
{
    private const int Full = (int)StripItemType.DepartureStrip;
    private const int Half = (int)StripItemType.HalfStripLeft;
    private const int Blank = (int)StripItemType.BlankStrip;
    private const int Separator = (int)StripItemType.WhiteSeparator;

    private static StripRackSortEntry F(string callsign) => new($"STRIP_{callsign}", Full, callsign);

    private static StripRackSortEntry H(string id, string line1) => new(id, Half, line1);

    private static StripRackSortEntry B(string id) => new(id, Blank, "");

    private static StripRackSortEntry S(string id) => new(id, Separator, id);

    /// <summary>Sorts a rack given top-down and returns it top-down.</summary>
    private static List<string> SortTopDown(params StripRackSortEntry[] topDown)
    {
        List<StripRackSortEntry> stored = [.. topDown.Reverse()];
        List<string> sorted = StripRackSort.Sort(stored);
        sorted.Reverse();
        return sorted;
    }

    [Fact]
    public void TailRegistration_SortsAboveAirlineCallsign()
    {
        List<string> sorted = SortTopDown(F("AAL2210"), F("N1234"));

        Assert.Equal(["STRIP_N1234", "STRIP_AAL2210"], sorted);
    }

    [Fact]
    public void Sort_ReturnsStorageOrder_WithTheTopOfTheRackLast()
    {
        List<string> stored = StripRackSort.Sort([F("N1234"), F("AAL2210")]);

        Assert.Equal(["STRIP_AAL2210", "STRIP_N1234"], stored);
    }

    [Theory]
    [InlineData("AAL2210", true)]
    [InlineData(" aal2210 ", true)]
    [InlineData("SKW5", true)]
    [InlineData("AA2210", false)]
    [InlineData("AALX210", false)]
    [InlineData("AAL", false)]
    [InlineData("N1234", false)]
    [InlineData("C-GABC", false)]
    [InlineData("", false)]
    public void AirlineShaped_IsExactlyThreeLettersThenADigit(string callsign, bool expected) =>
        Assert.Equal(expected, StripRackSort.IsAirlineShaped(callsign));

    [Fact]
    public void ThreeLettersThenADigit_SortsAsAnAirline_AndAnythingElseAsATail()
    {
        List<string> sorted = SortTopDown(F("AAL2210"), F("AALX210"), F("AA2210"));

        Assert.Equal(["STRIP_AA2210", "STRIP_AALX210", "STRIP_AAL2210"], sorted);
    }

    [Fact]
    public void HalfStrip_SortsByItsBoxOne()
    {
        List<string> sorted = SortTopDown(F("UAL512"), H("HSTRIP_a", "DAL88"), F("ASA301"));

        Assert.Equal(["STRIP_ASA301", "HSTRIP_a", "STRIP_UAL512"], sorted);
    }

    [Fact]
    public void Comparison_IgnoresCase()
    {
        List<string> sorted = SortTopDown(H("HSTRIP_b", "bbb1"), H("HSTRIP_a", "AAA1"));

        Assert.Equal(["HSTRIP_a", "HSTRIP_b"], sorted);
    }

    [Fact]
    public void BlankStripsAndEmptyBoxOne_SitAtTheSegmentBottom_InTheirExistingOrder()
    {
        List<string> sorted = SortTopDown(H("HSTRIP_empty", "  "), F("UAL512"), B("BLANK_1"), F("N1234"));

        Assert.Equal(["STRIP_N1234", "STRIP_UAL512", "HSTRIP_empty", "BLANK_1"], sorted);
    }

    [Fact]
    public void EqualKeys_BreakOnTheStripId()
    {
        var copy = new StripRackSortEntry("STRIP_AAL1_b", Full, "AAL1");
        var original = new StripRackSortEntry("STRIP_AAL1_a", Full, "AAL1");

        List<string> sorted = SortTopDown(copy, original);

        Assert.Equal(["STRIP_AAL1_a", "STRIP_AAL1_b"], sorted);
    }

    /// <summary>The mock's lane A (separator-lanes.html): two separators split the rack into three runs, each sorted on its own.</summary>
    [Fact]
    public void TwoSeparators_SplitTheRackIntoThreeIndependentlySortedSegments()
    {
        List<string> sorted = SortTopDown(
            F("UAL512"),
            S("SEP_28L"),
            F("SKW5432"),
            F("N172SP"),
            B("BLANK_1"),
            F("AAL2210"),
            S("SEP_1R"),
            H("HSTRIP_dal", "DAL88"),
            F("N1234"),
            F("ASA301")
        );

        Assert.Equal(
            [
                "STRIP_UAL512",
                "SEP_28L",
                "STRIP_N172SP",
                "STRIP_AAL2210",
                "STRIP_SKW5432",
                "BLANK_1",
                "SEP_1R",
                "STRIP_N1234",
                "STRIP_ASA301",
                "HSTRIP_dal",
            ],
            sorted
        );
    }

    [Fact]
    public void Separators_KeepTheirSlots()
    {
        List<StripRackSortEntry> stored = [F("ZZZ1"), S("SEP_a"), F("N1"), F("AAL1"), F("B1"), S("SEP_b"), S("SEP_c"), F("N9")];

        List<string> sorted = StripRackSort.Sort(stored);

        Assert.Equal(stored.Count, sorted.Count);
        Assert.Equal(1, sorted.IndexOf("SEP_a"));
        Assert.Equal(5, sorted.IndexOf("SEP_b"));
        Assert.Equal(6, sorted.IndexOf("SEP_c"));
    }

    [Fact]
    public void EmptyRack_SortsToEmpty() => Assert.Empty(StripRackSort.Sort([]));
}
