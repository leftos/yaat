using System.Collections.Frozen;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The task categories the aircraft menu's commands fall into, in the order All Commands lists them: the live-traffic
/// assume items first, the seven task groups, then the sim-control items. Each task group's label heads its All Commands
/// group; the live-traffic and sim-control items have no group header there, and their labels name them in the quick
/// commands only.
/// </summary>
public enum MenuCategory
{
    LiveTraffic,
    Runway,
    Taxi,
    Vectors,
    Approach,
    Pattern,
    PilotReports,
    TrackAndCoordination,
    Sim,
}

/// <summary>
/// The category map: which category (<see cref="MenuCategory"/>) each <see cref="MenuCatalog"/> id belongs to, the
/// label each category shows, and the ids that belong to none (the root's items, spawn, point-menu and For-section
/// items). Every catalog id is in exactly one of the two.
/// </summary>
public static class MenuCategories
{
    /// <summary>The category's label: its All Commands group's header for a task group, and the quick commands' label.</summary>
    /// <param name="category">The category to name.</param>
    /// <returns>
    /// "Live traffic", "Runway", "Taxi", "Vectors", "Approach", "Pattern", "Pilot reports", "Track and coordination" or "Sim".
    /// </returns>
    public static string Label(MenuCategory category) =>
        category switch
        {
            MenuCategory.LiveTraffic => "Live traffic",
            MenuCategory.Sim => "Sim",
            MenuCategory.Runway => "Runway",
            MenuCategory.Taxi => "Taxi",
            MenuCategory.Vectors => "Vectors",
            MenuCategory.Approach => "Approach",
            MenuCategory.Pattern => "Pattern",
            MenuCategory.PilotReports => "Pilot reports",
            MenuCategory.TrackAndCoordination => "Track and coordination",
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Not a menu category."),
        };

    /// <summary>The category of the catalog id <paramref name="menuId"/>, or null when it belongs to none.</summary>
    /// <param name="menuId">A <see cref="MenuIds"/> identifier.</param>
    /// <returns>The id's category, or null for an id in <see cref="Uncategorized"/> or an unknown id.</returns>
    public static MenuCategory? Of(string menuId) => Categories.TryGetValue(menuId, out MenuCategory category) ? category : null;

