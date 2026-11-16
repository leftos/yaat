using SkiaSharp;
using Yaat.Client.Models;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>Which tracks the Scope draws a vector line on: the viewer's own (with no viewer, every track) or all of them.</summary>
public enum ScopeVectorOwnership
{
    /// <summary>The viewer's own tracks; with no viewer, every track.</summary>
    Own,

    /// <summary>Every track.</summary>
    All,
}

/// <summary>The text names of <see cref="ScopeVectorOwnership"/>, as <c>.scope vector</c> and the pref-set files take them.</summary>
public static class ScopeVectorOwnerships
{
    /// <summary>The text name of <see cref="ScopeVectorOwnership.Own"/>.</summary>
    public const string OwnName = "own";

    /// <summary>The text name of <see cref="ScopeVectorOwnership.All"/>.</summary>
    public const string AllName = "all";

    /// <summary>The lowercase text name of <paramref name="ownership"/>.</summary>
    public static string NameOf(ScopeVectorOwnership ownership) => (ownership == ScopeVectorOwnership.All) ? AllName : OwnName;

    /// <summary>Parses <c>own</c> or <c>all</c>, case-insensitive.</summary>
    /// <returns>False when <paramref name="word"/> is neither.</returns>
    public static bool TryParse(string word, out ScopeVectorOwnership ownership)
    {
        if (string.Equals(word, AllName, StringComparison.OrdinalIgnoreCase))
        {
            ownership = ScopeVectorOwnership.All;
            return true;
        }

        ownership = ScopeVectorOwnership.Own;
        return string.Equals(word, OwnName, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Pure: the screen geometry of a track's vector line and history dots, as the Radar's PTL and history trails draw them,
/// with the vector projected along the ground track rather than the heading.
/// </summary>
internal static class ScopeTrackLayers
{
    /// <summary>The slowest ground speed, in knots, that draws a vector line.</summary>
    internal const double MinVectorGroundSpeedKts = 1.0;

    /// <summary>
    /// The screen point a vector of <paramref name="minutes"/> ends at: ground speed times minutes along
    /// <see cref="AircraftModel.TrueTrack"/>, wind included; null at 0 minutes or under 1 kt ground speed.
    /// </summary>
    internal static SKPoint? VectorEnd(AircraftModel ac, double minutes, MapViewport vp)
    {
        if ((minutes <= 0) || (ac.GroundSpeed < MinVectorGroundSpeedKts))
        {
            return null;
        }

        double distNm = ac.GroundSpeed * minutes / 60.0;
        LatLon end = GeoMath.ProjectPoint(ac.Position, new TrueHeading(ac.TrueTrack), distNm);
        (float x, float y) = vp.LatLonToScreen(end.Lat, end.Lon);
        return new SKPoint(x, y);
    }

    /// <summary>Whether a track draws its vector: every track under ALL, an own track under OWN.</summary>
    internal static bool ShowsVector(ScopeVectorOwnership ownership, bool isOwn) => (ownership == ScopeVectorOwnership.All) || isOwn;

    /// <summary>
    /// The screen points of the newest <paramref name="count"/> history samples, oldest first; every sample there is when
    /// there are fewer, and none at a count of 0 or with no samples. A sample with fewer than two values is skipped.
    /// </summary>
    internal static IReadOnlyList<SKPoint> HistoryDots(AircraftModel ac, int count, MapViewport vp)
    {
        if ((count <= 0) || (ac.PositionHistory is not { Count: > 0 } samples))
        {
            return [];
        }

        int start = Math.Max(0, samples.Count - count);
        int drawable = 0;
        for (int i = start; i < samples.Count; i++)
        {
            drawable += (samples[i].Length >= 2) ? 1 : 0;
        }

        var dots = new SKPoint[drawable];
        int next = 0;
        for (int i = start; i < samples.Count; i++)
        {
            double[] sample = samples[i];
            if (sample.Length >= 2)
            {
                (float x, float y) = vp.LatLonToScreen(sample[0], sample[1]);
                dots[next++] = new SKPoint(x, y);
            }
        }

        return dots;
    }
}
