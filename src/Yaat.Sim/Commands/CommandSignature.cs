namespace Yaat.Sim.Commands;

public record CommandParameter(string Name, string TypeHint, bool IsOptional, bool IsLiteral = false);

public record CommandSignature(
    CanonicalCommandType Type,
    string Label,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<CommandParameter> Parameters,
    string? UsageHint
);

public record CommandSignatureSet(IReadOnlyList<CommandSignature> Signatures)
{
    public static CommandSignatureSet FromDefinition(CommandDefinition def, IReadOnlyList<string> aliases)
    {
        var sigs = def
            .Overloads.Select(o => new CommandSignature(
                def.Type,
                o.VariantLabel is not null ? $"{def.Label} — {o.VariantLabel}" : def.Label,
                aliases,
                o.Parameters,
                o.UsageHint
            ))
            .ToArray();
        return new CommandSignatureSet(sigs);
    }
}
