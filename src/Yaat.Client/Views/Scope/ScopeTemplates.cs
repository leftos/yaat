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
/// <param name="Column0">Marks drawn left of the text block on fixed block rows, or null when the template has no column 0.</param>
/// <param name="LimitedLines">The lines a Limited block draws, or null when it draws <paramref name="Lines"/>.</param>
/// <param name="PartialLines">The lines a Partial block draws, or null when it draws <paramref name="Lines"/>.</param>
/// <param name="DwellLines">The lines a Dwell block draws, or null when it draws <paramref name="Lines"/>.</param>
/// <param name="GroundLines">The lines a ground target's block draws, or null when it draws <paramref name="Lines"/>.</param>
public sealed record ScopeTemplate(
    string Id,
    ScopeGapMode GapMode,
    float FieldGap,
    float Padding,
    IReadOnlyList<ScopeTemplateLine> Lines,
    IReadOnlyList<ScopeColumn0Mark>? Column0,
    IReadOnlyList<ScopeTemplateLine>? LimitedLines = null,
    IReadOnlyList<ScopeTemplateLine>? PartialLines = null,
    IReadOnlyList<ScopeTemplateLine>? DwellLines = null,
    IReadOnlyList<ScopeTemplateLine>? GroundLines = null
)
{
    /// <summary>
    /// The lines a block at <paramref name="level"/> draws: <see cref="Lines"/> at Full, and at Limited, Partial, Dwell and
    /// Ground unless it has its own.
    /// </summary>
    public IReadOnlyList<ScopeTemplateLine> LinesAt(ScopeBlockLevel level) =>
        level switch
        {
            ScopeBlockLevel.Limited => LimitedLines ?? Lines,
            ScopeBlockLevel.Partial => PartialLines ?? Lines,
            ScopeBlockLevel.Dwell => DwellLines ?? Lines,
            ScopeBlockLevel.Ground => GroundLines ?? Lines,
            _ => Lines,
        };
}

/// <summary>One column-0 mark: a slot drawn left of the text block on a fixed row, whether or not a line takes that row.</summary>
/// <param name="Slot">The slot drawn.</param>
/// <param name="Row">The 1-based block row it sits on.</param>
public sealed record ScopeColumn0Mark(ScopeFieldSlot Slot, int Row);

/// <summary>The templates step 1 ships, looked up by id.</summary>
public static class ScopeTemplates
{
    /// <summary>Id of the STARS template.</summary>
    public const string StarsId = "stars";

    /// <summary>Id of the ERAM template.</summary>
    public const string EramId = "eram";

    /// <summary>Id of the EuroScope tag template.</summary>
    public const string EuroScopeId = "euroscope";

    /// <summary>
    /// Id of the Mixed pick: a routing id, not a template. Each track draws the template of its owner's display kind
    /// (<see cref="ScopeOwnerKind.Resolve"/>), so <see cref="Get"/> has no template for it.
    /// </summary>
    public const string MixedId = "mixed";

