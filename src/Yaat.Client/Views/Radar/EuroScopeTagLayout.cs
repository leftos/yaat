using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Sim;

namespace Yaat.Client.Views.Radar;

/// <summary>
/// Identifies an interactive tag field for hit testing and flyout dispatch.
/// See docs/euroscope/pseudopilot.md for the EuroScope conventions this mirrors.
/// </summary>
public enum TagFieldId
{
    None,
    Owner,
    Callsign,
    TypeCwt,
    Destination,
    CurrentAltitude,
    AssignedAltitude,
    CurrentSpeed,
    AssignedSpeed,
    AssignedHeading,
    AssignedRunway,
    Scratchpad1,
    Scratchpad2,
    Squawk,
    Handoff,
    ModeC,
    NoLandingClearance,
    Note,
}

/// <summary>One field's text and its bounding rect in canvas coordinates.</summary>
public readonly record struct TagFieldRect(TagFieldId Field, SKRect Rect, string Text);

/// <summary>The rendered EuroScope tag with per-field rects for hit testing.</summary>
public readonly record struct EuroScopeTagResult(SKRect Bounds, IReadOnlyList<TagFieldRect> Fields);

/// <summary>
/// Computes the EuroScope-style 4-line aircraft tag with per-field bounding rects.
/// Reference layout (see docs/euroscope/pseudopilot.md):
///   Line 1: OWNER CALLSIGN     (owner = controlling-RPO initials, or '--' if uncontrolled)
///   Line 2: TYPE/CWT  DEST
///   Line 3: 080 (120) ASP(180) AHDG(270)
///   Line 4: RWY28R .SCRA +SCRB
/// Empty assigned fields show their identifier (ASP/AHDG/ARC) so the click target stays
/// stable -- this matches EuroScope behavior.
/// </summary>
public static class EuroScopeTagLayout
{
    private const float FieldGap = 6f;
    private const float Padding = 3f;

