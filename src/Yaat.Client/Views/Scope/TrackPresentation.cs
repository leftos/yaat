namespace Yaat.Client.Views.Scope;

/// <summary>
/// How much of a track's datablock is shown: an input to <see cref="TrackPresenter.Present"/>, chosen per track by
/// <see cref="ScopeFrame.Build"/>. Each template draws its own lines per level (<see cref="ScopeTemplate.LinesAt"/>).
/// </summary>
public enum ScopeBlockLevel
{
    /// <summary>Limited datablock: ERAM's paired LDB, the STARS LDB; EuroScope draws its full tag.</summary>
    Limited,

    /// <summary>Partial datablock: ERAM's enhanced paired LDB, the STARS PDB; EuroScope draws its full tag.</summary>
    Partial,

    /// <summary>Full datablock.</summary>
    Full,

    /// <summary>A hovered block: the Full block's content, drawn on top with the dwell emphasis box and without the stale alpha.</summary>
    Dwell,

    /// <summary>
    /// A ground target's block in top-down mode (STARS and ERAM): the aircraft ID, then the destination or beacon code and the
    /// pilot's aircraft type, the same at every zoom level and on dwell.
    /// </summary>
    Ground,
}

/// <summary>A named colour role; the renderer maps each token to a colour and brightness group.</summary>
public enum ScopeColorToken
{
    /// <summary>The block's own colour.</summary>
    Default,

    /// <summary>Instructor note (amber today).</summary>
    Note,

    /// <summary>No-landing-clearance warning (red today).</summary>
    NoLandingClearance,

    /// <summary>Conflict alert.</summary>
    ConflictAlert,

    /// <summary>ATPA in-trail distance in the warning cone state.</summary>
    AtpaWarning,

    /// <summary>ATPA in-trail distance in the alert cone state.</summary>
    AtpaAlert,

    /// <summary>The STARS cwt/type token of an aircraft whose actual type differs from its filed type (amber today).</summary>
    TypeMismatch,

    /// <summary>Altitude below the MVA floor by more than the at-band (red today).</summary>
    MvaBelow,

    /// <summary>Altitude within the at-band of the MVA floor (amber today).</summary>
    MvaAt,
}

/// <summary>A track block's colour role; the renderer resolves it through the frame's palette.</summary>
public enum ScopeBlockColor
{
    /// <summary>White.</summary>
    Default,

    /// <summary>The student's scope owns the track (white).</summary>
    StudentOwned,

    /// <summary>The student's scope does not own the track (lime green).</summary>
    StudentUnowned,

    /// <summary>The track is pointed out to the student's scope (yellow).</summary>
    StudentPointout,

    /// <summary>The student's scope highlights the track (cyan).</summary>
    StudentHighlighted,

    /// <summary>Assigned to the user: the user's assignment tint.</summary>
    AssignedToMe,

    /// <summary>Assigned to someone else or no one: the user's unassigned tint.</summary>
    AssignedToOther,

    /// <summary>Middle-click highlight (cyan).</summary>
    Highlighted,
}

/// <summary>Which of the user's RPO tints applies to a track; the renderer resolves it through the frame's palette.</summary>
public enum ScopeTintRole
{
    /// <summary>No tint applies.</summary>
    None,

    /// <summary>Assigned to the user: the user's assignment tint.</summary>
    AssignedToMe,

    /// <summary>Assigned to someone else or no one: the user's unassigned tint.</summary>
    AssignedToOther,
}

/// <summary>How the renderer draws a field's text beyond its colour.</summary>
[Flags]
public enum ScopeFieldStyle
{
    /// <summary>Plain text.</summary>
    None = 0,

    /// <summary>The text after the first space draws at a quarter of the field colour's brightness (the squawk pulse's off-phase).</summary>
    DimTail = 1,

    /// <summary>A line of the field colour across the text at the font's strikeout position.</summary>
    Strikethrough = 2,

    /// <summary>An up arrow drawn as a path in the text's <see cref="TrackPresenter.EramArrowCell"/> (ERAM climbing to its altitude).</summary>
    ArrowUp = 4,

    /// <summary>A down arrow drawn as a path in the text's <see cref="TrackPresenter.EramArrowCell"/> (ERAM descending to its altitude).</summary>
    ArrowDown = 8,

    /// <summary>The ERAM HSF indicator ↴ drawn as a path in the text's <see cref="TrackPresenter.EramArrowCell"/>.</summary>
    HsfIndicator = 16,

