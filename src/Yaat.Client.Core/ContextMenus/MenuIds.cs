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

    /// <summary>Check the call-for-release window (<c>CFR CHECK</c>).</summary>
    public const string CoordinationCheckReleaseWindow = "coordination.check-release-window";

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

    /// <summary>Join a STAR (<c>JARR</c>).</summary>
    public const string ProceduresJoinStar = "procedures.join-star";

    /// <summary>Climb via the SID (<c>CVIA</c>).</summary>
    public const string ProceduresClimbViaSid = "procedures.climb-via-sid";

    /// <summary>Descend via the STAR (<c>DVIA</c>).</summary>
    public const string ProceduresDescendViaStar = "procedures.descend-via-star";

    /// <summary>Cross a fix (<c>CFIX</c>).</summary>
    public const string ProceduresCrossFix = "procedures.cross-fix";

    /// <summary>Depart a fix (<c>DEPART</c>).</summary>
    public const string ProceduresDepartFix = "procedures.depart-fix";

    /// <summary>Position, turn, altitude and approach clearance in one (<c>PTAC</c>).</summary>
    public const string ProceduresPtac = "procedures.ptac";

    /// <summary>Join an airway (<c>JAWY</c>).</summary>
    public const string ProceduresJoinAirway = "procedures.join-airway";

    /// <summary>Join a radial outbound from a fix (<c>JRADO</c>).</summary>
    public const string ProceduresJoinRadialOutbound = "procedures.join-radial-outbound";

    /// <summary>Join a radial inbound to a fix (<c>JRADI</c>).</summary>
    public const string ProceduresJoinRadialInbound = "procedures.join-radial-inbound";

    /// <summary>Line up and wait (<c>LUAW</c>).</summary>
    public const string TowerLineUpAndWait = "tower.line-up-and-wait";

    /// <summary>Cleared for takeoff (<c>CTO</c>, with or without a departure instruction).</summary>
    public const string TowerClearedForTakeoff = "tower.cto";

    /// <summary>Cancel the takeoff clearance (<c>CTOC</c>).</summary>
    public const string TowerCancelTakeoff = "tower.cancel-takeoff";

    /// <summary>Cleared to land (<c>CLAND</c>).</summary>
    public const string TowerClearedToLand = "tower.cleared-to-land";

    /// <summary>Force a landing regardless of the energy state (<c>CLANDF</c>).</summary>
    public const string TowerForceLanding = "tower.force-landing";

    /// <summary>Cleared for the option (<c>COPT</c>).</summary>
    public const string TowerClearedOption = "tower.cleared-option";

    /// <summary>Touch and go (<c>TG</c>).</summary>
    public const string TowerTouchAndGo = "tower.touch-and-go";

    /// <summary>Stop and go (<c>SG</c>).</summary>
    public const string TowerStopAndGo = "tower.stop-and-go";

    /// <summary>Low approach (<c>LA</c>).</summary>
    public const string TowerLowApproach = "tower.low-approach";

    /// <summary>Go around (<c>GA</c>).</summary>
    public const string TowerGoAround = "tower.go-around";

    /// <summary>Cancel the landing clearance (<c>CLC</c>).</summary>
    public const string TowerCancelLanding = "tower.cancel-landing";

    /// <summary>Exit the runway to the left (<c>EL</c>).</summary>
    public const string TowerExitLeft = "tower.exit-left";

    /// <summary>Exit the runway to the right (<c>ER</c>).</summary>
    public const string TowerExitRight = "tower.exit-right";

    /// <summary>Enter a left downwind (<c>ELD</c>, with or without a runway).</summary>
    public const string PatternEnterLeftDownwind = "pattern.enter-left-downwind";

    /// <summary>Enter a right downwind (<c>ERD</c>, with or without a runway).</summary>
    public const string PatternEnterRightDownwind = "pattern.enter-right-downwind";

    /// <summary>Enter a left base (<c>ELB</c>, with or without a runway).</summary>
    public const string PatternEnterLeftBase = "pattern.enter-left-base";

    /// <summary>Enter a right base (<c>ERB</c>, with or without a runway).</summary>
    public const string PatternEnterRightBase = "pattern.enter-right-base";

    /// <summary>Enter a straight-in final (<c>EF</c>, with or without a runway).</summary>
    public const string PatternEnterFinal = "pattern.enter-final";

    /// <summary>Turn crosswind (<c>TC</c>).</summary>
    public const string PatternTurnCrosswind = "pattern.turn-crosswind";

    /// <summary>Turn downwind (<c>TD</c>).</summary>
    public const string PatternTurnDownwind = "pattern.turn-downwind";

    /// <summary>Turn base (<c>TB</c>).</summary>
    public const string PatternTurnBase = "pattern.turn-base";

    /// <summary>Extend the pattern leg (<c>EXT</c>).</summary>
    public const string PatternExtend = "pattern.extend";

    /// <summary>Make a short approach (<c>MSA</c>).</summary>
    public const string PatternShortApproach = "pattern.short-approach";

    /// <summary>Make a normal approach (<c>MNA</c>).</summary>
    public const string PatternNormalApproach = "pattern.normal-approach";

    /// <summary>Make a left 360 (<c>L360</c>).</summary>
    public const string PatternLeft360 = "pattern.left-360";

    /// <summary>Make a right 360 (<c>R360</c>).</summary>
    public const string PatternRight360 = "pattern.right-360";

    /// <summary>Make a left 270 (<c>L270</c>).</summary>
    public const string PatternLeft270 = "pattern.left-270";

    /// <summary>Make a right 270 (<c>R270</c>).</summary>
    public const string PatternRight270 = "pattern.right-270";

    /// <summary>Plan a 270 at the next turn (<c>P270</c>).</summary>
    public const string PatternPlan270 = "pattern.plan-270";

    /// <summary>Cancel a planned 270 (<c>NO270</c>).</summary>
    public const string PatternCancel270 = "pattern.cancel-270";

    /// <summary>Circle the airport (<c>CA</c>).</summary>
    public const string PatternCircleAirport = "pattern.circle-airport";

    /// <summary>Push back from the stand (<c>PUSH</c>).</summary>
    public const string GroundPushback = "ground.pushback";

    /// <summary>Hold position on the ground (<c>HP</c>).</summary>
    public const string GroundHoldPosition = "ground.hold-position";

    /// <summary>Resume taxi from a hold-short or a stationary hold (<c>RES</c>).</summary>
    public const string GroundResumeTaxi = "ground.resume-taxi";

    /// <summary>Cross the runway being held short of (<c>CROSS</c>).</summary>
    public const string GroundCrossRunway = "ground.cross-runway";

    /// <summary>Override the ground-conflict speed limit (<c>BREAK</c>).</summary>
    public const string GroundBreakConflict = "ground.break-conflict";

    /// <summary>Hold short of a runway or taxiway on the taxi route (<c>HS</c>), from the route's targets.</summary>
    public const string GroundHoldShort = "ground.hold-short";

    /// <summary>Follow another ground aircraft (<c>FOLLOWG</c>), from the nearest ground traffic.</summary>
    public const string GroundFollow = "ground.follow";

    /// <summary>Give way to another ground aircraft (<c>GW</c>), from the nearest ground traffic.</summary>
    public const string GroundGiveWay = "ground.give-way";

    /// <summary>Push back to a magnetic facing (<c>PUSH FACE</c>), one flat item per taxiway leaving the stand.</summary>
    public const string GroundPushbackFace = "ground.pushback-face";

    /// <summary>Push back to a named parking, spot or helipad node (<c>PUSH @name</c> / <c>PUSH $name</c>), nearest first.</summary>
    public const string GroundPushbackTo = "ground.pushback-to";

    /// <summary>Draw a tug move on the surface (<c>PUSH</c> / <c>PUSHM</c> to the drawn targets).</summary>
    public const string GroundPushRoute = "ground.push-route";

    /// <summary>Taxi along one of the airport's preset routes (<c>TAXI</c>), the ones walkable from the aircraft's node.</summary>
    public const string GroundTaxiPreset = "ground.taxi-preset";

    /// <summary>Draw a taxi route on the surface for the aircraft.</summary>
    public const string GroundDrawTaxiRoute = "ground.draw-taxi-route";

    /// <summary>The selected aircraft follows the right-clicked one on the ground (<c>FOLLOWG</c>).</summary>
    public const string GroundRelativeFollow = "ground.relative-follow";

    /// <summary>The selected aircraft gives way to the right-clicked one on the ground (<c>GW</c>).</summary>
    public const string GroundRelativeGiveWay = "ground.relative-give-way";

    /// <summary>Choose how the aircraft's taxi route is drawn: always shown, always hidden, or following the global setting.</summary>
    public const string DisplayTaxiRoute = "display.taxi-route";

    /// <summary>Hide the aircraft's data block, or show it again.</summary>
    public const string DisplayHideDataBlock = "display.hide-datablock";
}
