using System.Text.RegularExpressions;

namespace Yaat.Client.Services;

public sealed partial class MacroDefinition
{
    private static readonly Regex ParamRegex = GetParamRegex();
    private static readonly Regex NameRegex = GetNameRegex();

    public required string Name { get; set; }
    public required string Expansion { get; set; }

    /// <summary>
    /// Ordered list of unique parameter tokens found in Expansion (first-appearance order).
    /// Supports both positional ($1, $2) and named ($hdg, $alt) parameters.
    /// </summary>
    public IReadOnlyList<string> ParameterNames
    {
        get
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
    }

    public int ParameterCount => ParameterNames.Count;

    public static bool IsValidName(string name)
    {
        return !string.IsNullOrEmpty(name) && name.Length <= 30 && NameRegex.IsMatch(name);
    }

    [GeneratedRegex(@"\$([A-Za-z_]\w*|\d+)")]
    private static partial Regex GetParamRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex GetNameRegex();
}
