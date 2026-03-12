namespace Yaat.Sim.Commands;

public enum ArgMode
{
    None,
    Required,
    Optional,
}

public record CommandDefinition(
    CanonicalCommandType Type,
    string Label,
    string Category,
    bool IsGlobal,
    string[] DefaultAliases,
    CommandOverload[] Overloads,
    CompoundModifier[]? CompoundModifiers = null,
    string[]? SyntaxPatterns = null
)
{
    public ArgMode ArgMode => DeriveArgMode();

    public string SampleArg => Overloads.SelectMany(o => o.Parameters).FirstOrDefault(p => !p.IsLiteral)?.TypeHint ?? "";

    private ArgMode DeriveArgMode()
    {
        bool hasBare = Overloads.Any(o => o.Parameters.Length == 0);
        bool hasParams = Overloads.Any(o => o.Parameters.Length > 0);
        if (!hasParams)
        {
            return ArgMode.None;
        }

        return hasBare ? ArgMode.Optional : ArgMode.Required;
    }
}

public record CommandOverload(string? VariantLabel, CommandParameter[] Parameters, string? UsageHint);

public record CompoundModifier(string Keyword, string? ArgHint, bool Repeatable);