    /// <summary>The ERAM VCI mark drawn as a path in the text's <see cref="TrackPresenter.EramArrowCell"/>.</summary>
    Vci = 32,

    /// <summary>The altitude-mismatch <c>≠</c> drawn as a path in the text's <see cref="TrackPresenter.EramArrowCell"/>.</summary>
    AltitudeMismatch = 64,
}

/// <summary>
/// What a left click on a field does besides selecting its track: the presenter sets it per template and slot
/// (<see cref="TrackPresenter.ActionFor"/>). The ERAM Field A dwell-lock click is not one: the canvas decides it.
/// </summary>
public enum ScopeFieldAction
{
    /// <summary>The click only selects the track.</summary>
    None,

    /// <summary>Opens the altitude pick menu.</summary>
    OpenAltitudeMenu,

    /// <summary>Opens the speed pick menu.</summary>
    OpenSpeedMenu,

    /// <summary>Opens the heading rose.</summary>
    OpenHeadingMenu,

    /// <summary>Opens the squawk pick menu.</summary>
    OpenSquawkMenu,

    /// <summary>Opens the runway pick menu.</summary>
    OpenRunwayMenu,

    /// <summary>Opens the scratchpad pick menu.</summary>
    OpenScratchpadMenu,

    /// <summary>Opens the handoff menu, or the accept for a handoff inbound.</summary>
    OpenHandoffMenu,

    /// <summary>Opens the instructor note box.</summary>
    OpenNoteBox,

    /// <summary>Takes control of the track.</summary>
    TakeControl,

    /// <summary>Toggles the track's HSF display, as an ERAM QS entry.</summary>
    ToggleHsf,

    /// <summary>Toggles the track's VCI, as an ERAM QN // entry.</summary>
    ToggleVci,

    /// <summary>Opens the ERAM altitude popup: its scope-data entries beside the pilot's Maintain altitude panel.</summary>
    OpenEramAltitudeMenu,

    /// <summary>Opens the ERAM heading popup: its HSF heading entries beside the pilot's Fly heading panel.</summary>
    OpenEramHeadingMenu,

    /// <summary>Opens the ERAM speed popup: its HSF speed entries beside the pilot's Assign speed panel.</summary>
    OpenEramSpeedMenu,

    /// <summary>Opens the ERAM free-text popup: its HSF free-text entries alone, with no pilot panel.</summary>
    OpenEramFreeTextMenu,

    /// <summary>Opens the ERAM heading and speed popup: both HSF sections beside Fly heading above Assign speed.</summary>
    OpenEramHsfMenu,
}

/// <summary>
/// One datablock field as the presenter decided it.
/// </summary>
/// <param name="Slot">Where the field sits.</param>
/// <param name="Text">Text to draw this frame; empty during a flashing field's off-phase.</param>
/// <param name="StableText">Text the field shows on its on-phase, which a reserved slot measures so rects never pulse.</param>
/// <param name="Token">Colour role of the field.</param>
/// <param name="Action">What clicking the field does.</param>
/// <param name="Style">How the text draws beyond its colour; it never changes the field's rect.</param>
public sealed record ScopeField(
    ScopeFieldSlot Slot,
    string Text,
    string StableText,
    ScopeColorToken Token,
    ScopeFieldAction Action,
    ScopeFieldStyle Style
);

/// <summary>
/// Everything a template needs to lay out one track's datablock, produced by <see cref="TrackPresenter.Present"/>.
/// </summary>
/// <param name="TemplateId">Id of the template the fields were presented for.</param>
/// <param name="Level">The block level presented (the input's).</param>
/// <param name="BlockColor">
/// Colour role of the block as a whole: its leader and every <see cref="ScopeColorToken.Default"/> field.
/// </param>
/// <param name="Tint">The RPO tint that applies to the track whatever its highlight; it colours the symbol.</param>
/// <param name="Fields">Fields for every slot the template places at <paramref name="Level"/>, in template order.</param>
/// <param name="ShowsLeader">Whether the block draws its leader; CRC's ERAM LDBs have none.</param>
public sealed record TrackPresentation(
    string TemplateId,
    ScopeBlockLevel Level,
    ScopeBlockColor BlockColor,
    ScopeTintRole Tint,
    IReadOnlyList<ScopeField> Fields,
    bool ShowsLeader = true
);
