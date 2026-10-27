using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// The staleness of a <see cref="PrecomputeKey"/>: each of the four layout inputs (GeoJSON hash, navdata serial,
/// layout format version, layout source hash, stand lead-in hash) breaks both matches when it moves, and a push-target source
/// change or an airport sidecar change outside the lead-ins breaks only the push half. One test per clause, so dropping any clause of
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

    /// <summary>A stand lead-in edit reshapes the layout, so it stales the layout half and the push half with it.</summary>
    [Fact]
    public void StandLeadInChange_BreaksBothMatches()
    {
        PrecomputeKey stored = Stored();
        PrecomputeKey current = Stored() with { StandLeadInHash = "other" };

        Assert.False(current.LayoutMatches(stored));
        Assert.False(current.PushTargetsMatch(stored));
    }

    /// <summary>
    /// An entry written before the lead-in hash existed still reads, and reads as layout-stale against a key computed now,
    /// even for an airport with no lead-ins.
    /// </summary>
    [Fact]
    public void StoredKeyWithoutStandLeadInHash_ReadsAsLayoutStale()
    {
        var current = PrecomputeKey.Current(
            "md5",
            7,
            "sidecar",
            AirportSidecarHash.OfStandLeadIns([]),
            string.Empty,
            standoffInputHash: string.Empty
        );
        JsonObject json = JsonSerializer.SerializeToNode(current, GroundLayoutSerializer.Options)!.AsObject();
        string field = json.Select(property => property.Key)
            .Single(key => key.Equals(nameof(PrecomputeKey.StandLeadInHash), StringComparison.OrdinalIgnoreCase));
        json.Remove(field);

        PrecomputeKey old = json.Deserialize<PrecomputeKey>(GroundLayoutSerializer.Options)!;

        Assert.Equal(string.Empty, old.StandLeadInHash);
        Assert.False(current.LayoutMatches(old));
    }

    /// <summary>A videomap change can move the bars the layout snaps to, so it stales both halves; the offline check never sees it.</summary>
    [Fact]
    public void VideoMapChange_BreaksBothMatches()
    {
        var stored = PrecomputeKey.Current("md5", 7, "sidecar", "lead-ins", "videomap", standoffInputHash: "standoff");
        var current = PrecomputeKey.Current("md5", 7, "sidecar", "lead-ins", "other", standoffInputHash: "standoff");

        Assert.False(current.LayoutMatches(stored));
        Assert.False(current.PushTargetsMatch(stored));
        Assert.True(current.PushTargetsMatchOffline(stored));
    }

    /// <summary>
    /// An entry written before the videomap hash existed still reads, with the empty hash of an airport without a map, so
    /// it stays current against a key built without a map.
    /// </summary>
    [Fact]
    public void AnEntryWithoutAVideoMapHash_ReadsEmpty()
    {
        var current = PrecomputeKey.Current("md5", 7, "sidecar", "lead-ins", videoMapHash: "", standoffInputHash: "standoff");
        JsonObject json = JsonSerializer.SerializeToNode(current, GroundLayoutSerializer.Options)!.AsObject();
        string field = json.Select(property => property.Key)
            .Single(key => key.Equals(nameof(PrecomputeKey.VideoMapHash), StringComparison.OrdinalIgnoreCase));
        json.Remove(field);

        PrecomputeKey old = json.Deserialize<PrecomputeKey>(GroundLayoutSerializer.Options)!;

        Assert.Equal(string.Empty, old.VideoMapHash);
        Assert.True(current.LayoutMatches(old));
    }

    /// <summary>
    /// A change in the runway inputs the hold-short standoff rule reads moves the bars, so it stales both halves; equal
    /// hashes match, and the offline check never sees it.
    /// </summary>
    [Fact]
    public void StandoffInputChange_BreaksBothMatches()
    {
        var stored = PrecomputeKey.Current("md5", 7, "sidecar", "lead-ins", string.Empty, standoffInputHash: "standoff");
        var same = PrecomputeKey.Current("md5", 7, "sidecar", "lead-ins", string.Empty, standoffInputHash: "standoff");
        var current = PrecomputeKey.Current("md5", 7, "sidecar", "lead-ins", string.Empty, standoffInputHash: "other");

        Assert.True(same.LayoutMatches(stored));
        Assert.True(same.PushTargetsMatch(stored));
        Assert.False(current.LayoutMatches(stored));
        Assert.False(current.PushTargetsMatch(stored));
        Assert.True(current.PushTargetsMatchOffline(stored));
    }

    /// <summary>
    /// An entry written before the standoff-input hash existed still reads, with an empty hash, so it reads as layout-stale
    /// against a key computed now from navdata.
    /// </summary>
    [Fact]
    public void AnEntryWithoutAStandoffInputHash_ReadsEmpty()
    {
        var current = PrecomputeKey.Current("md5", 7, "sidecar", "lead-ins", string.Empty, standoffInputHash: "standoff");
        JsonObject json = JsonSerializer.SerializeToNode(current, GroundLayoutSerializer.Options)!.AsObject();
        string field = json.Select(property => property.Key)
            .Single(key => key.Equals(nameof(PrecomputeKey.StandoffInputHash), StringComparison.OrdinalIgnoreCase));
        json.Remove(field);

        PrecomputeKey old = json.Deserialize<PrecomputeKey>(GroundLayoutSerializer.Options)!;

        Assert.Equal(string.Empty, old.StandoffInputHash);
        Assert.False(current.LayoutMatches(old));
        Assert.True(current.PushTargetsMatchOffline(old));
    }

    /// <summary>
    /// The offline comparison of a stored key with the key built for a client that has no vNAS ignores the two facts
    /// only the maintainer's run knows (the GeoJSON MD5 and the navdata serial, which the offline key leaves blank) and
    /// holds on all four offline fields: the layout format version and the three source hashes.
    /// </summary>
    [Fact]
    public void PushTargetsMatchOffline_IgnoresMd5AndSerial_ButNotSourceHashes()
    {
        var stored = PrecomputeKey.Current("md5", 7, "sidecar", "lead-ins", string.Empty, standoffInputHash: string.Empty);
        var offline = PrecomputeKey.CurrentOffline("sidecar");

        Assert.NotEqual(stored.GeoJsonMd5, offline.GeoJsonMd5);
        Assert.NotEqual(stored.NavDataSerial, offline.NavDataSerial);
        Assert.True(offline.PushTargetsMatchOffline(stored));
        Assert.False(stored.LayoutMatches(offline));
        Assert.False(stored.PushTargetsMatch(offline));

        Assert.False((stored with { LayoutFormatVersion = stored.LayoutFormatVersion + 1 }).PushTargetsMatchOffline(offline));
        Assert.False((stored with { LayoutSourceHash = "other" }).PushTargetsMatchOffline(offline));
        Assert.False((stored with { PushTargetSourceHash = "other" }).PushTargetsMatchOffline(offline));
        Assert.False((stored with { PushSidecarHash = "other" }).PushTargetsMatchOffline(offline));
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
            StandLeadInHash = "lead-ins",
        };
}