    /// <summary>
    /// The catalog ids in no category, listed explicitly: the root's items (Favorites, Command…, Note…), the delayed-spawn
    /// items, and the point-menu and For-section items.
    /// </summary>
    public static IReadOnlySet<string> Uncategorized { get; } =
        new[]
        {
            MenuIds.FavoritesMenu,
            MenuIds.AircraftCommand,
            MenuIds.AircraftNote,
            MenuIds.SpawnNow,
            MenuIds.SpawnDelay,
            MenuIds.PointFlyHeading,
            MenuIds.PointDirectTo,
            MenuIds.PointAppendDirectTo,
            MenuIds.PointHoldLeft,
            MenuIds.PointHoldRight,
            MenuIds.PointTaxiHere,
            MenuIds.PointTaxiToRunway,
            MenuIds.PointPushTo,
            MenuIds.PointCustomTaxi,
            MenuIds.PointWarpHere,
            MenuIds.RelativeReportInSight,
            MenuIds.RelativeFollow,
            MenuIds.GroundRelativeFollow,
            MenuIds.GroundRelativeGiveWay,
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Each category's ids: a task group's in the order its All Commands group lists them, Live traffic's and Sim's in
    /// the order All Commands lists those items.
    /// </summary>
    private static readonly (MenuCategory Category, string[] Ids)[] Groups =
    [
        (
            MenuCategory.LiveTraffic,
            [MenuIds.LiveTrafficAssume, MenuIds.LiveTrafficAssumeAndTrack, MenuIds.LiveTrafficAssumeSelected, MenuIds.LiveTrafficUnassume]
        ),
        (MenuCategory.Sim, [MenuIds.SimControlWarp, MenuIds.SimControlDelete]),
        (
            MenuCategory.Runway,
            [
                MenuIds.TowerLineUpAndWait,
                MenuIds.TowerClearedForTakeoff,
                MenuIds.TowerCancelTakeoff,
                MenuIds.TowerClearedToLand,
                MenuIds.TowerForceLanding,
                MenuIds.TowerClearedOption,
                MenuIds.TowerTouchAndGo,
                MenuIds.TowerStopAndGo,
                MenuIds.TowerLowApproach,
                MenuIds.TowerGoAround,
                MenuIds.TowerCancelLanding,
                MenuIds.TowerExitLeft,
                MenuIds.TowerExitRight,
            ]
        ),
        (
            MenuCategory.Taxi,
            [
                MenuIds.GroundPushback,
                MenuIds.GroundPushbackFace,
                MenuIds.GroundPushbackTo,
                MenuIds.GroundPushRoute,
                MenuIds.GroundHoldPosition,
                MenuIds.GroundTaxiToRunway,
                MenuIds.GroundTaxiPreset,
                MenuIds.GroundAirTaxi,
                MenuIds.GroundLandHelicopter,
                MenuIds.GroundHoldShort,
                MenuIds.GroundFollow,
                MenuIds.GroundGiveWay,
                MenuIds.GroundBreakConflict,
                MenuIds.GroundResumeTaxi,
                MenuIds.GroundCrossRunway,
                MenuIds.GroundDrawTaxiRoute,
            ]
        ),
        (
            MenuCategory.Vectors,
            [
                MenuIds.HeadingPresent,
                MenuIds.HeadingFly,
                MenuIds.HeadingTurnLeft,
                MenuIds.HeadingTurnRight,
                MenuIds.HeadingTurnLeftDegrees,
                MenuIds.HeadingTurnRightDegrees,
                MenuIds.AltitudeMaintain,
                MenuIds.SpeedAssign,
                MenuIds.SpeedCustom,
                MenuIds.SpeedNormal,
                MenuIds.SpeedFinalApproach,
                MenuIds.NavigationDirectTo,
                MenuIds.NavigationAppendDirectTo,
                MenuIds.NavigationOnCourse,
                MenuIds.ProceduresCrossFix,
                MenuIds.ProceduresDepartFix,
                MenuIds.ProceduresJoinAirway,
                MenuIds.ProceduresJoinRadialOutbound,
                MenuIds.ProceduresJoinRadialInbound,
                MenuIds.ProceduresClimbViaSid,
            ]
        ),
        (
            MenuCategory.Approach,
            [
                .. MenuCatalog.ApproachPickerIds,
                MenuIds.ApproachClearedVisual,
                MenuIds.ProceduresJoinStar,
                MenuIds.ProceduresDescendViaStar,
                MenuIds.ProceduresPtac,
                MenuIds.HoldPattern,
                MenuIds.HoldPresentLeft,
                MenuIds.HoldPresentRight,
                MenuIds.HoldFixLeft,
                MenuIds.HoldFixRight,
            ]
        ),
        (
            MenuCategory.Pattern,
            [
                MenuIds.PatternEnterLeftDownwind,
                MenuIds.PatternEnterRightDownwind,
                MenuIds.PatternEnterLeftBase,
                MenuIds.PatternEnterRightBase,
                MenuIds.PatternEnterFinal,
                MenuIds.PatternTurnCrosswind,
                MenuIds.PatternTurnDownwind,
                MenuIds.PatternTurnBase,
                MenuIds.PatternExtend,
                MenuIds.PatternShortApproach,
                MenuIds.PatternNormalApproach,
                MenuIds.PatternLeft360,
                MenuIds.PatternRight360,
                MenuIds.PatternLeft270,
                MenuIds.PatternRight270,
                MenuIds.PatternPlan270,
                MenuIds.PatternCancel270,
                MenuIds.PatternCircleAirport,
                MenuIds.PatternMakeLeftTraffic,
                MenuIds.PatternMakeRightTraffic,
                MenuIds.PatternFollow,
            ]
        ),
        (
            MenuCategory.PilotReports,
            [
                MenuIds.ApproachReportFieldInSight,
                MenuIds.ApproachReportTrafficInSight,
                MenuIds.ApproachReportWhen,
                MenuIds.ApproachReportNMileFinal,
                MenuIds.ApproachReportAtFix,
                MenuIds.AskPilotAltitude,
                MenuIds.AskPilotHeading,
                MenuIds.AskPilotSpeed,
                MenuIds.AskPilotMach,
                MenuIds.AskPilotPosition,
                MenuIds.AskPilotExpectedApproach,
                MenuIds.AskPilotCustom,
            ]
        ),
        (
            MenuCategory.TrackAndCoordination,
            [
                MenuIds.TrackTrack,
                MenuIds.TrackInitiateHandoff,
                MenuIds.TrackPointOut,
                MenuIds.TrackDrop,
                MenuIds.TrackAcceptHandoff,
                MenuIds.TrackCancelHandoff,
                MenuIds.TrackAcknowledgePointout,
                MenuIds.DataBlockScratchpad,
                MenuIds.DataBlockScratchpadClear1,
                MenuIds.DataBlockScratchpadClear2,
                MenuIds.DataBlockTempAltitude,
                MenuIds.DataBlockCruise,
                MenuIds.DataBlockAnnotate,
                MenuIds.SquawkCode,
                MenuIds.SquawkAssigned,
                MenuIds.SquawkRandom,
                MenuIds.SquawkVfr,
                MenuIds.SquawkNormal,
                MenuIds.SquawkStandby,
                MenuIds.SquawkIdent,
                MenuIds.CoordinationRelease,
                MenuIds.CoordinationHold,
                MenuIds.CoordinationRecall,
                MenuIds.CoordinationAcknowledge,
                MenuIds.CoordinationReleaseHeld,
                MenuIds.CoordinationCheckReleaseWindow,
                MenuIds.AircraftEditFlightPlan,
            ]
        ),
    ];

    private static readonly FrozenDictionary<string, MenuCategory> Categories = Groups
        .SelectMany(group => group.Ids.Select(id => KeyValuePair.Create(id, group.Category)))
        .ToFrozenDictionary(StringComparer.Ordinal);
}
