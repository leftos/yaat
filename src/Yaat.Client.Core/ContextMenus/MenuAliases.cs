using Yaat.Sim.Commands;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The command verbs each catalog entry sends, which its menu row shows beside its label so a mouse user learns the typed
/// command: every <see cref="MenuCatalog.All"/> id is either in <see cref="CanonicalVerbs"/> or in <see cref="NoVerb"/>.
/// </summary>
public static class MenuAliases
{
    /// <summary>The separator between the two verbs of a row that sends two.</summary>
    private const string PairSeparator = " / ";

    /// <summary>What follows the first verb of a row that sends three or more.</summary>
    private const string MoreMarker = " …";

    /// <summary>
    /// Each id that sends a verb of its own and the verbs its row sends, each written as any of its aliases: a leaf's one
    /// verb, or a flyout's or box's verbs in the flyout's own row order. A runway-bearing leaf has its bare verb.
    /// <see cref="Display(string, CommandScheme)"/> maps each to the scheme's primary verb, falling back to the verb as listed.
    /// Warp here lists both its verbs, though each build sends one and records that one's alias itself.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> CanonicalVerbs { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [MenuIds.LiveTrafficAssume] = ["ASSUME"],
            [MenuIds.LiveTrafficAssumeAndTrack] = ["ASSUME", "TRACK"],
            [MenuIds.LiveTrafficUnassume] = ["UNASSUME"],
            [MenuIds.LiveTrafficAssumeSelected] = ["ASSUME"],
            [MenuIds.TrackTrack] = ["TRACK"],
            [MenuIds.TrackDrop] = ["DROP"],
            [MenuIds.TrackAcceptHandoff] = ["ACCEPT"],
            [MenuIds.TrackInitiateHandoff] = ["HO"],
            [MenuIds.TrackCancelHandoff] = ["CANCEL"],
            [MenuIds.TrackPointOut] = ["PO"],
            [MenuIds.TrackAcknowledgePointout] = ["OK"],
            [MenuIds.SquawkCode] = ["SQ", "RANDSQ", "SQVFR", "SQNORM", "SQSBY", "IDENT"],
            [MenuIds.SquawkAssigned] = ["SQ"],
            [MenuIds.SquawkRandom] = ["RANDSQ"],
            [MenuIds.SquawkVfr] = ["SQVFR"],
            [MenuIds.SquawkNormal] = ["SQNORM"],
            [MenuIds.SquawkStandby] = ["SQSBY"],
            [MenuIds.SquawkIdent] = ["IDENT"],
            [MenuIds.AskPilotAltitude] = ["SALT"],
            [MenuIds.AskPilotHeading] = ["SHDG"],
            [MenuIds.AskPilotSpeed] = ["SSPD"],
            [MenuIds.AskPilotMach] = ["SMACH"],
            [MenuIds.AskPilotPosition] = ["SPOS"],
            [MenuIds.AskPilotExpectedApproach] = ["SEAPP"],
            [MenuIds.AskPilotCustom] = ["SALT", "SHDG", "SSPD", "SMACH", "SPOS", "SEAPP", "SAY"],
            [MenuIds.CoordinationRelease] = ["RD"],
            [MenuIds.CoordinationHold] = ["RDH"],
            [MenuIds.CoordinationRecall] = ["RDR"],
            [MenuIds.CoordinationAcknowledge] = ["RDACK"],
            [MenuIds.CoordinationReleaseHeld] = ["REL"],
            [MenuIds.CoordinationCheckReleaseWindow] = ["CFR"],
            [MenuIds.DataBlockScratchpad] = ["SP", "SP1", "SP2"],
            [MenuIds.DataBlockScratchpadClear1] = ["SP1"],
            [MenuIds.DataBlockScratchpadClear2] = ["SP2"],
            [MenuIds.DataBlockTempAltitude] = ["TEMPALT"],
            [MenuIds.DataBlockCruise] = ["CRUISE"],
            [MenuIds.DataBlockAnnotate] = ["ANNOTATE"],
            [MenuIds.SimControlWarp] = ["WARP"],
            [MenuIds.SimControlDelete] = ["DEL"],
            [MenuIds.HeadingPresent] = ["FPH"],
            [MenuIds.HeadingFly] = ["FH"],
            [MenuIds.HeadingTurnLeft] = ["TL"],
            [MenuIds.HeadingTurnRight] = ["TR"],
            [MenuIds.HeadingTurnLeftDegrees] = ["LT"],
            [MenuIds.HeadingTurnRightDegrees] = ["RT"],
            [MenuIds.AltitudeMaintain] = ["CM", "DM"],
            [MenuIds.SpeedAssign] = ["SPD", "MACH", "RNS", "RFAS"],
            [MenuIds.SpeedCustom] = ["SPD", "MACH", "RNS", "RFAS"],
            [MenuIds.SpeedNormal] = ["RNS"],
            [MenuIds.SpeedFinalApproach] = ["RFAS"],
            [MenuIds.NavigationDirectTo] = ["DCT"],
            [MenuIds.NavigationAppendDirectTo] = ["ADCT"],
            [MenuIds.NavigationOnCourse] = ["OC"],
            [MenuIds.HoldPresentLeft] = ["HPPL"],
            [MenuIds.HoldPresentRight] = ["HPPR"],
            [MenuIds.HoldFixLeft] = ["HFIXL"],
            [MenuIds.HoldFixRight] = ["HFIXR"],
            [MenuIds.HoldPattern] = ["HOLDP"],
            [MenuIds.ApproachCleared] = ["CAPP", "CAPPF", "CVA", "CVAF"],
            [MenuIds.ApproachJoin] = ["JAPP"],
            [MenuIds.ApproachClearedStraightIn] = ["CAPPSI"],
            [MenuIds.ApproachJoinStraightIn] = ["JAPPSI"],
            [MenuIds.ApproachClearedForce] = ["CAPPF"],
            [MenuIds.ApproachJoinForce] = ["JAPPF"],
            [MenuIds.ApproachJoinFinalCourse] = ["JFAC"],
            [MenuIds.ApproachExpect] = ["EAPP"],
            [MenuIds.ApproachClearedVisual] = ["CVA", "CVAF"],
            [MenuIds.ApproachReportFieldInSight] = ["RFIS"],
            [MenuIds.ApproachReportTrafficInSight] = ["RTIS"],
            [MenuIds.ApproachReportWhen] = ["REPORT"],
            [MenuIds.ApproachReportNMileFinal] = ["REPORT"],
            [MenuIds.ApproachReportAtFix] = ["REPORT"],
            [MenuIds.ProceduresJoinStar] = ["JARR", "DVIA"],
            [MenuIds.ProceduresClimbViaSid] = ["CVIA"],
            [MenuIds.ProceduresDescendViaStar] = ["DVIA"],
            [MenuIds.ProceduresCrossFix] = ["CFIX"],
            [MenuIds.ProceduresDepartFix] = ["DEPART"],
            [MenuIds.ProceduresPtac] = ["PTAC", "PTACF"],
            [MenuIds.ProceduresJoinAirway] = ["JAWY"],
            [MenuIds.ProceduresJoinRadialOutbound] = ["JRADO"],
            [MenuIds.ProceduresJoinRadialInbound] = ["JRADI"],
            [MenuIds.TowerLineUpAndWait] = ["LUAW"],
            [MenuIds.TowerClearedForTakeoff] = ["CTO"],
            [MenuIds.TowerCancelTakeoff] = ["CTOC"],
            [MenuIds.TowerClearedToLand] = ["CLAND"],
            [MenuIds.TowerForceLanding] = ["CLANDF"],
            [MenuIds.TowerClearedOption] = ["COPT"],
            [MenuIds.TowerTouchAndGo] = ["TG"],
            [MenuIds.TowerStopAndGo] = ["SG"],
            [MenuIds.TowerLowApproach] = ["LA"],
            [MenuIds.TowerGoAround] = ["GA"],
            [MenuIds.TowerCancelLanding] = ["CLC"],
            [MenuIds.TowerExitLeft] = ["EL"],
            [MenuIds.TowerExitRight] = ["ER"],
            [MenuIds.PatternEnterLeftDownwind] = ["ELD"],
            [MenuIds.PatternEnterRightDownwind] = ["ERD"],
            [MenuIds.PatternEnterLeftBase] = ["ELB"],
            [MenuIds.PatternEnterRightBase] = ["ERB"],
            [MenuIds.PatternEnterFinal] = ["EF"],
            [MenuIds.PatternTurnCrosswind] = ["TC"],
            [MenuIds.PatternTurnDownwind] = ["TD"],
            [MenuIds.PatternTurnBase] = ["TB"],
            [MenuIds.PatternExtend] = ["EXT"],
            [MenuIds.PatternShortApproach] = ["MSA"],
            [MenuIds.PatternNormalApproach] = ["MNA"],
            [MenuIds.PatternLeft360] = ["L360"],
            [MenuIds.PatternRight360] = ["R360"],
            [MenuIds.PatternLeft270] = ["L270"],
            [MenuIds.PatternRight270] = ["R270"],
            [MenuIds.PatternPlan270] = ["P270"],
            [MenuIds.PatternCancel270] = ["NO270"],
            [MenuIds.PatternCircleAirport] = ["CA"],
            [MenuIds.PatternMakeLeftTraffic] = ["MLT"],
            [MenuIds.PatternMakeRightTraffic] = ["MRT"],
            [MenuIds.PatternFollow] = ["FOLLOW", "FOLLOWF"],
            [MenuIds.GroundPushback] = ["PUSH"],
            [MenuIds.GroundPushbackFace] = ["PUSH"],
            [MenuIds.GroundPushbackTo] = ["PUSH"],
            [MenuIds.GroundHoldPosition] = ["HOLD"],
            [MenuIds.GroundResumeTaxi] = ["RES"],
            [MenuIds.GroundCrossRunway] = ["CROSS"],
            [MenuIds.GroundBreakConflict] = ["BREAK"],
            [MenuIds.GroundHoldShort] = ["HS"],
            [MenuIds.GroundFollow] = ["FOLLOWG"],
            [MenuIds.GroundGiveWay] = ["GW"],
            [MenuIds.GroundTaxiPreset] = ["TAXI"],
            [MenuIds.GroundTaxiToRunway] = ["TAXI"],
            [MenuIds.GroundAirTaxi] = ["ATXI"],
            [MenuIds.GroundLandHelicopter] = ["LAND"],
            [MenuIds.RelativeReportInSight] = ["RTIS"],
            [MenuIds.RelativeFollow] = ["FOLLOW"],
            [MenuIds.GroundRelativeGiveWay] = ["GW"],
            [MenuIds.GroundRelativeFollow] = ["FOLLOWG"],
            [MenuIds.SpawnNow] = ["SPAWN"],
            [MenuIds.SpawnDelay] = ["SPAWNDELAY"],
            [MenuIds.PointFlyHeading] = ["FH"],
            [MenuIds.PointDirectTo] = ["DCT"],
            [MenuIds.PointAppendDirectTo] = ["ADCT"],
            [MenuIds.PointHoldLeft] = ["HFIXL"],
            [MenuIds.PointHoldRight] = ["HFIXR"],
            [MenuIds.PointTaxiHere] = ["TAXI"],
            [MenuIds.PointTaxiToRunway] = ["TAXI"],
            [MenuIds.PointPushTo] = ["PUSH"],
            [MenuIds.PointCustomTaxi] = ["TAXI"],
            [MenuIds.PointWarpHere] = ["WARPG", "WARP"],
        };

    /// <summary>
    /// The ids with no verb of their own, whose rows show no alias: the command and note boxes, the flight-plan editor,
    /// Favorites, and the host actions that send nothing themselves (drawing a taxi route or a push route).
    /// </summary>
    public static IReadOnlySet<string> NoVerb { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            MenuIds.AircraftCommand,
            MenuIds.AircraftNote,
            MenuIds.AircraftEditFlightPlan,
            MenuIds.FavoritesMenu,
            MenuIds.GroundDrawTaxiRoute,
            MenuIds.GroundPushRoute,
        };

    /// <summary>
    /// The alias <paramref name="id"/>'s row shows under <paramref name="scheme"/>: its <see cref="CanonicalVerbs"/> entry
    /// as <see cref="Display(IReadOnlyList{string}, CommandScheme)"/> writes it; null for an id in <see cref="NoVerb"/>, and
    /// for a custom quick entry (<see cref="CustomQuickCommandEntry.MenuId"/>), whose builder records the alias of the
    /// command it sends itself and leaves an empty command with none.
    /// </summary>
    /// <exception cref="KeyNotFoundException">When <paramref name="id"/> is in neither table.</exception>
    public static string? Display(string id, CommandScheme scheme)
    {
        if (NoVerb.Contains(id) || (id == CustomQuickCommandEntry.MenuId))
        {
            return null;
        }

        if (!CanonicalVerbs.TryGetValue(id, out IReadOnlyList<string>? verbs))
        {
            throw new KeyNotFoundException(
                $"The menu alias table has no entry for '{id}'; add it to MenuAliases.CanonicalVerbs or MenuAliases.NoVerb."
            );
        }

        return Display(verbs, scheme);
    }

    /// <summary>
    /// The alias a row sending <paramref name="verbs"/> shows under <paramref name="scheme"/>: each verb mapped to the scheme's
    /// primary verb for its command type, then one verb alone, two as <c>A / B</c>, three or more as <c>A …</c>. A builder that
    /// knows which one verb its row sends records the alias itself through this (<see cref="MenuAlias.SetText"/>).
    /// </summary>
    public static string Display(IReadOnlyList<string> verbs, CommandScheme scheme)
    {
        List<string> shown = [.. verbs.Select(verb => PrimaryVerb(verb, scheme)).Distinct(StringComparer.Ordinal)];
        return shown.Count switch
        {
            0 => throw new ArgumentException("A command alias needs at least one verb.", nameof(verbs)),
            1 => shown[0],
            2 => shown[0] + PairSeparator + shown[1],
            _ => shown[0] + MoreMarker,
        };
    }

    /// <summary>
    /// <paramref name="verb"/> as the scheme's primary verb for its command type, as the command bar completes it; the verb as
    /// listed when the registry knows no such verb or the scheme has no aliases for its type.
    /// </summary>
    private static string PrimaryVerb(string verb, CommandScheme scheme) =>
        CommandRegistry.AliasToCanonicType.TryGetValue(verb, out CanonicalCommandType type)
        && scheme.Patterns.TryGetValue(type, out CommandPattern? pattern)
        && (pattern.Aliases.Count > 0)
            ? pattern.PrimaryVerb
            : verb;
}
