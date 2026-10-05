using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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

    private static AircraftModel OnGround(string rules) =>
        new()
        {
            Callsign = "TST123",
            CurrentPhase = "",
            IsOnGround = true,
            FlightRules = rules,
            Situation = AircraftSituation.Taxiing,
        };

    private static MenuContext Context() => Context(QuickCommandDefaults.For);

    private static MenuContext Context(Func<AircraftSituation, IReadOnlyList<QuickCommandEntry>> lists) =>
        new(new MenuClick("TST123", null, null, []), new MenuSession("XX", false, VfrCommandsForIfr.None, lists));

    private static List<QuickCommandEntry> Entries(params string[] ids) => [.. ids.Select(id => new CatalogQuickCommandEntry(id, null))];

    /// <summary>The command the resolved entry's item sends; the send path reads the host only when the item is clicked.</summary>
    private static string? SentCommand(MenuCatalogEntry entry, IMenuAircraft aircraft, MenuContext context)
    {
        MenuItem item = Assert.IsType<MenuItem>(entry.Build(aircraft, context, null!));
        return MenuCommandText.GetCommand(item);
    }

    private static List<string> StripIds(QuickCommandResolution resolution) => [.. resolution.Strip.Select(item => item.Entry.Id)];

    private static List<string> TextIds(QuickCommandResolution resolution) => [.. resolution.Text.Select(entry => entry.Id)];

    [Theory]
    [InlineData("IFR", true)]
    [InlineData("VFR", false)]
    public void IfrOnlyEntry_KeptForIfr_DroppedForVfr(string rules, bool kept)
    {
        List<QuickCommandEntry> entries =
        [
            new CatalogQuickCommandEntry(MenuIds.TrackTrack, MenuFlightRules.IfrOnly),
            new CatalogQuickCommandEntry(MenuIds.HeadingPresent, null),
        ];

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

    [Fact]
    public void StoredList_ReplacesTheDefault_ForItsSituationOnly()
    {
        MenuContext context = Context(situation =>
            situation == AircraftSituation.IfrEnroute ? Entries(MenuIds.HeadingPresent, MenuIds.SpeedNormal) : QuickCommandDefaults.For(situation)
        );
        AircraftModel arrival = Airborne("IFR", AircraftSituation.IfrArrival);

        QuickCommandResolution enroute = QuickCommandResolver.Resolve(Airborne("IFR", AircraftSituation.IfrEnroute), context, _ => true);
        QuickCommandResolution arrivalStored = QuickCommandResolver.Resolve(arrival, context, _ => true);
        QuickCommandResolution arrivalDefault = QuickCommandResolver.Resolve(
            QuickCommandDefaults.For(AircraftSituation.IfrArrival),
            arrival,
            Context(),
            _ => true
        );

        Assert.Empty(enroute.Strip);
        Assert.Equal([MenuIds.HeadingPresent, MenuIds.SpeedNormal], TextIds(enroute));
        Assert.Equal(StripIds(arrivalDefault), StripIds(arrivalStored));
        Assert.Equal(TextIds(arrivalDefault), TextIds(arrivalStored));
        Assert.NotEmpty(StripIds(arrivalStored));
    }

    [AvaloniaFact]
    public void CustomEntry_ResolvesToItsLabel_AndSendsItsCommandTextTrimmed()
    {
        AircraftModel aircraft = Airborne("IFR", AircraftSituation.IfrEnroute);
        List<QuickCommandEntry> entries = [new CustomQuickCommandEntry("Say altitude", "  SA  ", "TAXI A", MenuFlightRules.Both)];

        QuickCommandResolution resolution = QuickCommandResolver.Resolve(entries, aircraft, Context(), _ => true);

        MenuCatalogEntry entry = Assert.Single(resolution.Text);
        Assert.Equal("Say altitude", entry.Label);
        Assert.Equal("SA", SentCommand(entry, aircraft, Context()));
    }

    [AvaloniaFact]
    public void CustomEntry_OnTheGround_SendsItsGroundText()
    {
        AircraftModel aircraft = OnGround("IFR");
        List<QuickCommandEntry> entries = [new CustomQuickCommandEntry("Go", "FH 270", " TAXI A B ", MenuFlightRules.Both)];

        QuickCommandResolution resolution = QuickCommandResolver.Resolve(entries, aircraft, Context(), _ => true);

        Assert.Equal("TAXI A B", SentCommand(Assert.Single(resolution.Text), aircraft, Context()));
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("   ")]
    public void CustomEntry_OnTheGround_WithoutGroundText_SendsItsCommandText(string? groundText)
    {
        AircraftModel aircraft = OnGround("IFR");
        List<QuickCommandEntry> entries = [new CustomQuickCommandEntry("Hold", "HOLD", groundText, MenuFlightRules.Both)];

        QuickCommandResolution resolution = QuickCommandResolver.Resolve(entries, aircraft, Context(), _ => true);

        Assert.Equal("HOLD", SentCommand(Assert.Single(resolution.Text), aircraft, Context()));
    }

    [Theory]
    [InlineData("IFR", MenuFlightRules.IfrOnly, true)]
    [InlineData("VFR", MenuFlightRules.IfrOnly, false)]
    [InlineData("IFR", MenuFlightRules.VfrOnly, false)]
    [InlineData("VFR", MenuFlightRules.VfrOnly, true)]
    [InlineData("", MenuFlightRules.Both, true)]
    [InlineData("", MenuFlightRules.VfrOnly, false)]
    public void CustomEntry_IsFilteredByItsFlightRules(string rules, MenuFlightRules entryRules, bool kept)
    {
        List<QuickCommandEntry> entries = [new CustomQuickCommandEntry("Custom", "SA", null, entryRules)];

        QuickCommandResolution resolution = QuickCommandResolver.Resolve(
            entries,
            Airborne(rules, AircraftSituation.IfrEnroute),
            Context(),
            _ => true
        );

        Assert.Equal(kept, TextIds(resolution).Contains(CustomQuickCommandEntry.MenuId));
    }

    [Theory]
    [InlineData(VfrCommandsForIfr.All, true)]
    [InlineData(VfrCommandsForIfr.EnterFinalOnly, false)]
    [InlineData(VfrCommandsForIfr.None, false)]
    public void VfrOnlyCustomEntry_ReachesAnIfrAircraft_OnlyUnderAll(VfrCommandsForIfr mode, bool kept)
    {
        List<QuickCommandEntry> entries = [new CustomQuickCommandEntry("Left traffic", "MLT", null, MenuFlightRules.VfrOnly)];
        var context = new MenuContext(new MenuClick("TST123", null, null, []), new MenuSession("XX", false, mode, QuickCommandDefaults.For));

        QuickCommandResolution resolution = QuickCommandResolver.Resolve(entries, Airborne("IFR", AircraftSituation.IfrEnroute), context, _ => true);

        Assert.Equal(kept, TextIds(resolution).Contains(CustomQuickCommandEntry.MenuId));
    }

    [Fact]
    public void CustomEntry_NeverEntersTheStrip_EvenWithStripRoomLeft()
    {
        List<QuickCommandEntry> entries =
        [
            new CustomQuickCommandEntry("First", "SA", null, MenuFlightRules.Both),
            new CatalogQuickCommandEntry(MenuIds.TrackTrack, null),
            new CustomQuickCommandEntry("Second", "SS", null, MenuFlightRules.Both),
        ];

        QuickCommandResolution resolution = QuickCommandResolver.Resolve(
            entries,
            Airborne("IFR", AircraftSituation.IfrEnroute),
            Context(),
            _ => true
        );

        Assert.Equal([MenuIds.TrackTrack], StripIds(resolution));
        Assert.Equal(["First", "Second"], resolution.Text.Select(entry => entry.Label));
        Assert.All(resolution.Text, entry => Assert.Equal(CustomQuickCommandEntry.MenuId, entry.Id));
    }
}
