using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Sim;

namespace Yaat.Client.Views.Map;

/// <summary>
/// The STARS full-datablock text builders and placement rules the Scope, the Ground view and the Radar share: the
/// owner/scratchpad, conflict and ATPA fields, the cwt/type token, the collapsed LDB/PDB lines, the student-level
/// marker and the block offset from the symbol.
/// </summary>
internal static class DatablockText
{
    internal const string NoLandingClearanceText = "NoLndgClnc";
    internal const string ConflictAlertText = "CA";
    internal const string ModeCIntruderText = "MCI";

    /// <summary>
    /// IDENT indicator, flashed on the 500 ms cycle at the end of the block's altitude line. Every
    /// builder here appends it unconditionally while the ident is active, so the measured string — and
    /// therefore the rect, hit area, and leader endpoint — keeps the token's width across both flash
    /// phases; the renderer is what blanks it off-phase, mirroring the <c>NoLndgClnc</c> contract.
    /// </summary>
    internal const string IdentText = "ID";

    /// <summary>Default datablock placement: up and to the right of the symbol.</summary>
    internal static readonly SKPoint DefaultOffset = new(28, -28);

    /// <summary>STARS <c>LeaderDirection.Default</c> sentinel (matches <see cref="StarsDatablockClassifier.DefaultLeaderDirection"/>).</summary>
    internal const int DefaultLeaderDirection = 5;

    /// <summary>Pixel gap from the symbol to the near edge of the block when placing by leader direction.</summary>
    private const float LeaderGap = 18f;

    /// <summary>
    /// Builds the conflict-alert field: <c>"{CA|MCI} {horizontal}/{vertical}"</c> — horizontal separation
    /// in nautical miles to one decimal, vertical in feet to the nearest 100.
    /// <para>
    /// The leading token follows the P/CG distinction that 7110.65 §5-14-6 treats as two alert types:
    /// <b>CA</b> between tracked targets, <b>MCI</b> when either member is an untracked, uncorrelated
    /// Mode C target. It is a property of the pair, so both members show the same token.
    /// </para>
    /// <para>
    /// Vertical is computed by quantizing each altitude to hundreds <i>first</i> and then differencing,
    /// rather than differencing and then rounding. Mode C reports in 100 ft increments, and the altitude
    /// field on line 2 truncates the same way — differencing the raw values would let this field read
    /// 200 while the two altitude readouts directly above it showed 100 apart.
    /// </para>
    /// <para>
    /// Separation is derived from the pair's live positions on every layout pass rather than carried on
    /// the conflict broadcast, which is signature-guarded and only fires when the pair set changes —
    /// broadcast values would freeze at the moment the conflict opened.
    /// </para>
    /// Falls back to the bare <see cref="ConflictAlertText"/> when the peer isn't resolvable (it left
    /// the scope, or the broadcast arrived before the peer's first position update) — the alert must
    /// still show rather than vanish, even when it can't be quantified or classified.
    /// </summary>
    internal static string BuildConflictLine(AircraftModel ac, AircraftModel? peer)
    {
        if (peer is null)
        {
            return ConflictAlertText;
        }

        string label = IsModeCIntruder(ac) || IsModeCIntruder(peer) ? ModeCIntruderText : ConflictAlertText;
        double horizontalNm = GeoMath.DistanceNm(ac.Position, peer.Position);
        int verticalFt = Math.Abs(((int)ac.Altitude / 100) - ((int)peer.Altitude / 100)) * 100;
        return $"{label} {horizontalNm:F1}/{verticalFt:D3}";
    }

    /// <summary>
    /// Builds the ATPA in-trail distance field: the live distance to the aircraft ahead in nautical
    /// miles to one decimal (e.g. <c>"3.2"</c>).
    /// <para>
    /// Like the conflict field, the distance is derived from the pair's live positions on every layout
    /// pass rather than carried on the <c>AtpaResultsChanged</c> broadcast, which is signature-guarded on
    /// the pairing / allowed separation / cone state and would freeze a wire-carried value between fires.
    /// </para>
    /// Returns an empty string when the lead isn't resolvable (it left the scope, or the broadcast
    /// arrived before the lead's first position update) — unlike a conflict alert there is no bare token
    /// worth showing, since the distance is the field's entire content.
    /// </summary>
    internal static string BuildAtpaLine(AircraftModel ac, AircraftModel? lead) =>
        lead is null ? "" : $"{GeoMath.DistanceNm(ac.Position, lead.Position):F1}";

    /// <summary>
    /// True when an aircraft is an untracked, uncorrelated Mode C target — nobody owns the track and
    /// there is no flight plan to correlate it to. Per the P/CG, a conflict involving such a target is a
    /// Mode C Intruder alert rather than a conflict alert.
    /// </summary>
    private static bool IsModeCIntruder(AircraftModel ac) => string.IsNullOrEmpty(ac.Owner) && !ac.HasFlightPlan;

