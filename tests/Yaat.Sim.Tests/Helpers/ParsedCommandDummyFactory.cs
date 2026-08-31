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
    public static readonly Type[] AllParsedCommandTypes = typeof(ParsedCommand)
        .Assembly.GetTypes()
        .Where(t => t.IsSubclassOf(typeof(ParsedCommand)) && !t.IsAbstract)
        .OrderBy(t => t.Name)
        .ToArray();

    /// <summary>
    /// Creates a dummy instance of a ParsedCommand subtype using reflection.
    /// Provides minimal valid constructor arguments. Returns null when no
    /// constructor argument set can be synthesized (callers must fail loudly).
    /// </summary>
    public static ParsedCommand? CreateDummy(Type type)
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
