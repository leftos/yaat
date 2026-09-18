using System.Text;
using System.Text.RegularExpressions;

namespace Yaat.Client.Services;

public static partial class MacroExpander
{
    private static readonly Regex ParamSlotRegex = GetParamSlotRegex();

    private const int MaxExpansionDepth = 20;

    /// <summary>
    /// Expands macro references (!NAME args...) in commandText.
    /// Expands recursively until no macros remain or the result stabilizes.
    /// Returns the expanded string, or null if no macros were found.
    /// Sets error if a macro is referenced but args are missing or name is unknown.
    /// </summary>
    public static string? TryExpand(string commandText, IReadOnlyList<MacroDefinition> macros, out string? error)
    {
        error = null;

        string current = commandText;
        bool everExpanded = false;

        for (int depth = 0; depth < MaxExpansionDepth; depth++)
        {
            string? result = ExpandOnce(current, macros, out error);
            if (error is not null)
            {
                return null;
            }

            if (result is null || result == current)
            {
                break;
            }

            current = result;
            everExpanded = true;
        }

        return everExpanded ? current : null;
    }

    private static string? ExpandOnce(string commandText, IReadOnlyList<MacroDefinition> macros, out string? error)
    {
        error = null;

        if (!commandText.Contains('!'))
        {
            return null;
        }

        var result = new StringBuilder(commandText.Length * 2);
        int i = 0;
        bool expanded = false;

        while (i < commandText.Length)
        {
            if (commandText[i] == '!' && IsMacroBoundary(commandText, i))
            {
                int nameStart = i + 1;
                int nameEnd = nameStart;
                while (nameEnd < commandText.Length && !IsSeparator(commandText[nameEnd]))
                {
                    nameEnd++;
                }

                string name = commandText[nameStart..nameEnd];
                MacroDefinition? macro = FindMacro(name, macros);

                if (macro is null)
                {
                    error = $"Unknown macro \"!{name}\"";
                    return null;
                }

                if (macro.HasExplicitParameters)
                {
                    string? validationError = macro.Validate();
                    if (validationError is not null)
                    {
                        error = $"Macro \"!{macro.BaseName}\": {validationError}";
                        return null;
                    }
                }

                IReadOnlyList<string> paramNames = macro.ParameterNames;
                var args = new List<string>(paramNames.Count);
                int pos = nameEnd;

                for (int p = 0; p < paramNames.Count; p++)
                {
                    while (pos < commandText.Length && commandText[pos] == ' ')
                    {
                        pos++;
                    }

                    if (pos >= commandText.Length || commandText[pos] is ';' or ',')
                    {
                        string hint = paramNames.Count > 0 ? $" ({string.Join(", ", paramNames.Select(n => $"&{n}"))})" : "";
                        error = $"Macro \"!{macro.BaseName}\" expects {paramNames.Count} parameter(s), got {p}{hint}";
                        return null;
                    }

                    int argStart = pos;
                    while (pos < commandText.Length && commandText[pos] != ' ' && commandText[pos] != ';' && commandText[pos] != ',')
                    {
                        pos++;
                    }

                    args.Add(commandText[argStart..pos]);
                }

                string expansion = SubstituteParams(macro.Expansion, paramNames, args);
                result.Append(expansion);
                i = pos;
                expanded = true;
            }
            else
            {
                result.Append(commandText[i]);
                i++;
            }
        }

        return expanded ? result.ToString() : null;
    }

    private static bool IsMacroBoundary(string text, int bangIndex)
    {
        if (bangIndex == 0)
        {
            return true;
        }

        char prev = text[bangIndex - 1];
        return prev is ' ' or ';' or ',';
    }

    private static bool IsSeparator(char c)
    {
        return c is ' ' or ';' or ',';
    }

    private static MacroDefinition? FindMacro(string name, IReadOnlyList<MacroDefinition> macros)
    {
        for (int i = 0; i < macros.Count; i++)
        {
            if (string.Equals(macros[i].BaseName, name, StringComparison.OrdinalIgnoreCase))
            {
                return macros[i];
            }
        }

        return null;
    }

    private static string SubstituteParams(string expansion, IReadOnlyList<string> paramNames, List<string> args)
    {
        if (args.Count == 0)
        {
            return expansion;
        }

        // Build name→value lookup from positional args mapped to parameter names
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < paramNames.Count && i < args.Count; i++)
        {
            lookup[paramNames[i]] = args[i];
        }

        return ParamSlotRegex.Replace(
            expansion,
            match =>
            {
                string token = match.Groups[1].Value;
                return lookup.TryGetValue(token, out string? value) ? value : match.Value;
            }
        );
    }

    [GeneratedRegex(@"&([A-Za-z_]\w*|\d+)")]
    private static partial Regex GetParamSlotRegex();
}
