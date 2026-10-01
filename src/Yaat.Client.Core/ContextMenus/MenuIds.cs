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
}
