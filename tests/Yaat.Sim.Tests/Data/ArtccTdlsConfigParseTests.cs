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

        Assert.NotEmpty(tdls.Sids);
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
    public void SfoTdlsConfig_DefaultSidIdResolvesToASidInTheList()
    {
        var root = LoadZoaSnapshot();
        var sfo = Walk(root.Facility).First(f => f.Id == "SFO");
        var tdls = sfo.TdlsConfiguration!;

        Assert.NotNull(tdls.DefaultSidId);
        Assert.Contains(tdls.Sids, sid => sid.Id == tdls.DefaultSidId);
    }

    [Fact]
    public void TdlsSidTransition_PreservesPopulatedAndNullDefaults()
    {
        var root = LoadZoaSnapshot();
        var sfo = Walk(root.Facility).First(f => f.Id == "SFO");

        var allTransitions = sfo.TdlsConfiguration!.Sids.SelectMany(s => s.Transitions).ToList();

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
    public void OakTdlsConfig_HasDefaultTransitionIdSet()
    {
        var root = LoadZoaSnapshot();
        var oak = Walk(root.Facility).First(f => f.Id == "OAK");
        var tdls = oak.TdlsConfiguration!;

        Assert.NotNull(tdls.DefaultTransitionId);
        Assert.NotNull(tdls.DefaultSidId);

        var defaultSid = tdls.Sids.First(s => s.Id == tdls.DefaultSidId);
        Assert.Contains(defaultSid.Transitions, t => t.Id == tdls.DefaultTransitionId);
    }
}
