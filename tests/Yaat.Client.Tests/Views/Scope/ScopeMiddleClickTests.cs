using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// What a middle click on the Scope does: the row is picked by the clicked block's resolved kind, and on the map by the entry
/// layout. ERAM fills the track (or the point) into a non-empty entry and runs it; ERAM Field A with an empty preview runs
/// <c>QF</c>; STARS and EuroScope toggle the highlight whatever the preview holds; the armed Rings tool takes nothing.
/// </summary>
public class ScopeMiddleClickTests
{
    private static AircraftModel Track(string? ownerSectorCode) => new() { Callsign = "UAL238", OwnerSectorCode = ownerSectorCode };

    [Fact]
    public void EramTrack_WithText_FillsAndExecutes() =>
        Assert.Equal(
            ScopeMiddleClickAction.FillTrackAndExecute,
            ScopeMiddleClick.OnTrack(ScopeTemplates.EramId, Track(null), onEramFieldA: false, hasText: true, ringsArmed: false)
        );

    [Fact]
    public void EramTrack_Empty_DoesNothing() =>
        Assert.Equal(
            ScopeMiddleClickAction.None,
            ScopeMiddleClick.OnTrack(ScopeTemplates.EramId, Track(null), onEramFieldA: false, hasText: false, ringsArmed: false)
        );

    [Fact]
    public void EramFieldA_Empty_RunsQuickFlight() =>
        Assert.Equal(
            ScopeMiddleClickAction.RunQuickFlight,
            ScopeMiddleClick.OnTrack(ScopeTemplates.EramId, Track(null), onEramFieldA: true, hasText: false, ringsArmed: false)
        );

    [Fact]
    public void EramFieldA_WithText_FillsAndExecutes() =>
        Assert.Equal(
            ScopeMiddleClickAction.FillTrackAndExecute,
            ScopeMiddleClick.OnTrack(ScopeTemplates.EramId, Track(null), onEramFieldA: true, hasText: true, ringsArmed: false)
        );

    [Fact]
    public void MixedTrack_ResolvingEram_ActsAsEram() =>
        Assert.Equal(
            ScopeMiddleClickAction.FillTrackAndExecute,
            ScopeMiddleClick.OnTrack(ScopeTemplates.MixedId, Track("C45"), onEramFieldA: false, hasText: true, ringsArmed: false)
        );

    [Fact]
    public void MixedTrack_ResolvingStars_TogglesHighlight() =>
        Assert.Equal(
            ScopeMiddleClickAction.ToggleHighlight,
            ScopeMiddleClick.OnTrack(ScopeTemplates.MixedId, Track("1A"), onEramFieldA: false, hasText: true, ringsArmed: false)
        );

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StarsTrack_EmptyOrWithText_TogglesHighlight(bool hasText) =>
        Assert.Equal(
            ScopeMiddleClickAction.ToggleHighlight,
            ScopeMiddleClick.OnTrack(ScopeTemplates.StarsId, Track(null), onEramFieldA: false, hasText, ringsArmed: false)
        );

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EuroScopeTrack_TogglesHighlight(bool hasText) =>
        Assert.Equal(
            ScopeMiddleClickAction.ToggleHighlight,
            ScopeMiddleClick.OnTrack(ScopeTemplates.EuroScopeId, Track(null), onEramFieldA: false, hasText, ringsArmed: false)
        );

    [Fact]
    public void Map_EramLayoutWithText_FillsPointAndExecutes() =>
        Assert.Equal(ScopeMiddleClickAction.FillPointAndExecute, ScopeMiddleClick.OnMap(eramLayout: true, hasText: true, ringsArmed: false));

    [Fact]
    public void Map_EramLayoutEmpty_DoesNothing() =>
        Assert.Equal(ScopeMiddleClickAction.None, ScopeMiddleClick.OnMap(eramLayout: true, hasText: false, ringsArmed: false));

    [Fact]
    public void Map_StarsLayoutWithText_DoesNothing() =>
        Assert.Equal(ScopeMiddleClickAction.None, ScopeMiddleClick.OnMap(eramLayout: false, hasText: true, ringsArmed: false));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RingsArmed_Track_DoesNothing(bool onEramFieldA, bool hasText)
    {
        Assert.Equal(
            ScopeMiddleClickAction.None,
            ScopeMiddleClick.OnTrack(ScopeTemplates.EramId, Track(null), onEramFieldA, hasText, ringsArmed: true)
        );
        Assert.Equal(
            ScopeMiddleClickAction.None,
            ScopeMiddleClick.OnTrack(ScopeTemplates.StarsId, Track(null), onEramFieldA, hasText, ringsArmed: true)
        );
    }

    [Fact]
    public void RingsArmed_Map_DoesNothing() =>
        Assert.Equal(ScopeMiddleClickAction.None, ScopeMiddleClick.OnMap(eramLayout: true, hasText: true, ringsArmed: true));
}
