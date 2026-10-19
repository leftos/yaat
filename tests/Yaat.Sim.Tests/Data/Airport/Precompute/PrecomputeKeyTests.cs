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
        var current = PrecomputeKey.Current("md5", 7, "sidecar", AirportSidecarHash.OfStandLeadIns([]));
        JsonObject json = JsonSerializer.SerializeToNode(current, GroundLayoutSerializer.Options)!.AsObject();
        string field = json.Select(property => property.Key)
            .Single(key => key.Equals(nameof(PrecomputeKey.StandLeadInHash), StringComparison.OrdinalIgnoreCase));
        json.Remove(field);

        PrecomputeKey old = json.Deserialize<PrecomputeKey>(GroundLayoutSerializer.Options)!;

        Assert.Equal(string.Empty, old.StandLeadInHash);
        Assert.False(current.LayoutMatches(old));
    }

    /// <summary>
    /// The offline comparison of a stored key with the key built for a client that has no vNAS ignores the two facts
    /// only the maintainer's run knows (the GeoJSON MD5 and the navdata serial, which the offline key leaves blank) and
    /// holds on all four offline fields: the layout format version and the three source hashes.
    /// </summary>
    [Fact]
    public void PushTargetsMatchOffline_IgnoresMd5AndSerial_ButNotSourceHashes()
    {
        var stored = PrecomputeKey.Current("md5", 7, "sidecar", "lead-ins");
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
