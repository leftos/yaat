using SkiaSharp;

namespace Yaat.Client.ViewModels;

/// <summary>
/// The Scope's per-track display state as its menus and automation reach it: hiding and showing a block and taking back a
/// dragged position. A Shift+click and a drag write the same state from the canvas. None of it is sent to the server, saved in a
/// pref set or kept across a scenario load.
/// </summary>
public sealed partial class ScopeViewModel
{
    /// <summary>Whether <paramref name="callsign"/>'s block is hidden by the instructor on this scope.</summary>
    public bool IsDataBlockHidden(string callsign) => DataBlockState.HiddenCallsigns.Contains(callsign);

    /// <summary>Whether any block on this scope is hidden.</summary>
    public bool AnyDataBlockHidden => DataBlockState.HiddenCallsigns.Count > 0;

    /// <summary>Hides <paramref name="callsign"/>'s block and its leader; a selected aircraft still shows its block.</summary>
    public void HideDataBlock(string callsign) => DataBlockState.Hide(callsign);

    /// <summary>Shows <paramref name="callsign"/>'s block again.</summary>
    public void ShowDataBlock(string callsign) => DataBlockState.Show(callsign);

    /// <summary>Hides <paramref name="callsign"/>'s block when it is drawn, shows it when it is hidden.</summary>
    public void ToggleDataBlockHidden(string callsign) => DataBlockState.ToggleHidden(callsign);

    /// <summary>Shows every hidden block on this scope.</summary>
    public void ShowAllDataBlocks() => DataBlockState.ShowAll();

    /// <summary>Whether <paramref name="callsign"/>'s block sits at an offset the instructor set by dragging it.</summary>
    public bool HasManualDataBlockOffset(string callsign) => DataBlockState.ManualOffsets.ContainsKey(callsign);

    /// <summary>Moves <paramref name="callsign"/>'s block to <paramref name="offset"/> from its position symbol, as a drag does.</summary>
    public void SetDataBlockOffset(string callsign, SKPoint offset) => DataBlockState.SetManualOffset(callsign, offset);

    /// <summary>Returns <paramref name="callsign"/>'s block to the placement its position and the student's leader direction give it.</summary>
    /// <returns>True when the block had a manual offset to remove.</returns>
    public bool ResetDataBlockPosition(string callsign) => DataBlockState.RemoveManualOffset(callsign);
}
