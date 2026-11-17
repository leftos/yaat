using SkiaSharp;
using Yaat.Client.Models;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The plural form of the frame's own hit tests: every block a point lands on and every symbol within the click radius, in
/// the order a right click over stacked tracks offers them.
/// </summary>
public static class ScopeFrameHits
{
    /// <summary>Every aircraft whose block contains (<paramref name="x"/>, <paramref name="y"/>), topmost first.</summary>
    public static IReadOnlyList<AircraftModel> BlocksAt(this ScopeFrame frame, float x, float y)
    {
        var hits = new List<AircraftModel>();
        for (int i = frame.Tracks.Count - 1; i >= 0; i--)
        {
            if (frame.Tracks[i].Block.Bounds.Contains(x, y))
            {
                hits.Add(frame.Tracks[i].Aircraft);
            }
        }

        return hits;
    }

    /// <summary>
    /// Every aircraft whose symbol is within <see cref="ScopeFrame.TargetHitRadiusPx"/> of (<paramref name="x"/>,
    /// <paramref name="y"/>), nearest first.
    /// </summary>
    public static IReadOnlyList<AircraftModel> TargetsNear(this ScopeFrame frame, float x, float y)
    {
        var point = new SKPoint(x, y);
        var hits = new List<(AircraftModel Aircraft, float Distance)>();
        foreach (ScopeTrack track in frame.Tracks)
        {
            float distance = SKPoint.Distance(track.Symbol, point);
            if (distance < ScopeFrame.TargetHitRadiusPx)
            {
                hits.Add((track.Aircraft, distance));
            }
        }

        return [.. hits.OrderBy(hit => hit.Distance).Select(hit => hit.Aircraft)];
    }
}
