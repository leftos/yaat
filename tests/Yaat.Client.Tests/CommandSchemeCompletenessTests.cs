using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class CommandSchemeCompletenessTests
{
    private static readonly CanonicalCommandType[] AllCommandTypes =
        Enum.GetValues<CanonicalCommandType>();

    [Fact]
    public void AtcTrainerScheme_CoversAllCommandTypes()
    {
        var scheme = CommandScheme.AtcTrainer();

        foreach (var type in AllCommandTypes)
        {
            Assert.True(
                scheme.Patterns.ContainsKey(type),
                $"AtcTrainer scheme is missing CanonicalCommandType.{type}");
        }
    }

    [Fact]
    public void ViceScheme_CoversAllCommandTypes()
    {
        var scheme = CommandScheme.Vice();

        foreach (var type in AllCommandTypes)
        {
            Assert.True(
                scheme.Patterns.ContainsKey(type),
                $"VICE scheme is missing CanonicalCommandType.{type}");
        }
    }

    [Fact]
    public void CommandMetadata_CoversAllCommandTypes()
    {
        var metadataTypes = CommandMetadata.AllCommands
            .Select(c => c.Type)
            .ToHashSet();

        foreach (var type in AllCommandTypes)
        {
            Assert.True(
                metadataTypes.Contains(type),
                $"CommandMetadata.AllCommands is missing CanonicalCommandType.{type}");
        }
    }

    [Fact]
    public void CommandMetadata_HasNoDuplicateTypes()
    {
        var types = CommandMetadata.AllCommands.Select(c => c.Type).ToList();
        var duplicates = types
            .GroupBy(t => t)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            $"CommandMetadata has duplicate entries for: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void AllSchemeAliases_AreNonEmpty()
    {
        var schemes = new[]
        {
            ("ATCTrainer", CommandScheme.AtcTrainer()),
            ("VICE", CommandScheme.Vice()),
        };

        foreach (var (name, scheme) in schemes)
        {
            foreach (var (type, pattern) in scheme.Patterns)
            {
                Assert.True(
                    pattern.Aliases.Count > 0,
                    $"{name} scheme has no aliases for CanonicalCommandType.{type}");

                foreach (var alias in pattern.Aliases)
                {
                    Assert.True(
                        !string.IsNullOrWhiteSpace(alias),
                        $"{name} scheme has empty/whitespace alias for CanonicalCommandType.{type}");
                }
            }
        }
    }

    [Fact]
    public void AllSchemeAliases_HaveNoDuplicatesWithinCommand()
    {
        var schemes = new[]
        {
            ("ATCTrainer", CommandScheme.AtcTrainer()),
            ("VICE", CommandScheme.Vice()),
        };

        foreach (var (name, scheme) in schemes)
        {
            foreach (var (type, pattern) in scheme.Patterns)
            {
                var duplicates = pattern.Aliases
                    .GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToList();

                Assert.True(
                    duplicates.Count == 0,
                    $"{name} scheme has duplicate aliases [{string.Join(", ", duplicates)}] within CanonicalCommandType.{type}");
            }
        }
    }

    [Fact]
    public void AtcTrainerAliases_AreUniqueAcrossCommands()
    {
        var scheme = CommandScheme.AtcTrainer();
        var aliasToTypes = new Dictionary<string, List<CanonicalCommandType>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (type, pattern) in scheme.Patterns)
        {
            foreach (var alias in pattern.Aliases)
            {
                if (!aliasToTypes.TryGetValue(alias, out var list))
                {
                    list = [];
                    aliasToTypes[alias] = list;
                }

                list.Add(type);
            }
        }

        var conflicts = aliasToTypes
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => $"'{kv.Key}' used by: {string.Join(", ", kv.Value)}")
            .ToList();

        Assert.True(
            conflicts.Count == 0,
            $"ATCTrainer scheme has aliases shared across commands:\n{string.Join("\n", conflicts)}");
    }

    [Fact]
    public void ViceAliases_AreUniqueAcrossCommands_ExceptKnownShared()
    {
        // VICE scheme intentionally shares: T for RelativeLeft/RelativeRight, H for FlyHeading/FlyPresentHeading
        var knownShared = new HashSet<(CanonicalCommandType, CanonicalCommandType)>
        {
            (CanonicalCommandType.RelativeLeft, CanonicalCommandType.RelativeRight),
            (CanonicalCommandType.FlyHeading, CanonicalCommandType.FlyPresentHeading),
        };

        var scheme = CommandScheme.Vice();
        var aliasToTypes = new Dictionary<string, List<CanonicalCommandType>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (type, pattern) in scheme.Patterns)
        {
            foreach (var alias in pattern.Aliases)
            {
                if (!aliasToTypes.TryGetValue(alias, out var list))
                {
                    list = [];
                    aliasToTypes[alias] = list;
                }

                list.Add(type);
            }
        }

        var conflicts = new List<string>();
        foreach (var (alias, types) in aliasToTypes)
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
                    var pair = (types[i], types[j]);
                    var pairRev = (types[j], types[i]);
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

        Assert.True(
            conflicts.Count == 0,
            $"VICE scheme has unexpected aliases shared across commands:\n{string.Join("\n", conflicts)}");
    }
}
