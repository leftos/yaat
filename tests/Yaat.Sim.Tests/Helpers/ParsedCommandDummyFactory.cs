using System.Reflection;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Builds minimal dummy instances of <see cref="ParsedCommand"/> subtypes via reflection so
/// completeness tests can sweep every concrete command type through a switch under test.
/// Shared by <c>CommandDescriberCompletenessTests</c> and <c>ClassifyCommandCompletenessTests</c>.
/// </summary>
internal static class ParsedCommandDummyFactory
{
    /// <summary>
    /// All concrete (non-abstract) ParsedCommand record types in Yaat.Sim, name-ordered.
    /// </summary>
    public static readonly Type[] AllParsedCommandTypes =
    [
        .. typeof(ParsedCommand).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(ParsedCommand)) && !t.IsAbstract).OrderBy(t => t.Name),
    ];

    /// <summary>
    /// Creates a dummy instance of a ParsedCommand subtype using reflection.
    /// Provides minimal valid constructor arguments. Returns null when no
    /// constructor argument set can be synthesized (callers must fail loudly).
    /// </summary>
    public static ParsedCommand? CreateDummy(Type type) => CreateDummy(type, depth: 0);

    /// <summary>Deepest nesting of a command built as another's argument; a record holding its own type stops there.</summary>
    private const int MaxNestedCommandDepth = 4;

    private static ParsedCommand? CreateDummy(Type type, int depth)
    {
        if (depth > MaxNestedCommandDepth)
        {
            return null;
        }

        ConstructorInfo? ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).FirstOrDefault();
        if (ctor is null)
        {
            return null;
        }

        object?[] args = [.. ctor.GetParameters().Select(p => MakeDummyArg(p.ParameterType, depth))];

        try
        {
            return (ParsedCommand)ctor.Invoke(args);
        }
        catch
        {
            return null;
        }
    }

    private static object? MakeDummyArg(Type paramType, int depth)
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

        // A command that wraps another (RTAXI's typed TAXI) gets a dummy of that command, not null.
        if (typeof(ParsedCommand).IsAssignableFrom(paramType) && !paramType.IsAbstract)
        {
            return CreateDummy(paramType, depth + 1);
        }

        if (paramType == typeof(PlannedAltitude))
        {
            return PlannedAltitude.Ifr(10000);
        }

        if (paramType.IsGenericType && paramType.GetGenericTypeDefinition() == typeof(List<>))
        {
            return Activator.CreateInstance(paramType);
        }

        if (paramType.IsGenericType && paramType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            // Return an empty concrete List<T> — the record will see it as IReadOnlyList<T>.
            Type listType = typeof(List<>).MakeGenericType(paramType.GetGenericArguments()[0]);
            return Activator.CreateInstance(listType);
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
