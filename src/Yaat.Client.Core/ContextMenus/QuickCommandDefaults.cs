using Yaat.Sim.Situation;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The quick-command list each classified <see cref="AircraftSituation"/> starts with, most frequent first, as the
/// aviation-reviewed default situation table gives them. An instruction the table annotates "(IFR)" or "(VFR)" carries
/// that flight-rules override; every other entry takes its catalog default. A list names catalog actions only, never a
/// view's display item. <see cref="AircraftSituation.Unknown"/> has no list, so an unclassified aircraft shows no quick
/// commands.
/// </summary>
public static class QuickCommandDefaults
{
    /// <summary>Every situation's default list; <see cref="AircraftSituation.Unknown"/> is absent.</summary>
    public static IReadOnlyDictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>> Lists { get; } =
        new Dictionary<AircraftSituation, IReadOnlyList<QuickCommandEntry>>
        {
            [AircraftSituation.AtParking] =
            [
                Entry(MenuIds.GroundPushback),
                Entry(MenuIds.GroundTaxiPreset),
                Entry(MenuIds.GroundDrawTaxiRoute),
                Entry(MenuIds.GroundPushbackTo),
                Entry(MenuIds.CoordinationCheckReleaseWindow),
            ],
            [AircraftSituation.PushingBack] = [Entry(MenuIds.GroundHoldPosition), Entry(MenuIds.GroundPushRoute)],
            [AircraftSituation.HoldingOnGround] =
            [
                Entry(MenuIds.GroundResumeTaxi),
                Entry(MenuIds.GroundDrawTaxiRoute),
                Entry(MenuIds.GroundFollow),
                Entry(MenuIds.GroundGiveWay),
                Entry(MenuIds.GroundCrossRunway),
            ],
            [AircraftSituation.Taxiing] =
            [
                Entry(MenuIds.GroundHoldPosition),
                Entry(MenuIds.GroundHoldShort),
                Entry(MenuIds.GroundCrossRunway),
                Entry(MenuIds.GroundFollow),
                Entry(MenuIds.GroundGiveWay),
                Entry(MenuIds.GroundBreakConflict),
                Entry(MenuIds.TowerClearedForTakeoff),
                Entry(MenuIds.TowerCancelTakeoff),
            ],
            // One list for both bars: the visibility rules show takeoff and LUAW only at the departure runway's bar, and
            // Cross and Resume taxi only at any other (the sim refuses RES at a departure-runway bar).
            [AircraftSituation.HoldingShort] =
            [
                Entry(MenuIds.TowerClearedForTakeoff),
                Entry(MenuIds.TowerLineUpAndWait),
                Entry(MenuIds.GroundCrossRunway),
                Entry(MenuIds.GroundResumeTaxi),
            ],
            [AircraftSituation.LinedUp] =
            [
                Entry(MenuIds.TowerClearedForTakeoff),
                Entry(MenuIds.TowerCancelTakeoff),
                Entry(MenuIds.GroundDrawTaxiRoute),
            ],
            [AircraftSituation.Departing] =
            [
                Entry(MenuIds.HeadingFly),
                Entry(MenuIds.AltitudeMaintain),
                Ifr(MenuIds.ProceduresClimbViaSid),
                Entry(MenuIds.NavigationDirectTo),
                Entry(MenuIds.SpeedAssign),
            ],
            [AircraftSituation.IfrEnroute] =
            [
                Entry(MenuIds.HeadingFly),
                Entry(MenuIds.AltitudeMaintain),
                Entry(MenuIds.NavigationDirectTo),
                Entry(MenuIds.SpeedAssign),
                Entry(MenuIds.HoldPattern),
                Entry(MenuIds.ProceduresCrossFix),
            ],
            [AircraftSituation.IfrArrival] =
            [
                Ifr(MenuIds.ProceduresDescendViaStar),
                Entry(MenuIds.AltitudeMaintain),
                Entry(MenuIds.SpeedAssign),
                Entry(MenuIds.HeadingFly),
                Entry(MenuIds.NavigationDirectTo),
                Entry(MenuIds.ApproachExpect),
                Entry(MenuIds.HoldPattern),
            ],
            [AircraftSituation.VfrFlightFollowing] =
            [
                Entry(MenuIds.ApproachReportTrafficInSight),
                Entry(MenuIds.HeadingFly),
                Entry(MenuIds.AltitudeMaintain),
                Entry(MenuIds.NavigationDirectTo),
                Entry(MenuIds.ApproachExpect),
            ],
            [AircraftSituation.Approach] =
            [
                Entry(MenuIds.ApproachCleared),
                Entry(MenuIds.AltitudeMaintain),
                Entry(MenuIds.SpeedAssign),
                Entry(MenuIds.ApproachReportFieldInSight),
                Ifr(MenuIds.ApproachClearedVisual),
                Entry(MenuIds.TowerClearedToLand),
            ],
            // A VFR hold (VfrHoldPhase, AirspaceBoundaryHoldPhase) also classifies as Holding, so the IFR defaults are
            // followed by VFR-tagged pattern entries rather than a different mapping.
            [AircraftSituation.Holding] =
            [
                Ifr(MenuIds.ApproachCleared),
                Entry(MenuIds.NavigationDirectTo),
                Entry(MenuIds.AltitudeMaintain),
                Vfr(MenuIds.PatternEnterLeftDownwind),
                Vfr(MenuIds.PatternEnterRightDownwind),
                Vfr(MenuIds.PatternEnterLeftBase),
                Vfr(MenuIds.PatternEnterRightBase),
                Vfr(MenuIds.PatternEnterFinal),
            ],
            [AircraftSituation.Pattern] =
            [
                Entry(MenuIds.TowerClearedToLand),
                Entry(MenuIds.TowerClearedOption),
                Entry(MenuIds.TowerTouchAndGo),
                Entry(MenuIds.PatternFollow),
                Entry(MenuIds.PatternExtend),
                Entry(MenuIds.PatternShortApproach),
                Entry(MenuIds.PatternLeft360),
                Entry(MenuIds.PatternRight360),
                Entry(MenuIds.PatternTurnBase),
                Entry(MenuIds.TowerGoAround),
            ],
            [AircraftSituation.Final] =
            [
                Entry(MenuIds.TowerClearedToLand),
                Entry(MenuIds.TowerGoAround),
                Entry(MenuIds.TowerCancelLanding),
                Entry(MenuIds.SpeedFinalApproach),
            ],
            [AircraftSituation.RolloutExit] =
            [
                Entry(MenuIds.TowerExitLeft),
                Entry(MenuIds.TowerExitRight),
                Entry(MenuIds.GroundCrossRunway),
                Entry(MenuIds.GroundDrawTaxiRoute),
            ],
            [AircraftSituation.GoAround] =
            [
                Entry(MenuIds.HeadingFly),
                Entry(MenuIds.AltitudeMaintain),
                Ifr(MenuIds.ApproachCleared),
                Vfr(MenuIds.PatternEnterLeftDownwind),
                Vfr(MenuIds.PatternEnterRightDownwind),
            ],
            [AircraftSituation.LiveTraffic] = [Entry(MenuIds.LiveTrafficAssume), Entry(MenuIds.LiveTrafficAssumeAndTrack)],
            [AircraftSituation.VfrArrivalInbound] =
            [
                Entry(MenuIds.PatternEnterLeftDownwind),
                Entry(MenuIds.PatternEnterRightDownwind),
                Entry(MenuIds.PatternEnterLeftBase),
                Entry(MenuIds.PatternEnterRightBase),
                Entry(MenuIds.PatternEnterFinal),
                Entry(MenuIds.ApproachReportNMileFinal),
                Entry(MenuIds.ApproachReportAtFix),
                Entry(MenuIds.TowerClearedToLand),
                Entry(MenuIds.PatternFollow),
            ],
            [AircraftSituation.VfrDeparting] =
            [
                Entry(MenuIds.HeadingFly),
                Entry(MenuIds.NavigationOnCourse),
                Entry(MenuIds.AltitudeMaintain),
                Entry(MenuIds.ApproachReportAtFix),
                Entry(MenuIds.PatternMakeLeftTraffic),
                Entry(MenuIds.PatternMakeRightTraffic),
            ],
        };

    /// <summary>The default list for <paramref name="situation"/>; empty for <see cref="AircraftSituation.Unknown"/>.</summary>
    public static IReadOnlyList<QuickCommandEntry> For(AircraftSituation situation) =>
        Lists.TryGetValue(situation, out IReadOnlyList<QuickCommandEntry>? list) ? list : [];

    /// <summary>
    /// True when <paramref name="entries"/> is <paramref name="situation"/>'s default list, entry for entry; an empty list
    /// is the default for <see cref="AircraftSituation.Unknown"/>.
    /// </summary>
    public static bool IsDefault(AircraftSituation situation, IReadOnlyList<QuickCommandEntry> entries) => entries.SequenceEqual(For(situation));

    private static QuickCommandEntry Entry(string id) => new CatalogQuickCommandEntry(id, null);

    private static QuickCommandEntry Ifr(string id) => new CatalogQuickCommandEntry(id, MenuFlightRules.IfrOnly);

    private static QuickCommandEntry Vfr(string id) => new CatalogQuickCommandEntry(id, MenuFlightRules.VfrOnly);
}
