using System.Globalization;
using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Mva;

namespace Yaat.Client.Views.Scope;

/// <summary>Everything one scope frame reads to lay its tracks out.</summary>
/// <param name="Aircraft">The tracks to draw, already filtered for visibility.</param>
/// <param name="Selected">The selected aircraft, or null.</param>
/// <param name="Viewport">The viewport the frame is drawn in.</param>
/// <param name="TemplateId">Id of the datablock template in use (see <see cref="ScopeTemplates"/>).</param>
/// <param name="Metrics">Datablock text measurements.</param>
/// <param name="Alerts">Alert overlays the user's preferences allow.</param>
/// <param name="Display">Instructor display hints the user's preferences allow.</param>
/// <param name="ManualOffsets">Per-callsign block offsets from the symbol; a callsign absent here takes the default offset.</param>
/// <param name="HighlightedCallsigns">Callsigns whose blocks are highlighted (middle-click).</param>
/// <param name="MvaFacilityId">
/// The facility whose MVA chart wins where charts overlap (the Radar's <c>RadarViewModel.MvaFacilityId</c>, copied on the UI
/// thread); null for none.
/// </param>
/// <param name="OthersLevel">
/// The zoom band's level for tracks the viewer does not own (<see cref="Profiles.ScopeProfile.OthersFull"/> or
/// <see cref="Profiles.ScopeProfile.OthersPartial"/>), copied on the UI thread; with no viewer every track is Full.
/// </param>
/// <param name="VectorMinutes">The vector length in effect, in minutes, copied on the UI thread; 0 draws no vector.</param>
/// <param name="VectorOwnership">The tracks vectors draw on: the viewer's own (every track with no viewer) or all.</param>
/// <param name="HistoryCount">The history dots in effect per track, copied on the UI thread; 0 draws none.</param>
/// <param name="LocalRings">
/// The rings the instructor placed on this scope, by callsign (<c>ScopeViewModel.LocalRings</c>), copied on the UI thread;
/// empty is none. A callsign with no drawn track draws nothing.
/// </param>
/// <param name="HoveredCallsign">The callsign of the track the pointer dwells on (<see cref="ScopeFrame.DwellAt"/>), or null.</param>
/// <param name="VciHoveredCallsign">
/// The callsign of the track whose VCI cell the pointer is on (<see cref="ScopeFrame.HoverAt"/>), or null; its VCI lights
/// without dwelling the block.
/// </param>
/// <param name="LocalDwellLockedCallsigns">
/// Callsigns the instructor dwell-locked on this scope (DataBlockViewState.DwellLockedCallsigns); null is none.
/// </param>
/// <param name="ShowTopDown">
/// Whether top-down mode is on (the canvas's <c>ShowTopDown</c>, copied on the UI thread): an aircraft on the ground then
/// takes the ground-target block on the STARS and ERAM templates.
/// </param>
/// <param name="LocalPtlCallsigns">
/// The callsigns with a PTL on this scope (<c>ScopeViewModel.LocalPtlCallsigns</c>), copied on the UI thread; null is none.
/// Their tracks draw a vector whatever the VECTOR ownership says.
/// </param>
/// <param name="LensPath">
/// Whether the pilot lens's intent path draws (the lens and its PATH both on), copied on the UI thread; it draws for the
/// selected aircraft and for <paramref name="LensPathCallsigns"/>.
/// </param>
/// <param name="LensPathCallsigns">
/// The callsigns whose intent path is toggled on (<c>ScopeViewModel.LensPathCallsigns</c>), copied on the UI thread; null is
/// none.
/// </param>
/// <param name="NavDb">
/// The navigation database the intent path reads: null while none is loaded (then only its shapes draw) and while the lens
/// draws no path.
/// </param>
/// <param name="IntentPaths">
/// The canvas's cache of each drawn track's intent-path routes, so only the projection runs per frame.
/// </param>
/// <param name="LensQueue">
/// Whether the pilot lens's queued-commands badge draws (the lens and its QUEUE both on), copied on the UI thread; it draws
/// for every drawn track with a pending command.
/// </param>
/// <param name="QueueHoveredCallsign">
/// The callsign of the track whose queued-commands badge the pointer is on (<see cref="ScopeFrame.HoverAt"/>), or null; its
/// pending lines show without dwelling the block.
/// </param>
/// <param name="LensCheckIn">
/// Whether the pilot lens's check-in tag draws (the lens and its CHECKIN both on), copied on the UI thread; it draws for every
/// drawn track whose pilot's radio state has a mark (<see cref="ScopeCheckIn.StateOf"/>).
/// </param>
/// <param name="CrrGroups">
/// The room's ERAM CRR groups (<c>MainViewModel.CrrGroups</c>), copied on the UI thread; null is none. A track drawn with an
/// FDB-level ERAM block whose group is among them carries its range data block (<see cref="ScopeCrrReadout"/>).
/// </param>
public sealed record ScopeFrameInput(
    IReadOnlyList<AircraftModel> Aircraft,
    AircraftModel? Selected,
    MapViewport Viewport,
    string TemplateId,
    ScopeTextMetrics Metrics,
    ScopeAlertOptions Alerts,
    ScopeDisplayOptions Display,
    IReadOnlyDictionary<string, SKPoint> ManualOffsets,
    IReadOnlySet<string> HighlightedCallsigns,
    string? MvaFacilityId,
    string OthersLevel,
    double VectorMinutes,
    ScopeVectorOwnership VectorOwnership,
    int HistoryCount,
    IReadOnlyDictionary<string, ScopeTrackRings> LocalRings,
    ScopeIntentPathCache IntentPaths,
    string? HoveredCallsign = null,
    string? VciHoveredCallsign = null,
    IReadOnlySet<string>? LocalDwellLockedCallsigns = null,
    bool ShowTopDown = false,
    IReadOnlySet<string>? LocalPtlCallsigns = null,
    bool LensPath = false,
    IReadOnlySet<string>? LensPathCallsigns = null,
    NavigationDatabase? NavDb = null,
    bool LensQueue = false,
    string? QueueHoveredCallsign = null,
    bool LensCheckIn = false,
    IReadOnlyList<EramCrrGroupHubDto>? CrrGroups = null
);

/// <summary>One track as the frame draws it: its symbol's screen point and its block laid out at its placed position.</summary>
/// <param name="Aircraft">The track.</param>
/// <param name="Symbol">Screen point of the position symbol.</param>
/// <param name="Block">The block's one layout, placed in screen coordinates; drawing and hit-testing both read it.</param>
/// <param name="IsSelected">Whether the track is selected.</param>
/// <param name="BlockColor">The block's colour role, as the presenter decided it.</param>
/// <param name="IsStale">Whether the track is a coasting live-traffic shadow, drawn at half alpha.</param>
/// <param name="Tint">The RPO tint that applies to the track, as the presenter decided it; it colours the symbol.</param>
/// <param name="VectorEnd">Screen point the vector line from the symbol ends at, or null when the track draws none.</param>
/// <param name="History">Screen points of the history dots, oldest first; null or empty when the track draws none.</param>
/// <param name="Rings">The track's J-ring, cone, DRI halo and ATPA cone in screen points; <see cref="ScopeRingShapes.None"/> for none.</param>
/// <param name="Level">The block level the track was presented at.</param>
/// <param name="ShowsLeader">Whether the block draws its leader, as the presenter decided it.</param>
/// <param name="Dwelled">
/// Whether the pointer dwells on the track or it is dwell-locked; a ground target's block keeps its <see cref="ScopeBlockLevel.Ground"/>
/// level then, so this carries its dwell.
/// </param>
/// <param name="IntentPath">The track's pilot-lens intent path in screen points, or null when it draws none.</param>
/// <param name="QueueBadge">
/// The track's pilot-lens queued-commands badge, or null when the overlay is off or the aircraft has no pending command.
/// </param>
/// <param name="CheckInTag">
/// The track's pilot-lens check-in tag (<see cref="ScopeCheckIn.LabelOf"/>), or null when the overlay is off or the track
/// has no mark.
/// </param>
public sealed record ScopeTrack(
    AircraftModel Aircraft,
    SKPoint Symbol,
    ScopeBlockLayout Block,
    bool IsSelected,
    ScopeBlockColor BlockColor,
    bool IsStale,
    ScopeTintRole Tint,
    SKPoint? VectorEnd,
    IReadOnlyList<SKPoint>? History,
    ScopeRingShapes Rings,
    ScopeBlockLevel Level,
    bool ShowsLeader,
    bool Dwelled,
    ScopeIntentPath? IntentPath,
    ScopeQueueBadgeView? QueueBadge,
    string? CheckInTag
)
{
    /// <summary>How far the dwell box sits outside the text bounds (CRC <c>FdbRenderObject</c>: the fields rect inflated 2 px).</summary>
    public const float DwellBoxInflatePx = 2f;

    /// <summary>
    /// Whether the block is dwelled (hovered or dwell-locked): drawn on top, without the stale alpha, with its dwell box. A
    /// Dwell block always is; a ground target's block is while its level stays Ground.
    /// </summary>
    public bool IsDwelled => Dwelled;

    /// <summary>
    /// Whether the block is dwell-locked, by the instructor on this scope or by the student's CRC: the pick tier above a plain
    /// block (<see cref="ScopeFrame.IndicatorPickAt"/>). A pointer hover alone does not set it, as CRC's move walk resets the
    /// dwell first.
    /// </summary>
    public bool IsDwellLocked { get; init; }

    /// <summary>
    /// The track's range data block (<see cref="ScopeCrrReadout"/>), or null when its block is no FDB-level ERAM block or its
    /// aircraft is in no existing CRR group.
    /// </summary>
    public ScopeCrrReadoutView? CrrReadout { get; init; }

    /// <summary>The text bounds inflated <see cref="DwellBoxInflatePx"/>; the hover hit-test and the emphasis both read it.</summary>
    public SKRect DwellBox
    {
        get
        {
            SKRect box = Block.TextBounds;
            box.Inflate(DwellBoxInflatePx, DwellBoxInflatePx);
            return box;
        }
    }
}

