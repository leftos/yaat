namespace Yaat.Client.Views.Scope;

/// <summary>How a template spaces the fields on one line.</summary>
public enum ScopeGapMode
{
    /// <summary>Fields join into one text run with each placement's separator; positions are measured on the run.</summary>
    TextSeparator,

    /// <summary>Fields sit a fixed pixel gap apart, each measured alone.</summary>
    PixelGap,
}

/// <summary>One slot's place on a template line.</summary>
/// <param name="Slot">The slot placed.</param>
/// <param name="Separator">Text put between this field and the one before it on the line.</param>
/// <param name="Reserved">Whether the slot keeps its on-phase width while its text flashes off.</param>
public sealed record ScopeSlotPlacement(ScopeFieldSlot Slot, string Separator, bool Reserved);

/// <summary>One template line.</summary>
/// <param name="Slots">The slots on the line, left to right.</param>
/// <param name="AlwaysShown">Whether the line keeps its row when none of its fields has text.</param>
/// <param name="TrailingGapInBounds">
/// Whether the block width counts the template's <see cref="ScopeTemplate.FieldGap"/> after the line's last field,
/// as today's EuroScope tag does for every line but its flashing warning and conflict lines.
/// </param>
public sealed record ScopeTemplateLine(IReadOnlyList<ScopeSlotPlacement> Slots, bool AlwaysShown, bool TrailingGapInBounds);

/// <summary>A datablock template: the slots it places and how it spaces them.</summary>
/// <param name="Id">Template id a profile names (<c>"stars"</c>, <c>"euroscope"</c>).</param>
/// <param name="GapMode">How fields on a line are spaced.</param>
/// <param name="FieldGap">Pixel gap after each field when <see cref="GapMode"/> is <see cref="ScopeGapMode.PixelGap"/>.</param>
/// <param name="Padding">Padding between the text and the block bounds.</param>
/// <param name="Lines">The template's lines, top to bottom.</param>
public sealed record ScopeTemplate(string Id, ScopeGapMode GapMode, float FieldGap, float Padding, IReadOnlyList<ScopeTemplateLine> Lines);

/// <summary>The templates step 1 ships, looked up by id.</summary>
public static class ScopeTemplates
{
    /// <summary>Id of the STARS template.</summary>
    public const string StarsId = "stars";

    /// <summary>Id of the EuroScope tag template.</summary>
    public const string EuroScopeId = "euroscope";

    /// <summary>
    /// The STARS full datablock: callsign (with the VFR mark and the student-level marker), then altitude, ground speed, cwt/type and the
    /// ident, then the squawk-mismatch, status, ATPA, ModeC, warning, conflict and note lines.
    /// </summary>
    public static readonly ScopeTemplate Stars = new(
        StarsId,
        ScopeGapMode.TextSeparator,
        FieldGap: 0f,
        Padding: 3f,
        [
            Line(
                true,
                Steady(ScopeFieldSlot.Callsign),
                new ScopeSlotPlacement(ScopeFieldSlot.VfrMark, "", false),
                new ScopeSlotPlacement(ScopeFieldSlot.StudentLevelMark, "", false)
            ),
            Line(
                true,
                Steady(ScopeFieldSlot.Altitude),
                Steady(ScopeFieldSlot.GroundSpeed),
                Steady(ScopeFieldSlot.CwtType),
                Flashing(ScopeFieldSlot.Ident)
            ),
            Line(false, Steady(ScopeFieldSlot.Squawk)),
            Line(false, Flashing(ScopeFieldSlot.StatusLine3)),
            Line(false, Steady(ScopeFieldSlot.Atpa)),
            Line(false, Steady(ScopeFieldSlot.ModeC)),
            Line(false, Flashing(ScopeFieldSlot.NoLandingClearance)),
            Line(false, Flashing(ScopeFieldSlot.ConflictAlert)),
            Line(false, Steady(ScopeFieldSlot.InstructorNote)),
        ]
    );

    /// <summary>
    /// The EuroScope tag: owner and callsign; type/cwt, destination and ident; current and assigned altitude,
    /// assigned speed and heading; runway, scratchpads and handoff; then the squawk, ModeC, warning, conflict
    /// and note lines.
    /// </summary>
    public static readonly ScopeTemplate EuroScope = new(
        EuroScopeId,
        ScopeGapMode.PixelGap,
        FieldGap: 6f,
        Padding: 3f,
        [
            Line(true, Steady(ScopeFieldSlot.Owner), Steady(ScopeFieldSlot.Callsign)),
            Line(true, Steady(ScopeFieldSlot.TypeCwt), Steady(ScopeFieldSlot.Destination), Flashing(ScopeFieldSlot.Ident)),
            Line(
                true,
                Steady(ScopeFieldSlot.Altitude),
                Steady(ScopeFieldSlot.AssignedAltitude),
                Steady(ScopeFieldSlot.AssignedSpeed),
                Steady(ScopeFieldSlot.AssignedHeading)
            ),
            Line(
                false,
                Steady(ScopeFieldSlot.AssignedRunway),
                Steady(ScopeFieldSlot.Scratchpad1),
                Steady(ScopeFieldSlot.Scratchpad2),
                Flashing(ScopeFieldSlot.Handoff)
            ),
            Line(false, Steady(ScopeFieldSlot.Squawk)),
            Line(false, Steady(ScopeFieldSlot.ModeC)),
            WarningLine(ScopeFieldSlot.NoLandingClearance),
            WarningLine(ScopeFieldSlot.ConflictAlert),
            Line(false, Steady(ScopeFieldSlot.InstructorNote)),
        ]
    );

    /// <summary>Returns the template with <paramref name="id"/>.</summary>
    /// <exception cref="ArgumentException">No template has that id.</exception>
    public static ScopeTemplate Get(string id) =>
        id switch
        {
            StarsId => Stars,
            EuroScopeId => EuroScope,
            _ => throw new ArgumentException($"Unknown scope template id '{id}'.", nameof(id)),
        };

    private static ScopeSlotPlacement Steady(ScopeFieldSlot slot) => new(slot, " ", false);

    private static ScopeSlotPlacement Flashing(ScopeFieldSlot slot) => new(slot, " ", true);

    private static ScopeTemplateLine Line(bool alwaysShown, params ScopeSlotPlacement[] slots) => new(slots, alwaysShown, TrailingGapInBounds: true);

    private static ScopeTemplateLine WarningLine(ScopeFieldSlot slot) => new([Flashing(slot)], AlwaysShown: false, TrailingGapInBounds: false);
}
