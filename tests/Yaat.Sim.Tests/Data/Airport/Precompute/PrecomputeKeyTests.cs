using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
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
        PrecomputeKey current = Stored() with { LayoutSidecarHash = "other" };

        Assert.False(current.LayoutMatches(stored));
        Assert.False(current.PushTargetsMatch(stored));
    }

    /// <summary>
    /// An entry written before the layout sidecar hash existed still reads, and reads as layout-stale against a key computed
    /// now, even for an airport with no lead-ins.
    /// </summary>
    [Fact]
    public void StoredKeyWithoutLayoutSidecarHash_ReadsAsLayoutStale()
    {
        var current = PrecomputeKey.Current(
            "md5",
            7,
            "sidecar",
            AirportSidecarHash.OfLayoutSidecar([], [], CornerFilletOverrides.None),
            string.Empty,
            standoffInputHash: string.Empty
        );
        JsonObject json = JsonSerializer.SerializeToNode(current, GroundLayoutSerializer.Options)!.AsObject();
        string field = json.Select(property => property.Key)
            .Single(key => key.Equals(nameof(PrecomputeKey.LayoutSidecarHash), StringComparison.OrdinalIgnoreCase));
        json.Remove(field);

        PrecomputeKey old = json.Deserialize<PrecomputeKey>(GroundLayoutSerializer.Options)!;

        Assert.Equal(string.Empty, old.LayoutSidecarHash);
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

    /// <summary>
    /// An airport with no spot-edge chains hashes as its stand lead-ins alone. The literals are the SHA-256 of that canonical
    /// text.
    /// </summary>
    [Fact]
    public void LayoutSidecarHash_OfAnAirportWithoutChains_HashesItsLeadInsAlone()
    {
        StandLeadIn leadIn = new("D12", "A", new LatLon(37.619622, -122.381946), "why");

        Assert.Equal(
            "ca4ba579105f36f4be57e228d868e15131c865b8ff280556153c190aa8d40502",
            AirportSidecarHash.OfLayoutSidecar([leadIn], [], CornerFilletOverrides.None)
        );
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            AirportSidecarHash.OfLayoutSidecar([], [], CornerFilletOverrides.None)
        );
    }

    /// <summary>
    /// The layout sidecar hash moves with a spot-edge chain and ignores its notes and every sidecar section the layout build
    /// does not read.
    /// </summary>
    [Fact]
    public void LayoutSidecarHash_FollowsSpotEdges_NotTheirNotesOrOtherSections()
    {
        const string LeadIns = """ "standLeadIns": [{ "stand": "D12", "taxiway": "A", "point": [-122.381946, 37.619622] }] """;
        const string Chain = """ "spotEdges": [{ "name": "RAMP", "path": [{ "spot": "1" }, { "spot": "2" }], "notes": "first" }] """;
        const string Renoted = """ "spotEdges": [{ "name": "RAMP", "path": [{ "spot": "1" }, { "spot": "2" }], "notes": "second" }] """;
        const string Other = """ "nonMovementTaxilanes": [{ "name": "L1" }] """;

        string bare = LayoutSidecarHashOf($$"""{ "airportId": "KSFO", {{LeadIns}} }""");
        string chained = LayoutSidecarHashOf($$"""{ "airportId": "KSFO", {{LeadIns}}, {{Chain}} }""");

        Assert.Equal(
            AirportSidecarHash.OfLayoutSidecar(
                [new StandLeadIn("D12", "A", new LatLon(37.619622, -122.381946), null)],
                [],
                CornerFilletOverrides.None
            ),
            bare
        );
        Assert.NotEqual(bare, chained);
        Assert.Equal(chained, LayoutSidecarHashOf($$"""{ "airportId": "KSFO", {{LeadIns}}, {{Renoted}} }"""));
        Assert.Equal(chained, LayoutSidecarHashOf($$"""{ "airportId": "KSFO", {{LeadIns}}, {{Chain}}, {{Other}} }"""));
        Assert.Equal(bare, LayoutSidecarHashOf($$"""{ "airportId": "KSFO", {{LeadIns}}, {{Other}} }"""));
    }

    /// <summary>Moving a chain's point, renaming the chain or reordering its path each change the layout sidecar hash.</summary>
    [Fact]
    public void LayoutSidecarHash_FollowsAChainsPointsNameAndOrder()
    {
        SpotEdgePoint spot = new(SpotEdgePointKind.Spot, "1", null, null, null, null);
        SpotEdgePoint point = new(SpotEdgePointKind.Point, null, null, null, null, new LatLon(25.79, -80.27));
        SpotEdgePoint moved = point with { Position = new LatLon(25.79, -80.2701) };
        string chain = AirportSidecarHash.OfLayoutSidecar([], [new SpotEdgeChain("RAMP", [spot, point], null)], CornerFilletOverrides.None);

        Assert.NotEqual(chain, AirportSidecarHash.OfLayoutSidecar([], [new SpotEdgeChain("RAMP", [spot, moved], null)], CornerFilletOverrides.None));
        Assert.NotEqual(chain, AirportSidecarHash.OfLayoutSidecar([], [new SpotEdgeChain("N8", [spot, point], null)], CornerFilletOverrides.None));
        Assert.NotEqual(chain, AirportSidecarHash.OfLayoutSidecar([], [new SpotEdgeChain("RAMP", [point, spot], null)], CornerFilletOverrides.None));
    }

    /// <summary>
    /// Adding a corner override or changing any of its fields changes the layout sidecar hash; reordering the overrides or
    /// swapping a corner's taxiway order (its bearings and tangents with it) does not.
    /// </summary>
    [Fact]
    public void LayoutSidecarHash_FollowsCornerFillets()
    {
        CornerFilletOverride first = new(37.7, -122.2, "A", "B", 10, 100, false, 120, true, 40, 50);
        CornerFilletOverride second = new(37.7, -122.2, "A", "B", 190, 280, true, null, false, null, null);
        CornerFilletOverride swapped = first with { TaxiwayA = "B", TaxiwayB = "A", BearingA = 100, BearingB = 10, TangentAFt = 50, TangentBFt = 40 };
        string none = HashOf();
        string both = HashOf(first, second);

        Assert.NotEqual(none, HashOf(first));
        Assert.NotEqual(HashOf(first), both);
        Assert.Equal(both, HashOf(second, first));
        Assert.Equal(both, HashOf(swapped, second));
        CornerFilletOverride[] changed =
        [
            first with
            {
                ApexLat = 37.7000001,
            },
            first with
            {
                ApexLon = -122.2000001,
            },
            first with
            {
                TaxiwayA = "C",
            },
            first with
            {
                TaxiwayB = "C",
            },
            first with
            {
                BearingA = 11,
            },
            first with
            {
                BearingB = 101,
            },
            first with
            {
                Suppress = true,
            },
            first with
            {
                RadiusFt = 121,
            },
            first with
            {
                RadiusFt = null,
            },
            first with
            {
                Force = false,
            },
            first with
            {
                TangentAFt = 41,
            },
            first with
            {
                TangentBFt = null,
            },
        ];
        Assert.All(changed, edit => Assert.NotEqual(both, HashOf(edit, second)));
    }

    /// <summary>Two overrides with one key but different edits hash the same in either order.</summary>
    [Fact]
    public void LayoutSidecarHash_OfTwoOverridesWithOneKey_IgnoresTheirOrder()
    {
        CornerFilletOverride first = new(37.7, -122.2, "A", "B", 10, 100, false, 120, false, null, null);
        CornerFilletOverride second = first with { RadiusFt = null, Force = true, TangentAFt = 40, TangentBFt = 50 };

        Assert.Equal(HashOf(first, second), HashOf(second, first));
    }

    /// <summary>A -0 field hashes as 0.</summary>
    [Fact]
    public void LayoutSidecarHash_ReadsNegativeZeroAsZero()
    {
        CornerFilletOverride zero = new(0, 0, "A", "B", 0, 100, false, 120, false, 40, 50);
        CornerFilletOverride negativeZero = zero with { ApexLat = -0.0, ApexLon = -0.0, BearingA = -0.0 };
        CornerFilletOverride other = zero with { TaxiwayB = "C" };

        Assert.Equal(HashOf(zero, other), HashOf(other, negativeZero));
    }

    /// <summary>
    /// A sidecar file's cornerFillets section reaches the layout sidecar hash through the catalog: an entry moves the hash,
    /// its note does not, its radius does, the entry listed with its taxiway pair and bearings swapped does not, and an
    /// entry the loader drops (it has no apex) leaves the hash as if the section were absent.
    /// </summary>
    [Fact]
    public void LayoutSidecarHash_FollowsACornerFilletsSection_OfTheSidecarFile()
    {
        const string LeadIns = """ "standLeadIns": [{ "stand": "D12", "taxiway": "A", "point": [-122.381946, 37.619622] }] """;
        const string Section = """
            "cornerFillets": [{ "apex": [-122.38, 37.62], "taxiways": ["A", "B"], "bearings": [10, 100], "radiusFt": 60, "note": "x" }]
            """;
        const string Renoted = """
            "cornerFillets": [{ "apex": [-122.38, 37.62], "taxiways": ["A", "B"], "bearings": [10, 100], "radiusFt": 60, "note": "y" }]
            """;
        const string WiderRadius = """
            "cornerFillets": [{ "apex": [-122.38, 37.62], "taxiways": ["A", "B"], "bearings": [10, 100], "radiusFt": 61, "note": "x" }]
            """;
        const string Swapped = """
            "cornerFillets": [{ "apex": [-122.38, 37.62], "taxiways": ["B", "A"], "bearings": [100, 10], "radiusFt": 60, "note": "x" }]
            """;
        const string Apexless = """ "cornerFillets": [{ "taxiways": ["A", "B"], "bearings": [10, 100], "radiusFt": 60 }] """;

        string bare = LayoutSidecarHashOf($$"""{ "airportId": "KSFO", {{LeadIns}} }""");
        string sectioned = LayoutSidecarHashOf($$"""{ "airportId": "KSFO", {{LeadIns}}, {{Section}} }""");

        Assert.NotEqual(bare, sectioned);
        Assert.Equal(sectioned, LayoutSidecarHashOf($$"""{ "airportId": "KSFO", {{LeadIns}}, {{Renoted}} }"""));
        Assert.NotEqual(sectioned, LayoutSidecarHashOf($$"""{ "airportId": "KSFO", {{LeadIns}}, {{WiderRadius}} }"""));
        Assert.Equal(sectioned, LayoutSidecarHashOf($$"""{ "airportId": "KSFO", {{LeadIns}}, {{Swapped}} }"""));
        Assert.Equal(bare, LayoutSidecarHashOf($$"""{ "airportId": "KSFO", {{LeadIns}}, {{Apexless}} }"""));
    }

    /// <summary>
    /// A cornerFillets entry added to an airport's sidecar stales both halves of its precompute key, since the layout it
    /// builds moves; two keys built from the same sidecar still match.
    /// </summary>
    [Fact]
    public void CornerFilletsChange_BreaksBothMatches()
    {
        const string LeadIns = """ "standLeadIns": [{ "stand": "D12", "taxiway": "A", "point": [-122.381946, 37.619622] }] """;
        const string Section = """
            "cornerFillets": [{ "apex": [-122.38, 37.62], "taxiways": ["A", "B"], "bearings": [10, 100], "radiusFt": 60 }]
            """;

        var stored = PrecomputeKey.Current(
            "md5",
            7,
            "sidecar",
            LayoutSidecarHashOf($$"""{ "airportId": "KSFO", {{LeadIns}} }"""),
            string.Empty,
            standoffInputHash: string.Empty
        );
        var current = PrecomputeKey.Current(
            "md5",
            7,
            "sidecar",
            LayoutSidecarHashOf($$"""{ "airportId": "KSFO", {{LeadIns}}, {{Section}} }"""),
            string.Empty,
            standoffInputHash: string.Empty
        );
        var same = PrecomputeKey.Current(
            "md5",
            7,
            "sidecar",
            LayoutSidecarHashOf($$"""{ "airportId": "KSFO", {{LeadIns}} }"""),
            string.Empty,
            standoffInputHash: string.Empty
        );

        Assert.False(current.LayoutMatches(stored));
        Assert.False(current.PushTargetsMatch(stored));
        Assert.True(current.PushTargetsMatchOffline(stored));
        Assert.True(same.LayoutMatches(stored));
    }

    private static string HashOf(params CornerFilletOverride[] corners) =>
        AirportSidecarHash.OfLayoutSidecar([], [], new CornerFilletOverrides(corners));

    /// <summary><see cref="AirportSidecarHash.ForLayoutSidecar"/> of SFO with <paramref name="sidecarJson"/> its one sidecar.</summary>
    private static string LayoutSidecarHashOf(string sidecarJson)
    {
        string root = Path.Combine(Path.GetTempPath(), "layout-sidecar-hash-" + Guid.NewGuid());
        string airports = Path.Combine(root, "ZOA", "Airports");
        Directory.CreateDirectory(airports);
        try
        {
            File.WriteAllText(Path.Combine(airports, "sfo.json"), sidecarJson);
            return AirportSidecarHash.ForLayoutSidecar(root, "SFO");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
            LayoutSidecarHash = "lead-ins",
        };
}
