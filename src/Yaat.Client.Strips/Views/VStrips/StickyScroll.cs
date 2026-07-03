namespace Yaat.Client.Views.VStrips;

/// <summary>
/// Pure decision logic for the vStrips racks' sticky-bottom scroll: strips dock bottom-up
/// (newest at the visual bottom), so a fresh strip extends the content downward. When the
/// controller was already scrolled to the bottom we re-pin to the new bottom so the newest
/// strip stays visible; when they had scrolled up to review older strips we leave the offset
/// alone. Kept UI-free so it can be unit-tested without a ScrollViewer.
/// </summary>
internal static class StickyScroll
{
    /// <summary>
    /// Given a scroll change, returns the vertical offset to pin to when the content grew while
    /// the user sat at the bottom, or <c>null</c> to leave the offset untouched. The delta
    /// arguments are "current minus previous", as reported by <c>ScrollChangedEventArgs</c>.
    /// </summary>
    public static double? PinnedBottomOffset(
        double offsetY,
        double extentHeight,
        double viewportHeight,
        double offsetDeltaY,
        double extentDeltaY,
        double viewportDeltaY,
        double epsilon
    )
    {
        // Only react when the content actually grew (a new strip arrived).
        if (extentDeltaY <= epsilon)
        {
            return null;
        }

        var prevOffsetY = offsetY - offsetDeltaY;
        var prevMax = Math.Max(0, (extentHeight - extentDeltaY) - (viewportHeight - viewportDeltaY));
        var wasAtBottom = prevOffsetY >= (prevMax - epsilon);
        if (!wasAtBottom)
        {
            return null;
        }

        var newMax = Math.Max(0, extentHeight - viewportHeight);
        return offsetY < (newMax - epsilon) ? newMax : null;
    }
}
