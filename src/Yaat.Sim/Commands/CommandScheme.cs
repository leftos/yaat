namespace Yaat.Sim.Commands;

public class CommandPattern
{
    public required List<string> Aliases { get; set; }

    public string PrimaryVerb => Aliases[0];
}

public class CommandScheme
{
    public required Dictionary<CanonicalCommandType, CommandPattern> Patterns { get; init; }

    public static CommandScheme Default()
    {
        return new CommandScheme
        {
            Patterns = CommandRegistry.All.ToDictionary(kvp => kvp.Key, kvp => new CommandPattern { Aliases = [.. kvp.Value.DefaultAliases] }),
        };
    }

    /// <summary>Whether <paramref name="token"/> is a command verb alias in this scheme (case-insensitive).</summary>
    public bool IsKnownVerb(string token)
    {
        foreach (var pattern in Patterns.Values)
        {
            foreach (var alias in pattern.Aliases)
            {
                if (string.Equals(alias, token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
