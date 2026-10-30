namespace Yaat.Client.Views.Map;

/// <summary>The order a <see cref="RightClickPicker"/> lists its targets in.</summary>
public static class RightClickTargetOrder
{
    /// <summary>
    /// <paramref name="targets"/> with <paramref name="hovered"/> — the target the hover label names — as option 1: an
    /// entry naming the same thing (<see cref="RightClickTarget.IsSameTargetAs"/>) moves to the front, else
    /// <paramref name="hovered"/> is inserted there. Every other target keeps its relative order; with no hovered target
    /// the list is returned unchanged.
    /// </summary>
    public static IReadOnlyList<RightClickTarget> HoveredFirst(IReadOnlyList<RightClickTarget> targets, RightClickTarget? hovered)
    {
        if (hovered is null)
        {
            return targets;
        }

        RightClickTarget first = targets.FirstOrDefault(hovered.IsSameTargetAs) ?? hovered;
        return [first, .. targets.Where(t => !t.IsSameTargetAs(hovered))];
    }
}
