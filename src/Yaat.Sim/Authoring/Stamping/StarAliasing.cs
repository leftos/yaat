using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Authoring.Stamping;

/// <summary>
/// Rewrites STAR names in route text on whole route tokens: a token equal to an alias's From, or the STAR part of a
/// STAR.TRANS token, never a substring of a longer token.
/// </summary>
public static class StarAliasing
{
    private static readonly ILogger Log = SimLog.CreateLogger("StarAliasing");

    /// <summary>Applies <paramref name="aliases"/> in order to the tokens of <paramref name="routeText"/>.</summary>
    /// <remarks>
    /// Matching ignores case and the output is upper case with single spaces. An alias with a blank From or To is skipped,
    /// one whose From or To holds a space or a '.' is skipped and listed as rejected, and one whose To equals its From
    /// changes nothing. A token several aliases rewrite in turn is reported once, from its first form to its last, and not
    /// at all when it ends as it began.
    /// </remarks>
    /// <param name="routeText">The route text.</param>
    /// <param name="aliases">The aliases, applied one after another.</param>
    /// <returns>The new route text, each token replaced and each alias rejected.</returns>
    public static StarAliasResult Substitute(string? routeText, IReadOnlyList<StarAlias> aliases)
    {
        string[] original = [.. RouteTokens.Split(routeText).Select(t => t.ToUpperInvariant())];
        string[] tokens = [.. original];
        var lastAlias = new StarAlias?[tokens.Length];
        var rejected = new List<StarAlias>();
        foreach (StarAlias alias in aliases)
        {
            if (IsBlank(alias))
            {
                continue;
            }

            if (IsMalformed(alias))
            {
                rejected.Add(alias);
                continue;
            }

            ApplyAlias(tokens, alias, lastAlias);
        }

        List<StarAliasReplacement> replacements = Collapse(original, tokens, lastAlias);
        if ((replacements.Count > 0) && Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("STAR aliases replaced {Count} route tokens", replacements.Count);
        }

        return new StarAliasResult(RouteTokens.Join(tokens), replacements, rejected);
    }

    private static bool IsBlank(StarAlias alias) => string.IsNullOrWhiteSpace(alias.From) || string.IsNullOrWhiteSpace(alias.To);

    private static bool IsMalformed(StarAlias alias) => !IsStarName(alias.From.Trim()) || !IsStarName(alias.To.Trim());

    private static bool IsStarName(string name) => !name.Any(c => char.IsWhiteSpace(c) || (c == '.'));

    private static void ApplyAlias(string[] tokens, StarAlias alias, StarAlias?[] lastAlias)
    {
        string from = alias.From.Trim();
        string to = alias.To.Trim().ToUpperInvariant();
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        for (int i = 0; i < tokens.Length; i++)
        {
            DottedToken parts = RouteTokens.SplitDotted(tokens[i]);
            if (string.Equals(parts.Head, from, StringComparison.OrdinalIgnoreCase))
            {
                tokens[i] = to + parts.Rest;
                lastAlias[i] = alias;
            }
        }
    }

    private static List<StarAliasReplacement> Collapse(string[] original, string[] tokens, StarAlias?[] lastAlias)
    {
        var replacements = new List<StarAliasReplacement>();
        for (int i = 0; i < tokens.Length; i++)
        {
            if ((lastAlias[i] is { } alias) && !string.Equals(original[i], tokens[i], StringComparison.Ordinal))
            {
                replacements.Add(new StarAliasReplacement(i, original[i], tokens[i], alias));
            }
        }

        return replacements;
    }
}

/// <summary>The outcome of <see cref="StarAliasing.Substitute"/>.</summary>
/// <param name="RouteText">The route text after every alias.</param>
/// <param name="Replacements">Each token replaced, in route order, from its first form to its last.</param>
/// <param name="RejectedAliases">Each alias skipped because its From or To holds a space or a '.'.</param>
public sealed record StarAliasResult(string RouteText, IReadOnlyList<StarAliasReplacement> Replacements, IReadOnlyList<StarAlias> RejectedAliases);

/// <summary>One token an alias replaced.</summary>
/// <param name="TokenIndex">The token's zero-based position in the route.</param>
/// <param name="OldToken">The token before the alias, upper case.</param>
/// <param name="NewToken">The token after it.</param>
/// <param name="Alias">The last alias that rewrote it.</param>
public sealed record StarAliasReplacement(int TokenIndex, string OldToken, string NewToken, StarAlias Alias);
