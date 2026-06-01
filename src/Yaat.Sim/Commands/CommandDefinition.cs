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
    public bool ProducesPilotUnable { get; init; }

    public ArgMode ArgMode => DeriveArgMode();

    public string SampleArg => Overloads.SelectMany(o => o.Parameters).FirstOrDefault(p => !p.IsLiteral)?.TypeHint ?? "";

    private ArgMode DeriveArgMode()
    {
        bool hasBare = Overloads.Any(o => o.Parameters.Length == 0);
        bool hasParams = Overloads.Any(o => o.Parameters.Length > 0);
        // Compound modifiers (e.g. RES CROSS <rwy>, CLAND NODEL) are optional arguments too —
        // a verb whose only overload is bare but that carries modifiers still accepts input.
        // Without this, the client parser rejects "RES CROSS 28L" with "does not accept
        // arguments", which then prevents the callsign prefix from being stripped.
        bool hasModifiers = CompoundModifiers is { Length: > 0 };
        if (!hasParams && !hasModifiers)
        {
            return ArgMode.None;
        }

        if (!hasParams)
        {
            // Modifier-only verb: the bare form is always valid, so the argument is optional.
            return ArgMode.Optional;
        }

        return hasBare ? ArgMode.Optional : ArgMode.Required;
    }
}

public record CommandOverload(string? VariantLabel, CommandParameter[] Parameters, string? UsageHint);

public record CompoundModifier(string Keyword, string? ArgHint, bool Repeatable);
