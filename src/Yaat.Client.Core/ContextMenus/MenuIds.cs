namespace Yaat.Client.ContextMenus;

/// <summary>
/// The stable identity of every catalog action, so a stored quick-command list survives the menus being
/// rearranged. An identifier is <c>&lt;group&gt;.&lt;item&gt;</c> (<c>tower.cto</c>, <c>heading.fly</c>,
/// <c>ground.pushback</c>), one per action rather than one per placement: the All Commands group the action
/// belongs to is its prefix, and a list stores the action, so moving an action between groups is a deliberate
/// identifier change.
///
/// <para>Identifiers are append-only. An exported preference file carries them, so one is never renamed, reused
/// or deleted — a retired action keeps its constant, so an old file resolves to nothing rather than to a
/// different command.</para>
/// </summary>
public static class MenuIds
{
    /// <summary>The Favorite Commands submenu.</summary>
    public const string FavoritesMenu = "favorites.menu";

    /// <summary>Assume control of a live-traffic shadow (<c>ASSUME</c>).</summary>
    public const string LiveTrafficAssume = "livetraffic.assume";

    /// <summary>Assume control of a live-traffic shadow and start tracking it (<c>ASSUME</c>, then <c>TRACK</c>).</summary>
    public const string LiveTrafficAssumeAndTrack = "livetraffic.assume-and-track";

    /// <summary>Release an aircraft assumed from the live feed back to it (<c>UNASSUME</c>).</summary>
    public const string LiveTrafficUnassume = "livetraffic.unassume";

    /// <summary>Start tracking (<c>TRACK</c>).</summary>
    public const string TrackTrack = "track.track";

    /// <summary>Drop the track (<c>DROP</c>).</summary>
    public const string TrackDrop = "track.drop";

    /// <summary>Accept a handoff (<c>ACCEPT</c>).</summary>
    public const string TrackAcceptHandoff = "track.accept-handoff";

    /// <summary>Initiate a handoff to a position (<c>HO</c>).</summary>
    public const string TrackInitiateHandoff = "track.initiate-handoff";

    /// <summary>Cancel a handoff (<c>CANCEL</c>).</summary>
    public const string TrackCancelHandoff = "track.cancel-handoff";

    /// <summary>Point the track out to a position (<c>PO</c>).</summary>
    public const string TrackPointOut = "track.point-out";

    /// <summary>Acknowledge a pointout (<c>OK</c>).</summary>
    public const string TrackAcknowledgePointout = "track.acknowledge-pointout";

    /// <summary>Squawk a given code (<c>SQ</c>).</summary>
    public const string SquawkCode = "squawk.code";

    /// <summary>Squawk a random code (<c>RANDSQ</c>).</summary>
    public const string SquawkRandom = "squawk.random";

    /// <summary>Squawk VFR (<c>SQVFR</c>).</summary>
    public const string SquawkVfr = "squawk.vfr";

    /// <summary>Squawk normal (<c>SQNORM</c>).</summary>
    public const string SquawkNormal = "squawk.normal";

    /// <summary>Squawk standby (<c>SQSBY</c>).</summary>
    public const string SquawkStandby = "squawk.standby";

    /// <summary>Ident.</summary>
    public const string SquawkIdent = "squawk.ident";

    /// <summary>Ask the pilot to say altitude (<c>SALT</c>).</summary>
    public const string AskPilotAltitude = "askpilot.altitude";

    /// <summary>Ask the pilot to say heading (<c>SHDG</c>).</summary>
    public const string AskPilotHeading = "askpilot.heading";

    /// <summary>Ask the pilot to say speed (<c>SSPD</c>).</summary>
    public const string AskPilotSpeed = "askpilot.speed";

    /// <summary>Ask the pilot to say Mach (<c>SMACH</c>).</summary>
    public const string AskPilotMach = "askpilot.mach";

    /// <summary>Ask the pilot to say position (<c>SPOS</c>).</summary>
    public const string AskPilotPosition = "askpilot.position";

    /// <summary>Ask the pilot to say the expected approach (<c>SEAPP</c>).</summary>
    public const string AskPilotExpectedApproach = "askpilot.expected-approach";

    /// <summary>Have the pilot say free text (<c>SAY</c>).</summary>
    public const string AskPilotCustom = "askpilot.custom";

    /// <summary>Release a departure (<c>RD</c>).</summary>
    public const string CoordinationRelease = "coordination.release";

    /// <summary>Hold a departure release (<c>RDH</c>).</summary>
    public const string CoordinationHold = "coordination.hold";

