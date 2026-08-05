using Xunit;
using Yaat.Client.Views.VStrips;

namespace Yaat.Client.Tests;

/// <summary>
/// Covers the visual-to-model index mapping used by the strip drag-drop
/// handler (task #29 + user re-test). Strips render bottom-up via
/// DockPanel.Dock=Bottom so strip[0] occupies the visual bottom band.
/// Insertion semantics:
///   - Drop in strip[i]'s top half → insert above it (model index i+1).
///   - Drop in strip[i]'s bottom half → insert below it (model index i).
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
    public void TopHalfOfStrip_InsertsAboveIt()
    {
        // strip[2]'s top half is Y 354..395. Drop in that range → insert above
        // strip[2] (model index 3 = append).
        var bands = new (double Top, double Bottom)[] { (518, 600), (436, 518), (354, 436) };
        Assert.Equal(3, VStripsView.ComputeDropIndexFromBands(posY: 370, bands));

        // strip[1]'s top half is Y 436..477 → model index 2.
        Assert.Equal(2, VStripsView.ComputeDropIndexFromBands(posY: 450, bands));
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
    public void SingleStrip_TopHalfAppends_BottomHalfInsertsAtZero()
    {
        var bands = new (double Top, double Bottom)[] { (534, 600) };
        Assert.Equal(1, VStripsView.ComputeDropIndexFromBands(posY: 540, bands));
        Assert.Equal(0, VStripsView.ComputeDropIndexFromBands(posY: 590, bands));
    }

    // ── Hysteresis overload ─────────────────────────────────────
    //
    // The hysteresis-aware overload keeps the active preview index until the
    // pointer clears the boundary between adjacent indices (the midpoint of
    // the lower band) by more than hysteresisPx, so the gap doesn't flicker
    // under hand tremor at band boundaries.

    [Fact]
    public void Hysteresis_WithinBandOfBoundary_KeepsCurrentIndex()
    {
        // strip[1]'s band is 436..518, midpoint 477. Raw index flips between
        // 1 and 2 exactly at 477; with current=1 and hysteresis 6, positions
        // just above the midpoint (477 > posY > 471) must stay at 1.
        var bands = new (double Top, double Bottom)[] { (518, 600), (436, 518), (354, 436) };
        Assert.Equal(2, VStripsView.ComputeDropIndexFromBands(posY: 474, bands));
        Assert.Equal(1, VStripsView.ComputeDropIndexFromBands(posY: 474, bands, currentIndex: 1, hysteresisPx: 6));
    }

    [Fact]
    public void Hysteresis_BeyondBand_Switches()
    {
        var bands = new (double Top, double Bottom)[] { (518, 600), (436, 518), (354, 436) };
        // Clearly above midpoint-minus-hysteresis (471) → flips up to 2.
        Assert.Equal(2, VStripsView.ComputeDropIndexFromBands(posY: 460, bands, currentIndex: 1, hysteresisPx: 6));
        // And back down: with current=2, positions just below the midpoint
        // stay at 2 until clearing 483.
        Assert.Equal(2, VStripsView.ComputeDropIndexFromBands(posY: 480, bands, currentIndex: 2, hysteresisPx: 6));
        Assert.Equal(1, VStripsView.ComputeDropIndexFromBands(posY: 490, bands, currentIndex: 2, hysteresisPx: 6));
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
        Assert.Equal(2, VStripsView.ComputeDropIndexFromBands(posY: 474, bands, currentIndex: -1, hysteresisPx: 6));
    }

    [Fact]
    public void Hysteresis_AppendBoundary_UsesTopmostBandMidpoint()
    {
        // Between index 2 and append (3), the boundary is strip[2]'s midpoint
        // (395). With current=3 (append), staying just below the midpoint
        // keeps the append preview.
        var bands = new (double Top, double Bottom)[] { (518, 600), (436, 518), (354, 436) };
        Assert.Equal(3, VStripsView.ComputeDropIndexFromBands(posY: 398, bands, currentIndex: 3, hysteresisPx: 6));
        Assert.Equal(2, VStripsView.ComputeDropIndexFromBands(posY: 410, bands, currentIndex: 3, hysteresisPx: 6));
    }
}
