namespace Yaat.Client.Views.Scope;

/// <summary>
/// The fixed, named places a datablock field can occupy. Each template places a subset of these into its
/// lines; a slot means the same datum in every template, while the text a template shows for it may differ.
/// </summary>
public enum ScopeFieldSlot
{
    /// <summary>Column-0 marks (the on-frequency VCI and the not-your-control <c>R</c>).</summary>
    Column0Marks,

    /// <summary>Aircraft callsign.</summary>
    Callsign,

    /// <summary>The <c>*</c> after a VFR callsign.</summary>
    VfrMark,

    /// <summary>Reported altitude in hundreds of feet.</summary>
    Altitude,

    /// <summary>Ground speed in tens of knots.</summary>
    GroundSpeed,

    /// <summary>The STARS <c>cwt/type</c> token.</summary>
    CwtType,

    /// <summary>The flashing <c>ID</c> while the transponder idents.</summary>
    Ident,

    /// <summary>ATPA in-trail distance to the aircraft ahead.</summary>
    Atpa,

    /// <summary>The line-3 status slot (owner, handoff, point-out and scratchpads in step 1).</summary>
    StatusLine3,

    /// <summary>The line-4 heading/speed/free-text notes.</summary>
    Line4Notes,

    /// <summary>Beacon-code mismatch: reported code then assigned code.</summary>
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

    /// <summary>EuroScope tag: destination airport.</summary>
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
