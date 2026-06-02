using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Sim;

namespace Yaat.Client.Views.Radar;

/// <summary>
/// Layout result for the radar full datablock. Pure: shared by renderer (draw) and
/// hit-test paths so geometry can be computed once.
/// </summary>
internal readonly struct RadarDatablockLayout
{
    private const float Pad = 3f;
    internal const string NoLandingClearanceText = "NoLndgClnc";

    /// <summary>Default datablock placement: up and to the right of the symbol.</summary>
    internal static readonly SKPoint DefaultOffset = new(28, -28);

    /// <summary>STARS <c>LeaderDirection.Default</c> sentinel (matches <see cref="StarsDatablockClassifier.DefaultLeaderDirection"/>).</summary>
    internal const int DefaultLeaderDirection = 5;

    /// <summary>Pixel gap from the symbol to the near edge of the block when placing by leader direction.</summary>
    private const float LeaderGap = 18f;

    public readonly SKRect Rect;
    public readonly float TextX;
    public readonly float TextY;
    public readonly float LineHeight;
    public readonly string Line1;
    public readonly string Line2;
    public readonly string Line3;
    public readonly string Line4;
    public readonly string Line5;

    /// <summary>Instructor note line (amber), drawn at the bottom of the block. Empty when no note.</summary>
    public readonly string Line6;

    /// <summary>Total drawn lines, including any reserved warning slot and the note line.</summary>
    public readonly int LineCount;

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
        string line6,
        int lineCount
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
        Line6 = line6;
        LineCount = lineCount;
    }

    public static RadarDatablockLayout Compute(
        AircraftModel ac,
        float blockX,
        float blockY,
        SKPaint paint,
        bool showNoLandingClearance,
        string callsignMarker
    )
    {
        bool isVfr = ac.FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase);
        string line1 = (isVfr ? $"{ac.Callsign}*" : ac.Callsign) + callsignMarker;

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

        // Instructor note — always-on amber line at the bottom of the block when set.
        string line6 = ac.HasNote ? ac.Note : "";

        float w1 = paint.MeasureText(line1);
        float w2 = paint.MeasureText(line2);
        float w3 = line3.Length > 0 ? paint.MeasureText(line3) : 0f;
        float w4 = line4.Length > 0 ? paint.MeasureText(line4) : 0f;
        // Reserve width for the warning line whenever it's active so the rect width doesn't pulse.
        float w5 = noLndgClncActive ? paint.MeasureText(NoLandingClearanceText) : 0f;
        float w6 = line6.Length > 0 ? paint.MeasureText(line6) : 0f;
        float textW = MathF.Max(MathF.Max(MathF.Max(w1, w2), MathF.Max(w3, w4)), MathF.Max(w5, w6));

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
        if (line6.Length > 0)
        {
            lineCount++;
        }

        float lineH = paint.TextSize + 2;
        var rect = new SKRect(blockX - Pad, blockY - paint.TextSize - Pad, blockX + textW + Pad, blockY + (lineCount - 1) * lineH + Pad);

        return new RadarDatablockLayout(rect, blockX, blockY, lineH, line1, line2, line3, line4, line5, line6, lineCount);
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

    /// <summary>
    /// "(LDB)" / "(PDB)" suffix (with a leading space) describing how the student's STARS scope shows
    /// the track, or "" for a full datablock. Appended to the callsign line when the marker is enabled.
    /// </summary>
    public static string StudentLevelMarker(StarsDatablockLevel? level) =>
        level switch
        {
            StarsDatablockLevel.Limited => " (LDB)",
            StarsDatablockLevel.Partial => " (PDB)",
            _ => "",
        };

    /// <summary>Single-line minified block: altitude (hundreds) + CWT category.</summary>
    public static string BuildMinifiedLine(AircraftModel ac)
    {
        string altHundreds = ((int)ac.Altitude / 100).ToString("D3");
        string cwt = !string.IsNullOrEmpty(ac.CwtCode) ? ac.CwtCode : "";
        return cwt.Length > 0 ? $"{altHundreds} {cwt}" : altHundreds;
    }

    /// <summary>
    /// The reduced datablock the student actually sees when the instructor opts into LDB/FDB collapse,
    /// matching STARS default content (CRC <c>BuildLdb</c>/<c>BuildPdb</c>):
    /// <list type="bullet">
    /// <item>Limited (LDB): beacon code + altitude. Ground speed is hidden unless the track is queried,
    /// which the instructor mirror does not track, so it is omitted from the default view.</item>
    /// <item>Partial (PDB): the FDB altitude line — altitude, the receiving sector during a handoff, and
    /// ground-speed tens — plus scratchpad 1 when set.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> BuildCollapsedLines(AircraftModel ac)
    {
        string altHundreds = ((int)ac.Altitude / 100).ToString("D3");

        if (ac.StudentDatablockLevel == StarsDatablockLevel.Partial)
        {
            string gsTens = ((int)ac.GroundSpeed / 10).ToString("D2");
            string handoff = !string.IsNullOrEmpty(ac.HandoffDisplay) ? $"{ac.HandoffDisplay} " : "";
            string line1 = $"{altHundreds} {handoff}{gsTens}";
            return !string.IsNullOrEmpty(ac.Scratchpad1) ? [line1, ac.Scratchpad1!] : [line1];
        }

        string beacon = ac.BeaconCode > 0 ? ac.BeaconCode.ToString("0000") : "";
        return beacon.Length > 0 ? [$"{beacon} {altHundreds}"] : [altHundreds];
    }

    /// <summary>Bounding rect of a reduced (minified/collapsed) block whose text origin is (blockX, blockY).</summary>
    public static SKRect ReducedRect(IReadOnlyList<string> lines, SKPaint paint, float blockX, float blockY)
    {
        float lineH = paint.TextSize + 2;
        float maxW = 0f;
        for (int i = 0; i < lines.Count; i++)
        {
            maxW = MathF.Max(maxW, paint.MeasureText(lines[i]));
        }

        return new SKRect(blockX - Pad, blockY - paint.TextSize - Pad, blockX + maxW + Pad, blockY + ((lines.Count - 1) * lineH) + Pad);
    }

    /// <summary>
    /// Resolves the datablock text-origin offset from the symbol. A manual drag offset always wins;
    /// otherwise, when leader-direction sync is on and the student set a non-default direction, the
    /// block is placed in that compass direction; failing both, the default upper-right offset is used.
    /// <paramref name="rectAtOrigin"/> is the block's bounds when drawn at text origin (0, 0).
    /// </summary>
    public static SKPoint ResolveBlockOffset(AircraftModel ac, bool syncLeader, bool hasManual, SKPoint manual, SKRect rectAtOrigin)
    {
        if (hasManual)
        {
            return manual;
        }

        if (syncLeader && ac.StudentLeaderDirection is { } dir && dir != DefaultLeaderDirection)
        {
            return LeaderDirectionOffset(dir, rectAtOrigin);
        }

        return DefaultOffset;
    }

    private static SKPoint LeaderDirectionOffset(int dir, SKRect rectAtOrigin)
    {
        // STARS LeaderDirection enum mapped to a screen compass unit (screen Y is down, so North is
        // negative Y): 1=SW 2=S 3=SE 4=W 6=E 7=NW 8=N 9=NE.
        (int hx, int hy) = dir switch
        {
            8 => (0, -1),
            9 => (1, -1),
            6 => (1, 0),
            3 => (1, 1),
            2 => (0, 1),
            1 => (-1, 1),
            4 => (-1, 0),
            7 => (-1, -1),
            _ => (1, -1),
        };

        // The text-origin to rect-center vector is invariant under translation. Place the rect center
        // in the chosen compass direction (gap + half-extent from the symbol) and back out the origin,
        // which right-justifies left-side blocks so their text never overlaps the symbol.
        float centerX = (rectAtOrigin.Left + rectAtOrigin.Right) / 2f;
        float centerY = (rectAtOrigin.Top + rectAtOrigin.Bottom) / 2f;
        float desiredCenterX = hx * (LeaderGap + (rectAtOrigin.Width / 2f));
        float desiredCenterY = hy * (LeaderGap + (rectAtOrigin.Height / 2f));
        return new SKPoint(desiredCenterX - centerX, desiredCenterY - centerY);
    }
}
