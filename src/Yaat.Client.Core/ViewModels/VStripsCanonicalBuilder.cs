using System.Globalization;
using System.Text;
using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Pure helpers that convert UI state into canonical strip-command strings for
/// <see cref="ServerConnection.SendCommandAsync"/>. Every user action in the vStrips
/// view — drag/drop, delete, offset toggle, annotation edit, separator or blank
/// creation — funnels through one of these builders so the wire format stays
/// replay-safe and the command pipeline mirrors every other yaat-client surface.
///
/// <para>The canonical wire format uses <b>1-based</b> rack/index values and
/// <b>slash-compound destination specs</b> (<c>bay/rack/index</c>) across every
/// vStrips verb — STRIP, HSC, HSM, SEP, SEPE, SEPD, BLANK, BLANKD. Users think
/// in "rack 1" and "slot 1", not "rack 0" / "slot 0"; callers pass 0-based
/// integers (matching the view-model representation) and the builders add +1 on
/// the wire. The server's canonical parser performs the reverse mapping inside
/// a single <c>TryParseStripDestSpec</c> helper, eliminating the greedy bay
/// matching the older space-separated form required.</para>
///
/// <para>Omitting trailing parts of the dest-spec is valid: <c>bay</c> alone
/// appends to the first rack, <c>bay/rack</c> appends to that rack (first
/// free slot bottom-up), <c>bay/rack/index</c> targets a specific slot. On
/// STRIP specifically, a null index also emits the shorter form so the server
/// gets the "append to tail" signal — pass <c>index = null</c> to
/// <see cref="BuildStripMove"/> for that.</para>
/// </summary>
public static class VStripsCanonicalBuilder
{
    /// <summary>
    /// Move an existing full strip (departure/arrival) into a bay position.
    /// Pass <paramref name="index"/> <c>null</c> to append to the tail of the
    /// rack (the first-available bottom slot).
    /// </summary>
    public static string BuildStripMove(string bayName, int rack, int? index) =>
        index is int i ? $"STRIP {bayName}/{OneBased(rack)}/{OneBased(i)}" : $"STRIP {bayName}/{OneBased(rack)}";

    /// <summary>Delete the full strip owned by the currently-selected aircraft.</summary>
    public static string BuildStripDelete() => "STRIPD";

    /// <summary>Toggle offset on the full strip owned by the currently-selected aircraft.</summary>
    public static string BuildStripOffset() => "STRIPO";

    /// <summary>
    /// Edit a single annotation slot. <paramref name="box"/> is the canonical
    /// slot id — <c>"1"</c>..<c>"9"</c> for the 3×3 grid (maps to
    /// FieldValues[10..18] server-side), or <c>"8a"</c>/<c>"8b"</c> for the
    /// col-3 freeform slots below field 8 (FieldValues[19..20]). Empty or
    /// whitespace-only <paramref name="text"/> clears the slot.
    /// </summary>
    public static string BuildAnnotate(string box, string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? $"AN {box}" : $"AN {box} {trimmed}";
    }

