using System.Globalization;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Authoring.Routes;

/// <summary>
/// What resolving a route point gave: its position and nav-path text, or a failure message; with a warning when the text is
/// valid but does not say exactly what the author placed.
/// </summary>
/// <param name="Position">Where the written text resolves; null on failure.</param>
/// <param name="Text">The nav-path text the scenario loader reads; null on failure.</param>
/// <param name="Failure">Why the point cannot be written; null on success.</param>
/// <param name="Warning">What the author should know about the written text; null when there is nothing.</param>
public sealed record RoutePointResult(LatLon? Position, string? Text, string? Failure, string? Warning)
{
    /// <summary>A failed result.</summary>
    /// <param name="message">Why the point cannot be written.</param>
    /// <returns>The failure.</returns>
    public static RoutePointResult Fail(string message) => new(null, null, message, null);
}

/// <summary>
/// Turns route points into positions and the nav-path text the scenario loader and <see cref="RouteExpander"/> read. A fix
/// is written as its ident and an FRD as ident, 3-digit magnetic radial and 3-digit NM distance (<c>SFO090010</c>). A raw
/// coordinate is written as an FRD from the nearest NAVAID that gives both a radial and a DME within 30 NM, else as a
/// lat/lon token (<c>3904N/10916W</c>). Nothing here throws for an unknown fix or missing navdata; callers pass
/// <see cref="NavigationDatabase.InstanceOrNull"/>.
/// </summary>
public static class RoutePointResolver
{
    /// <summary>The failure every method returns when no navigation database is loaded.</summary>
    public const string NavdataNotLoaded = "navdata not loaded";

    /// <summary>How far a NAVAID that gives a radial and a DME may be from a coordinate it anchors, in NM.</summary>
    public const double NavaidAnchorRangeNm = 30.0;

    /// <summary>How far the written text may land from the placed point before a warning, in NM.</summary>
    public const double DisplacementWarningNm = 1.0;

    /// <summary>How far apart two positions of one ident may be and still count as the same instance, in NM.</summary>
    public const double SameInstanceNm = 0.1;

    private const double WholeMinuteTolerance = 1e-9;

    private const string DuplicateAdvice = "write the point as an FRD from a unique NAVAID or add a neighbouring fix.";

    private static readonly ILogger Log = SimLog.CreateLogger("RoutePointResolver");

    /// <summary>Resolves one point to its position and nav-path text.</summary>
    /// <param name="point">The point.</param>
    /// <param name="ctx">The airports a fix or FRD anchor may fall back to (CIFP terminal waypoints), as the sim resolves it.</param>
    /// <param name="navDb">The navigation database; null when it is not loaded.</param>
    /// <returns>The position and text, or a failure.</returns>
    public static RoutePointResult Resolve(RoutePoint point, FixResolutionContext ctx, NavigationDatabase? navDb)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (navDb is null)
        {
            return RoutePointResult.Fail(NavdataNotLoaded);
        }