    /// <summary>
    /// The value STARS shows in the primary-scratchpad slot: the controller-entered scratchpad when
    /// one is set, otherwise the server-resolved destination fallback (already gated by the student
    /// area's display adaptation). CRC renders the two identically, so the instructor sees what the
    /// student sees. Null or empty when the slot should stay empty.
    /// </summary>
    internal static string? EffectiveScratchpad1(AircraftModel ac) => !string.IsNullOrEmpty(ac.Scratchpad1) ? ac.Scratchpad1 : ac.AutoScratchpad1;

    /// <summary>
    /// Builds the owner/handoff/scratchpad line. <paramref name="showHandoff"/> controls whether the
    /// flashing handoff token is included — pass the current 500 ms flash phase for drawing, or
    /// <c>true</c> to measure the stable (widest) line so the rect doesn't pulse with the flash.
    /// </summary>
    internal static string? BuildOwnerScratchpadLine(AircraftModel ac, bool showHandoff)
    {
        string? effectiveSp1 = EffectiveScratchpad1(ac);
        bool hasAssigned = !string.IsNullOrEmpty(ac.AssignedTo);
        bool hasOwner = !string.IsNullOrEmpty(ac.OwnerDisplay);
        bool hasHandoff = !string.IsNullOrEmpty(ac.HandoffDisplay);
        bool hasSp1 = !string.IsNullOrEmpty(effectiveSp1);
        bool hasSp2 = !string.IsNullOrEmpty(ac.Scratchpad2);
        bool hasOutgoingPointout = !string.IsNullOrEmpty(ac.PointoutToTcpCode);

        if (!hasAssigned && !hasOwner && !hasHandoff && !hasSp1 && !hasSp2 && !hasOutgoingPointout)
        {
            return null;
        }

        var parts = new List<string>(6);
        if (hasAssigned)
        {
            parts.Add($"[{ac.AssignedTo}]");
        }
        if (hasOwner)
        {
            parts.Add(showHandoff && hasHandoff ? $"{ac.OwnerDisplay} >{ac.HandoffDisplay}" : ac.OwnerDisplay!);
        }
        else if (hasHandoff && showHandoff)
        {
            parts.Add($">{ac.HandoffDisplay}");
        }

        // Pending outgoing point-out the student initiated, shown right after the owner (e.g. "3E*").
        if (hasOutgoingPointout)
        {
            parts.Add($"{ac.PointoutToTcpCode}*");
        }

        if (hasSp1)
        {
            parts.Add($".{effectiveSp1}");
        }

        if (hasSp2)
        {
            parts.Add($"+{ac.Scratchpad2}");
        }

        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    /// <summary>
    /// Joins a CWT category and aircraft type as <c>cwt/type</c> (e.g. <c>E/B738</c>), falling back to
    /// whichever side is present. Shared with the ground view datablock (Views.Ground.DataBlockLayout).
    /// </summary>
    internal static string FormatCwtType(string cwt, string aircraftType)
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
        // The ident goes on the first line of both reduced formats, where it stays the trailing token.
        string ident = ac.IsIdenting ? $" {IdentText}" : "";

        if (ac.StudentDatablockLevel == StarsDatablockLevel.Partial)
        {
            string gsTens = ((int)ac.GroundSpeed / 10).ToString("D2");
            string handoff = !string.IsNullOrEmpty(ac.HandoffDisplay) ? $"{ac.HandoffDisplay} " : "";
            string line1 = $"{altHundreds} {handoff}{gsTens}{ident}";
            string? effectiveSp1 = EffectiveScratchpad1(ac);
            return !string.IsNullOrEmpty(effectiveSp1) ? [line1, effectiveSp1] : [line1];
        }

        string beacon = ac.BeaconCode > 0 ? ac.BeaconCode.ToString("0000") : "";
        return beacon.Length > 0 ? [$"{beacon} {altHundreds}{ident}"] : [$"{altHundreds}{ident}"];
    }

    /// <summary>
    /// Resolves the datablock text-origin offset from the symbol. A manual drag offset always wins;
    /// otherwise, when deconfliction supplied an offset for this block, that is used; otherwise, when
    /// leader-direction sync is on and the student set a non-default direction, the block is placed in
    /// that compass direction; failing all three, the default upper-right offset is used.
    /// <paramref name="rectAtOrigin"/> is the block's bounds when drawn at text origin (0, 0).
    /// <paramref name="deconflictOffset"/> is the auto-deconfliction result, or null when deconfliction
    /// is off or this block is pinned.
    /// </summary>
    public static SKPoint ResolveBlockOffset(
        AircraftModel ac,
        bool syncLeader,
        bool hasManual,
        SKPoint manual,
        SKRect rectAtOrigin,
        SKPoint? deconflictOffset
    )
    {
        if (hasManual)
        {
            return manual;
        }

        if (deconflictOffset is { } resolved)
        {
            return resolved;
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
