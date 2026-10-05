using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Data;

/// <summary>
/// Finds the SID or STAR a filed route names, as its airport publishes it, without activating it: the lookup a bare
/// <c>CVIA</c> or <c>DVIA</c> makes before it activates the filed procedure, shared with the client's quick-list rules so
/// the two cannot disagree. Filed routes carry versioned procedure names (e.g. NIMI5, TEJAS5), so the strict route-token
/// resolvers are used: a bare fix is never taken for a procedure.
/// </summary>
public static class FiledProcedureLookup
{
    /// <summary>The first token of <paramref name="route"/> that names a SID <paramref name="departure"/> publishes.</summary>
    /// <param name="navDb">The navigation database to resolve against.</param>
    /// <param name="departure">The departure airport; null or empty finds none.</param>
    /// <param name="route">The filed route, space-separated; null or blank finds none.</param>
    /// <returns>The resolved SID id and procedure; null when no token names one the airport publishes.</returns>
    public static (string Id, CifpSidProcedure Procedure)? FindSid(NavigationDatabase navDb, string? departure, string? route)
    {
        if (string.IsNullOrEmpty(departure))
        {
            return null;
        }

        foreach (string token in RouteTokens(route))
        {
            if ((navDb.ResolveSidId(token) is { } sidId) && (navDb.GetSid(departure, sidId) is { } sid))
            {
                return (sidId, sid);
            }
        }

        return null;
    }

    /// <summary>The first token of <paramref name="route"/> that names a STAR <paramref name="destination"/> publishes.</summary>
    /// <param name="navDb">The navigation database to resolve against.</param>
    /// <param name="destination">The destination airport; null or empty finds none.</param>
    /// <param name="route">The filed route, space-separated; null or blank finds none.</param>
    /// <returns>The resolved STAR id and procedure; null when no token names one the airport publishes.</returns>
    public static (string Id, CifpStarProcedure Procedure)? FindStar(NavigationDatabase navDb, string? destination, string? route)
    {
        if (string.IsNullOrEmpty(destination))
        {
            return null;
        }

        foreach (string token in RouteTokens(route))
        {
            if ((navDb.ResolveStarId(token) is { } starId) && (navDb.GetStar(destination, starId) is { } star))
            {
                return (starId, star);
            }
        }

        return null;
    }

    private static string[] RouteTokens(string? route) =>
        string.IsNullOrWhiteSpace(route) ? [] : route.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
