using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

/// <summary>
/// Immutable result of parsing the current command input text.
/// Produced once per keystroke, consumed by both autocomplete and signature help.
/// </summary>
public record CommandInputParseResult(
    string CurrentFragment,
    string? ConditionVerb,
    string StrippedFragment,
    string[] Tokens,
    int VerbIndex,
    string? Verb,
    CanonicalCommandType? CommandType,
    CommandDefinition? Definition,
    IReadOnlyList<string> Aliases,
    int ParameterIndex,
    string[] TypedArgs,
    bool HasTrailingSpace
);
