using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope's datablock deconfliction (DCNF), over the engine the Radar and Ground share
/// (<see cref="DatablockDeconfliction"/>): a frame is laid out once as without it, its blocks go to the engine at the offsets
/// they took there (a manual offset, else the student's leader direction, else the default), and the frame is laid out
/// again at the offsets the engine gives back, so the rings, the CRR readout and the hit-test follow the block. A block the
/// instructor dragged is pinned, so it never moves and the others avoid it; a hidden block takes no part. The engine's last
/// result seeds its next run, so a layout stays put frame to frame; a build that does not step Free-form (a hit-test's)
/// reuses that result.
/// </summary>
public sealed class ScopeDeconfliction
{
    private const string OffName = "off";
    private const string SnapName = "snap";
    private const string FreeName = "free";

    // The last build's offsets, which seed the next one, and the engine's output, copied into them after each build.
    private readonly Dictionary<string, SKPoint> _resolved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SKPoint> _scratch = new(StringComparer.Ordinal);

    /// <summary>The offsets the last stepping build gave, by callsign: what every build until the next one places blocks at.</summary>
    public IReadOnlyDictionary<string, SKPoint> ResolvedOffsets => _resolved;

    /// <summary>The mode's name, as <c>.scope dcnf</c> takes it: <c>off</c>, <c>snap</c> or <c>free</c>.</summary>
    public static string NameOf(DatablockDeconflictMode mode) =>
        mode switch
        {
            DatablockDeconflictMode.CompassSnap => SnapName,
            DatablockDeconflictMode.FreeForm => FreeName,
            _ => OffName,
        };

    /// <summary>The mode <paramref name="name"/> names (<see cref="NameOf"/>), case-insensitive, or null for none.</summary>
    public static DatablockDeconflictMode? ParseMode(string name) =>
        name.ToLowerInvariant() switch
        {
            OffName => DatablockDeconflictMode.Off,
            SnapName => DatablockDeconflictMode.CompassSnap,
            FreeName => DatablockDeconflictMode.FreeForm,
            _ => null,
        };

    /// <summary>The mode after <paramref name="mode"/> in DCNF's cycle: Off, Snap, Free-form, then Off again.</summary>
    public static DatablockDeconflictMode Next(DatablockDeconflictMode mode) =>
        mode switch
        {
            DatablockDeconflictMode.Off => DatablockDeconflictMode.CompassSnap,
            DatablockDeconflictMode.CompassSnap => DatablockDeconflictMode.FreeForm,
            _ => DatablockDeconflictMode.Off,
        };

    /// <summary>
    /// Lays the frame of <paramref name="input"/> out with its blocks deconflicted by <paramref name="mode"/>; with the mode
    /// Off, or a viewport under a pixel, the frame as without it, and the next build starts afresh.
    /// </summary>
    /// <param name="mode">The deconfliction mode.</param>
    /// <param name="input">The frame's inputs.</param>
    /// <param name="step">
    /// Whether this build runs the engine: the render's build does, a hit-test's build between two renders does not and places
    /// the blocks at <see cref="ResolvedOffsets"/>, so it finds each block where the last render drew it. Only Free-form needs
    /// it, as its blocks slide one damped step per run; Snap is no iteration, its run on the same traffic places the same, so
    /// it runs on every build.
    /// </param>
    public ScopeFrame Build(DatablockDeconflictMode mode, ScopeFrameInput input, bool step)
    {
        var frame = ScopeFrame.Build(input);
        if ((mode == DatablockDeconflictMode.Off) || (input.Viewport.PixelWidth < 1) || (input.Viewport.PixelHeight < 1))
        {
            _resolved.Clear();
            return frame;
        }

        if (step || (mode != DatablockDeconflictMode.FreeForm))
        {
            Resolve(mode, frame, input);
        }

        if (_resolved.Count == 0)
        {
            return frame;
        }

        return ScopeFrame.Build(input with { DeconflictOffsets = new Dictionary<string, SKPoint>(_resolved, StringComparer.Ordinal) });
    }

    /// <summary>Runs the engine over <paramref name="frame"/>'s blocks, seeded from the last run, and keeps its offsets.</summary>
    private void Resolve(DatablockDeconflictMode mode, ScopeFrame frame, ScopeFrameInput input)
    {
        var bounds = new SKRect(0, 0, input.Viewport.PixelWidth, input.Viewport.PixelHeight);
        DatablockDeconfliction.Resolve(mode, BuildItems(frame, input), DatablockDeconfliction.Options.Default(bounds), _resolved, _scratch);
        _resolved.Clear();
        foreach (KeyValuePair<string, SKPoint> offset in _scratch)
        {
            _resolved[offset.Key] = offset.Value;
        }
    }

    /// <summary>
    /// The engine's items for <paramref name="frame"/>'s blocks, laid out from <paramref name="input"/> as without
    /// deconfliction: each at its symbol and the offset it took, pinned when the instructor dragged it, placed first when
    /// its track is selected; a hidden block is left out.
    /// </summary>
    public static List<DatablockDeconfliction.Item> BuildItems(ScopeFrame frame, ScopeFrameInput input)
    {
        var items = new List<DatablockDeconfliction.Item>(frame.Tracks.Count);
        foreach (ScopeTrack track in frame.Tracks)
        {
            if (!ScopeFrame.IsBlockHidden(input, track.Aircraft, track.IsSelected))
            {
                items.Add(ItemOf(track, input.ManualOffsets.ContainsKey(track.Aircraft.Callsign)));
            }
        }

        return items;
    }

    private static DatablockDeconfliction.Item ItemOf(ScopeTrack track, bool pinned)
    {
        SKPoint origin = track.Symbol + track.BlockOffset;
        SKRect bounds = track.Block.Bounds;
        return new DatablockDeconfliction.Item
        {
            Callsign = track.Aircraft.Callsign,
            Anchor = track.Symbol,
            RectAtOrigin = new SKRect(bounds.Left - origin.X, bounds.Top - origin.Y, bounds.Right - origin.X, bounds.Bottom - origin.Y),
            PreferredOffset = track.BlockOffset,
            IsPinned = pinned,
            IsPriority = track.IsSelected,
        };
    }
}
