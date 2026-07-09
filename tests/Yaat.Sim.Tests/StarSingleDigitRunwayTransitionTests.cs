using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests;

/// <summary>
/// A controller issuing a STAR with a single-digit runway transition (e.g. <c>JARR BDEGA 1R</c>)
/// must resolve the same CIFP runway-transition legs as the zero-padded form (<c>01R</c>). The
/// JARR parser passes the runway token through unnormalized (see <see cref="JarrOverloadTests"/>),
/// and every runway-transition lookup builds its key as <c>"RW" + designator</c>. A single-digit
/// designator therefore produces <c>"RW1R"</c>, which never matches the CIFP-stored <c>"RW01R"</c>,
/// silently dropping the runway-transition legs from the flown route.
///
/// Real data: KSFO BDEGA STAR has an <c>RW01R</c> runway transition (BDEGA -&gt; CORKK -&gt; BRIXX).
///
/// See https://github.com/leftos/yaat/issues/273.
/// </summary>
[Collection("NavDbMutator")]
public class StarSingleDigitRunwayTransitionTests : IDisposable
{
    private readonly IDisposable _scope;

    public StarSingleDigitRunwayTransitionTests()
    {
        TestVnasData.EnsureInitialized();
        _scope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
    }

    public void Dispose() => _scope.Dispose();

    [Fact]
    public void Parser_SingleDigitRunway_PassesDesignatorUnnormalized()
    {
        // Entry point: the JARR parser hands the handler a bare "1R" (accepted by IsRunwayDesignator).
        var result = CommandParser.Parse("JARR BDEGA 1R");
        Assert.True(result.IsSuccess, $"parse failed: {result.Reason}");
        var cmd = Assert.IsType<JoinStarCommand>(result.Value);
        Assert.Equal("1R", cmd.RunwayTransition);
    }

    [Fact]
    public void LookupRunwayTransition_SingleDigitRunway_ResolvesSameAsPadded()
    {
        var star = NavigationDatabase.Instance.GetStar("KSFO", "BDEGA4");
        Assert.NotNull(star);

        // Real-data sanity: KSFO BDEGA carries a single-digit (01x) runway transition.
        var padded = NavigationCommandHandler.LookupRunwayTransition(star!.RunwayTransitions, "01R");
        Assert.NotNull(padded);

        // Flown-route path: TryResolveStarFromCifp resolves the runway transition via this exact
        // helper. A single-digit designator must resolve the same transition legs.
        var singleDigit = NavigationCommandHandler.LookupRunwayTransition(star.RunwayTransitions, "1R");
        Assert.NotNull(singleDigit);
        Assert.Equal(padded!.Legs.Count, singleDigit!.Legs.Count);
    }

    [Fact]
    public void GetStarRunwayTransitions_SingleDigitRunway_ResolvesSameAsPadded()
    {
        var navDb = NavigationDatabase.Instance;

        var padded = navDb.GetStarRunwayTransitions("KSFO", "BDEGA4", "01R");
        Assert.NotNull(padded);
        Assert.NotEmpty(padded!);

        var singleDigit = navDb.GetStarRunwayTransitions("KSFO", "BDEGA4", "1R");
        Assert.NotNull(singleDigit);
        Assert.Equal(padded, singleDigit);
    }

    [Fact]
    public void SpawnOnStar_SingleDigitRunway_StoresPaddedDestinationRunway()
    {
        // Scenario "OnStar" spawns write StarRunway (uppercased-but-unpadded by SpawnParser) straight
        // into Procedure.DestinationRunway. A single-digit spawn ("1R") must be zero-padded ("01R") so
        // the flown-route and restriction lookups match the CIFP "RW01R" key (issue #273).
        var navDb = NavigationDatabase.Instance;
        var star = navDb.GetStar("KSFO", "BDEGA4");
        if (star is null || star.CommonLegs.Count == 0 || !star.RunwayTransitions.ContainsKey("RW01R"))
        {
            return; // offline CIFP fallback lacks BDEGA4/RW01R
        }

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(new List<CifpLeg>(star.CommonLegs));
        if (targets.Count == 0 || navDb.GetFixPosition(targets[0].Name) is null)
        {
            return;
        }

        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Ifr,
            Weight = WeightClass.Heavy,
            Engine = EngineKind.Jet,
            PositionType = SpawnPositionType.OnStar,
            StarEntryFix = targets[0].Name,
            StarId = "BDEGA4",
            StarRunway = "1R",
            DescendVia = false,
            StarAltitude = null,
            DestinationAirportId = "KSFO",
        };

        var (state, error) = AircraftGenerator.Generate(
            request,
            "KSFO",
            Array.Empty<AircraftState>(),
            groundLayout: null,
            new Random(1),
            new BeaconCodePool()
        );

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.Equal("01R", state!.Procedure.DestinationRunway);
    }
}
