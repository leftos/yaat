using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests;

/// <summary>
/// The typed-send splitter resolves a fix the way the server's parse does: given the target's airports, a fix that is
/// only a CIFP terminal waypoint of its destination (CFBMS at KBCT) splits off the command after it.
/// </summary>
public class TypedChainTerminalWaypointTests : IDisposable
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();
    private readonly IDisposable _navDbScope;

    public TypedChainTerminalWaypointTests()
    {
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb!);
    }

    public void Dispose() => _navDbScope.Dispose();

    [Fact]
    public void TypedChain_DctCfbmsThenCm_Splits_ForKbctArrival()
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound(
            "DCT CFBMS CM 50",
            Scheme,
            out ParseFailure? failure,
            new FixResolutionContext("KBCT", "KBWI")
        );

        Assert.Null(failure);
        Assert.NotNull(result);
        string[] commands = result.CanonicalString.Split(',', StringSplitOptions.TrimEntries);
        Assert.Equal(2, commands.Length);
        Assert.Equal("DCT CFBMS", commands[0]);
        Assert.StartsWith("CM ", commands[1]);
    }
}
