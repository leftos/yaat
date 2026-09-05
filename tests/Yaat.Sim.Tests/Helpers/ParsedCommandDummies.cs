using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>
/// Builds a throwaway instance of every concrete <see cref="ParsedCommand"/> subtype, so a sweep can push each
/// command *type* through a routing table without hand-writing an argument list per verb. The values are
/// placeholders ("TEST", 100, true, the first enum member); they exist so the constructor runs, not so the
/// command means anything. An arm that runs and chokes on them is still an arm.
/// </summary>
public static class ParsedCommandDummies
{
    /// <summary>Every non-abstract <see cref="ParsedCommand"/> subtype in Yaat.Sim, in a stable order.</summary>
    public static IReadOnlyList<Type> ConcreteTypes() =>
        [
            .. typeof(ParsedCommand)
                .Assembly.GetTypes()
                .Where(t => t.IsSubclassOf(typeof(ParsedCommand)) && !t.IsAbstract)
                .OrderBy(t => t.FullName, StringComparer.Ordinal),
        ];

    /// <summary>A dummy instance, or null when no constructor could be satisfied with placeholder arguments.</summary>
    public static ParsedCommand? Create(Type type)
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
        catch (Exception ex) when (ex is System.Reflection.TargetInvocationException or ArgumentException)
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

        if (paramType == typeof(double))
        {
            return 100.0;
        }

        if (paramType == typeof(bool))
        {
            return true;
        }

        if (paramType.IsEnum)
        {
            return Enum.GetValues(paramType).GetValue(0);
        }

        if (paramType.IsGenericType && paramType.GetGenericTypeDefinition() == typeof(List<>))
        {
            return Activator.CreateInstance(paramType);
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