    /// <summary>Recall a departure release (<c>RDR</c>).</summary>
    public const string CoordinationRecall = "coordination.recall";

    /// <summary>Acknowledge a departure release (<c>RDACK</c>).</summary>
    public const string CoordinationAcknowledge = "coordination.acknowledge";

    /// <summary>Set the data-block scratchpad (<c>SP</c>).</summary>
    public const string DataBlockScratchpad = "datablock.scratchpad";

    /// <summary>Set the data-block note (<c>NOTE</c>).</summary>
    public const string DataBlockNote = "datablock.note";

    /// <summary>Set the data block's temporary altitude (<c>TEMPALT</c>).</summary>
    public const string DataBlockTempAltitude = "datablock.temp-altitude";

    /// <summary>Set the data block's cruise altitude (<c>CRUISE</c>).</summary>
    public const string DataBlockCruise = "datablock.cruise";

    /// <summary>Toggle the data-block annotation (<c>ANNOTATE</c>).</summary>
    public const string DataBlockAnnotate = "datablock.annotate";

    /// <summary>Warp the aircraft to a position, heading, altitude and speed (<c>WARP</c>).</summary>
    public const string SimControlWarp = "simcontrol.warp";

    /// <summary>Delete the aircraft (<c>DEL</c>).</summary>
    public const string SimControlDelete = "simcontrol.delete";

    /// <summary>Open the aircraft's flight-plan editor.</summary>
    public const string AircraftEditFlightPlan = "aircraft.edit-fp";

    /// <summary>Toggle the aircraft's data block between its full and mini forms.</summary>
    public const string DisplayMiniDataBlock = "display.mini-datablock";

    /// <summary>Put the aircraft's data block back on the student position it was dragged off.</summary>
    public const string DisplayResetDataBlockPosition = "display.reset-datablock-position";

    /// <summary>Show or hide the aircraft's nav route.</summary>
    public const string DisplayNavRoute = "display.nav-route";

    /// <summary>Latch the pending range/bearing measurement to the aircraft.</summary>
    public const string DisplayMeasure = "display.measure";

    /// <summary>Set the aircraft's leader-direction line, 1-9 (<c>LDR</c>).</summary>
    public const string DisplayLeaderDirection = "display.leader-direction";

    /// <summary>Draw a J-ring around the aircraft at a radius, or clear it (<c>JRING</c>).</summary>
    public const string DisplayJRing = "display.jring";

    /// <summary>Draw a cone from the aircraft at a length, or clear it (<c>CONE</c>).</summary>
    public const string DisplayCone = "display.cone";

    /// <summary>Blank the aircraft's data block (<c>BLANK</c>).</summary>
    public const string DisplayBlank = "display.blank";

    /// <summary>Unblank the aircraft's data block (<c>BLANKD</c>).</summary>
    public const string DisplayUnblank = "display.unblank";

    /// <summary>Fly present heading (<c>FPH</c>).</summary>
    public const string HeadingPresent = "heading.present";

    /// <summary>Fly a heading picked from the list (<c>FH</c>).</summary>
    public const string HeadingFly = "heading.fly";

    /// <summary>Turn left to a heading picked from the list (<c>TL</c>).</summary>
    public const string HeadingTurnLeft = "heading.turn-left";

    /// <summary>Turn right to a heading picked from the list (<c>TR</c>).</summary>
    public const string HeadingTurnRight = "heading.turn-right";

    /// <summary>Turn left by a number of degrees picked from the list (<c>LT</c>).</summary>
    public const string HeadingTurnLeftDegrees = "heading.turn-left-degrees";

    /// <summary>Turn right by a number of degrees picked from the list (<c>RT</c>).</summary>
    public const string HeadingTurnRightDegrees = "heading.turn-right-degrees";

    /// <summary>Climb or descend to an altitude picked from the list (<c>CM</c> above the current altitude, <c>DM</c> otherwise).</summary>
    public const string AltitudeMaintain = "altitude.maintain";

    /// <summary>Assign a speed picked from the list (<c>SPD</c>).</summary>
    public const string SpeedAssign = "speed.assign";

    /// <summary>Assign a typed speed (<c>SPD</c>).</summary>
    public const string SpeedCustom = "speed.custom";

    /// <summary>Resume normal speed (<c>RNS</c>).</summary>
    public const string SpeedNormal = "speed.normal";

    /// <summary>Reduce to final approach speed (<c>RFAS</c>).</summary>
    public const string SpeedFinalApproach = "speed.final-approach";

    /// <summary>Proceed direct to a fix (<c>DCT</c>).</summary>
    public const string NavigationDirectTo = "navigation.direct-to";

