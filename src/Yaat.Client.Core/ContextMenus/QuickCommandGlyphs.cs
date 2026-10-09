namespace Yaat.Client.ContextMenus;

/// <summary>The command family a quick-command glyph is coloured by.</summary>
public enum QuickCommandGlyphFamily
{
    /// <summary>Tower clearances, drawn amber (<c>#E8A33D</c>).</summary>
    Tower,

    /// <summary>Ground movement, drawn teal (<c>#4FB8A8</c>).</summary>
    Ground,

    /// <summary>Flight instructions, drawn blue (<c>#7AA7F0</c>).</summary>
    Flight,

    /// <summary>Traffic-pattern instructions, drawn violet (<c>#B98BE8</c>).</summary>
    Pattern,

    /// <summary>Scope and sim actions, drawn grey (<c>#AEB6C0</c>).</summary>
    ScopeAndSim,
}

/// <summary>A quick command's icon: a 24 px stroke path and the family that colours it.</summary>
/// <param name="PathData">The SVG path data, drawn on a 24 × 24 view box with round caps and joins.</param>
/// <param name="Family">The family whose colour the glyph is drawn in.</param>
public sealed record QuickCommandGlyph(string PathData, QuickCommandGlyphFamily Family);

/// <summary>
/// The icon each glyph-bearing catalog action shows in a quick-command menu's strip, from the approved icon set, and the
/// rule that splits a resolved quick-command list into the strip and the text entries below it: the first
/// <see cref="StripCapacity"/> glyph-bearing entries in list order form the strip (two rows of five), and every other
/// entry, a glyph-bearing one past the tenth included, is a text entry. Every default quick command has a glyph. An action
/// pair the icon set draws as one glyph (left and right 360, left and right closed traffic) shares it, and so do Cancel
/// landing clearance and Cancel takeoff, which no situation offers together. A pattern entry draws the traffic pattern for
/// a runway landing west as a rounded rectangle, an arrowhead in the middle of its runway edge pointing west, with an
/// arrow ending just short of the point of entry: a 45° arrow onto mid-downwind, an arrow along the base leg (the east
/// side) toward the runway, or one along the extended runway line from the east. Left traffic has the runway on the top
/// edge and right traffic on the bottom, so left and right entries mirror top to bottom; the straight-in final draws the
/// left-traffic pattern, the default side when no runway is known.
/// </summary>
public static class QuickCommandGlyphs
{
    /// <summary>The most entries the strip shows: two rows of five.</summary>
    public const int StripCapacity = 10;

    private const string CancelPath = "M2 20h20 M8 5l8 8 M16 5l-8 8";

    private const string SpeedPath = "M4 17a8 8 0 1 1 16 0 M12 17l4-5";

    private const string PatternRectPath = "M3.5 9h12a2 2 0 0 1 2 2v2a2 2 0 0 1 -2 2h-12a2 2 0 0 1 -2 -2v-2a2 2 0 0 1 2 -2z";

    private const string LeftTrafficPatternPath = PatternRectPath + " M11 7.4 L9.4 9 L11 10.6";

    private const string RightTrafficPatternPath = PatternRectPath + " M11 13.4 L9.4 15 L11 16.6";

    private const string EnterLeftDownwindPath = LeftTrafficPatternPath + " M6.35 20.65 L10.15 16.85 M10.15 16.85 h-3 M10.15 16.85 v3";

    private const string EnterRightDownwindPath = RightTrafficPatternPath + " M6.35 3.35 L10.15 7.15 M10.15 7.15 h-3 M10.15 7.15 v-3";

    private const string EnterLeftBasePath = LeftTrafficPatternPath + " M17.5 23 V16.6 M17.5 16.6 l-2.2 2.2 M17.5 16.6 l2.2 2.2";

    private const string EnterRightBasePath = RightTrafficPatternPath + " M17.5 1 V7.4 M17.5 7.4 l-2.2 -2.2 M17.5 7.4 l2.2 -2.2";

    private const string EnterFinalPath = LeftTrafficPatternPath + " M23.5 9 H19.1 M19.1 9 l2.2 -2.2 M19.1 9 l2.2 2.2";

    private const string Turn360Path = "M20 12a8 8 0 1 1-3-6.2 M20 4v5h-5";

    private const string RacetrackPath = "M8 6h8a6 6 0 0 1 0 12H8a6 6 0 0 1 0-12z";

