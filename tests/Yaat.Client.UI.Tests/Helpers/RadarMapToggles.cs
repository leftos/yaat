using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.Helpers;

/// <summary>Radar video-map toggles as a facility's map list builds them: map id <c>m{starsId}</c>, short name <c>MAP{starsId}</c>.</summary>
internal static class RadarMapToggles
{
    /// <summary>The toggle for the map with <paramref name="starsId"/>, off.</summary>
    public static VideoMapToggleItem Toggle(int starsId) =>
        new()
        {
            MapId = $"m{starsId}",
            ShortName = $"MAP{starsId}",
            Name = $"Map {starsId}",
            BrightnessCategory = "A",
            StarsId = starsId,
        };

    /// <summary>Adds one toggle per STARS id to <paramref name="radar"/>, in the order given.</summary>
    public static void Add(RadarViewModel radar, params int[] starsIds)
    {
        foreach (int starsId in starsIds)
        {
            radar.MapToggles.Add(Toggle(starsId));
        }
    }
}