    /// <summary>Append a direct-to fix after the current one (<c>ADCT</c>).</summary>
    public const string NavigationAppendDirectTo = "navigation.append-direct-to";

    /// <summary>Draw a route for the aircraft on the surface.</summary>
    public const string NavigationDrawRoute = "navigation.draw-route";

    /// <summary>Hold at present position, left turns (<c>HPPL</c>).</summary>
    public const string HoldPresentLeft = "hold.present-left";

    /// <summary>Hold at present position, right turns (<c>HPPR</c>).</summary>
    public const string HoldPresentRight = "hold.present-right";

    /// <summary>Hold at a fix, left turns (<c>HFIXL</c>).</summary>
    public const string HoldFixLeft = "hold.fix-left";

    /// <summary>Hold at a fix, right turns (<c>HFIXR</c>).</summary>
    public const string HoldFixRight = "hold.fix-right";

    /// <summary>Clear the aircraft for an approach (<c>CAPP</c>).</summary>
    public const string ApproachCleared = "approach.cleared";

    /// <summary>Join an approach (<c>JAPP</c>).</summary>
    public const string ApproachJoin = "approach.join";

    /// <summary>Clear the aircraft for a straight-in approach (<c>CAPPSI</c>).</summary>
    public const string ApproachClearedStraightIn = "approach.cleared-straight-in";

    /// <summary>Join an approach straight in (<c>JAPPSI</c>).</summary>
    public const string ApproachJoinStraightIn = "approach.join-straight-in";

    /// <summary>Clear the aircraft for an approach, skipping the intercept checks (<c>CAPPF</c>).</summary>
    public const string ApproachClearedForce = "approach.cleared-force";

    /// <summary>Join an approach, skipping the intercept checks (<c>JAPPF</c>).</summary>
    public const string ApproachJoinForce = "approach.join-force";

    /// <summary>Join an approach's final approach course (<c>JFAC</c>).</summary>
    public const string ApproachJoinFinalCourse = "approach.join-final-course";

    /// <summary>Tell the pilot to expect an approach (<c>EAPP</c>).</summary>
    public const string ApproachExpect = "approach.expect";

    /// <summary>Clear the aircraft for a visual approach to a runway (<c>CVA</c>).</summary>
    public const string ApproachClearedVisual = "approach.cleared-visual";

    /// <summary>Ask the pilot to report the field in sight (<c>RFIS</c>).</summary>
    public const string ApproachReportFieldInSight = "approach.report-field-in-sight";

    /// <summary>Ask the pilot to report traffic in sight, optionally naming the target (<c>RTIS</c>).</summary>
    public const string ApproachReportTrafficInSight = "approach.report-traffic-in-sight";

    /// <summary>Ask the pilot to report turning base (<c>REPORT BASE</c>).</summary>
    public const string ApproachReportBase = "approach.report-base";

    /// <summary>Ask the pilot to report turning final (<c>REPORT FINAL</c>).</summary>
    public const string ApproachReportFinal = "approach.report-final";

    /// <summary>Ask the pilot to report turning crosswind (<c>REPORT CROSSWIND</c>).</summary>
    public const string ApproachReportCrosswind = "approach.report-crosswind";

    /// <summary>Ask the pilot to report turning downwind (<c>REPORT DOWNWIND</c>).</summary>
    public const string ApproachReportDownwind = "approach.report-downwind";

    /// <summary>Ask the pilot to report a typed distance on final (<c>REPORT {n} FINAL</c>).</summary>
    public const string ApproachReportNMileFinal = "approach.report-n-mile-final";

    /// <summary>Ask the pilot to report at a typed fix (<c>REPORT {fix}</c>).</summary>
    public const string ApproachReportAtFix = "approach.report-at-fix";

    /// <summary>Cancel the turning-base report (<c>REPORT OFF BASE</c>).</summary>
    public const string ApproachReportOffBase = "approach.report-off-base";

    /// <summary>Cancel the turning-final report (<c>REPORT OFF FINAL</c>).</summary>
    public const string ApproachReportOffFinal = "approach.report-off-final";

    /// <summary>Cancel the turning-crosswind report (<c>REPORT OFF CROSSWIND</c>).</summary>
    public const string ApproachReportOffCrosswind = "approach.report-off-crosswind";

    /// <summary>Cancel the turning-downwind report (<c>REPORT OFF DOWNWIND</c>).</summary>
    public const string ApproachReportOffDownwind = "approach.report-off-downwind";

    /// <summary>Cancel every pending report (<c>REPORT OFF</c>).</summary>
    public const string ApproachReportOffAll = "approach.report-off-all";
}
