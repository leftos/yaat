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

        var current = commandText;
        var everExpanded = false;

        for (var depth = 0; depth < MaxExpansionDepth; depth++)
        {
            var result = ExpandOnce(current, macros, out error);
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
        var i = 0;
        var expanded = false;

        while (i < commandText.Length)
        {
            if (commandText[i] == '!' && IsMacroBoundary(commandText, i))
            {
                var nameStart = i + 1;
                var nameEnd = nameStart;
                while (nameEnd < commandText.Length && !IsSeparator(commandText[nameEnd]))
                {
                    nameEnd++;
                }

                var name = commandText[nameStart..nameEnd];
                var macro = FindMacro(name, macros);

                if (macro is null)
                {
                    error = $"Unknown macro \"!{name}\"";
                    return null;
                }

                var paramNames = macro.ParameterNames;
                var args = new List<string>(paramNames.Count);
                var pos = nameEnd;

                for (var p = 0; p < paramNames.Count; p++)
                {
                    while (pos < commandText.Length && commandText[pos] == ' ')
                    {
                        pos++;
                    }

                    if (pos >= commandText.Length || commandText[pos] is ';' or ',')
                    {
                        var hint = paramNames.Count > 0 ? $" ({string.Join(", ", paramNames.Select(n => $"${n}"))})" : "";
                        error = $"Macro \"!{macro.Name}\" expects {paramNames.Count} parameter(s), got {p}{hint}";
                        return null;
                    }

                    var argStart = pos;
                    while (pos < commandText.Length && commandText[pos] != ' ' && commandText[pos] != ';' && commandText[pos] != ',')
                    {
                        pos++;
                    }

                    args.Add(commandText[argStart..pos]);
                }

                var expansion = SubstituteParams(macro.Expansion, paramNames, args);
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

        var prev = text[bangIndex - 1];
        return prev is ' ' or ';' or ',';
    }

    private static bool IsSeparator(char c)
    {
        return c is ' ' or ';' or ',';
    }

    private static MacroDefinition? FindMacro(string name, IReadOnlyList<MacroDefinition> macros)
    {
        for (var i = 0; i < macros.Count; i++)
        {
            if (string.Equals(macros[i].Name, name, StringComparison.OrdinalIgnoreCase))
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
        for (var i = 0; i < paramNames.Count && i < args.Count; i++)
        {
            lookup[paramNames[i]] = args[i];
        }

        return ParamSlotRegex.Replace(
            expansion,
            match =>
            {
                var token = match.Groups[1].Value;
                return lookup.TryGetValue(token, out var value) ? value : match.Value;
            }
        );
    }

    [GeneratedRegex(@"\$([A-Za-z_]\w*|\d+)")]
    private static partial Regex GetParamSlotRegex();
}
