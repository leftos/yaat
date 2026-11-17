using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// What a middle click on the Scope does: the row is picked by the clicked block's resolved kind, and on the map by the entry
/// layout. ERAM fills the track (or the point) into a non-empty entry and runs it; with an empty preview ERAM Field A runs
/// <c>QF</c>, and an HSF or VCI cell, or a field with an action, does what a plain left click on it does; STARS and EuroScope
/// toggle the highlight whatever the preview holds; the armed Rings tool takes nothing.
/// </summary>
public class ScopeMiddleClickTests
{
    private static AircraftModel Track(string? ownerSectorCode) => new() { Callsign = "UAL238", OwnerSectorCode = ownerSectorCode };

    private static ScopeMiddleHit On(ScopeFieldAction action) => new(OnEramFieldA: false, action);

    private static readonly ScopeMiddleHit Block = On(ScopeFieldAction.None);

    private static readonly ScopeMiddleHit FieldA = new(OnEramFieldA: true, ScopeFieldAction.None);

    [Fact]
    public void EramTrack_WithText_FillsAndExecutes() =>
        Assert.Equal(
            ScopeMiddleClickAction.FillTrackAndExecute,
            ScopeMiddleClick.OnTrack(ScopeTemplates.EramId, Track(null), Block, hasText: true, ringsArmed: false)
        );

    [Theory]
    [InlineData(ScopeFieldAction.ToggleHsf)]
    [InlineData(ScopeFieldAction.ToggleVci)]
    [InlineData(ScopeFieldAction.OpenEramAltitudeMenu)]
    [InlineData(ScopeFieldAction.OpenSpeedMenu)]
    public void EramTrack_WithText_OnACellOrAField_FillsAndExecutes(ScopeFieldAction action) =>
        Assert.Equal(
            ScopeMiddleClickAction.FillTrackAndExecute,
            ScopeMiddleClick.OnTrack(ScopeTemplates.EramId, Track(null), On(action), hasText: true, ringsArmed: false)
        );

    [Fact]
    public void EramTrack_Empty_OnAFieldWithNoActionOrOutsideAnyField_DoesNothing() =>
        Assert.Equal(
            ScopeMiddleClickAction.None,
            ScopeMiddleClick.OnTrack(ScopeTemplates.EramId, Track(null), Block, hasText: false, ringsArmed: false)
        );

    [Theory]
    [InlineData(ScopeFieldAction.ToggleHsf)]
    [InlineData(ScopeFieldAction.ToggleVci)]
    public void EramTrack_Empty_OnTheHsfOrVciCell_ClicksItAsAPlainLeftClick(ScopeFieldAction action) =>
        Assert.Equal(
            ScopeMiddleClickAction.ClickField,
            ScopeMiddleClick.OnTrack(ScopeTemplates.EramId, Track(null), On(action), hasText: false, ringsArmed: false)
        );

    [Theory]
    [InlineData(ScopeFieldAction.OpenEramAltitudeMenu)]
    [InlineData(ScopeFieldAction.OpenSpeedMenu)]
    [InlineData(ScopeFieldAction.OpenSquawkMenu)]
    [InlineData(ScopeFieldAction.OpenHandoffMenu)]
    public void EramTrack_Empty_OnAFieldWithAMenu_OpensItAsAPlainLeftClick(ScopeFieldAction action) =>
        Assert.Equal(
            ScopeMiddleClickAction.ClickField,
            ScopeMiddleClick.OnTrack(ScopeTemplates.EramId, Track(null), On(action), hasText: false, ringsArmed: false)
        );

    [Fact]
    public void EramFieldA_Empty_RunsQuickFlight() =>
        Assert.Equal(
            ScopeMiddleClickAction.RunQuickFlight,
            ScopeMiddleClick.OnTrack(ScopeTemplates.EramId, Track(null), FieldA, hasText: false, ringsArmed: false)
        );

    [Fact]
    public void EramFieldA_WithText_FillsAndExecutes() =>
        Assert.Equal(
            ScopeMiddleClickAction.FillTrackAndExecute,
            ScopeMiddleClick.OnTrack(ScopeTemplates.EramId, Track(null), FieldA, hasText: true, ringsArmed: false)
        );

    [Fact]
    public void MixedTrack_ResolvingEram_ActsAsEram() =>
        Assert.Equal(
            ScopeMiddleClickAction.FillTrackAndExecute,
            ScopeMiddleClick.OnTrack(ScopeTemplates.MixedId, Track("C45"), Block, hasText: true, ringsArmed: false)
        );

    [Fact]
    public void MixedTrack_ResolvingEram_Empty_OnAFieldWithAMenu_OpensIt() =>
        Assert.Equal(
            ScopeMiddleClickAction.ClickField,
            ScopeMiddleClick.OnTrack(
                ScopeTemplates.MixedId,
                Track("C45"),
                On(ScopeFieldAction.OpenEramAltitudeMenu),
                hasText: false,
                ringsArmed: false
            )
        );

    [Fact]
    public void MixedTrack_ResolvingStars_TogglesHighlight() =>
        Assert.Equal(
            ScopeMiddleClickAction.ToggleHighlight,
            ScopeMiddleClick.OnTrack(ScopeTemplates.MixedId, Track("1A"), Block, hasText: true, ringsArmed: false)
        );

    [Fact]
    public void MixedTrack_ResolvingStars_Empty_OnAFieldWithAMenu_TogglesHighlight() =>
        Assert.Equal(
            ScopeMiddleClickAction.ToggleHighlight,
            ScopeMiddleClick.OnTrack(ScopeTemplates.MixedId, Track("1A"), On(ScopeFieldAction.OpenAltitudeMenu), hasText: false, ringsArmed: false)
        );

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StarsTrack_EmptyOrWithText_TogglesHighlight(bool hasText) =>
        Assert.Equal(
            ScopeMiddleClickAction.ToggleHighlight,
            ScopeMiddleClick.OnTrack(ScopeTemplates.StarsId, Track(null), On(ScopeFieldAction.OpenAltitudeMenu), hasText, ringsArmed: false)
        );

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EuroScopeTrack_TogglesHighlight(bool hasText) =>
        Assert.Equal(
            ScopeMiddleClickAction.ToggleHighlight,
            ScopeMiddleClick.OnTrack(ScopeTemplates.EuroScopeId, Track(null), On(ScopeFieldAction.OpenAltitudeMenu), hasText, ringsArmed: false)
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
    [InlineData(false, false, ScopeFieldAction.None)]
    [InlineData(true, true, ScopeFieldAction.None)]
    [InlineData(false, false, ScopeFieldAction.ToggleHsf)]
    [InlineData(false, false, ScopeFieldAction.OpenEramAltitudeMenu)]
    public void RingsArmed_Track_DoesNothing(bool onEramFieldA, bool hasText, ScopeFieldAction action)
    {
        var hit = new ScopeMiddleHit(onEramFieldA, action);
        Assert.Equal(ScopeMiddleClickAction.None, ScopeMiddleClick.OnTrack(ScopeTemplates.EramId, Track(null), hit, hasText, ringsArmed: true));
        Assert.Equal(ScopeMiddleClickAction.None, ScopeMiddleClick.OnTrack(ScopeTemplates.StarsId, Track(null), hit, hasText, ringsArmed: true));
    }

    [Fact]
    public void RingsArmed_Map_DoesNothing() =>
        Assert.Equal(ScopeMiddleClickAction.None, ScopeMiddleClick.OnMap(eramLayout: true, hasText: true, ringsArmed: true));
}
