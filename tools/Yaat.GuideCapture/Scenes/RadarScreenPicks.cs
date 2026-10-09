using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Radar;
using Yaat.GuideCapture.Capture;
using Yaat.Sim;

namespace Yaat.GuideCapture.Scenes;

// Picks the aircraft a radar showcase scene works on from where the radar draws
// them, so a menu opened on one has room to open and one aircraft alone sits
// under the pointer. Every pick is a pure function of the traffic picture,
// which the paused room makes the same every run.
internal static class RadarScreenPicks
{
    public static RadarCanvas Canvas(Window window) =>
        window.GetVisualDescendants().OfType<RadarCanvas>().FirstOrDefault(c => c.IsEffectivelyVisible)
        ?? throw new InvalidOperationException("The main window shows no radar canvas.");

    // The airborne aircraft drawn inside region (fractions of the canvas's
    // width and height) that is farthest on screen from every other aircraft.
    public static AircraftModel MostIsolated(MainViewModel vm, Window window, RadarCanvas canvas, Rect region)
    {
        SceneActions.RenderOnce(window);
        List<(AircraftModel Aircraft, Point Screen)> drawn = Drawn(vm, canvas);
        return drawn
                .Where(d => InRegion(canvas, d.Screen, region))
                .OrderByDescending(d => drawn.Where(o => o.Aircraft != d.Aircraft).Min(o => Distance(d.Screen, o.Screen)))
                .ThenBy(d => d.Aircraft.Callsign, StringComparer.Ordinal)
                .Select(d => d.Aircraft)
                .FirstOrDefault()
            ?? throw new InvalidOperationException($"No airborne aircraft is drawn inside {region} of the radar canvas.");
    }

    // The pair of airborne aircraft between minNm and maxNm apart, both drawn
    // inside region, with the most other aircraft drawn within crowdPx of the
    // line's midpoint: the place a range/bearing label meets data blocks.
    public static (AircraftModel From, AircraftModel To) CrowdedPair(
        MainViewModel vm,
        Window window,
        RadarCanvas canvas,
        Rect region,
        (double MinNm, double MaxNm) span,
        double crowdPx
    )
    {
        SceneActions.RenderOnce(window);
        List<(AircraftModel Aircraft, Point Screen)> drawn = [.. Drawn(vm, canvas).Where(d => InRegion(canvas, d.Screen, region))];
        (AircraftModel From, AircraftModel To)? best = null;
        int bestCrowd = -1;
        for (int i = 0; i < drawn.Count; i++)
        {
            for (int j = i + 1; j < drawn.Count; j++)
            {
                double nm = GeoMath.DistanceNm(drawn[i].Aircraft.Position, drawn[j].Aircraft.Position);
                if ((nm < span.MinNm) || (nm > span.MaxNm))
                {
                    continue;
                }

                Point mid = new((drawn[i].Screen.X + drawn[j].Screen.X) / 2, (drawn[i].Screen.Y + drawn[j].Screen.Y) / 2);
                int crowd = drawn.Count(d =>
                    (d.Aircraft != drawn[i].Aircraft) && (d.Aircraft != drawn[j].Aircraft) && (Distance(d.Screen, mid) <= crowdPx)
                );
                if (crowd > bestCrowd)
                {
                    best = (drawn[i].Aircraft, drawn[j].Aircraft);
                    bestCrowd = crowd;
                }
            }
        }

        return best ?? throw new InvalidOperationException($"No two aircraft drawn inside {region} are {span.MinNm}-{span.MaxNm} nm apart.");
    }

    private static List<(AircraftModel Aircraft, Point Screen)> Drawn(MainViewModel vm, RadarCanvas canvas) =>
        [
            .. vm
                .Aircraft.Where(a => (!a.IsOnGround) && (!a.IsDelayed))
                .OrderBy(a => a.Callsign, StringComparer.Ordinal)
                .Select(a =>
                {
                    (float x, float y) = canvas.Viewport.LatLonToScreen(a.Position.Lat, a.Position.Lon);
                    return (a, new Point(x, y));
                }),
        ];

    private static bool InRegion(RadarCanvas canvas, Point screen, Rect region) =>
        region.Contains(new Point(screen.X / canvas.Bounds.Width, screen.Y / canvas.Bounds.Height));

    private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