        return point.Kind switch
        {
            RoutePointKind.Fix => ResolveFix(point.Fix, ctx, navDb),
            RoutePointKind.Frd => ResolveFrd(point, ctx, navDb),
            RoutePointKind.LatLon when (point.Lat is { } lat) && (point.Lon is { } lon) => ResolveLatLon(new LatLon(lat, lon), navDb),
            RoutePointKind.LatLon => RoutePointResult.Fail($"The {point.Kind} point is missing its fields."),
            _ => RoutePointResult.Fail(string.Create(CultureInfo.InvariantCulture, $"Unknown route point kind {(int)point.Kind}.")),
        };
    }

    /// <summary>
    /// Resolves a saved coordinate: one that sits on whole minutes of latitude and longitude is the position a written token
    /// parses to, so it keeps that token instead of re-anchoring; any other coordinate anchors the way a click does.
    /// </summary>
    /// <param name="position">The saved coordinate.</param>
    /// <param name="navDb">The navigation database, loaded.</param>
    /// <returns>The token's text and position, or the anchor's.</returns>
    private static RoutePointResult ResolveLatLon(LatLon position, NavigationDatabase navDb) =>
        IsWholeMinute(position) ? LatLonToken(position) : Anchor(position, navDb);

    /// <summary>Whether a coordinate sits on whole minutes of latitude and longitude, the position a lat/lon token parses to.</summary>
    /// <param name="position">The coordinate.</param>
    /// <returns>True when both parts are whole minutes.</returns>
    private static bool IsWholeMinute(LatLon position) => IsWholeMinuteDegrees(position.Lat) && IsWholeMinuteDegrees(position.Lon);

    private static bool IsWholeMinuteDegrees(double degrees)
    {
        double minutes = Math.Abs(degrees) * 60.0;
        return Math.Abs(minutes - Math.Round(minutes)) <= WholeMinuteTolerance;
    }

    /// <summary>
    /// Writes a raw coordinate as an FRD (or a bare fix when it sits on one) from the nearest navaid that gives both a
    /// magnetic radial and a DME within 30 NM (<see cref="NavigationDatabase.IsDmeRadialNavaid"/>: a VOR/DME, VORTAC or
    /// TACAN), else as a lat/lon token the scenario loader reads (<c>3904N/10916W</c>), with the position that text resolves
    /// to so a preview can show where the point moved.
    /// </summary>
    /// <param name="click">Where the author placed the point.</param>
    /// <param name="navDb">The navigation database; null when it is not loaded.</param>
    /// <returns>The text and its position, a failure only when navdata is not loaded, and a warning past 1 NM.</returns>
    public static RoutePointResult Anchor(LatLon click, NavigationDatabase? navDb)
    {
        if (navDb is null)
        {
            return RoutePointResult.Fail(NavdataNotLoaded);
        }

        NearbyFix? anchor = navDb
            .FixesWithin(click, NavaidAnchorRangeNm)
            .FirstOrDefault(f => IsAnchorable(f.Name) && navDb.IsDmeRadialNavaid(f.Name));
        if (anchor is null)
        {
            return LatLonToken(click);
        }

        (string, double, double)[] anchors = [(anchor.Name, anchor.Position.Lat, anchor.Position.Lon)];
        string? text = FrdResolver.ToFrd(click.Lat, click.Lon, anchors, navDb, anchor.DistanceNm + 1.0);
        if ((text is null) || (FrdResolver.Resolve(text, navDb) is not { } resolved))
        {
            Log.LogWarning("Anchor: {Navaid} in range but {Text} does not resolve; writing a lat/lon token", anchor.Name, text);
            return LatLonToken(click);
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Anchored {Lat},{Lon} as {Text}", click.Lat, click.Lon, text);
        }

        return new RoutePointResult(resolved, text, null, DisplacementWarning(click, resolved, text));
    }

    /// <summary>
    /// Reads the lat/lon nav-path token the scenario loader takes (<c>3904N/10916W</c>, <c>3330S/15112E</c>) as a position,
    /// the way a caller tells a token point from a fix ident.
    /// </summary>
    /// <param name="text">The nav-path text to read.</param>
    /// <param name="position">Where the token points, when it is one; default otherwise.</param>
    /// <returns>True when <paramref name="text"/> is a lat/lon token.</returns>
    public static bool TryParseLatLonToken(string text, out LatLon position)
    {
        if (!string.IsNullOrWhiteSpace(text) && (ScenarioLoader.ParseNavPathCoordinate(text) is { } parsed))
        {
            position = new LatLon(parsed.Lat, parsed.Lon);
            return true;
        }

        position = default;
        return false;
    }

    /// <summary>The lat/lon token for a coordinate no navaid can anchor, with the position the token resolves to.</summary>
    /// <param name="point">Where the author placed the point.</param>
    /// <returns>The token text, its position, and a warning past 1 NM.</returns>
    private static RoutePointResult LatLonToken(LatLon point)
    {
        int latRaw = MinuteRaw(point.Lat);
        int lonRaw = MinuteRaw(point.Lon);
        char latHemisphere = (point.Lat < 0) ? 'S' : 'N';
        char lonHemisphere = (point.Lon < 0) ? 'W' : 'E';
        string text = string.Create(
            CultureInfo.InvariantCulture,
            $"{(latRaw / 100):D2}{latRaw % 100:D2}{latHemisphere}/{(lonRaw / 100):D3}{lonRaw % 100:D2}{lonHemisphere}"
        );
        if (!TryParseLatLonToken(text, out LatLon position))
        {
            throw new InvalidOperationException($"RoutePointResolver wrote a lat/lon token the scenario loader cannot read: {text}");
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Anchored {Lat},{Lon} as {Text}", point.Lat, point.Lon, text);
        }

        return new RoutePointResult(position, text, null, DisplacementWarning(point, position, text));
    }

    /// <summary>A coordinate's absolute value as DDMM, its minutes rounded to the nearest so the token reads back within half a minute.</summary>
    /// <param name="degrees">The coordinate in signed degrees.</param>
    /// <returns>The degrees and minutes as one integer (DDMM).</returns>
    private static int MinuteRaw(double degrees)
    {
        double absolute = Math.Abs(degrees);
        int wholeDegrees = (int)absolute;
        int minutes = (int)Math.Round((absolute - wholeDegrees) * 60.0, MidpointRounding.AwayFromZero);
        if (minutes >= 60)
        {
            wholeDegrees += 1;
            minutes -= 60;
        }

        return (wholeDegrees * 100) + minutes;
    }

    /// <summary>
    /// Picks the instance of <paramref name="ident"/> nearest <paramref name="near"/> among the NavData fix and the CIFP
    /// terminal waypoints of the context's airports, then resolves the ident the way the sim will
    /// (<see cref="NavigationDatabase.GetFixPosition(string, FixResolutionContext)"/>) and warns when that lands on another
    /// instance.
    /// </summary>
    /// <param name="ident">The fix ident.</param>
    /// <param name="near">Where the author placed the point.</param>
    /// <param name="ctx">The airports the sim falls back to for this aircraft.</param>
    /// <param name="navDb">The navigation database; null when it is not loaded.</param>
    /// <returns>The picked instance's position and the ident, a failure for an unknown ident, and a warning on a mismatch.</returns>
    public static RoutePointResult PickFix(string ident, LatLon near, FixResolutionContext ctx, NavigationDatabase? navDb)
    {
        if (navDb is null)
        {
            return RoutePointResult.Fail(NavdataNotLoaded);
        }

        string name = ident.Trim().ToUpperInvariant();
        List<LatLon> instances = Instances(name, ctx, navDb);
        if (instances.Count == 0)
        {
            return RoutePointResult.Fail($"Unknown fix {name}.");
        }

        LatLon picked = instances.MinBy(p => GeoMath.DistanceNm(near, p));
        (double Lat, double Lon)? runtime = navDb.GetFixPosition(name, ctx);
        double apartNm = runtime is { } r ? GeoMath.DistanceNm(picked, new LatLon(r.Lat, r.Lon)) : 0.0;
        string? warning = null;
        if (apartNm > SameInstanceNm)
        {
            warning = string.Create(
                CultureInfo.InvariantCulture,
                $"The sim reads {name} as another fix of that name, {apartNm:0.0} NM away; {DuplicateAdvice}"
            );
        }

        return new RoutePointResult(picked, name, null, warning);
    }

    /// <summary>The nav-path text of a route: each point's text joined by single spaces, or the first point that fails.</summary>
    /// <param name="points">The route's points in flying order.</param>
    /// <param name="ctx">The airports a fix or FRD anchor may fall back to, as the sim resolves the route.</param>
    /// <param name="navDb">The navigation database; null when it is not loaded.</param>
    /// <returns>The joined text with the points' warnings, or the first failure naming its point by 1-based number.</returns>
    public static RoutePointResult NavPathText(IReadOnlyList<RoutePoint> points, FixResolutionContext ctx, NavigationDatabase? navDb)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (navDb is null)
        {
            return RoutePointResult.Fail(NavdataNotLoaded);
        }

        List<string> texts = [];
        List<string> warnings = [];
        for (int i = 0; i < points.Count; i++)
        {
            RoutePointResult result = Resolve(points[i], ctx, navDb);
            if (result.Failure is not null)
            {
                return RoutePointResult.Fail($"Point {i + 1}: {result.Failure}");
            }

            texts.Add(result.Text!);
            if (result.Warning is not null)
            {
                warnings.Add($"Point {i + 1}: {result.Warning}");
            }
        }

        return new RoutePointResult(null, string.Join(' ', texts), null, warnings.Count == 0 ? null : string.Join("; ", warnings));
    }

    /// <summary>The warning for written text that lands more than 1 NM from where the author placed the point.</summary>
    /// <param name="placed">Where the author placed the point.</param>
    /// <param name="resolved">Where the written text resolves.</param>
    /// <param name="text">The written text.</param>
    /// <returns>The warning, or null within 1 NM.</returns>
    public static string? DisplacementWarning(LatLon placed, LatLon resolved, string text)
    {
        double movedNm = GeoMath.DistanceNm(placed, resolved);
        if (movedNm <= DisplacementWarningNm)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{text} lands {movedNm:0.0} NM from where the point was placed.");
    }

    private static bool IsAnchorable(string name) => !FrdResolver.IsFrdIdentifier(name);

    private static RoutePointResult ResolveFix(string? ident, FixResolutionContext ctx, NavigationDatabase navDb)
    {
        if (string.IsNullOrWhiteSpace(ident))
        {
            return RoutePointResult.Fail("The fix point has no ident.");
        }

        string name = ident.Trim().ToUpperInvariant();
        (double Lat, double Lon)? pos = navDb.GetFixPosition(name, ctx);
        return pos is { } p ? new RoutePointResult(new LatLon(p.Lat, p.Lon), name, null, null) : RoutePointResult.Fail($"Unknown fix {name}.");
    }

    private static RoutePointResult ResolveFrd(RoutePoint point, FixResolutionContext ctx, NavigationDatabase navDb)
    {
        if (string.IsNullOrWhiteSpace(point.Fix) || (point.Radial is not (>= 1 and <= 360)) || (point.DistanceNm is not (>= 0 and <= 999)))
        {
            return RoutePointResult.Fail("The FRD point needs a fix, a radial from 1 to 360 and a distance from 0 to 999 NM.");
        }

        string anchor = point.Fix.Trim().ToUpperInvariant();
        if (!IsAnchorable(anchor))
        {
            return RoutePointResult.Fail($"{anchor} is itself an FRD; anchor the FRD on a named fix.");
        }

        string text = string.Create(CultureInfo.InvariantCulture, $"{anchor}{point.Radial.Value:D3}{point.DistanceNm.Value:D3}");
        LatLon? pos = FrdResolver.Resolve(text, navDb, ctx);
        return pos is { } p ? new RoutePointResult(p, text, null, null) : RoutePointResult.Fail($"Unknown fix {anchor}.");
    }

    private static List<LatLon> Instances(string name, FixResolutionContext ctx, NavigationDatabase navDb)
    {
        List<LatLon> instances = [];
        if (navDb.GetFixPosition(name) is { } navData)
        {
            instances.Add(new LatLon(navData.Lat, navData.Lon));
        }

        foreach (string? airport in (string?[])[ctx.Destination, ctx.Departure])
        {
            if ((airport is not null) && navDb.TryGetTerminalWaypoint(airport, name, out (double Lat, double Lon) terminal))
            {
                instances.Add(new LatLon(terminal.Lat, terminal.Lon));
            }
        }

        return instances;
    }
}