    public static EuroScopeTagResult Layout(
        AircraftModel ac,
        float originX,
        float originY,
        SKPaint paint,
        string? localUserInitials,
        bool showNoLandingClearance
    )
    {
        var fields = new List<TagFieldRect>(12);
        float lineH = paint.TextSize + 2;

        float maxWidth = 0;
        int lineCount = 0;

        // Line 1: owner marker + callsign
        float y1Top = originY - paint.TextSize;
        float y1Bot = originY;
        float x = originX;

        string owner = OwnerMarker(ac);
        x = AddField(fields, TagFieldId.Owner, owner, x, y1Top, y1Bot, paint);
        string callsign = ac.AutoDeletePending ? $"{ac.Callsign}*" : ac.Callsign;
        x = AddField(fields, TagFieldId.Callsign, callsign, x, y1Top, y1Bot, paint);
        maxWidth = MathF.Max(maxWidth, x - originX);
        lineCount = 1;

        // Line 2: type/cwt + destination
        float y2Top = y1Top + lineH;
        float y2Bot = y1Bot + lineH;
        x = originX;
        string typeCwt = FormatTypeCwt(ac);
        if (typeCwt.Length > 0)
        {
            x = AddField(fields, TagFieldId.TypeCwt, typeCwt, x, y2Top, y2Bot, paint);
        }
        if (!string.IsNullOrEmpty(ac.Destination))
        {
            x = AddField(fields, TagFieldId.Destination, ac.Destination, x, y2Top, y2Bot, paint);
        }
        if (x > originX)
        {
            maxWidth = MathF.Max(maxWidth, x - originX);
            lineCount = 2;
        }

        // Line 3: current altitude (assigned altitude) ASP(speed) AHDG(heading)
        float y3Top = y2Top + lineH;
        float y3Bot = y2Bot + lineH;
        x = originX;

        string curAlt = ((int)ac.Altitude / 100).ToString("D3");
        x = AddField(fields, TagFieldId.CurrentAltitude, curAlt, x, y3Top, y3Bot, paint);

        string asgnAlt = FormatAssignedAltitude(ac);
        x = AddField(fields, TagFieldId.AssignedAltitude, asgnAlt, x, y3Top, y3Bot, paint);

        string asp = FormatAssignedSpeed(ac);
        x = AddField(fields, TagFieldId.AssignedSpeed, asp, x, y3Top, y3Bot, paint);

        string ahdg = FormatAssignedHeading(ac);
        x = AddField(fields, TagFieldId.AssignedHeading, ahdg, x, y3Top, y3Bot, paint);

        maxWidth = MathF.Max(maxWidth, x - originX);
        lineCount = 3;

        // Line 4: runway + scratchpads (only if at least one is set, to keep block compact)
        bool hasRwy = !string.IsNullOrEmpty(ac.AssignedRunway);
        bool hasSp1 = !string.IsNullOrEmpty(ac.Scratchpad1);
        bool hasSp2 = !string.IsNullOrEmpty(ac.Scratchpad2);
        bool hasHandoff = !string.IsNullOrEmpty(ac.HandoffDisplay);

        // Tracks the y-top of the last line that actually emitted, so an optional
        // ModeC line below can sit directly under whatever the bottom line ended up being.
        float lastLineYTop = y3Top;

        if (hasRwy || hasSp1 || hasSp2 || hasHandoff)
        {
            float y4Top = y3Top + lineH;
            float y4Bot = y3Bot + lineH;
            x = originX;
            if (hasRwy)
            {
                x = AddField(fields, TagFieldId.AssignedRunway, $"R{ac.AssignedRunway}", x, y4Top, y4Bot, paint);
            }
            if (hasSp1)
            {
                x = AddField(fields, TagFieldId.Scratchpad1, $".{ac.Scratchpad1}", x, y4Top, y4Bot, paint);
            }
            if (hasSp2)
            {
                x = AddField(fields, TagFieldId.Scratchpad2, $"+{ac.Scratchpad2}", x, y4Top, y4Bot, paint);
            }
            if (hasHandoff)
            {
                // Flash to match STARS convention.
                bool show = Environment.TickCount64 / 500 % 2 == 0;
                if (show)
                {
                    x = AddField(fields, TagFieldId.Handoff, $">{ac.HandoffDisplay}", x, y4Top, y4Bot, paint);
                }
            }
            maxWidth = MathF.Max(maxWidth, x - originX);
            lineCount = 4;
            lastLineYTop = y4Top;
        }

        // ModeC line: aircraft squawking standby — STARS would not be receiving Mode C.
        // Renderer draws a strikethrough through the literal "ModeC" text.
        if (ac.TransponderMode == "Standby")
        {
            float yTop = lastLineYTop + lineH;
            float yBot = yTop + paint.TextSize;
            x = originX;
            x = AddField(fields, TagFieldId.ModeC, "ModeC", x, yTop, yBot, paint);
            maxWidth = MathF.Max(maxWidth, x - originX);
            lineCount++;
            lastLineYTop = yTop;
        }

        // No-landing-clearance warning — same 500 ms flash cadence as the handoff above.
        // Belt-and-suspenders on auto-CTL — the sim already gates the trigger on
        // !AutoClearedToLand, but if the toggle flips mid-session before the next push, the
        // flash stays off.
        bool noLndgClncActive = showNoLandingClearance && ac.NoLandingClearanceWarningActive && !ac.IsAutoClearedToLand;
        if (noLndgClncActive)
        {
            float yTop = lastLineYTop + lineH;
            float yBot = yTop + paint.TextSize;
            // Reserve the line slot in the bounds even when the flash is off-phase so the
            // tag height doesn't pulse.
            float reservedWidth = paint.MeasureText(RadarDatablockLayout.NoLandingClearanceText);
            maxWidth = MathF.Max(maxWidth, reservedWidth);
            lineCount++;

            bool flashOn = Environment.TickCount64 / 500 % 2 == 0;
            if (flashOn)
            {
                x = originX;
                AddField(fields, TagFieldId.NoLandingClearance, RadarDatablockLayout.NoLandingClearanceText, x, yTop, yBot, paint);
            }
            lastLineYTop = yTop;
        }

        // Instructor note — always-on amber line at the bottom of the tag when set.
        if (ac.HasNote)
        {
            float yTop = lastLineYTop + lineH;
            float yBot = yTop + paint.TextSize;
            x = originX;
            x = AddField(fields, TagFieldId.Note, ac.Note, x, yTop, yBot, paint);
            maxWidth = MathF.Max(maxWidth, x - originX);
            lineCount++;
            lastLineYTop = yTop;
        }

        var bounds = new SKRect(
            originX - Padding,
            originY - paint.TextSize - Padding,
            originX + maxWidth + Padding,
            originY + (lineCount - 1) * lineH + Padding
        );

        return new EuroScopeTagResult(bounds, fields);
    }

    private static float AddField(List<TagFieldRect> fields, TagFieldId id, string text, float x, float top, float bottom, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text))
        {
            return x;
        }
        float width = paint.MeasureText(text);
        var rect = new SKRect(x, top, x + width, bottom);
        fields.Add(new TagFieldRect(id, rect, text));
        return x + width + FieldGap;
    }

    private static string OwnerMarker(AircraftModel ac)
    {
        // Whoever owns the track, show their initials. Uncontrolled -> "--" (so the field
        // remains a stable click target for the assume-track action).
        return string.IsNullOrEmpty(ac.AssignedTo) ? "--" : ac.AssignedTo;
    }

    private static string FormatTypeCwt(AircraftModel ac)
    {
        var type = ac.DisplayAircraftType.Trim();
        var cwt = ac.CwtCode.Trim();
        if (type.Length > 0 && cwt.Length > 0)
        {
            return $"{type}/{cwt}";
        }
        return type.Length > 0 ? type : cwt;
    }

    private static string FormatAssignedAltitude(AircraftModel ac)
    {
        if (ac.AssignedAltitude is double alt)
        {
            return $"({(int)alt / 100:D3})";
        }
        return "(---)";
    }

    private static string FormatAssignedSpeed(AircraftModel ac)
    {
        if (ac.AssignedSpeed is double spd)
        {
            return $"S{(int)spd:D3}";
        }
        return "ASP";
    }

    private static string FormatAssignedHeading(AircraftModel ac)
    {
        if (ac.AssignedHeading is MagneticHeading hdg)
        {
            return $"H{(int)hdg.Degrees:D3}";
        }
        return "AHDG";
    }
}
