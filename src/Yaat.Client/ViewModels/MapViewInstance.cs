using System.Globalization;

namespace Yaat.Client.ViewModels;

/// <summary>
/// One extra Ground View window (instance #2, #3, …) and the <see cref="GroundViewModel"/> it owns. Each
/// instance keeps its own center, zoom, rotation, filters and datablock offsets. An instance whose airport
/// is the primary view's mirrors its layout (<see cref="GroundViewModel.MirrorLayoutFrom"/>) rather than
/// reloading it; any other airport loads its own. The docked Ground View is the implicit #1 and has no
/// instance record.
/// </summary>
public sealed class GroundViewInstance
{
    /// <summary>Instance number, always ≥ 2 — the docked primary view is #1.</summary>
    public required int Ordinal { get; init; }

    /// <summary>
    /// The base airport this window was opened on (the user picks it before the window appears). Equal to
    /// the primary view's airport means mirror it; anything else means load that airport's own layout.
    /// </summary>
    public required string AirportId { get; init; }

    /// <summary>Window-geometry and preference key for this instance, e.g. <c>GroundView#2</c>.</summary>
    public string GeometryKey => ViewInstanceOrdinals.GroundKey(Ordinal);

    /// <summary>Window title shown to the user.</summary>
    public string Title => $"Ground View #{Ordinal} — {AirportId}";

    /// <summary>The view-model driving this window. Not shared with any other view.</summary>
    public required GroundViewModel Vm { get; init; }
}

/// <summary>
/// The ordinal and key conventions shared by the extra Ground View and YAAT Scope instances. A key is the
/// prefix plus the ordinal verbatim ("GroundView#2"), which <see cref="Services.UserPreferences.GetWindowGeometry"/>
/// routes through its dictionary store like any other non-fixed window name.
/// </summary>
public static class ViewInstanceOrdinals
{
    /// <summary>Key prefix for an extra Ground View instance.</summary>
    public const string GroundPrefix = "GroundView#";

    /// <summary>Key prefix for an extra YAAT Scope window.</summary>
    public const string ScopePrefix = MainViewModel.ScopeViewKey + "#";

    /// <summary>
    /// The lowest instance number ≥ 2 that is not already in <paramref name="taken"/>. #1 is the docked
    /// primary view, so a first extra window is #2 and a gap left by a closed window is reused.
    /// </summary>
    public static int NextFree(IEnumerable<int> taken)
    {
        var used = new HashSet<int>(taken);
        int ordinal = 2;
        while (used.Contains(ordinal))
        {
            ordinal++;
        }

        return ordinal;
    }

    /// <summary>The geometry/preference key for extra Ground View instance <paramref name="ordinal"/>.</summary>
    public static string GroundKey(int ordinal) => GroundPrefix + ordinal.ToString(CultureInfo.InvariantCulture);

    /// <summary>The geometry/preference key for extra YAAT Scope window <paramref name="ordinal"/>.</summary>
    public static string ScopeKey(int ordinal) => ScopePrefix + ordinal.ToString(CultureInfo.InvariantCulture);
}
