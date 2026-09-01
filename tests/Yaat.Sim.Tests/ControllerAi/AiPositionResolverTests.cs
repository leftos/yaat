using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary><see cref="AiPositionResolver"/> over the real ZOA config: the OAK cab, its TRACON, and the ARTCC.</summary>
public class AiPositionResolverTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AiPositionResolverTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Catalog_ClassifiesTheOakCab_TheTracon_AndTheCenter()
    {
        if (_zoa is null)
        {
            return;
        }

        var catalog = AiPositionResolver.Catalog(_zoa, "OAK", AiTestHost.NoOverrides);

        var ground = Assert.Single(catalog, p => p.Callsign == "OAK_GND");
        Assert.Equal(ControlRole.Ground, ground.Role);
        Assert.Equal("Oakland Ground", ground.RadioName);
        Assert.Equal("OAK", ground.FacilityId);
        Assert.Equal(["OAK"], ground.AirportIds);

        var tower = Assert.Single(catalog, p => p.Callsign == "OAK_TWR");
        Assert.Equal(ControlRole.Local, tower.Role);

        Assert.DoesNotContain(catalog, p => p.Callsign == "OAK_DEL");
        Assert.DoesNotContain(catalog, p => p.Callsign.EndsWith("_RMP", StringComparison.Ordinal));
        Assert.DoesNotContain(catalog, p => p.FacilityId == "SFO");

        // NCT publishes several NCT_APP positions (one per STARS area); every one is Approach with its own TCP and
        // area airports, and the combined-area one covers OAK and SFO.
        var approaches = catalog.Where(p => p.Callsign == "NCT_APP").ToList();
        Assert.NotEmpty(approaches);
        Assert.All(approaches, p => Assert.Equal(ControlRole.Approach, p.Role));
        Assert.All(approaches, p => Assert.NotNull(p.Tcp));
        var combined = TestAiPositions.NorCalApproach(_zoa);
        Assert.Contains("OAK", combined.AirportIds);
        Assert.Contains("SFO", combined.AirportIds);

        var center = Assert.Single(catalog, p => p.Callsign == "OAK_14_CTR");
        Assert.Equal(ControlRole.Center, center.Role);
        Assert.Empty(center.AirportIds);
        Assert.Equal(TrackOwnerType.Eram, center.Identity.OwnerType);
    }

    [Fact]
    public void Catalog_IsSortedByRankThenPositionId()
    {
        if (_zoa is null)
        {
            return;
        }

        var catalog = AiPositionResolver.Catalog(_zoa, "OAK", AiTestHost.NoOverrides);

        var expected = catalog.OrderBy(p => ControlRoles.Rank(p.Role)).ThenBy(p => p.PositionId, StringComparer.Ordinal).Select(p => p.PositionId);
        Assert.Equal(expected, catalog.Select(p => p.PositionId));
    }

    [Fact]
    public void Override_PlaysClearanceDeliveryAsGround()
    {
        if (_zoa is null)
        {
            return;
        }

        var delivery = _zoa.FindPositionByCallsign("OAK_DEL")!;
        var overrides = new Dictionary<string, ControlRole>(StringComparer.Ordinal) { [delivery.Id] = ControlRole.Ground };

        var catalog = AiPositionResolver.Catalog(_zoa, "OAK", overrides);

        var played = Assert.Single(catalog, p => p.Callsign == "OAK_DEL");
        Assert.Equal(ControlRole.Ground, played.Role);
        Assert.Equal("Oakland Clearance", played.RadioName);
    }

    [Fact]
    public void Resolve_ReturnsTheEnabledPositions_SortedByRank_AndRejectsUnknownIds()
    {
        if (_zoa is null)
        {
            return;
        }

        var tower = _zoa.FindPositionByCallsign("OAK_TWR")!.Id;
        var ground = _zoa.FindPositionByCallsign("OAK_GND")!.Id;
        var config = new ControllerAiConfig
        {
            Seed = 1,
            EnabledPositionIds = [tower, ground],
            RoleOverrides = AiTestHost.NoOverrides,
        };

        var resolved = AiPositionResolver.Resolve(_zoa, "OAK", config);

        Assert.Equal(["OAK_GND", "OAK_TWR"], resolved.Select(p => p.Callsign));

        var unknown = new ControllerAiConfig
        {
            Seed = 1,
            EnabledPositionIds = ["not-a-position"],
            RoleOverrides = AiTestHost.NoOverrides,
        };
        Assert.Throws<InvalidOperationException>(() => AiPositionResolver.Resolve(_zoa, "OAK", unknown));
    }

    [Fact]
    public void Catalog_UnknownAirport_Throws()
    {
        if (_zoa is null)
        {
            return;
        }

        Assert.Throws<InvalidOperationException>(() => AiPositionResolver.Catalog(_zoa, "XYZ", AiTestHost.NoOverrides));
    }
}
