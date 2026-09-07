namespace Yaat.Sim.Commands.Arguments;

/// <summary>
/// One resolved positional argument: the type that claimed it, the token exactly as typed (what
/// canonical text renders, so an AGL altitude survives the round trip), and the parsed payload —
/// <see cref="string"/> for a runway, <see cref="int"/> for an altitude in feet.
/// </summary>
public readonly record struct CommandArgumentValue(CommandArgumentType Type, string Token, object? Value);

/// <summary>
/// The outcome of matching a token list against a command's overload shapes: the resolved arguments,
/// or — when a token fits no viable overload — the failure tail the calling verb prefixes with its own
/// name. <see cref="Failure"/> is null exactly when the resolution succeeded.
/// </summary>
public sealed record CommandArgumentResolution(IReadOnlyList<CommandArgumentValue> Values, string? Failure)
{
    /// <summary>True when an argument of <paramref name="type"/> was given.</summary>
    public bool Has(CommandArgumentType type) => Values.Any(value => value.Type == type);

    /// <summary>
    /// The payload of the first argument of <paramref name="type"/>, or <c>default</c> when there is
    /// none (or its payload is not a <typeparamref name="T"/>). Pair with <see cref="Has"/> when the
    /// payload is a value type and its default is a legal value.
    /// </summary>
    public T? ValueOf<T>(CommandArgumentType type)
        where T : notnull
    {
        foreach (var argument in Values)
        {
            if ((argument.Type == type) && (argument.Value is T typed))
            {
                return typed;
            }
        }

        return default;
    }

    /// <summary>The token as typed for the first argument of <paramref name="type"/>, or null.</summary>
    public string? TokenOf(CommandArgumentType type)
    {
        foreach (var argument in Values)
        {
            if (argument.Type == type)
            {
                return argument.Token;
            }
        }

        return null;
    }
}

/// <summary>
/// Resolves a command's argument tokens against its declared overload shapes the way a compiler
/// resolves an overload set: every shape starts viable, each token is matched against the argument
/// types the still-viable shapes declare at that position, and the shapes that disagree with the type
/// that claimed it drop out. The first token no viable overload accepts is a parse failure, named with
/// what was expected at that position.
///
/// <para>Where more than one type is a candidate at a position, <see cref="BindingPrecedence"/> decides
/// — the disambiguation lives here, in the position, not in the validators. Runway before Altitude is
/// what makes <c>MLT 15</c> runway 15 while <c>MLT 28R 15</c>, whose second slot only an altitude can
/// fill, is 1,500 ft.</para>
///
/// <para>Step 1 of the typed-command-argument migration (docs/plans/typed-command-arguments.md): the
/// pattern modifier's <c>[runway] [altitude]</c> tail is the first grammar to use it, and other
/// runway-or-altitude slots move onto it as they are audited.</para>
/// </summary>
public static class CommandArgumentResolver
{
    /// <summary>
    /// The order in which competing types claim a token at one position. A type absent from this list
    /// is tried last.
    /// </summary>
    private static readonly CommandArgumentType[] BindingPrecedence = [CommandArgumentType.Runway, CommandArgumentType.Altitude];

    /// <summary>
    /// Matches <paramref name="tokens"/> against <paramref name="shapes"/> (each shape is one
    /// overload's positional argument types, e.g. <c>[]</c>, <c>[Runway]</c>, <c>[Runway, Altitude]</c>).
    /// </summary>
    public static CommandArgumentResolution Resolve(IReadOnlyList<string> tokens, IReadOnlyList<CommandArgumentType[]> shapes)
    {
        var viable = shapes.ToList();
        var values = new List<CommandArgumentValue>();

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var candidates = viable.Where(shape => shape.Length > i).Select(shape => shape[i]).Distinct().OrderBy(PrecedenceOf).ToList();
            if (candidates.Count == 0)
            {
                return Failed(values, token, i, "no more arguments are expected");
            }

            var matched = candidates.Select(type => TryParse(type, token)).FirstOrDefault(value => value is not null);
            if (matched is not { } value)
            {
                return Failed(values, token, i, string.Join(", or ", candidates.Select(Expectation)));
            }

            values.Add(value);
            viable.RemoveAll(shape => (shape.Length <= i) || (shape[i] != value.Type));
        }

        if (!viable.Exists(shape => shape.Length == tokens.Count))
        {
            var missing = viable.Where(shape => shape.Length > tokens.Count).Select(shape => shape[tokens.Count]).Distinct();
            return new CommandArgumentResolution(values, $"needs {string.Join(", or ", missing.Select(Expectation))}");
        }

        return new CommandArgumentResolution(values, null);
    }

    private static int PrecedenceOf(CommandArgumentType type)
    {
        int index = Array.IndexOf(BindingPrecedence, type);
        return index < 0 ? int.MaxValue : index;
    }

    private static CommandArgumentValue? TryParse(CommandArgumentType type, string token) =>
        type switch
        {
            CommandArgumentType.Runway => RunwayArgument.TryParse(token) is { } runway ? new CommandArgumentValue(type, token, runway) : null,
            CommandArgumentType.Altitude => AltitudeArgument.TryParse(token) is { } feet ? new CommandArgumentValue(type, token, feet) : null,
            _ => null,
        };

    /// <summary>
    /// The failure tail: the token that stopped the parse, the argument it followed (so a second
    /// runway in <c>MLT 28R 28L</c> reads "after runway 28R"), and what was expected in its place.
    /// </summary>
    private static CommandArgumentResolution Failed(List<CommandArgumentValue> values, string token, int index, string expected)
    {
        string after = index > 0 ? $" after {Describe(values[index - 1])}" : "";
        return new CommandArgumentResolution(values, $"does not understand '{token}'{after}: {expected}");
    }

    private static string Describe(CommandArgumentValue value) =>
        value.Type switch
        {
            CommandArgumentType.Runway => $"runway {value.Token}",
            CommandArgumentType.Altitude => $"altitude {value.Token}",
            _ => value.Token,
        };

    private static string Expectation(CommandArgumentType type) =>
        type switch
        {
            CommandArgumentType.Runway => "a runway is one or two digits with an optional L/C/R suffix, e.g. 28R",
            CommandArgumentType.Altitude => "an altitude is hundreds of feet (015), feet (1500), or an AGL form (KOAK+005)",
            _ => "an argument of an unknown type",
        };
}
