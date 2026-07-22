using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Coverage for the two facility-set resolvers against the committed ZOA config
/// snapshot, which carries the exact shapes both features exist for: OAK ATCT
/// links NCT and O90 bays via <c>externalBays</c>, and NCT consolidates five
/// child TDLS facilities while owning no TDLS configuration of its own.
/// Skips silently when the snapshot isn't present (mirrors TestVnasData).
/// </summary>
public class AccessibleFacilityResolverTests
{
    // ── Strips: own + descendants + facilities linked via externalBays ──

    [Fact]
    public void StripFacilities_TowerSeesTheFacilitiesItLinksBaysFrom()
    {
        if (TestArtccConfig.LoadZoa() is not { } config)
        {
            return;
        }

        var facilities = config.GetAccessibleStripFacilities("OAK_TWR");

        var own = facilities.Single(f => f.FacilityId == "OAK");
        Assert.True(own.IsStudentFacility);
        // OAK's config links NCT and O90 bays for scanning strips out; both open
        // as their own tabs so what was scanned there can actually be read.
        Assert.Contains(facilities, f => f.FacilityId == "NCT");
        Assert.Contains(facilities, f => f.FacilityId == "O90");
        Assert.All(facilities.Where(f => f.FacilityId != "OAK"), f => Assert.False(f.IsStudentFacility));
    }

    [Fact]
    public void StripFacilities_DoesNotFollowLinksOfLinkedFacilities()
    {
        if (TestArtccConfig.LoadZoa() is not { } config)
        {
            return;
        }

        // One hop only. SFO is a sibling ATCT under NCT — reachable from NCT's
        // subtree but not from anything OAK links, so it stays out.
        var facilities = config.GetAccessibleStripFacilities("OAK_TWR");

        Assert.DoesNotContain(facilities, f => f.FacilityId == "SFO");
        Assert.DoesNotContain(facilities, f => f.FacilityId == "SJC");
    }

    [Fact]
    public void StripFacilities_TraconSeesItsDescendantTowers()
    {
        if (TestArtccConfig.LoadZoa() is not { } config)
        {
            return;
        }

        var facilities = config.GetAccessibleStripFacilities("NCT_APP");

        Assert.True(facilities.Single(f => f.FacilityId == "NCT").IsStudentFacility);
        Assert.Contains(facilities, f => f.FacilityId == "OAK");
        Assert.Contains(facilities, f => f.FacilityId == "SFO");
    }

    [Fact]
    public void CommandTargetableBays_SpanEveryAccessibleFacility()
    {
        if (TestArtccConfig.LoadZoa() is not { } config)
        {
            return;
        }

        var bays = config.GetAllCommandTargetableStripBays("OAK_TWR");

        // NC1 is not among the bays OAK links, but it belongs to a facility OAK
        // can open — so a command issued from that tab has to resolve it.
        Assert.Contains(bays, b => b.Owner.Id == "NCT" && b.Bay.Name == "NC1");
        Assert.Contains(bays, b => b.Owner.Id == "OAK" && b.Bay.Name == "Ground 1");
        // Own-facility bays are never external; everything else is.
        Assert.All(bays, b => Assert.Equal(b.Owner.Id != "OAK", b.IsExternal));
    }

    [Fact]
    public void GetAccessibleStripBay_FacilitySegmentIsAHardFilter()
    {
        if (TestArtccConfig.LoadZoa() is not { } config)
        {
            return;
        }

        Assert.NotNull(config.GetAccessibleStripBay("OAK_TWR", "NCT", "NC1"));
        // "Ground 1" is OAK's bay; asking for it under NCT must not fall through.
        Assert.Null(config.GetAccessibleStripBay("OAK_TWR", "NCT", "Ground 1"));
    }

    // ── vTDLS: own + descendants, keyed on TDLS config, parents consolidate ──

    [Fact]
    public void TdlsFacilities_ParentConsolidatesItsChildren()
    {
        if (TestArtccConfig.LoadZoa() is not { } config)
        {
            return;
        }

        var facilities = config.GetAccessibleTdlsFacilities("NCT_APP");

        var nct = facilities.Single(f => f.FacilityId == "NCT");
        // NCT owns no tdlsConfiguration — it is listed purely as the consolidated
        // page over the five child facilities that do (upstream's parent view).
        Assert.Equal(["SFO", "OAK", "SJC", "SMF", "RNO"], nct.MemberFacilityIds);
        Assert.True(nct.IsStudentFacility);
    }

    [Fact]
    public void TdlsFacilities_LeafCarriesOnlyItself()
    {
        if (TestArtccConfig.LoadZoa() is not { } config)
        {
            return;
        }

        var facilities = config.GetAccessibleTdlsFacilities("NCT_APP");

        Assert.Equal(["OAK"], facilities.Single(f => f.FacilityId == "OAK").MemberFacilityIds);
    }

    [Fact]
    public void TdlsFacilities_TowerSeesOnlyItself_NotItsParent()
    {
        if (TestArtccConfig.LoadZoa() is not { } config)
        {
            return;
        }

        // Upstream-faithful: the consolidated page is a top-down-consolidation
        // affordance, so working OAK does not reach up to NCT (unlike strips,
        // which follow the external-bay links).
        var facilities = config.GetAccessibleTdlsFacilities("OAK_TWR");

        var only = Assert.Single(facilities);
        Assert.Equal("OAK", only.FacilityId);
        Assert.Equal(["OAK"], only.MemberFacilityIds);
    }

    [Fact]
    public void TdlsFacilities_ExcludeFacilitiesWithNoTdlsAnywhereBeneath()
    {
        if (TestArtccConfig.LoadZoa() is not { } config)
        {
            return;
        }

        var facilities = config.GetAccessibleTdlsFacilities("NCT_APP");

        // MRY has strip bays but no TDLS config and no children — nothing to show.
        Assert.DoesNotContain(facilities, f => f.FacilityId == "MRY");
    }
}
