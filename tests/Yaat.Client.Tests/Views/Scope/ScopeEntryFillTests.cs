using Xunit;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The text a click fills into the Scope's preview: a track's callsign goes first in a pilot entry (after a leading
/// <c>PILOT</c>, in place of a known aircraft's callsign) and at the end of any other entry; a point appends its FRD, else its
/// ERAM lat/long.
/// </summary>
public class ScopeEntryFillTests
{
    // Known aircraft, matched case-insensitively as the Scope matches them.
    private static bool Known(string callsign) => callsign.ToUpperInvariant() is "AAL1" or "UAL238";

    private static string FillTrack(string entry, ScopeProfileKind profile) => ScopeEntryFill.Track(entry, "UAL238", profile, Known);

    [Fact]
    public void PilotEntry_TakesTheCallsignFirst() => Assert.Equal("UAL238 FH 270", FillTrack("FH 270", ScopeProfileKind.Eram));

    [Fact]
    public void PilotWordEntry_TakesTheCallsignAfterThePilotWord() =>
        Assert.Equal("PILOT UAL238 FH 270", FillTrack("PILOT FH 270", ScopeProfileKind.Eram));

    [Fact]
    public void PilotEntry_ForAnotherKnownAircraft_ReplacesItsCallsign() =>
        Assert.Equal("UAL238 FH 270", FillTrack("AAL1 FH 270", ScopeProfileKind.Eram));

    [Fact]
    public void OverrideEntry_KeepsTheOverrideInFrontOfTheCallsign() =>
        Assert.Equal("** UAL238 FH 270", FillTrack("** FH 270", ScopeProfileKind.Eram));

    [Fact]
    public void OverrideEntry_ForAnotherKnownAircraft_ReplacesItsCallsign() =>
        Assert.Equal("** UAL238 FH 270", FillTrack("** AAL1 FH 270", ScopeProfileKind.Eram));

    [Fact]
    public void PilotWordOverrideEntry_KeepsBothInFrontOfTheCallsign() =>
        Assert.Equal("PILOT ** UAL238 FH 270", FillTrack("PILOT ** FH 270", ScopeProfileKind.Eram));

    [Fact]
    public void EramDialectEntry_AppendsTheCallsign() => Assert.Equal("QQ 250 UAL238", FillTrack("QQ 250", ScopeProfileKind.Eram));

    [Fact]
    public void EntryEndingInTheCallsign_IsNotFilledAgain() => Assert.Equal("QQ 250 UAL238", FillTrack("QQ 250 UAL238", ScopeProfileKind.Eram));

    [Fact]
    public void StarsEntryWithTrailingSpace_AppendsTheCallsign_WithOneSpace() =>
        Assert.Equal("HND UAL238", FillTrack("HND ", ScopeProfileKind.Stars));

    [Theory]
    [InlineData("PILOT UAL238 CM 80", false)]
    [InlineData("pilot ual238 cm 80", false)]
    [InlineData("UAL238 FH 270", false)]
    [InlineData("** UAL238 FH 270", false)]
    [InlineData("QQ 250 UAL238", false)]
    [InlineData("QQ 250 ual238", false)]
    [InlineData("PILOT AAL1 CM 80", true)]
    [InlineData("** FH 270", true)]
    [InlineData("FH 270", true)]
    [InlineData("QQ 250", true)]
    [InlineData("QQ 250 AAL1", true)]
    public void ChangesTrack_IsFalseOnlyWhenTheEntryAlreadyNamesTheTrack(string entry, bool expected) =>
        Assert.Equal(expected, ScopeEntryFill.ChangesTrack(entry, "UAL238", ScopeProfileKind.Eram, Known));

    [Fact]
    public void Point_WithAnFrd_AppendsTheFrd() => Assert.Equal("QU OAK090010", ScopeEntryFill.Point("QU", new LatLon(37.7, -122.2), "OAK090010"));

    [Fact]
    public void Point_WithoutAnFrd_AppendsTheEramLatLong() =>
        Assert.Equal("QU //3742N/12212W", ScopeEntryFill.Point("QU ", new LatLon(37.7, -122.2), null));
}
