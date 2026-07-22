using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

public sealed class ArtccTdlsConfigParseTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static ArtccConfigRoot LoadZoaSnapshot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "artcc-zoa-snapshot.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ArtccConfigRoot>(json, JsonOptions)!;
    }

    private static IEnumerable<FacilityConfig> Walk(FacilityConfig facility)
    {
        yield return facility;
        foreach (var child in facility.ChildFacilities)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }

    [Fact]
    public void Deserialize_ZoaSnapshot_FindsFiveTdlsConfiguredFacilities()
    {
        var root = LoadZoaSnapshot();

        var tdlsFacilities = Walk(root.Facility)
            .Where(f => f.TdlsConfiguration is not null)
            .Select(f => f.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { "OAK", "RNO", "SFO", "SJC", "SMF" }, tdlsFacilities);
    }

    [Fact]
    public void ZoaRootFacility_HasNoTdlsConfiguration()
    {
        var root = LoadZoaSnapshot();
        Assert.Null(root.Facility.TdlsConfiguration);
    }

    [Fact]
    public void SfoTdlsConfig_MandatoryFieldsMatchUpstream()
    {
        var root = LoadZoaSnapshot();
        var sfo = Walk(root.Facility).First(f => f.Id == "SFO");
        var tdls = sfo.TdlsConfiguration!;

        Assert.False(tdls.MandatorySid);
        Assert.True(tdls.MandatoryExpect);
        Assert.True(tdls.MandatoryDepFreq);
        Assert.False(tdls.MandatoryClimbout);
        Assert.False(tdls.MandatoryClimbvia);
        Assert.False(tdls.MandatoryInitialAlt);
        Assert.False(tdls.MandatoryContactInfo);
        Assert.False(tdls.MandatoryLocalInfo);
    }

    [Fact]
    public void SfoTdlsConfig_HasPopulatedClearanceValueLists()
    {
        var root = LoadZoaSnapshot();
        var sfo = Walk(root.Facility).First(f => f.Id == "SFO");
        var tdls = sfo.TdlsConfiguration!;

        // SFO moved its SIDs into ops configs, so the facility-level list is empty on the wire
        // and only the resolver returns anything. The value lists stay facility-level.
        Assert.Empty(tdls.Sids);
        Assert.NotEmpty(tdls.ResolveSids(opConfigId: null));
        Assert.NotEmpty(tdls.Climbouts);
        Assert.NotEmpty(tdls.Climbvias);
        Assert.NotEmpty(tdls.InitialAlts);
        Assert.NotEmpty(tdls.DepFreqs);
        Assert.NotEmpty(tdls.Expects);
        Assert.NotEmpty(tdls.ContactInfos);
        Assert.NotEmpty(tdls.LocalInfos);

        foreach (var value in tdls.Expects)
        {
            Assert.False(string.IsNullOrEmpty(value.Id));
            Assert.False(string.IsNullOrEmpty(value.Value));
        }
    }

    [Fact]
    public void SmfTdlsConfig_ResolvesFacilityLevelDefaultsWhenOpConfigsAreDisabled()
    {
        // SMF still lists its SIDs at facility level (no ops configs), so the resolvers fall
        // through to the facility list and the facility-level defaults stay in force.
        var root = LoadZoaSnapshot();
        var smf = Walk(root.Facility).First(f => f.Id == "SMF");
        var tdls = smf.TdlsConfiguration!;

        Assert.False(tdls.DclOpConfigsEnabled);
        Assert.Empty(tdls.OpConfigs);
        Assert.Same(tdls.Sids, tdls.ResolveSids(opConfigId: null));
        Assert.Null(tdls.ResolveOpConfig(opConfigId: null));

        var defaultSidId = tdls.ResolveDefaultSidId(opConfigId: null);
        Assert.Equal(tdls.DefaultSidId, defaultSidId);
        Assert.NotNull(defaultSidId);
        Assert.Contains(tdls.ResolveSids(opConfigId: null), sid => sid.Id == defaultSidId);
    }

    [Fact]
    public void OpConfigDefaults_AreOptionalPerFacility()
    {
        // Whether a config names a default SID/transition is per-facility, so a consumer must
        // tolerate null on either: SFO sets both on all seven, OAK sets neither on any three.
        var root = LoadZoaSnapshot();
        var sfo = Walk(root.Facility).First(f => f.Id == "SFO").TdlsConfiguration!;
        var oak = Walk(root.Facility).First(f => f.Id == "OAK").TdlsConfiguration!;

        Assert.All(sfo.OpConfigs, c => Assert.NotNull(c.DefaultSidId));
        Assert.All(sfo.OpConfigs, c => Assert.NotNull(c.DefaultTransitionId));
        Assert.All(oak.OpConfigs, c => Assert.Null(c.DefaultSidId));
        Assert.All(oak.OpConfigs, c => Assert.Null(c.DefaultTransitionId));

        // The resolver reads through to the active config, never the empty facility-level field.
        var first = sfo.OpConfigs[0];
        Assert.Equal(first.DefaultSidId, sfo.ResolveDefaultSidId(first.Id));
        Assert.Equal(first.DefaultTransitionId, sfo.ResolveDefaultTransitionId(first.Id));
        Assert.Null(oak.ResolveDefaultSidId(oak.OpConfigs[0].Id));
    }

    [Fact]
    public void TdlsSidTransition_PreservesPopulatedAndNullDefaults()
    {
        var root = LoadZoaSnapshot();
        var sfo = Walk(root.Facility).First(f => f.Id == "SFO");

        var allTransitions = sfo.TdlsConfiguration!.ResolveSids(opConfigId: null).SelectMany(s => s.Transitions).ToList();

        Assert.NotEmpty(allTransitions);

        Assert.Contains(allTransitions, t => !string.IsNullOrEmpty(t.DefaultExpect));
        Assert.Contains(
            allTransitions,
            t =>
                t.DefaultExpect is null
                || t.DefaultClimbout is null
                || t.DefaultClimbvia is null
                || t.DefaultInitialAlt is null
                || t.DefaultDepFreq is null
                || t.DefaultContactInfo is null
                || t.DefaultLocalInfo is null
        );

        foreach (var t in allTransitions)
        {
            Assert.False(string.IsNullOrEmpty(t.Id));
            Assert.False(string.IsNullOrEmpty(t.Name));
        }
    }

    [Fact]
    public void OakTdlsConfig_ExposesItsThreeOpConfigs()
    {
        var root = LoadZoaSnapshot();
        var oak = Walk(root.Facility).First(f => f.Id == "OAK");
        var tdls = oak.TdlsConfiguration!;

        Assert.True(tdls.DclOpConfigsEnabled);
        Assert.Equal(["OAKW", "OAKE", "SFOE"], tdls.OpConfigs.Select(c => c.Name));
        Assert.Empty(tdls.Sids);

        foreach (var config in tdls.OpConfigs)
        {
            Assert.False(string.IsNullOrEmpty(config.Id));
            Assert.NotEmpty(config.Sids);
        }
    }

    [Fact]
    public void ResolveSids_PicksTheNamedOpConfig_AndFallsBackToTheFirst()
    {
        var root = LoadZoaSnapshot();
        var oak = Walk(root.Facility).First(f => f.Id == "OAK");
        var tdls = oak.TdlsConfiguration!;

        var oakE = tdls.OpConfigs.First(c => c.Name == "OAKE");
        Assert.Same(oakE.Sids, tdls.ResolveSids(oakE.Id));

        // Unknown and unset both fall back to the first config rather than yielding nothing —
        // a facility whose SIDs all live in ops configs must never resolve to an empty list.
        Assert.Same(tdls.OpConfigs[0].Sids, tdls.ResolveSids(opConfigId: null));
        Assert.Same(tdls.OpConfigs[0].Sids, tdls.ResolveSids("not-a-real-id"));
    }

    [Fact]
    public void OpConfigs_ReuseSidNamesButNotAlwaysSidIds()
    {
        // The id a clearance carries is only meaningful against the config that was active when
        // it was picked: OAK issues a distinct id per config for the same SID name.
        var root = LoadZoaSnapshot();
        var oak = Walk(root.Facility).First(f => f.Id == "OAK");
        var tdls = oak.TdlsConfiguration!;

        var west = tdls.OpConfigs.First(c => c.Name == "OAKW");
        var east = tdls.OpConfigs.First(c => c.Name == "OAKE");

        var shared = west.Sids.Select(s => s.Name).Intersect(east.Sids.Select(s => s.Name)).ToList();
        Assert.NotEmpty(shared);

        foreach (var name in shared)
        {
            var westId = west.Sids.First(s => s.Name == name).Id;
            var eastId = east.Sids.First(s => s.Name == name).Id;
            Assert.NotEqual(westId, eastId);
        }
    }

    [Fact]
    public void SfoOpConfigs_CarryPerConfigTransitionDefaults()
    {
        // The payload of the feature: the same SID + transition issues different local info per
        // runway configuration.
        var root = LoadZoaSnapshot();
        var sfo = Walk(root.Facility).First(f => f.Id == "SFO");
        var tdls = sfo.TdlsConfiguration!;

        Assert.True(tdls.DclOpConfigsEnabled);
        Assert.Equal(7, tdls.OpConfigs.Count);

        var localInfos = new List<string?>();
        foreach (var name in new[] { "2801", "2828RT", "0101" })
        {
            var config = tdls.OpConfigs.First(c => c.Name == name);
            var transition = config.Sids.First(s => s.Name == "TRUKN2").Transitions.First(t => t.Name == "GRTFL");
            localInfos.Add(transition.DefaultLocalInfo);
        }

        Assert.Equal(["EXPECT RWY 1R", "EXPECT RWY 28L", "EXPECT RWY 1L"], localInfos);
    }
}
