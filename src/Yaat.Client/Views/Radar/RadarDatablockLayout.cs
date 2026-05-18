using SkiaSharp;
using Yaat.Client.Models;

namespace Yaat.Client.Views.Radar;

/// <summary>
/// Layout result for the radar full datablock. Pure: shared by renderer (draw) and
/// hit-test paths so geometry can be computed once.
/// </summary>
internal readonly struct RadarDatablockLayout
{
    private const float Pad = 3f;
    internal const string NoLandingClearanceText = "NoLndgClnc";

    public readonly SKRect Rect;
    public readonly float TextX;
    public readonly float TextY;
    public readonly float LineHeight;
    public readonly string Line1;
    public readonly string Line2;
    public readonly string Line3;
    public readonly string Line4;
    public readonly string Line5;

    private RadarDatablockLayout(
        SKRect rect,
        float textX,
        float textY,
        float lineHeight,
        string line1,
        string line2,
        string line3,
        string line4,
        string line5
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
    }

    public static RadarDatablockLayout Compute(AircraftModel ac, float blockX, float blockY, SKPaint paint, bool showNoLandingClearance)
    {
        bool isVfr = ac.FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase);
        string line1 = isVfr ? $"{ac.Callsign}*" : ac.Callsign;

        string altHundreds = ((int)ac.Altitude / 100).ToString("D3");
        string cwt = !string.IsNullOrEmpty(ac.CwtCode) ? ac.CwtCode : "";
        string spdTens = ((int)ac.GroundSpeed / 10).ToString("D2");
        string cwtType = FormatCwtType(cwt, ac.DisplayAircraftType);
        string line2 = cwtType.Length > 0 ? $"{altHundreds} {spdTens} {cwtType}" : $"{altHundreds} {spdTens}";

        string line3 = BuildOwnerScratchpadLine(ac.OwnerDisplay, ac.HandoffDisplay, ac.Scratchpad1, ac.Scratchpad2, ac.AssignedTo) ?? "";
        string line4 = ac.TransponderMode == "Standby" ? "ModeC" : "";

        // No-landing-clearance warning flashes in sync with the handoff indicator (500 ms cycle).
        // Belt-and-suspenders on auto-CTL — the sim already gates the warning on !AutoClearedToLand,
        // but if the toggle flips mid-session before the next state push, the flash stays off.
        bool noLndgClncActive = showNoLandingClearance && ac.NoLandingClearanceWarningActive && !ac.IsAutoClearedToLand;
        bool noLndgClncFlashOn = noLndgClncActive && (Environment.TickCount64 / 500 % 2 == 0);
        string line5 = noLndgClncFlashOn ? NoLandingClearanceText : "";

        float w1 = paint.MeasureText(line1);
        float w2 = paint.MeasureText(line2);
        float w3 = line3.Length > 0 ? paint.MeasureText(line3) : 0f;
        float w4 = line4.Length > 0 ? paint.MeasureText(line4) : 0f;
        // Reserve width for the warning line whenever it's active so the rect width doesn't pulse.
        float w5 = noLndgClncActive ? paint.MeasureText(NoLandingClearanceText) : 0f;
        float textW = MathF.Max(MathF.Max(MathF.Max(w1, w2), MathF.Max(w3, w4)), w5);

        int lineCount = 2;
        if (line3.Length > 0)
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

        float lineH = paint.TextSize + 2;
        var rect = new SKRect(blockX - Pad, blockY - paint.TextSize - Pad, blockX + textW + Pad, blockY + (lineCount - 1) * lineH + Pad);

        return new RadarDatablockLayout(rect, blockX, blockY, lineH, line1, line2, line3, line4, line5);
    }

    private static string? BuildOwnerScratchpadLine(string? ownerDisplay, string? handoffDisplay, string? sp1, string? sp2, string? assignedTo)
    {
        bool hasAssigned = !string.IsNullOrEmpty(assignedTo);
        bool hasOwner = !string.IsNullOrEmpty(ownerDisplay);
        bool hasHandoff = !string.IsNullOrEmpty(handoffDisplay);
        bool hasSp1 = !string.IsNullOrEmpty(sp1);
        bool hasSp2 = !string.IsNullOrEmpty(sp2);

        if (!hasAssigned && !hasOwner && !hasHandoff && !hasSp1 && !hasSp2)
        {
            return null;
        }

        var parts = new List<string>(5);
        if (hasAssigned)
        {
            parts.Add($"[{assignedTo}]");
        }
        if (hasOwner)
        {
            // Flash handoff indicator: 500ms on/off cycle (all flash in sync, STARS behavior)
            bool showHandoff = hasHandoff && Environment.TickCount64 / 500 % 2 == 0;
            parts.Add(showHandoff ? $"{ownerDisplay} >{handoffDisplay}" : ownerDisplay!);
        }
        else if (hasHandoff)
        {
            bool showHandoff = Environment.TickCount64 / 500 % 2 == 0;
            if (showHandoff)
            {
                parts.Add($">{handoffDisplay}");
            }
        }

        if (hasSp1)
        {
            parts.Add($".{sp1}");
        }

        if (hasSp2)
        {
            parts.Add($"+{sp2}");
        }

        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    private static string FormatCwtType(string cwt, string aircraftType)
    {
        string baseType = aircraftType.Trim();
        if (cwt.Length > 0 && baseType.Length > 0)
        {
            return $"{cwt}/{baseType}";
        }

        if (cwt.Length > 0)
        {
            return cwt;
        }

        return baseType;
    }
}
