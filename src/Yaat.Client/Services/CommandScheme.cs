using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

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
}
