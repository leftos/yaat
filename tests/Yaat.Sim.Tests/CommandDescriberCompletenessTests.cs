using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// Verifies that CommandDescriber switch expressions cover every ParsedCommand subtype.
/// Prevents silent fallback bugs like UnsupportedCommand being mapped to FlyHeading.
/// </summary>
public class CommandDescriberCompletenessTests(ITestOutputHelper output)
{
    /// <summary>
    /// All concrete (non-abstract) ParsedCommand record types in Yaat.Sim.
    /// </summary>
    private static readonly Type[] AllParsedCommandTypes = typeof(ParsedCommand)
        .Assembly.GetTypes()
        .Where(t => t.IsSubclassOf(typeof(ParsedCommand)) && !t.IsAbstract)
        .OrderBy(t => t.Name)
        .ToArray();

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

    /// <summary>
    /// Creates a dummy instance of a ParsedCommand subtype using reflection.
    /// Provides minimal valid constructor arguments.
    /// </summary>
    private static ParsedCommand? CreateDummy(Type type)
    {
        var ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).FirstOrDefault();
        if (ctor is null)
        {
            return null;
        }

        var args = ctor.GetParameters().Select(p => MakeDummyArg(p.ParameterType)).ToArray();

        try
        {
            return (ParsedCommand)ctor.Invoke(args);
        }
        catch
        {
            return null;
        }
    }

    private static object? MakeDummyArg(Type paramType)
    {
        if (paramType == typeof(string))
        {
            return "TEST";
        }

        if (paramType == typeof(int))
        {
            return 100;
        }

        if (paramType == typeof(uint))
        {
            return 1200u;
        }

        if (paramType == typeof(double))
        {
            return 1.0;
        }

        if (paramType == typeof(bool))
        {
            return false;
        }

        if (paramType == typeof(MagneticHeading))
        {
            return new MagneticHeading(180);
        }

        if (paramType == typeof(TrueHeading))
        {
            return new TrueHeading(180);
        }

        if (paramType == typeof(TurnDirection))
        {
            return TurnDirection.Left;
        }

        if (paramType == typeof(PatternDirection))
        {
            return PatternDirection.Left;
        }

        if (paramType == typeof(SpeedModifier))
        {
            return SpeedModifier.None;
        }

        if (paramType == typeof(DepartureInstruction))
        {
            return new DefaultDeparture();
        }

        if (paramType == typeof(CrossFixAltitudeType))
        {
            return CrossFixAltitudeType.At;
        }

        if (paramType.IsGenericType && paramType.GetGenericTypeDefinition() == typeof(List<>))
        {
            return Activator.CreateInstance(paramType);
        }

        if (paramType.IsGenericType && paramType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            // Return an empty concrete List<T> — the record will see it as IReadOnlyList<T>.
            var listType = typeof(List<>).MakeGenericType(paramType.GetGenericArguments()[0]);
            return Activator.CreateInstance(listType);
        }

        if (paramType.IsGenericType && paramType.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            return null;
        }

        if (Nullable.GetUnderlyingType(paramType) is not null)
        {
            return null;
        }

        if (!paramType.IsValueType)
        {
            return null;
        }

        return Activator.CreateInstance(paramType);
    }
}
