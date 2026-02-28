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
    public void AllSchemeVerbs_AreNonEmpty()
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
                    !string.IsNullOrWhiteSpace(pattern.Verb),
                    $"{name} scheme has empty verb for CanonicalCommandType.{type}");
            }
        }
    }
}