    private const string ClosedTrafficPath = "M7 4h10a4 4 0 0 1 4 4v8a4 4 0 0 1-4 4H7a4 4 0 0 1-4-4V8a4 4 0 0 1 4-4z M7 12h10 M13 1l-3 3 3 3";

    /// <summary>Every glyph-bearing catalog action's glyph; an action with no glyph is absent.</summary>
    public static IReadOnlyDictionary<string, QuickCommandGlyph> ById { get; } =
        new Dictionary<string, QuickCommandGlyph>(StringComparer.Ordinal)
        {
            [MenuIds.TowerClearedForTakeoff] = Tower("M2 20h20 M5 16L19 6 M13 6h6v6"),
            [MenuIds.TowerLineUpAndWait] = Tower("M6 21V3 M18 21V3 M10 9v6 M14 9v6"),
            [MenuIds.TowerCancelTakeoff] = Tower(CancelPath),
            [MenuIds.TowerClearedToLand] = Tower("M2 20h20 M5 6l11 9 M10 15h6v-6"),
            [MenuIds.TowerCancelLanding] = Tower(CancelPath),
            [MenuIds.TowerGoAround] = Tower("M4 19h7a6 6 0 0 0 6-6V5 M13 9l4-4 4 4"),
            [MenuIds.TowerTouchAndGo] = Tower("M2 20h20 M3 7c4 0 6 10 9 10s5-10 9-10"),
            [MenuIds.TowerExitLeft] = Tower("M15 21V11a4 4 0 0 0-4-4H4 M8 3L4 7l4 4"),
            [MenuIds.TowerExitRight] = Tower("M9 21V11a4 4 0 0 1 4-4h7 M16 3l4 4-4 4"),
            [MenuIds.ApproachReportTrafficInSight] = Tower("M12 3v4 M12 17v4 M3 12h4 M17 12h4 M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8"),
            [MenuIds.ApproachReportFieldInSight] = Tower("M12 2v3 M2 13h3 M19 13h3 M7 21l3-12h4l3 12 M12 12v2 M12 17v2"),
            [MenuIds.ApproachReportNMileFinal] = Tower("M7 21l3-10h4l3 10 M12 14v1.5 M12 18v1.5 M8.5 7a5 5 0 0 1 7 0 M5.5 4a9 9 0 0 1 13 0"),
            [MenuIds.ApproachReportAtFix] = Tower("M12 10l6 11H6z M8.5 7a5 5 0 0 1 7 0 M5.5 4a9 9 0 0 1 13 0"),
            [MenuIds.GroundPushback] = Ground("M12 3v12 M8 11l4 4 4-4 M6 20h12"),
            [MenuIds.GroundPushbackTo] = Ground("M12 3v8 M9 8l3 3 3-3 M12 13a4 4 0 1 0 0 8a4 4 0 1 0 0-8"),
            [MenuIds.GroundPushRoute] = Ground(
                "M7 3v5.5 M7 8.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3 M8.3 10.8l6.4 3.4"
                    + " M16 13.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3 M16 16.5V21 M13 18l3 3 3-3"
            ),
            [MenuIds.GroundTaxiPreset] = Ground("M4 21v-5a4 4 0 0 1 4-4h8a4 4 0 0 0 4-4V4 M17 7l3-3 3 3"),
            [MenuIds.GroundTaxiToRunway] = Ground("M19 3v18 M3 12h12 M11 8l4 4-4 4"),
            [MenuIds.GroundDrawTaxiRoute] = Ground("M4 20l4-1L19 8l-3-3L5 16z M14 7l3 3"),
            [MenuIds.GroundHoldPosition] = Ground("M8 3h8l5 5v8l-5 5H8l-5-5V8z M9 12h6"),
            [MenuIds.GroundResumeTaxi] = Ground("M7 4l13 8-13 8z"),
            [MenuIds.GroundHoldShort] = Ground("M3 7h18 M3 10h18 M3 14h3 M9 14h3 M15 14h3 M3 17h3 M9 17h3 M15 17h3"),
            [MenuIds.GroundCrossRunway] = Ground("M3 8h18 M3 16h18 M12 3v18 M9 18l3 3 3-3"),
            [MenuIds.GroundFollow] = Ground("M4 17l5-5-5-5 M13 17l5-5-5-5"),
            [MenuIds.GroundGiveWay] = Ground("M3 5h18L12 20z"),
            [MenuIds.GroundBreakConflict] = Ground("M3 12h6 M15 12h6 M10 6l4 12"),
            [MenuIds.HeadingFly] = Flight("M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M12 7l3 8-3-2-3 2z"),
            [MenuIds.AltitudeMaintain] = Flight("M3 18l7-7 4 4 7-7 M15 8h6v6"),
            [MenuIds.SpeedAssign] = Flight(SpeedPath),
            [MenuIds.SpeedFinalApproach] = Flight(SpeedPath),
            [MenuIds.NavigationDirectTo] = Flight("M3 21L16 8 M10 8h6v6 M19 3a2 2 0 1 0 0 4a2 2 0 1 0 0-4"),
            [MenuIds.HoldPattern] = Flight(RacetrackPath + " M8 18l-2 3"),
            [MenuIds.ApproachCleared] = Flight("M3 12L21 5 M3 12L21 19 M3 12h18"),
            [MenuIds.ApproachClearedVisual] = Flight("M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6"),
            [MenuIds.ApproachExpect] = Flight(
                "M3 12l4.5-1.75 M10.2 9.2l4.5-1.75 M17.4 6.4L21 5 M3 12l4.5 1.75 M10.2 14.8l4.5 1.75 M17.4 17.6L21 19"
                    + " M3 12h4.5 M10.2 12h4.5 M17.4 12H21"
            ),
            [MenuIds.ProceduresClimbViaSid] = Flight("M3 20h5v-5h5v-5h5V5 M15 8l3-3 3 3"),
            [MenuIds.ProceduresDescendViaStar] = Flight("M3 4h5v5h5v5h5v5 M15 16l3 3 3-3"),
            [MenuIds.ProceduresCrossFix] = Flight("M12 7l5 9H7z M2 12h19 M18 9l3 3-3 3"),
            [MenuIds.NavigationOnCourse] = Flight("M3 21c0-6 3-9 9-9h9 M18 9l3 3-3 3"),
            [MenuIds.PatternEnterLeftDownwind] = Pattern(EnterLeftDownwindPath),
            [MenuIds.PatternEnterRightDownwind] = Pattern(EnterRightDownwindPath),
            [MenuIds.PatternEnterLeftBase] = Pattern(EnterLeftBasePath),
            [MenuIds.PatternEnterRightBase] = Pattern(EnterRightBasePath),
            [MenuIds.PatternEnterFinal] = Pattern(EnterFinalPath),
            [MenuIds.PatternFollow] = Pattern("M4 17l5-5-5-5 M17 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6"),
            [MenuIds.PatternMakeLeftTraffic] = Pattern(ClosedTrafficPath),
            [MenuIds.PatternMakeRightTraffic] = Pattern(ClosedTrafficPath),
            [MenuIds.PatternExtend] = Pattern("M3 8v8 M3 12h16 M15 8l4 4-4 4"),
            [MenuIds.PatternTurnBase] = Pattern("M4 4h10a6 6 0 0 1 6 6v10 M16 16l4 4 4-4"),
            [MenuIds.PatternLeft360] = Pattern(Turn360Path),
            [MenuIds.PatternRight360] = Pattern(Turn360Path),
            [MenuIds.PatternShortApproach] = Pattern("M4 6h8v12 M4 18h16 M9 13l3 5 3-5"),
            [MenuIds.TowerClearedOption] = Pattern("M2 20h20 M6 16c2-6 4-9 6-9 M12 7c2 0 4 3 6 9 M12 7V3"),
            [MenuIds.TrackTrack] = ScopeAndSim("M4 9V4h5 M15 4h5v5 M20 15v5h-5 M9 20H4v-5 M12 9v6 M9 12h6"),
            [MenuIds.TrackInitiateHandoff] = ScopeAndSim("M14 4h6v16h-6 M3 12h12 M11 8l4 4-4 4"),
            [MenuIds.SquawkCode] = ScopeAndSim("M3 7h18v10H3z M7 12h2 M11 12h2 M15 12h2"),
            [MenuIds.LiveTrafficAssume] = ScopeAndSim("M12 3v10 M8 9l4 4 4-4 M4 17v4h16v-4"),
            [MenuIds.LiveTrafficAssumeAndTrack] = ScopeAndSim("M4 9V4h5 M15 4h5v5 M20 15v5h-5 M9 20H4v-5 M12 7v8 M9 12l3 3 3-3"),
            [MenuIds.CoordinationCheckReleaseWindow] = ScopeAndSim("M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18 M12 7v5l3 3"),
            [MenuIds.SimControlWarp] = ScopeAndSim("M13 2L4 14h7l-1 8 9-12h-7z"),
            [MenuIds.SimControlDelete] = ScopeAndSim("M4 7h16 M9 7V4h6v3 M6 7l1 13h10l1-13"),
        };