    /// <summary>Create a new half-strip in a bay/rack with the given lines (max 6).</summary>
    public static string BuildHalfStripCreate(string bayName, int rack, IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder("HSC ").Append(bayName).Append('/').Append(OneBased(rack));
        if (lines.Count > 0)
        {
            sb.Append(' ');
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append('\\');
                }
                sb.Append(lines[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>Amend an existing half-strip's lines by lookup key (first field value).</summary>
    public static string BuildHalfStripAmend(string lookupKey, IReadOnlyList<string> newLines)
    {
        var sb = new StringBuilder("HSA ").Append(lookupKey);
        foreach (var line in newLines)
        {
            sb.Append(' ').Append(line);
        }
        return sb.ToString();
    }

    /// <summary>Move a half-strip by lookup key to a destination bay/rack/index.</summary>
    public static string BuildHalfStripMove(string lookupKey, string destBayName, int rack, int index) =>
        $"HSM {lookupKey} {destBayName}/{OneBased(rack)}/{OneBased(index)}";

    /// <summary>Delete a half-strip by lookup key.</summary>
    public static string BuildHalfStripDelete(string lookupKey) => $"HSD {lookupKey}";

    /// <summary>
    /// Replace a half-strip's full FieldValues by stripId. Empty cells are
    /// preserved by `\`-separation so the inline grid can clear individual
    /// slots without collapsing the array.
    /// </summary>
    public static string BuildHalfStripEdit(string stripId, IReadOnlyList<string> slots) =>
        slots.Count == 0 ? $"HSE {stripId}" : $"HSE {stripId} {string.Join('\\', slots)}";

    /// <summary>Toggle offset on a half-strip by lookup key.</summary>
    public static string BuildHalfStripOffset(string lookupKey) => $"HSO {lookupKey}";

    /// <summary>Slide a half-strip (toggle Left ↔ Right) by lookup key.</summary>
    public static string BuildHalfStripSlide(string lookupKey) => $"HSS {lookupKey}";

    /// <summary>
    /// Create a separator of the given style at a bay position with an optional
    /// label. Emits <c>SEP style bay/rack/index [label]</c>; label may contain
    /// spaces and is captured as the remainder of the line.
    /// </summary>
    public static string BuildSeparatorCreate(SeparatorStyle style, string bayName, int rack, int? index, string? label)
    {
        // Index null → wire omits the slash-index, signaling "append at the
        // top of the rack" to the server. Used by the empty-rack add-menu so
        // a freshly added separator stacks above any existing strips instead
        // of pushing them upward off the visual top.
        var sb = new StringBuilder("SEP ").Append(StyleChar(style)).Append(' ').Append(bayName).Append('/').Append(OneBased(rack));
        if (index is int explicitIndex)
        {
            sb.Append('/').Append(OneBased(explicitIndex));
        }
        var trimmed = label?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            sb.Append(' ').Append(trimmed);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Delete a separator by bay + label (preferred) or bay + position fallback.
    /// Label form: <c>SEPD bay/rack label-text</c>. Position form:
    /// <c>SEPD bay/rack 1-based-index</c>. Either trailing token is interpreted
    /// first as a label; if it's purely numeric it falls through to position.
    /// </summary>
    public static string BuildSeparatorDelete(string bayName, int rack, string? label, int? index)
    {
        var trimmed = label?.Trim();
        var tail = !string.IsNullOrEmpty(trimmed) ? trimmed : OneBased(index ?? 0);
        return $"SEPD {bayName}/{OneBased(rack)} {tail}";
    }

    /// <summary>
    /// Delete a separator by stripId. Emits <c>SEPD &lt;stripId&gt;</c> — the
    /// server detects the <c>SEP_</c> prefix and skips the bay/rack/label
    /// resolution so the command can't desync between the right-click and
    /// the dispatch.
    /// </summary>
    public static string BuildSeparatorDeleteById(string stripId) => $"SEPD {stripId}";

    /// <summary>
    /// Atomic separator label edit. Emits <c>SEPE bay/rack/index newLabel</c>
    /// where <paramref name="rack"/> / <paramref name="index"/> are 0-based
    /// internally and converted to 1-based on the wire. Replaces the prior
    /// delete+create pattern which was racy under reconnect. New label may
    /// contain spaces — the server joins remaining tokens after the locator.
    /// </summary>
    public static string BuildSeparatorEdit(string bayName, int rack, int index, string newLabel)
    {
        var trimmed = newLabel.Trim();
        return $"SEPE {bayName}/{OneBased(rack)}/{OneBased(index)} {trimmed}";
    }

    /// <summary>
    /// Atomic separator label edit by stripId. Emits <c>SEPE &lt;stripId&gt;
    /// &lt;newLabel&gt;</c>. The server detects the id form via the
    /// <c>SEP_</c> prefix and bypasses bay/rack/index resolution — useful
    /// when the inline edit dispatches without knowing the current rack
    /// position (the strip might have been moved by another client between
    /// keystrokes).
    /// </summary>
    public static string BuildSeparatorEditById(string stripId, string newLabel)
    {
        var trimmed = newLabel.Trim();
        return string.IsNullOrEmpty(trimmed) ? $"SEPE {stripId}" : $"SEPE {stripId} {trimmed}";
    }

    /// <summary>
    /// Move a separator by stripId to a new bay/rack/index. Emits
    /// <c>SEPM &lt;stripId&gt; &lt;bay&gt;/&lt;rack&gt;/&lt;index&gt;</c>.
    /// True relocate (not a delete+create) so existing label and style stick
    /// with the strip across the move.
    /// </summary>
    public static string BuildSeparatorMove(string stripId, string bayName, int rack, int index) =>
        $"SEPM {stripId} {bayName}/{OneBased(rack)}/{OneBased(index)}";

    /// <summary>
    /// Create a blank strip. Null bay = printer queue; otherwise bay[/rack[/index]].
    /// Omitting <paramref name="index"/> tells the server to append at the
    /// top of the rack (matches the empty-rack add-menu intent).
    /// </summary>
    public static string BuildBlankCreate(string? bayName, int? rack, int? index)
    {
        if (bayName is null)
        {
            return "BLANK";
        }
        var sb = new StringBuilder("BLANK ").Append(bayName).Append('/').Append(OneBased(rack ?? 0));
        if (index is int explicitIndex)
        {
            sb.Append('/').Append(OneBased(explicitIndex));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Delete a blank strip by stripId. Emits <c>BLANKD &lt;stripId&gt;</c> —
    /// the server detects the <c>BLANK_</c> prefix and removes the strip
    /// wherever it lives (printer queue or a bay rack), so the printer
    /// modal's Delete button can wire through without resolving a bay.
    /// </summary>
    public static string BuildBlankDeleteById(string stripId) => $"BLANKD {stripId}";

    /// <summary>Delete one blank strip from a bay (blanks are fungible — server picks first match).</summary>
    public static string BuildBlankDelete(string bayName, int? rack)
    {
        if (rack is null)
        {
            return $"BLANKD {bayName}";
        }
        return $"BLANKD {bayName}/{OneBased(rack.Value)}";
    }

    /// <summary>Converts a 0-based view-model index into its 1-based wire form.</summary>
    private static string OneBased(int zeroBased) => (zeroBased + 1).ToString(CultureInfo.InvariantCulture);

    private static char StyleChar(SeparatorStyle style) =>
        style switch
        {
            SeparatorStyle.Handwritten => 'H',
            SeparatorStyle.White => 'W',
            SeparatorStyle.Red => 'R',
            SeparatorStyle.Green => 'G',
            _ => 'H',
        };
}

public enum SeparatorStyle
{
    Handwritten = 0,
    White = 1,
    Red = 2,
    Green = 3,
}
