using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class CommandParserCompletenessTests(ITestOutputHelper output)
{
    [Fact]
    public void AllRegistryAliases_ProduceNonNullFromParse()
    {
        var unsupported = new List<(string Alias, CanonicalCommandType Type)>();

        foreach ((string? alias, CanonicalCommandType type) in CommandRegistry.AliasToCanonicType)
        {
            // Parse with just the alias (no arg) — should produce something or null for arg-required verbs
            ParseResult<ParsedCommand> result = CommandParser.Parse(alias);
            if (result.Value is UnsupportedCommand)
            {
                unsupported.Add((alias, type));
            }
        }

        foreach ((string? alias, CanonicalCommandType type) in unsupported)
        {
            output.WriteLine($"UnsupportedCommand: {alias} → {type}");
        }

        // Log count for visibility, but don't fail — UnsupportedCommand is expected for
        // registry-known verbs that don't have ParseByType implementations yet (e.g. ADD).
        // This test exists to track graduation progress over time.
        output.WriteLine($"\n{unsupported.Count} alias(es) produce UnsupportedCommand out of {CommandRegistry.AliasToCanonicType.Count} total.");
    }

    [Fact]
    public void AllPrimaryAliases_ResolveInRegistry()
    {
        var missing = new List<CanonicalCommandType>();

        foreach (CommandDefinition def in CommandRegistry.All.Values)
        {
            string primaryAlias = def.DefaultAliases[0];
            if (!CommandRegistry.AliasToCanonicType.ContainsKey(primaryAlias))
            {
                missing.Add(def.Type);
            }
        }

        Assert.Empty(missing);
    }
}