    /// <summary>The glyph <paramref name="catalogId"/> shows in the strip, or null when it has none and shows as text.</summary>
    public static QuickCommandGlyph? For(string catalogId) => ById.GetValueOrDefault(catalogId);

    /// <summary>
    /// The glyphs of the point items the point menu's strip shows — the ground ones (Taxi here, Taxi to runway, Push to,
    /// Custom taxi…) and the radar ones (Fly heading, Direct to, Hold left, Hold right, Warp here) — kept apart from
    /// <see cref="ById"/> because no quick-command list may hold a point item. Push to shares Push back's glyph, Taxi to
    /// runway the aircraft menu's Taxi to runway's, Custom taxi… Draw taxi route…'s, Fly heading, Direct to and Warp here
    /// the aircraft menu's own; the two holds draw the hold pattern's racetrack with an arrow on its top leg showing the
    /// direction of the turns.
    /// </summary>
    public static IReadOnlyDictionary<string, QuickCommandGlyph> PointById { get; } =
        new Dictionary<string, QuickCommandGlyph>(StringComparer.Ordinal)
        {
            [MenuIds.PointTaxiHere] = Ground("M12 21s-6-5.5-6-10.5a6 6 0 0 1 12 0c0 5-6 10.5-6 10.5z M12 8a2.5 2.5 0 1 0 0 5a2.5 2.5 0 1 0 0-5"),
            [MenuIds.PointTaxiToRunway] = ById[MenuIds.GroundTaxiToRunway],
            [MenuIds.PointPushTo] = ById[MenuIds.GroundPushback],
            [MenuIds.PointCustomTaxi] = ById[MenuIds.GroundDrawTaxiRoute],
            [MenuIds.PointFlyHeading] = ById[MenuIds.HeadingFly],
            [MenuIds.PointDirectTo] = ById[MenuIds.NavigationDirectTo],
            [MenuIds.PointHoldLeft] = Flight(RacetrackPath + " M14 3l-3 3 3 3"),
            [MenuIds.PointHoldRight] = Flight(RacetrackPath + " M10 3l3 3-3 3"),
            [MenuIds.PointWarpHere] = ById[MenuIds.SimControlWarp],
        };

