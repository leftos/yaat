namespace Yaat.Client.Views.Scope;

/// <summary>
/// The fixed, named places a datablock field can occupy. Each template places a subset of these into its
/// lines; a slot means the same datum in every template, while the text a template shows for it may differ.
/// </summary>
public enum ScopeFieldSlot
{
    /// <summary>ERAM column 0, row 2: the VCI while the viewer's sector is on frequency, a path in its cell.</summary>
    Vci,

    /// <summary>ERAM column 0, row 3: the not-your-control <c>R</c> while the track's owner is not the viewer's sector.</summary>
    NotYourControl,

    /// <summary>Aircraft callsign.</summary>
    Callsign,

    /// <summary>The <c>*</c> after a VFR callsign.</summary>
    VfrMark,

    /// <summary>The <c>(LDB)</c>/<c>(PDB)</c> marker after the callsign: the level the student's scope shows the track at.</summary>
    StudentLevelMark,

    /// <summary>The <c>PO</c> after a STARS callsign while the track has a pending point-out.</summary>
    PointoutMark,

    /// <summary>Reported altitude in hundreds of feet.</summary>
    Altitude,

    /// <summary>ERAM FDB line 2's altitude: assigned (Field B), its character 4, and the reported altitude (Field C).</summary>
    EramAltitude,

    /// <summary>
    /// The <c>≠</c> cell right after the ERAM altitude while the ERAM plan altitude and the filed (STARS) altitude differ,
    /// drawn as a path in its <see cref="TrackPresenter.EramArrowCell"/>.
    /// </summary>
    AltitudeMismatch,

    /// <summary>
    /// The dwell-only line under line 2 listing the other side's altitude while the two differ: <c>STARS 350</c> on the ERAM
    /// block, <c>ERAM 240</c> on the STARS block.
    /// </summary>
    OtherAltitude,

    /// <summary>Ground speed in tens of knots.</summary>
    GroundSpeed,

    /// <summary>The STARS <c>cwt/type</c> token.</summary>
    CwtType,

    /// <summary>A ground target's block: the aircraft type the pilot connected with, alone.</summary>
    AircraftType,

    /// <summary>The flashing <c>ID</c> while the transponder idents.</summary>
    Ident,

    /// <summary>ATPA in-trail distance to the aircraft ahead.</summary>
    Atpa,

    /// <summary>
    /// The line-3 status slot: STARS shows owner, handoff, point-out and scratchpads; ERAM one Field E text by priority
    /// (special-purpose code, handoff, recently accepted, beacon mismatch or <c>NONE</c>, <c>FRZN</c>, <c>CST</c>).
    /// </summary>
    StatusLine3,

    /// <summary>The ERAM HSF indicator after the line-3 status, drawn as a path in its <see cref="TrackPresenter.EramArrowCell"/>.</summary>
    HsfIndicator,

    /// <summary>The ERAM line 4's type or destination fallback, shown while no HSF value shows.</summary>
    Line4Notes,

    /// <summary>The ERAM line 4's HSF free text, shown alone whatever heading or speed is also set.</summary>
    Line4FreeText,

    /// <summary>The ERAM line 4's HSF heading, padded to four characters when a speed follows it.</summary>
    Line4Heading,

    /// <summary>The ERAM line 4's blank heading column before a speed shown alone; never clicked.</summary>
    Line4Pad,

    /// <summary>The ERAM line 4's HSF speed, in the column after the heading's.</summary>
    Line4Speed,

    /// <summary>
    /// Beacon-code mismatch: reported code then assigned code; the beacon code alone on the STARS LDB and on a ground target's
    /// block.
    /// </summary>
    Squawk,

    /// <summary>The struck-through <c>ModeC</c> while the transponder is in standby.</summary>
    ModeC,

    /// <summary>The flashing no-landing-clearance warning.</summary>
    NoLandingClearance,

    /// <summary>The flashing conflict-alert (CA/MCI) readout.</summary>
    ConflictAlert,

    /// <summary>The instructor's note.</summary>
    InstructorNote,

    /// <summary>EuroScope tag: controlling-RPO initials, or <c>--</c> when uncontrolled.</summary>
    Owner,

    /// <summary>EuroScope tag: the <c>type/cwt</c> token.</summary>
    TypeCwt,

    /// <summary>Destination airport: on the EuroScope tag, and on a ground target's block when it has a flight plan.</summary>
    Destination,

    /// <summary>EuroScope tag: assigned altitude, or its <c>(---)</c> placeholder.</summary>
    AssignedAltitude,

    /// <summary>EuroScope tag: assigned speed, or its <c>ASP</c> placeholder.</summary>
    AssignedSpeed,

    /// <summary>EuroScope tag: assigned heading, or its <c>AHDG</c> placeholder.</summary>
    AssignedHeading,

    /// <summary>EuroScope tag: assigned runway.</summary>
    AssignedRunway,

    /// <summary>EuroScope tag: primary scratchpad.</summary>
    Scratchpad1,

    /// <summary>EuroScope tag: secondary scratchpad.</summary>
    Scratchpad2,

    /// <summary>EuroScope tag: the flashing handoff target.</summary>
    Handoff,
}

/// <summary>Questions about a <see cref="ScopeFieldSlot"/>.</summary>
public static class ScopeFieldSlotExtensions
{
    /// <summary>Whether <paramref name="slot"/> is on the ERAM line 4: its fallback, free text, heading, pad or speed.</summary>
    public static bool IsLine4(this ScopeFieldSlot slot) =>
        slot
            is ScopeFieldSlot.Line4Notes
                or ScopeFieldSlot.Line4FreeText
                or ScopeFieldSlot.Line4Heading
                or ScopeFieldSlot.Line4Pad
                or ScopeFieldSlot.Line4Speed;
}
