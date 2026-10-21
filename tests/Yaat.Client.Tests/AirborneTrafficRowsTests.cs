using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;

namespace Yaat.Client.Tests;

/// <summary>
/// The words an airborne traffic row prints after the clock, miles and altitude: the target's direction of flight from its
/// true track, and the pattern leg it flies from the sim's pattern position and its assigned runway.
/// </summary>
public class AirborneTrafficRowsTests
{
    [Theory]
    [InlineData(0, "northbound")]
    [InlineData(22.4, "northbound")]
    [InlineData(22.5, "northeastbound")]
    [InlineData(90, "eastbound")]
    [InlineData(135, "southeastbound")]
    [InlineData(180, "southbound")]
    [InlineData(225, "southwestbound")]
    [InlineData(270, "westbound")]
    [InlineData(315, "northwestbound")]
    [InlineData(337.5, "northbound")]
    [InlineData(359, "northbound")]
    [InlineData(-45, "northwestbound")]
    [InlineData(450, "eastbound")]
    public void DirectionWord_FromTrueTrack(double trueTrack, string word) => Assert.Equal(word, AirborneTrafficRows.DirectionWord(trueTrack));

    [Theory]
    [InlineData(PatternEntryLeg.Downwind, PatternDirection.Left, "28L", "left downwind 28L")]
    [InlineData(PatternEntryLeg.Base, PatternDirection.Right, "28R", "right base 28R")]
    [InlineData(PatternEntryLeg.Crosswind, PatternDirection.Left, "30", "left crosswind 30")]
    [InlineData(PatternEntryLeg.Upwind, PatternDirection.Right, "", "right upwind")]
    [InlineData(PatternEntryLeg.Final, null, "30", "final 30")]
    [InlineData(PatternEntryLeg.Final, PatternDirection.Left, "30", "final 30")]
    public void LegWord_FromPatternPosition(PatternEntryLeg leg, PatternDirection? side, string runway, string word) =>
        Assert.Equal(word, AirborneTrafficRows.LegWord(new PatternPosition(leg, side, 1.5), runway));

    [Fact]
    public void LegWord_NoPatternPosition_IsNull() => Assert.Null(AirborneTrafficRows.LegWord(null, "28L"));

    /// <summary>The lead reported in sight goes on top; the cap stays eight, so a lead beyond the eight nearest takes the eighth place.</summary>
    [Fact]
    public void Listed_LeadBeyondTheEightNearest_ListsLeadAndSevenNearest()
    {
        List<MenuAirborneTrafficRow> traffic = [.. Enumerable.Range(1, 10).Select(i => Row($"AC{i:00}", i))];

        List<MenuAirborneTrafficRow> listed = AirborneTrafficRows.Listed(traffic, "", "AC10");

        Assert.Equal(["AC10", "AC01", "AC02", "AC03", "AC04", "AC05", "AC06", "AC07"], listed.Select(row => row.Callsign));
        Assert.Equal(
            ["AC03", "AC01", "AC02", "AC04", "AC05", "AC06", "AC07", "AC08"],
            AirborneTrafficRows.Listed(traffic, "", "AC03").Select(row => row.Callsign)
        );
    }

    private static MenuAirborneTrafficRow Row(string callsign, double nm) => new(new MenuTrafficRow(callsign, "C172", nm, 12, 0), "eastbound", null);
}