    /// <summary>
    /// The STARS full datablock: callsign (with the VFR mark and the student-level marker), then altitude, ground speed, cwt/type,
    /// the ATPA in-trail distance (at line 2's right end, as CRC places it) and the ident, then the squawk-mismatch, status,
    /// ModeC, warning, conflict and note lines. Limited and Partial draw the collapsed blocks
    /// (<see cref="Map.DatablockText.BuildCollapsedLines"/>): the beacon code (in the squawk
    /// slot), altitude and ident; or the altitude, handoff, ground speed and ident, then scratchpad 1. Dwell adds the ERAM
    /// altitude's line under line 2 while the two sides differ. Ground draws the ground target's block in top-down mode.
    /// </summary>
    public static readonly ScopeTemplate Stars = WithDwellLine(
        new(
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
                    Steady(ScopeFieldSlot.Atpa),
                    Flashing(ScopeFieldSlot.Ident)
                ),
                Line(false, Steady(ScopeFieldSlot.Squawk)),
                Line(false, Flashing(ScopeFieldSlot.StatusLine3)),
                Line(false, Steady(ScopeFieldSlot.ModeC)),
                Line(false, Flashing(ScopeFieldSlot.NoLandingClearance)),
                Line(false, Flashing(ScopeFieldSlot.ConflictAlert)),
                Line(false, Steady(ScopeFieldSlot.InstructorNote)),
            ],
            Column0: null,
            LimitedLines: [Line(true, Steady(ScopeFieldSlot.Squawk), Steady(ScopeFieldSlot.Altitude), Flashing(ScopeFieldSlot.Ident))],
            PartialLines:
            [
                Line(
                    true,
                    Steady(ScopeFieldSlot.Altitude),
                    Steady(ScopeFieldSlot.Handoff),
                    Steady(ScopeFieldSlot.GroundSpeed),
                    Flashing(ScopeFieldSlot.Ident)
                ),
                Line(false, Steady(ScopeFieldSlot.Scratchpad1)),
            ],
            GroundLines: GroundTargetLines()
        )
    );

    /// <summary>
    /// The ERAM datablock: line 1 as STARS; line 2 the ERAM altitude (Field B, its character 4 and Field C) and the
    /// altitude-mismatch cell flush after it, followed by the STARS right side (ground speed, cwt/type, the ATPA in-trail
    /// distance, then the ident); line 3 the status text and the HSF indicator; line 4 one of the HSF free text, the HSF
    /// heading and speed tokens (a blank pad before a speed shown alone), or the type/destination fallback; then the ModeC,
    /// warning, conflict and note lines. Column 0 holds the VCI mark on row 2 and the not-your-control <c>R</c> on row 3.
    /// Limited draws CRC's paired LDB (the aircraft id, the padded CERA or Mode C altitude,
    /// the special-purpose code) and Partial its enhanced paired LDB (the aircraft id, the ERAM altitude and its mismatch cell,
    /// the special-purpose code); every placement is reserved, since the whole LDB blinks while a special code shows. Dwell
    /// adds the STARS altitude's line under line 2 while the two sides differ. Ground draws the ground target's block in
    /// top-down mode.
    /// </summary>
    public static readonly ScopeTemplate Eram = WithDwellLine(
        new(
            EramId,
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
                    Steady(ScopeFieldSlot.EramAltitude),
                    new ScopeSlotPlacement(ScopeFieldSlot.AltitudeMismatch, "", false),
                    Steady(ScopeFieldSlot.GroundSpeed),
                    Steady(ScopeFieldSlot.CwtType),
                    Steady(ScopeFieldSlot.Atpa),
                    Flashing(ScopeFieldSlot.Ident)
                ),
                Line(false, Flashing(ScopeFieldSlot.StatusLine3), new ScopeSlotPlacement(ScopeFieldSlot.HsfIndicator, "", false)),
                Line(
                    false,
                    Steady(ScopeFieldSlot.Line4FreeText),
                    Steady(ScopeFieldSlot.Line4Heading),
                    new ScopeSlotPlacement(ScopeFieldSlot.Line4Pad, "", false),
                    new ScopeSlotPlacement(ScopeFieldSlot.Line4Speed, " ", false),
                    Steady(ScopeFieldSlot.Line4Notes)
                ),
                Line(false, Steady(ScopeFieldSlot.ModeC)),
                Line(false, Flashing(ScopeFieldSlot.NoLandingClearance)),
                Line(false, Flashing(ScopeFieldSlot.ConflictAlert)),
                Line(false, Steady(ScopeFieldSlot.InstructorNote)),
            ],
            Column0: [new(ScopeFieldSlot.Vci, 2), new(ScopeFieldSlot.NotYourControl, 3)],
            LimitedLines: [Line(true, Flashing(ScopeFieldSlot.Callsign)), Line(false, Flashing(ScopeFieldSlot.Altitude)), SpcLine()],
            PartialLines:
            [
                Line(true, Flashing(ScopeFieldSlot.Callsign)),
                Line(false, Flashing(ScopeFieldSlot.EramAltitude), new ScopeSlotPlacement(ScopeFieldSlot.AltitudeMismatch, "", true)),
                SpcLine(),
            ],
            GroundLines: GroundTargetLines()
        )
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
        ],
        Column0: null
    );

    /// <summary>Returns the template with <paramref name="id"/>.</summary>
    /// <exception cref="ArgumentException">No template has that id.</exception>
    public static ScopeTemplate Get(string id) =>
        id switch
        {
            StarsId => Stars,
            EramId => Eram,
            EuroScopeId => EuroScope,
            _ => throw new ArgumentException($"Unknown scope template id '{id}'.", nameof(id)),
        };

    /// <summary>Whether <paramref name="id"/> is a pick the scope's Template menu offers: a template id or <see cref="MixedId"/>.</summary>
    public static bool IsSelectable(string id) => id is StarsId or EramId or EuroScopeId or MixedId;

    // The template with its Dwell lines: its Full lines with the other-altitude line inserted under line 2.
    private static ScopeTemplate WithDwellLine(ScopeTemplate template) =>
        template with
        {
            DwellLines = [.. template.Lines.Take(2), Line(false, Steady(ScopeFieldSlot.OtherAltitude)), .. template.Lines.Skip(2)],
        };

    private static ScopeSlotPlacement Steady(ScopeFieldSlot slot) => new(slot, " ", false);

    private static ScopeSlotPlacement Flashing(ScopeFieldSlot slot) => new(slot, " ", true);

    private static ScopeTemplateLine Line(bool alwaysShown, params ScopeSlotPlacement[] slots) => new(slots, alwaysShown, TrailingGapInBounds: true);

    // CRC's ground target data block (STARS and ERAM alike): the aircraft ID; then the destination, or else the beacon code, and the type.
    private static ScopeTemplateLine[] GroundTargetLines() =>
        [
            Line(true, Steady(ScopeFieldSlot.Callsign)),
            Line(true, Steady(ScopeFieldSlot.Destination), Steady(ScopeFieldSlot.Squawk), Steady(ScopeFieldSlot.AircraftType)),
        ];

    // ERAM LDB line 3: the special-purpose-code label in the status slot; it takes no row when there is none.
    private static ScopeTemplateLine SpcLine() => Line(false, Flashing(ScopeFieldSlot.StatusLine3));

    private static ScopeTemplateLine WarningLine(ScopeFieldSlot slot) => new([Flashing(slot)], AlwaysShown: false, TrailingGapInBounds: false);
}
