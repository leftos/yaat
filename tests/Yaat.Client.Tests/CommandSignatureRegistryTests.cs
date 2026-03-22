using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class CommandRegistryTests
{
    [Fact]
    public void Registry_CoversAllCommandTypes()
    {
        foreach (var type in Enum.GetValues<CanonicalCommandType>())
        {
            Assert.True(CommandRegistry.All.ContainsKey(type), $"CommandRegistry is missing CanonicalCommandType.{type}");
        }
    }

    [Fact]
    public void AllEntries_HaveAtLeastOneAlias()
    {
        foreach (var (type, def) in CommandRegistry.All)
        {
            Assert.True(def.DefaultAliases.Length > 0, $"{type} has no default aliases");

            foreach (var alias in def.DefaultAliases)
            {
                Assert.False(string.IsNullOrWhiteSpace(alias), $"{type} has empty/whitespace alias");
            }
        }
    }

    [Fact]
    public void AllEntries_HaveAtLeastOneOverload()
    {
        foreach (var (type, def) in CommandRegistry.All)
        {
            Assert.True(def.Overloads.Length > 0, $"{type} has no overloads");
        }
    }

    [Fact]
    public void AllEntries_HaveNonEmptyLabel()
    {
        foreach (var (type, def) in CommandRegistry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(def.Label), $"{type} has empty label");
        }
    }

    [Fact]
    public void AllEntries_HaveNonEmptyCategory()
    {
        foreach (var (type, def) in CommandRegistry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(def.Category), $"{type} has empty category");
        }
    }

    [Fact]
    public void ClearedForTakeoff_HasMultipleOverloads()
    {
        var def = CommandRegistry.Get(CanonicalCommandType.ClearedForTakeoff);
        Assert.NotNull(def);
        Assert.True(def.Overloads.Length > 5, $"CTO should have many overloads, got {def.Overloads.Length}");
    }

    [Fact]
    public void GoAround_HasThreeOverloads()
    {
        var def = CommandRegistry.Get(CanonicalCommandType.GoAround);
        Assert.NotNull(def);
        Assert.Equal(3, def.Overloads.Length);
        Assert.Empty(def.Overloads[0].Parameters);
        Assert.Single(def.Overloads[1].Parameters);
        Assert.Equal(2, def.Overloads[2].Parameters.Length);
    }

    [Fact]
    public void BareCommands_HaveArgModeNone()
    {
        Assert.Equal(ArgMode.None, CommandRegistry.Get(CanonicalCommandType.Delete)!.ArgMode);
        Assert.Equal(ArgMode.None, CommandRegistry.Get(CanonicalCommandType.FlyPresentHeading)!.ArgMode);
        Assert.Equal(ArgMode.None, CommandRegistry.Get(CanonicalCommandType.ResumeNormalSpeed)!.ArgMode);
    }

    [Fact]
    public void RequiredArgCommands_HaveArgModeRequired()
    {
        Assert.Equal(ArgMode.Required, CommandRegistry.Get(CanonicalCommandType.FlyHeading)!.ArgMode);
        Assert.Equal(ArgMode.Required, CommandRegistry.Get(CanonicalCommandType.ClimbMaintain)!.ArgMode);
        Assert.Equal(ArgMode.Required, CommandRegistry.Get(CanonicalCommandType.Taxi)!.ArgMode);
    }

    [Fact]
    public void OptionalArgCommands_HaveArgModeOptional()
    {
        Assert.Equal(ArgMode.Optional, CommandRegistry.Get(CanonicalCommandType.Expedite)!.ArgMode);
        Assert.Equal(ArgMode.Optional, CommandRegistry.Get(CanonicalCommandType.Squawk)!.ArgMode);
        Assert.Equal(ArgMode.Optional, CommandRegistry.Get(CanonicalCommandType.ClearedForTakeoff)!.ArgMode);
        Assert.Equal(ArgMode.Optional, CommandRegistry.Get(CanonicalCommandType.GoAround)!.ArgMode);
    }

    [Fact]
    public void FromDefinition_ProducesCorrectSignatureSet()
    {
        var def = CommandRegistry.Get(CanonicalCommandType.FlyHeading)!;
        var sigSet = CommandSignatureSet.FromDefinition(def, ["FH", "H"]);

        Assert.Single(sigSet.Signatures);
        Assert.Equal("Fly Heading", sigSet.Signatures[0].Label);
        Assert.Equal(2, sigSet.Signatures[0].Aliases.Count);
        Assert.Equal("FH", sigSet.Signatures[0].Aliases[0]);
        Assert.Single(sigSet.Signatures[0].Parameters);
        Assert.Equal("heading", sigSet.Signatures[0].Parameters[0].Name);
    }

    [Fact]
    public void FromDefinition_MultiOverload_IncludesVariantInLabel()
    {
        var def = CommandRegistry.Get(CanonicalCommandType.ClearedForTakeoff)!;
        var sigSet = CommandSignatureSet.FromDefinition(def, ["CTO"]);

        // First overload has no variant label
        Assert.Equal("Cleared for Takeoff", sigSet.Signatures[0].Label);

        // Second overload has variant "RH"
        Assert.Equal("Cleared for Takeoff — RH", sigSet.Signatures[1].Label);
    }

    [Fact]
    public void CompoundModifiers_PresentOnTaxi()
    {
        var def = CommandRegistry.Get(CanonicalCommandType.Taxi);
        Assert.NotNull(def);
        Assert.NotNull(def.CompoundModifiers);
        Assert.True(def.CompoundModifiers.Length > 0);
        Assert.Contains(def.CompoundModifiers, m => m.Keyword == "RWY");
        Assert.Contains(def.CompoundModifiers, m => m.Keyword == "HS" && m.Repeatable);
        Assert.Contains(def.CompoundModifiers, m => m.Keyword == "NODEL" && m.ArgHint is null);
    }

    [Fact]
    public void DefaultScheme_MatchesRegistryCount()
    {
        var scheme = CommandScheme.Default();
        Assert.Equal(CommandRegistry.All.Count, scheme.Patterns.Count);

        foreach (var type in Enum.GetValues<CanonicalCommandType>())
        {
            Assert.True(scheme.Patterns.ContainsKey(type), $"Default scheme is missing {type}");
        }
    }
}
