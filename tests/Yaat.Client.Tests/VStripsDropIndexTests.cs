using Xunit;
using Yaat.Client.Views.VStrips;

namespace Yaat.Client.Tests;

/// <summary>
/// Covers the visual-to-model index mapping used by the strip drag-drop
/// handler (task #29 + user re-test). Strips render bottom-up via
/// DockPanel.Dock=Bottom so strip[0] occupies the visual bottom band.
/// Insertion semantics:
///   - Drop anywhere in strip[i]'s band (top..bottom inclusive) → insert at
///     that strip's slot (model index i); strip i and everything above it
///     slide up one strip.
///   - Drop above the whole stack (empty space above the topmost strip) →
///     append at the tail (model index = count).
///   - Drop below strip[0] → insert at model index 0.
/// </summary>
public class VStripsDropIndexTests
{
    [Fact]
    public void Empty_Bands_ReturnsZero()
    {
        Assert.Equal(0, VStripsView.ComputeDropIndexFromBands(posY: 0, bands: []));
        Assert.Equal(0, VStripsView.ComputeDropIndexFromBands(posY: 50, bands: []));
    }

    [Fact]
    public void VisualTop_MapsToModelAppend()
    {
        // 3 strips of 82px each stacked bottom-up inside a 600px-tall host.
        // strip[0] = 518..600, strip[1] = 436..518, strip[2] = 354..436.
        // Anywhere above 354 (the topmost strip's top) is "empty space above
        // the stack" and should append.
        var bands = new (double Top, double Bottom)[] { (518, 600), (436, 518), (354, 436) };
        Assert.Equal(3, VStripsView.ComputeDropIndexFromBands(posY: 0, bands));
        Assert.Equal(3, VStripsView.ComputeDropIndexFromBands(posY: 200, bands));
    }

    [Fact]
    public void BandTopEdge_InsertsAtThatBand()
    {
        // A band's exact top edge belongs to that band: the whole band
        // targets its own slot, with no midpoint split.
        var bands = new (double Top, double Bottom)[] { (518, 600), (436, 518), (354, 436) };
        Assert.Equal(2, VStripsView.ComputeDropIndexFromBands(posY: 354, bands));
        Assert.Equal(1, VStripsView.ComputeDropIndexFromBands(posY: 436, bands));
    }

    [Fact]
    public void OnePixelAboveStack_Appends()
    {
        // One pixel above the topmost band's top is the append zone.
        var bands = new (double Top, double Bottom)[] { (518, 600), (436, 518), (354, 436) };
        Assert.Equal(3, VStripsView.ComputeDropIndexFromBands(posY: 353, bands));
    }

    [Fact]
    public void UpperHalfOfStrip_InsertsAtItsSlot()
    {
        // strip[2]'s band is Y 354..436; its upper half is 354..395. Drop
        // anywhere in the band → insert at that strip's slot (model 2).
        var bands = new (double Top, double Bottom)[] { (518, 600), (436, 518), (354, 436) };
        Assert.Equal(2, VStripsView.ComputeDropIndexFromBands(posY: 370, bands));

        // strip[1]'s upper half is Y 436..477 → model index 1.
        Assert.Equal(1, VStripsView.ComputeDropIndexFromBands(posY: 450, bands));
    }

    [Fact]
    public void BottomHalfOfStrip_InsertsBelowIt()
    {
        var bands = new (double Top, double Bottom)[] { (518, 600), (436, 518), (354, 436) };

        // strip[0]'s bottom half: Y 559..600 → model index 0.
        Assert.Equal(0, VStripsView.ComputeDropIndexFromBands(posY: 580, bands));

        // strip[1]'s bottom half: Y 477..518 → model index 1.
        Assert.Equal(1, VStripsView.ComputeDropIndexFromBands(posY: 500, bands));
    }

