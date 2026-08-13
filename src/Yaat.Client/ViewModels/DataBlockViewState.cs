using SkiaSharp;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Session-persistent per-callsign datablock UI state, owned by the map view-model so it survives
/// tab switches and pop-out/dock-back — both of which detach or rebuild the rendering canvas.
/// The canvas mutates it in place and defensively copies the collections into each immutable
/// render snapshot; the view-model clears it on layout/scenario lifecycle events.
/// </summary>
public class DataBlockViewState
{
    private int _nextZOrder = 1;

    /// <summary>Manual drag offsets (callsign → screen-space offset from the position symbol).</summary>
    public Dictionary<string, SKPoint> ManualOffsets { get; } = new();

    /// <summary>Callsigns whose datablocks are highlighted (middle-click toggle).</summary>
    public HashSet<string> HighlightedCallsigns { get; } = new();

    /// <summary>Datablock draw order (callsign → z-index); higher draws on top.</summary>
    public Dictionary<string, int> DataBlockZOrder { get; } = new();

    /// <summary>Surfaces the callsign's datablock to the top of the z-order.</summary>
    public void SurfaceDataBlock(string callsign) => DataBlockZOrder[callsign] = _nextZOrder++;

    /// <summary>Toggles the highlight state of the callsign's datablock.</summary>
    public void ToggleHighlight(string callsign)
    {
        if (!HighlightedCallsigns.Remove(callsign))
        {
            HighlightedCallsigns.Add(callsign);
        }
    }

    public virtual void Clear()
    {
        ManualOffsets.Clear();
        HighlightedCallsigns.Clear();
        DataBlockZOrder.Clear();
        _nextZOrder = 1;
    }
}

/// <summary>Ground-view datablock state: adds the hide/show choices and their inversion mode.</summary>
public sealed class GroundDataBlockViewState : DataBlockViewState
{
    /// <summary>Callsigns explicitly hidden while <see cref="StartWithAllHidden"/> is off.</summary>
    public HashSet<string> HiddenDataBlockCallsigns { get; } = new();

    /// <summary>Callsigns explicitly shown while <see cref="StartWithAllHidden"/> is on.</summary>
    public HashSet<string> ShownDataBlockCallsigns { get; } = new();

    /// <summary>When true all datablocks start hidden and <see cref="ShownDataBlockCallsigns"/> opts in.</summary>
    public bool StartWithAllHidden { get; private set; }

    public bool IsDataBlockHidden(string callsign)
    {
        return StartWithAllHidden ? !ShownDataBlockCallsigns.Contains(callsign) : HiddenDataBlockCallsigns.Contains(callsign);
    }

    public void ToggleHiddenDataBlock(string callsign)
    {
        var set = StartWithAllHidden ? ShownDataBlockCallsigns : HiddenDataBlockCallsigns;
        if (!set.Remove(callsign))
        {
            set.Add(callsign);
        }
    }

    /// <summary>
    /// Sets the hide-by-default mode. Per-callsign choices reset only when the mode actually flips,
    /// so a second view binding to this shared state (opening a pop-out) can re-apply the preference
    /// without wiping choices made in the first view. Returns true when anything changed.
    /// </summary>
    public bool SetStartWithAllHidden(bool hidden)
    {
        if (StartWithAllHidden == hidden)
        {
            return false;
        }

        StartWithAllHidden = hidden;
        HiddenDataBlockCallsigns.Clear();
        ShownDataBlockCallsigns.Clear();
        return true;
    }

    public override void Clear()
    {
        base.Clear();
        // StartWithAllHidden mirrors a user preference, not per-callsign state — it survives clears.
        HiddenDataBlockCallsigns.Clear();
        ShownDataBlockCallsigns.Clear();
    }
}

/// <summary>Radar-view datablock state: adds the full/mini datablock choices.</summary>
public sealed class RadarDataBlockViewState : DataBlockViewState
{
    /// <summary>Callsigns showing the minified (single-line) datablock.</summary>
    public HashSet<string> MinifiedCallsigns { get; } = new();

    public void ToggleMinified(string callsign)
    {
        if (!MinifiedCallsigns.Remove(callsign))
        {
            MinifiedCallsigns.Add(callsign);
        }
    }

    public override void Clear()
    {
        base.Clear();
        MinifiedCallsigns.Clear();
    }
}
