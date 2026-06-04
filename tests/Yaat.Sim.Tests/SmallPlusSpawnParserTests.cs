using Xunit;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests;

/// <summary>
/// ADD command parsing for the SmallPlus weight tier. The token is "S+" (e.g. ADD I S+ J 28R 10).
/// SmallPlus+Jet and SmallPlus+Turboprop are valid; SmallPlus+Piston is rejected (no such pool).
/// The bare "S" token must still mean Small and must not be greedily matched as SmallPlus.
/// </summary>
public class SmallPlusSpawnParserTests
{
    [Fact]
    public void Parse_SmallPlusJet_IsAllowed()
    {
        var (request, error) = SpawnParser.Parse("I S+ J 28R 10");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(WeightClass.SmallPlus, request!.Weight);
        Assert.Equal(EngineKind.Jet, request.Engine);
    }

    [Fact]
    public void Parse_SmallPlusTurboprop_IsAllowed()
    {
        var (request, error) = SpawnParser.Parse("I S+ T 28R 10");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(WeightClass.SmallPlus, request!.Weight);
        Assert.Equal(EngineKind.Turboprop, request.Engine);
    }

    [Fact]
    public void Parse_SmallPlusPiston_IsRejected()
    {
        var (request, error) = SpawnParser.Parse("I S+ P 28R");

        Assert.Null(request);
        Assert.NotNull(error);
    }

    [Fact]
    public void Parse_SmallToken_StillMeansSmall_NotSmallPlus()
    {
        var (request, error) = SpawnParser.Parse("I S P 28R");

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(WeightClass.Small, request!.Weight);
    }

    [Fact]
    public void Parse_SmallJet_StillRejected()
    {
        // Small + Jet remains invalid (unchanged) — only SmallPlus opens the jet path for small-ish types.
        var (request, error) = SpawnParser.Parse("I S J 28R");

        Assert.Null(request);
        Assert.NotNull(error);
    }
}
