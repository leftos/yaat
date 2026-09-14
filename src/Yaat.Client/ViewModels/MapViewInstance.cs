using System.Globalization;

namespace Yaat.Client.ViewModels;

/// <summary>
/// One extra Radar View window (instance #2, #3, …) and the <see cref="RadarViewModel"/> it owns. Each
/// instance keeps its own center, range, filters and datablock offsets; the selected aircraft stays app-wide.
/// The docked Radar View is the implicit #1 and has no instance record.
/// </summary>
public sealed class RadarViewInstance
{
    /// <summary>Instance number, always ≥ 2 — the docked primary view is #1.</summary>
    public required int Ordinal { get; init; }

    /// <summary>Window-geometry and preference key for this instance, e.g. <c>RadarView#2</c>.</summary>
    public string GeometryKey => ViewInstanceOrdinals.RadarKey(Ordinal);

    /// <summary>Window title shown to the user.</summary>
    public string Title => $"Radar View #{Ordinal}";

    /// <summary>The view-model driving this window. Not shared with any other view.</summary>
    public required RadarViewModel Vm { get; init; }
}

/// <summary>
/// One extra Ground View window (instance #2, #3, …) and the <see cref="GroundViewModel"/> it owns. Each
/// instance keeps its own center, zoom, rotation, filters and datablock offsets; the airport layout itself
/// is mirrored from the primary view (<see cref="GroundViewModel.MirrorLayoutFrom"/>) rather than reloaded.
/// The docked Ground View is the implicit #1 and has no instance record.
/// </summary>
public sealed class GroundViewInstance
{
    /// <summary>Instance number, always ≥ 2 — the docked primary view is #1.</summary>
    public required int Ordinal { get; init; }

    /// <summary>Window-geometry and preference key for this instance, e.g. <c>GroundView#2</c>.</summary>
    public string GeometryKey => ViewInstanceOrdinals.GroundKey(Ordinal);

    /// <summary>Window title shown to the user.</summary>
    public string Title => $"Ground View #{Ordinal}";

    /// <summary>The view-model driving this window. Not shared with any other view.</summary>
    public required GroundViewModel Vm { get; init; }
}

/// <summary>
/// The ordinal and key conventions shared by the extra Radar/Ground view instances. A key is the prefix
/// plus the ordinal verbatim ("RadarView#2"), which <see cref="Services.UserPreferences.GetWindowGeometry"/>
/// routes through its dictionary store like any other non-fixed window name.
/// </summary>
public static class ViewInstanceOrdinals
{
    /// <summary>Key prefix for an extra Radar View instance.</summary>
    public const string RadarPrefix = "RadarView#";

    /// <summary>Key prefix for an extra Ground View instance.</summary>
    public const string GroundPrefix = "GroundView#";

    /// <summary>
    /// The lowest instance number ≥ 2 that is not already in <paramref name="taken"/>. #1 is the docked
    /// primary view, so a first extra window is #2 and a gap left by a closed window is reused.
    /// </summary>
    public static int NextFree(IEnumerable<int> taken)
    {
        var used = new HashSet<int>(taken);
        var ordinal = 2;
        while (used.Contains(ordinal))
        {
            ordinal++;
        }

        return ordinal;
    }

    /// <summary>The geometry/preference key for extra Radar View instance <paramref name="ordinal"/>.</summary>
    public static string RadarKey(int ordinal) => RadarPrefix + ordinal.ToString(CultureInfo.InvariantCulture);

    /// <summary>The geometry/preference key for extra Ground View instance <paramref name="ordinal"/>.</summary>
    public static string GroundKey(int ordinal) => GroundPrefix + ordinal.ToString(CultureInfo.InvariantCulture);
}
