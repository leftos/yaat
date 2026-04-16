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
/// </summary>
public static class VStripsCanonicalBuilder
{
    /// <summary>Move an existing full strip (departure/arrival) into a bay position.</summary>
    public static string BuildStripMove(string bayName, int rack, int index) =>
        $"STRIP {bayName} {rack.ToString(CultureInfo.InvariantCulture)} {index.ToString(CultureInfo.InvariantCulture)}";

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
        var sb = new StringBuilder("HSC ").Append(bayName).Append('/').Append(rack.ToString(CultureInfo.InvariantCulture));
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
        $"HSM {lookupKey} {destBayName}/{rack.ToString(CultureInfo.InvariantCulture)}/{index.ToString(CultureInfo.InvariantCulture)}";

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
            .Append(rack.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(index.ToString(CultureInfo.InvariantCulture));
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
        var tail = !string.IsNullOrEmpty(trimmed) ? trimmed : (index ?? 0).ToString(CultureInfo.InvariantCulture);
        return $"SEPD {bayName} {rack.ToString(CultureInfo.InvariantCulture)} {tail}";
    }

    /// <summary>Create a blank strip. Null bay = printer queue; otherwise bay/rack/index.</summary>
    public static string BuildBlankCreate(string? bayName, int? rack, int? index)
    {
        if (bayName is null)
        {
            return "BLANK";
        }
        var rackVal = (rack ?? 0).ToString(CultureInfo.InvariantCulture);
        var indexVal = (index ?? 0).ToString(CultureInfo.InvariantCulture);
        return $"BLANK {bayName} {rackVal} {indexVal}";
    }

    /// <summary>Delete one blank strip from a bay (blanks are fungible — server picks first match).</summary>
    public static string BuildBlankDelete(string bayName, int? rack)
    {
        if (rack is null)
        {
            return $"BLANKD {bayName}";
        }
        return $"BLANKD {bayName} {rack.Value.ToString(CultureInfo.InvariantCulture)}";
    }

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
