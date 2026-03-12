using System.Text.RegularExpressions;

namespace Yaat.Client.Services;

public sealed partial class MacroDefinition
{
    private static readonly Regex ParamRegex = GetParamRegex();
    private static readonly Regex NameRegex = GetNameRegex();
    private static readonly Regex ParamTokenRegex = GetParamTokenRegex();

    public required string Name { get; set; }
    public required string Expansion { get; set; }

    /// <summary>
    /// First whitespace-delimited token of Name (the actual macro identifier).
    /// </summary>
    public string BaseName => ExtractBaseName(Name);

    /// <summary>
    /// True when Name contains $token declarations after the base name.
    /// </summary>
    public bool HasExplicitParameters
    {
        get
        {
            var tokens = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 1 && tokens.Skip(1).Any(t => t.StartsWith('&'));
        }
    }

    /// <summary>
    /// Ordered list of unique parameter tokens.
    /// If the Name declares explicit parameters (e.g. "HC $hdg $alt"), those are used in declaration order.
    /// Otherwise, parameters are inferred from Expansion in first-appearance order.
    /// </summary>
    public IReadOnlyList<string> ParameterNames
    {
        get
        {
            if (HasExplicitParameters)
            {
                return ParseExplicitParameters();
            }

            return InferParametersFromExpansion();
        }
    }

    public int ParameterCount => ParameterNames.Count;

    public static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        // First token: valid identifier, ≤30 chars
        if (tokens[0].Length > 30 || !NameRegex.IsMatch(tokens[0]))
        {
            return false;
        }

        // Remaining tokens (if any): must be $identifier, no duplicates
        if (tokens.Length > 1)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < tokens.Length; i++)
            {
                if (!ParamTokenRegex.IsMatch(tokens[i]))
                {
                    return false;
                }

                var paramName = tokens[i][1..]; // strip &
                if (!seen.Add(paramName))
                {
                    return false; // duplicate
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Extracts the base macro name (first token) from a full Name string.
    /// </summary>
    public static string ExtractBaseName(string name)
    {
        return name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    /// <summary>
    /// Validates that explicit parameters (if declared) all appear in the expansion.
    /// Returns null if valid, or an error message if a declared parameter is missing from the expansion.
    /// Inferred-parameter macros always pass validation.
    /// </summary>
    public string? Validate()
    {
        if (!HasExplicitParameters)
        {
            return null;
        }

        var paramNames = ParseExplicitParameters();
        foreach (var param in paramNames)
        {
            var pattern = $"&{param}";
            if (!Expansion.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return $"Parameter &{param} declared in name but not found in expansion";
            }
        }

        return null;
    }

    private List<string> ParseExplicitParameters()
    {
        var tokens = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var names = new List<string>();
        for (var i = 1; i < tokens.Length; i++)
        {
            if (tokens[i].StartsWith('&') && tokens[i].Length > 1)
            {
                names.Add(tokens[i][1..]);
            }
        }

        return names;
    }

    private List<string> InferParametersFromExpansion()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (Match m in ParamRegex.Matches(Expansion))
        {
            var token = m.Groups[1].Value;
            if (seen.Add(token))
            {
                names.Add(token);
            }
        }

        return names;
    }

    [GeneratedRegex(@"&([A-Za-z_]\w*|\d+)")]
    private static partial Regex GetParamRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex GetNameRegex();

    [GeneratedRegex(@"^&[A-Za-z_]\w*$")]
    private static partial Regex GetParamTokenRegex();
}
