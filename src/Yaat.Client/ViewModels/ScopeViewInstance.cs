using System.Globalization;

namespace Yaat.Client.ViewModels;

/// <summary>
/// One extra YAAT Scope window (#2, #3, …) and the <see cref="ScopeViewModel"/> it owns: another view of the same facility,
/// with its own range, centre, template, maps, markers and tools, saved in its own slot of the facility's current state. The
/// primary Scope (the main window's tab or its pop-out) is the implicit #1 and has no instance record.
/// </summary>
public sealed class ScopeViewInstance
{
    /// <summary>The start of every Scope window's title; an extra window adds its number.</summary>
    public const string TitlePrefix = "YAAT Radar";

    /// <summary>Instance number, always ≥ 2: the primary Scope is #1.</summary>
    public required int Ordinal { get; init; }

    /// <summary>Window-geometry and preference key for this instance, e.g. <c>ScopeView#2</c>.</summary>
    public string GeometryKey => ViewInstanceOrdinals.ScopeKey(Ordinal);

    /// <summary>Window title shown to the user, e.g. <c>YAAT Scope #2</c>.</summary>
    public string Title => $"{TitlePrefix} #{Ordinal.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The view model driving this window. Not shared with any other Scope.</summary>
    public required ScopeViewModel Vm { get; init; }
}
