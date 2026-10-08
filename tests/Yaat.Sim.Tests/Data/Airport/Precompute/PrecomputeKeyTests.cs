using Xunit;
using Yaat.Sim.Data.Airport.Precompute;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// The staleness of a <see cref="PrecomputeKey"/>: each of the four layout inputs (GeoJSON hash, navdata serial,
/// layout format version, layout source hash) breaks both matches when it moves, and a push-target source change or an
/// airport sidecar change breaks only the push half. One test per clause, so dropping any clause of
/// <see cref="PrecomputeKey.LayoutMatches"/> or <see cref="PrecomputeKey.PushTargetsMatch"/> fails a test.
/// </summary>
public class PrecomputeKeyTests
{
    [Fact]
    public void PushOnlyChange_KeepsLayoutMatch()
    {
        PrecomputeKey stored = Stored();
        PrecomputeKey current = Stored() with { PushTargetSourceHash = "other" };

        Assert.True(current.LayoutMatches(stored));
        Assert.False(current.PushTargetsMatch(stored));
    }

    [Fact]
    public void SidecarChange_BreaksPushHalfOnly()
    {
        PrecomputeKey stored = Stored();
        PrecomputeKey current = Stored() with { PushSidecarHash = "other" };

        Assert.True(current.LayoutMatches(stored));
        Assert.False(current.PushTargetsMatch(stored));
        Assert.True(Stored().PushTargetsMatch(stored));
    }

    [Fact]
    public void GeoJsonChange_BreaksBothMatches()
    {
        PrecomputeKey stored = Stored();
        PrecomputeKey current = Stored() with { GeoJsonMd5 = "other" };

        Assert.False(current.LayoutMatches(stored));
        Assert.False(current.PushTargetsMatch(stored));
    }

    [Fact]
    public void NavDataSerialChange_BreaksBothMatches()
    {
        PrecomputeKey stored = Stored();
        PrecomputeKey current = Stored() with { NavDataSerial = 8 };

        Assert.False(current.LayoutMatches(stored));
        Assert.False(current.PushTargetsMatch(stored));
    }

    [Fact]
    public void LayoutFormatVersionChange_BreaksBothMatches()
    {
        PrecomputeKey stored = Stored();
        PrecomputeKey current = Stored() with { LayoutFormatVersion = 2 };

        Assert.False(current.LayoutMatches(stored));
        Assert.False(current.PushTargetsMatch(stored));
    }

    [Fact]
    public void LayoutSourceChange_BreaksBothMatches()
    {
        PrecomputeKey stored = Stored();
        PrecomputeKey current = Stored() with { LayoutSourceHash = "other" };

        Assert.False(current.LayoutMatches(stored));
        Assert.False(current.PushTargetsMatch(stored));
    }

    private static PrecomputeKey Stored() =>
        new()
        {
            GeoJsonMd5 = "md5",
            NavDataSerial = 7,
            LayoutFormatVersion = 1,
            LayoutSourceHash = "layout",
            PushTargetSourceHash = "push",
            PushSidecarHash = "sidecar",
        };
}
