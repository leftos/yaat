using SkiaSharp;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Radar;

/// <summary>
/// The alert overlays a full datablock may carry, bundled so the draw and hit-test paths pass them as one
/// unit. Each preference travels with the peer aircraft it needs: the conflict and ATPA fields print live
/// separation/in-trail values derived from the other member's position, so a set flag with a null peer
/// still reserves the field but has nothing to measure against.
/// </summary>
/// <param name="ShowConflictAlerts">Conflict-alert (CA/MCI) display preference.</param>
/// <param name="ConflictPeer">Other member of this aircraft's conflict pair, or null when it isn't on the scope.</param>
/// <param name="ShowAtpa">ATPA in-trail display preference.</param>
/// <param name="AtpaLead">Aircraft immediately ahead in the ATPA pairing, or null when it isn't on the scope.</param>
internal readonly record struct DatablockOverlays(bool ShowConflictAlerts, AircraftModel? ConflictPeer, bool ShowAtpa, AircraftModel? AtpaLead);

/// <summary>
/// Where and how a full datablock draws — its text origin, the style it measures in, and the
/// no-landing-clearance warning preference — bundled so <see cref="RadarDatablockLayout.Compute"/>
/// takes one positional argument for them.
/// </summary>
/// <param name="BlockX">Text origin X, in canvas coordinates.</param>
/// <param name="BlockY">Text origin Y — the first line's baseline.</param>
/// <param name="Style">Style the block measures its lines in.</param>
/// <param name="ShowNoLandingClearance">Whether the NoLndgClnc warning may draw.</param>
internal readonly record struct DatablockPlacement(float BlockX, float BlockY, TextStyle Style, bool ShowNoLandingClearance);

