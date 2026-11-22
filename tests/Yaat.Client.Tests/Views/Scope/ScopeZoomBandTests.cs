using Xunit;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Profiles;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Zoom bands: the band a range selects (the first whose edge is null or at least the range, inclusive) and the values in
/// effect once the pins are laid over it.
/// </summary>
public class ScopeZoomBandTests
{
    private static readonly IReadOnlyList<ScopeZoomBand> MixedBands = ScopeProfileStore.Shipped(ScopeProfileKind.Mixed).Bands;

    private static readonly ScopeZoomBand Approach = new(30, ScopeProfile.OthersFull, 0, 5, ScopeProfile.RingsJRing, ScopeTemplates.StarsId);

    [Fact]
    public void Resolve_At29Nm_IsTheApproachBand() => Assert.Equal(30, ScopeZoomBands.Resolve(MixedBands, 29).MaxNm);

    [Fact]
    public void Resolve_At30Nm_IsTheApproachBand_TheEdgeIsInclusive()
    {
        ScopeZoomBand band = ScopeZoomBands.Resolve(MixedBands, 30);

        Assert.Equal(30, band.MaxNm);
        Assert.Equal(ScopeProfile.OthersFull, band.OthersLevel);
    }

    [Fact]
    public void Resolve_At31Nm_IsTheTransitionBand()
    {
        ScopeZoomBand band = ScopeZoomBands.Resolve(MixedBands, 31);

        Assert.Equal(120, band.MaxNm);
        Assert.Equal(ScopeProfile.OthersPartial, band.OthersLevel);
    }

    [Fact]
    public void Resolve_At500Nm_IsTheLastBand()
    {
        ScopeZoomBand band = ScopeZoomBands.Resolve(MixedBands, 500);

        Assert.Null(band.MaxNm);
        Assert.Equal(4, band.VectorMin);
        Assert.Equal(3, band.History);
        Assert.Equal(ScopeProfile.RingsDri, band.Rings);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(40)]
    [InlineData(256)]
    public void Resolve_SingleBandProfile_IsThatBandAtEveryRange(double rangeNm)
    {
        IReadOnlyList<ScopeZoomBand> eram = ScopeProfileStore.Shipped(ScopeProfileKind.Eram).Bands;

        Assert.Same(Assert.Single(eram), ScopeZoomBands.Resolve(eram, rangeNm));
    }

    [Fact]
    public void Effective_NoPins_IsTheBand()
    {
        Assert.Equal(Approach, ScopeZoomBands.Effective(Approach, ScopeBandPins.None));
        Assert.False(ScopeBandPins.None.Any);
    }

    [Fact]
    public void Effective_OthersLevelPin_OverridesOnlyTheLevel()
    {
        ScopeBandPins pins = ScopeBandPins.None with { OthersLevel = ScopeProfile.OthersPartial };

        Assert.Equal(Approach with { OthersLevel = ScopeProfile.OthersPartial }, ScopeZoomBands.Effective(Approach, pins));
        Assert.True(pins.Any);
    }

    [Fact]
    public void Effective_VectorPin_OverridesOnlyTheVector()
    {
        ScopeBandPins pins = ScopeBandPins.None with { VectorMin = 2 };

        Assert.Equal(Approach with { VectorMin = 2 }, ScopeZoomBands.Effective(Approach, pins));
        Assert.True(pins.Any);
    }

    [Fact]
    public void Effective_HistoryPin_OverridesOnlyTheHistory()
    {
        ScopeBandPins pins = ScopeBandPins.None with { History = 0 };

        Assert.Equal(Approach with { History = 0 }, ScopeZoomBands.Effective(Approach, pins));
        Assert.True(pins.Any);
    }

    [Fact]
    public void Effective_RingsPin_OverridesOnlyTheRings()
    {
        ScopeBandPins pins = ScopeBandPins.None with { Rings = ScopeProfile.RingsDri };

        Assert.Equal(Approach with { Rings = ScopeProfile.RingsDri }, ScopeZoomBands.Effective(Approach, pins));
        Assert.True(pins.Any);
    }
}
