namespace Yaat.Client.Views.Scope;

/// <summary>How much of a track's datablock is shown. Step 1 presents only <see cref="Full"/>.</summary>
public enum ScopeBlockLevel
{
    /// <summary>Limited datablock.</summary>
    Limited,

    /// <summary>Partial datablock.</summary>
    Partial,

    /// <summary>Full datablock.</summary>
    Full,

    /// <summary>Full datablock expanded by hover.</summary>
    Dwell,
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
}

/// <summary>What clicking a field does. Every step-1 field carries <see cref="None"/>.</summary>
public enum ScopeFieldAction
{
    /// <summary>The field is inert.</summary>
    None,
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
/// <param name="Level">Block level.</param>
/// <param name="BlockColor">
/// Colour role of the block as a whole: its leader and every <see cref="ScopeColorToken.Default"/> field.
/// </param>
/// <param name="Tint">The RPO tint that applies to the track whatever its highlight; it colours the symbol.</param>
/// <param name="Fields">Fields for every slot the template places, in template order.</param>
public sealed record TrackPresentation(
    string TemplateId,
    ScopeBlockLevel Level,
    ScopeBlockColor BlockColor,
    ScopeTintRole Tint,
    IReadOnlyList<ScopeField> Fields
);
