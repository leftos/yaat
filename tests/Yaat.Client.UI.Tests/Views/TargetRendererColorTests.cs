using SkiaSharp;
using Xunit;
using Yaat.Client.Views.Radar;

namespace Yaat.Client.UI.Tests.Views;

// Regression for the reported bug: selecting an aircraft on the radar flattened its datablock text
// (and leader) to the selection color, discarding the meaningful unselected color (green ground
// tracks, RPO tints, student-scope colors). Selection should leave the datablock color untouched —
// the white rectangular border is the cue — while the position symbol still brightens.
public class TargetRendererColorTests
{
    private static readonly SKColor Selected = SKColors.Magenta; // sentinel: distinct from any base color
    private static readonly SKColor Tint = SKColors.Orange;

    private static TargetRenderer.TargetColorInputs Inputs(bool isSelected, bool isHighlighted, bool isOnGround, SKColor? tint, SKColor? student) =>
        new(isSelected, isHighlighted, isOnGround, tint, student, Selected);

    [Fact]
    public void Selection_DoesNotChange_GroundDatablockColor()
    {
        var unselected = TargetRenderer.ResolveTargetColors(Inputs(false, false, true, null, null));
        var selected = TargetRenderer.ResolveTargetColors(Inputs(true, false, true, null, null));

        Assert.Equal(unselected.DataBlock, selected.DataBlock); // text keeps its unselected color
        Assert.NotEqual(Selected, selected.DataBlock); // not flattened to the selection color
    }

    [Fact]
    public void Selection_DoesNotChange_TintedDatablockColor()
    {
        var selected = TargetRenderer.ResolveTargetColors(Inputs(true, false, false, Tint, null));

        Assert.Equal(Tint, selected.DataBlock);
    }

    [Fact]
    public void Selection_Brightens_PositionSymbol()
    {
        var unselected = TargetRenderer.ResolveTargetColors(Inputs(false, false, false, null, null));
        var selected = TargetRenderer.ResolveTargetColors(Inputs(true, false, false, null, null));

        Assert.Equal(Selected, selected.Symbol); // symbol still brightens to the selection color
        Assert.NotEqual(Selected, unselected.Symbol);
    }

    [Fact]
    public void Highlight_OverridesDatablockColor_RegardlessOfSelection()
    {
        var highlighted = TargetRenderer.ResolveTargetColors(Inputs(false, true, false, null, null));
        var highlightedSelected = TargetRenderer.ResolveTargetColors(Inputs(true, true, false, null, null));

        Assert.Equal(SKColors.Cyan, highlighted.DataBlock);
        Assert.Equal(SKColors.Cyan, highlightedSelected.DataBlock);
    }
}
