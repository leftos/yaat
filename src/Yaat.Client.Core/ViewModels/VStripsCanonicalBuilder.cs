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
/// <para>The canonical wire format uses <b>1-based</b> rack/index values — users
/// think in "rack 1" and "slot 1", not "rack 0" / "slot 0". Callers pass
/// 0-based integers (matching the internal view-model representation) and the
/// builders add +1 on the wire. The server's
/// <c>StripMutations.ResolveStripTokens</c> performs the reverse mapping.</para>
///
/// <para>Omitting the index argument on STRIP is valid: <c>STRIP bay rack</c>
/// (no trailing index) means "append to the end of the rack" (CRC bottom-up
/// first-available semantics). Pass <c>index = null</c> to <see cref="BuildStripMove"/>
/// to emit that form.</para>
/// </summary>
public static class VStripsCanonicalBuilder
{
    /// <summary>
    /// Move an existing full strip (departure/arrival) into a bay position.
    /// Pass <paramref name="index"/> <c>null</c> to append to the tail of the
    /// rack (the first-available bottom slot).
    /// </summary>
    public static string BuildStripMove(string bayName, int rack, int? index) =>
        index is int i ? $"STRIP {bayName} {OneBased(rack)} {OneBased(i)}" : $"STRIP {bayName} {OneBased(rack)}";

    /// <summary>Delete the full strip owned by the currently-selected aircraft.</summary>
    public static string BuildStripDelete() => "STRIPD";

    /// <summary>Toggle offset on the full strip owned by the currently-selected aircraft.</summary>
    public static string BuildStripOffset() => "STRIPO";

    /// <summary>Edit a single annotation box (1-9, maps to FieldValues[10..18]).</summary>
    public static string BuildAnnotate(int box, string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? $"AN {box.ToString(CultureInfo.InvariantCulture)}"
            : $"AN {box.ToString(CultureInfo.InvariantCulture)} {trimmed}";
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

    /// <summary>Toggle offset on a half-strip by lookup key.</summary>
    public static string BuildHalfStripOffset(string lookupKey) => $"HSO {lookupKey}";

    /// <summary>Slide a half-strip (toggle Left ↔ Right) by lookup key.</summary>
    public static string BuildHalfStripSlide(string lookupKey) => $"HSS {lookupKey}";

    /// <summary>Create a separator of the given style at a bay position with an optional label.</summary>
    public static string BuildSeparatorCreate(SeparatorStyle style, string bayName, int rack, int index, string? label)
    {
        var sb = new StringBuilder("SEP ")
            .Append(StyleChar(style))
            .Append(' ')
            .Append(bayName)
            .Append(' ')
            .Append(OneBased(rack))
            .Append(' ')
            .Append(OneBased(index));
        var trimmed = label?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            sb.Append(' ').Append(trimmed);
        }
        return sb.ToString();
    }

    /// <summary>Delete a separator by bay + label (preferred) or bay + position fallback.</summary>
    public static string BuildSeparatorDelete(string bayName, int rack, string? label, int? index)
    {
        var trimmed = label?.Trim();
        var tail = !string.IsNullOrEmpty(trimmed) ? trimmed : OneBased(index ?? 0);
        return $"SEPD {bayName} {OneBased(rack)} {tail}";
    }

    /// <summary>
    /// Atomic separator label edit. Emits <c>SEPE bay rack index newLabel</c>
    /// where <paramref name="rack"/> / <paramref name="index"/> are 0-based
    /// internally and converted to 1-based on the wire. Replaces the prior
    /// delete+create pattern which was racy under reconnect. New label may
    /// contain spaces — the server joins remaining tokens after the locator.
    /// </summary>
    public static string BuildSeparatorEdit(string bayName, int rack, int index, string newLabel)
    {
        var trimmed = newLabel.Trim();
        return $"SEPE {bayName} {OneBased(rack)} {OneBased(index)} {trimmed}";
    }

    /// <summary>Create a blank strip. Null bay = printer queue; otherwise bay/rack/index.</summary>
    public static string BuildBlankCreate(string? bayName, int? rack, int? index)
    {
        if (bayName is null)
        {
            return "BLANK";
        }
        var rackVal = OneBased(rack ?? 0);
        var indexVal = OneBased(index ?? 0);
        return $"BLANK {bayName} {rackVal} {indexVal}";
    }

    /// <summary>Delete one blank strip from a bay (blanks are fungible — server picks first match).</summary>
    public static string BuildBlankDelete(string bayName, int? rack)
    {
        if (rack is null)
        {
            return $"BLANKD {bayName}";
        }
        return $"BLANKD {bayName} {OneBased(rack.Value)}";
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