/// <summary>
/// What the pointer hovers: the track whose block it dwells, the track whose VCI it previews, or the track whose queued-commands
/// badge it is on; at most one is set.
/// </summary>
/// <param name="Dwelled">The aircraft whose block the pointer dwells on, or null.</param>
/// <param name="VciPreviewed">The aircraft whose VCI cell the pointer is on, or null.</param>
/// <param name="QueueHovered">The aircraft whose queued-commands badge the pointer is on, or null.</param>
public readonly record struct ScopeHoverHit(AircraftModel? Dwelled, AircraftModel? VciPreviewed, AircraftModel? QueueHovered);

/// <summary>An ERAM direct-action indicator a left press picks ahead of the block fields: whose it is and the action it runs.</summary>
/// <param name="Aircraft">The aircraft whose indicator the press is on.</param>
/// <param name="Action"><see cref="ScopeFieldAction.ToggleHsf"/> or <see cref="ScopeFieldAction.ToggleVci"/>.</param>
public readonly record struct ScopeIndicatorPick(AircraftModel Aircraft, ScopeFieldAction Action);

/// <summary>A field a point landed on, and whose block it is.</summary>
public sealed record ScopeFieldHit(AircraftModel Aircraft, FieldRect Field);

/// <summary>
/// One scope frame's tracks, each block laid out once on the UI thread. The renderer draws from it and the canvas
/// hit-tests against it, so the two can never disagree about where a field is.
/// </summary>
/// <param name="TemplateId">The scope's picked template id (a template id, or <see cref="ScopeTemplates.MixedId"/>, resolved per track).</param>
/// <param name="Tracks">Tracks in draw order: the last one is drawn on top.</param>
public sealed record ScopeFrame(string TemplateId, IReadOnlyList<ScopeTrack> Tracks)
{
    /// <summary>How near a symbol a click must land to hit it, as today's radar uses.</summary>
    public const float TargetHitRadiusPx = 28f;

    /// <summary>How near a symbol the pointer must be to dwell its track (CRC <c>ViewDataBlocks.GetDataBlockByPosition</c>).</summary>
    public const float DwellSymbolRadiusPx = 8f;

    /// <summary>How far a queued-commands badge's hover box sits outside its text box, on every side.</summary>
    private const float QueueBadgeHoverInflatePx = 3f;

    /// <summary>How near the MVA floor an altitude reads as at it, as today's radar uses.</summary>
    private const int MvaAtBandFt = 100;

    /// <summary>
    /// Lays every track out for this frame, reading the blink phase from <see cref="MapBlink"/> once so the whole frame
    /// shares one phase. Each block sits at the default leader offset; dwelled tracks come last, so they draw on top and
    /// hit-test first: the dwell-locked ones, then the hovered one.
    /// </summary>
    public static ScopeFrame Build(ScopeFrameInput input)
    {
        bool flashOn = MapBlink.IsOn;
        var byCallsign = new Dictionary<string, AircraftModel>(StringComparer.Ordinal);
        foreach (AircraftModel ac in input.Aircraft)
        {
            byCallsign.TryAdd(ac.Callsign, ac);
        }

        var tracks = new List<ScopeTrack>(input.Aircraft.Count);
        var locked = new List<ScopeTrack>();
        var hovered = new List<ScopeTrack>();
        foreach (AircraftModel ac in input.Aircraft)
        {
            ScopeTrack track = BuildTrack(input, byCallsign, ac, flashOn);
            if (!track.IsDwelled)
            {
                tracks.Add(track);
            }
            else
            {
                (IsHovered(ac, input) ? hovered : locked).Add(track);
            }
        }

        tracks.AddRange(locked);
        tracks.AddRange(hovered);
        input.IntentPaths.Sweep();
        return new ScopeFrame(input.TemplateId, tracks);
    }

    /// <summary>
    /// The one place a track's block level is chosen: <see cref="ScopeBlockLevel.Ground"/> for a ground target
    /// (<see cref="IsGroundTarget"/>), dwelled or not; else <see cref="ScopeBlockLevel.Dwell"/> while it is
    /// <paramref name="dwelled"/> (hovered, or <see cref="IsDwellLockedTrack"/>); else Full for the viewer's own track and for every track
    /// when there is no viewer; else the others' level for the track's template (<see cref="OthersLevelFor"/>).
    /// </summary>
    private static ScopeBlockLevel LevelFor(AircraftModel ac, string templateId, ScopeFrameInput input, bool dwelled)
    {
        if (IsGroundTarget(ac, templateId, input))
        {
            return ScopeBlockLevel.Ground;
        }

        if (dwelled)
        {
            return ScopeBlockLevel.Dwell;
        }

        ScopeViewer viewer = input.Display.Viewer;
        if (IsOwnTrack(ac, viewer))
        {
            return ScopeBlockLevel.Full;
        }

        return OthersLevelFor(templateId, input.OthersLevel);
    }

    /// <summary>
    /// Whether <paramref name="ac"/> is drawn with the ERAM template and dwell-locked, by the instructor on this scope
    /// (<see cref="ScopeFrameInput.LocalDwellLockedCallsigns"/>) or by the student's CRC for the viewer's ERAM sector. A track
    /// is dwelled while the pointer dwells on it or it is locked.
    /// </summary>
    private static bool IsDwellLockedTrack(AircraftModel ac, string templateId, ScopeFrameInput input) =>
        (templateId == ScopeTemplates.EramId)
        && ((input.LocalDwellLockedCallsigns?.Contains(ac.Callsign) ?? false) || EramDwellLock.IsDwellLocked(ac, input.Display.Viewer));

    /// <summary>
    /// Whether <paramref name="ac"/> is a ground target: on the ground while top-down mode is on, drawn with the STARS or ERAM
    /// template (EuroScope keeps its ordinary tag). An airborne aircraft below the display floor is not one.
    /// </summary>
    private static bool IsGroundTarget(AircraftModel ac, string templateId, ScopeFrameInput input) =>
        input.ShowTopDown && ac.IsOnGround && (templateId is ScopeTemplates.StarsId or ScopeTemplates.EramId);

    /// <summary>Whether <paramref name="ac"/> counts as the viewer's own: every track does with no viewer.</summary>
    private static bool IsOwnTrack(AircraftModel ac, ScopeViewer viewer) => !EramColumn0.HasViewer(viewer) || EramColumn0.IsViewersTrack(ac, viewer);

    /// <summary>
    /// The track's vector end, when its ownership draws one (<see cref="ScopeTrackLayers.ShowsVector"/>) or it carries a
    /// PTL (<see cref="ScopeFrameInput.LocalPtlCallsigns"/>); else null.
    /// </summary>
    private static SKPoint? VectorEndFor(AircraftModel ac, ScopeFrameInput input) =>
        (
            ScopeTrackLayers.ShowsVector(input.VectorOwnership, IsOwnTrack(ac, input.Display.Viewer))
            || (input.LocalPtlCallsigns?.Contains(ac.Callsign) == true)
        )
            ? ScopeTrackLayers.VectorEnd(ac, input.VectorMinutes, input.Viewport)
            : null;

    /// <summary>
    /// The block level <paramref name="othersLevel"/> gives a track drawn with <paramref name="templateId"/>: <c>partial</c>
    /// is ERAM's Partial (its LDB) and STARS' Limited, and leaves EuroScope, which has no collapsed lines, Full; any other
    /// word is Full.
    /// </summary>
    private static ScopeBlockLevel OthersLevelFor(string templateId, string othersLevel)
    {
        if (othersLevel != Profiles.ScopeProfile.OthersPartial)
        {
            return ScopeBlockLevel.Full;
        }

        return templateId switch
        {
            ScopeTemplates.EramId => ScopeBlockLevel.Partial,
            ScopeTemplates.StarsId => ScopeBlockLevel.Limited,
            _ => ScopeBlockLevel.Full,
        };
    }

    private static bool IsHovered(AircraftModel ac, ScopeFrameInput input) =>
        string.Equals(ac.Callsign, input.HoveredCallsign, StringComparison.Ordinal);

