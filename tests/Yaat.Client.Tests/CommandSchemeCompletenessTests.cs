using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class CommandSchemeCompletenessTests
{
    private static readonly CanonicalCommandType[] AllCommandTypes = Enum.GetValues<CanonicalCommandType>();

    [Fact]
    public void Registry_CoversAllCommandTypes()
    {
        foreach (CanonicalCommandType type in AllCommandTypes)
        {
            Assert.True(CommandRegistry.All.ContainsKey(type), $"CommandRegistry is missing CanonicalCommandType.{type}");
        }
    }

    [Fact]
    public void Registry_HasNoDuplicateTypes()
    {
        // Dictionary enforces uniqueness, but verify the build data doesn't silently overwrite
        int count = CommandRegistry.All.Count;
        Assert.Equal(AllCommandTypes.Length, count);
    }

    [Fact]
    public void DefaultScheme_CoversAllCommandTypes()
    {
        var scheme = CommandScheme.Default();

        foreach (CanonicalCommandType type in AllCommandTypes)
        {
            Assert.True(scheme.Patterns.ContainsKey(type), $"Default scheme is missing CanonicalCommandType.{type}");
        }
    }

    [Fact]
    public void DefaultSchemeAliases_AreNonEmpty()
    {
        var scheme = CommandScheme.Default();

        foreach ((CanonicalCommandType type, CommandPattern? pattern) in scheme.Patterns)
        {
            Assert.True(pattern.Aliases.Count > 0, $"Default scheme has no aliases for CanonicalCommandType.{type}");

            foreach (string alias in pattern.Aliases)
            {
                Assert.True(!string.IsNullOrWhiteSpace(alias), $"Default scheme has empty/whitespace alias for CanonicalCommandType.{type}");
            }
        }
    }

    [Fact]
    public void DefaultSchemeAliases_HaveNoDuplicatesWithinCommand()
    {
        var scheme = CommandScheme.Default();

        foreach ((CanonicalCommandType type, CommandPattern? pattern) in scheme.Patterns)
        {
            var duplicates = pattern.Aliases.GroupBy(a => a, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

            Assert.True(
                duplicates.Count == 0,
                $"Default scheme has duplicate aliases [{string.Join(", ", duplicates)}] within CanonicalCommandType.{type}"
            );
        }
    }

    [Fact]
    public void DefaultSchemeAliases_AreUniqueAcrossCommands_ExceptKnownShared()
    {
        // Unified scheme intentionally shares:
        // H for FlyHeading/FlyPresentHeading
        var knownShared = new HashSet<(CanonicalCommandType, CanonicalCommandType)>
        {
            (CanonicalCommandType.FlyHeading, CanonicalCommandType.FlyPresentHeading),
        };

        var scheme = CommandScheme.Default();
        var aliasToTypes = new Dictionary<string, List<CanonicalCommandType>>(StringComparer.OrdinalIgnoreCase);

        foreach ((CanonicalCommandType type, CommandPattern? pattern) in scheme.Patterns)
        {
            foreach (string alias in pattern.Aliases)
            {
                if (!aliasToTypes.TryGetValue(alias, out List<CanonicalCommandType>? list))
                {
                    list = [];
                    aliasToTypes[alias] = list;
                }

                list.Add(type);
            }
        }

        var conflicts = new List<string>();
        foreach ((string? alias, List<CanonicalCommandType>? types) in aliasToTypes)
        {
            if (types.Count <= 1)
            {
                continue;
            }

            // Check if all pairs in this group are known-shared
            bool allKnown = true;
            for (int i = 0; i < types.Count && allKnown; i++)
            {
                for (int j = i + 1; j < types.Count && allKnown; j++)
                {
                    (CanonicalCommandType, CanonicalCommandType) pair = (types[i], types[j]);
                    (CanonicalCommandType, CanonicalCommandType) pairRev = (types[j], types[i]);
                    if (!knownShared.Contains(pair) && !knownShared.Contains(pairRev))
                    {
                        allKnown = false;
                    }
                }
            }

            if (!allKnown)
            {
                conflicts.Add($"'{alias}' used by: {string.Join(", ", types)}");
            }
        }

        Assert.True(conflicts.Count == 0, $"Default scheme has unexpected aliases shared across commands:\n{string.Join("\n", conflicts)}");
    }
}