/// <summary>
/// Layout result for the radar full datablock. Pure: shared by renderer (draw) and
/// hit-test paths so geometry can be computed once.
/// </summary>
internal readonly struct RadarDatablockLayout
{
    private const float Pad = 3f;

    public readonly SKRect Rect;
    public readonly float TextX;
    public readonly float TextY;
    public readonly float LineHeight;
    public readonly string Line1;
    public readonly string Line2;
    public readonly string Line3;
    public readonly string Line4;
    public readonly string Line5;

    /// <summary>
    /// Beacon-code mismatch line <c>"{reported} {assigned}"</c> (e.g. <c>"1200 0301"</c>), drawn right
    /// below the altitude/speed line, or empty when the codes match / the mismatch is suppressed. The
    /// renderer draws the reported code solid and the assigned code dim-pulsing, emulating CRC STARS.
    /// </summary>
    public readonly string SquawkLine;

    /// <summary>
    /// ATPA in-trail distance to the aircraft ahead, in nautical miles to one decimal (e.g. <c>"3.2"</c>),
    /// drawn steady (never flashing) right below the owner/scratchpad line and coloured by the pairing's
    /// cone state. Empty when this aircraft is not the trailing member of an ATPA pairing, when the lead
    /// isn't resolvable, or when the <c>ShowAtpa</c> preference is off.
    /// </summary>
    public readonly string AtpaLine;

    /// <summary>Instructor note line (amber), drawn at the bottom of the block. Empty when no note.</summary>
    public readonly string Line6;

    /// <summary>
    /// Conflict-alert field (<c>"CA"</c>), drawn red and flashing on the 500 ms cycle while this
    /// aircraft is a member of an active conflict pair. Empty during the off-phase of the flash and
    /// whenever no conflict is active — but the reserved width/line slot does not blink with it.
    /// </summary>
    public readonly string ConflictLine;

    /// <summary>
    /// True when <see cref="Line2"/> ends with the <see cref="DatablockText.IdentText"/> token. The renderer flashes that
    /// trailing token; carrying the flag here keeps the layout the single source of truth for what is on
    /// the line, rather than having the draw path re-derive the condition from the model.
    /// </summary>
    public readonly bool IdentActive;

    /// <summary>
    /// Start index of the <c>cwt/type</c> token within <see cref="Line2"/>, or −1 when the line carries
    /// no type token. The renderer tints that span when the physical type differs from the filed one; the
    /// span is recorded here rather than re-derived at draw time so the line's text stays the single
    /// source of truth for both the draw and hit-test paths.
    /// </summary>
    public readonly int TypeTokenStart;

    /// <summary>Length of the <see cref="TypeTokenStart"/> span, 0 when there is no type token.</summary>
    public readonly int TypeTokenLength;

    /// <summary>Total drawn lines, including any reserved warning slot and the note line.</summary>
    public readonly int LineCount;

    /// <summary>
    /// True when the owner/handoff line (Line3) occupies a reserved slot. The slot is reserved whenever
    /// the line would ever be non-empty (e.g. a handoff with no owner, which flashes blank), so the draw
    /// path must advance its row by this flag rather than by <see cref="Line3"/>'s current emptiness.
    /// </summary>
    public readonly bool ReserveOwnerSlot;

    /// <summary>
    /// True when the no-landing-clearance warning (<see cref="Line5"/>) occupies a reserved slot. Like
    /// <see cref="ReserveOwnerSlot"/>, the draw path must advance its row by this flag rather than by
    /// <see cref="Line5"/>'s emptiness — the line flashes blank but keeps its slot.
    /// </summary>
    public readonly bool ReserveWarningSlot;

    /// <summary>
    /// True when the conflict alert (<see cref="ConflictLine"/>) occupies a reserved slot. Same
    /// flash-blank-but-keep-the-slot contract as <see cref="ReserveWarningSlot"/>.
    /// </summary>
    public readonly bool ReserveConflictSlot;

    private RadarDatablockLayout(
        SKRect rect,
        float textX,
        float textY,
        float lineHeight,
        string line1,
        string line2,
        string line3,
        string line4,
        string line5,
        string squawkLine,
        string atpaLine,
        string line6,
        string conflictLine,
        bool identActive,
        int typeTokenStart,
        int typeTokenLength,
        int lineCount,
        bool reserveOwnerSlot,
        bool reserveWarningSlot,
        bool reserveConflictSlot
    )
    {
        Rect = rect;
        TextX = textX;
        TextY = textY;
        LineHeight = lineHeight;
        Line1 = line1;
        Line2 = line2;
        Line3 = line3;
        Line4 = line4;
        Line5 = line5;
        SquawkLine = squawkLine;
        AtpaLine = atpaLine;
        Line6 = line6;
        ConflictLine = conflictLine;
        IdentActive = identActive;
        TypeTokenStart = typeTokenStart;
        TypeTokenLength = typeTokenLength;
        LineCount = lineCount;
        ReserveOwnerSlot = reserveOwnerSlot;
        ReserveWarningSlot = reserveWarningSlot;
        ReserveConflictSlot = reserveConflictSlot;
    }

    public static RadarDatablockLayout Compute(AircraftModel ac, DatablockPlacement placement, DatablockOverlays overlays, string callsignMarker)
    {
        bool isVfr = ac.FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase);
        string line1 = (isVfr ? $"{ac.Callsign}*" : ac.Callsign) + callsignMarker;

        string altHundreds = ((int)ac.Altitude / 100).ToString("D3");
        string cwt = !string.IsNullOrEmpty(ac.CwtCode) ? ac.CwtCode : "";
        string spdTens = ((int)ac.GroundSpeed / 10).ToString("D2");
        string cwtType = DatablockText.FormatCwtType(cwt, ac.DisplayAircraftType);
        string line2 = $"{altHundreds} {spdTens}";
        // Record where the type token lands so the renderer can tint it without re-parsing the line.
        int typeTokenStart = -1;
        int typeTokenLength = 0;
        if (cwtType.Length > 0)
        {
            typeTokenStart = line2.Length + 1;
            typeTokenLength = cwtType.Length;
            line2 += $" {cwtType}";
        }
        // The ident flashes on the tail of this line, after the type — the width stays reserved here so
        // the block does not pulse; the renderer blanks the token on the off-phase.
        if (ac.IsIdenting)
        {
            line2 += $" {DatablockText.IdentText}";
        }

        // line3 (owner/handoff/scratchpads) flashes the handoff token on a 500 ms cycle. Draw the
        // flashing string but reserve width and a line slot for the stable (handoff-always) version so
        // the rect — and thus the selection border, leader endpoint, and hit area — never pulses.
        bool handoffFlashOn = MapBlink.IsOn;
        string line3 = DatablockText.BuildOwnerScratchpadLine(ac, showHandoff: handoffFlashOn) ?? "";
        string line3Stable = DatablockText.BuildOwnerScratchpadLine(ac, showHandoff: true) ?? "";
        bool reserveOwnerSlot = line3Stable.Length > 0;
        string line4 = ac.TransponderMode == "Standby" ? "ModeC" : "";

        // Beacon-code mismatch line: reported code + assigned code, drawn right under the alt/speed line.
        // The reported code draws solid and the assigned code dim-pulses (emulating CRC STARS). The
        // condition itself does not flash, so the reserved width/height stay stable while active.
        string squawkLine = AircraftCommandApplicability.TryGetSquawkMismatch(ac, out string reportedCode, out string assignedCode)
            ? $"{reportedCode} {assignedCode}"
            : "";

        // No-landing-clearance warning flashes in sync with the handoff indicator (500 ms cycle).
        // Belt-and-suspenders on auto-CTL — the sim already gates the warning on !AutoClearedToLand,
        // but if the toggle flips mid-session before the next state push, the flash stays off.
        bool noLndgClncActive = placement.ShowNoLandingClearance && ac.NoLandingClearanceWarningActive && !ac.IsAutoClearedToLand;
        bool noLndgClncFlashOn = noLndgClncActive && MapBlink.IsOn;
        string line5 = noLndgClncFlashOn ? DatablockText.NoLandingClearanceText : "";

        // ATPA in-trail distance, drawn steady below the owner/scratchpad line. Only the trailing member
        // of a pairing carries a lead callsign, and the line is dropped when the lead isn't on the scope —
        // the distance is the whole content, so there is nothing to show without both positions.
        string atpaLine = (overlays.ShowAtpa && !string.IsNullOrEmpty(ac.AtpaLeadCallsign)) ? DatablockText.BuildAtpaLine(ac, overlays.AtpaLead) : "";

        // Instructor note — always-on amber line at the bottom of the block when set.
        string line6 = ac.HasNote ? ac.Note : "";

        // Conflict alert flashes in sync with the handoff / no-landing-clearance indicators. The text
        // is built unconditionally while active and measured below, so the reserved width tracks the
        // live separation values without pulsing between flash phases.
        bool conflictActive = overlays.ShowConflictAlerts && !string.IsNullOrEmpty(ac.ConflictPeerCallsign);
        string conflictStable = conflictActive ? DatablockText.BuildConflictLine(ac, overlays.ConflictPeer) : "";
        bool conflictFlashOn = conflictActive && MapBlink.IsOn;
        string conflictLine = conflictFlashOn ? conflictStable : "";

        float w1 = placement.Style.Measure(line1);
        float w2 = placement.Style.Measure(line2);
        float wSquawk = squawkLine.Length > 0 ? placement.Style.Measure(squawkLine) : 0f;
        float w3 = reserveOwnerSlot ? placement.Style.Measure(line3Stable) : 0f;
        float w4 = line4.Length > 0 ? placement.Style.Measure(line4) : 0f;
        // Reserve width for the warning line whenever it's active so the rect width doesn't pulse.
        float w5 = noLndgClncActive ? placement.Style.Measure(DatablockText.NoLandingClearanceText) : 0f;
        float w6 = line6.Length > 0 ? placement.Style.Measure(line6) : 0f;
        // Reserve width for the conflict field whenever it's active so the rect doesn't pulse.
        float wConflict = conflictActive ? placement.Style.Measure(conflictStable) : 0f;
        // The ATPA line never flashes, so its drawn text is also its reserved width.
        float wAtpa = atpaLine.Length > 0 ? placement.Style.Measure(atpaLine) : 0f;
        float textW = MathF.Max(MathF.Max(MathF.Max(w1, w2), MathF.Max(w3, w4)), MathF.Max(MathF.Max(w5, w6), MathF.Max(wSquawk, wConflict)));
        textW = MathF.Max(textW, wAtpa);

        int lineCount = 2;
        if (squawkLine.Length > 0)
        {
            lineCount++;
        }
        if (reserveOwnerSlot)
        {
            lineCount++;
        }
        // The ATPA line sits directly below the owner/scratchpad slot; it is steady, so no reservation.
        if (atpaLine.Length > 0)
        {
            lineCount++;
        }
        if (line4.Length > 0)
        {
            lineCount++;
        }
        // Reserve a line slot whenever the warning is active so the rect height doesn't pulse.
        if (noLndgClncActive)
        {
            lineCount++;
        }
        if (line6.Length > 0)
        {
            lineCount++;
        }
        // Reserve a line slot whenever the conflict is active so the rect height doesn't pulse.
        if (conflictActive)
        {
            lineCount++;
        }

        float lineH = placement.Style.LineHeight;
        var rect = new SKRect(
            placement.BlockX - Pad,
            placement.BlockY - placement.Style.Size - Pad,
            placement.BlockX + textW + Pad,
            placement.BlockY + (lineCount - 1) * lineH + Pad
        );

        return new RadarDatablockLayout(
            rect,
            placement.BlockX,
            placement.BlockY,
            lineH,
            line1,
            line2,
            line3,
            line4,
            line5,
            squawkLine,
            atpaLine,
            line6,
            conflictLine,
            ac.IsIdenting,
            typeTokenStart,
            typeTokenLength,
            lineCount,
            reserveOwnerSlot,
            noLndgClncActive,
            conflictActive
        );
    }

    /// <summary>Single-line minified block: altitude (hundreds) + CWT category, plus a flashing ident.</summary>
    public static string BuildMinifiedLine(AircraftModel ac)
    {
        string altHundreds = ((int)ac.Altitude / 100).ToString("D3");
        string cwt = !string.IsNullOrEmpty(ac.CwtCode) ? ac.CwtCode : "";
        string line = cwt.Length > 0 ? $"{altHundreds} {cwt}" : altHundreds;
        return ac.IsIdenting ? $"{line} {DatablockText.IdentText}" : line;
    }

    /// <summary>Bounding rect of a reduced (minified/collapsed) block whose text origin is (blockX, blockY).</summary>
    public static SKRect ReducedRect(IReadOnlyList<string> lines, TextStyle style, float blockX, float blockY)
    {
        float lineH = style.LineHeight;
        float maxW = 0f;
        for (int i = 0; i < lines.Count; i++)
        {
            maxW = MathF.Max(maxW, style.Measure(lines[i]));
        }

        return new SKRect(blockX - Pad, blockY - style.Size - Pad, blockX + maxW + Pad, blockY + ((lines.Count - 1) * lineH) + Pad);
    }
}
