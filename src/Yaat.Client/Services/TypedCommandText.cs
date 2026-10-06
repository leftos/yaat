using System.Diagnostics.CodeAnalysis;
using Yaat.Sim.Commands;

namespace Yaat.Client.Services;

/// <summary>
/// The preprocessing command text the controller authored gets before it is sent: its macros expanded
/// (<see cref="MacroExpander"/>), then put in canonical verbs under the controller's command scheme
/// (<see cref="CommandSchemeParser.ParseCompound(string, CommandScheme)"/>). Typed input, custom quick commands and the
/// Quick Commands editor's validation share it, so what the editor accepts is what a click sends.
/// </summary>
public static class TypedCommandText
{
    /// <summary>The problem <see cref="TryPrepare"/> reports for text that does not parse under the scheme.</summary>
    public const string UnrecognizedCommand = "Unrecognized command";

    /// <summary>Expands the macros <paramref name="text"/> names.</summary>
    /// <param name="text">The command text as the controller wrote it.</param>
    /// <param name="macros">The controller's macros.</param>
    /// <param name="expanded">The expansion; <paramref name="text"/> itself when it names no macro or a macro fails.</param>
    /// <param name="error">Why a macro failed to expand (an unknown name, a missing argument); null on success.</param>
    /// <returns>False when a macro failed to expand.</returns>
    public static bool TryExpandMacros(
        string text,
        IReadOnlyList<MacroDefinition> macros,
        out string expanded,
        [NotNullWhen(false)] out string? error
    )
    {
        string? expansion = MacroExpander.TryExpand(text, macros, out error);
        expanded = expansion ?? text;
        return error is null;
    }

    /// <summary>Expands the macros <paramref name="text"/> names, then puts the result in canonical verbs.</summary>
    /// <param name="text">The command text as the controller wrote it; surrounding blanks are ignored.</param>
    /// <param name="macros">The controller's macros.</param>
    /// <param name="scheme">The controller's command verbs.</param>
    /// <param name="prepared">
    /// The canonical command; on failure, as far as it got: the trimmed text when a macro failed, the expansion when it
    /// does not parse.
    /// </param>
    /// <param name="problem">The macro error, or <see cref="UnrecognizedCommand"/>; null on success.</param>
    /// <returns>False when a macro failed to expand or the expansion does not parse.</returns>
    public static bool TryPrepare(
        string text,
        IReadOnlyList<MacroDefinition> macros,
        CommandScheme scheme,
        out string prepared,
        [NotNullWhen(false)] out string? problem
    )
    {
        if (!TryExpandMacros(text.Trim(), macros, out prepared, out problem))
        {
            return false;
        }

        if (CommandSchemeParser.ParseCompound(prepared, scheme) is not { } compound)
        {
            problem = UnrecognizedCommand;
            return false;
        }

        prepared = compound.CanonicalString;
        return true;
    }
}
