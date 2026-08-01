using PR = Yaat.Sim.Commands.ParseResult<Yaat.Sim.Commands.ParsedCommand>;

namespace Yaat.Sim.Commands;

/// <summary>
/// Parses the AP/1B military training route clearances (FAA JO 7110.65 §9-2-6).
///
/// Kept out of <see cref="ApproachCommandParser"/> because military routes are a self-contained
/// domain with their own database dependency, following the per-domain split already used by
/// <see cref="GroundCommandParser"/> and <see cref="DepartureCommandParser"/>.
/// </summary>
public static class MilitaryRouteCommandParser
{
    /// <summary>
    /// <c>CMTR IR149</c> — cleared in, maintain published route altitudes.
    /// <c>CMTR IR149 50</c> — cleared in, maintain 5000.
    /// <c>CMTR IR149 B50</c> — cleared in, maintain at or below 5000.
    /// </summary>
    internal static PR ParseClearedInto(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Fail("CMTR requires a military route ID (e.g. CMTR IR149)");
        }

        var parts = arg.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var designator = parts[0].ToUpperInvariant();

        if (parts.Length == 1)
        {
            return PR.Ok(new ClearedIntoMilitaryRouteCommand(designator, null, false));
        }

        if (parts.Length > 2)
        {
            return PR.Fail($"CMTR takes a route ID and at most one altitude, got '{arg.Trim()}'");
        }

        // The B prefix mirrors the existing at-or-below convention used by CFIX {fix} B{alt}.
        var altitudeToken = parts[1];
        bool atOrBelow = altitudeToken.StartsWith('B') || altitudeToken.StartsWith('b');
        if (atOrBelow)
        {
            altitudeToken = altitudeToken[1..];
        }

        var altitude = AltitudeResolver.Resolve(altitudeToken);
        if (altitude is null)
        {
            return PR.Fail($"Invalid altitude '{parts[1]}' for CMTR");
        }

        return PR.Ok(new ClearedIntoMilitaryRouteCommand(designator, altitude, atOrBelow));
    }

    internal static PR ParseMaintainRouteAltitudes(string? arg)
    {
        return string.IsNullOrWhiteSpace(arg)
            ? PR.Ok(new MaintainMilitaryRouteAltitudesCommand())
            : PR.Fail($"MTRA takes no arguments, got '{arg.Trim()}'");
    }

    /// <summary>
    /// <c>XMTR KTCM</c> — cleared to a limit off the route.
    /// <c>XMTR KTCM VIA V495 SEA</c> — cleared to a limit via a route of flight.
    /// </summary>
    internal static PR ParseClearedOutOf(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return PR.Fail("XMTR requires a clearance limit (e.g. XMTR KTCM)");
        }

        var parts = arg.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var destination = parts[0].ToUpperInvariant();

        if (parts.Length == 1)
        {
            return PR.Ok(new ClearedOutOfMilitaryRouteCommand(destination, null));
        }

        int routeStart = string.Equals(parts[1], "VIA", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        if (routeStart >= parts.Length)
        {
            return PR.Fail("XMTR VIA requires a route of flight");
        }

        var route = string.Join(' ', parts[routeStart..]).ToUpperInvariant();
        return PR.Ok(new ClearedOutOfMilitaryRouteCommand(destination, route));
    }

    internal static PR ParseSayExitFixEstimate(string? arg)
    {
        return string.IsNullOrWhiteSpace(arg) ? PR.Ok(new SayExitFixEstimateCommand()) : PR.Fail($"SAYEXIT takes no arguments, got '{arg.Trim()}'");
    }
}
