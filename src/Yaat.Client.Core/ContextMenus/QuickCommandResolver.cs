using System.Collections.Frozen;
using Yaat.Sim.Commands;
using Yaat.Sim.Situation;

namespace Yaat.Client.ContextMenus;

/// <summary>A strip entry: the catalog action and the glyph it shows.</summary>
/// <param name="Entry">The catalog action the glyph sends or opens.</param>
/// <param name="Glyph">The glyph drawn for it.</param>
public sealed record QuickCommandStripItem(MenuCatalogEntry Entry, QuickCommandGlyph Glyph);

/// <summary>A resolved quick-command list: the strip entries and the text entries below it, each in list order.</summary>
/// <param name="Strip">The glyph-bearing entries the strip shows, at most <see cref="QuickCommandGlyphs.StripCapacity"/>; empty for no strip.</param>
/// <param name="Text">Every other shown entry, as text.</param>
public sealed record QuickCommandResolution(IReadOnlyList<QuickCommandStripItem> Strip, IReadOnlyList<MenuCatalogEntry> Text)
{
    /// <summary>No quick commands: no strip and no text entries.</summary>
    public static QuickCommandResolution Empty { get; } = new([], []);
}

/// <summary>
/// Resolves an aircraft's quick commands: its situation's list, filtered by flight rules (the entry's override, else the
/// catalog default, against the aircraft's filed rules), by each catalog entry's applicability, and by the quick-list
/// visibility rules that read the server's situation flags, then split into the strip and the text entries
/// (<see cref="QuickCommandGlyphs.Split"/>). The visibility rules apply only here: All Commands keeps every entry
/// whatever the flags say.
/// </summary>
public static class QuickCommandResolver
{
    /// <summary>The quick-list visibility rule each gated catalog action is filtered by beside its applicability.</summary>
    private static readonly FrozenDictionary<string, Func<IMenuAircraft, bool>> Visibility = new Dictionary<string, Func<IMenuAircraft, bool>>(
        StringComparer.Ordinal
    )
    {
        [MenuIds.TowerClearedForTakeoff] = ac =>
            AircraftCommandApplicability.ShowsTakeoffWhileTaxiing(ac)
            && AircraftCommandApplicability.ShowsDepartureClearanceAtHoldShort(ac)
            && AircraftCommandApplicability.ShowsDepartureClearanceWhileHeld(ac),
        [MenuIds.TowerLineUpAndWait] = ac =>
            AircraftCommandApplicability.ShowsDepartureClearanceAtHoldShort(ac) && AircraftCommandApplicability.ShowsDepartureClearanceWhileHeld(ac),
        [MenuIds.GroundGiveWay] = AircraftCommandApplicability.ShowsGiveWay,
        [MenuIds.ProceduresClimbViaSid] = AircraftCommandApplicability.ShowsClimbViaSid,
        [MenuIds.ProceduresDescendViaStar] = AircraftCommandApplicability.ShowsDescendViaStar,
        [MenuIds.TowerClearedToLand] = AircraftCommandApplicability.ShowsClearedToLandOnFinal,
        [MenuIds.ApproachCleared] = ac =>
            AircraftCommandApplicability.ShowsApproachClearanceAfterGoAround(ac)
            && AircraftCommandApplicability.ShowsApproachClearanceUntilCleared(ac),
        [MenuIds.TowerCancelTakeoff] = AircraftCommandApplicability.ShowsCancelTakeoff,
        [MenuIds.GroundCrossRunway] = AircraftCommandApplicability.ShowsCrossAtHoldShort,
        [MenuIds.GroundResumeTaxi] = AircraftCommandApplicability.ShowsResumeTaxiAtHoldShort,
        [MenuIds.ApproachClearedVisual] = AircraftCommandApplicability.ShowsClearedVisual,
        [MenuIds.SpeedAssign] = AircraftCommandApplicability.ShowsSpeedAdjustment,
        [MenuIds.SpeedCustom] = AircraftCommandApplicability.ShowsSpeedAdjustment,
        [MenuIds.SpeedFinalApproach] = AircraftCommandApplicability.ShowsSpeedAdjustment,
        [MenuIds.TowerExitLeft] = AircraftCommandApplicability.ShowsRunwayExit,
        [MenuIds.TowerExitRight] = AircraftCommandApplicability.ShowsRunwayExit,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The phases a quick entry is admitted in beyond its catalog applicability, each one the sim is proven to accept the
    /// command in; All Commands keeps its own applicability. The flight-rules filter and the visibility rules still apply.
    /// </summary>
    private static readonly FrozenDictionary<string, Func<IMenuAircraft, MenuContext, bool>> Widening = new Dictionary<
        string,
        Func<IMenuAircraft, MenuContext, bool>
    >(StringComparer.Ordinal)
    {
        [MenuIds.GroundPushRoute] = (ac, _) => AircraftCommandApplicability.WidensPushRoute(ac),
        [MenuIds.GroundDrawTaxiRoute] = (ac, _) => AircraftCommandApplicability.WidensDrawTaxiRoute(ac),
        [MenuIds.GroundFollow] = AircraftCommandApplicability.WidensFollowWhileFollowing,
        [MenuIds.GroundGiveWay] = AircraftCommandApplicability.WidensFollowWhileFollowing,
        [MenuIds.GroundCrossRunway] = (ac, _) => AircraftCommandApplicability.WidensCrossRunway(ac),
        [MenuIds.TowerCancelTakeoff] = (ac, _) => AircraftCommandApplicability.WidensCancelTakeoff(ac),
        [MenuIds.PatternEnterLeftDownwind] = (ac, context) => WidensLegEntry(ac, context, takenInClimbOut: true),
        [MenuIds.PatternEnterRightDownwind] = (ac, context) => WidensLegEntry(ac, context, takenInClimbOut: true),
        [MenuIds.PatternEnterLeftBase] = (ac, context) => WidensLegEntry(ac, context, takenInClimbOut: true),
        [MenuIds.PatternEnterRightBase] = (ac, context) => WidensLegEntry(ac, context, takenInClimbOut: false),
        [MenuIds.PatternEnterFinal] = (ac, context) => AircraftCommandApplicability.WidensEnterFinal(ac, context.VfrCommandsForIfr),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The quick commands for <paramref name="aircraft"/> from its situation's effective list
    /// (<see cref="MenuContext.QuickCommandListFor"/>: the stored list, else the default); empty for an
    /// <see cref="AircraftSituation.Unknown"/> aircraft, which shows no quick list.
    /// </summary>
    /// <param name="aircraft">The aircraft the menu commands.</param>
    /// <param name="context">The menu's click and session.</param>
    /// <param name="buildsAnItem">
    /// Whether an entry builds a menu item for this aircraft; one that builds none is dropped before the strip is capped,
    /// so it never takes a strip slot from the next glyph-bearing entry.
    /// </param>
    public static QuickCommandResolution Resolve(IMenuAircraft aircraft, MenuContext context, Func<MenuCatalogEntry, bool> buildsAnItem) =>
        aircraft.Situation == AircraftSituation.Unknown
            ? QuickCommandResolution.Empty
            : Resolve(context.QuickCommandListFor(aircraft.Situation), aircraft, context, buildsAnItem);

    /// <summary>
    /// The quick commands <paramref name="entries"/> leaves for <paramref name="aircraft"/>, in list order. A custom entry
    /// resolves to a text entry (<see cref="CustomQuickCommandEntry.MenuId"/>, no glyph) that sends
    /// <see cref="CustomQuickCommandEntry.CommandFor"/>; it is filtered by its flight rules alone.
    /// </summary>
    /// <param name="entries">The quick-command list to resolve, in order.</param>
    /// <param name="aircraft">The aircraft the menu commands.</param>
    /// <param name="context">The menu's click and session.</param>
    /// <param name="buildsAnItem">Whether an entry builds a menu item for this aircraft; one that builds none is dropped before the split.</param>
    public static QuickCommandResolution Resolve(
        IReadOnlyList<QuickCommandEntry> entries,
        IMenuAircraft aircraft,
        MenuContext context,
        Func<MenuCatalogEntry, bool> buildsAnItem
    )
    {
        List<MenuCatalogEntry> shown = [];
        foreach (QuickCommandEntry entry in entries)
        {
            MenuCatalogEntry? menuEntry = entry switch
            {
                CatalogQuickCommandEntry catalog => ShownCatalogEntry(catalog, aircraft, context),
                CustomQuickCommandEntry custom => ShownCustomEntry(custom, aircraft, context),
                _ => throw new ArgumentOutOfRangeException(nameof(entries), entry, "Unknown quick-command entry kind."),
            };
            if ((menuEntry is not null) && buildsAnItem(menuEntry))
            {
                shown.Add(menuEntry);
            }
        }

        return QuickCommandGlyphs.Split(shown);
    }

    private static MenuCatalogEntry? ShownCatalogEntry(CatalogQuickCommandEntry entry, IMenuAircraft aircraft, MenuContext context)
    {
        MenuCatalogEntry catalogEntry = MenuCatalog.Get(entry.CatalogId);
        bool rulesMatch = MatchesFlightRules(entry.FlightRules ?? catalogEntry.DefaultFlightRules, entry.CatalogId, aircraft, context);
        return (rulesMatch && IsAdmitted(catalogEntry, aircraft, context) && IsVisible(entry.CatalogId, aircraft)) ? catalogEntry : null;
    }

    /// <summary>
    /// A custom entry as a menu entry whose click sends its command for <paramref name="aircraft"/> through the host's VFR
    /// gate, as a favorite's does, since the controller typed it; the ground or air text is chosen at the click, on the
    /// live aircraft. Null when its flight rules leave it out. A VFR-only custom entry is hidden from an IFR aircraft under
    /// <see cref="VfrCommandsForIfr.EnterFinalOnly"/> even when its command is <c>EF</c>, which the gate would let through:
    /// the straight-in exception is keyed on the catalog's enter-final action, not on command text.
    /// </summary>
    private static MenuCatalogEntry? ShownCustomEntry(CustomQuickCommandEntry entry, IMenuAircraft aircraft, MenuContext context)
    {
        if (!MatchesFlightRules(entry.FlightRules, CustomQuickCommandEntry.MenuId, aircraft, context))
        {
            return null;
        }

        return new MenuCatalogEntry(
            CustomQuickCommandEntry.MenuId,
            entry.Label,
            entry.FlightRules,
            (_, _) => true,
            (_, menuContext, host) => MenuCatalog.BuildGatedSend(entry.Label, () => entry.CommandFor(aircraft), menuContext, host)
        );
    }

    private static bool WidensLegEntry(IMenuAircraft aircraft, MenuContext context, bool takenInClimbOut) =>
        AircraftCommandApplicability.WidensPatternLegEntry(aircraft, context.VfrCommandsForIfr, takenInClimbOut);

    private static bool IsAdmitted(MenuCatalogEntry catalogEntry, IMenuAircraft aircraft, MenuContext context) =>
        catalogEntry.IsApplicable(aircraft, context)
        || (Widening.TryGetValue(catalogEntry.Id, out Func<IMenuAircraft, MenuContext, bool>? widen) && widen(aircraft, context));

    private static bool IsVisible(string catalogId, IMenuAircraft aircraft) =>
        !Visibility.TryGetValue(catalogId, out Func<IMenuAircraft, bool>? rule) || rule(aircraft);

    /// <summary>
    /// Whether an entry offered under <paramref name="rules"/> fits the aircraft's filed rules. An aircraft whose rules are
    /// neither IFR nor VFR (none filed) gets only the entries offered under both. A VFR-only entry reaches an IFR aircraft
    /// under the controller's "VFR commands for IFR aircraft" setting, as All Commands offers it: every one under
    /// <see cref="VfrCommandsForIfr.All"/>, straight-in final alone under <see cref="VfrCommandsForIfr.EnterFinalOnly"/>.
    /// </summary>
    private static bool MatchesFlightRules(MenuFlightRules rules, string catalogId, IMenuAircraft aircraft, MenuContext context) =>
        rules switch
        {
            MenuFlightRules.Both => true,
            MenuFlightRules.IfrOnly => IsIfr(aircraft),
            MenuFlightRules.VfrOnly => AircraftCommandApplicability.IsVfr(aircraft) || (IsIfr(aircraft) && AllowsVfrEntryForIfr(catalogId, context)),
            _ => throw new ArgumentOutOfRangeException(nameof(rules), rules, "Unknown quick-command flight-rules filter."),
        };

    private static bool IsIfr(IMenuAircraft aircraft) => string.Equals(aircraft.FlightRules, "IFR", StringComparison.OrdinalIgnoreCase);

    private static bool AllowsVfrEntryForIfr(string catalogId, MenuContext context) =>
        context.VfrCommandsForIfr switch
        {
            VfrCommandsForIfr.All => true,
            VfrCommandsForIfr.EnterFinalOnly => catalogId == MenuIds.PatternEnterFinal,
            _ => false,
        };
}
