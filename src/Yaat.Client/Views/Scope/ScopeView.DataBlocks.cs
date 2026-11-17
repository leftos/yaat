using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope view's datablock wiring: a block moved or hidden from the canvas, a menu or the automation repaints the canvas
/// showing it, whichever surface wrote the state, and the Rings tool's armed state suspends the datablock gestures so its own
/// press places a ring and nothing else.
/// </summary>
public partial class ScopeView
{
    // The Rings tool takes left presses: while it is armed a press on a block places a ring instead of selecting, moving or
    // hiding the block.
    partial void WireDataBlocks() => Canvas.BlockGesturesSuspended = () => DataContext is ScopeViewModel { IsRingToolArmed: true };

    partial void AttachDataBlocks(ScopeViewModel vm)
    {
        vm.DataBlockState.ManualOffsetsChanged += OnDataBlockStateChanged;
        vm.DataBlockState.HiddenChanged += OnDataBlockStateChanged;
    }

    partial void DetachDataBlocks(ScopeViewModel vm)
    {
        vm.DataBlockState.ManualOffsetsChanged -= OnDataBlockStateChanged;
        vm.DataBlockState.HiddenChanged -= OnDataBlockStateChanged;
    }

    // Either the offsets or the hidden set changed: the kept frame and its hit-test read the state from before, so it is
    // dropped and the next frame is built from the new one.
    private void OnDataBlockStateChanged() => Canvas.InvalidateDataBlockFrame();
}
