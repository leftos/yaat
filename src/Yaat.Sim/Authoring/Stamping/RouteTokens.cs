using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring.Stamping;

/// <summary>
/// Splits route text into its whitespace tokens and a dotted token into the part before its first '.' and the rest, the
/// way <see cref="Yaat.Sim.Data.RouteExpander"/> reads a STAR.TRANS or airway token.
/// </summary>
public static class RouteTokens
{
    private static readonly ILogger Log = SimLog.CreateLogger("RouteTokens");

    /// <summary>Splits route text on whitespace, dropping empty entries.</summary>
    /// <param name="routeText">The route text; null or blank gives no tokens.</param>
    /// <returns>The tokens in order.</returns>
    public static IReadOnlyList<string> Split(string? routeText) =>
        string.IsNullOrWhiteSpace(routeText) ? [] : routeText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Splits a token at its first '.'.</summary>
    /// <param name="token">The route token.</param>
    /// <returns>The part before the first '.', and the rest from the '.' on (empty when the token has no '.').</returns>
    public static DottedToken SplitDotted(string token)
    {
        int dot = token.IndexOf('.');
        if (dot < 0)
        {
            return new DottedToken(token, "");
        }

        if (Log.IsEnabled(LogLevel.Trace))
        {
            Log.LogTrace("Route token {Token} splits at index {Dot}", token, dot);
        }

        return new DottedToken(token[..dot], token[dot..]);
    }

    /// <summary>Joins tokens with single spaces.</summary>
    /// <param name="tokens">The tokens.</param>
    /// <returns>The route text.</returns>
    public static string Join(IEnumerable<string> tokens) => string.Join(' ', tokens);
}

/// <summary>A route token split at its first '.'.</summary>
/// <param name="Head">The part before the first '.', or the whole token.</param>
/// <param name="Rest">The rest from the '.' on, or empty.</param>
public readonly record struct DottedToken(string Head, string Rest);
