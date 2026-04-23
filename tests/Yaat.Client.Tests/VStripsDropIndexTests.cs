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
}
