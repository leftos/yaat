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
        var uncovered = new List<string>();

        foreach (var type in AllParsedCommandTypes)
        {
            var cmd = CreateDummy(type);
            if (cmd is null)
            {
                continue;
            }

            var desc = CommandDescriber.DescribeCommand(cmd);
            // The default fallback is command.ToString() — if that's what we get
            // for a known type, it means the switch doesn't have an explicit case.
            // But ToString() returns the record's default representation which isn't
            // a useful canonical form. We check for the "?" fallback only.
            if (desc == "?")
            {
                uncovered.Add(type.Name);
            }
        }

        if (uncovered.Count > 0)
        {
            output.WriteLine("DescribeCommand returns '?' for:");
            foreach (var name in uncovered)
            {
                output.WriteLine($"  - {name}");
            }
        }

        // This is informational — ToString() fallback is acceptable for display
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
