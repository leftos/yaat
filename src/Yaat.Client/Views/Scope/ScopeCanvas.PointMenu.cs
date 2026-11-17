using Avalonia;
using Yaat.Client.Views.Map;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope canvas's side of the map point menu: which measurement the click on the map landed on, for the menu's remove row.
/// </summary>
public sealed partial class ScopeCanvas
{
    /// <summary>How close, in pixels, a click must land to a measurement line to count as on it.</summary>
    private const float MeasurePickRadiusPx = 8f;

    /// <summary>
    /// Slot number of the measurement drawn nearest <paramref name="pos"/>, or null when none is within picking distance.
    /// </summary>
    public int? MeasurementSlotAt(Point pos)
    {
        (IReadOnlyList<ResolvedRbl> lines, _) = ResolveMeasurements();
        return RangeBearingHitTest.NearestSlot(lines, Viewport, (float)pos.X, (float)pos.Y, MeasurePickRadiusPx);
    }
}
