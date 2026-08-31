using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Verifies that CommandDescriber switch expressions cover every ParsedCommand subtype.
/// Prevents silent fallback bugs like UnsupportedCommand being mapped to FlyHeading.
/// </summary>
public class CommandDescriberCompletenessTests(ITestOutputHelper output)
{
    private static readonly Type[] AllParsedCommandTypes = ParsedCommandDummyFactory.AllParsedCommandTypes;

    [Fact]
    public void ToCanonicalType_CoversAllParsedCommandTypes_ExceptUnsupported()
    {
        var missing = new List<string>();

        foreach (var type in AllParsedCommandTypes)
        {
            if (type == typeof(UnsupportedCommand))
            {
                // UnsupportedCommand intentionally throws — it must be caught earlier
                var instance = new UnsupportedCommand("test");
                Assert.Throws<InvalidOperationException>(() => CommandDescriber.ToCanonicalType(instance));
                continue;
            }

            var cmd = CreateDummy(type);
            if (cmd is null)
            {
                output.WriteLine($"SKIP: Cannot construct {type.Name}");
                continue;
            }

            try
            {
                CommandDescriber.ToCanonicalType(cmd);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Unhandled"))
            {
                missing.Add(type.Name);
            }
        }

        if (missing.Count > 0)
        {
            output.WriteLine("Missing from ToCanonicalType:");
            foreach (var name in missing)
            {
                output.WriteLine($"  - {name}");
            }
        }

        Assert.True(missing.Count == 0, $"ToCanonicalType is missing cases for: {string.Join(", ", missing)}");
    }

    [Fact]
    public void DescribeCommand_CoversAllParsedCommandTypes()
    {
        AssertAllTypesCovered(CommandDescriber.DescribeCommand, "DescribeCommand");
    }

    [Fact]
    public void DescribeNatural_CoversAllParsedCommandTypes()
    {
        AssertAllTypesCovered(CommandDescriber.DescribeNatural, "DescribeNatural");
    }

    /// <summary>
    /// Asserts that a describer produces an explicit friendly string for every ParsedCommand
    /// subtype — never the "?" or the record's default <c>ToString()</c> fallback (which leaks
    /// raw text like "DeleteCommand { }" into the command line, see GitHub issue #226).
    /// A subtype that <see cref="CreateDummy"/> cannot construct fails the test loudly rather
    /// than being silently skipped, so the guardrail can't be defeated by an un-dummyable type.
    /// </summary>
    private void AssertAllTypesCovered(Func<ParsedCommand, string> describe, string describerName)
    {
        var uncovered = new List<string>();
        var unconstructible = new List<string>();

        foreach (var type in AllParsedCommandTypes)
        {
            var cmd = CreateDummy(type);
            if (cmd is null)
            {
                unconstructible.Add(type.Name);
                continue;
            }

            var desc = describe(cmd);
            if (desc == "?" || desc == cmd.ToString())
            {
                uncovered.Add(type.Name);
            }
        }

        if (uncovered.Count > 0)
        {
            output.WriteLine($"{describerName} falls back to ToString()/'?' for:");
            foreach (var name in uncovered)
            {
                output.WriteLine($"  - {name}");
            }
        }

        if (unconstructible.Count > 0)
        {
            output.WriteLine("Cannot construct a dummy for (extend MakeDummyArg):");
            foreach (var name in unconstructible)
            {
                output.WriteLine($"  - {name}");
            }
        }

        Assert.True(uncovered.Count == 0, $"{describerName} is missing an arm for: {string.Join(", ", uncovered)}");
        Assert.True(unconstructible.Count == 0, $"CreateDummy cannot build: {string.Join(", ", unconstructible)}");
    }

    private static ParsedCommand? CreateDummy(Type type) => ParsedCommandDummyFactory.CreateDummy(type);
}
