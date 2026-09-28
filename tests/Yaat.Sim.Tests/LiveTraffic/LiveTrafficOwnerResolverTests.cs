using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.LiveTraffic;

/// <summary>
/// <see cref="LiveTrafficOwnerResolver.Resolve"/> stores the configured facility and sector spelling, not the feed's, so a
/// shadow's owner is the same <see cref="TrackOwner"/> the configured position resolves to however the feed spells it.
/// </summary>
public class LiveTrafficOwnerResolverTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    private static SimScenarioState Scenario(ArtccConfigRoot? config, string? artccId) =>
        new()
        {
            ScenarioId = "s",
            ScenarioName = "s",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            ArtccConfig = config,
            ArtccId = artccId,
        };

    [Fact]
    public void StarsOwner_FeedFacilityInLowerCase_TakesTheConfiguredSpelling()
    {
        Assert.SkipWhen(_zoa is null, "ZOA config not available");
        SimScenarioState scenario = Scenario(_zoa, "ZOA");

        TrackOwner? upper = LiveTrafficOwnerResolver.Resolve(scenario, "NCT", "3O");
        TrackOwner? lower = LiveTrafficOwnerResolver.Resolve(scenario, "nct", "3o");

        Assert.NotNull(upper);
        Assert.Equal("NCT", upper.FacilityId);
        Assert.Equal("O", upper.SectorId);
        Assert.Equal(upper, lower);
    }

    [Fact]
    public void EramOwner_FeedCentreInLowerCase_IsTheConfiguredPositionsOwner()
    {
        Assert.SkipWhen(_zoa is null, "ZOA config not available");
        SimScenarioState scenario = Scenario(_zoa, "ZOA");

        TrackOwner? fromFeed = LiveTrafficOwnerResolver.Resolve(scenario, "zoa", "44");

        Assert.Equal(_zoa!.ResolveEramCode("C44"), fromFeed);
    }

    [Fact]
    public void EramOwner_WithNoCentre_HasNoFacility()
    {
        TrackOwner? owner = LiveTrafficOwnerResolver.Resolve(Scenario(null, null), null, "44");

        Assert.NotNull(owner);
        Assert.Null(owner.FacilityId);
    }
}
