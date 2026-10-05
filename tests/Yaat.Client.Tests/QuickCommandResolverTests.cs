using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Commands;
using Yaat.Sim.Situation;

namespace Yaat.Client.Tests;

/// <summary>
/// Pins <see cref="QuickCommandResolver"/>: the flight-rules filter (entry override else catalog default), the
/// applicability filter, the strip split (the first ten glyph-bearing entries, in order) and the empty result for an
/// unclassified aircraft.
/// </summary>
public class QuickCommandResolverTests
{
    /// <summary>Eleven glyph-bearing entries that apply to any airborne, controllable aircraft, in list order.</summary>
    private static readonly string[] GlyphIds =
    [
        MenuIds.TrackTrack,
        MenuIds.TrackInitiateHandoff,
        MenuIds.SquawkCode,
        MenuIds.SimControlWarp,
        MenuIds.SimControlDelete,
        MenuIds.HeadingFly,
        MenuIds.AltitudeMaintain,
        MenuIds.SpeedAssign,
        MenuIds.NavigationDirectTo,
        MenuIds.ApproachCleared,
        MenuIds.HoldPattern,
    ];

    private static AircraftModel Airborne(string rules, AircraftSituation situation) =>
        new()
        {
            Callsign = "TST123",
            CurrentPhase = "",
            IsOnGround = false,
            FlightRules = rules,
            Situation = situation,
        };

    private static MenuContext Context() => new(new MenuClick("TST123", null, null, []), new MenuSession("XX", false, VfrCommandsForIfr.None));

    private static List<QuickCommandEntry> Entries(params string[] ids) => [.. ids.Select(id => new QuickCommandEntry(id, null))];

    private static List<string> StripIds(QuickCommandResolution resolution) => [.. resolution.Strip.Select(item => item.Entry.Id)];

    private static List<string> TextIds(QuickCommandResolution resolution) => [.. resolution.Text.Select(entry => entry.Id)];

    [Theory]
    [InlineData("IFR", true)]
    [InlineData("VFR", false)]
    public void IfrOnlyEntry_KeptForIfr_DroppedForVfr(string rules, bool kept)
    {
        List<QuickCommandEntry> entries = [new(MenuIds.TrackTrack, MenuFlightRules.IfrOnly), new(MenuIds.HeadingPresent, null)];

        QuickCommandResolution resolution = QuickCommandResolver.Resolve(
            entries,
            Airborne(rules, AircraftSituation.IfrEnroute),
            Context(),
            _ => true
        );

        Assert.Equal(kept, StripIds(resolution).Contains(MenuIds.TrackTrack));
        Assert.Equal([MenuIds.HeadingPresent], TextIds(resolution));
    }

    [Fact]
    public void CatalogDefaultFlightRules_ApplyWhenTheEntryHasNoOverride()
    {
        // hold.pattern's catalog default is IFR only.
        QuickCommandResolution resolution = QuickCommandResolver.Resolve(
            Entries(MenuIds.HoldPattern),
            Airborne("VFR", AircraftSituation.IfrEnroute),
            Context(),
            _ => true
        );

        Assert.Empty(resolution.Strip);
        Assert.Empty(resolution.Text);
    }

    [Fact]
    public void InapplicableEntry_IsDropped()
    {
        // Push back applies only at parking or after a pushback, never to an airborne aircraft.
        QuickCommandResolution resolution = QuickCommandResolver.Resolve(
            Entries(MenuIds.GroundPushback, MenuIds.TrackTrack),
            Airborne("IFR", AircraftSituation.IfrEnroute),
            Context(),
            _ => true
        );

        Assert.Equal([MenuIds.TrackTrack], StripIds(resolution));
        Assert.Empty(resolution.Text);
    }

    [Fact]
    public void Strip_TakesTheFirstTenGlyphEntriesInOrder_AndTheEleventhGoesToText()
    {
        List<QuickCommandEntry> entries = Entries([MenuIds.HeadingPresent, .. GlyphIds]);

        QuickCommandResolution resolution = QuickCommandResolver.Resolve(
            entries,
            Airborne("IFR", AircraftSituation.IfrEnroute),
            Context(),
            _ => true
        );

        Assert.Equal(GlyphIds[..QuickCommandGlyphs.StripCapacity], StripIds(resolution));
        Assert.Equal([MenuIds.HeadingPresent, GlyphIds[^1]], TextIds(resolution));
    }

    [Fact]
    public void ListWithNoGlyphEntries_HasNoStrip()
    {
        QuickCommandResolution resolution = QuickCommandResolver.Resolve(
            Entries(MenuIds.HeadingPresent, MenuIds.SpeedNormal),
            Airborne("IFR", AircraftSituation.IfrEnroute),
            Context(),
            _ => true
        );

        Assert.Empty(resolution.Strip);
        Assert.Equal([MenuIds.HeadingPresent, MenuIds.SpeedNormal], TextIds(resolution));
    }

    [Fact]
    public void UnknownSituation_ResolvesEmpty()
    {
        QuickCommandResolution resolution = QuickCommandResolver.Resolve(Airborne("IFR", AircraftSituation.Unknown), Context(), _ => true);

        Assert.Empty(resolution.Strip);
        Assert.Empty(resolution.Text);
    }
}