    [Fact]
    public void BelowBottomStrip_InsertsAtZero()
    {
        // Drop past strip[0]'s bottom edge (e.g., if the host extends below the
        // strip stack). Should insert at model 0 (below the bottom strip).
        var bands = new (double Top, double Bottom)[] { (518, 600), (436, 518), (354, 436) };
        Assert.Equal(0, VStripsView.ComputeDropIndexFromBands(posY: 650, bands));
    }

    [Fact]
    public void SingleStrip_AnyHeightInsertsAtZero()
    {
        var bands = new (double Top, double Bottom)[] { (534, 600) };
        Assert.Equal(0, VStripsView.ComputeDropIndexFromBands(posY: 540, bands));
        Assert.Equal(0, VStripsView.ComputeDropIndexFromBands(posY: 590, bands));
    }

    // ── Hysteresis overload ─────────────────────────────────────
    //
    // The hysteresis-aware overload keeps the active preview index until the
    // pointer clears the boundary between adjacent indices (the edge between
    // the two bands, the lower band's top) by more than hysteresisPx, so the
    // gap doesn't flicker under hand tremor at band boundaries.

    [Fact]
    public void Hysteresis_WithinBandOfBoundary_KeepsCurrentIndex()
    {
        // The boundary between index 0 and 1 is band 0's top edge (518).
        // Moving up from the bottom slot, the preview stays at 0 until the
        // pointer is more than 6 px above that edge.
        var bands = new (double Top, double Bottom)[] { (518, 600), (436, 518), (354, 436) };
        Assert.Equal(0, VStripsView.ComputeDropIndexFromBands(posY: 515, bands, currentIndex: 0, hysteresisPx: 6));
        Assert.Equal(1, VStripsView.ComputeDropIndexFromBands(posY: 511, bands, currentIndex: 0, hysteresisPx: 6));
    }

    [Fact]
    public void Hysteresis_BeyondBoundary_Switches()
    {
        // Moving down from index 1, the preview stays at 1 until the pointer
        // is more than 6 px below band 0's top edge (518).
        var bands = new (double Top, double Bottom)[] { (518, 600), (436, 518), (354, 436) };
        Assert.Equal(1, VStripsView.ComputeDropIndexFromBands(posY: 521, bands, currentIndex: 1, hysteresisPx: 6));
        Assert.Equal(0, VStripsView.ComputeDropIndexFromBands(posY: 525, bands, currentIndex: 1, hysteresisPx: 6));
    }

    [Fact]
    public void Hysteresis_NonAdjacentJump_SwitchesImmediately()
    {
        // A fast pointer move across multiple bands must not be damped —
        // hysteresis only applies to adjacent flips.
        var bands = new (double Top, double Bottom)[] { (518, 600), (436, 518), (354, 436) };
        Assert.Equal(0, VStripsView.ComputeDropIndexFromBands(posY: 590, bands, currentIndex: 3, hysteresisPx: 6));
        Assert.Equal(3, VStripsView.ComputeDropIndexFromBands(posY: 200, bands, currentIndex: 0, hysteresisPx: 6));
    }

    [Fact]
    public void Hysteresis_NoCurrentIndex_PassesThrough()
    {
        var bands = new (double Top, double Bottom)[] { (518, 600), (436, 518), (354, 436) };
        Assert.Equal(1, VStripsView.ComputeDropIndexFromBands(posY: 474, bands, currentIndex: -1, hysteresisPx: 6));
    }

    [Fact]
    public void Hysteresis_AppendBoundary_UsesTopmostBandTop()
    {
        // Between index 2 and append (3) the boundary is the topmost band's
        // top edge (354). With current=3 (append), the preview stays append
        // until the pointer is more than 6 px inside band 2.
        var bands = new (double Top, double Bottom)[] { (518, 600), (436, 518), (354, 436) };
        Assert.Equal(3, VStripsView.ComputeDropIndexFromBands(posY: 358, bands, currentIndex: 3, hysteresisPx: 6));
        Assert.Equal(2, VStripsView.ComputeDropIndexFromBands(posY: 362, bands, currentIndex: 3, hysteresisPx: 6));
    }
}