    /// <summary>The point item <paramref name="pointId"/>'s glyph (<see cref="PointById"/>).</summary>
    /// <exception cref="ArgumentException">The id is not a point item with a strip glyph.</exception>
    public static QuickCommandGlyph ForPoint(string pointId) =>
        PointById.TryGetValue(pointId, out QuickCommandGlyph? glyph)
            ? glyph
            : throw new ArgumentException($"'{pointId}' is not a point item with a strip glyph.", nameof(pointId));

    /// <summary>
    /// Splits <paramref name="entries"/>, a resolved quick-command list in order, into the strip (the first
    /// <see cref="StripCapacity"/> glyph-bearing entries) and the text entries (every other one, in order). With no
    /// glyph-bearing entry the strip is empty.
    /// </summary>
    public static QuickCommandResolution Split(IReadOnlyList<MenuCatalogEntry> entries)
    {
        List<QuickCommandStripItem> strip = [];
        List<MenuCatalogEntry> text = [];
        foreach (MenuCatalogEntry entry in entries)
        {
            if ((strip.Count < StripCapacity) && (For(entry.Id) is { } glyph))
            {
                strip.Add(new QuickCommandStripItem(entry, glyph));
            }
            else
            {
                text.Add(entry);
            }
        }

        return new QuickCommandResolution(strip, text);
    }

    private static QuickCommandGlyph Tower(string path) => new(path, QuickCommandGlyphFamily.Tower);

    private static QuickCommandGlyph Ground(string path) => new(path, QuickCommandGlyphFamily.Ground);

    private static QuickCommandGlyph Flight(string path) => new(path, QuickCommandGlyphFamily.Flight);

    private static QuickCommandGlyph Pattern(string path) => new(path, QuickCommandGlyphFamily.Pattern);

    private static QuickCommandGlyph ScopeAndSim(string path) => new(path, QuickCommandGlyphFamily.ScopeAndSim);
}
