using System.Text.RegularExpressions;

namespace Yaat.Client.Services;

/// <summary>
/// Flight-plan fields a CRC alias can substitute. Kept as a plain value so the substitution stays pure —
/// the caller reads these off whichever aircraft is selected.
/// </summary>
public readonly record struct CrcAliasContext(string Departure, string Destination, string Route)
{
    public static CrcAliasContext None => new("", "", "");

    public bool HasFlightPlan =>
        !string.IsNullOrWhiteSpace(Departure) || !string.IsNullOrWhiteSpace(Destination) || !string.IsNullOrWhiteSpace(Route);
}

/// <summary>
/// Substitutes the CRC alias <c>$variables</c> that YAAT supports.
/// </summary>
/// <remarks>
/// Only the variables that appear in the alias verbs YAAT executes are implemented. CRC resolves a much
/// larger set (<c>$squawk</c>, <c>$freq()</c>, <c>$dist()</c>, …), but those only ever occur in
/// <c>.msg</c>/<c>.am</c> bodies, which YAAT rejects — so they are left literal rather than half-supported.
///
/// Substitution runs in two passes, no-argument variables before function-form ones, exactly as CRC does.
/// That ordering is what makes <c>$urlescape($fullroute)</c> work without any recursive re-parsing: by the
/// time the function pass runs, <c>$fullroute</c> has already become literal text.
/// </remarks>
public static partial class CrcAliasVariables
{
    /// <summary>CRC's sentinel for a variable it cannot resolve.</summary>
    public const string Unresolved = "----";

    private static readonly Regex SimpleVariableRegex = GetSimpleVariableRegex();
    private static readonly Regex FunctionVariableRegex = GetFunctionVariableRegex();
    private static readonly Regex LeadingPlusRegex = GetLeadingPlusRegex();

    public static string Substitute(string text, CrcAliasContext context)
    {
        var withSimple = SimpleVariableRegex.Replace(text, match => ResolveSimple(match.Groups[1].Value, context) ?? match.Value);
        return FunctionVariableRegex.Replace(withSimple, match => ResolveFunction(match.Groups[1].Value, match.Groups[2].Value) ?? match.Value);
    }

    /// <summary>Returns null for a name we don't handle, so the caller leaves the token literal.</summary>
    private static string? ResolveSimple(string name, CrcAliasContext context)
    {
        return name.ToLowerInvariant() switch
        {
            "dep" => Or(context.Departure),
            "arr" => Or(context.Destination),
            "route" => context.HasFlightPlan ? StripLeadingPlus(context.Route) : Unresolved,
            "fullroute" => context.HasFlightPlan ? BuildFullRoute(context) : Unresolved,
            _ => null,
        };
    }

    private static string? ResolveFunction(string name, string argument)
    {
        return name.ToLowerInvariant() switch
        {
            "urlescape" => Uri.EscapeDataString(argument),
            _ => null,
        };
    }

    private static string Or(string value) => string.IsNullOrWhiteSpace(value) ? Unresolved : value;

    /// <summary>vNAS flight-plan routes can carry a leading <c>+</c> marker; CRC strips it for display.</summary>
    private static string StripLeadingPlus(string route) => LeadingPlusRegex.Replace(route, "");

    private static string BuildFullRoute(CrcAliasContext context) =>
        $"{context.Departure} {StripLeadingPlus(context.Route)} {context.Destination}".Trim();

    [GeneratedRegex(@"\$(\w+)(?!\()", RegexOptions.IgnoreCase)]
    private static partial Regex GetSimpleVariableRegex();

    [GeneratedRegex(@"\$(\w+)\(([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex GetFunctionVariableRegex();

    [GeneratedRegex(@"^\+")]
    private static partial Regex GetLeadingPlusRegex();
}
