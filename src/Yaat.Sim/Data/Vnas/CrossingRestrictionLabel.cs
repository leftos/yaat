namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// Formats a fix's crossing altitude/speed restriction into the compact label lines drawn
/// under the fix on the radar "Show nav route" overlay. Covers both controller-issued
/// restrictions (<c>CFIX</c>) and procedure-published ones (SID/STAR/approach legs) — they
/// live in the same <see cref="CifpAltitudeRestriction"/>/<see cref="CifpSpeedRestriction"/>
/// form on the flown route, so one formatter serves both.
///
/// Conventions:
/// <list type="bullet">
///   <item>Altitude ≥ 18000 ft renders as a flight level (<c>FL240</c>); below that, as feet (<c>6000</c>).</item>
///   <item>At → the bare altitude. At-or-above (and glideslope-intercept, a minimum until
///         intercept per AIM 5-4-5.b.2 Note 2) → <c>≥alt</c> (a floor). At-or-below → <c>≤alt</c>
///         (a ceiling). A window (between) → two lines, ceiling over floor.</item>
///   <item>Speed is the bare knots value for a ceiling/mandatory limit (how CFIX and most charted
///         speeds are flown); an at-or-above speed floor is annotated <c>≥kts</c>.</item>
///   <item>A single-altitude restriction shares one line with the speed (<c>≥6000  250</c>);
///         a window stacks the speed on its own line beneath the two altitude lines.</item>
/// </list>
/// </summary>
public static class CrossingRestrictionLabel
{
    private const int FlightLevelFloorFt = 18000;

    /// <summary>
    /// Returns the ordered label lines for the given restrictions, or an empty list when the fix
    /// carries neither an altitude nor a speed restriction.
    /// </summary>
    public static IReadOnlyList<string> BuildLines(CifpAltitudeRestriction? altitude, CifpSpeedRestriction? speed) =>
        [.. BuildTokenLines(altitude, speed, altitudeLatent: false, speedLatent: false).Select(line => string.Join("  ", line.Select(t => t.Text)))];

    /// <summary>
    /// Returns the label lines as tokens, laid out as <see cref="BuildLines"/> lays out its lines (a line's tokens joined
    /// with two spaces are that line), or an empty list when the fix carries neither restriction. Every altitude token
    /// carries <paramref name="altitudeLatent"/> and the speed token <paramref name="speedLatent"/>.
    /// </summary>
    /// <param name="altitude">The fix's altitude restriction, or null.</param>
    /// <param name="speed">The fix's speed restriction, or null.</param>
    /// <param name="altitudeLatent">Whether the altitude restriction is published but not in force now.</param>
    /// <param name="speedLatent">Whether the speed restriction is published but not in force now.</param>
    /// <returns>The ordered label lines, each its tokens left to right.</returns>
    public static IReadOnlyList<IReadOnlyList<NavRouteRestrictionToken>> BuildTokenLines(
        CifpAltitudeRestriction? altitude,
        CifpSpeedRestriction? speed,
        bool altitudeLatent,
        bool speedLatent
    )
    {
        List<string> altLines = BuildAltitudeLines(altitude);
        string? speedText = FormatSpeed(speed);
        NavRouteRestrictionToken? speedToken = speedText is null ? null : new NavRouteRestrictionToken(speedText, speedLatent);

        if (altLines.Count <= 1)
        {
            var combined = new List<NavRouteRestrictionToken>(2);
            if (altLines.Count == 1)
            {
                combined.Add(new NavRouteRestrictionToken(altLines[0], altitudeLatent));
            }
            if (speedToken is not null)
            {
                combined.Add(speedToken);
            }
            return combined.Count == 0 ? [] : [combined];
        }

        // Window: two altitude lines (ceiling over floor); speed, if any, on its own line below.
        var lines = new List<IReadOnlyList<NavRouteRestrictionToken>>(altLines.Count + 1);
        lines.AddRange(altLines.Select(text => (IReadOnlyList<NavRouteRestrictionToken>)[new NavRouteRestrictionToken(text, altitudeLatent)]));
        if (speedToken is not null)
        {
            lines.Add([speedToken]);
        }
        return lines;
    }

    private static List<string> BuildAltitudeLines(CifpAltitudeRestriction? altitude)
    {
        if (altitude is not { } alt)
        {
            return [];
        }

        return alt.Type switch
        {
            // GlideSlopeIntercept is the altitude maintained until GS/GP intercept — an at-or-above
            // minimum (AIM 5-4-5.b.2 Note 2), so it shares the ≥ floor rendering.
            CifpAltitudeRestrictionType.AtOrAbove or CifpAltitudeRestrictionType.GlideSlopeIntercept => [$"≥{FormatAltitude(alt.Altitude1Ft)}"],
            CifpAltitudeRestrictionType.AtOrBelow => [$"≤{FormatAltitude(alt.Altitude1Ft)}"],
            CifpAltitudeRestrictionType.Between when alt.Altitude2Ft is { } lower =>
            [
                $"≤{FormatAltitude(alt.Altitude1Ft)}",
                $"≥{FormatAltitude(lower)}",
            ],
            // At, or a malformed Between without a lower bound: a single crossing altitude.
            _ => [FormatAltitude(alt.Altitude1Ft)],
        };
    }

    private static string? FormatSpeed(CifpSpeedRestriction? speed)
    {
        if (speed is not { } s)
        {
            return null;
        }

        string kts = s.SpeedKts.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // AtOrBelow (the '-' qualifier) and Mandatory are both flown as do-not-exceed ceilings, so a
        // bare number reads correctly. An at-or-above speed floor (the rare '+' qualifier) is annotated.
        return s.Type == CifpSpeedRestrictionType.AtOrAbove ? $"≥{kts}" : kts;
    }

    private static string FormatAltitude(int feet) =>
        feet >= FlightLevelFloorFt ? $"FL{feet / 100}" : feet.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