    private static ScopeTrack BuildTrack(ScopeFrameInput input, Dictionary<string, AircraftModel> byCallsign, AircraftModel ac, bool flashOn)
    {
        bool isSelected = ReferenceEquals(ac, input.Selected);
        string templateId = ScopeOwnerKind.Resolve(input.TemplateId, ac);
        AircraftModel? atpaLead = Peer(byCallsign, ac.AtpaLeadCallsign);
        bool locked = IsDwellLockedTrack(ac, templateId, input);
        bool dwelled = IsHovered(ac, input) || locked;
        var presenterInput = new TrackPresenterInput(
            ac,
            templateId,
            atpaLead,
            Peer(byCallsign, ac.ConflictPeerCallsign),
            flashOn,
            isSelected,
            input.Alerts,
            input.Display,
            MvaRelationFor(ac, input.Display.ShowMvaAltitudeTint, input.MvaFacilityId),
            input.HighlightedCallsigns.Contains(ac.Callsign),
            LevelFor(ac, templateId, input, dwelled),
            VciPreview: string.Equals(ac.Callsign, input.VciHoveredCallsign, StringComparison.Ordinal)
        );
        TrackPresentation presentation = TrackPresenter.Present(presenterInput);
        ScopeBlockLayout atOrigin = DatablockTemplate.Layout(presentation, input.Metrics);
        bool hasManual = input.ManualOffsets.TryGetValue(ac.Callsign, out SKPoint manual);
        SKPoint offset = DatablockText.ResolveBlockOffset(ac, false, hasManual, manual, atOrigin.Bounds, null);
        (float sx, float sy) = input.Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
        ScopeBlockLayout placed = atOrigin.At(sx + offset.X, sy + offset.Y);
        if (!EramDwellLock.HasEramSector(input.Display.Viewer))
        {
            // With no viewer ERAM sector the VCI can never light, so its cell takes no hover.
            placed = placed with
            {
                VciCell = null,
            };
        }
        return new ScopeTrack(
            ac,
            new SKPoint(sx, sy),
            placed,
            isSelected,
            presentation.BlockColor,
            ac.LiveTrafficStale,
            presentation.Tint,
            VectorEndFor(ac, input),
            ScopeTrackLayers.HistoryDots(ac, input.HistoryCount, input.Viewport),
            RingsFor(ac, input, input.Alerts.ShowAtpa ? atpaLead : null, offset),
            presentation.Level,
            presentation.ShowsLeader,
            dwelled,
            IntentPathFor(ac, input, isSelected),
            QueueBadgeFor(ac, input, sx, sy),
            CheckInTagFor(ac, input)
        )
        {
            IsDwellLocked = locked,
            CrrReadout = CrrReadoutFor(ac, templateId, presentation.Level, input) is { } crr
                ? ScopeCrrReadout.Place(crr, new SKPoint(sx, sy), offset, input.Metrics)
                : null,
        };
    }

    /// <summary>The range data block's text and colour for <paramref name="ac"/>, or null (<see cref="ScopeCrrReadout.Content"/>).</summary>
    private static (string Text, SKColor Color)? CrrReadoutFor(AircraftModel ac, string templateId, ScopeBlockLevel level, ScopeFrameInput input) =>
        input.CrrGroups is { Count: > 0 } groups ? ScopeCrrReadout.Content(ac, templateId, level, groups) : null;

    /// <summary>
    /// The check-in tag of <paramref name="ac"/> while the lens check-in draws (<see cref="ScopeFrameInput.LensCheckIn"/>):
    /// the label of its mark as the frame's viewer sees it; else, or when it has no mark, null.
    /// </summary>
    private static string? CheckInTagFor(AircraftModel ac, ScopeFrameInput input) =>
        input.LensCheckIn ? ScopeCheckIn.LabelOf(ScopeCheckIn.StateOf(ac, input.Display.Viewer), ac.PendingPilotRequest) : null;

    /// <summary>
    /// The queued-commands badge of <paramref name="ac"/> while the lens queue draws (<see cref="ScopeFrameInput.LensQueue"/>)
    /// and the aircraft has a pending line (<see cref="ScopeQueueBadge.PendingLines"/>); else null. Its text box sits below-left
    /// of the symbol at (<paramref name="sx"/>, <paramref name="sy"/>), measured with the frame's metrics.
    /// </summary>
    private static ScopeQueueBadgeView? QueueBadgeFor(AircraftModel ac, ScopeFrameInput input, float sx, float sy)
    {
        if (!input.LensQueue)
        {
            return null;
        }

        IReadOnlyList<string> lines = ScopeQueueBadge.PendingLines(ac.PendingCommands);
        if (lines.Count == 0)
        {
            return null;
        }

        string text = lines.Count.ToString(CultureInfo.InvariantCulture);
        float right = sx - ScopeQueueBadge.SymbolGapPx;
        float top = sy + ScopeQueueBadge.SymbolGapPx;
        var rect = new SKRect(right - input.Metrics.Measure(text), top, right, top + input.Metrics.Size);
        bool hovered = string.Equals(ac.Callsign, input.QueueHoveredCallsign, StringComparison.Ordinal);
        return new ScopeQueueBadgeView(lines.Count, lines, rect, hovered);
    }

    /// <summary>
    /// The intent path of <paramref name="ac"/> while the lens path draws (<see cref="ScopeFrameInput.LensPath"/>) and the
    /// track is selected or toggled on (<see cref="ScopeFrameInput.LensPathCallsigns"/>); else null. Through the canvas's
    /// cache (<see cref="ScopeFrameInput.IntentPaths"/>).
    /// </summary>
    private static ScopeIntentPath? IntentPathFor(AircraftModel ac, ScopeFrameInput input, bool isSelected) =>
        (input.LensPath && (isSelected || (input.LensPathCallsigns?.Contains(ac.Callsign) == true)))
            ? input.IntentPaths.PathFor(ac, input.NavDb, input.Viewport)
            : null;

    /// <summary>
    /// The ring layer of <paramref name="ac"/>: its placed rings (<see cref="ScopeFrameInput.LocalRings"/>) and its ATPA cone
    /// toward <paramref name="atpaLead"/>, null when ATPA is off or the lead is not drawn; the J-ring's label sits opposite the
    /// leader (<paramref name="leaderOffset"/>).
    /// </summary>
    private static ScopeRingShapes RingsFor(AircraftModel ac, ScopeFrameInput input, AircraftModel? atpaLead, SKPoint leaderOffset)
    {
        ScopeTrackRings rings = input.LocalRings.GetValueOrDefault(ac.Callsign) ?? ScopeTrackRings.None;
        return ScopeRingGeometry.Shapes(ac, rings, atpaLead, leaderOffset, input.Viewport);
    }

