using Xunit;
using Yaat.Sim.Data.Airport.Precompute;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// The staleness of a <see cref="PrecomputeKey"/>: each of the four layout inputs (GeoJSON hash, navdata serial,
/// layout format version, layout source hash) breaks both matches when it moves, and a push-target source change
/// breaks only the push half. One test per layout clause, so dropping any clause of
/// <see cref="PrecomputeKey.LayoutMatches"/> fails a test.
/// </summary>
public class PrecomputeKeyTests
{
    [Fact]
    public void PushOnlyChange_KeepsLayoutMatch()
    {
        PrecomputeKey stored = Stored();
        PrecomputeKey current = Key("md5", 7, 1, "layout", "other");

        Assert.True(current.LayoutMatches(stored));
        Assert.False(current.PushTargetsMatch(stored));
    }

    [Fact]
    public void GeoJsonChange_BreaksBothMatches()
    {
        PrecomputeKey stored = Stored();
        PrecomputeKey current = Key("other", 7, 1, "layout", "push");

        Assert.False(current.LayoutMatches(stored));
        Assert.False(current.PushTargetsMatch(stored));
    }

    [Fact]
    public void NavDataSerialChange_BreaksBothMatches()
    {
        PrecomputeKey stored = Stored();
        PrecomputeKey current = Key("md5", 8, 1, "layout", "push");

        Assert.False(current.LayoutMatches(stored));
        Assert.False(current.PushTargetsMatch(stored));
    }

    [Fact]
    public void LayoutFormatVersionChange_BreaksBothMatches()
    {
        PrecomputeKey stored = Stored();
        PrecomputeKey current = Key("md5", 7, 2, "layout", "push");

        Assert.False(current.LayoutMatches(stored));
        Assert.False(current.PushTargetsMatch(stored));
    }

    [Fact]
    public void LayoutSourceChange_BreaksBothMatches()
    {
        PrecomputeKey stored = Stored();
        PrecomputeKey current = Key("md5", 7, 1, "other", "push");

        Assert.False(current.LayoutMatches(stored));
        Assert.False(current.PushTargetsMatch(stored));
    }

    private static PrecomputeKey Stored() => Key("md5", 7, 1, "layout", "push");

    private static PrecomputeKey Key(
        string geoJsonMd5,
        long navDataSerial,
        int layoutFormatVersion,
        string layoutSourceHash,
        string pushTargetSourceHash
    ) => new(geoJsonMd5, navDataSerial, layoutFormatVersion, layoutSourceHash, pushTargetSourceHash);
}
