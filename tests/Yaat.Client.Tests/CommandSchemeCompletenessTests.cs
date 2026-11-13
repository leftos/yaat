using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class CommandSchemeCompletenessTests
{
    private static readonly CanonicalCommandType[] AllCommandTypes = Enum.GetValues<CanonicalCommandType>();

    /// <summary>
    /// Alias pairs the scheme and registry uniqueness checks accept as shared. Empty today: no default alias
    /// maps to two commands.
    /// </summary>
    private static readonly HashSet<(CanonicalCommandType, CanonicalCommandType)> KnownSharedAliases = [];

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

        List<string> conflicts =
        [
            .. aliasToTypes
                .Where(kvp => !AllPairsKnownShared(kvp.Value, KnownSharedAliases))
                .Select(kvp => $"'{kvp.Key}' used by: {string.Join(", ", kvp.Value)}"),
        ];

        Assert.True(conflicts.Count == 0, $"Default scheme has unexpected aliases shared across commands:\n{string.Join("\n", conflicts)}");
    }

    [Fact]
    public void RegistryDefaultAliases_AreUniqueAcrossCommands_ExceptKnownShared()
    {
        // CommandRegistry.BuildAliasToCanonicType uses TryAdd, so a duplicate alias silently resolves to the
        // first command registered; this catches that collision before it shadows a command. Each definition's
        // enum name is checked too, so the name CommandRegistry.RenderSignature falls back to stays
        // unambiguous: it is not a parser alias. The allowlist is KnownSharedAliases, shared with the scheme
        // check above.
        var aliasToTypes = new Dictionary<string, HashSet<CanonicalCommandType>>(StringComparer.OrdinalIgnoreCase);
        foreach (CommandDefinition def in CommandRegistry.All.Values)
        {
            foreach (string alias in def.DefaultAliases.Append(def.Type.ToString()))
            {
                if (!aliasToTypes.TryGetValue(alias, out HashSet<CanonicalCommandType>? types))
                {
                    types = [];
                    aliasToTypes[alias] = types;
                }

                types.Add(def.Type);
            }
        }

        var conflicts = new List<string>();
        foreach ((string alias, HashSet<CanonicalCommandType> types) in aliasToTypes)
        {
            if (!AllPairsKnownShared(types, KnownSharedAliases))
            {
                conflicts.Add($"'{alias}' used by: {string.Join(", ", types)}");
            }
        }

        Assert.True(conflicts.Count == 0, $"Registry has unexpected aliases shared across commands:\n{string.Join("\n", conflicts)}");
    }

    private static bool AllPairsKnownShared(
        IReadOnlyCollection<CanonicalCommandType> types,
        HashSet<(CanonicalCommandType, CanonicalCommandType)> knownShared
    )
    {
        if (types.Count <= 1)
        {
            return true;
        }

        CanonicalCommandType[] ordered = [.. types];
        for (int i = 0; i < ordered.Length; i++)
        {
            for (int j = i + 1; j < ordered.Length; j++)
            {
                if (!knownShared.Contains((ordered[i], ordered[j])) && !knownShared.Contains((ordered[j], ordered[i])))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