    /// <summary>
    /// The altitude's relation to the MVA floor at <paramref name="ac"/>'s position, or null when the tint does not apply:
    /// it is off, the aircraft is on the ground, established on an approach, or VFR (MSAW-inhibited), as today's radar rules.
    /// Where MVA charts overlap, <paramref name="mvaFacilityId"/>'s chart wins, as on the radar; null for none.
    /// </summary>
    internal static MvaRelation? MvaRelationFor(AircraftModel ac, bool showTint, string? mvaFacilityId)
    {
        if (!showTint || ac.IsOnGround || ac.IsEstablishedOnApproach || ac.FlightRules.Equals("VFR", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return MvaDatabase.Default.Classify(ac.Position, ac.Altitude, MvaAtBandFt, mvaFacilityId).Relation;
    }

    /// <summary>
    /// The topmost field under (<paramref name="x"/>, <paramref name="y"/>), or null. A flashing field keeps its reserved rect
    /// in both blink phases; the EuroScope template hit-tests it only while its text shows, as today's tag does.
    /// </summary>
    public ScopeFieldHit? FieldAt(float x, float y)
    {
        bool onlyShownText = TemplateId == ScopeTemplates.EuroScopeId;
        for (int i = Tracks.Count - 1; i >= 0; i--)
        {
            ScopeTrack track = Tracks[i];
            foreach (FieldRect field in track.Block.Fields)
            {
                if ((onlyShownText && (field.Text.Length == 0)) || !field.Rect.Contains(x, y))
                {
                    continue;
                }
                return new ScopeFieldHit(track.Aircraft, field);
            }
        }
        return null;
    }

    /// <summary>The aircraft the pointer at (<paramref name="x"/>, <paramref name="y"/>) dwells on, or null (<see cref="HoverAt"/>).</summary>
    public AircraftModel? DwellAt(float x, float y) => HoverAt(x, y).Dwelled;

    /// <summary>
    /// What the pointer at (<paramref name="x"/>, <paramref name="y"/>) hovers, by CRC's pick precedence, each pass topmost block
    /// first (<c>ViewDataBlocks.HandleMouseMove</c>): within <see cref="DwellSymbolRadiusPx"/> of a symbol dwells its block;
    /// else inside a track's queued-commands badge, inflated <see cref="QueueBadgeHoverInflatePx"/>, shows its pending lines;
    /// else inside a block's VCI cell, inflated as CRC's <c>OnFrequencyIndicatorBounds</c>, previews its VCI, unless a plain
    /// track's cell lies under a dwell-locked block or an HSF indicator takes the point (<see cref="IndicatorsAt"/>); else
    /// inside a block's <see cref="ScopeTrack.DwellBox"/> dwells it. Separate from the click rule (<see cref="BlockAt"/>,
    /// <see cref="TargetAt"/>), save for the VCI it previews, which a left press toggles (<see cref="IndicatorPickAt"/>).
    /// </summary>
    public ScopeHoverHit HoverAt(float x, float y)
    {
        if (SymbolAt(x, y) is { } symbol)
        {
            return new ScopeHoverHit(symbol, null, null);
        }
        if (QueueBadgeAt(x, y) is { } queued)
        {
            return new ScopeHoverHit(null, null, queued);
        }
        if (IndicatorsAt(x, y).Vci is { } vci)
        {
            return new ScopeHoverHit(null, vci, null);
        }
        return new ScopeHoverHit(Topmost(t => t.DwellBox.Contains(x, y)), null, null);
    }

    private AircraftModel? SymbolAt(float x, float y)
    {
        var point = new SKPoint(x, y);
        return Topmost(t => SKPoint.Distance(t.Symbol, point) <= DwellSymbolRadiusPx);
    }

    private AircraftModel? QueueBadgeAt(float x, float y) => Topmost(t => (t.QueueBadge is { } badge) && QueueHoverBox(badge.Rect).Contains(x, y));

    /// <summary>
    /// The indicator under (<paramref name="x"/>, <paramref name="y"/>), at most one set, by CRC's two pick tiers
    /// (<c>ViewDataBlocks.IsDwelled</c>: a dwell-locked block, then a plain one) and its order within a tier
    /// (<c>PickPrecedenceManager</c>: the HSF indicator, then the on-frequency indicator, then the block's fields). The VCI
    /// owner is the topmost track whose inflated VCI cell holds the point, a plain track's cell skipped where a dwell-locked
    /// track's <see cref="ScopeTrack.DwellBox"/> holds it; then an HSF indicator of a block in that owner's tier or above,
    /// topmost first, takes the point from the VCI. A hover alone ranks nothing, and selection is no tier. Both null off every
    /// VCI cell.
    /// </summary>
    private (AircraftModel? Vci, AircraftModel? Hsf) IndicatorsAt(float x, float y)
    {
        bool underLockedBlock = Tracks.Any(t => t.IsDwellLocked && t.DwellBox.Contains(x, y));
        if (TopmostTrack(t => OwnsVciAt(t, x, y, underLockedBlock)) is not { } owner)
        {
            return (null, null);
        }

        AircraftModel? hsf = Topmost(t => HsfIndicatorWinsAt(t, owner, x, y));
        return (hsf is null) ? (owner.Aircraft, null) : (null, hsf);
    }

    // Whether the track's inflated VCI cell holds the point in a tier it may pick in: a plain track's cell not under a locked
    // block's dwell box.
    private static bool OwnsVciAt(ScopeTrack t, float x, float y, bool underLockedBlock) =>
        (t.Block.VciCell is { } cell) && VciHoverBox(cell).Contains(x, y) && (t.IsDwellLocked || !underLockedBlock);

    // Whether the track's HSF indicator holds the point in the VCI owner's tier or above. A plain owner already implies that no
    // locked dwell box holds the point, so a plain HSF block qualifies with it.
    private static bool HsfIndicatorWinsAt(ScopeTrack t, ScopeTrack owner, float x, float y) =>
        (t.IsDwellLocked || !owner.IsDwellLocked)
        && t.Block.Fields.Any(f =>
            (f.Action == ScopeFieldAction.ToggleHsf) && f.Style.HasFlag(ScopeFieldStyle.HsfIndicator) && f.Rect.Contains(x, y)
        );

    /// <summary>
    /// The ERAM indicator a left press at (<paramref name="x"/>, <paramref name="y"/>) picks ahead of the block fields
    /// (<see cref="IndicatorsAt"/>): an HSF indicator over a VCI cell in the owner's tier or above toggles the HSF; else the VCI
    /// cell, the one <see cref="HoverAt"/> previews, lit or not, toggles the VCI, as CRC picks <c>OnFrequencyIndicatorBounds</c>.
    /// Null near a symbol, on a queued-commands badge (which <see cref="HoverAt"/> ranks first) and off every VCI cell, so the
    /// press goes on to the block.
    /// </summary>
    public ScopeIndicatorPick? IndicatorPickAt(float x, float y)
    {
        if ((SymbolAt(x, y) is not null) || (QueueBadgeAt(x, y) is not null))
        {
            return null;
        }

        (AircraftModel? vci, AircraftModel? hsf) = IndicatorsAt(x, y);
        if (hsf is not null)
        {
            return new ScopeIndicatorPick(hsf, ScopeFieldAction.ToggleHsf);
        }
        return (vci is not null) ? new ScopeIndicatorPick(vci, ScopeFieldAction.ToggleVci) : null;
    }

    private static SKRect QueueHoverBox(SKRect rect) =>
        new(
            rect.Left - QueueBadgeHoverInflatePx,
            rect.Top - QueueBadgeHoverInflatePx,
            rect.Right + QueueBadgeHoverInflatePx,
            rect.Bottom + QueueBadgeHoverInflatePx
        );

    /// <summary>The VCI cell's hover box: CRC's <c>OnFrequencyIndicatorBounds</c>, 5 px out left and down, 3 px up and right.</summary>
    private static SKRect VciHoverBox(SKRect cell) => new(cell.Left - 5f, cell.Top - 3f, cell.Right + 3f, cell.Bottom + 5f);

    private AircraftModel? Topmost(Func<ScopeTrack, bool> hit) => TopmostTrack(hit)?.Aircraft;

    private ScopeTrack? TopmostTrack(Func<ScopeTrack, bool> hit)
    {
        for (int i = Tracks.Count - 1; i >= 0; i--)
        {
            if (hit(Tracks[i]))
            {
                return Tracks[i];
            }
        }
        return null;
    }

    /// <summary>The aircraft whose block (topmost first) contains (<paramref name="x"/>, <paramref name="y"/>), or null.</summary>
    public AircraftModel? BlockAt(float x, float y)
    {
        for (int i = Tracks.Count - 1; i >= 0; i--)
        {
            if (Tracks[i].Block.Bounds.Contains(x, y))
            {
                return Tracks[i].Aircraft;
            }
        }
        return null;
    }

    /// <summary>
    /// The aircraft whose symbol is nearest (<paramref name="x"/>, <paramref name="y"/>) within <see cref="TargetHitRadiusPx"/>,
    /// or null.
    /// </summary>
    public AircraftModel? TargetAt(float x, float y)
    {
        AircraftModel? nearest = null;
        float best = TargetHitRadiusPx;
        foreach (ScopeTrack track in Tracks)
        {
            float dist = SKPoint.Distance(track.Symbol, new SKPoint(x, y));
            if (dist < best)
            {
                best = dist;
                nearest = track.Aircraft;
            }
        }
        return nearest;
    }

    private static AircraftModel? Peer(Dictionary<string, AircraftModel> byCallsign, string? callsign) =>
        string.IsNullOrEmpty(callsign) ? null : byCallsign.GetValueOrDefault(callsign);
}

/// <summary>What the render thread draws one scope frame from. Built on the UI thread; immutable.</summary>
/// <param name="Frame">The laid-out tracks.</param>
/// <param name="VideoMaps">Video maps to draw.</param>
/// <param name="BrightnessLookup">Brightness category ("A" or "B") by map id; a copy the render thread owns.</param>
/// <param name="Brightness">The scope's BRITE groups: the maps by category, the symbols, the blocks, line 4, the alerts and DWELL.</param>
/// <param name="FontSize">Datablock font size the frame was measured with.</param>
/// <param name="Palette">The user's block colours, read from the preferences on the UI thread.</param>
/// <param name="RangeBearingLines">The measure lines taken on the Scope, resolved for this frame.</param>
/// <param name="PendingRangeBearingLine">The half-placed measure line picked on the Scope, to the cursor; null for none.</param>
/// <param name="RblReadoutRects">Each measure readout's placed rect, by slot (0 for the half-placed line).</param>
/// <param name="MinSep">The STARS min-sep pair and its projection; null for none.</param>
public sealed record ScopeRenderSnapshot(
    ScopeFrame Frame,
    IReadOnlyList<VideoMapData> VideoMaps,
    IReadOnlyDictionary<string, string> BrightnessLookup,
    ScopeBrightness Brightness,
    float FontSize,
    ScopePalette Palette,
    IReadOnlyList<ResolvedRbl> RangeBearingLines,
    ResolvedRbl? PendingRangeBearingLine,
    IReadOnlyDictionary<int, SKRect> RblReadoutRects,
    ScopeMinSep? MinSep
);

/// <summary>The user-chosen colours a frame draws with; the render thread reads no preferences.</summary>
/// <param name="AssignedToMe">The assignment tint, or null when it is off or its colour does not parse.</param>
/// <param name="AssignedToOther">The unassigned tint, or null when it is off or its colour does not parse.</param>
/// <param name="SelectedSymbol">The selected track's symbol colour.</param>
public sealed record ScopePalette(SKColor? AssignedToMe, SKColor? AssignedToOther, SKColor SelectedSymbol);

/// <summary>
/// Draws a <see cref="ScopeRenderSnapshot"/> back to front: background, video maps, every track's rings (J-ring, DRI halo,
/// cone, then ATPA cone), every track's history dots, every track's vector line, then each track's symbol, leader and
/// block, then the pilot lens (each track's intent path, queued-commands badge and check-in tag, then a hovered badge's lines), then the
/// measure lines and their readouts. Every block field is drawn from its <see cref="FieldRect"/>; the presenter has already
/// blanked a flashing field's off-phase text, so nothing here flashes.
/// </summary>
public sealed class ScopeRenderer : IDisposable
{
    private const float SymbolRadius = 5f;

    private static readonly SKColor SymbolColor = new(0, 176, 255);

    // The history dots' colour, as the Radar's history trails.
    private static readonly SKColor HistoryColor = new(0, 176, 255);

    private static readonly SKColor VectorColor = SKColors.White;

    // The pilot lens's colour: apart from every other Scope colour, so an intent path never reads as a map, a track or an alert.
    private static readonly SKColor LensColor = SKColors.Magenta;

    // The half-size of an intent path's fix diamond, and its label lines' spacing, as the Radar's nav route draws them.
    private const float IntentDiamondSize = 5f;
    private const float IntentLabelLineHeight = 11f;

    // A hovered queued-commands badge's line box: its gap below the badge and its padding around the lines.
    private const float QueueDetailGapPx = 2f;
    private const float QueueDetailPadPx = 3f;

    // The size labels' font size, as the Radar's TPA labels.
    private const float RingLabelSize = 11f;

    /// <summary>The VCI's three arc radii as fractions of the largest.</summary>
    private static readonly float[] VciArcScales = [1f / 3f, 2f / 3f, 1f];

    /// <summary>The ≠ bars' heights above the baseline as fractions of the cap height, the upper first.</summary>
    private static readonly float[] NotEqualBarHeights = [0.62f, 0.30f];
    private static readonly SKColor BlockColor = SKColors.White;
    private static readonly SKColor SelectedBorderColor = SKColors.White;
    private static readonly SKColor HighlightColor = SKColors.Cyan;

    // The student's STARS block colours, as today's radar mirrors them from CRC (DisplayElementTracks).
    private static readonly SKColor StarsOwnedColor = SKColors.White;
    private static readonly SKColor StarsUnownedColor = new(50, 205, 50);
    private static readonly SKColor StarsPointoutColor = SKColors.Yellow;
    private static readonly SKColor StarsHighlightColor = SKColors.Cyan;
    private static readonly SKColor NoteColor = new(255, 200, 60);
    private static readonly SKColor NoLandingClearanceColor = SKColors.Red;
    private static readonly SKColor ConflictAlertColor = new(255, 60, 60);
    private static readonly SKColor AtpaWarningColor = SKColors.Yellow;
    private static readonly SKColor AtpaAlertColor = new(255, 55, 0);
    private static readonly SKColor TypeMismatchColor = new(0xFF, 0xB0, 0x30);
    private static readonly SKColor MvaBelowColor = new(255, 90, 90);
    private static readonly SKColor MvaAtColor = new(255, 200, 60);

    // The squawk pulse's off-phase brightness, as today's radar (CRC ApplyColorBrightness(color, 25)).
    private const float DimTailBrightness = 0.25f;

    // A coasting live-traffic shadow's alpha, as today's radar.
    private const byte StaleAlpha = 128;

    private readonly VideoMapRenderer _videoMapRenderer = new();
    private readonly RangeBearingRenderer _rangeBearingRenderer = new();

    private readonly MinSepRenderer _minSepRenderer = new();

    private readonly SKPaint _symbolPaint = new()
    {
        StrokeWidth = 1.5f,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _leaderPaint = new()
    {
        Color = BlockColor,
        StrokeWidth = 2,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _selectedBorderPaint = new()
    {
        Color = SelectedBorderColor,
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    // CRC's dwell emphasis box: a 1 px outline in the block's own colour (FdbRenderObject, StyleManager.FdbEmphasisBox).
    private readonly SKPaint _dwellBoxPaint = new()
    {
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _historyPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    private readonly SKPaint _vectorPaint = new()
    {
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _ringPaint = new()
    {
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _ringLabelBackingPaint = new() { Color = SKColors.Black, Style = SKPaintStyle.Fill };

    private readonly SKPaint _ringTextPaint = new() { IsAntialias = true };

    private readonly SKFont _ringLabelFont = PlatformHelper.MonospaceFontBold(RingLabelSize);

    private readonly SKPaint _textPaint = new() { IsAntialias = true };

    // Antialiased like today's radar strike paint, and outside the radar line style: its width comes from the font each draw.
    private readonly SKPaint _strikethroughPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };

    // The ERAM climb/descend arrow, drawn as a path in its monospace cell; its width comes from the font each draw.
    private readonly SKPaint _arrowPaint = new()
    {
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
    };

    // The pilot lens's intent path, as the Radar's "Show nav route" draws it: solid legs and diamonds, a dashed leader from
    // the symbol, dashed tails and open-ended shapes.
    private readonly SKPaint _lensLinePaint = new()
    {
        StrokeWidth = 2,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
    };

    private readonly SKPaint _lensLeaderPaint = new()
    {
        StrokeWidth = 1,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash([6, 4], 0),
    };

    private readonly SKPaint _lensDashPaint = new()
    {
        StrokeWidth = 2,
        Style = SKPaintStyle.Stroke,
        IsAntialias = true,
        PathEffect = SKPathEffect.CreateDash([10, 5], 0),
    };

    private readonly SKPaint _lensFillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

    private readonly SKPaint _lensTextPaint = new() { IsAntialias = true };

    // The box a hovered queued-commands badge's lines draw on.
    private readonly SKPaint _lensBackingPaint = new() { Color = new SKColor(0, 0, 0, 180), Style = SKPaintStyle.Fill };

    private readonly SKFont _lensLabelFont = PlatformHelper.MonospaceFont(12);

    private readonly SKFont _lensRestrictionFont = PlatformHelper.MonospaceFont(10);

    // Width of two spaces in the restriction font: the gap between a label line's tokens.
    private readonly float _lensRestrictionTokenGap;

    private readonly SKPath _lensPath = new();

    // Stroke paints drawn in the radar's line style (no AA, device-pixel width) each frame; each carries its base width in DIPs.
    private readonly (SKPaint Paint, float BaseWidth)[] _strokePaints;

    private SKFont? _font;

    public ScopeRenderer()
    {
        _strokePaints =
        [
            (_symbolPaint, 1.5f),
            (_leaderPaint, 2f),
            (_selectedBorderPaint, 1f),
            (_dwellBoxPaint, 1f),
            (_vectorPaint, 1f),
            (_ringPaint, 1f),
            (_lensLinePaint, 2f),
            (_lensLeaderPaint, 1f),
            (_lensDashPaint, 2f),
        ];
        _lensRestrictionTokenGap = _lensRestrictionFont.MeasureText("  ");
    }

    /// <summary>Draws <paramref name="snapshot"/> into <paramref name="canvas"/>.</summary>
    public void Render(SKCanvas canvas, MapViewport vp, ScopeRenderSnapshot snapshot)
    {
        MapLineStyle.Apply(_strokePaints, MapLineStyle.GetScale(canvas));
        canvas.Clear(SKColors.Black);
        _videoMapRenderer.BrightnessA = snapshot.Brightness.MapA / 100f;
        _videoMapRenderer.BrightnessB = snapshot.Brightness.MapB / 100f;
        _videoMapRenderer.Render(canvas, vp, snapshot.VideoMaps, snapshot.BrightnessLookup);

        foreach (ScopeTrack track in snapshot.Frame.Tracks)
        {
            DrawRings(canvas, track, snapshot.Brightness.Rings);
        }

        foreach (ScopeTrack track in snapshot.Frame.Tracks)
        {
            DrawHistory(canvas, track, snapshot.Brightness.Hist);
        }

        foreach (ScopeTrack track in snapshot.Frame.Tracks)
        {
            DrawVector(canvas, track, snapshot.Brightness.Vector);
        }

        SKFont font = FontFor(snapshot.FontSize);
        foreach (ScopeTrack track in snapshot.Frame.Tracks)
        {
            DrawTrack(canvas, track, font, snapshot.Palette, snapshot.Brightness);
            DrawCrrReadout(canvas, track, font, snapshot.Brightness);
        }

        // The pilot lens draws over targets and blocks, under the measure layer; a hovered badge's lines over every badge.
        foreach (ScopeTrack track in snapshot.Frame.Tracks)
        {
            DrawIntentPath(canvas, track, snapshot.Brightness.Lens);
            DrawQueueBadge(canvas, track, font, snapshot.Brightness.Lens);
            DrawCheckInTag(canvas, track, font, snapshot.Brightness.Lens);
        }

        foreach (ScopeTrack track in snapshot.Frame.Tracks)
        {
            DrawQueueDetail(canvas, track, font, snapshot.Brightness.Lens);
        }

        // Last, so measurements stay legible over targets and blocks: white and unscaled by BRITE, as on the Radar.
        _rangeBearingRenderer.Draw(canvas, vp, snapshot.RangeBearingLines, snapshot.PendingRangeBearingLine, snapshot.RblReadoutRects);
        _minSepRenderer.Draw(canvas, vp, snapshot.MinSep);
    }

    /// <summary>
    /// Draws <paramref name="track"/>'s history dots at HIST, as the Radar's history trails: the oldest faintest, the alpha
    /// ramping to full at the newest. DWELL never lifts them.
    /// </summary>
    private void DrawHistory(SKCanvas canvas, ScopeTrack track, int percent)
    {
        if (track.History is not { Count: > 0 } dots)
        {
            return;
        }

        SKColor color = ScopeBrightness.Apply(HistoryColor, percent);
        for (int i = 0; i < dots.Count; i++)
        {
            _historyPaint.Color = color.WithAlpha((byte)(255 * (i + 1) / dots.Count));
            canvas.DrawCircle(dots[i].X, dots[i].Y, SymbolRadius / 2f, _historyPaint);
        }
    }

    /// <summary>
    /// Draws <paramref name="track"/>'s vector line from its symbol at VECTOR, at half alpha for a stale track; DWELL never
    /// lifts it.
    /// </summary>
    private void DrawVector(SKCanvas canvas, ScopeTrack track, int percent)
    {
        if (track.VectorEnd is not { } end)
        {
            return;
        }

        SKColor color = ScopeBrightness.Apply(VectorColor, percent);
        _vectorPaint.Color = track.IsStale ? color.WithAlpha(StaleAlpha) : color;
        canvas.DrawLine(track.Symbol, end, _vectorPaint);
    }

    /// <summary>
    /// Draws <paramref name="track"/>'s ring layer at RINGS, at half alpha for a stale track: the J-ring and the DRI halo,
    /// the manual cone, then the ATPA cone in its state's colour, each size label on a black box. DWELL never lifts them.
    /// </summary>
    private void DrawRings(SKCanvas canvas, ScopeTrack track, int percent)
    {
        ScopeRingShapes rings = track.Rings;
        if (rings.IsEmpty)
        {
            return;
        }

        SKColor color = RingShade(TargetColors.TpaColor, track, percent);
        if (rings.JRing is { } ring)
        {
            DrawPolyline(canvas, ring.Points, color);
            DrawRingLabel(canvas, ring.LabelAt, ring.Label, color);
        }

        foreach (IReadOnlyList<SKPoint> arc in rings.DriArcs)
        {
            DrawPolyline(canvas, arc, color);
        }

        if (rings.Cone is { } cone)
        {
            DrawWedge(canvas, cone, color);
        }

        if (rings.AtpaCone is { } atpa)
        {
            DrawWedge(canvas, atpa, RingShade(TargetColors.AtpaConeColorFor(rings.AtpaState), track, percent));
        }
    }

    /// <summary><paramref name="color"/> at <paramref name="percent"/>, at the stale alpha when <paramref name="track"/> coasts.</summary>
    private static SKColor RingShade(SKColor color, ScopeTrack track, int percent) => Stale(track, ScopeBrightness.Apply(color, percent));

    private void DrawPolyline(SKCanvas canvas, IReadOnlyList<SKPoint> points, SKColor color)
    {
        using var path = new SKPath();
        path.MoveTo(points[0]);
        for (int i = 1; i < points.Count; i++)
        {
            path.LineTo(points[i]);
        }

        _ringPaint.Color = color;
        canvas.DrawPath(path, _ringPaint);
    }

    /// <summary>Strokes <paramref name="wedge"/>: apex, left, tip, right and back, then its length label at mid-axis.</summary>
    private void DrawWedge(SKCanvas canvas, ScopeWedge wedge, SKColor color)
    {
        using var path = new SKPath();
        path.MoveTo(wedge.Apex);
        path.LineTo(wedge.Left);
        path.LineTo(wedge.Tip);
        path.LineTo(wedge.Right);
        path.Close();
        _ringPaint.Color = color;
        canvas.DrawPath(path, _ringPaint);
        DrawRingLabel(canvas, wedge.LabelAt, wedge.Label, color);
    }

    /// <summary>Draws <paramref name="text"/> centred on <paramref name="center"/> on a black box, as the Radar's TPA labels.</summary>
    private void DrawRingLabel(SKCanvas canvas, SKPoint center, string text, SKColor color)
    {
        float width = _ringLabelFont.MeasureText(text);
        float height = _ringLabelFont.Size;
        float x = center.X - (width / 2f);
        float baseline = center.Y + (height / 2f);
        canvas.DrawRect(x - 2f, baseline - height - 1f, width + 4f, height + 4f, _ringLabelBackingPaint);
        _ringTextPaint.Color = color;
        canvas.DrawText(text, x, baseline, SKTextAlign.Left, _ringLabelFont, _ringTextPaint);
    }

    public void Dispose()
    {
        _videoMapRenderer.Dispose();
        _rangeBearingRenderer.Dispose();
        _minSepRenderer.Dispose();
        _ringPaint.Dispose();
        _ringLabelBackingPaint.Dispose();
        _ringTextPaint.Dispose();
        _ringLabelFont.Dispose();
        _symbolPaint.Dispose();
        _leaderPaint.Dispose();
        _selectedBorderPaint.Dispose();
        _dwellBoxPaint.Dispose();
        _historyPaint.Dispose();
        _vectorPaint.Dispose();
        _textPaint.Dispose();
        _strikethroughPaint.Dispose();
        _arrowPaint.Dispose();
        _lensLinePaint.Dispose();
        _lensLeaderPaint.Dispose();
        _lensDashPaint.Dispose();
        _lensFillPaint.Dispose();
        _lensTextPaint.Dispose();
        _lensBackingPaint.Dispose();
        _lensLabelFont.Dispose();
        _lensRestrictionFont.Dispose();
        _lensPath.Dispose();
        _font?.Dispose();
    }

    /// <summary>
    /// Draws <paramref name="track"/>'s intent path at LENS, as the Radar's "Show nav route" draws it: each segment's dashed
    /// leader from the symbol, its legs, a diamond with name and crossing-restriction lines at each named fix, and its vector
    /// tail, then the shapes. DWELL never lifts it; LENS 0 draws nothing.
    /// </summary>
    private void DrawIntentPath(SKCanvas canvas, ScopeTrack track, int percent)
    {
        if ((track.IntentPath is not { } path) || (percent <= 0))
        {
            return;
        }

        SKColor color = ScopeBrightness.Apply(LensColor, percent);
        _lensLinePaint.Color = color;
        _lensDashPaint.Color = color;
        _lensFillPaint.Color = color;
        _lensLeaderPaint.Color = color.WithAlpha(150);
        foreach (ScopeIntentSegment segment in path.Segments)
        {
            DrawIntentSegment(canvas, segment, color);
        }

        foreach (ScopeIntentShape shape in path.Shapes)
        {
            DrawIntentShape(canvas, shape, color);
        }
    }

    /// <summary>
    /// Draws <paramref name="track"/>'s queued-commands badge at LENS: its count in the track font, bottom-aligned in its text
    /// box. DWELL never lifts it; LENS 0 draws nothing.
    /// </summary>
    private void DrawQueueBadge(SKCanvas canvas, ScopeTrack track, SKFont font, int percent)
    {
        if ((track.QueueBadge is not { } badge) || (percent <= 0))
        {
            return;
        }

        _lensTextPaint.Color = ScopeBrightness.Apply(LensColor, percent);
        string text = badge.Count.ToString(CultureInfo.InvariantCulture);
        canvas.DrawText(text, badge.Rect.Left, badge.Rect.Bottom, SKTextAlign.Left, font, _lensTextPaint);
    }

    /// <summary>
    /// Draws <paramref name="track"/>'s range data block in its group's colour at FDB, lifted by DWELL when the track is
    /// dwelled as its block is, its baseline at the bottom of its text box. FDB 0 draws nothing.
    /// </summary>
    private void DrawCrrReadout(SKCanvas canvas, ScopeTrack track, SKFont font, ScopeBrightness brightness)
    {
        int percent = Lifted(track, brightness.Fdb, brightness.Dwell);
        if ((track.CrrReadout is not { } crr) || (percent <= 0))
        {
            return;
        }

        _textPaint.Color = ScopeBrightness.Apply(crr.Color, percent);
        canvas.DrawText(crr.Text, crr.Rect.Left, crr.Rect.Bottom, SKTextAlign.Left, font, _textPaint);
    }

    /// <summary>
    /// Draws <paramref name="track"/>'s check-in tag at LENS in the track font, its text box's left edge
    /// <see cref="ScopeCheckIn.SymbolGapPx"/> right of the symbol and its top as far below it. LENS 0 draws nothing.
    /// </summary>
    private void DrawCheckInTag(SKCanvas canvas, ScopeTrack track, SKFont font, int percent)
    {
        if ((track.CheckInTag is not { } tag) || (percent <= 0))
        {
            return;
        }

        _lensTextPaint.Color = ScopeBrightness.Apply(LensColor, percent);
        float left = track.Symbol.X + ScopeCheckIn.SymbolGapPx;
        float baseline = track.Symbol.Y + ScopeCheckIn.SymbolGapPx + font.Size;
        canvas.DrawText(tag, left, baseline, SKTextAlign.Left, font, _lensTextPaint);
    }

    /// <summary>
    /// Draws the pending lines of <paramref name="track"/>'s hovered queued-commands badge at LENS, one per line in the track
    /// font, on a dark box whose top-left sits just below the badge's bottom-left. LENS 0 draws nothing.
    /// </summary>
    private void DrawQueueDetail(SKCanvas canvas, ScopeTrack track, SKFont font, int percent)
    {
        if ((track.QueueBadge is not { Hovered: true } badge) || (percent <= 0))
        {
            return;
        }

        float width = 0f;
        foreach (string line in badge.Lines)
        {
            width = Math.Max(width, font.MeasureText(line));
        }

        float left = badge.Rect.Left;
        float top = badge.Rect.Bottom + QueueDetailGapPx;
        float height = (badge.Lines.Count * font.Spacing) + (2f * QueueDetailPadPx);
        canvas.DrawRect(left, top, width + (2f * QueueDetailPadPx), height, _lensBackingPaint);
        _lensTextPaint.Color = ScopeBrightness.Apply(LensColor, percent);
        for (int i = 0; i < badge.Lines.Count; i++)
        {
            float baseline = top + QueueDetailPadPx + font.Size + (i * font.Spacing);
            canvas.DrawText(badge.Lines[i], left + QueueDetailPadPx, baseline, SKTextAlign.Left, font, _lensTextPaint);
        }
    }

    private void DrawIntentSegment(SKCanvas canvas, ScopeIntentSegment segment, SKColor color)
    {
        if ((segment.LeaderFrom is { } from) && (segment.Points.Count > 0))
        {
            canvas.DrawLine(from, segment.Points[0], _lensLeaderPaint);
        }

        for (int i = 1; i < segment.Points.Count; i++)
        {
            canvas.DrawLine(segment.Points[i - 1], segment.Points[i], _lensLinePaint);
        }

        foreach (ScopeIntentFix fix in segment.Fixes)
        {
            DrawIntentFix(canvas, fix, color);
        }

        if (segment.Tail is { } tail)
        {
            DrawIntentTail(canvas, tail);
        }
    }

    /// <summary>
    /// Draws a fix's diamond and name, its crossing-restriction lines stacked under the name, each line's tokens left to
    /// right two spaces apart, a latent token (published, not in force now) dimmed.
    /// </summary>
    private void DrawIntentFix(SKCanvas canvas, ScopeIntentFix fix, SKColor color)
    {
        const float d = IntentDiamondSize;
        float x = fix.At.X;
        float y = fix.At.Y;
        canvas.DrawLine(x, y - d, x + d, y, _lensLinePaint);
        canvas.DrawLine(x + d, y, x, y + d, _lensLinePaint);
        canvas.DrawLine(x, y + d, x - d, y, _lensLinePaint);
        canvas.DrawLine(x - d, y, x, y - d, _lensLinePaint);
        _lensTextPaint.Color = color;
        canvas.DrawText(fix.Name, x + 8, y - 4, SKTextAlign.Left, _lensLabelFont, _lensTextPaint);
        if (fix.Restrictions is not { Count: > 0 } lines)
        {
            return;
        }

        float ly = y + 8;
        foreach (IReadOnlyList<NavRouteRestrictionToken> line in lines)
        {
            float tx = x + 8;
            foreach (NavRouteRestrictionToken token in line)
            {
                _lensTextPaint.Color = token.Latent ? color.WithAlpha(115) : color;
                canvas.DrawText(token.Text, tx, ly, SKTextAlign.Left, _lensRestrictionFont, _lensTextPaint);
                tx += _lensRestrictionFont.MeasureText(token.Text) + _lensRestrictionTokenGap;
            }

            ly += IntentLabelLineHeight;
        }
    }

    /// <summary>Draws a shape as one path, dashed or solid as it says, its arrowhead when it has one, and its label lines.</summary>
    private void DrawIntentShape(SKCanvas canvas, ScopeIntentShape shape, SKColor color)
    {
        if (shape.Points.Count >= 2)
        {
            _lensPath.Reset();
            _lensPath.MoveTo(shape.Points[0]);
            for (int i = 1; i < shape.Points.Count; i++)
            {
                _lensPath.LineTo(shape.Points[i]);
            }

            canvas.DrawPath(_lensPath, shape.Dashed ? _lensDashPaint : _lensLinePaint);
            if (shape.Arrow)
            {
                DrawIntentArrowhead(canvas, shape.Points[^2], shape.Points[^1]);
            }
        }

        if (shape.LabelAt is not { } at)
        {
            return;
        }

        _lensTextPaint.Color = color;
        float ty = at.Y - 4;
        foreach (string label in shape.Labels)
        {
            canvas.DrawText(label, at.X + 8, ty, SKTextAlign.Left, _lensRestrictionFont, _lensTextPaint);
            ty += IntentLabelLineHeight;
        }
    }

    /// <summary>Draws a vector tail: a dashed line with a filled arrowhead at its tip, turned to the line's screen direction.</summary>
    private void DrawIntentTail(SKCanvas canvas, ScopeIntentTail tail)
    {
        canvas.DrawLine(tail.From, tail.To, _lensDashPaint);
        DrawIntentArrowhead(canvas, tail.From, tail.To);
    }

    /// <summary>
    /// Draws a filled isosceles arrowhead with its tip at <paramref name="to"/>, turned along the screen direction from
    /// <paramref name="from"/>; nothing when the two are under a pixel apart.
    /// </summary>
    private void DrawIntentArrowhead(SKCanvas canvas, SKPoint from, SKPoint to)
    {
        const float arrowLen = 14f;
        const float arrowHalfWidth = 7f;
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float len = MathF.Sqrt((dx * dx) + (dy * dy));
        if (len < 1f)
        {
            return;
        }

        float ux = dx / len;
        float uy = dy / len;
        float baseX = to.X - (ux * arrowLen);
        float baseY = to.Y - (uy * arrowLen);
        float perpX = -uy * arrowHalfWidth;
        float perpY = ux * arrowHalfWidth;
        _lensPath.Reset();
        _lensPath.MoveTo(to);
        _lensPath.LineTo(baseX + perpX, baseY + perpY);
        _lensPath.LineTo(baseX - perpX, baseY - perpY);
        _lensPath.Close();
        canvas.DrawPath(_lensPath, _lensFillPaint);
    }

    private SKFont FontFor(float size)
    {
        if ((_font is null) || (Math.Abs(_font.Size - size) > 0.01f))
        {
            _font?.Dispose();
            _font = PlatformHelper.MonospaceFontBold(size);
        }
        return _font;
    }

    /// <summary>
    /// Draws <paramref name="track"/>: its symbol at TARGETS, its leader, dwell box and fields at their groups (see
    /// <see cref="FieldPercent"/>), each lifted by DWELL when the track is dwelled; the selection border never dims.
    /// </summary>
    private void DrawTrack(SKCanvas canvas, ScopeTrack track, SKFont font, ScopePalette palette, ScopeBrightness brightness)
    {
        _symbolPaint.Color = ScopeBrightness.Apply(SymbolColorOf(track, palette), Lifted(track, brightness.Targets, brightness.Dwell));
        canvas.DrawCircle(track.Symbol.X, track.Symbol.Y, SymbolRadius, _symbolPaint);

        if (track.IsSelected)
        {
            canvas.DrawRect(SelectionBorder(track), _selectedBorderPaint);
        }

        SKColor rawBlockColor = BlockColorOf(track, palette);
        SKColor blockColor = ScopeBrightness.Apply(rawBlockColor, Lifted(track, brightness.Fdb, brightness.Dwell));
        if (track.IsDwelled)
        {
            _dwellBoxPaint.Color = blockColor;
            canvas.DrawRect(track.DwellBox, _dwellBoxPaint);
        }

        if (track.ShowsLeader)
        {
            _leaderPaint.Color = blockColor;
            canvas.DrawLine(track.Symbol, LeaderEnd(track), _leaderPaint);
        }

        foreach (FieldRect field in track.Block.Fields)
        {
            if (field.Text.Length > 0)
            {
                int percent = Lifted(track, FieldPercent(field, brightness), brightness.Dwell);
                DrawField(canvas, field, font, ScopeBrightness.Apply(ColorFor(field.Token, rawBlockColor), percent));
            }
        }
    }

    /// <summary>
    /// <paramref name="percent"/>, lifted by <paramref name="dwell"/> when <paramref name="track"/> is dwelled.
    /// </summary>
    private static int Lifted(ScopeTrack track, int percent, int dwell) => track.IsDwelled ? ScopeBrightness.Lift(percent, dwell) : percent;

    /// <summary>
    /// The group percent <paramref name="field"/> draws at before the dwell lift: the ERAM line 4 at FDB plus LINE 4, clamped
    /// to 0 to 100; an alert token at ALERTS; any other field at FDB.
    /// </summary>
    internal static int FieldPercent(FieldRect field, ScopeBrightness brightness)
    {
        if (field.Slot.IsLine4())
        {
            return Math.Clamp(brightness.Fdb + brightness.Line4, 0, 100);
        }

        return IsAlertToken(field.Token) ? brightness.Alerts : brightness.Fdb;
    }

    /// <summary>Whether <paramref name="token"/> is one the ALERTS group dims: conflict, no landing clearance, ATPA.</summary>
    private static bool IsAlertToken(ScopeColorToken token) =>
        token is ScopeColorToken.ConflictAlert or ScopeColorToken.NoLandingClearance or ScopeColorToken.AtpaWarning or ScopeColorToken.AtpaAlert;

    /// <summary>
    /// Draws <paramref name="field"/> in <paramref name="color"/>, its token's colour at its brightness; the squawk-mismatch
    /// pulse's off phase dims its tail, dwelled or not.
    /// </summary>
    private void DrawField(SKCanvas canvas, FieldRect field, SKFont font, SKColor color)
    {
        if (field.Style.HasFlag(ScopeFieldStyle.DimTail))
        {
            DrawDimTail(canvas, field, font, color, Dim(color, DimTailBrightness));
        }
        else
        {
            _textPaint.Color = color;
            // A field rect spans the font's ascent budget above its baseline, so the baseline is the rect's bottom.
            canvas.DrawText(field.Text, field.Rect.Left, field.Rect.Bottom, SKTextAlign.Left, font, _textPaint);
        }

        if (field.Style.HasFlag(ScopeFieldStyle.Strikethrough))
        {
            DrawStrikethrough(canvas, field, font, color);
        }

        if ((field.Style & (ScopeFieldStyle.ArrowUp | ScopeFieldStyle.ArrowDown)) != 0)
        {
            bool up = field.Style.HasFlag(ScopeFieldStyle.ArrowUp);
            DrawCellPath(canvas, field, font, color, cell => ArrowPath(field, font, cell, up));
        }

        if (field.Style.HasFlag(ScopeFieldStyle.HsfIndicator))
        {
            DrawCellPath(canvas, field, font, color, cell => HsfPath(field, font, cell));
        }

        if (field.Style.HasFlag(ScopeFieldStyle.Vci))
        {
            DrawCellPath(canvas, field, font, color, cell => VciPath(field, font, cell));
        }

        if (field.Style.HasFlag(ScopeFieldStyle.AltitudeMismatch))
        {
            DrawCellPath(canvas, field, font, color, cell => NotEqualPath(field, font, cell));
        }
    }

    /// <summary>
    /// Strokes the glyph <paramref name="buildPath"/> builds in the monospace cell <see cref="TrackPresenter.EramArrowCell"/>
    /// holds in <paramref name="field"/>'s text (the arrow, the HSF indicator, the VCI); nothing when the text holds no cell.
    /// </summary>
    private void DrawCellPath(SKCanvas canvas, FieldRect field, SKFont font, SKColor color, Func<int, SKPath> buildPath)
    {
        int cell = field.Text.IndexOf(TrackPresenter.EramArrowCell, StringComparison.Ordinal);
        if (cell < 0)
        {
            return;
        }

        using SKPath path = buildPath(cell);
        _arrowPaint.Color = color;
        _arrowPaint.StrokeWidth = MathF.Max(1f, font.Size / 10f);
        canvas.DrawPath(path, _arrowPaint);
    }

    /// <summary>
    /// Where cell <paramref name="cell"/> of <paramref name="field"/> sits: its left edge, its width and the font's cap height
    /// (seven tenths of the size when the font reports none).
    /// </summary>
    private static (float Left, float Width, float CapHeight) CellOf(FieldRect field, SKFont font, int cell) =>
        (
            field.Rect.Left + font.MeasureText(field.Text[..cell]),
            font.MeasureText(TrackPresenter.EramArrowCell),
            font.Metrics.CapHeight > 0 ? font.Metrics.CapHeight : font.Size * 0.7f
        );

    /// <summary>
    /// The VCI path in cell <paramref name="cell"/> of <paramref name="field"/>: three concentric quarter arcs about the cell's
    /// bottom-left corner, opening up and right, the largest as wide as the cell or as tall as the cap height.
    /// </summary>
    internal static SKPath VciPath(FieldRect field, SKFont font, int cell)
    {
        (float left, float width, float capHeight) = CellOf(field, font, cell);
        float radius = MathF.Min(width, capHeight) * 0.9f;
        float cx = left + (width * 0.05f);
        float cy = field.Rect.Bottom;

        var path = new SKPath();
        foreach (float scale in VciArcScales)
        {
            float r = radius * scale;
            path.AddArc(new SKRect(cx - r, cy - r, cx + r, cy + r), -90f, 90f);
        }
        return path;
    }

    /// <summary>
    /// The ↴ path in cell <paramref name="cell"/> of <paramref name="field"/>: a stroke rightwards a quarter of the cap height
    /// below the cap line, then down to the baseline, with a downward head at its foot.
    /// </summary>
    internal static SKPath HsfPath(FieldRect field, SKFont font, int cell)
    {
        (float left, float width, float capHeight) = CellOf(field, font, cell);
        float bottom = field.Rect.Bottom;
        float y = bottom - (capHeight * 0.75f);
        float x = left + (width * 0.65f);
        float head = width * 0.3f;

        var path = new SKPath();
        path.MoveTo(left + (width * 0.1f), y);
        path.LineTo(x, y);
        path.LineTo(x, bottom);
        path.MoveTo(x - head, bottom - head);
        path.LineTo(x, bottom);
        path.LineTo(x + head, bottom - head);
        return path;
    }

    /// <summary>
    /// The ≠ path in cell <paramref name="cell"/> of <paramref name="field"/>: two bars across 0.12 to 0.88 of the cell at 0.62
    /// and 0.30 of the cap height above the baseline, the upper first, then a diagonal from 0.72 of the cell at 0.95 of the cap
    /// height down to 0.28 of the cell a twentieth of the cap height below the baseline.
    /// </summary>
    internal static SKPath NotEqualPath(FieldRect field, SKFont font, int cell)
    {
        (float left, float width, float capHeight) = CellOf(field, font, cell);
        float baseline = field.Rect.Bottom;
        float barLeft = left + (width * 0.12f);
        float barRight = left + (width * 0.88f);

        var path = new SKPath();
        foreach (float height in NotEqualBarHeights)
        {
            float y = baseline - (capHeight * height);
            path.MoveTo(barLeft, y);
            path.LineTo(barRight, y);
        }
        path.MoveTo(left + (width * 0.72f), baseline - (capHeight * 0.95f));
        path.LineTo(left + (width * 0.28f), baseline + (capHeight * 0.05f));
        return path;
    }

    /// <summary>
    /// The ERAM climb or descend arrow's path in cell <paramref name="cell"/> of <paramref name="field"/>: a shaft from the
    /// baseline to the cap height with a head at the pointed end.
    /// </summary>
    internal static SKPath ArrowPath(FieldRect field, SKFont font, int cell, bool up)
    {
        (float left, float width, float capHeight) = CellOf(field, font, cell);
        float bottom = field.Rect.Bottom;
        float top = bottom - capHeight;
        float x = left + (width / 2f);
        float head = width * 0.35f;
        float tip = up ? top : bottom;
        float barb = up ? top + head : bottom - head;

        var path = new SKPath();
        path.MoveTo(x, bottom);
        path.LineTo(x, top);
        path.MoveTo(x - head, barb);
        path.LineTo(x, tip);
        path.LineTo(x + head, barb);
        return path;
    }

    /// <summary>
    /// Draws the text before the first space at <paramref name="color"/>, then the rest (its leading space included) at
    /// <paramref name="tailColor"/>, placed as today's <c>TargetColors.DrawSquawkMismatchLine</c> places its head and tail.
    /// </summary>
    private void DrawDimTail(SKCanvas canvas, FieldRect field, SKFont font, SKColor color, SKColor tailColor)
    {
        string text = field.Text;
        float x = field.Rect.Left;
        float baseline = field.Rect.Bottom;
        int space = text.IndexOf(' ');
        string head = space < 0 ? text : text[..space];
        _textPaint.Color = color;
        canvas.DrawText(head, x, baseline, SKTextAlign.Left, font, _textPaint);
        if (space < 0)
        {
            return;
        }

        float headWidth = font.MeasureText(head);
        _textPaint.Color = tailColor;
        canvas.DrawText(text[space..], x + headWidth, baseline, SKTextAlign.Left, font, _textPaint);
    }

    /// <summary>A line across <paramref name="field"/> at the font's strikeout position, as today's radar strikes ModeC.</summary>
    private void DrawStrikethrough(SKCanvas canvas, FieldRect field, SKFont font, SKColor color)
    {
        SKFontMetrics metrics = font.Metrics;
        // StrikeoutPosition is negative (above the baseline); a font that reports none falls back to a third of its size.
        float strikeOffset = metrics.StrikeoutPosition.GetValueOrDefault();
        if (strikeOffset == 0)
        {
            strikeOffset = -font.Size / 3f;
        }
        float strikeY = field.Rect.Bottom + strikeOffset;
        _strikethroughPaint.Color = color;
        _strikethroughPaint.StrokeWidth = MathF.Max(1f, metrics.StrikeoutThickness.GetValueOrDefault());
        canvas.DrawLine(field.Rect.Left, strikeY, field.Rect.Right, strikeY, _strikethroughPaint);
    }

    /// <summary>Scales <paramref name="color"/>'s RGB toward black by <paramref name="factor"/>, keeping its alpha.</summary>
    private static SKColor Dim(SKColor color, float factor) =>
        new((byte)(color.Red * factor), (byte)(color.Green * factor), (byte)(color.Blue * factor), color.Alpha);

    /// <summary>
    /// The colour of <paramref name="track"/>'s leader, dwell box and every <see cref="ScopeColorToken.Default"/> field, at the
    /// stale alpha when it coasts, unless it is dwelled: a dwelled block skips the stale alpha.
    /// </summary>
    internal static SKColor BlockColorOf(ScopeTrack track, ScopePalette palette)
    {
        SKColor color = ResolveBlockColor(track.BlockColor, palette);
        return track.IsDwelled ? color : Stale(track, color);
    }

    /// <summary>
    /// The colour of <paramref name="track"/>'s symbol: the selected colour when selected, else its tint, else the symbol blue,
    /// at the stale alpha when it coasts. The highlight and student colours leave the symbol alone, as on today's radar.
    /// </summary>
    internal static SKColor SymbolColorOf(ScopeTrack track, ScopePalette palette)
    {
        SKColor? tint = track.Tint switch
        {
            ScopeTintRole.AssignedToMe => palette.AssignedToMe,
            ScopeTintRole.AssignedToOther => palette.AssignedToOther,
            _ => null,
        };
        return Stale(track, track.IsSelected ? palette.SelectedSymbol : (tint ?? SymbolColor));
    }

    /// <summary>The colour <paramref name="token"/> draws in; <see cref="ScopeColorToken.Default"/> takes <paramref name="block"/>.</summary>
    internal static SKColor ColorFor(ScopeColorToken token, SKColor block) =>
        token switch
        {
            ScopeColorToken.TypeMismatch => TypeMismatchColor,
            ScopeColorToken.MvaBelow => MvaBelowColor,
            ScopeColorToken.MvaAt => MvaAtColor,
            ScopeColorToken.Note => NoteColor,
            ScopeColorToken.NoLandingClearance => NoLandingClearanceColor,
            ScopeColorToken.ConflictAlert => ConflictAlertColor,
            ScopeColorToken.AtpaWarning => AtpaWarningColor,
            ScopeColorToken.AtpaAlert => AtpaAlertColor,
            _ => block,
        };

    private static SKColor ResolveBlockColor(ScopeBlockColor role, ScopePalette palette) =>
        role switch
        {
            ScopeBlockColor.StudentOwned => StarsOwnedColor,
            ScopeBlockColor.StudentUnowned => StarsUnownedColor,
            ScopeBlockColor.StudentPointout => StarsPointoutColor,
            ScopeBlockColor.StudentHighlighted => StarsHighlightColor,
            ScopeBlockColor.Highlighted => HighlightColor,
            ScopeBlockColor.AssignedToMe => palette.AssignedToMe ?? BlockColor,
            ScopeBlockColor.AssignedToOther => palette.AssignedToOther ?? BlockColor,
            _ => BlockColor,
        };

    private static SKColor Stale(ScopeTrack track, SKColor color) => track.IsStale ? color.WithAlpha(StaleAlpha) : color;

    /// <summary>
    /// Where <paramref name="track"/>'s leader meets its block: the nearest point of the text bounds, so column 0 never moves it
    /// (CRC attaches the leader to the fields box).
    /// </summary>
    internal static SKPoint LeaderEnd(ScopeTrack track) => ClampToBlockEdge(track.Symbol, track.Block.TextBounds);

    /// <summary>The rect a selected track's border is drawn on: its text block, so column 0 never moves it.</summary>
    internal static SKRect SelectionBorder(ScopeTrack track) => track.Block.TextBounds;

    /// <summary>The point on <paramref name="rect"/>'s edge nearest <paramref name="point"/>, or the point itself when inside.</summary>
    private static SKPoint ClampToBlockEdge(SKPoint point, SKRect rect) =>
        rect.Contains(point.X, point.Y) ? point : new SKPoint(Math.Clamp(point.X, rect.Left, rect.Right), Math.Clamp(point.Y, rect.Top, rect.Bottom));
}
