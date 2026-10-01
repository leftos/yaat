namespace Yaat.Client.ContextMenus;

/// <summary>
/// What the surface's range/bearing measure tool is doing, which decides whether the Display menu offers a measure
/// item and how that item reads.
/// </summary>
public enum MenuMeasureState
{
    /// <summary>The surface has no measure tool at all: no item is shown. An idle tool is <see cref="NoAnchor"/>.</summary>
    None,

    /// <summary>A measurement is waiting for its first endpoint: "Measure from {callsign}".</summary>
    NoAnchor,

    /// <summary>A measurement has its first endpoint and is waiting for its second: "Measure to {callsign}".</summary>
    HasAnchor,
}
