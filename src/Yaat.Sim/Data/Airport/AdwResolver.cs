using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>Which end of an Arrival/Departure Window a mark depicts.</summary>
public enum AdwMarkKind
{
    /// <summary>
    /// The point on the final approach course by which a converging departure must already have begun
    /// its takeoff roll (7110.65 P/CG, <em>arrival/departure window</em>).
    /// </summary>
    Outer,

    /// <summary>The point past which the arrival has exited the window and a departure may begin its roll.</summary>
    Inner,
}

/// <summary>
/// One drawable Arrival/Departure Window mark: a short line across the arrival runway's final approach
/// course at the window's outer or inner range. Endpoints are already resolved to lat/lon, so a display
/// only has to stroke <see cref="A"/> to <see cref="B"/>.
/// </summary>
public sealed record AdwMark(string ArrivalRunway, string DepartureRunway, AdwMarkKind Kind, LatLon A, LatLon B);

/// <summary>
/// Resolves the per-airport <see cref="AdwWindow"/>s (authored as ranges in nm from the landing
/// threshold) against a concrete <see cref="AirportGroundLayout"/>, producing the marks Ground View
/// draws. Positive ranges run outbound along the final approach course; negative ones run past the
/// threshold and down the runway. Mirrors <see cref="Pathfinding.BlockedTurnResolver"/>: resolved marks
/// are cached per layout instance.
/// </summary>
public static class AdwResolver
{
    private static readonly ILogger Log = SimLog.CreateLogger("AdwResolver");

    /// <summary>
    /// Half-length of an outer mark, in feet. The outer range sits out on final with no runway to size
    /// against, so it uses the same ~547 ft tick the SFO final-approach overlay draws
    /// (<c>tools/build-surface-temp-data.py</c>).
    /// </summary>
    private const double OuterMarkHalfLengthFt = 273.5;

    private static readonly ConditionalWeakTable<AirportGroundLayout, IReadOnlyList<AdwMark>> Cache = new();

    /// <summary>
    /// ADW marks for <paramref name="layout"/>, cached. Reads the airport's windows from the global
    /// <see cref="NavigationDatabase"/>; empty when no database is initialized or the airport has none.
    /// </summary>
    public static IReadOnlyList<AdwMark> GetMarks(AirportGroundLayout layout) => Cache.GetValue(layout, BuildForLayout);

    private static IReadOnlyList<AdwMark> BuildForLayout(AirportGroundLayout layout)
    {
        var db = NavigationDatabase.InstanceOrNull;
        IReadOnlyList<AdwWindow> windows = db?.AirportSidecars.GetAdwWindows(layout.AirportId) ?? [];
        return Resolve(layout, windows);
    }

    /// <summary>Pure resolution of <paramref name="windows"/> against <paramref name="layout"/>. Unit-testable without a database.</summary>
    public static IReadOnlyList<AdwMark> Resolve(AirportGroundLayout layout, IReadOnlyList<AdwWindow> windows)
    {
        if (windows.Count == 0)
        {
            return [];
        }

        var marks = new List<AdwMark>(windows.Count * 2);
        foreach (var window in windows)
        {
            var runway = layout.FindRunway(window.ArrivalRunway);
            if (runway is null)
            {
                Log.LogWarning("{Airport}: ADW arrival runway {Runway} is not in the layout, skipping", layout.AirportId, window.ArrivalRunway);
                continue;
            }

            if (runway.LandingThresholdForEnd(window.ArrivalRunway) is not { } landing)
            {
                Log.LogWarning(
                    "{Airport}: ADW arrival runway {Runway} has no usable threshold geometry, skipping",
                    layout.AirportId,
                    window.ArrivalRunway
                );
                continue;
            }

            // The geometry is entirely the arrival runway's, so a bad departure designator still places
            // the marks correctly — but they would carry the name of a runway the airport does not have.
            if (layout.FindRunway(window.DepartureRunway) is null)
            {
                Log.LogWarning(
                    "{Airport}: ADW departure runway {Runway} is not in the layout; drawing the {Arrival} window anyway",
                    layout.AirportId,
                    window.DepartureRunway,
                    window.ArrivalRunway
                );
            }

            marks.Add(BuildMark(window, AdwMarkKind.Outer, window.OuterNm, landing, OuterMarkHalfLengthFt / GeoMath.FeetPerNm));

            // The inner mark spans exactly the pavement, measured off the ASDE-X reference (a 31 px tick
            // across a 32 px runway bar). Do not widen it to overhang the edges — the reference doesn't.
            marks.Add(BuildMark(window, AdwMarkKind.Inner, window.InnerNm, landing, (runway.WidthFt / 2.0) / GeoMath.FeetPerNm));
        }

        return marks;
    }

    private static AdwMark BuildMark(
        AdwWindow window,
        AdwMarkKind kind,
        double rangeNm,
        (LatLon Threshold, double LandingCourseDeg) landing,
        double halfLengthNm
    )
    {
        // Ranges are signed against the outbound (final approach course) direction: positive is out on
        // final, negative walks back over the threshold and down the runway.
        double outboundCourse = (landing.LandingCourseDeg + 180.0) % 360.0;
        var (centerLat, centerLon) = GeoMath.ProjectPointRaw(landing.Threshold.Lat, landing.Threshold.Lon, outboundCourse, rangeNm);

        var (aLat, aLon) = GeoMath.ProjectPointRaw(centerLat, centerLon, outboundCourse + 90.0, halfLengthNm);
        var (bLat, bLon) = GeoMath.ProjectPointRaw(centerLat, centerLon, outboundCourse - 90.0, halfLengthNm);

        return new AdwMark(window.ArrivalRunway, window.DepartureRunway, kind, new LatLon(aLat, aLon), new LatLon(bLat, bLon));
    }
}
